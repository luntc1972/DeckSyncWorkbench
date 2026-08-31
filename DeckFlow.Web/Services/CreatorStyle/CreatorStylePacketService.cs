using DeckFlow.Core.Content;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Knowledge.CreatorStyleRubric;
using DeckFlow.Core.Models;
using DeckFlow.Web.Models;
using System.Globalization;
using System.Text;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.FeatureFlags;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeckFlow.Web.Services.CreatorStyle;

/// <summary>
/// Builds creator-style artifact packets from the creator profile, submitted deck, and validated deck context.
/// </summary>
public interface ICreatorStylePacketService
{
    /// <summary>
    /// Computes the creator-style packet cache key for the supplied request, or <see langword="null"/>
    /// when the request should bypass <see cref="PacketSessionCache"/>.
    /// </summary>
    /// <param name="request">Current creator-style request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The canonical packet cache key, or <see langword="null"/> when cache bypass is active.</returns>
    Task<string?> TryComputeCacheKeyAsync(CreatorStyleRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Builds a deterministic creator-style artifact packet for the supplied request.
    /// </summary>
    /// <param name="request">Current creator-style request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assembled packet result.</returns>
    Task<CreatorStylePacketResult> BuildAsync(CreatorStyleRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Accepted-only exemplar projection returned by <see cref="CreatorStylePacketService"/>.
/// </summary>
public sealed record CreatorStyleExemplarDeck
{
    /// <summary>
    /// Gets the creator deck identifier.
    /// </summary>
    public required string DeckId { get; init; }

    /// <summary>
    /// Gets the optional creator folder name.
    /// </summary>
    public string? FolderName { get; init; }

    /// <summary>
    /// Gets the upstream confidence marker for this exemplar.
    /// </summary>
    public required string ConfidenceMarker { get; init; }

    /// <summary>
    /// Gets the accepted canonical card names retained for this exemplar.
    /// </summary>
    public required IReadOnlyList<string> CardNames { get; init; }
}

/// <summary>
/// Returns the results of a creator-style packet build.
/// </summary>
// Why: keep JSON-round-trippable; do not convert to get-only.
public sealed record CreatorStylePacketResult
{
    /// <summary>
    /// Gets the deterministic artifact text assembled for downstream critique.
    /// </summary>
    public required string ArtifactText { get; init; }

    /// <summary>
    /// Gets the rubric scores for the submitted deck against the creator profile.
    /// </summary>
    public required RubricScoreResult RubricScores { get; init; }

    /// <summary>
    /// Gets the accepted-only exemplar deck projections.
    /// </summary>
    public required IReadOnlyList<CreatorStyleExemplarDeck> Exemplars { get; init; }

    /// <summary>
    /// Gets the validated creator-whitelist names returned by the pool builder.
    /// </summary>
    public required IReadOnlyList<string> ValidatedWhitelist { get; init; }

    /// <summary>
    /// Gets the validated combo-card names retained after the extra grounding pass.
    /// </summary>
    public required IReadOnlyList<string> ValidatedComboCards { get; init; }

    /// <summary>
    /// Gets a value indicating whether any candidate cards were withheld or upstream grounding degraded.
    /// </summary>
    public required bool GroundingDegraded { get; init; }

    /// <summary>
    /// Gets an optional notice describing degraded or incomplete grounding context.
    /// </summary>
    public string? Notice { get; init; }

    /// <summary>
    /// Gets a value indicating whether packet generation is unavailable because the creator profile
    /// was missing or insufficient, distinct from grounding degradation.
    /// </summary>
    public bool ProfileUnavailable { get; init; }
}

/// <summary>
/// Orchestrates creator-style packet assembly with a single fail-closed post-whitelist grounding pass.
/// </summary>
public sealed class CreatorStylePacketService : ICreatorStylePacketService
{
    private const int MaxUserTextLength = 200;
    private const string CritiqueInstruction = "Critique this deck ONLY using the cards provided above. Do not invent, suggest, or reference any card that is not listed here.";
    private const string SupersededVerdict = "superseded";
    internal const string CreatorStyleToolEnabledFlag = "tool.creator-style.enabled";

    // Why (WR-15 tracking note, see WR-08 in 112-REVIEW.md): CreatorStyleToolEnabledFlag is the
    // sole gate that makes this service reachable at all - every packet built here necessarily
    // has the flag ON, so registering it as "prompt mutating" makes ShouldBypassPacketCache()
    // return true unconditionally in every state this code can actually run in. That makes
    // PacketSessionCache's read/write wiring for this tool a total no-op today, not the
    // conditional no-op the comment below used to claim. Left unresolved pending a maintainer
    // decision between two mutually exclusive fixes (either drop this flag from the list below,
    // which would also require rewriting the three tests in CreatorStylePacketServiceTests.cs
    // that currently pin today's always-bypass behavior as intentional, or delete the cache
    // wiring and CreatorStyleCacheInputs entirely) - see 112-REVIEW-FIX.md for the fuller
    // rationale.
    internal static readonly IReadOnlyList<string> PromptMutatingCreatorStyleFlags = new[]
    {
        CreatorStyleToolEnabledFlag,
    };

    private readonly Func<string, CancellationToken, Task<CreatorStyleProfile?>> _getProfileAsync;
    private readonly Func<string, CancellationToken, Task<SubmittedDeckAnalysis>> _buildSubmittedDeckAsync;
    private readonly Func<string, CardGroundingDeckContext, CancellationToken, Task<CreatorWhitelistPoolBuildResult>> _buildWhitelistAsync;
    private readonly Func<IReadOnlyList<string>, CardGroundingDeckContext, CancellationToken, Task<CardGroundingBatchResult>> _validateAdditionalCardsAsync;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<CreatorDeckCacheEntry>>> _getCreatorDecksAsync;
    private readonly Func<string, IReadOnlyList<FusedTarget>, SubmittedDeckStats, RubricScoreResult> _scoreRubric;
    private readonly PacketSessionCache? _packetCache;
    private readonly IFeatureFlagCache? _flagCache;
    private readonly ILogger<CreatorStylePacketService> _logger;

    /// <summary>
    /// Creates the production creator-style packet service.
    /// </summary>
    public CreatorStylePacketService(
        ICreatorStyleProfileStore creatorStyleProfileStore,
        ISubmittedDeckStatsBuilder submittedDeckStatsBuilder,
        CreatorWhitelistPoolBuilder creatorWhitelistPoolBuilder,
        ICardGroundingGuard cardGroundingGuard,
        ICreatorDeckCacheStore creatorDeckCacheStore,
        PacketSessionCache packetCache,
        IFeatureFlagCache? flagCache = null,
        ILogger<CreatorStylePacketService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(creatorStyleProfileStore);
        ArgumentNullException.ThrowIfNull(submittedDeckStatsBuilder);
        ArgumentNullException.ThrowIfNull(creatorWhitelistPoolBuilder);
        ArgumentNullException.ThrowIfNull(cardGroundingGuard);
        ArgumentNullException.ThrowIfNull(creatorDeckCacheStore);
        ArgumentNullException.ThrowIfNull(packetCache);

        _getProfileAsync = (creatorSlug, cancellationToken) => creatorStyleProfileStore.GetBySlugAsync(creatorSlug, cancellationToken);
        _buildSubmittedDeckAsync = (deckSource, cancellationToken) => submittedDeckStatsBuilder.BuildAsync(deckSource, cancellationToken);
        _buildWhitelistAsync = (creatorSlug, deckContext, cancellationToken) => creatorWhitelistPoolBuilder.BuildWithDiagnosticsAsync(creatorSlug, deckContext, cancellationToken);
        _validateAdditionalCardsAsync = (candidateNames, deckContext, cancellationToken) => cardGroundingGuard.ValidateAllAsync(candidateNames, deckContext, cancellationToken);
        _getCreatorDecksAsync = (creatorSlug, cancellationToken) => creatorDeckCacheStore.GetByCreatorAsync(creatorSlug, cancellationToken);
        _scoreRubric = (creatorSlug, targets, stats) => CreatorStyleRubricScorer.Score(creatorSlug, targets, stats);
        _packetCache = packetCache;
        _flagCache = flagCache;
        _logger = logger ?? NullLogger<CreatorStylePacketService>.Instance;
    }

    internal CreatorStylePacketService(
        Func<string, CancellationToken, Task<CreatorStyleProfile?>> getProfileAsync,
        Func<string, CancellationToken, Task<SubmittedDeckAnalysis>> buildSubmittedDeckAsync,
        Func<string, CardGroundingDeckContext, CancellationToken, Task<CreatorWhitelistPoolBuildResult>> buildWhitelistAsync,
        Func<IReadOnlyList<string>, CardGroundingDeckContext, CancellationToken, Task<CardGroundingBatchResult>> validateAdditionalCardsAsync,
        Func<string, CancellationToken, Task<IReadOnlyList<CreatorDeckCacheEntry>>> getCreatorDecksAsync,
        Func<string, IReadOnlyList<FusedTarget>, SubmittedDeckStats, RubricScoreResult> scoreRubric,
        PacketSessionCache? packetCache = null,
        IFeatureFlagCache? flagCache = null,
        ILogger<CreatorStylePacketService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(getProfileAsync);
        ArgumentNullException.ThrowIfNull(buildSubmittedDeckAsync);
        ArgumentNullException.ThrowIfNull(buildWhitelistAsync);
        ArgumentNullException.ThrowIfNull(validateAdditionalCardsAsync);
        ArgumentNullException.ThrowIfNull(getCreatorDecksAsync);
        ArgumentNullException.ThrowIfNull(scoreRubric);
        _getProfileAsync = getProfileAsync;
        _buildSubmittedDeckAsync = buildSubmittedDeckAsync;
        _buildWhitelistAsync = buildWhitelistAsync;
        _validateAdditionalCardsAsync = validateAdditionalCardsAsync;
        _getCreatorDecksAsync = getCreatorDecksAsync;
        _scoreRubric = scoreRubric;
        _packetCache = packetCache;
        _flagCache = flagCache;
        _logger = logger ?? NullLogger<CreatorStylePacketService>.Instance;
    }

    /// <inheritdoc />
    public Task<string?> TryComputeCacheKeyAsync(CreatorStyleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ShouldBypassPacketCache())
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(PacketSessionCache.ComputeKey(BuildCacheInputs(request)));
    }

    /// <inheritdoc />
    public async Task<CreatorStylePacketResult> BuildAsync(CreatorStyleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool bypassCacheWrite = ShouldBypassPacketCache();

        CreatorStyleProfile? profile = await _getProfileAsync(request.CreatorSlug, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return FinalizeResult(CreateUnavailableResult("No creator style profile is available for the supplied creator slug."));
        }

        if (profile.InsufficientSample)
        {
            return FinalizeResult(CreateUnavailableResult("The creator style profile sample is insufficient for artifact generation."));
        }

        Task<IReadOnlyList<CreatorDeckCacheEntry>> creatorDecksTask = _getCreatorDecksAsync(request.CreatorSlug, cancellationToken);
        SubmittedDeckAnalysis analysis = await _buildSubmittedDeckAsync(request.DeckSource, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FusedTarget> scoreableTargets = profile.FusedTargets
            .Where(static target => !IsSuperseded(target))
            .Where(static target => string.IsNullOrWhiteSpace(target.Condition))
            .ToArray();
        RubricScoreResult rubricScores = _scoreRubric(request.CreatorSlug, scoreableTargets, analysis.Stats);

        IReadOnlyList<CreatorDeckCacheEntry> creatorDecks = await creatorDecksTask.ConfigureAwait(false);
        IReadOnlyList<CreatorDeckCacheEntry> selectedExemplars = CreatorDeckExemplarSelector.SelectExemplars(creatorDecks, analysis.Stats.DeckSize);

        CreatorWhitelistPoolBuildResult whitelist = await _buildWhitelistAsync(
            request.CreatorSlug,
            analysis.DeckContext,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> comboCandidates = analysis.IncludedComboCardNames;

        HashSet<string> whitelistSet = new(whitelist.AcceptedNames, StringComparer.Ordinal);
        IReadOnlyList<string> additionalCandidates = selectedExemplars
            .SelectMany(deck => deck.Entries.Select(entry => entry.Name.Trim()))
            .Concat(comboCandidates)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Where(name => !whitelistSet.Contains(name))
            .ToArray();

        CardGroundingBatchResult additionalValidation = additionalCandidates.Count == 0
            ? new CardGroundingBatchResult
            {
                Verdicts = [],
                HasUpstreamFailure = false,
            }
            : await _validateAdditionalCardsAsync(additionalCandidates, analysis.DeckContext, cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> acceptedByOriginal = BuildAcceptedByOriginal(additionalCandidates, additionalValidation.Verdicts);
        IReadOnlyList<CreatorStyleExemplarDeck> exemplars = selectedExemplars
            .Select(deck => new CreatorStyleExemplarDeck
            {
                DeckId = deck.DeckId,
                FolderName = deck.FolderName,
                ConfidenceMarker = deck.ConfidenceMarker,
                CardNames = deck.Entries
                    .Select(entry => ResolveAcceptedCardName(entry.Name.Trim(), whitelistSet, acceptedByOriginal))
                    .Where(static cardName => cardName is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
            })
            .ToArray();

        IReadOnlyList<string> validatedComboCards = comboCandidates
            .Select(cardName => ResolveAcceptedCardName(cardName, whitelistSet, acceptedByOriginal))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        int excludedCount = additionalCandidates.Count - acceptedByOriginal.Count;
        bool deckResolutionDegraded = analysis.DeckResolutionDegraded;
        bool groundingDegraded = whitelist.HasUpstreamFailure
            || additionalValidation.HasUpstreamFailure
            || excludedCount > 0
            || deckResolutionDegraded;
        string? notice = groundingDegraded
            ? deckResolutionDegraded
                ? "The submitted deck could not be fully resolved for grounding-sensitive analysis."
                : excludedCount > 0
                    ? "Some cards couldn't be validated and were left out of this packet. The critique below still reflects your deck's core build — just with a smaller card pool than usual."
                    : "Card validation is temporarily unavailable, so this packet uses a reduced card pool. Try again in a few minutes for the full picture."
            : null;

        if (groundingDegraded)
        {
            _logger.LogWarning(
                "Creator-style grounding degraded for creator {CreatorSlug}; accepted {AcceptedCount} of {CandidateCount} post-whitelist candidates.",
                request.CreatorSlug,
                acceptedByOriginal.Count,
                additionalCandidates.Count);
        }

        return FinalizeResult(new CreatorStylePacketResult
        {
            ArtifactText = BuildArtifactText(
                request,
                profile,
                rubricScores,
                exemplars,
                whitelist.AcceptedNames,
                validatedComboCards,
                groundingDegraded,
                notice),
            RubricScores = rubricScores,
            Exemplars = exemplars,
            ValidatedWhitelist = whitelist.AcceptedNames,
            ValidatedComboCards = validatedComboCards,
            GroundingDegraded = groundingDegraded,
            Notice = notice,
        });

        CreatorStylePacketResult FinalizeResult(CreatorStylePacketResult result)
        {
            if (!bypassCacheWrite && _packetCache is not null)
            {
                // Why (WR-08 in 112-REVIEW.md): this branch never actually runs today - it is the
                // *cache*, not the bypass, that is the no-op. CreatorStyleToolEnabledFlag is the
                // sole flag in PromptMutatingCreatorStyleFlags and also the sole gate that makes
                // this service reachable, so bypassCacheWrite is always true whenever BuildAsync
                // executes. The wiring is left in place, mirroring DeckAnalysisPacketService's
                // shape, pending a maintainer decision on how to resolve WR-08 (see the constant
                // declaration above and 112-REVIEW-FIX.md for the two candidate fixes).
                string cacheKey = PacketSessionCache.ComputeKey(BuildCacheInputs(request));
                _packetCache.Set(cacheKey, result, PacketSizeEstimator.EstimateSizeBytes(result));
            }

            return result;
        }
    }

    private static CreatorStylePacketResult CreateUnavailableResult(string notice)
        => new()
        {
            ArtifactText = string.Empty,
            RubricScores = new RubricScoreResult
            {
                CreatorSlug = string.Empty,
                MetricScores = [],
            },
            Exemplars = [],
            ValidatedWhitelist = [],
            ValidatedComboCards = [],
            GroundingDegraded = false,
            Notice = notice,
            ProfileUnavailable = true,
        };

    private static Dictionary<string, string> BuildAcceptedByOriginal(
        IReadOnlyList<string> candidateNames,
        IReadOnlyList<CardGroundingVerdict> verdicts)
    {
        if (verdicts.Count != candidateNames.Count)
        {
            throw new InvalidOperationException(
                $"Card grounding returned {verdicts.Count} verdicts for {candidateNames.Count} candidates; ordered verdicts must match the submitted candidate batch exactly.");
        }

        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < candidateNames.Count; i++)
        {
            if (verdicts[i].Accepted)
            {
                accepted[candidateNames[i]] = verdicts[i].CanonicalName;
            }
        }

        return accepted;
    }

    private static string? ResolveAcceptedCardName(
        string candidateName,
        IReadOnlySet<string> whitelistSet,
        IReadOnlyDictionary<string, string> acceptedByOriginal)
    {
        if (whitelistSet.Contains(candidateName))
        {
            return candidateName;
        }

        return acceptedByOriginal.TryGetValue(candidateName, out string? canonicalName)
            ? canonicalName
            : null;
    }

    private static bool IsSuperseded(FusedTarget target)
        => string.Equals(target.Verdict, SupersededVerdict, StringComparison.OrdinalIgnoreCase);

    private bool IsCreatorStyleFlagOn(string flagKey)
        => _flagCache is not null
            && _flagCache.Snapshot().TryGetValue(flagKey, out bool on)
            && on;

    private bool ShouldBypassPacketCache()
        => PromptMutatingCreatorStyleFlags.Any(IsCreatorStyleFlagOn);

    private static CreatorStyleCacheInputs BuildCacheInputs(CreatorStyleRequest request)
        => new(
            CreatorSlug: request.CreatorSlug.Trim(),
            NormalizedDeckSource: NormalizeDeckSource(request.DeckSource),
            Format: request.Format.Trim());

    private static string NormalizeDeckSource(string deckSource)
        => (deckSource ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string BuildArtifactText(
        CreatorStyleRequest request,
        CreatorStyleProfile profile,
        RubricScoreResult rubricScores,
        IReadOnlyList<CreatorStyleExemplarDeck> exemplars,
        IReadOnlyList<string> validatedWhitelist,
        IReadOnlyList<string> validatedComboCards,
        bool groundingDegraded,
        string? notice)
    {
        var sb = new StringBuilder();
        string sanitizedCreatorSlug = SanitizeUserText(request.CreatorSlug, fallback: profile.Slug);

        if (groundingDegraded)
        {
            sb.Append("Grounding caveat: ");
            sb.AppendLine(string.IsNullOrWhiteSpace(notice) ? "Some referenced cards were withheld after validation." : notice);
            sb.AppendLine();
        }

        sb.AppendLine("Creator Targets");
        sb.Append("Requested Creator: ");
        sb.AppendLine(sanitizedCreatorSlug);
        foreach (FusedTarget target in profile.FusedTargets)
        {
            if (IsSuperseded(target))
            {
                continue;
            }

            // Why: conditional targets are rendered (labeled) but intentionally excluded from scoring — the scorer cannot evaluate conditions.
            sb.Append("- Metric: ");
            sb.Append(target.Metric);
            sb.Append("; Value: ");
            sb.Append(FormatNumber(target.Value));
            sb.Append("; Weight: ");
            sb.Append(FormatNumber(target.Weight));

            if (target.StatedMin.HasValue)
            {
                sb.Append("; StatedMin: ");
                sb.Append(FormatNumber(target.StatedMin.Value));
            }

            if (target.StatedMax.HasValue)
            {
                sb.Append("; StatedMax: ");
                sb.Append(FormatNumber(target.StatedMax.Value));
            }

            if (!string.IsNullOrWhiteSpace(target.Condition))
            {
                sb.Append("; Condition: ");
                sb.Append(target.Condition);
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Exemplar Decklists");
        if (exemplars.Count == 0)
        {
            sb.AppendLine("- None");
        }
        else
        {
            foreach (CreatorStyleExemplarDeck exemplar in exemplars)
            {
                sb.Append("- DeckId: ");
                sb.Append(SanitizeUserText(exemplar.DeckId, fallback: "(unknown)"));
                sb.Append("; FolderName: ");
                sb.Append(string.IsNullOrWhiteSpace(exemplar.FolderName)
                    ? "(none)"
                    : SanitizeUserText(exemplar.FolderName, fallback: "(none)"));
                sb.Append("; ConfidenceMarker: ");
                sb.Append(SanitizeUserText(exemplar.ConfidenceMarker, fallback: "unknown"));
                sb.Append("; Cards: ");
                sb.AppendLine(exemplar.CardNames.Count == 0 ? "(none)" : string.Join(", ", exemplar.CardNames));
            }
        }

        sb.AppendLine();
        sb.AppendLine("Validated Synergy Context");
        sb.Append("- Validated Combo Cards: ");
        sb.AppendLine(validatedComboCards.Count == 0 ? "(none)" : string.Join(", ", validatedComboCards));
        sb.Append("- Validated Whitelist: ");
        sb.AppendLine(validatedWhitelist.Count == 0 ? "(none)" : string.Join(", ", validatedWhitelist));

        sb.AppendLine();
        sb.AppendLine("Rubric Scores");
        if (rubricScores.MetricScores.Count == 0)
        {
            sb.AppendLine("- None");
        }
        else
        {
            foreach (RubricMetricScore metricScore in rubricScores.MetricScores)
            {
                sb.Append("- Metric: ");
                sb.Append(metricScore.Metric);
                sb.Append("; Target: ");
                sb.Append(FormatNumber(metricScore.TargetValue));
                sb.Append("; Submitted: ");
                sb.Append(metricScore.SubmittedValue.HasValue ? FormatNumber(metricScore.SubmittedValue.Value) : "n/a");
                sb.Append("; Delta: ");
                sb.Append(metricScore.Delta.HasValue ? FormatNumber(metricScore.Delta.Value) : "n/a");
                sb.Append("; Weight: ");
                sb.Append(FormatNumber(metricScore.Weight));
                sb.Append("; Verdict: ");
                sb.Append(metricScore.Verdict);
                sb.Append("; Confidence: ");
                sb.AppendLine(string.IsNullOrWhiteSpace(metricScore.Confidence) ? "n/a" : metricScore.Confidence);
            }
        }

        sb.AppendLine();
        sb.AppendLine("Instruction");
        sb.AppendLine(CritiqueInstruction);

        return sb.ToString();
    }

    private static string FormatNumber(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string SanitizeUserText(string? value, string fallback)
    {
        // Why: raw request slug plus upstream exemplar DeckId, FolderName, and ConfidenceMarker are free text; each artifact site is sanitized to retain its one-line contract.
        string singleLine = JsonTextFormatterService.NormalizeSingleLine(value, fallback).Trim();
        if (singleLine.Length == 0)
        {
            return fallback;
        }

        return singleLine.Length <= MaxUserTextLength
            ? singleLine
            : singleLine[..MaxUserTextLength];
    }

}
