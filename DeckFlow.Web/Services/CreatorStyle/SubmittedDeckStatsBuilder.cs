using System.Net;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Knowledge.CreatorStyleRubric;
using DeckFlow.Core.Knowledge.MeasuredStyleExtraction;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Services.Manabase;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RestSharp;

namespace DeckFlow.Web.Services.CreatorStyle;

/// <summary>
/// Builds submitted-deck statistics and deck-context inputs for creator-style evaluation.
/// </summary>
public interface ISubmittedDeckStatsBuilder
{
    /// <summary>
    /// Loads and analyzes a submitted deck source.
    /// </summary>
    /// <param name="deckSource">Deck URL or pasted export text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The submitted-deck analysis result.</returns>
    Task<SubmittedDeckAnalysis> BuildAsync(string deckSource, CancellationToken cancellationToken = default);
}

/// <summary>
/// Carries the submitted-deck statistics, grounding context, and normalized load result.
/// </summary>
public sealed record SubmittedDeckAnalysis
{
    /// <summary>
    /// Gets the submitted-deck statistics keyed by canonical measured metric strings.
    /// </summary>
    public required SubmittedDeckStats Stats { get; init; }

    /// <summary>
    /// Gets the deck-context inputs needed for card-grounding and whitelist checks.
    /// </summary>
    public required CardGroundingDeckContext DeckContext { get; init; }

    /// <summary>
    /// Gets the loaded deck entries after commander inference has been applied.
    /// </summary>
    public required IReadOnlyList<DeckEntry> Entries { get; init; }

    /// <summary>
    /// Gets the distinct combo-card names from included Spellbook combos over the analyzed mainboard+commander entries.
    /// </summary>
    public required IReadOnlyList<string> IncludedComboCardNames { get; init; }

    /// <summary>
    /// Gets a value indicating whether the analyzed deck entries could not be fully resolved for manabase analysis.
    /// </summary>
    public required bool DeckResolutionDegraded { get; init; }

    /// <summary>
    /// Gets the resolved commander name when card resolution succeeds.
    /// </summary>
    public string? ResolvedCommanderName { get; init; }

    /// <summary>
    /// Gets the loader-provided import notice, if any.
    /// </summary>
    public string? ImportNotice { get; init; }
}

/// <summary>
/// Produces submitted-deck metrics using the same category, combo, and Karsten pipelines as creator profiles.
/// </summary>
public sealed class SubmittedDeckStatsBuilder : ISubmittedDeckStatsBuilder
{
    private static readonly HashSet<string> AnalyzedBoards = new(StringComparer.OrdinalIgnoreCase)
    {
        "mainboard",
        "commander"
    };

    private readonly Func<string, CancellationToken, Task<DeckSourceLoadResult>> _loadDeckAsync;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>> _getCategoriesAsync;
    private readonly Func<IReadOnlyList<DeckEntry>, CancellationToken, Task<CommanderSpellbookResult?>> _findCombosAsync;
    private readonly Func<IReadOnlyList<DeckEntry>, CancellationToken, Task<SubmittedDeckResolution>> _analyzeSubmittedDeckAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> _executeCollectionAsync;
    private readonly Func<string, CancellationToken, Task<ScryfallCard?>> _searchFallbackCardAsync;
    private readonly ILogger<SubmittedDeckStatsBuilder> _logger;

    /// <summary>
    /// Creates a submitted-deck stats builder using the production dependencies.
    /// </summary>
    public SubmittedDeckStatsBuilder(
        IDeckEntryLoader deckEntryLoader,
        CategoryKnowledgeRepository categoryKnowledgeRepository,
        ICommanderSpellbookService commanderSpellbookService,
        IScryfallCardResolver scryfallCardResolver,
        ILogger<SubmittedDeckStatsBuilder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(deckEntryLoader);
        ArgumentNullException.ThrowIfNull(categoryKnowledgeRepository);
        ArgumentNullException.ThrowIfNull(commanderSpellbookService);
        ArgumentNullException.ThrowIfNull(scryfallCardResolver);

        _loadDeckAsync = (deckSource, cancellationToken) => deckEntryLoader.LoadFromSourceAsync(deckSource, cancellationToken: cancellationToken);
        _getCategoriesAsync = (cardName, cancellationToken) => categoryKnowledgeRepository.GetCategoriesAsync(cardName, cancellationToken);
        _findCombosAsync = (entries, cancellationToken) => commanderSpellbookService.FindCombosAsync(entries, cancellationToken);
        _analyzeSubmittedDeckAsync = AnalyzeSubmittedDeckAsync;
        _executeCollectionAsync = (request, cancellationToken) => scryfallCardResolver.ExecuteCollectionAsync(request, cancellationToken);
        _searchFallbackCardAsync = (cardName, cancellationToken) => scryfallCardResolver.SearchFallbackCardAsync(cardName, cancellationToken);
        _logger = logger ?? NullLogger<SubmittedDeckStatsBuilder>.Instance;
    }

    internal SubmittedDeckStatsBuilder(
        Func<string, CancellationToken, Task<DeckSourceLoadResult>>? loadDeckAsync = null,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? getCategoriesAsync = null,
        Func<IReadOnlyList<DeckEntry>, CancellationToken, Task<CommanderSpellbookResult?>>? findCombosAsync = null,
        Func<IReadOnlyList<DeckEntry>, CancellationToken, Task<SubmittedDeckResolution>>? analyzeSubmittedDeckAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsync = null,
        Func<string, CancellationToken, Task<ScryfallCard?>>? searchFallbackCardAsync = null,
        ILogger<SubmittedDeckStatsBuilder>? logger = null)
    {
        _logger = logger ?? NullLogger<SubmittedDeckStatsBuilder>.Instance;
        _loadDeckAsync = loadDeckAsync ?? throw new ArgumentNullException(nameof(loadDeckAsync));
        _getCategoriesAsync = getCategoriesAsync ?? throw new ArgumentNullException(nameof(getCategoriesAsync));
        _findCombosAsync = findCombosAsync ?? throw new ArgumentNullException(nameof(findCombosAsync));
        _analyzeSubmittedDeckAsync = analyzeSubmittedDeckAsync ?? AnalyzeSubmittedDeckAsync;
        _executeCollectionAsync = executeCollectionAsync ?? ((_, _) =>
            throw new InvalidOperationException("executeCollectionAsync must be supplied when the built-in submitted-deck analysis path is used."));
        _searchFallbackCardAsync = searchFallbackCardAsync ?? ((_, _) =>
            throw new InvalidOperationException("searchFallbackCardAsync must be supplied when the built-in submitted-deck analysis path is used."));
    }

    /// <inheritdoc />
    public async Task<SubmittedDeckAnalysis> BuildAsync(string deckSource, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deckSource);

        DeckSourceLoadResult loaded = await _loadDeckAsync(deckSource, cancellationToken).ConfigureAwait(false);
        List<DeckEntry> flaggedEntries = CommanderInference.ReflagInferredCommanders(loaded.Entries.ToList());
        List<DeckEntry> analyzedEntries = flaggedEntries
            .Where(entry => AnalyzedBoards.Contains(entry.Board))
            .ToList();

        Task<CommanderSpellbookResult?> comboTask = ResolveCombosAsync(analyzedEntries, cancellationToken);
        Task<SubmittedDeckResolution> resolutionTask = _analyzeSubmittedDeckAsync(analyzedEntries, cancellationToken);
        IReadOnlyDictionary<string, IReadOnlyList<string>> cardCategories =
            await ResolveCategoriesAsync(analyzedEntries, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, int> categoryCounts = CountCategories(analyzedEntries, cardCategories);
        CommanderSpellbookResult? comboResult = await comboTask.ConfigureAwait(false);
        SubmittedDeckResolution resolution = await resolutionTask.ConfigureAwait(false);

        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (string category in ContentTagVocabulary.CardCategories)
        {
            metrics[$"category_ratio:{category}"] = categoryCounts.TryGetValue(category, out int count)
                ? count
                : 0d;
        }

        metrics["combo_density:included_per_deck"] = comboResult?.IncludedCombos.Count ?? 0;
        if (resolution.HasResolvedDeck)
        {
            metrics["karsten:land_delta"] = resolution.Report.LandDelta;
            metrics["karsten:target_lands"] = resolution.Report.TargetLands;
            metrics["karsten:health_score"] = CreatorStyleDeckAnalysis.ToHealthScore(resolution.Report.Health);
        }

        return new SubmittedDeckAnalysis
        {
            Stats = new SubmittedDeckStats
            {
                Metrics = metrics,
                DeckSize = analyzedEntries.Sum(entry => entry.Quantity),
                CommanderCount = analyzedEntries
                    .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
                    .Sum(entry => entry.Quantity)
            },
            DeckContext = resolution.DeckContext,
            Entries = flaggedEntries,
            IncludedComboCardNames = comboResult?.IncludedCombos
                .SelectMany(combo => combo.CardNames)
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [],
            DeckResolutionDegraded = !resolution.HasResolvedDeck,
            ResolvedCommanderName = resolution.ResolvedCommanderName,
            ImportNotice = loaded.FallbackNotice
        };
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ResolveCategoriesAsync(
        IReadOnlyList<DeckEntry> entries,
        CancellationToken cancellationToken)
    {
        var categoryMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string cardName in entries
                     .Select(entry => entry.Name)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> categories = await _getCategoriesAsync(cardName, cancellationToken).ConfigureAwait(false);
            categoryMap[cardName] = categories;
        }

        return categoryMap;
    }

    private static IReadOnlyDictionary<string, int> CountCategories(
        IReadOnlyList<DeckEntry> entries,
        IReadOnlyDictionary<string, IReadOnlyList<string>> cardCategories)
    {
        if (entries.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var sample = new CreatorDeckSample
        {
            DeckId = "submitted-deck",
            Entries = entries,
            CardCount = entries.Sum(entry => entry.Quantity),
            ConfidenceMarker = string.Empty
        };

        return CategoryCounter.CountPerDeck(sample, cardCategories);
    }

    private async Task<CommanderSpellbookResult?> ResolveCombosAsync(IReadOnlyList<DeckEntry> entries, CancellationToken cancellationToken)
    {
        try
        {
            return await _findCombosAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Commander Spellbook lookup failed; continuing without combo density.");
            return null;
        }
    }

    private async Task<SubmittedDeckResolution> AnalyzeSubmittedDeckAsync(
        IReadOnlyList<DeckEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return EmptyResolution();
        }

        return await CreatorStyleDeckAnalysis.AnalyzeSubmittedDeckAsync(
            entries,
            _executeCollectionAsync,
            _searchFallbackCardAsync,
            cardName => _logger.LogDebug("Skipping unresolved submitted-deck manabase card {CardName}.", cardName),
            "submitted-deck manabase analysis.",
            cancellationToken).ConfigureAwait(false);
    }

    private static SubmittedDeckResolution EmptyResolution()
    {
        return new SubmittedDeckResolution
        {
            Report = CreatorStyleDeckAnalysis.EmptyReport(),
            DeckContext = new CardGroundingDeckContext
            {
                CommanderColorIdentity = new HashSet<string>(StringComparer.Ordinal),
                DeckProducedColors = new HashSet<char>(),
                DeckCardNames = new HashSet<string>(StringComparer.Ordinal)
            },
            ResolvedCommanderName = null,
            HasResolvedDeck = false
        };
    }
}

internal sealed record SubmittedDeckResolution
{
    public required ManabaseReport Report { get; init; }

    public required CardGroundingDeckContext DeckContext { get; init; }

    public string? ResolvedCommanderName { get; init; }

    public required bool HasResolvedDeck { get; init; }
}
