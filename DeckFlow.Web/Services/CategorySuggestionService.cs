using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Models;
using DeckFlow.Core.Parsing;
using DeckFlow.Core.Reporting;
using DeckFlow.Web.Models;
using Microsoft.Extensions.Logging;

namespace DeckFlow.Web.Services;

/// <summary>
/// Computes category suggestions for cards using the cached store, reference decks, and fallbacks.
/// </summary>
public interface ICategorySuggestionService
{
    /// <summary>
    /// Executes a lookup for category suggestions.
    /// </summary>
    /// <param name="request">Request describing the lookup mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<CategorySuggestionResult> SuggestAsync(CategorySuggestionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the outcome of a category suggestion lookup.
/// </summary>
public sealed record CategorySuggestionResult(
    string CardName,
    IReadOnlyList<string> ExactCategories,
    IReadOnlyList<string> InferredCategories,
    IReadOnlyList<string> EdhrecCategories,
    IReadOnlyList<string> TaggerCategories,
    CardDeckTotals CardDeckTotals,
    IReadOnlyList<string> UsedSources,
    bool NothingFound,
    int AdditionalDecksFound,
    bool CacheHarvestTriggered)
{
    /// <summary>
    /// Creates an empty result for a card that produced no suggestions from any source.
    /// </summary>
    /// <param name="cardName">Card name that was queried.</param>
    /// <returns>An empty suggestion result.</returns>
    public static CategorySuggestionResult Empty(string cardName) => new(
        cardName,
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        CardDeckTotals.Empty,
        Array.Empty<string>(),
        true,
        0,
        false);
}

/// <summary>
/// Default implementation of <see cref="ICategorySuggestionService"/>.
/// </summary>
public sealed class CategorySuggestionService : ICategorySuggestionService
{
    private const int ClickSweepDurationSeconds = 30;
    private readonly ICategoryKnowledgeStore _knowledgeStore;
    private readonly ILogger<CategorySuggestionService> _logger;
    private readonly ArchidektParser _archidektParser;
    private readonly IArchidektDeckImporter _archidektImporter;
    private readonly IScryfallTaggerService _taggerService;

    /// <summary>
    /// Initializes a new instance of <see cref="CategorySuggestionService"/>.
    /// </summary>
    public CategorySuggestionService(
        ICategoryKnowledgeStore knowledgeStore,
        ArchidektParser archidektParser,
        IArchidektDeckImporter archidektImporter,
        IScryfallTaggerService taggerService,
        ILogger<CategorySuggestionService> logger)
    {
        _knowledgeStore = knowledgeStore;
        _archidektParser = archidektParser;
        _archidektImporter = archidektImporter;
        _taggerService = taggerService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CategorySuggestionResult> SuggestAsync(CategorySuggestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.CardName))
        {
            throw new ArgumentException("Card name is required.", nameof(request));
        }

        if (request.Mode == CategorySuggestionMode.ReferenceDeck && !HasSuggestionInput(request))
        {
            throw new InvalidOperationException(request.ArchidektInputSource == DeckInputSource.PublicUrl
                ? "An Archidekt deck URL is required."
                : "Archidekt text is required.");
        }

        var cardName = request.CardName.Trim();
        var mode = request.Mode;
        var runAll = mode == CategorySuggestionMode.All;

        var initialDeckCount = await _knowledgeStore.GetProcessedDeckCountAsync(cancellationToken);

        var runReferencePath = mode == CategorySuggestionMode.ReferenceDeck;

        var exactCategories = runReferencePath
            ? CategorySuggestionReporter.SuggestCategories(await LoadReferenceEntriesAsync(request, cancellationToken), cardName)
            : Array.Empty<string>();

        var taggerCategories = mode == CategorySuggestionMode.ScryfallTagger || runAll
            ? await _taggerService.LookupOracleTagsAsync(cardName, cancellationToken)
            : Array.Empty<string>();

        var runCachedPath = mode == CategorySuggestionMode.CachedData || runAll;

        if (runCachedPath)
        {
            await _knowledgeStore.RunCacheSweepAsync(_logger, ClickSweepDurationSeconds, cancellationToken);
        }

        var inferredCategories = runCachedPath
            ? await _knowledgeStore.GetCategoriesAsync(cardName, cancellationToken)
            : Array.Empty<string>();

        var cardTotals = runCachedPath
            ? await _knowledgeStore.GetCardDeckTotalsAsync(cardName, cancellationToken: cancellationToken)
            : CardDeckTotals.Empty;

        var edhrecCategories = runCachedPath && exactCategories.Count == 0 && inferredCategories.Count == 0 && taggerCategories.Count == 0
            ? await new EdhrecCardLookup().LookupCategoriesAsync(cardName, cancellationToken)
            : Array.Empty<string>();

        if (edhrecCategories.Count > 0)
        {
            await _knowledgeStore.PersistObservedCategoriesAsync("edhrec", cardName, edhrecCategories, cancellationToken: cancellationToken);
        }

        var finalDeckCount = await _knowledgeStore.GetProcessedDeckCountAsync(cancellationToken);
        var additionalDecksFound = Math.Max(finalDeckCount - initialDeckCount, 0);

        var usedSources = new List<string>();
        if (exactCategories.Count > 0)
        {
            usedSources.Add("reference deck");
        }

        if (taggerCategories.Count > 0)
        {
            usedSources.Add("Scryfall Tagger");
        }

        if (inferredCategories.Count > 0)
        {
            usedSources.Add("cached store");
        }

        if (edhrecCategories.Count > 0)
        {
            usedSources.Add("EDHREC");
        }

        var nothingFound = exactCategories.Count == 0 && inferredCategories.Count == 0 && edhrecCategories.Count == 0 && taggerCategories.Count == 0;

        return new CategorySuggestionResult(
            cardName,
            exactCategories,
            inferredCategories,
            edhrecCategories,
            taggerCategories,
            cardTotals,
            usedSources,
            nothingFound,
            additionalDecksFound,
            runCachedPath);
    }

    /// <summary>
    /// Determines whether the request contains the deck input needed for reference-deck mode.
    /// </summary>
    /// <param name="request">Suggestion request to validate.</param>
    private static bool HasSuggestionInput(CategorySuggestionRequest request)
        => request.ArchidektInputSource == DeckInputSource.PublicUrl
            ? !string.IsNullOrWhiteSpace(request.ArchidektUrl)
            : !string.IsNullOrWhiteSpace(request.ArchidektText);

    /// <summary>
    /// Loads the reference deck entries from either a public URL or pasted deck text.
    /// </summary>
    /// <param name="request">Suggestion request with the reference deck input.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed reference deck entries.</returns>
    private async Task<List<DeckEntry>> LoadReferenceEntriesAsync(CategorySuggestionRequest request, CancellationToken cancellationToken)
    {
        if (request.ArchidektInputSource == DeckInputSource.PublicUrl)
        {
            return await _archidektImporter.ImportAsync(request.ArchidektUrl, cancellationToken);
        }

        return _archidektParser.ParseText(request.ArchidektText);
    }
}
