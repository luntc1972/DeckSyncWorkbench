using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.DeckModules;

namespace DeckFlow.Web.Services;

/// <summary>
/// Dedicated packet-result session cache fronting <see cref="DeckAnalysisPacketService.BuildAsync(DeckAnalysisRequest, CancellationToken)"/>,
/// <see cref="DeckComparisonService.BuildAsync(DeckComparisonRequest, CancellationToken)"/>, and
/// <see cref="MetaGapService.BuildAsync(MetaGapRequest, CancellationToken)"/>. Owns a private <see cref="MemoryCache"/>
/// with <c>SizeLimit=10_000_000</c> (~10 MB) so packet results never share LRU pressure with the shared
/// <c>IMemoryCache</c> singleton. Five-minute absolute TTL aligns with the ScryfallTaggerHttpClient.SetHandlerLifetime
/// invariant. Entries are wrapped in a <c>CachedEntry&lt;TResult&gt;(Result, SizeBytes)</c> envelope so the stored size
/// is recoverable on hit/eviction for accurate D-13 logging. The cache exposes <see cref="ComputeKey(object)"/> as
/// the low-level SHA-256 hashing primitive; per-service field-bag composition lives in each service's
/// <c>TryComputeCacheKeyAsync</c> helper, not here.
/// </summary>
public sealed class PacketSessionCache
{
    private static readonly JsonSerializerOptions DeterministicJsonOptions = new()
    {
        WriteIndented = false
    };

    private const int CacheCapacityBytes = 10_000_000; // D-06: dedicated packet cache capped at ~10 MB.
    private const int KeyPrefixLength = 8; // D-13: log only the first 8 chars of the SHA-256 hex key.
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(5); // D-05: preview/download reuse window.

    private readonly IMemoryCache _cache;
    private readonly ILogger<PacketSessionCache> _logger;

    private sealed record CachedEntry<TResult>(TResult Result, int SizeBytes) where TResult : class;

    /// <summary>
    /// Initialises a new <see cref="PacketSessionCache"/> with an optional logger.
    /// </summary>
    /// <param name="logger">Optional structured logger; falls back to <see cref="NullLogger{T}"/> when not provided.</param>
    public PacketSessionCache(ILogger<PacketSessionCache>? logger = null)
    {
        _logger = logger ?? NullLogger<PacketSessionCache>.Instance;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = CacheCapacityBytes });
    }

    /// <summary>
    /// Computes the canonical SHA-256 cache key for a caller-supplied field bag.
    /// </summary>
    /// <param name="fieldBag">The already-normalized, service-specific field bag to hash.</param>
    /// <returns>A lowercase 64-character SHA-256 hex string.</returns>
    public static string ComputeKey(object fieldBag)
    {
        ArgumentNullException.ThrowIfNull(fieldBag);

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(fieldBag, DeterministicJsonOptions);
        var hashBytes = SHA256.HashData(jsonBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>Returns the short log-safe prefix for a cache key.</summary>
    /// <param name="key">Full cache key.</param>
    /// <returns>The first eight characters, or the whole key when shorter.</returns>
    public static string GetKeyPrefix(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Length <= KeyPrefixLength ? key : key[..KeyPrefixLength];
    }

    /// <summary>
    /// Attempts to retrieve a previously cached packet result.
    /// </summary>
    /// <typeparam name="TResult">The result reference type expected by the caller.</typeparam>
    /// <param name="key">The 64-character packet cache key.</param>
    /// <param name="result">The cached packet result when the lookup succeeds; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> on a typed cache hit; otherwise <see langword="false"/>.</returns>
    public bool TryGet<TResult>(string key, out TResult? result) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!_cache.TryGetValue(key, out var raw) || raw is not CachedEntry<TResult> entry)
        {
            result = null;
            LogCacheEvent("miss", key, 0);
            return false;
        }

        result = entry.Result;
        LogCacheEvent("hit", key, entry.SizeBytes);
        return true;
    }

    /// <summary>
    /// Stores a packet result with a five-minute absolute TTL and caller-supplied size estimate.
    /// </summary>
    /// <typeparam name="TResult">The result reference type being cached.</typeparam>
    /// <param name="key">The 64-character packet cache key.</param>
    /// <param name="result">The packet result to cache.</param>
    /// <param name="sizeBytes">The estimated payload size in bytes used for <see cref="MemoryCache"/> capacity accounting.</param>
    public void Set<TResult>(string key, TResult result, int sizeBytes) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(result);

        var entry = new CachedEntry<TResult>(result, sizeBytes);
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = EntryTtl,
            Size = sizeBytes,
        };

        options.RegisterPostEvictionCallback((evictedKey, evictedValue, _, _) =>
        {
            var evictedSize = (evictedValue as CachedEntry<TResult>)?.SizeBytes ?? 0;
            _logger.LogInformation(
                "Packet cache {Outcome} for {KeyPrefix} ({SizeBytes} bytes)",
                "evicted",
                GetKeyPrefix((string)evictedKey),
                evictedSize);
        });

        _cache.Set(key, entry, options);
        LogCacheEvent("write", key, sizeBytes);
    }

    private void LogCacheEvent(string outcome, string key, int sizeBytes)
    {
        _logger.LogInformation(
            "Packet cache {Outcome} for {KeyPrefix} ({SizeBytes} bytes)",
            outcome,
            GetKeyPrefix(key),
            sizeBytes);
    }
}

/// <summary>
/// D-07 size estimators: sum every string-payload property length per result type.
/// Co-located with <see cref="PacketSessionCache"/> so all size-bookkeeping logic lives in one place
/// and downstream services call these helpers rather than inlining property sums.
/// </summary>
internal static class PacketSizeEstimator
{
    public static int EstimateSizeBytes(DeckAnalysisPacketResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (result.InputSummary?.Length ?? 0)
            + (result.SuggestedChatTitle?.Length ?? 0)
            + (result.DeckProfileSchemaJson?.Length ?? 0)
            + (result.ReferenceText?.Length ?? 0)
            + (result.AnalysisPromptText?.Length ?? 0)
            + (result.SetUpgradePromptText?.Length ?? 0)
            + (result.RequestContextText?.Length ?? 0)
            + (result.TimingSummary?.Length ?? 0)
            + (result.ImportWarning?.Length ?? 0)
            + (result.ResolvedCommanderName?.Length ?? 0)
            + (result.DecklistText?.Length ?? 0);
    }

    public static int EstimateSizeBytes(DeckComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (result.InputSummary?.Length ?? 0)
            + (result.DeckAListText?.Length ?? 0)
            + (result.DeckBListText?.Length ?? 0)
            + (result.DeckAComboText?.Length ?? 0)
            + (result.DeckBComboText?.Length ?? 0)
            + (result.ComparisonContextText?.Length ?? 0)
            + (result.ComparisonPromptText?.Length ?? 0)
            + (result.FollowUpPromptText?.Length ?? 0)
            + (result.ComparisonSchemaJson?.Length ?? 0)
            + (result.TimingSummary?.Length ?? 0)
            + (result.ResolvedDeckACommander?.Length ?? 0)
            + (result.ResolvedDeckBCommander?.Length ?? 0)
            + (result.RequestContextText?.Length ?? 0);
    }

    public static int EstimateSizeBytes(MetaGapResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (result.InputSummary?.Length ?? 0)
            + (result.ResolvedCommanderName?.Length ?? 0)
            + (result.PromptText?.Length ?? 0)
            + (result.SchemaJson?.Length ?? 0)
            + (result.RequestContextText?.Length ?? 0)
            + (result.DecklistText?.Length ?? 0);
    }

    public static int EstimateSizeBytes(ConfigurationAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ConfigurationId.Length
            + result.ConfigurationName.Length
            + (result.AnalysisNotice?.Length ?? 0)
            + result.UnresolvedCardNames.Sum(name => name.Length)
            + result.ColorSources.Sum(row => row.Color.Length + row.DisplayColor.Length + row.DrivingSpell.Length)
            + (result.Signals is null
                ? 0
                : result.Signals.CatalogEffectiveDate.Length
                    + result.Signals.GameChangers.Sum(name => name.Length)
                    + result.Signals.MassLandDenialCards.Sum(name => name.Length)
                    + result.Signals.ExtraTurnCards.Sum(name => name.Length));
    }
}

internal sealed record DeckAnalysisCacheInputs(
    string Commander,
    string NormalizedDeckSource,
    bool IncludeCardVersions,
    bool IncludeCandidateReferencesInAnalysis,
    string TargetAiPlatformKey,
    IReadOnlyList<string> SelectedQuestionIds);

internal sealed record DeckComparisonCacheInputs(
    string NormalizedDeckASource,
    string NormalizedDeckBSource,
    string DeckABracket,
    string DeckBBracket,
    string TargetAiPlatformKey);

internal sealed record MetaGapCacheInputs(
    string CommanderName,
    string NormalizedDeckSource,
    CedhMetaTimePeriod TimePeriod,
    CedhMetaSortBy SortBy,
    int MinEventSize,
    int? MaxStanding,
    IReadOnlyList<int> SelectedReferenceIndexes,
    string TargetAiPlatformKey);
