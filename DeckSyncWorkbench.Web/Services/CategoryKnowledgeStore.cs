using DeckSyncWorkbench.Core.Integration;
using DeckSyncWorkbench.Core.Knowledge;
using System.Diagnostics;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Reporting;

namespace DeckSyncWorkbench.Web.Services;

public sealed class CategoryKnowledgeStore
{
    private const int HarvestDeckCount = 20;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private readonly string _artifactsPath;
    private readonly string _databasePath;
    private readonly string _harvestTextPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CategoryKnowledgeRepository _repository;

    public CategoryKnowledgeStore(IWebHostEnvironment environment)
    {
        _artifactsPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "artifacts"));
        _databasePath = Path.Combine(_artifactsPath, "category-knowledge.db");
        _harvestTextPath = Path.Combine(_artifactsPath, "archidekt-recent-20-categories.txt");
        _repository = new CategoryKnowledgeRepository(_databasePath);
    }

    public string DatabasePath => _databasePath;

    public async Task EnsureHarvestFreshAsync(HttpClient httpClient, ILogger logger, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_artifactsPath);
            await _repository.EnsureSchemaAsync(cancellationToken);

            var refreshNeeded = !File.Exists(_harvestTextPath)
                || DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_harvestTextPath) > MaxAge
                || !await _repository.HasSourceDataAsync("archidekt_harvest", cancellationToken);

            if (!refreshNeeded)
            {
                return;
            }

            logger.LogInformation("Refreshing Archidekt category knowledge database at {Path}.", _databasePath);
            var recentDeckIds = await new ArchidektRecentDecksImporter(httpClient).ImportRecentDeckIdsAsync(HarvestDeckCount, cancellationToken);
            var importer = new ArchidektApiDeckImporter(httpClient);
            var entries = new List<DeckEntry>();

            foreach (var deckId in recentDeckIds)
            {
                entries.AddRange(await importer.ImportAsync(deckId, cancellationToken));
            }

            var rows = CategoryKnowledgeReporter.Build(entries);
            await _repository.ReplaceSourceRowsAsync("archidekt_harvest", rows, cancellationToken);
            await File.WriteAllTextAsync(_harvestTextPath, CategoryKnowledgeReporter.ToText(rows, recentDeckIds.Count), cancellationToken);
            await _repository.AddDeckIdsAsync(recentDeckIds, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(string cardName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _repository.GetCategoriesAsync(cardName, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PersistObservedCategoriesAsync(string source, string cardName, IReadOnlyList<string> categories, int quantity = 1, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(cardName) || categories.Count == 0 || quantity <= 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _repository.PersistObservedCategoriesAsync(source, cardName, categories, quantity, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ProcessNextDecksAsync(HttpClient httpClient, ILogger logger, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _repository.EnsureSchemaAsync(cancellationToken);
            var unprocessed = await _repository.GetUnprocessedCountAsync(cancellationToken);
            if (unprocessed == 0)
            {
                var importer = new ArchidektRecentDecksImporter(httpClient);
                var recentDeckIds = await importer.ImportRecentDeckIdsAsync(HarvestDeckCount, cancellationToken);
                if (recentDeckIds.Count > 0)
                {
                    await _repository.AddDeckIdsAsync(recentDeckIds, cancellationToken);
                    unprocessed = await _repository.GetUnprocessedCountAsync(cancellationToken);
                }
            }

            if (unprocessed == 0)
            {
                return 0;
            }

            var toProcess = await _repository.GetNextUnprocessedDeckIdsAsync(1, cancellationToken);
            if (toProcess.Count == 0)
            {
                return 0;
            }

            var deckImporter = new ArchidektApiDeckImporter(httpClient);
            foreach (var deckId in toProcess)
            {
                try
                {
                    var entries = await deckImporter.ImportAsync(deckId, cancellationToken);
                    await PersistDeckEntriesAsync("archidekt_live", entries, cancellationToken);
                    await _repository.MarkDecksProcessedAsync(new[] { deckId }, cancellationToken: cancellationToken);
                    logger.LogInformation("Cached categories from deck {DeckId}.", deckId);
                }
                catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
                {
                    await _repository.MarkDecksProcessedAsync(new[] { deckId }, skip: true, cancellationToken: cancellationToken);
                    logger.LogWarning(exception, "Skipping deck {DeckId} while warming cache.", deckId);
                }
            }

            return toProcess.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RunCacheSweepAsync(HttpClient httpClient, ILogger logger, int durationSeconds, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _repository.EnsureSchemaAsync(cancellationToken);
            var stopwatch = Stopwatch.StartNew();
            var processed = 0;
            var recentImporter = new ArchidektRecentDecksImporter(httpClient);
            var deckImporter = new ArchidektApiDeckImporter(httpClient);

            while (stopwatch.Elapsed.TotalSeconds < durationSeconds && !cancellationToken.IsCancellationRequested)
            {
                var deckIds = await _repository.GetNextUnprocessedDeckIdsAsync(5, cancellationToken);
                if (deckIds.Count == 0)
                {
                    var newIds = await recentImporter.ImportRecentDeckIdsAsync(HarvestDeckCount, cancellationToken);
                    if (newIds.Count == 0)
                    {
                        break;
                    }

                    await _repository.AddDeckIdsAsync(newIds, cancellationToken);
                    continue;
                }

                foreach (var deckId in deckIds)
                {
                    try
                    {
                        var entries = await deckImporter.ImportAsync(deckId, cancellationToken);
                        await PersistDeckEntriesAsync("archidekt_live", entries, cancellationToken);
                        await _repository.MarkDecksProcessedAsync(new[] { deckId }, cancellationToken: cancellationToken);
                        processed++;
                        logger.LogInformation("Cached categories from deck {DeckId}.", deckId);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
                    {
                        await _repository.MarkDecksProcessedAsync(new[] { deckId }, skip: true, cancellationToken: cancellationToken);
                        logger.LogWarning(exception, "Skipping deck {DeckId} while warming cache.", deckId);
                    }

                    if (stopwatch.Elapsed.TotalSeconds >= durationSeconds)
                    {
                        break;
                    }
                }
            }

            return processed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistDeckEntriesAsync(string source, IEnumerable<DeckEntry> entries, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<(string CardName, string Category), int>(CardCategoryComparer.Instance);
        foreach (var entry in entries)
        {
            foreach (var category in CategoryKnowledgeReporter.SplitCategories(entry.Category))
            {
                var key = (entry.Name, category);
                counts[key] = counts.TryGetValue(key, out var existing) ? existing + entry.Quantity : entry.Quantity;
            }
        }

        foreach (var group in counts)
        {
            await _repository.PersistObservedCategoriesAsync(source, group.Key.CardName, new[] { group.Key.Category }, group.Value, cancellationToken);
        }
    }


}
