using System.Diagnostics;
using System.Net;
using DeckSyncWorkbench.Core.Integration;
using DeckSyncWorkbench.Core.Models;
using Microsoft.Extensions.Logging;

namespace DeckSyncWorkbench.Core.Knowledge;

public sealed class ArchidektDeckCacheSession
{
    private readonly CategoryKnowledgeRepository _repository;
    private readonly IArchidektDeckImporter _deckImporter;
    private readonly ArchidektRecentDecksImporter _recentImporter;
    private readonly ILogger? _logger;

    public ArchidektDeckCacheSession(CategoryKnowledgeRepository repository, IArchidektDeckImporter deckImporter, ArchidektRecentDecksImporter recentImporter, ILogger? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _deckImporter = deckImporter ?? throw new ArgumentNullException(nameof(deckImporter));
        _recentImporter = recentImporter ?? throw new ArgumentNullException(nameof(recentImporter));
        _logger = logger;
    }

    /// <summary>
    /// Runs the cache session for a limited time, fetching decks and persisting categories.
    /// </summary>
    /// <param name="duration">Duration to run.</param>
    /// <param name="queueBatchSize">Max queue size per iteration.</param>
    /// <param name="fetchBatchSize">Max deck fetches per cycle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<ArchidektCacheRunResult> RunAsync(TimeSpan duration, int queueBatchSize = 5, int fetchBatchSize = 10, CancellationToken cancellationToken = default)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        queueBatchSize = Math.Max(1, queueBatchSize);
        fetchBatchSize = Math.Max(1, fetchBatchSize);

        await _repository.EnsureSchemaAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var added = 0;
        var updated = 0;
        var skipped = 0;

        while (stopwatch.Elapsed < duration && !cancellationToken.IsCancellationRequested)
        {
            var newestDeckIds = await _recentImporter.ImportRecentDeckIdsPageAsync(1, cancellationToken);
            if (newestDeckIds.Count > 0)
            {
                await _repository.AddDeckIdsAsync(newestDeckIds, cancellationToken);
            }

            var crawlPage = await _repository.GetRecentDeckCrawlPageAsync(cancellationToken);
            var deeperDeckIds = await _recentImporter.ImportRecentDeckIdsPageAsync(crawlPage, cancellationToken);
            if (deeperDeckIds.Count > 0)
            {
                await _repository.AddDeckIdsAsync(deeperDeckIds, cancellationToken);
                await _repository.SetRecentDeckCrawlPageAsync(crawlPage + 1, cancellationToken);
            }
            else
            {
                await _repository.SetRecentDeckCrawlPageAsync(2, cancellationToken);
            }

            var deckIds = await _repository.GetNextUnprocessedDeckIdsAsync(queueBatchSize, cancellationToken);
            if (deckIds.Count == 0)
            {
                break;
            }

            foreach (var deckId in deckIds)
            {
                try
                {
                    var cacheResult = await PersistDeckAsync(deckId, cancellationToken);
                    if (cacheResult == DeckCacheWriteResult.Added)
                    {
                        added++;
                    }
                    else
                    {
                        updated++;
                    }

                    _logger?.LogInformation("Cached categories from deck {DeckId} ({Result}).", deckId, cacheResult);
                    await _repository.MarkDecksProcessedAsync(new[] { deckId }, cancellationToken: cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
                {
                    skipped++;
                    _logger?.LogWarning(exception, "Skipping deck {DeckId} while caching categories.", deckId);
                    await _repository.MarkDecksProcessedAsync(new[] { deckId }, skip: true, cancellationToken: cancellationToken);
                }

                if (stopwatch.Elapsed >= duration || cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        stopwatch.Stop();
        return new ArchidektCacheRunResult(added, updated, skipped, stopwatch.Elapsed);
    }

    /// <summary>
    /// Imports a single deck and writes its categories to the repository.
    /// </summary>
    /// <param name="deckId">Deck ID to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<DeckCacheWriteResult> PersistDeckAsync(string deckId, CancellationToken cancellationToken)
    {
        var source = $"archidekt_live:{deckId}";
        var alreadyCached = await _repository.HasSourceDataAsync(source, cancellationToken);
        var entries = await _deckImporter.ImportAsync(deckId, cancellationToken);
        await DeckCategoryCacheWriter.ReplaceDeckEntriesAsync(_repository, source, entries, cancellationToken);
        return alreadyCached ? DeckCacheWriteResult.Updated : DeckCacheWriteResult.Added;
    }
}

internal enum DeckCacheWriteResult
{
    Added,
    Updated,
}

public sealed record ArchidektCacheRunResult(int DecksAdded, int DecksUpdated, int DecksSkipped, TimeSpan Duration)
{
    public int DecksProcessed => DecksAdded + DecksUpdated;
}
