using System;
using System.IO;
using System.Threading;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Knowledge;
using MtgDeckStudio.Core.Reporting;
using Microsoft.Extensions.Logging;

namespace MtgDeckStudio.Web.Services;

public sealed class CategoryKnowledgeStore : ICategoryKnowledgeStore
{
    private const int HarvestDeckCount = 20;
    private readonly string _artifactsPath;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CategoryKnowledgeRepository _repository;
    private readonly ArchidektApiDeckImporter _archidektImporter;
    private readonly ArchidektRecentDecksImporter _recentDeckImporter;

    /// <summary>
    /// Initializes the knowledge store for the web app environment.
    /// </summary>
    /// <param name="environment">Web host environment for locating artifacts.</param>
    public CategoryKnowledgeStore(IWebHostEnvironment environment)
    {
        _artifactsPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "artifacts"));
        _databasePath = Path.Combine(_artifactsPath, "category-knowledge.db");
        _repository = new CategoryKnowledgeRepository(_databasePath);
        _archidektImporter = new ArchidektApiDeckImporter();
        _recentDeckImporter = new ArchidektRecentDecksImporter();
    }

    public string DatabasePath => _databasePath;

    /// <summary>
    /// Gets cached categories for a given card from the repository.
    /// </summary>
    /// <param name="cardName">Card name to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Persists observed categories emitted during runtime lookups.
    /// </summary>
    /// <param name="source">Source label for categories.</param>
    /// <param name="cardName">Card name.</param>
    /// <param name="categories">Categories to persist.</param>
    /// <param name="quantity">Quantity recorded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PersistObservedCategoriesAsync(string source, string cardName, IReadOnlyList<string> categories, int quantity = 1, string board = "mainboard", int deckCountIncrement = 0, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(cardName) || categories.Count == 0 || quantity <= 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _repository.PersistObservedCategoriesAsync(source, cardName, categories, quantity, board, deckCountIncrement, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs an extended cache sweep for the specified duration.
    /// </summary>
    /// <param name="logger">Logger for the sweep.</param>
    /// <param name="durationSeconds">Duration in seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> RunCacheSweepAsync(ILogger logger, int durationSeconds, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_artifactsPath);
            await _repository.EnsureSchemaAsync(cancellationToken);
            var session = new ArchidektDeckCacheSession(_repository, _archidektImporter, _recentDeckImporter, logger);
            var result = await session.RunAsync(TimeSpan.FromSeconds(durationSeconds), queueBatchSize: 5, fetchBatchSize: HarvestDeckCount, cancellationToken: cancellationToken);
            return result.DecksProcessed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Retrieves cached category rows for a card.
    /// </summary>
    /// <param name="cardName">Card name to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<CategoryKnowledgeRow>> GetCategoryRowsAsync(string cardName, string? boardFilter = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _repository.GetCategoryRowsForCardAsync(cardName, boardFilter, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Retrieves overall deck totals for the provided card.
    /// </summary>
    public async Task<CardDeckTotals> GetCardDeckTotalsAsync(string cardName, string? boardFilter = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _repository.GetCardDeckTotalsAsync(cardName, boardFilter, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gets the number of decks whose categories have been cached.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<int> GetProcessedDeckCountAsync(CancellationToken cancellationToken = default)
    {
        return _repository.GetProcessedDeckCountAsync(cancellationToken);
    }
}
