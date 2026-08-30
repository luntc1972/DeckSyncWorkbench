using DeckFlow.Core.Content;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeckFlow.Web.Services.CreatorStyle;

/// <summary>
/// Carries the accepted whitelist names plus the upstream-failure diagnostic from the validation batch.
/// </summary>
public sealed record CreatorWhitelistPoolBuildResult
{
    /// <summary>
    /// Gets the accepted canonical whitelist names in ranked order.
    /// </summary>
    public required IReadOnlyList<string> AcceptedNames { get; init; }

    /// <summary>
    /// Gets a value indicating whether the grounding batch experienced any upstream failures.
    /// </summary>
    public required bool HasUpstreamFailure { get; init; }
}

/// <summary>
/// Builds a constrained creator-whitelist candidate pool from the creator's cached deck corpus.
/// </summary>
public sealed class CreatorWhitelistPoolBuilder
{
    private const string CacheKeyPrefix = "creator-whitelist-pool:";
    private static readonly TimeSpan RawPoolCacheTtl = TimeSpan.FromHours(1);
    private const int WhitelistCap = 25; // Why: 25 keeps the whitelist materially useful while bounding grounding latency and packet size even when lift-metric count changes independently.

    private readonly ICreatorDeckCacheStore _creatorDeckCacheStore;
    private readonly ICardGroundingGuard _cardGroundingGuard;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CreatorWhitelistPoolBuilder> _logger;

    /// <summary>
    /// Creates a creator-whitelist pool builder.
    /// </summary>
    /// <param name="creatorDeckCacheStore">Creator deck corpus store.</param>
    /// <param name="cardGroundingGuard">Strict guard used to validate ranked candidates.</param>
    /// <param name="cache">In-memory cache for raw per-creator candidate pools.</param>
    /// <param name="logger">Optional logger.</param>
    public CreatorWhitelistPoolBuilder(
        ICreatorDeckCacheStore creatorDeckCacheStore,
        ICardGroundingGuard cardGroundingGuard,
        IMemoryCache cache,
        ILogger<CreatorWhitelistPoolBuilder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(creatorDeckCacheStore);
        ArgumentNullException.ThrowIfNull(cardGroundingGuard);
        ArgumentNullException.ThrowIfNull(cache);

        _creatorDeckCacheStore = creatorDeckCacheStore;
        _cardGroundingGuard = cardGroundingGuard;
        _cache = cache;
        _logger = logger ?? NullLogger<CreatorWhitelistPoolBuilder>.Instance;
    }

    /// <summary>
    /// Builds a guard-validated creator whitelist and returns the upstream-failure diagnostic from the validation batch.
    /// </summary>
    /// <param name="creatorSlug">Creator slug.</param>
    /// <param name="deckContext">Deck-context inputs used by the guard.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted canonical card names plus upstream-failure diagnostics.</returns>
    public async Task<CreatorWhitelistPoolBuildResult> BuildWithDiagnosticsAsync(
        string creatorSlug,
        CardGroundingDeckContext deckContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(creatorSlug);
        ArgumentNullException.ThrowIfNull(deckContext);

        IReadOnlyList<string> rawPool = await GetOrBuildRawPoolAsync(creatorSlug, cancellationToken).ConfigureAwait(false);
        if (rawPool.Count == 0)
        {
            return new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = [],
                HasUpstreamFailure = false,
            };
        }

        CardGroundingBatchResult validation = await _cardGroundingGuard
            .ValidateAllAsync(rawPool, deckContext, cancellationToken)
            .ConfigureAwait(false);

        if (validation.HasUpstreamFailure)
        {
            _logger.LogWarning(
                "Creator whitelist validation saw upstream failures for creator {CreatorSlug}; returning accepted subset only.",
                creatorSlug);
        }

        return new CreatorWhitelistPoolBuildResult
        {
            AcceptedNames = validation.Verdicts
                .Where(verdict => verdict.Accepted)
                .Select(verdict => verdict.CanonicalName)
                .ToArray(),
            HasUpstreamFailure = validation.HasUpstreamFailure,
        };
    }

    private Task<IReadOnlyList<string>> GetOrBuildRawPoolAsync(string creatorSlug, CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(creatorSlug);
        return _cache.GetOrCreateAsync(
            cacheKey,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = RawPoolCacheTtl;
                return await BuildRawPoolAsync(creatorSlug, cancellationToken).ConfigureAwait(false);
            })!;
    }

    private async Task<IReadOnlyList<string>> BuildRawPoolAsync(string creatorSlug, CancellationToken cancellationToken)
    {
        IReadOnlyList<CreatorDeckCacheEntry> cachedDecks = await _creatorDeckCacheStore
            .GetByCreatorAsync(creatorSlug, cancellationToken)
            .ConfigureAwait(false);

        var frequencyByName = new Dictionary<string, RankedCandidate>(StringComparer.Ordinal);
        foreach (CreatorDeckCacheEntry cachedDeck in cachedDecks)
        {
            foreach (var candidate in DistinctMainboardCandidates(cachedDeck.Entries))
            {
                if (frequencyByName.TryGetValue(candidate.NormalizedName, out RankedCandidate? existing))
                {
                    frequencyByName[candidate.NormalizedName] = existing with
                    {
                        DisplayName = SelectPreferredDisplayName(existing.DisplayName, candidate.DisplayName),
                        DistinctDeckCount = existing.DistinctDeckCount + 1,
                    };
                }
                else
                {
                    frequencyByName[candidate.NormalizedName] = new RankedCandidate
                    {
                        NormalizedName = candidate.NormalizedName,
                        DisplayName = candidate.DisplayName,
                        DistinctDeckCount = 1,
                    };
                }
            }
        }

        return frequencyByName.Values
            .OrderByDescending(candidate => candidate.DistinctDeckCount)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .Take(WhitelistCap)
            .Select(candidate => candidate.DisplayName)
            .ToArray();
    }

    private static IEnumerable<RawCandidate> DistinctMainboardCandidates(IReadOnlyList<DeckEntry> entries)
    {
        var distinctCandidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DeckEntry entry in entries)
        {
            if (!string.Equals(entry.Board, "mainboard", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalizedName = string.IsNullOrWhiteSpace(entry.NormalizedName)
                ? CardNormalizer.Normalize(entry.Name)
                : entry.NormalizedName.Trim();
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                continue;
            }

            var displayName = entry.Name.Trim();
            if (distinctCandidates.TryGetValue(normalizedName, out string? existingDisplayName))
            {
                distinctCandidates[normalizedName] = SelectPreferredDisplayName(existingDisplayName, displayName);
            }
            else
            {
                distinctCandidates[normalizedName] = displayName;
            }
        }

        return distinctCandidates.Select(pair => new RawCandidate
        {
            NormalizedName = pair.Key,
            DisplayName = pair.Value,
        });
    }

    private static string SelectPreferredDisplayName(string left, string right)
        => string.Compare(left, right, StringComparison.Ordinal) <= 0 ? left : right;

    private static string BuildCacheKey(string creatorSlug)
        => CacheKeyPrefix + creatorSlug.Trim().ToLowerInvariant();

    private sealed record RawCandidate
    {
        public required string NormalizedName { get; init; }

        public required string DisplayName { get; init; }
    }

    private sealed record RankedCandidate
    {
        public required string NormalizedName { get; init; }

        public required string DisplayName { get; init; }

        public required int DistinctDeckCount { get; init; }
    }
}
