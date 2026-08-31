using System.Security.Cryptography;
using System.Text;
using DeckFlow.Core.Content;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Knowledge.MeasuredStyleExtraction;
using DeckFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeckFlow.Web.Services.CreatorStyle;

/// <summary>
/// Crawls creator-scoped Archidekt decks with a creator-level warm-cache short-circuit.
/// </summary>
public sealed class CreatorProfileDeckCrawler
{
    private const string ConfidenceMarker = "ok";

    private readonly IArchidektOwnerClient _ownerClient;
    private readonly IArchidektDeckImporter _deckImporter;
    private readonly ICreatorProfileSourceStore _profileSourceStore;
    private readonly ICreatorDeckCacheStore _deckCacheStore;
    private readonly ILogger<CreatorProfileDeckCrawler> _logger;
    private readonly TimeSpan _freshnessWindow;
    private readonly Func<DateTimeOffset> _nowUtc;

    /// <summary>
    /// Creates a creator-profile deck crawler.
    /// </summary>
    public CreatorProfileDeckCrawler(
        IArchidektOwnerClient ownerClient,
        IArchidektDeckImporter deckImporter,
        ICreatorProfileSourceStore profileSourceStore,
        ICreatorDeckCacheStore deckCacheStore,
        ILogger<CreatorProfileDeckCrawler>? logger = null,
        TimeSpan? freshnessWindow = null,
        Func<DateTimeOffset>? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(ownerClient);
        ArgumentNullException.ThrowIfNull(deckImporter);
        ArgumentNullException.ThrowIfNull(profileSourceStore);
        ArgumentNullException.ThrowIfNull(deckCacheStore);
        _ownerClient = ownerClient;
        _deckImporter = deckImporter;
        _profileSourceStore = profileSourceStore;
        _deckCacheStore = deckCacheStore;
        _logger = logger ?? NullLogger<CreatorProfileDeckCrawler>.Instance;
        _freshnessWindow = freshnessWindow ?? TimeSpan.FromHours(24);
        _nowUtc = nowUtc ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Crawls the creator's public Archidekt decks or serves them from the creator cache.
    /// </summary>
    /// <param name="creatorSlug">Creator slug.</param>
    /// <param name="forceRefresh">When <see langword="true"/>, bypasses the creator-level freshness short-circuit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The creator deck samples.</returns>
    public async Task<IReadOnlyList<CreatorDeckSample>> CrawlAsync(string creatorSlug, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(creatorSlug);

        var source = await _profileSourceStore.GetBySlugAsync(creatorSlug, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return Array.Empty<CreatorDeckSample>();
        }

        if (!forceRefresh
            && source.LastCrawledUtc is not null
            && (_nowUtc() - source.LastCrawledUtc.Value) < _freshnessWindow)
        {
            _logger.LogDebug("Serving creator {CreatorSlug} entirely from warm cache.", creatorSlug);
            var warmEntries = await _deckCacheStore.GetByCreatorAsync(creatorSlug, cancellationToken).ConfigureAwait(false);
            return RebuildSamplesFromCache(warmEntries, source);
        }

        var resolvedUsername = await _ownerClient.ResolveUsernameAsync(source.ProfileUsername, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resolvedUsername))
        {
            var fallbackInput = source.ProfileUrl ?? source.ProfileUsername;
            if (!ArchidektOwnerUrl.TryGetUsername(fallbackInput, out resolvedUsername))
            {
                return Array.Empty<CreatorDeckSample>();
            }
        }

        var listing = await _ownerClient.ListDeckSummariesAsync(resolvedUsername, cancellationToken).ConfigureAwait(false);
        var cacheEntries = await _deckCacheStore.GetByCreatorAsync(creatorSlug, cancellationToken).ConfigureAwait(false);
        if (listing.HasUpstreamFailure)
        {
            _logger.LogWarning("Archidekt enumeration failed for {CreatorSlug}; leaving freshness stamp untouched.", creatorSlug);
            return RebuildSamplesFromCache(cacheEntries, source);
        }

        var cacheByDeckId = cacheEntries.ToDictionary(entry => entry.DeckId, StringComparer.Ordinal);
        var samples = new List<CreatorDeckSample>();

        foreach (var summary in listing.Decks)
        {
            if (summary.Size > StapleStripper.MaxDeckSize)
            {
                continue;
            }

            CreatorDeckCacheEntry? cachedEntry = null;
            var hasCachedEntry = cacheByDeckId.TryGetValue(summary.Id, out cachedEntry);
            var canReuse = !forceRefresh
                && hasCachedEntry
                && !string.IsNullOrWhiteSpace(cachedEntry.ContentHash);
            if (canReuse)
            {
                samples.Add(RebuildSampleFromCache(cachedEntry!, source));
                continue;
            }

            var importedEntries = await _deckImporter.ImportAsync(summary.Id, cancellationToken).ConfigureAwait(false);
            var contentHash = ComputeCanonicalHash(importedEntries);
            if (cachedEntry is not null && string.Equals(cachedEntry.ContentHash, contentHash, StringComparison.Ordinal))
            {
                samples.Add(RebuildSampleFromCache(cachedEntry, source));
                continue;
            }

            var cacheEntry = new CreatorDeckCacheEntry
            {
                CreatorSlug = creatorSlug,
                DeckId = summary.Id,
                ContentHash = contentHash,
                FolderId = summary.ParentFolderId,
                FolderName = summary.ParentFolderName,
                Size = summary.Size,
                ConfidenceMarker = ConfidenceMarker,
                Entries = importedEntries,
                CachedUtc = _nowUtc()
            };

            await _deckCacheStore.UpsertAsync(cacheEntry, cancellationToken).ConfigureAwait(false);
            samples.Add(RebuildSampleFromCache(cacheEntry, source));
        }

        await _profileSourceStore.SetLastCrawledAsync(creatorSlug, _nowUtc(), cancellationToken).ConfigureAwait(false);
        return samples;
    }

    private static IReadOnlyList<CreatorDeckSample> RebuildSamplesFromCache(
        IReadOnlyList<CreatorDeckCacheEntry> cacheEntries,
        CreatorProfileSource source)
    {
        return cacheEntries.Select(entry => RebuildSampleFromCache(entry, source)).ToArray();
    }

    private static CreatorDeckSample RebuildSampleFromCache(CreatorDeckCacheEntry entry, CreatorProfileSource source)
    {
        return new CreatorDeckSample
        {
            DeckId = entry.DeckId,
            Entries = entry.Entries,
            CardCount = entry.Size,
            FolderId = entry.FolderId,
            FolderName = entry.FolderName,
            FolderWeight = ResolveFolderWeight(source, entry.FolderId),
            ConfidenceMarker = entry.ConfidenceMarker
        };
    }

    private static double ResolveFolderWeight(CreatorProfileSource source, int? folderId)
    {
        if (folderId is not null && source.FolderWeights.TryGetValue(folderId.Value, out var configuredWeight))
        {
            return configuredWeight;
        }

        return 1.0;
    }

    private static string ComputeCanonicalHash(IEnumerable<DeckEntry> entries)
    {
        var canonical = entries
            .OrderBy(entry => entry.NormalizedName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Board, StringComparer.Ordinal)
            .ThenBy(entry => entry.Category, StringComparer.Ordinal)
            .ThenBy(entry => entry.SetCode, StringComparer.Ordinal)
            .ThenBy(entry => entry.CollectorNumber, StringComparer.Ordinal)
            .Select(entry => string.Join(
                "|",
                entry.NormalizedName ?? string.Empty,
                entry.Quantity,
                entry.Board ?? string.Empty,
                entry.Category ?? string.Empty,
                entry.SetCode ?? string.Empty,
                entry.CollectorNumber ?? string.Empty,
                entry.IsFoil));
        var payload = string.Join("\n", canonical);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
