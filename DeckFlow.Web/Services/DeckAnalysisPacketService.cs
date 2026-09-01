using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Globalization;
using DeckFlow.Core.Analysis;
using DeckFlow.Core.Bracket;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Models;
using DeckFlow.Core.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using DeckFlow.Web.Services.Bracket;
using DeckFlow.Web.Services.FeatureFlags;
using DeckFlow.Web.Services.Http;
using DeckFlow.Web.Services.Packets;
using Polly;
using Polly.Registry;
using RestSharp;
using DeckFlow.Web.Models;
using CoreScryfallCollectionIdentifier = DeckFlow.Core.Normalization.ScryfallCollectionIdentifier;
using DeckFlow.Web.Services.Manabase;
using DeckFlow.Web.Services.PromptBuilders.Analysis;
using DeckFlow.Web.Services.PromptBuilders.SetUpgrade;
using DeckFlow.Web.Services.Scryfall;

namespace DeckFlow.Web.Services;

/// <summary>
/// Builds analysis and set-upgrade prompt packets for the deck-analysis page.
/// </summary>
public interface IDeckAnalysisPacketService
{
    /// <summary>
    /// Builds the next packet outputs for the supplied workflow state.
    /// </summary>
    /// <param name="request">Current workflow request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DeckAnalysisPacketResult> BuildAsync(DeckAnalysisRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to compute the packet-session cache key for the supplied request.
    /// </summary>
    /// <param name="request">Current workflow request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> TryComputeCacheKeyAsync(DeckAnalysisRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Returns the results of a deck-analysis packet build.
/// </summary>
public sealed record DeckAnalysisPacketResult(
    string InputSummary,
    string SuggestedChatTitle,
    string DeckProfileSchemaJson,
    string? ReferenceText,
    string? AnalysisPromptText,
    string? SetUpgradePromptText,
    string? RequestContextText,
    string? TimingSummary,
    DeckAnalysisResponse? AnalysisResponse = null,
    SetUpgradeResponse? SetUpgradeResponse = null,
    string? ImportWarning = null,
    string? ResolvedCommanderName = null,
    string? DecklistText = null,
    IReadOnlyDictionary<string, string>? SetUpgradeCardText = null,
    DeckMultiAxisScore? Score = null,
    InteractionAudit? InteractionAudit = null,
    WinConMap? WinConMap = null);

/// <summary>
/// Builds analysis and set-upgrade prompt packets by hydrating decks via Scryfall, banlist, and Commander Spellbook lookups, then composing the JSON-bound prompt artifacts saved to the session zip.
/// </summary>
public sealed partial class DeckAnalysisPacketService : IDeckAnalysisPacketService
{
    private static readonly Regex AbilityWordRegex = AbilityWordPattern();
    private static readonly JsonSerializerOptions IndentedJsonSerializerOptions = new()
    {
        WriteIndented = true
    };
    private readonly IDeckEntryLoader _deckEntryLoader;
    private readonly IMechanicLookupService _mechanicLookupService;
    private readonly ICommanderBanListService _commanderBanListService;
    private readonly IScryfallSetService _scryfallSetService;
    private readonly ICommanderSpellbookService _commanderSpellbookService;
    private readonly IGameChangerCatalogService _catalogService;
    private readonly IScryfallCardResolver _scryfallCardResolver;
    private readonly ScryfallReferenceResolver _scryfallReferenceResolver;
    private readonly IScryfallCollectionProtocol _collectionProtocol;
    private readonly ILogger<DeckAnalysisPacketService> _logger;
    private readonly AnalysisPromptVariantRegistry _analysisPromptRegistry;
    private readonly SetUpgradePromptVariantRegistry _setUpgradePromptRegistry;
    private readonly PacketSessionCache _packetCache;
    private readonly IFeatureFlagCache? _flagCache;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Feature-flag key controlling reference Oracle text. Enabled (the default-on / absent / store-error
    /// state) = include full Oracle text for every card (legacy behavior). Only when an operator
    /// explicitly DISABLES this flag does the recency gate engage and drop Oracle text for older cards.
    /// The fail-safe direction is intentional: any flag-resolution failure preserves the legacy prompt
    /// rather than silently changing analysis output.
    /// </summary>
    internal const string ReferenceFullOracleFlag = "analysis.reference.full-oracle-text";

    /// <summary>
    /// Age threshold for the reference recency gate: cards released more than this many months before
    /// build time are old enough that the target AI already knows them, so their Oracle text is dropped.
    /// </summary>
    internal const int ReferenceRecencyGateMonths = 12;

    /// <summary>
    /// Feature-flag key that, when enabled, injects a pre-computed deck_stats block (land/creature
    /// counts, mana curve, average mana value, role counts) into the analysis reference data so the AI
    /// states composition facts instead of tallying 100 cards by hand. Default-off (seeded FALSE);
    /// additive, so a flag-resolution fault that turns it on only adds grounding.
    /// </summary>
    internal const string ReferenceDeckStatsFlag = "analysis.reference.deck-stats";

    /// <summary>
    /// Feature-flag key that, when enabled, names the full command zone — all partners/Background plus
    /// any companion as side metadata — in the /deck-analysis prompt for all three AI variants.
    /// Default-off (seeded FALSE); output is byte-identical when off. Unwired in Plan 73-01 (the flag,
    /// companion parameter, and request field are registered here; rendering lands in later plans).
    /// </summary>
    internal const string CommandZoneAwarenessFlag = "analysis.command-zone-awareness";

    /// <summary>
    /// Feature-flag key that, when enabled, computes a four-axis Power/Speed/Control/Consistency deck
    /// score, renders it in the Step-3 results, and folds it into all three prompt artifacts. Default-off
    /// (seeded FALSE); output is byte-identical when off. Gated on the EXPLICIT snapshot value (absent key,
    /// null cache, or store-read failure all resolve to off) — never via IsEnabled() default-on semantics.
    /// </summary>
    internal const string MultiAxisScoreFlag = "analysis.multi-axis-score";

    /// <summary>
    /// Feature-flag key that, when enabled, computes a card-backed interaction audit from the current
    /// deck's resolved references and folds the hedged first-pass block into all three prompt artifacts.
    /// Default-off (seeded FALSE); output is byte-identical when off. Gated on the EXPLICIT snapshot
    /// value only - never via IsEnabled() default-on semantics.
    /// </summary>
    internal const string InteractionAuditFlag = "analysis.interaction-audit";

    /// <summary>
    /// Feature-flag key that, when enabled, computes a win-condition/combo map from the already-fetched
    /// Commander Spellbook result plus the current deck's resolved card references and folds the hedged
    /// candidate-win-line block into all three prompt artifacts. Default-off (seeded FALSE); output is
    /// byte-identical when off. Gated on the EXPLICIT snapshot value only - never via IsEnabled()
    /// default-on semantics. Widens (does not duplicate) the single existing combo-lookup fetch.
    /// </summary>
    internal const string WinConMapFlag = "analysis.wincon-map";

    /// <summary>
    /// Registry of every feature-flag key that mutates <see cref="DeckAnalysisPacketResult.AnalysisPromptText"/>
    /// (or any other cached artifact field). The <see cref="PacketSessionCache"/> key intentionally
    /// excludes analysis flags (D-01), so a packet built while ANY of these flags is ON must never be
    /// written to (or served from) that cache — otherwise flipping the flag OFF could replay a stale
    /// flag-ON packet (the Phase-73 replay class). Add new prompt-mutating flags here as they are
    /// introduced so <see cref="ShouldBypassPacketCache"/> and the write-side bypass gate stay in sync
    /// without needing a matching edit at every call site.
    /// </summary>
    internal static readonly IReadOnlyList<string> PromptMutatingAnalysisFlags = new[]
    {
        CommandZoneAwarenessFlag,
        MultiAxisScoreFlag,
        InteractionAuditFlag,
        WinConMapFlag,
        ReferenceDeckStatsFlag,
        // Precautionary: the manabase paste artifact and swap prompt are rebuilt per request
        // (ManabaseController.Download / ManabaseAnalysisService.AnalyzeAsync) and do not currently
        // touch PacketSessionCache, so this is inert today. Register it now so any future cache-routing
        // of manabase or merged text cannot replay a stale flag-ON prompt (cf. WinConMap and the
        // followup_packet_cache_flag_replay regression). Live byte-identity is guarded by the
        // flag-gated append in ManabaseReportTextBuilder.
        ManabaseAnalysisService.KeepShapesFlagKey,
    };

    /// <summary>
    /// Upper bound (characters) applied to a resolved companion name before it reaches any prompt.
    /// Mirrors the manabase companion cap; combined with the single-line collapse it defeats
    /// newline/length-based prompt-structure injection from the free-form companion designator.
    /// </summary>
    private const int MaxCompanionNameLength = 200;

    internal DeckAnalysisPacketService(
        IScryfallCardResolver scryfallCardResolver,
        ScryfallReferenceResolver scryfallReferenceResolver,
        IDeckEntryLoader deckEntryLoader,
        IMechanicLookupService mechanicLookupService,
        ICommanderBanListService commanderBanListService,
        IScryfallSetService scryfallSetService,
        ICommanderSpellbookService commanderSpellbookService,
        IGameChangerCatalogService catalogService,
        AnalysisPromptVariantRegistry analysisPromptRegistry,
        SetUpgradePromptVariantRegistry setUpgradePromptRegistry,
        PacketSessionCache packetCache,
        IFeatureFlagCache? flagCache = null,
        ILogger<DeckAnalysisPacketService>? logger = null,
        IScryfallCollectionProtocol? collectionProtocol = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scryfallCardResolver);
        ArgumentNullException.ThrowIfNull(scryfallReferenceResolver);
        ArgumentNullException.ThrowIfNull(deckEntryLoader);
        ArgumentNullException.ThrowIfNull(mechanicLookupService);
        ArgumentNullException.ThrowIfNull(commanderBanListService);
        ArgumentNullException.ThrowIfNull(scryfallSetService);
        ArgumentNullException.ThrowIfNull(commanderSpellbookService);
        ArgumentNullException.ThrowIfNull(catalogService);
        ArgumentNullException.ThrowIfNull(analysisPromptRegistry);
        ArgumentNullException.ThrowIfNull(setUpgradePromptRegistry);
        ArgumentNullException.ThrowIfNull(packetCache);
        _scryfallCardResolver = scryfallCardResolver;
        _scryfallReferenceResolver = scryfallReferenceResolver;
        _collectionProtocol = collectionProtocol ?? new ScryfallCollectionProtocol(scryfallCardResolver);
        _deckEntryLoader = deckEntryLoader;
        _mechanicLookupService = mechanicLookupService;
        _commanderBanListService = commanderBanListService;
        _scryfallSetService = scryfallSetService;
        _commanderSpellbookService = commanderSpellbookService;
        _catalogService = catalogService;
        _analysisPromptRegistry = analysisPromptRegistry;
        _setUpgradePromptRegistry = setUpgradePromptRegistry;
        _packetCache = packetCache;
        _flagCache = flagCache;
        _logger = logger ?? NullLogger<DeckAnalysisPacketService>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// PASS-4 H1 FIX — SINGLE source of truth for pre-Scryfall commander state used by BOTH
    /// <see cref="BuildAsync"/> (write path) and <see cref="TryComputeCacheKeyAsync"/> (read path).
    /// Mirrors lines 226-271 of BuildAsync EXACTLY, including the reflag mutation at lines 257-267
    /// that sets Board="commander" on inferred commander entries in BOTH the entries collection
    /// AND the deckEntries collection.
    ///
    /// SKIPS the line-283 ValidateCommanderAsync (Scryfall) and line-435 oracle remap (also Scryfall)
    /// — those happen post-key-computation in BuildAsync only. The post-validation / post-oracle
    /// commander is used in the result itself (correct — user sees the validated name) but NOT in
    /// the cache key. Cache key uses commander as extracted from parsed deck entries at the
    /// pre-Scryfall stage WITH the inferred-commander reflag mutation applied.
    ///
    /// EXPLICIT STAGE DECISION: cache key uses pre-Scryfall commander + pre-Scryfall (but
    /// reflag-mutated) entries. Key parity between write and read paths is enforced by code
    /// locality — both call this helper with the same upstream value, then both call
    /// <see cref="BuildDeckAnalysisCacheInputs"/> with this helper's returned values.
    /// </summary>
    private static (List<DeckEntry> Entries, List<DeckEntry> DeckEntries, string? CommanderName, bool InferredCommanderFromMoxfieldOrdering) ResolvePreScryfallCommanderState(List<DeckEntry> entries)
    {
        var deckEntries = entries
            .Where(entry =>
                !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var commanderName = deckEntries
            .FirstOrDefault(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            ?.Name;
        var inferredCommanderFromMoxfieldOrdering = false;

        // Fallback for Moxfield exports without a Commander section header: the commander (or
        // partner pair) appears first in the list. Shared with the manabase analyzer via
        // CommanderInference so the leading-one-of heuristic and its alphabetical guard live in
        // one place.
        if (commanderName is null)
        {
            IReadOnlyList<string> inferredCommanderNames = CommanderInference.InferLeadingCommanderNames(entries);
            if (inferredCommanderNames.Count > 0)
            {
                var commanderNames = inferredCommanderNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
                // REFLAG MUTATION — both `entries` and `deckEntries` collections get inferred
                // commander entries reflagged Board="commander". Without this mutation,
                // BuildCanonicalDeckSourceText would sort the same logical deck differently in read
                // vs write paths because Board is the primary sort key.
                entries = entries
                    .Select(entry => commanderNames.Contains(entry.Name)
                        ? entry with { Board = "commander" }
                        : entry)
                    .ToList();
                deckEntries = deckEntries
                    .Select(entry => commanderNames.Contains(entry.Name)
                        ? entry with { Board = "commander" }
                        : entry)
                    .ToList();
                commanderName = inferredCommanderNames[0];
                inferredCommanderFromMoxfieldOrdering = true;
            }
        }

        return (entries, deckEntries, commanderName, inferredCommanderFromMoxfieldOrdering);
    }

    /// <summary>
    /// PASS-4 H1 fix — SINGLE source of truth for the deck-analysis cache-input bag.
    ///
    /// Called from BOTH (a) <see cref="TryComputeCacheKeyAsync"/> (read side) and (b) the line-467
    /// cache-write site in <see cref="BuildAsync"/> (write side). Both sides pass the
    /// SAME pre-Scryfall (entries, commanderName) tuple as returned by
    /// <see cref="ResolvePreScryfallCommanderState"/> — meaning the reflag mutation has already been
    /// applied to <paramref name="entries"/> for inferred-commander Moxfield decks. This guarantees
    /// <see cref="BuildCanonicalDeckSourceText"/> sees identical Board values on identical logical
    /// input in both paths, producing identical SHA-256 cache keys.
    /// </summary>
    private static DeckAnalysisCacheInputs BuildDeckAnalysisCacheInputs(
        DeckAnalysisRequest request,
        IReadOnlyList<DeckEntry> entries,
        string? commanderName)
    {
        return new DeckAnalysisCacheInputs(
            Commander: commanderName ?? string.Empty,
            NormalizedDeckSource: BuildCanonicalDeckSourceText(entries),
            IncludeCardVersions: request.IncludeCardVersions,
            IncludeCandidateReferencesInAnalysis: request.IncludeCandidateReferencesInAnalysis,
            TargetAiPlatformKey: request.TargetAiPlatform,
            SelectedQuestionIds: (request.SelectedAnalysisQuestions ?? new List<string>())
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// Stable canonical text representation of the loaded deck entries (D-02). Sort by board,
    /// then name, then set, then collector number for byte-stable output across URL vs paste mode.
    /// Because Board is the primary sort key, the inferred-commander reflag mutation in
    /// <see cref="ResolvePreScryfallCommanderState"/> MUST be applied before this is called —
    /// otherwise read and write paths produce different canonical text for the same logical deck.
    /// </summary>
    private static string BuildCanonicalDeckSourceText(IReadOnlyList<DeckEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries
            .OrderBy(e => e.Board ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.SetCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.CollectorNumber ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(entry.Board ?? string.Empty).Append('|')
                   .Append(entry.Quantity).Append('|')
                   .Append(entry.Name ?? string.Empty).Append('|')
                   .Append(entry.SetCode ?? string.Empty).Append('|')
                   .Append(entry.CollectorNumber ?? string.Empty).Append('\n');
        }
        return builder.ToString();
    }

    // Phase 73: explicit default-OFF read of the command-zone-awareness flag (absent key, null cache,
    // or store-read failure all resolve to off) so a flag-system fault never changes behavior.
    private bool IsCommandZoneAwarenessEnabled()
        => _flagCache is not null
            && _flagCache.Snapshot().TryGetValue(CommandZoneAwarenessFlag, out var on)
            && on;

    // Phase 80: explicit default-OFF read of the win-condition/combo-map flag, mirroring
    // IsCommandZoneAwarenessEnabled() above. Shared by the enrichment gate and the cache-bypass
    // predicate so both observe the identical snapshot-read contract.
    private bool IsWinConMapEnabled()
        => _flagCache is not null
            && _flagCache.Snapshot().TryGetValue(WinConMapFlag, out var winConOn)
            && winConOn;

    // Follow-up hardening (post-80): explicit default-OFF read of an arbitrary analysis flag key,
    // used by ShouldBypassPacketCache() to test every entry in PromptMutatingAnalysisFlags with the
    // same absent-key/null-cache/store-fault-resolves-to-off contract as the individual per-flag
    // helpers above.
    private bool IsAnalysisFlagOn(string flagKey)
        => _flagCache is not null
            && _flagCache.Snapshot().TryGetValue(flagKey, out var on)
            && on;

    // Follow-up hardening (post-80): shared predicate for the Phase-73 cache-replay class, now driven
    // by the PromptMutatingAnalysisFlags registry instead of an explicit OR chain. The PacketSessionCache
    // key excludes feature flags (D-01), so ANY flag-mutating prompt block must bypass the cache while
    // its flag is ON, or a flag-ON packet could be replayed unchanged after the flag flips OFF. This
    // closes the gap where the multi-axis-score and interaction-audit flags mutated the prompt but were
    // never added to the bypass predicate. Explicit-snapshot read only - never IsEnabled(). Used ONLY
    // as the pre-build (read-side) guard in TryComputeCacheKeyAsync -- the write-side cache decision
    // in BuildAsync gates on the build-time LATCHED locals instead (see bypassCacheWrite below) so a
    // mid-request flag flip cannot desync the value used to enrich the packet from the value used to
    // decide whether to cache it (Codex LOW/MED code-review finding #1).
    private bool ShouldBypassPacketCache()
        => PromptMutatingAnalysisFlags.Any(IsAnalysisFlagOn);

    /// <summary>
    /// Composes the D-01 cache-input field bag and returns the canonical PacketSessionCache key
    /// for this request. Re-runs the same shared deck-loader path
    /// <see cref="BuildAsync"/> uses, then calls <see cref="ResolvePreScryfallCommanderState"/>
    /// (the SAME helper BuildAsync calls in its pre-Scryfall stage) to apply the inferred-commander
    /// reflag mutation, then calls <see cref="BuildDeckAnalysisCacheInputs"/> (the SAME helper
    /// BuildAsync calls at the line-467 write site) to compose the field bag.
    ///
    /// Returns null on load failure or empty deck — silent fall-through to BuildAsync (D-11).
    /// PASS-4 H1 fix: write↔read parity is now enforced by TWO shared helpers
    /// (ResolvePreScryfallCommanderState + BuildDeckAnalysisCacheInputs), eliminating the
    /// pass-3 gap where the inline inference block lacked the reflag mutation.
    /// </summary>
    public async Task<string?> TryComputeCacheKeyAsync(DeckAnalysisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DeckSource))
        {
            return null;
        }

        // Codex 73 HIGH-1 (Phase 80: generalized to ShouldBypassPacketCache): command-zone awareness and
        // the win-condition/combo map both change AnalysisPromptText but those inputs are intentionally
        // NOT in the cache key. Rather than widen the key (which would risk the flag-OFF byte-identity
        // contract), bypass the session cache entirely while either flag is ON: returning null here means
        // no cache hit, so /deck-analysis and /deck-analysis/download always rebuild and never replay a
        // stale OFF packet, a prior companion, or a stale win-con block.
        if (ShouldBypassPacketCache())
        {
            return null;
        }

        List<DeckEntry> entries;
        try
        {
            var loaded = await _deckEntryLoader.LoadFromSourceAsync(request.DeckSource, cancellationToken: cancellationToken).ConfigureAwait(false);
            _lastImportNotice = loaded.FallbackNotice;
            entries = loaded.Entries;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DeckParseException or HttpRequestException)
        {
            return null;
        }

        if (entries.Count == 0)
        {
            return null;
        }

        // PASS-4 H1 FIX: route through the shared helper that mirrors BuildAsync lines 226-271
        // INCLUDING the reflag mutation at lines 257-267. The helper returns the (possibly
        // reflagged) entries — we MUST use its returned Entries in the cache-input call below,
        // not the local `entries` from before the call.
        var preScryfall = ResolvePreScryfallCommanderState(entries);

        if (preScryfall.DeckEntries.Count == 0)
        {
            return null;
        }

        var inputs = BuildDeckAnalysisCacheInputs(request, preScryfall.Entries, preScryfall.CommanderName);
        return PacketSessionCache.ComputeKey(inputs);
    }

    /// <summary>
    /// Builds the requested prompt outputs for the current workflow state.
    /// </summary>
    /// <param name="request">Current workflow request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DeckAnalysisPacketResult> BuildAsync(DeckAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var overallStopwatch = Stopwatch.StartNew();
        var timings = new List<(string Label, long Ms, string? Detail)>();

        if (request.WorkflowStep == 3
            && string.IsNullOrWhiteSpace(request.DeckSource)
            && !string.IsNullOrWhiteSpace(request.DeckProfileJson))
        {
            var savedAnalysisResponse = ResponseParsers.ParseAnalysisResponse(request.DeckProfileJson);
            var savedDeckProfileSchemaJson = BuildDeckProfileSchemaJson(
                string.IsNullOrWhiteSpace(savedAnalysisResponse.Commander) ? null : savedAnalysisResponse.Commander,
                string.IsNullOrWhiteSpace(savedAnalysisResponse.Format) ? request.Format : savedAnalysisResponse.Format,
                savedAnalysisResponse.DeckVersions.Count > 0);
            var savedTimingSummary = BuildTimingSummary(timings, overallStopwatch.ElapsedMilliseconds);
            // Score has no live Scryfall data to recompute on this early-return; restore it from the
            // round-tripped ScoreJson hidden field (untrusted client input — deserialized into the typed
            // record inside TryDeserializeScore, which returns null on malformed/oversized/invalid input).
            // Gate the restore on the SAME explicit flag snapshot used at Step 2 so a crafted POST cannot
            // surface the score UI while the flag is OFF — the OFF path stays byte-identical.
            var scoreFlagEnabled = _flagCache is not null
                && _flagCache.Snapshot().TryGetValue(MultiAxisScoreFlag, out var scoreFlagOn)
                && scoreFlagOn;
            var savedScore = scoreFlagEnabled ? TryDeserializeScore(request.ScoreJson) : null;
            var interactionAuditFlagEnabled = _flagCache is not null
                && _flagCache.Snapshot().TryGetValue(InteractionAuditFlag, out var interactionAuditFlagOn)
                && interactionAuditFlagOn;
            var savedInteractionAudit = interactionAuditFlagEnabled ? TryDeserializeInteractionAudit(request.InteractionAuditJson) : null;
            var winConMapFlagEnabled = _flagCache is not null
                && _flagCache.Snapshot().TryGetValue(WinConMapFlag, out var winConMapFlagOn)
                && winConMapFlagOn;
            var savedWinConMap = winConMapFlagEnabled ? TryDeserializeWinConMap(request.WinConMapJson) : null;
            return new DeckAnalysisPacketResult(
                InputSummary: BuildAnalysisSummaryFromSavedJson(savedAnalysisResponse),
                SuggestedChatTitle: BuildSuggestedChatTitle(request, savedAnalysisResponse.Commander),
                DeckProfileSchemaJson: savedDeckProfileSchemaJson,
                ReferenceText: null,
                AnalysisPromptText: null,
                SetUpgradePromptText: null,
                RequestContextText: null,
                TimingSummary: savedTimingSummary,
                AnalysisResponse: savedAnalysisResponse,
                ResolvedCommanderName: savedAnalysisResponse.Commander,
                Score: savedScore,
                InteractionAudit: savedInteractionAudit,
                WinConMap: savedWinConMap);
        }

        if (request.WorkflowStep == 5
            && string.IsNullOrWhiteSpace(request.DeckSource)
            && !string.IsNullOrWhiteSpace(request.SetUpgradeResponseJson))
        {
            var savedSetUpgradeResponse = ResponseParsers.ParseSetUpgradeResponse(request.SetUpgradeResponseJson);
            var savedAnalysisResponse = string.IsNullOrWhiteSpace(request.DeckProfileJson)
                ? null
                : ResponseParsers.ParseAnalysisResponse(request.DeckProfileJson);
            var step5Commander = savedAnalysisResponse is null || string.IsNullOrWhiteSpace(savedAnalysisResponse.Commander)
                ? null
                : savedAnalysisResponse.Commander;
            var step5DeckProfileSchemaJson = BuildDeckProfileSchemaJson(
                step5Commander,
                savedAnalysisResponse is null || string.IsNullOrWhiteSpace(savedAnalysisResponse.Format) ? request.Format : savedAnalysisResponse.Format,
                (savedAnalysisResponse?.DeckVersions.Count ?? 0) > 0);
            var step5InputSummary = savedAnalysisResponse is null
                ? string.Empty
                : BuildAnalysisSummaryFromSavedJson(savedAnalysisResponse);
            // Step 5 has no pre-built packet (no analysis/set-upgrade packet was generated on this path).
            var step5CardText = await BuildSetUpgradeCardTextAsync(request, prebuiltGeneratedPacket: null, cancellationToken).ConfigureAwait(false);
            var savedTimingSummary = BuildTimingSummary(timings, overallStopwatch.ElapsedMilliseconds);
            return new DeckAnalysisPacketResult(
                InputSummary: step5InputSummary,
                SuggestedChatTitle: BuildSuggestedChatTitle(request, savedAnalysisResponse?.Commander),
                DeckProfileSchemaJson: step5DeckProfileSchemaJson,
                ReferenceText: null,
                AnalysisPromptText: null,
                SetUpgradePromptText: null,
                RequestContextText: null,
                TimingSummary: savedTimingSummary,
                AnalysisResponse: savedAnalysisResponse,
                SetUpgradeResponse: savedSetUpgradeResponse,
                ResolvedCommanderName: savedAnalysisResponse?.Commander,
                SetUpgradeCardText: step5CardText);
        }

        if (string.IsNullOrWhiteSpace(request.DeckSource))
        {
            throw new InvalidOperationException("A deck URL or pasted deck export is required.");
        }

        var loadDeckStopwatch = Stopwatch.StartNew();
        var loaded = await _deckEntryLoader.LoadFromSourceAsync(request.DeckSource, cancellationToken: cancellationToken).ConfigureAwait(false);
        _lastImportNotice = loaded.FallbackNotice;
        var entries = loaded.Entries;
        // Phase 73-02: capture the auto-detected companion (Moxfield import metadata) so command-zone
        // awareness can surface it as SIDE METADATA when the flag is on. Discarded otherwise — never
        // mutates the deck text, so flag-OFF output stays byte-identical.
        var detectedCompanionName = loaded.DetectedCompanionName;
        timings.Add(("Deck load", loadDeckStopwatch.ElapsedMilliseconds, null));
        _logger.LogInformation("Deck Analysis packet deck load completed in {ElapsedMs}ms.", loadDeckStopwatch.ElapsedMilliseconds);
        var deckEntries = entries
            .Where(entry =>
                !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var possibleIncludeEntries = entries
            .Where(entry =>
                string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (deckEntries.Count == 0)
        {
            throw new InvalidOperationException("The submitted deck did not contain any commander or mainboard cards.");
        }

        // PASS-4 H1 FIX: route pre-Scryfall commander resolution through the shared helper. This
        // applies the inferred-commander reflag mutation to BOTH `entries` and `deckEntries`. The
        // helper's returned values are the canonical pre-Scryfall state used by the cache key.
        var preScryfallState = ResolvePreScryfallCommanderState(entries);
        entries = preScryfallState.Entries;
        deckEntries = preScryfallState.DeckEntries;
        var commanderName = preScryfallState.CommanderName;
        var inferredCommanderFromMoxfieldOrdering = preScryfallState.InferredCommanderFromMoxfieldOrdering;

        // Capture the pre-Scryfall state for the line-467 cache write BEFORE any subsequent
        // mutation (the line-273 Commander-format branch may call ValidateCommanderAsync at
        // line 283 and reassign `entries`/`commanderName` to post-Scryfall values).
        var preScryfallEntries = entries;
        var preScryfallCommanderName = commanderName;

        // Codex 73 HIGH-1 (re-review): latch the command-zone flag ONCE per request so the enrichment
        // read and the cache-write bypass below cannot observe different values if the flag flips
        // mid-build. Reading it twice could otherwise enrich the packet (flag ON) and then still cache
        // it under the OFF key (flag flipped OFF before the write), re-opening stale replay.
        var commandZoneAwareness = IsCommandZoneAwarenessEnabled();

        // Phase 80 code-review fix (Codex LOW/MED finding #1): latch the win-con-map flag here too,
        // for the same reason -- the enrichment gate below and the cache-write decision at the end of
        // this method must observe the SAME value, not two independent snapshot reads that could
        // disagree if the flag flips mid-request.
        var winConMapEnabled = IsWinConMapEnabled();

        // Follow-up hardening (post-80): latch the multi-axis-score and interaction-audit flags here
        // too, mirroring commandZoneAwareness/winConMapEnabled above, so the enrichment gate further
        // down and the write-side cache-bypass decision at the end of this method observe the SAME
        // value rather than two independent snapshot reads that could disagree if either flag flips
        // mid-request. Explicit-snapshot read only (never IsEnabled()) so a flag-system fault never
        // mutates output and the flag-OFF path stays byte-identical.
        var scoreEnabled = IsAnalysisFlagOn(MultiAxisScoreFlag);
        var interactionAuditEnabled = IsAnalysisFlagOn(InteractionAuditFlag);

        // Follow-up hardening: latch the deck-stats reference flag here too. It mutates referenceText
        // (and therefore AnalysisPromptText) when ON but was previously absent from both the
        // PromptMutatingAnalysisFlags registry and the write-side bypass gate, so a packet built with
        // deck-stats ON could be cached and replayed after the flag flipped OFF within the 5-minute TTL
        // (the same replay class as the four flags above). Latch once, at build time, so the enrichment
        // gate below and the cache-write decision at the end of this method observe the SAME value.
        // Explicit-snapshot read (never IsEnabled()) so the flag-OFF path stays byte-identical.
        var deckStatsEnabled = IsAnalysisFlagOn(ReferenceDeckStatsFlag);

        if (string.Equals(request.Format, "Commander", StringComparison.OrdinalIgnoreCase) && inferredCommanderFromMoxfieldOrdering)
        {
            var inferredCommanderNames = entries
                .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (inferredCommanderNames.Count <= 1)
            {
                var validatedCommanderName = await ValidateCommanderAsync(entries, commanderName, cancellationToken).ConfigureAwait(false);
                commanderName = validatedCommanderName;
                entries = entries
                    .Select(entry => string.Equals(entry.Name, validatedCommanderName, StringComparison.OrdinalIgnoreCase)
                        ? entry with { Board = "commander" }
                        : string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase)
                            ? entry with { Board = "main" }
                            : entry)
                    .ToList();
            }
            else
            {
                foreach (var inferredCommander in inferredCommanderNames)
                {
                    await ValidateCommanderAsync(entries, inferredCommander, cancellationToken).ConfigureAwait(false);
                }

                commanderName = inferredCommanderNames[0];
            }

            deckEntries = entries
                .Where(entry =>
                    !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
                .ToList();
            possibleIncludeEntries = entries
                .Where(entry =>
                    string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var inputSummary = BuildInputSummary(request, deckEntries, possibleIncludeEntries, commanderName);
        var decklistText = PacketTextAssembler.BuildSectionedDecklistText(deckEntries, possibleIncludeEntries);
        var requiresFullDecklists = AnalysisQuestionCatalog.RequiresFullDecklistOutput(request.SelectedAnalysisQuestions);
        var deckProfileSchemaJson = BuildDeckProfileSchemaJson(commanderName, request.Format, requiresFullDecklists);
        var requestContextText = BuildRequestContextText(request, commanderName);

        string? referenceText = null;
        string? analysisPromptText = null;
        string? setUpgradePromptText = null;
        DeckAnalysisResponse? analysisResponse = null;
        SetUpgradeResponse? setUpgradeResponse = null;
        DeckMultiAxisScore? computedScore = null;
        InteractionAudit? interactionAudit = null;
        WinConMap? winConMap = null;

        if (request.WorkflowStep >= 3 && !string.IsNullOrWhiteSpace(request.DeckProfileJson))
        {
            analysisResponse = ResponseParsers.ParseAnalysisResponse(request.DeckProfileJson);
        }

        if (request.WorkflowStep >= 5 && !string.IsNullOrWhiteSpace(request.SetUpgradeResponseJson))
        {
            setUpgradeResponse = ResponseParsers.ParseSetUpgradeResponse(request.SetUpgradeResponseJson);
        }

        var deckProfileText = string.IsNullOrWhiteSpace(request.DeckProfileJson)
            ? deckProfileSchemaJson
            : ExtractJsonObject(request.DeckProfileJson);
        var selectedQuestions = AnalysisQuestionCatalog.NormalizeSelections(request.SelectedAnalysisQuestions);
        var wantsAnalysisPacket = request.WorkflowStep == 2;
        var wantsSetUpgradeOnly = request.WorkflowStep < 2
            && (!string.IsNullOrWhiteSpace(request.DeckProfileJson) || !string.IsNullOrWhiteSpace(request.SetPacketText));
        var wantsSetUpgradePacket = request.WorkflowStep == 4 || wantsSetUpgradeOnly;

        if (wantsAnalysisPacket && CommanderBracketCatalog.Find(request.TargetCommanderBracket) is null)
        {
            throw new InvalidOperationException("Choose a target Commander bracket before generating the analysis packet.");
        }

        if (wantsAnalysisPacket && selectedQuestions.Count == 0 && string.IsNullOrWhiteSpace(request.FreeformQuestion))
        {
            throw new InvalidOperationException("Select at least one analysis question before generating the analysis packet.");
        }

        if (wantsAnalysisPacket
            && selectedQuestions.Any(questionId => questionId is "card-worth-it" or "better-alternatives")
            && request.CardSpecificQuestionCardNames.Count == 0)
        {
            throw new InvalidOperationException("Enter at least one card name for the selected card-specific analysis questions.");
        }

        if (wantsAnalysisPacket
            && AnalysisQuestionCatalog.RequiresCategoryOutput(selectedQuestions)
            && string.IsNullOrWhiteSpace(request.DecklistExportFormat))
        {
            throw new InvalidOperationException("Choose Moxfield or Archidekt as the export format when assigning or updating categories — plain text does not support inline category formatting.");
        }

        if (wantsAnalysisPacket
            && selectedQuestions.Contains("budget-upgrades", StringComparer.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.BudgetUpgradeAmount))
        {
            throw new InvalidOperationException("Enter a budget amount for the selected budget upgrade question.");
        }

        // Only fetch banned list and set packet when the analysis or set-upgrade step actually needs them.
        // Hoisted so the set-upgrade card-text resolution can reuse the already-fetched packet instead of
        // re-fetching it from Scryfall.
        string? generatedSetPacket = null;
        if (wantsAnalysisPacket || wantsSetUpgradePacket)
        {
            // Fire banned-list and set-packet fetches in parallel — neither depends on the other.
            var parallelStopwatch = Stopwatch.StartNew();
            var bannedCardsTask = _commanderBanListService.GetBannedCardsAsync(cancellationToken);
            var setPacketTask = BuildGeneratedSetPacketAsync(request, cancellationToken);
            await Task.WhenAll(bannedCardsTask, setPacketTask).ConfigureAwait(false);
            timings.Add(("Ban list + set packet", parallelStopwatch.ElapsedMilliseconds, null));
            _logger.LogInformation("Deck Analysis packet banned-list + set-packet fetch completed in {ElapsedMs}ms.", parallelStopwatch.ElapsedMilliseconds);
            var bannedCards = bannedCardsTask.Result;
            generatedSetPacket = setPacketTask.Result;

            if (wantsAnalysisPacket)
            {
                var analysisPossibleIncludeEntries = possibleIncludeEntries
                    .Where(entry =>
                        (request.IncludeCandidateReferencesInAnalysis
                            && (string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                var cardReferenceRequests = BuildAnalysisCardReferenceRequests(deckEntries, analysisPossibleIncludeEntries);

                // scoreEnabled, interactionAuditEnabled, and winConMapEnabled are all latched once at
                // the top of BuildAsync (see the follow-up hardening / Phase 80 code-review fix comments
                // above) so they are reused here rather than re-read.

                // Start combo lookup immediately — only needs deckEntries, independent of Scryfall lookups.
                // Widen the SINGLE combo gate so the one fetch ALSO fires when the score flag or the
                // win-con map flag is on (Power/Consistency need combo density even when no combo question
                // was selected; the win-con map needs the same combo result to build its ranked-combo
                // block). The result is reused for the prompt combo-reference text, the score, AND the
                // win-con map — never double-fetched.
                var comboStopwatch = Stopwatch.StartNew();
                var requiresComboLookup = AnalysisQuestionCatalog.RequiresComboLookup(selectedQuestions);
                var comboTask = (scoreEnabled || winConMapEnabled || requiresComboLookup)
                    ? _commanderSpellbookService.FindCombosAsync(deckEntries, cancellationToken)
                    : Task.FromResult<CommanderSpellbookResult?>(null);

                var cardReferenceStopwatch = Stopwatch.StartNew();
                var cardReferenceBundle = await LookupCardReferencesAsync(cardReferenceRequests, cancellationToken).ConfigureAwait(false);
                timings.Add(("Scryfall card lookup", cardReferenceStopwatch.ElapsedMilliseconds, $"{cardReferenceBundle.CardReferences.Count} cards, {cardReferenceBundle.MechanicNames.Count} mechanics found"));
                _logger.LogInformation(
                    "Deck Analysis packet card reference lookup completed in {ElapsedMs}ms for {CardCount} cards and {MechanicCount} mechanics.",
                    cardReferenceStopwatch.ElapsedMilliseconds,
                    cardReferenceBundle.CardReferences.Count,
                    cardReferenceBundle.MechanicNames.Count);
                var mechanicReferenceStopwatch = Stopwatch.StartNew();
                var mechanicReferences = await LookupMechanicReferencesAsync(cardReferenceBundle.MechanicNames, cancellationToken).ConfigureAwait(false);
                timings.Add(("Mechanic rules lookup", mechanicReferenceStopwatch.ElapsedMilliseconds, $"{mechanicReferences.Count} mechanics resolved"));
                _logger.LogInformation(
                    "Deck Analysis packet mechanic lookup completed in {ElapsedMs}ms for {MechanicCount} mechanics.",
                    mechanicReferenceStopwatch.ElapsedMilliseconds,
                    mechanicReferences.Count);

                // Fail-safe: gate engages ONLY when the full-Oracle flag is explicitly disabled. A null
                // cache, an absent flag, or a store-read failure all resolve to "keep full Oracle"
                // (legacy), so a flag-system fault never silently mutates analysis output.
                var recencyGateEnabled = !(_flagCache?.IsEnabled(ReferenceFullOracleFlag) ?? true);
                var recencyCutoff = DateOnly.FromDateTime(_timeProvider.GetUtcNow().DateTime).AddMonths(-ReferenceRecencyGateMonths);

                // Pre-computed deck_stats (flag-controlled, additive): LLMs miscount long card lists, so
                // state composition facts (lands, creatures, curve, role counts) instead of asking the AI
                // to tally 100 cards. Empty when the flag is off, in which case the block is omitted.
                //
                // Fail-safe default-OFF: gated on the build-time-latched deckStatsEnabled (an explicit
                // snapshot read via IsAnalysisFlagOn — absent key, null cache, or store-read failure all
                // resolve to off, matching the documented default). Latched at method scope so the same
                // value drives this enrichment block and the write-side cache-bypass decision.
                var deckStatsText = deckStatsEnabled
                    ? BuildDeckStatsText(cardReferenceBundle.CardReferences)
                    : string.Empty;

                referenceText = BuildReferenceText(request, mechanicReferences, cardReferenceBundle.CardReferences, bannedCards, recencyGateEnabled, recencyCutoff, deckStatsText, _timeProvider);

                var comboResult = await comboTask.ConfigureAwait(false);
                // Keep the timing line gated on the prompt-side combo requirement so a score-only fetch
                // does not add a "Commander Spellbook" timing row that the OFF path would not emit.
                if (requiresComboLookup)
                {
                    timings.Add(("Commander Spellbook", comboStopwatch.ElapsedMilliseconds, $"{comboResult?.IncludedCombos.Count ?? 0} combos, {comboResult?.AlmostIncludedCombos.Count ?? 0} near-combos"));
                }
                _logger.LogInformation(
                    "Commander Spellbook lookup completed in {ElapsedMs}ms. Included={Included} AlmostIncluded={AlmostIncluded}.",
                    comboStopwatch.ElapsedMilliseconds,
                    comboResult?.IncludedCombos.Count ?? 0,
                    comboResult?.AlmostIncludedCombos.Count ?? 0);

                // Score/interaction-audit/win-con-map each filter the SAME current-deck, non-commander
                // card slice before projecting to their own DTO. Materialize it lazily ONCE and reuse it
                // across all three blocks below so the flag-OFF path (all three off) still runs ZERO
                // passes over cardReferenceBundle.CardReferences -- no regression on the byte-identity-
                // critical cheap path (Codex LOW efficiency finding #4).
                List<CardReference>? currentDeckNonCommanderCards = null;

                // Multi-axis score (flag-gated). Compute from the CURRENT-deck resolved card references
                // (mirrors BuildDeckStatsText input prep), the bracket classification (Game Changers +
                // two-card combos), and the reused combo result. comboDetectionAvailable distinguishes a
                // null combo result (API unavailable) from an empty one (ran, found none) so the scorer
                // never reports "0 combos" when detection simply failed (Pitfall 1).
                string? scoreBlockText = null;
                if (scoreEnabled)
                {
                    currentDeckNonCommanderCards ??= cardReferenceBundle.CardReferences
                        .Where(card => IsCurrentDeckScope(card.Scope) && !card.IsCommander)
                        .ToList();
                    var comboDetectionAvailable = comboResult is not null;
                    var scoreStats = DeckStatAggregator.Compute(currentDeckNonCommanderCards
                        .Select(card => new DeckStatCardInput(card.Quantity, card.TypeLine, card.OracleText, card.ManaCost)));

                    IReadOnlyList<TwoCardCombo>? scoreTwoCardCombos = comboResult is null
                        ? null
                        : comboResult.IncludedCombos
                            .Where(combo => combo.CardNames.Count == 2)
                            .Select(combo => new TwoCardCombo(combo.CardNames, combo.Results))
                            .ToList();

                    var bracketClassification = BracketClassifier.Classify(
                        deckEntries, _catalogService.GetCatalog(), scoreTwoCardCombos);

                    computedScore = MultiAxisScorer.Score(
                        scoreStats,
                        bracketClassification.DetectedGameChangers.Count,
                        scoreTwoCardCombos?.Count ?? 0,
                        comboDetectionAvailable,
                        bracketClassification.BracketNumber);
                    scoreBlockText = BuildScoreBlockText(computedScore);
                }

                string? interactionAuditText = null;
                if (interactionAuditEnabled)
                {
                    currentDeckNonCommanderCards ??= cardReferenceBundle.CardReferences
                        .Where(card => IsCurrentDeckScope(card.Scope) && !card.IsCommander)
                        .ToList();
                    interactionAudit = InteractionAuditAggregator.Compute(currentDeckNonCommanderCards
                        .Select(card => new InteractionCardInput(card.Quantity, card.Name, card.TypeLine, card.OracleText, card.ManaCost)));
                    interactionAuditText = BuildInteractionAuditText(interactionAudit);
                }

                // Win-condition/combo map (flag-gated). Maps the ALREADY-FETCHED comboResult (widened
                // gate above — no second fetch) plus the current-deck resolved card references onto the
                // Core input DTOs and computes once. comboDataAvailable distinguishes a null comboResult
                // (Commander Spellbook unavailable) from an empty one (ran, found nothing) — mirrors the
                // same comboDetectionAvailable distinction the score block makes above (WINCON-03).
                string? winConMapText = null;
                if (winConMapEnabled)
                {
                    var winConCombos = comboResult?.IncludedCombos
                        .Select(c => new WinConComboInput(c.CardNames, c.Results, c.ManaValueNeeded, c.Popularity))
                        .ToList() ?? new List<WinConComboInput>();
                    var winConNearCombos = comboResult?.AlmostIncludedCombos
                        .Select(a => new WinConNearComboInput(a.MissingCard, a.CardsInDeck, a.Results))
                        .ToList() ?? new List<WinConNearComboInput>();
                    currentDeckNonCommanderCards ??= cardReferenceBundle.CardReferences
                        .Where(card => IsCurrentDeckScope(card.Scope) && !card.IsCommander)
                        .ToList();
                    var winConClosingCards = currentDeckNonCommanderCards
                        .Select(card => new WinConClosingCardInput(card.Quantity, card.Name, card.TypeLine, card.OracleText));
                    var comboDataAvailable = comboResult is not null;

                    winConMap = WinConMapAggregator.Compute(winConCombos, winConNearCombos, winConClosingCards, comboDataAvailable);
                    winConMapText = BuildWinConMapText(winConMap);
                }

                // Resolve commander name to oracle name if the deck used a renamed printing.
                if (commanderName is not null && cardReferenceBundle.OracleNameMap.TryGetValue(commanderName, out var oracleCommanderName))
                {
                    commanderName = oracleCommanderName;
                }

                // Phase 73-02: command-zone awareness (default-OFF). Gate on the EXPLICIT snapshot value
                // (absent key, null cache, or store-read failure all resolve to off) so a flag-system
                // fault never mutates output and flag-OFF stays byte-identical. When on, name the FULL
                // command zone (all partners / commander+Background, each oracle-resolved INDIVIDUALLY
                // before the " & " join — never concatenate before resolving) and resolve the companion
                // as side metadata. The deck text, cache key, and ResolvePreScryfallCommanderState are
                // left untouched.
                string? companionName = null;
                if (commandZoneAwareness)
                {
                    // Codex 73 LOW-3: oracle-resolve each command-zone name FIRST, then dedupe and order on
                    // the RESOLVED names so two import aliases that collapse to the same oracle name do not
                    // produce a duplicate in the joined string and ordering is deterministic on final names.
                    var resolvedCommanderNames = deckEntries
                        .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
                        .Select(entry => entry.Name)
                        .Select(name => cardReferenceBundle.OracleNameMap.TryGetValue(name, out var oracleName) ? oracleName : name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (resolvedCommanderNames.Count >= 2)
                    {
                        commanderName = string.Join(" & ", resolvedCommanderNames);
                    }

                    companionName = ResolveCompanionName(request.CompanionName, detectedCompanionName);
                }

                var includeCardVersions = AnalysisQuestionCatalog.RequiresFullDecklistOutput(selectedQuestions) && request.IncludeCardVersions;
                var analysisDecklistText = includeCardVersions
                    ? PacketTextAssembler.BuildSectionedDecklistText(deckEntries, analysisPossibleIncludeEntries, includeVersions: true, oracleNameMap: cardReferenceBundle.OracleNameMap)
                    : PacketTextAssembler.BuildSectionedDecklistText(deckEntries, analysisPossibleIncludeEntries, oracleNameMap: cardReferenceBundle.OracleNameMap);
                // Keep the prompt-side combo-reference gate intact: only emit combo text when a combo
                // question was selected, so widening the fetch for the score never changes prompt output.
                var promptComboResult = requiresComboLookup ? comboResult : null;
                analysisPromptText = BuildAnalysisPrompt(request, analysisDecklistText, referenceText, deckProfileSchemaJson, commanderName, selectedQuestions, bannedCards, promptComboResult, includeCardVersions, companionName, scoreBlockText, interactionAuditText, winConMapText);
                if (wantsSetUpgradePacket)
                {
                    var oracleResolvedDecklistText = PacketTextAssembler.BuildSectionedDecklistText(deckEntries, possibleIncludeEntries, oracleNameMap: cardReferenceBundle.OracleNameMap);
                    setUpgradePromptText = BuildSetUpgradePrompt(request, oracleResolvedDecklistText, deckProfileText, commanderName, generatedSetPacket, bannedCards);
                }
            }
            else if (wantsSetUpgradePacket)
            {
                setUpgradePromptText = BuildSetUpgradePrompt(request, decklistText, deckProfileText, commanderName, generatedSetPacket, bannedCards);
            }
        }

        _logger.LogInformation(
            "Deck Analysis packet build completed in {ElapsedMs}ms. AnalysisGenerated={AnalysisGenerated} SetPacketGenerated={SetPacketGenerated}.",
            overallStopwatch.ElapsedMilliseconds,
            !string.IsNullOrWhiteSpace(analysisPromptText),
            !string.IsNullOrWhiteSpace(setUpgradePromptText));

        var timingSummary = BuildTimingSummary(timings, overallStopwatch.ElapsedMilliseconds);

        var suggestedChatTitle = BuildSuggestedChatTitle(request, commanderName);

        var setUpgradeCardText = setUpgradeResponse is null
            ? null
            : await BuildSetUpgradeCardTextAsync(request, generatedSetPacket, cancellationToken).ConfigureAwait(false);

        var result = new DeckAnalysisPacketResult(
            inputSummary,
            suggestedChatTitle,
            deckProfileSchemaJson,
            referenceText,
            analysisPromptText,
            setUpgradePromptText,
            requestContextText,
            timingSummary,
            analysisResponse,
            setUpgradeResponse,
            ImportWarning: _lastImportNotice,
            ResolvedCommanderName: commanderName,
            DecklistText: decklistText,
            SetUpgradeCardText: setUpgradeCardText,
            Score: computedScore,
            InteractionAudit: interactionAudit,
            WinConMap: winConMap);

        // Phase 999.3 cache write (PASS-4 H1 FIX). Use the pre-Scryfall entries +
        // commanderName captured immediately after the ResolvePreScryfallCommanderState call.
        // Both BuildAsync and TryComputeCacheKeyAsync route through the SAME two shared helpers
        // (ResolvePreScryfallCommanderState + BuildDeckAnalysisCacheInputs), guaranteeing
        // identical SHA-256 keys for identical logical inputs — including for Moxfield decks
        // without an explicit commander section (the case Codex pass-3 flagged).
        // Codex 73 HIGH-1 (Phase 80 code-review fix, finding #1; follow-up hardening widened to all
        // prompt-mutating flags) — gate on the BUILD-TIME LATCHED locals (commandZoneAwareness,
        // scoreEnabled, interactionAuditEnabled, winConMapEnabled, deckStatsEnabled), NOT a fresh
        // ShouldBypassPacketCache() re-read. A fresh re-read here could disagree with the value actually
        // used to enrich this packet if any flag flipped mid-request, letting an enriched packet get
        // cached under a flag-OFF key (or vice versa) and later replayed once the flag state changes
        // again. This also closes the open gap where score/interaction-audit/deck-stats packets were
        // being cached and could be replayed after the flag flipped OFF.
        var bypassCacheWrite = commandZoneAwareness || scoreEnabled || interactionAuditEnabled || winConMapEnabled || deckStatsEnabled;
        if (!bypassCacheWrite)
        {
            var cacheInputs = BuildDeckAnalysisCacheInputs(request, preScryfallEntries, preScryfallCommanderName);
            var cacheKey = PacketSessionCache.ComputeKey(cacheInputs);
            _packetCache.Set(cacheKey, result, PacketSizeEstimator.EstimateSizeBytes(result));
        }

        return result;
    }


    /// <summary>
    /// Warning surfaced to the UI when the Moxfield fallback (Commander Spellbook) was used.
    /// Set from the shared deck loader result, read during BuildAsync, cleared per call.
    /// </summary>
    private string? _lastImportNotice;

    /// <summary>
    /// Builds the short deck summary shown above the generated prompt packets.
    /// </summary>
    private static string BuildInputSummary(DeckAnalysisRequest request, IReadOnlyList<DeckEntry> entries, IReadOnlyList<DeckEntry> possibleIncludeEntries, string? commanderName)
    {
        var mainDeckCards = entries
            .Where(entry => string.Equals(entry.Board, "mainboard", StringComparison.OrdinalIgnoreCase))
            .Sum(entry => entry.Quantity);
        var commanderCards = entries
            .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .Sum(entry => entry.Quantity);
        var sideboardCards = possibleIncludeEntries
            .Where(entry => string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .Sum(entry => entry.Quantity);
        var maybeboardCards = possibleIncludeEntries
            .Where(entry => string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase))
            .Sum(entry => entry.Quantity);
        var builder = new StringBuilder();
        builder.AppendLine($"Deck: {ResolveDisplayName(request.DeckName, commanderName, "Commander Deck")}");
        builder.AppendLine();
        builder.AppendLine($"Format: {NormalizeSingleLine(request.Format, "Commander")}");
        if (!string.IsNullOrWhiteSpace(request.DeckName))
        {
            builder.AppendLine($"Deck name: {request.DeckName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(commanderName))
        {
            builder.AppendLine($"Commander: {commanderName}");
        }

        builder.AppendLine($"Main deck cards: {mainDeckCards}");
        if (!string.IsNullOrWhiteSpace(commanderName) || commanderCards > 0)
        {
            builder.AppendLine($"Commander cards: {commanderCards}");
        }

        if (possibleIncludeEntries.Count > 0)
        {
            builder.AppendLine($"Possible includes: {possibleIncludeEntries.Sum(entry => entry.Quantity)}");
            if (sideboardCards > 0)
            {
                builder.AppendLine($"Sideboard cards: {sideboardCards}");
            }

            if (maybeboardCards > 0)
            {
                builder.AppendLine($"Maybeboard cards: {maybeboardCards}");
            }
        }

        var bracket = CommanderBracketCatalog.Find(request.TargetCommanderBracket);
        if (bracket is not null)
        {
            builder.AppendLine($"Target commander bracket: {bracket.Label}");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats the Commander Spellbook combo lookup result as a reference block for injection into the analysis prompt.
    /// Returns an empty string when no combo data is available.
    /// </summary>
    internal static string BuildComboReferenceText(CommanderSpellbookResult? result)
    {
        if (result is null
            || (result.IncludedCombos.Count == 0 && result.AlmostIncludedCombos.Count == 0))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Commander Spellbook combo reference (verified data — use this when answering combo questions):");

        if (result.IncludedCombos.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"COMPLETE COMBOS IN THIS DECK ({result.IncludedCombos.Count}):");
            for (var i = 0; i < result.IncludedCombos.Count; i++)
            {
                var combo = result.IncludedCombos[i];
                builder.AppendLine($"{i + 1}. Cards: {string.Join(" + ", combo.CardNames)}");
                builder.AppendLine($"   Result: {string.Join(", ", combo.Results)}");
                if (!string.IsNullOrWhiteSpace(combo.Instructions))
                {
                    builder.AppendLine($"   How: {combo.Instructions}");
                }
            }
        }

        if (result.AlmostIncludedCombos.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"COMBOS ONE CARD AWAY (within color identity) ({result.AlmostIncludedCombos.Count}):");
            for (var i = 0; i < result.AlmostIncludedCombos.Count; i++)
            {
                var combo = result.AlmostIncludedCombos[i];
                builder.AppendLine($"{i + 1}. Missing: {combo.MissingCard} | Have: {string.Join(" + ", combo.CardsInDeck)}");
                builder.AppendLine($"   Result: {string.Join(", ", combo.Results)}");
                if (!string.IsNullOrWhiteSpace(combo.Instructions))
                {
                    builder.AppendLine($"   How: {combo.Instructions}");
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Suggests a conversation title derived from the deck name (falling back to commander).
    /// </summary>
    private static string BuildSuggestedChatTitle(DeckAnalysisRequest request, string? commanderName)
    {
        var primaryName = ResolveDisplayName(request.DeckName, commanderName, "Commander Deck");

        return $"{primaryName} | AI Deck Analysis";
    }

    private static string BuildAnalysisSummaryFromSavedJson(DeckAnalysisResponse analysisResponse)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Deck: {ResolveDisplayName(null, analysisResponse.Commander, "Commander Deck")}");
        builder.AppendLine();
        builder.AppendLine($"Format: {NormalizeSingleLine(analysisResponse.Format, "Commander")}");

        if (!string.IsNullOrWhiteSpace(analysisResponse.Commander))
        {
            builder.AppendLine($"Commander: {analysisResponse.Commander.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(analysisResponse.GamePlan))
        {
            builder.AppendLine($"Game plan: {analysisResponse.GamePlan.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(analysisResponse.Speed))
        {
            builder.AppendLine($"Speed: {analysisResponse.Speed.Trim()}");
        }

        if (analysisResponse.PrimaryAxes.Count > 0)
        {
            builder.AppendLine($"Primary axes: {string.Join(", ", analysisResponse.PrimaryAxes)}");
        }

        if (analysisResponse.SynergyTags.Count > 0)
        {
            builder.AppendLine($"Synergy tags: {string.Join(", ", analysisResponse.SynergyTags)}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string ResolveDisplayName(string? deckName, string? commanderName, string fallback)
        => !string.IsNullOrWhiteSpace(deckName)
            ? deckName.Trim()
            : !string.IsNullOrWhiteSpace(commanderName)
                ? commanderName.Trim()
                : fallback;

    /// <summary>
    /// Builds the authoritative card, mechanic, and banned-list reference bundle used during analysis.
    /// </summary>
    private static string BuildReferenceText(
        DeckAnalysisRequest request,
        IReadOnlyList<MechanicReference> mechanicReferences,
        IReadOnlyList<CardReference> cardReferences,
        IReadOnlyList<string> bannedCards,
        bool recencyGateEnabled,
        DateOnly recencyCutoff,
        string deckStatsText,
        TimeProvider timeProvider)
    {
        var builder = new StringBuilder();
        builder.AppendLine("reference_context:");
        builder.AppendLine("source: Scryfall Oracle and official Wizards Comprehensive Rules");
        builder.AppendLine($"generated_at_utc: {timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ssZ}");
        builder.AppendLine($"format: {NormalizeSingleLine(request.Format, "Commander")}");
        builder.AppendLine();
        if (!string.IsNullOrEmpty(deckStatsText))
        {
            builder.AppendLine(deckStatsText);
            builder.AppendLine();
        }

        builder.AppendLine($"official_commander_banned_cards: {FormatBannedCardsLine(bannedCards)}");
        builder.AppendLine();
        builder.AppendLine("mechanics:");
        if (mechanicReferences.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var mechanicReference in mechanicReferences)
            {
                builder.AppendLine($"{mechanicReference.Name}: {mechanicReference.Description}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("cards:");
        // Only describe the candidate_include scope when the user opted in to sideboard/maybeboard
        // references. With the box unchecked no candidate cards are emitted, so naming the scope here
        // would be dead legend (and would prime the AI to look for candidates that aren't present).
        builder.AppendLine(request.IncludeCandidateReferencesInAnalysis
            ? "[current_deck] = active deck. [candidate_include:sideboard] and [candidate_include:maybeboard] = optional candidates only."
            : "[current_deck] = active deck.");
        if (cardReferences.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var cardReference in cardReferences)
            {
                var mdfcMarker = cardReference.IsMdfcLand ? " [MDFC-land]" : string.Empty;

                // Recency gate (flag-controlled): well-known older cards are already in the model's
                // parametric knowledge, so their Oracle text is ~token-only noise. Drop it for cards
                // released before the cutoff; keep it for recent/unknown-date printings the model may
                // not know yet, preserving grounding where it actually changes the answer.
                //
                // The gate applies ONLY to non-commander current_deck cards. candidate_include
                // (sideboard/maybeboard) cards are the ones the user is asking the AI to evaluate for
                // inclusion — the most uncertain, highest-stakes cards — and the commander is the single
                // most important card for the analysis; both always keep full Oracle text regardless of
                // printing age.
                var gateApplies = IsCurrentDeckScope(cardReference.Scope) && !cardReference.IsCommander;
                var includeOracle = !gateApplies
                    || ShouldIncludeOracleText(cardReference.ReleasedAt, recencyGateEnabled, recencyCutoff);
                builder.AppendLine(includeOracle
                    ? $"[{cardReference.Scope}] {cardReference.Name} | {cardReference.ManaCost} | {cardReference.TypeLine} | {cardReference.OracleText}{mdfcMarker}"
                    : $"[{cardReference.Scope}] {cardReference.Name} | {cardReference.ManaCost} | {cardReference.TypeLine}{mdfcMarker}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Decides whether a reference card's Oracle text should be emitted. With the recency gate off,
    /// Oracle text is always included (legacy behavior). With the gate on, Oracle text is kept only
    /// for cards released on or after <paramref name="recencyCutoff"/>; cards with a missing or
    /// unparseable release date keep their Oracle text (fail open, so grounding is never silently lost).
    /// </summary>
    /// <param name="releasedAt">Scryfall <c>released_at</c> date string (yyyy-MM-dd), or null.</param>
    /// <param name="recencyGateEnabled">Whether the recency gate flag is on.</param>
    /// <param name="recencyCutoff">Cards older than this date drop their Oracle text when gated.</param>
    internal static bool ShouldIncludeOracleText(string? releasedAt, bool recencyGateEnabled, DateOnly recencyCutoff)
    {
        if (!recencyGateEnabled)
        {
            return true;
        }

        // Fail open: unknown/unparseable release date keeps Oracle text so we never drop grounding
        // for a card we simply could not date.
        if (!DateOnly.TryParse(releasedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var released))
        {
            return true;
        }

        return released >= recencyCutoff;
    }

    /// <summary>
    /// Formats the pre-computed deck_stats block from the resolved current-deck cards, excluding the
    /// commander(s). Counts are tallied by <see cref="DeckStatAggregator"/> so the prompt can state them
    /// as facts rather than asking the AI to count a 100-card list.
    /// </summary>
    /// <param name="cardReferences">All resolved reference cards (current deck + candidates).</param>
    private static string BuildDeckStatsText(
        IReadOnlyList<CardReference> cardReferences)
    {
        // Exclude the commander via the IsCommander flag (carried from DeckEntry.Board through the
        // reference pipeline), NOT by name-matching: the resolved Scryfall name can differ from the
        // submitted name (alt-art / Universes Beyond), and printing-fallback references carry a
        // composite "submitted_name: X | resolved_card: Y" name that no bare name set would match.
        var inputs = cardReferences
            .Where(card => IsCurrentDeckScope(card.Scope) && !card.IsCommander)
            .Select(card => new DeckStatCardInput(card.Quantity, card.TypeLine, card.OracleText, card.ManaCost));

        var stats = DeckStatAggregator.Compute(inputs);

        var builder = new StringBuilder();
        builder.AppendLine("deck_stats (counts computed from this deck's Scryfall-resolved cards, as a counting aid; any card that failed lookup is omitted):");
        builder.AppendLine($"cards: {stats.Cards} (excludes commander)");
        builder.AppendLine($"lands: {stats.Lands}");
        builder.AppendLine($"creatures: {stats.Creatures}");
        // Invariant culture so the decimal separator is always '.' regardless of server locale
        // (a comma-decimal host would otherwise emit "2,00" in this machine-readable block).
        builder.AppendLine($"average_mana_value: {stats.AverageManaValue.ToString("0.00", CultureInfo.InvariantCulture)} (nonland)");
        builder.AppendLine($"mana_curve: 0-1={stats.ManaCurve["0-1"]} 2={stats.ManaCurve["2"]} 3={stats.ManaCurve["3"]} 4={stats.ManaCurve["4"]} 5+={stats.ManaCurve["5+"]}");
        builder.Append($"role_counts: ramp={stats.Ramp} draw={stats.Draw} interaction={stats.Interaction} wipes={stats.Wipes} recursion={stats.Recursion} closing_power={stats.ClosingPower}");
        return builder.ToString();
    }

    /// <summary>True when a reference card belongs to the active deck (not a sideboard/maybeboard candidate).</summary>
    private static bool IsCurrentDeckScope(string scope)
        => string.Equals(scope, "current_deck", StringComparison.OrdinalIgnoreCase);

    internal static bool IsModalDfcLand(ScryfallCard card)
        => card.Layout == "modal_dfc"
            && card.CardFaces?.Any(face => face.TypeLine?.Contains("Land", StringComparison.OrdinalIgnoreCase) == true) == true;

    private static IReadOnlyList<CardReferenceRequest> BuildAnalysisCardReferenceRequests(
        IReadOnlyList<DeckEntry> deckEntries,
        IReadOnlyList<DeckEntry> analysisPossibleIncludeEntries)
    {
        var requests = new List<CardReferenceRequest>();

        requests.AddRange(deckEntries
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new CardReferenceRequest(
                entry.Name,
                "current_deck",
                entry.Quantity,
                string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))));

        requests.AddRange(analysisPossibleIncludeEntries
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new CardReferenceRequest(
                entry.Name,
                string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase)
                    ? "candidate_include:sideboard"
                    : "candidate_include:maybeboard",
                entry.Quantity)));

        return requests
            .GroupBy(request => request.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>
    /// Builds the main analysis prompt from the deck text, references, bracket guidance, and selected questions.
    /// Internal for test access — per-AI dispatcher exercised by the AI result contract tests.
    /// </summary>
    // Phase 15-02: converted from internal static to instance method; dispatches via injected AnalysisPromptVariantRegistry.
    internal string BuildAnalysisPrompt(DeckAnalysisRequest request, string decklistText, string referenceText, string deckProfileSchemaJson, string? commanderName, IReadOnlyList<string> selectedQuestionIds, IReadOnlyList<string> bannedCards, CommanderSpellbookResult? comboResult = null, bool includeCardVersions = false, string? companionName = null, string? scoreBlockText = null, string? interactionAuditText = null, string? winConMapText = null)
    {
        var enrichments = new AnalysisPromptEnrichments(companionName, scoreBlockText, interactionAuditText, winConMapText);
        return _analysisPromptRegistry.Build(
            AiPlatform.Normalize(request.TargetAiPlatform),
            request, decklistText, referenceText, deckProfileSchemaJson,
            commanderName, selectedQuestionIds, bannedCards,
            comboResult, includeCardVersions, enrichments);
    }

    /// <summary>
    /// Renders the multi-axis deck score as a paste-safe ASCII artifact block (UI-SPEC §10) folded into
    /// each analysis prompt variant. Header, four aligned "Axis: N/5 Label (rationale)" lines, the bracket
    /// cross-check note, and the heuristic disclaimer. ASCII only — plain hyphens, no em/en dashes.
    /// Internal-static for direct unit testing via <c>[InternalsVisibleTo]</c>.
    /// </summary>
    /// <param name="score">The computed four-axis score.</param>
    internal static string BuildScoreBlockText(DeckMultiAxisScore score)
    {
        ArgumentNullException.ThrowIfNull(score);
        var builder = new StringBuilder();
        builder.AppendLine("DECK SCORE (coarse 0-5 bands - magnitude, not quality)");
        builder.AppendLine(FormatScoreAxisLine("Power", score.PowerBand, score.PowerRationale.SignalText));
        builder.AppendLine(FormatScoreAxisLine("Speed", score.SpeedBand, score.SpeedRationale.SignalText));
        builder.AppendLine(FormatScoreAxisLine("Control", score.ControlBand, score.ControlRationale.SignalText));
        builder.AppendLine(FormatScoreAxisLine("Consistency", score.ConsistencyBand, score.ConsistencyRationale.SignalText));
        builder.AppendLine($"Cross-check: {score.BracketCrossCheckText}");
        builder.Append("(These bands are DeckFlow heuristic estimates from decklist signals - re-check and refine.)");
        return builder.ToString();
    }

    /// <summary>Formats one score axis line: <c>"  Power:       4/5  High      (rationale)"</c>.</summary>
    private static string FormatScoreAxisLine(string axisLabel, int band, string rationale)
        => $"  {(axisLabel + ":").PadRight(12)} {band}/5  {MultiAxisScorer.BandLabel(band).PadRight(9)} ({rationale})";

    /// <summary>
    /// Renders the interaction audit as a paste-safe ASCII artifact block. Counts are explicitly hedged
    /// as approximate because the target AI must verify DeckFlow's heuristic first-pass card buckets.
    /// </summary>
    /// <param name="audit">The computed interaction audit.</param>
    internal static string BuildInteractionAuditText(InteractionAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        var builder = new StringBuilder();
        builder.AppendLine("INTERACTION AUDIT (DeckFlow heuristic first pass - verify against the cards)");
        builder.AppendLine(FormatInteractionBucketLine("Targeted removal", audit.TargetedRemoval));
        builder.AppendLine(FormatInteractionBucketLine("Board wipes", audit.BoardWipes));
        builder.AppendLine(FormatInteractionBucketLine("Counterspells", audit.Counterspells));
        builder.AppendLine(FormatInteractionBucketLine("Protection or recursion", audit.ProtectionRecursion));
        builder.AppendLine(FormatInteractionBucketLine("Stax or taxation", audit.StaxTaxation));
        builder.AppendLine($"Coverage gaps to verify: {(audit.CoverageGaps.Count == 0 ? "none flagged by DeckFlow" : string.Join(", ", audit.CoverageGaps))}");
        builder.Append("Use these approximately counted buckets as a starting point only - verify every count and card role against the supplied card text.");
        return builder.ToString();
    }

    private static string FormatInteractionBucketLine(string label, InteractionBucketResult bucket)
    {
        var confidentCount = bucket.Confident.Sum(card => card.Quantity);
        var line = $"  {label}: approximately {confidentCount} confident - {FormatInteractionCards(bucket.Confident)}";
        if (bucket.Review.Count > 0)
        {
            line += $" (review: {FormatInteractionCards(bucket.Review)})";
        }

        return line;
    }

    private static string FormatInteractionCards(IReadOnlyList<InteractionCard> cards)
        => FormatQuantityNameList(cards, card => card.Quantity, card => card.Name);

    // "none found" when empty, else a comma-joined "Nx Name" list (the "1x" prefix is dropped for
    // singletons). Shared by the interaction-audit and win-condition closing-card readouts.
    private static string FormatQuantityNameList<T>(IReadOnlyList<T> items, Func<T, int> quantity, Func<T, string> name)
    {
        if (items.Count == 0)
        {
            return "none found";
        }

        return string.Join(", ", items.Select(item => quantity(item) > 1 ? $"{quantity(item)}x {name(item)}" : name(item)));
    }

    /// <summary>
    /// Renders the win-condition/combo map as a paste-safe ASCII artifact block. Every combo and figure
    /// is hedged as a CANDIDATE win line the target AI must confirm against castability, board state, and
    /// color access - this text never asserts the deck actually wins via any listed line. Near-combos are
    /// always labeled "one card away (not currently a win line)" and rendered separately from the
    /// confirmed combo list, and closing cards render even when no combo data is available so a
    /// combo-less (or lookup-failed) deck still gets a win-condition read.
    /// </summary>
    /// <param name="map">The computed win-condition/combo map.</param>
    internal static string BuildWinConMapText(WinConMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var builder = new StringBuilder();
        builder.AppendLine("WIN CONDITION & COMBO MAP (DeckFlow heuristic first pass - the AI must confirm castability, board state, and color access before treating any line below as a live win condition)");

        if (!map.ComboDataAvailable)
        {
            builder.AppendLine("Combo data unavailable (Commander Spellbook did not respond) - this is not a claim the deck has no win conditions.");
        }
        else if (map.Combos.Count == 0)
        {
            builder.AppendLine("No combos detected by DeckFlow's combo lookup - this is not a claim the deck has no win conditions.");
        }
        else
        {
            builder.AppendLine("Candidate combos, ranked (verify each is actually castable and assemblable before treating it as live):");
            foreach (var combo in map.Combos)
            {
                builder.AppendLine($"  {FormatWinConComboLine(combo)}");
            }
            builder.AppendLine($"Approximately {map.AssemblyPathCount} candidate assembly paths (verify castability).");
        }

        builder.AppendLine($"Near-combos, one card away (not currently a win line): {FormatWinConNearCombos(map.NearCombos)}");

        if (map.OverallBand != WinConBand.Unknown)
        {
            builder.AppendLine($"Fastest candidate combo typically comes online {WinConBandFormatter.Label(map.OverallBand)} (heuristic estimate - verify against the actual game plan).");
        }

        builder.Append($"Non-combo closers to verify: {FormatWinConClosingCards(map.ClosingCards)}");
        return builder.ToString();
    }

    private static string FormatWinConComboLine(WinConCombo combo)
    {
        var cardNames = string.Join(", ", combo.CardNames);
        var results = string.Join(", ", combo.Results);
        var bandText = combo.Band == WinConBand.Unknown ? string.Empty : $" (typically comes online {WinConBandFormatter.Label(combo.Band)})";
        return $"{cardNames} -> {results}{bandText}";
    }

    private static string FormatWinConNearCombos(IReadOnlyList<WinConNearCombo> nearCombos)
    {
        if (nearCombos.Count == 0)
        {
            return "none found";
        }

        return string.Join("; ", nearCombos.Select(n => $"missing {n.MissingCard} (have: {string.Join(", ", n.CardsInDeck)})"));
    }

    private static string FormatWinConClosingCards(IReadOnlyList<WinConClosingCard> closingCards)
        => FormatQuantityNameList(closingCards, card => card.Quantity, card => card.Name);

    /// <summary>
    /// Deserializes the round-tripped <c>ScoreJson</c> hidden field (untrusted client input) into the
    /// typed <see cref="DeckMultiAxisScore"/>. Returns <see langword="null"/> on empty, malformed, or
    /// oversized input — never throws, never reflects/evals the client string (threat T-77-04-01).
    /// </summary>
    /// <param name="scoreJson">The serialized score from the hidden form field.</param>
    private static DeckMultiAxisScore? TryDeserializeScore(string? scoreJson)
    {
        if (string.IsNullOrWhiteSpace(scoreJson) || scoreJson.Length > MaxScoreJsonLength)
        {
            return null;
        }

        try
        {
            var score = JsonSerializer.Deserialize<DeckMultiAxisScore>(scoreJson);
            return IsStructurallyValidScore(score) ? score : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates a deserialized <see cref="DeckMultiAxisScore"/> from untrusted client input. Well-formed
    /// JSON can still omit the nested rationale records or carry out-of-range bands; the Razor view
    /// dereferences each rationale's <c>SignalText</c>, so an unchecked null would crash the request.
    /// Returns <see langword="false"/> for any null rationale/text/cross-check or any band outside 0..5,
    /// so <see cref="TryDeserializeScore"/> yields a null score instead (threat T-77-04-01).
    /// </summary>
    private static bool IsStructurallyValidScore(DeckMultiAxisScore? score)
        => score is not null
            && score.PowerRationale?.SignalText is not null
            && score.SpeedRationale?.SignalText is not null
            && score.ControlRationale?.SignalText is not null
            && score.ConsistencyRationale?.SignalText is not null
            && score.BracketCrossCheckText is not null
            && score.PowerBand is >= 0 and <= 5
            && score.SpeedBand is >= 0 and <= 5
            && score.ControlBand is >= 0 and <= 5
            && score.ConsistencyBand is >= 0 and <= 5;

    /// <summary>
    /// Upper bound (characters) on the round-tripped ScoreJson hidden field. The serialized score is a
    /// handful of ints plus four short ASCII rationale strings; this cap defeats an oversized-payload DoS
    /// from a crafted form post before deserialization runs (threat T-77-04-01).
    /// </summary>
    private const int MaxScoreJsonLength = 8192;

    /// <summary>
    /// Deserializes the round-tripped <c>InteractionAuditJson</c> hidden field (untrusted client input)
    /// into the typed <see cref="InteractionAudit"/>. Returns <see langword="null"/> on empty,
    /// malformed, oversized, or structurally invalid input — never throws, never reflects/evals.
    /// </summary>
    /// <param name="interactionAuditJson">The serialized interaction audit from the hidden form field or zip entry.</param>
    private static InteractionAudit? TryDeserializeInteractionAudit(string? interactionAuditJson)
    {
        if (string.IsNullOrWhiteSpace(interactionAuditJson) || interactionAuditJson.Length > MaxInteractionAuditJsonLength)
        {
            return null;
        }

        try
        {
            var audit = JsonSerializer.Deserialize<InteractionAudit>(interactionAuditJson);
            return IsStructurallyValidInteractionAudit(audit) ? audit : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates a deserialized <see cref="InteractionAudit"/> from untrusted client input before the
    /// Razor view dereferences bucket lists and card names. Well-formed JSON can still carry null
    /// buckets, null inner lists, blank names, out-of-range quantities, or blank coverage gaps.
    /// </summary>
    private static bool IsStructurallyValidInteractionAudit(InteractionAudit? audit)
        => audit is not null
            && IsStructurallyValidInteractionBucket(audit.TargetedRemoval)
            && IsStructurallyValidInteractionBucket(audit.BoardWipes)
            && IsStructurallyValidInteractionBucket(audit.Counterspells)
            && IsStructurallyValidInteractionBucket(audit.ProtectionRecursion)
            && IsStructurallyValidInteractionBucket(audit.StaxTaxation)
            && audit.CoverageGaps is not null
            && audit.CoverageGaps.All(gap => !string.IsNullOrWhiteSpace(gap));

    private static bool IsStructurallyValidInteractionBucket(InteractionBucketResult? bucket)
        => bucket is not null
            && bucket.Confident is not null
            && bucket.Review is not null
            && bucket.Confident.All(IsStructurallyValidInteractionCard)
            && bucket.Review.All(IsStructurallyValidInteractionCard);

    private static bool IsStructurallyValidInteractionCard(InteractionCard card)
        => !string.IsNullOrWhiteSpace(card.Name)
            && card.Quantity is >= 1 and <= 99;

    /// <summary>
    /// Upper bound (characters) on the round-tripped InteractionAuditJson hidden field. The audit is a
    /// small five-bucket payload; this cap rejects oversized client posts before deserialization.
    /// </summary>
    private const int MaxInteractionAuditJsonLength = 16384;

    /// <summary>
    /// Deserializes the round-tripped <c>WinConMapJson</c> hidden field (untrusted client input) into
    /// the typed <see cref="WinConMap"/>. Returns <see langword="null"/> on empty, malformed, oversized,
    /// or structurally invalid input — never throws, never reflects/evals (threat T-80-03-01).
    /// </summary>
    /// <param name="winConMapJson">The serialized win-condition map from the hidden form field or zip entry.</param>
    private static WinConMap? TryDeserializeWinConMap(string? winConMapJson)
    {
        if (string.IsNullOrWhiteSpace(winConMapJson) || winConMapJson.Length > MaxWinConMapJsonLength)
        {
            return null;
        }

        try
        {
            var map = JsonSerializer.Deserialize<WinConMap>(winConMapJson);
            return IsStructurallyValidWinConMap(map) ? map : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates a deserialized <see cref="WinConMap"/> from untrusted client input before the Razor
    /// view dereferences combo/near-combo/closing-card lists and card names. Well-formed JSON can still
    /// carry null lists, blank names/results, out-of-range numeric fields, undefined enum values, a
    /// tampered assembly-path count, or oversized lists that would render dozens of degenerate rows.
    /// </summary>
    private static bool IsStructurallyValidWinConMap(WinConMap? map)
        => map is not null
            && map.Combos is not null
            && map.Combos.Count <= MaxWinConMapComboCount
            && map.Combos.All(IsStructurallyValidWinConCombo)
            && map.NearCombos is not null
            && map.NearCombos.Count <= MaxWinConMapNearComboCount
            && map.NearCombos.All(IsStructurallyValidWinConNearCombo)
            && map.ClosingCards is not null
            && map.ClosingCards.Count <= MaxWinConMapClosingCardCount
            && map.ClosingCards.All(IsStructurallyValidWinConClosingCard)
            && map.AssemblyPathCount == map.Combos.Count
            && Enum.IsDefined(typeof(WinConBand), map.OverallBand);

    private static bool IsStructurallyValidWinConCombo(WinConCombo combo)
        => combo.CardNames is not null
            && combo.CardNames.Count >= 1
            && combo.CardNames.Count <= MaxWinConMapListEntryCount
            && combo.CardNames.All(name => !string.IsNullOrWhiteSpace(name))
            && combo.Results is not null
            && combo.Results.Count <= MaxWinConMapListEntryCount
            && combo.Results.All(result => !string.IsNullOrWhiteSpace(result))
            && combo.ManaValueNeeded is null or >= 0
            && combo.Popularity is null or >= 0
            && Enum.IsDefined(typeof(WinConBand), combo.Band);

    private static bool IsStructurallyValidWinConNearCombo(WinConNearCombo nearCombo)
        => !string.IsNullOrWhiteSpace(nearCombo.MissingCard)
            && nearCombo.CardsInDeck is not null
            && nearCombo.CardsInDeck.Count <= MaxWinConMapListEntryCount
            && nearCombo.CardsInDeck.All(name => !string.IsNullOrWhiteSpace(name))
            && nearCombo.Results is not null
            && nearCombo.Results.Count <= MaxWinConMapListEntryCount
            && nearCombo.Results.All(result => !string.IsNullOrWhiteSpace(result));

    private static bool IsStructurallyValidWinConClosingCard(WinConClosingCard closingCard)
        => !string.IsNullOrWhiteSpace(closingCard.Name)
            && closingCard.Quantity is >= 1 and <= 99;

    /// <summary>
    /// Upper bound (characters) on the round-tripped WinConMapJson hidden field. The map is a
    /// small combos/near-combos/closing-cards payload; this cap rejects oversized client posts
    /// before deserialization runs.
    /// </summary>
    private const int MaxWinConMapJsonLength = 32768;

    /// <summary>Per-list count caps well under <see cref="MaxWinConMapJsonLength"/> so degenerate
    /// hidden-field JSON cannot render dozens of empty/low-value rows in the Step-3 readout.</summary>
    private const int MaxWinConMapComboCount = 50;
    private const int MaxWinConMapNearComboCount = 50;
    private const int MaxWinConMapClosingCardCount = 100;
    private const int MaxWinConMapListEntryCount = 20;

    /// <summary>
    /// Builds the optional set-upgrade prompt used after the deck profile has been generated.
    /// Internal for test access — per-AI dispatcher exercised by the AI result contract tests.
    /// </summary>
    // Phase 15-02: converted from internal static to instance method; dispatches via injected SetUpgradePromptVariantRegistry.
    internal string BuildSetUpgradePrompt(DeckAnalysisRequest request, string decklistText, string deckProfileJson, string? commanderName, string? generatedSetPacket, IReadOnlyList<string> bannedCards)
    {
        return _setUpgradePromptRegistry.Build(
            AiPlatform.Normalize(request.TargetAiPlatform),
            request, decklistText, deckProfileJson, commanderName,
            generatedSetPacket, bannedCards);
    }


    private static readonly IReadOnlyDictionary<string, string> EmptyCardText
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a card-name → exact rules-text map from the generated (or override) set packet so the
    /// set-upgrade results page can show what each suggested card does using authoritative Scryfall
    /// text. Card-text display is non-essential, so any failure (no set selected, multiple sets, or a
    /// Scryfall outage) degrades silently to an empty map, leaving the AI-echoed card_text in place.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> BuildSetUpgradeCardTextAsync(DeckAnalysisRequest request, string? prebuiltGeneratedPacket, CancellationToken cancellationToken)
    {
        try
        {
            // Mirror the set-upgrade prompt variants' packet resolution — a pasted override always
            // wins over the generated packet — so the displayed rules text matches the packet the AI
            // actually analyzed (and skips a needless Scryfall fetch when an override is present).
            // When the caller already generated the packet this build, reuse it rather than re-fetching.
            var packet = !string.IsNullOrWhiteSpace(request.SetPacketText)
                ? request.SetPacketText
                : prebuiltGeneratedPacket ?? await BuildGeneratedSetPacketAsync(request, cancellationToken).ConfigureAwait(false);
            return ParseSetPacketCardText(packet);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Why: the suggested-card text is supplementary; never let a packet/Scryfall failure
            // break Step 5 rendering. The view falls back to the AI-supplied card_text.
            _logger.LogInformation(exception, "Set-upgrade card-text packet build failed; falling back to AI-supplied card text.");
            return EmptyCardText;
        }
    }

    /// <summary>
    /// Parses the compact set-packet text into a card-name → rules-text map. Packet card lines use the
    /// pipe-delimited shape "Name | ManaCost | TypeLine | OracleText"; only the trailing oracle-text
    /// segment is captured. Lines without at least three pipes (headers, mechanics, notes) are ignored.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ParseSetPacketCardText(string? setPacketText)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(setPacketText))
        {
            return map;
        }

        foreach (var rawLine in setPacketText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var parts = rawLine.Split('|');
            if (parts.Length < 4)
            {
                continue;
            }

            var name = parts[0].Trim();
            var oracleText = string.Join("|", parts.Skip(3)).Trim();
            if (name.Length == 0 || oracleText.Length == 0)
            {
                continue;
            }

            // First occurrence wins; packets list each card once, but guard against duplicates defensively.
            map.TryAdd(name, oracleText);
        }

        return map;
    }

    /// <summary>
    /// Builds a condensed set packet from Scryfall for the selected set codes.
    /// </summary>
    private async Task<string?> BuildGeneratedSetPacketAsync(DeckAnalysisRequest request, CancellationToken cancellationToken)
    {
        if (request.SelectedSetCodes.Count == 0)
        {
            return string.IsNullOrWhiteSpace(request.SetPacketText) ? null : request.SetPacketText.Trim();
        }

        if (request.SelectedSetCodes.Count > 1 && string.IsNullOrWhiteSpace(request.SetPacketText))
        {
            throw new InvalidOperationException("Choose only one set or paste a condensed set packet override before generating the set-upgrade packet.");
        }

        var commanderColorIdentity = await LookupCommanderColorIdentityAsync(request.DeckSource, cancellationToken).ConfigureAwait(false);
        var generatedPacket = await _scryfallSetService
            .BuildSetPacketAsync([request.SelectedSetCodes[0]], commanderColorIdentity, cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(generatedPacket) ? null : generatedPacket;
    }

    /// <summary>
    /// Looks up the commander's color identity so generated set packets can filter to legal cards.
    /// </summary>
    private async Task<IReadOnlyList<string>> LookupCommanderColorIdentityAsync(string deckSource, CancellationToken cancellationToken)
    {
        var loaded = await _deckEntryLoader.LoadFromSourceAsync(deckSource, cancellationToken: cancellationToken).ConfigureAwait(false);
        _lastImportNotice = loaded.FallbackNotice;
        var entries = loaded.Entries;
        var commanderName = entries
            .FirstOrDefault(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            ?.Name;
        if (string.IsNullOrWhiteSpace(commanderName))
        {
            return Array.Empty<string>();
        }

        var response = await _collectionProtocol.ResolveAsync(
            new ScryfallCollectionProtocolRequest([ScryfallCollectionNameIdentifier.ForName(CoreScryfallCollectionIdentifier.ToFaceIdentifier(commanderName.Trim()))]),
            cancellationToken).ConfigureAwait(false);
        var card = response.Cards.FirstOrDefault();
        if (card?.ColorIdentity is null)
        {
            return Array.Empty<string>();
        }

        return card.ColorIdentity
            .Where(color => !string.IsNullOrWhiteSpace(color))
            .Select(color => color.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the deck-profile schema that the prompt should follow during analysis.
    /// </summary>
    private static string BuildDeckProfileSchemaJson(string? commanderName, string format, bool includeFullDecklists = false)
    {
        var payload = new Dictionary<string, object>
        {
            ["format"] = NormalizeSingleLine(format, "Commander"),
            ["commander"] = commanderName ?? string.Empty,
            ["game_plan"] = string.Empty,
            ["primary_axes"] = Array.Empty<string>(),
            ["speed"] = string.Empty,
            ["estimated_win_turn"] = 0,
            ["can_answer_win_turn"] = false,
            ["assessed_bracket"] = string.Empty,
            ["bracket_justification"] = string.Empty,
            ["strengths"] = Array.Empty<string>(),
            ["weaknesses"] = Array.Empty<string>(),
            ["deck_needs"] = Array.Empty<string>(),
            ["weak_slots"] = new[]
            {
                new
                {
                    card = string.Empty,
                    reason = string.Empty
                }
            },
            ["synergy_tags"] = Array.Empty<string>(),
            ["question_answers"] = new[]
            {
                new
                {
                    question_number = 1,
                    question = string.Empty,
                    answer = string.Empty,
                    basis = "authoritative|inference|mixed"
                }
            }
        };

        if (includeFullDecklists)
        {
            payload["deck_versions"] = new[]
            {
                new
                {
                    version_name = string.Empty,
                    decklist = "complete 100-card decklist, one card per line, same format as the text code blocks",
                    cards_added = Array.Empty<string>(),
                    cards_cut = Array.Empty<string>()
                }
            };
        }

        return JsonSerializer.Serialize(payload, IndentedJsonSerializerOptions);
    }

    private async Task<CardReferenceBundle> LookupCardReferencesAsync(IReadOnlyList<CardReferenceRequest> cardRequests, CancellationToken cancellationToken)
    {
        if (cardRequests.Count == 0)
        {
            return new CardReferenceBundle(Array.Empty<CardReference>(), Array.Empty<string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        // Cluster A delegation: the shared resolver owns chunk(75) -> cards/collection -> validate ->
        // match-back-by-original-name -> per-miss-fallback. Analysis keeps its OWN fallback strategy
        // (SearchPrintingFallbackCardAsync, the richer all-printings search) and its OWN
        // NormalizeForScryfall pre-submission normalization (normalizeForScryfall: true) — neither
        // choice is hardcoded in the collaborator.
        var requestByName = new Dictionary<string, CardReferenceRequest>(StringComparer.OrdinalIgnoreCase);
        foreach (var cardRequest in cardRequests)
        {
            requestByName[cardRequest.Name] = cardRequest;
        }

        ScryfallBatchResolution batchResolution;
        try
        {
            batchResolution = await _scryfallReferenceResolver.ResolveBatchAsync(
                cardRequests.Select(card => card.Name).ToList(),
                _scryfallCardResolver.SearchPrintingFallbackCardAsync,
                normalizeForScryfall: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ScryfallReferenceCollectionException exception)
        {
            // Preserve Analysis's own cards/collection-CALL message text verbatim. Catching the narrow
            // ScryfallReferenceCollectionException (not a plain HttpRequestException) is load-bearing:
            // a per-name printing-fallback failure must propagate with its ORIGINAL upstream message so
            // UpstreamErrorMessageBuilder produces the generic "Scryfall returned HTTP {n}" copy it did
            // pre-Phase-83 — re-labeling it here would flip which BuildDetailedScryfallMessage branch
            // fires (WR-01).
            throw new HttpRequestException(
                $"Scryfall card reference lookup (cards/collection) returned HTTP {(int)(exception.StatusCode ?? 0)} while building the analysis packet.",
                null,
                exception.StatusCode);
        }

        var mechanicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedCards = new List<CardReference>();
        foreach (var resolution in batchResolution.Resolutions)
        {
            var matchingRequest = requestByName[resolution.RequestName];
            var card = resolution.Card;

            // Analysis-only: fallback-resolved cards get an annotated display name when the resolved
            // card's own name doesn't match the submitted name under lookup-normalization; a direct
            // collection hit always uses the card's own Name verbatim.
            var name = resolution.FromFallback
                ? (ScryfallCardResolver.NormalizeLookupName(resolution.RequestName) == ScryfallCardResolver.NormalizeLookupName(card.Name)
                    ? card.Name
                    : $"submitted_name: {resolution.RequestName} | resolved_card: {card.Name}")
                : card.Name;

            resolvedCards.Add(new CardReference(
                matchingRequest.Scope,
                name,
                card.ManaCost ?? string.Empty,
                card.TypeLine,
                NormalizeOracleText(card),
                IsModalDfcLand(card),
                card.ReleasedAt,
                matchingRequest.Quantity,
                matchingRequest.IsCommander));

            foreach (var mechanicName in ExtractMechanicNames(card))
            {
                mechanicNames.Add(mechanicName);
            }
        }

        return new CardReferenceBundle(
            resolvedCards,
            mechanicNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
            batchResolution.OracleNameMap);
    }

    private async Task<string> ValidateCommanderAsync(IReadOnlyList<DeckEntry> entries, string? commanderName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commanderName))
        {
            throw new InvalidOperationException("The commander isn't in the deck text. Add a legal commander line before generating the analysis packet.");
        }

        var commanderEntry = entries.FirstOrDefault(entry => string.Equals(entry.Name, commanderName, StringComparison.OrdinalIgnoreCase));
        if (commanderEntry is null)
        {
            throw new InvalidOperationException("The commander isn't in the deck text. Add a legal commander line before generating the analysis packet.");
        }

        var commanderCard = await _scryfallCardResolver.SearchPrintingFallbackCardAsync(commanderName, cancellationToken).ConfigureAwait(false);
        if (commanderCard is null || !CommanderEligibility.IsEligible(commanderCard.TypeLine ?? string.Empty, NormalizeOracleText(commanderCard)))
        {
            throw new InvalidOperationException($"The commander isn't in the deck text. \"{commanderName}\" is not a legal commander by this workflow's rules.");
        }

        return commanderEntry.Name;
    }

    private async Task<IReadOnlyList<MechanicReference>> LookupMechanicReferencesAsync(IReadOnlyList<string> mechanicNames, CancellationToken cancellationToken)
    {
        var tasks = mechanicNames
            .Select(async mechanicName =>
            {
                var result = await _mechanicLookupService.LookupAsync(mechanicName, cancellationToken).ConfigureAwait(false);
                var description = result.SummaryText ?? result.RulesText ?? "No official rules text found.";
                return new MechanicReference(
                    mechanicName,
                    CollapseWhitespace(description),
                    result.RuleReference);
            })
            .ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string NormalizeOracleText(ScryfallCard card)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(card.OracleText))
        {
            parts.Add(CollapseWhitespace(card.OracleText));
        }

        foreach (var face in card.CardFaces ?? Array.Empty<ScryfallCardFace>())
        {
            var faceParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(face.Name))
            {
                faceParts.Add(face.Name.Trim());
            }

            if (!string.IsNullOrWhiteSpace(face.ManaCost))
            {
                faceParts.Add(face.ManaCost.Trim());
            }

            if (!string.IsNullOrWhiteSpace(face.TypeLine))
            {
                faceParts.Add(CollapseWhitespace(face.TypeLine));
            }

            if (!string.IsNullOrWhiteSpace(face.OracleText))
            {
                faceParts.Add(CollapseWhitespace(face.OracleText));
            }

            if (!string.IsNullOrWhiteSpace(face.Power) && !string.IsNullOrWhiteSpace(face.Toughness))
            {
                faceParts.Add($"{face.Power}/{face.Toughness}");
            }

            if (faceParts.Count > 0)
            {
                parts.Add(string.Join(" | ", faceParts));
            }
        }

        if (!string.IsNullOrWhiteSpace(card.Power) && !string.IsNullOrWhiteSpace(card.Toughness))
        {
            parts.Add($"{card.Power}/{card.Toughness}");
        }

        return string.Join(" ", parts);
    }

    private static string CollapseWhitespace(string value)
    {
        return string.Join(" ", (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    internal static string NormalizeSingleLine(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : CollapseWhitespace(value);

    // Phase 73-02: companion resolution with designator priority. The designator (free-form request
    // field) wins over the auto-detected name; both flow through BoundCompanionName.
    private static string? ResolveCompanionName(string? designator, string? detected)
        => BoundCompanionName(designator) ?? BoundCompanionName(detected);

    // Phase 73-02 (Codex HIGH-2): bound a companion name before it reaches any prompt. Collapses the
    // value to a SINGLE LINE so a crafted newline cannot break prompt structure, trims, then caps at
    // MaxCompanionNameLength. Returns null for null/whitespace input.
    // Codex 73 MEDIUM: the shared CollapseWhitespace only splits on \n (and \r\n); a lone CR would
    // survive into the rendered companion line. Normalize any bare \r to \n on the companion path FIRST
    // so every CR/LF form is collapsed. The shared helper is intentionally left untouched to keep the
    // flag-OFF byte-identity contract for all other prompt text.
    private static string? BoundCompanionName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var singleLine = CollapseWhitespace(name.Replace('\r', '\n')).Trim();
        if (singleLine.Length == 0)
        {
            return null;
        }

        return singleLine.Length <= MaxCompanionNameLength
            ? singleLine
            : singleLine[..MaxCompanionNameLength];
    }

    private static string ExtractJsonObject(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                trimmed = trimmed[..closingFence];
            }
        }

        return trimmed.Trim();
    }

    internal static string BuildRequestContextText(DeckAnalysisRequest request, string? commanderName)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"workflow_step: {request.WorkflowStep}");
        PacketTextAssembler.AppendKeyValueLine(builder, "format", request.Format, "Commander", NormalizeSingleLine);
        PacketTextAssembler.AppendKeyValueLine(builder, "deck_name", request.DeckName, string.Empty, NormalizeSingleLine);
        PacketTextAssembler.AppendKeyValueLine(builder, "commander", commanderName, string.Empty, NormalizeSingleLine);
        PacketTextAssembler.AppendKeyValueLine(builder, "target_commander_bracket", request.TargetCommanderBracket, string.Empty, NormalizeSingleLine);
        PacketTextAssembler.AppendKeyValueLine(builder, "target_ai_platform", request.TargetAiPlatform, "ChatGPT", NormalizeSingleLine);
        builder.AppendLine($"include_candidate_references_in_analysis: {request.IncludeCandidateReferencesInAnalysis}");
        builder.AppendLine("card_specific_question_card_names:");
        foreach (var cardName in request.CardSpecificQuestionCardNames)
        {
            builder.AppendLine($"- {NormalizeSingleLine(cardName, string.Empty)}");
        }
        PacketTextAssembler.AppendKeyValueLine(builder, "budget_upgrade_amount", request.BudgetUpgradeAmount, string.Empty, NormalizeSingleLine);
        builder.AppendLine("selected_analysis_questions:");
        foreach (var questionId in AnalysisQuestionCatalog.NormalizeSelections(request.SelectedAnalysisQuestions))
        {
            builder.AppendLine($"- {questionId}");
        }

        builder.AppendLine("selected_set_codes:");
        foreach (var setCode in request.SelectedSetCodes.Where(setCode => !string.IsNullOrWhiteSpace(setCode)))
        {
            builder.AppendLine($"- {setCode.Trim()}");
        }

        AppendOptionalContextBlock(builder, "strategy_notes", request.StrategyNotes);
        AppendOptionalContextBlock(builder, "meta_notes", request.MetaNotes);
        AppendOptionalContextBlock(builder, "deck_source", request.DeckSource);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendOptionalContextBlock(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"{label}:");
        builder.AppendLine(value.Trim());
    }

    internal static string FormatBannedCardsLine(IReadOnlyList<string> bannedCards)
        => bannedCards.Count == 0 ? "(unavailable)" : string.Join(", ", bannedCards);

    /// <summary>
    /// Parses a newline- or comma-separated list of card names into a deduplicated, trimmed list.
    /// </summary>
    internal static IReadOnlyList<string> ParseCardNameList(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        return input
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ExtractMechanicNames(ScryfallCard card)
    {
        foreach (var keyword in card.Keywords ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                yield return keyword.Trim();
            }
        }

        foreach (var oracleText in EnumerateOracleText(card))
        {
            foreach (var line in oracleText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine))
                {
                    continue;
                }

                var abilityWordMatch = AbilityWordRegex.Match(trimmedLine);
                if (abilityWordMatch.Success)
                {
                    yield return abilityWordMatch.Groups["term"].Value.Trim();
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateOracleText(ScryfallCard card)
    {
        if (!string.IsNullOrWhiteSpace(card.OracleText))
        {
            yield return card.OracleText;
        }

        foreach (var face in card.CardFaces ?? Array.Empty<ScryfallCardFace>())
        {
            if (!string.IsNullOrWhiteSpace(face.OracleText))
            {
                yield return face.OracleText;
            }
        }
    }

    private static string BuildTimingSummary(List<(string Label, long Ms, string? Detail)> timings, long totalMs)
    {
        var sb = new StringBuilder();
        foreach (var (label, ms, detail) in timings)
        {
            sb.Append($"{label}: {ms:N0}ms");
            if (!string.IsNullOrWhiteSpace(detail))
            {
                sb.Append($" ({detail})");
            }

            sb.AppendLine();
        }

        sb.Append($"Total: {totalMs:N0}ms");
        return sb.ToString();
    }

    [GeneratedRegex(@"^(?<term>[A-Za-z][A-Za-z' -]{1,40})\s+—\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AbilityWordPattern();


    private sealed record CardReferenceRequest(string Name, string Scope, int Quantity = 1, bool IsCommander = false);
    private sealed record CardReference(string Scope, string Name, string ManaCost, string TypeLine, string OracleText, bool IsMdfcLand, string? ReleasedAt = null, int Quantity = 1, bool IsCommander = false);

    private sealed record CardReferenceBundle(IReadOnlyList<CardReference> CardReferences, IReadOnlyList<string> MechanicNames, IReadOnlyDictionary<string, string> OracleNameMap);

    private sealed record MechanicReference(string Name, string Description, string? RuleReference);
}
