using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Normalization;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Core.Reporting;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class CategorySuggestionServiceTests
{
    [Fact]
    public async Task SuggestAsync_UsesInferredCategoriesWithoutHarvestWhenAvailable()
    {
        var totals = new CardDeckTotals(1, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["mainboard"] = 1
        });

        var store = new FakeKnowledgeStore(new[] { new[] { "Ramp" } }, processedDeckCount: 3, totals);
        var service = new CategorySuggestionService(store, new ArchidektParser(), new FakeImporter(), new FakeTaggerService(), NullLogger<CategorySuggestionService>.Instance);

        var request = new CategorySuggestionRequest
        {
            Mode = CategorySuggestionMode.CachedData,
            CardName = "Bird of Paradise"
        };

        var result = await service.SuggestAsync(request);

        Assert.False(result.NothingFound);
        Assert.Contains("Ramp", result.InferredCategories);
        Assert.Equal(1, store.RunCacheSweepCalls);
        Assert.Equal(1, result.CardDeckTotals.TotalDeckCount);
    }

    [Fact]
    public async Task SuggestAsync_TriggersHarvestWhenCacheEmpty()
    {
        var totals = CardDeckTotals.Empty;
        var store = new FakeKnowledgeStore(new[] { Array.Empty<string>(), new[] { "Draw" } }, processedDeckCount: 1, totals);
        var service = new CategorySuggestionService(store, new ArchidektParser(), new FakeImporter(), new FakeTaggerService(), NullLogger<CategorySuggestionService>.Instance);

        var request = new CategorySuggestionRequest
        {
            Mode = CategorySuggestionMode.CachedData,
            CardName = "Guardian Project"
        };

        var result = await service.SuggestAsync(request);

        Assert.Contains("Draw", result.InferredCategories);
        Assert.Equal(1, store.RunCacheSweepCalls);
        Assert.Equal(1, result.AdditionalDecksFound);
    }

    [Fact]
    public async Task SuggestAsync_UsesReferenceDeckWhenConfigured()
    {
        var totals = CardDeckTotals.Empty;
        var store = new FakeKnowledgeStore(new[] { Array.Empty<string>() }, processedDeckCount: 0, totals);
        var entries = new List<DeckEntry>
        {
            new() { Name = "Guardian Project", NormalizedName = CardNormalizer.Normalize("Guardian Project"), Category = "Draw,Ramp", Quantity = 1, Board = "mainboard" }
        };
        var importer = new FakeImporter(entries);
        var service = new CategorySuggestionService(store, new ArchidektParser(), importer, new FakeTaggerService(), NullLogger<CategorySuggestionService>.Instance);

        var request = new CategorySuggestionRequest
        {
            Mode = CategorySuggestionMode.ReferenceDeck,
            CardName = "Guardian Project",
            ArchidektInputSource = DeckInputSource.PublicUrl,
            ArchidektUrl = "deck-id"
        };

        var result = await service.SuggestAsync(request);

        Assert.Contains("Draw", result.ExactCategories);
        Assert.Contains("Ramp", result.ExactCategories);
        Assert.Contains("reference deck", result.UsedSources);
    }

    [Fact]
    public async Task SuggestAsync_UsesScryfallTaggerModeWithoutCacheSweep()
    {
        var store = new FakeKnowledgeStore(new[] { Array.Empty<string>() }, processedDeckCount: 0, CardDeckTotals.Empty);
        var tagger = new FakeTaggerService("Protection", "Value");
        var service = new CategorySuggestionService(store, new ArchidektParser(), new FakeImporter(), tagger, NullLogger<CategorySuggestionService>.Instance);

        var result = await service.SuggestAsync(new CategorySuggestionRequest
        {
            Mode = CategorySuggestionMode.ScryfallTagger,
            CardName = "Esper Sentinel"
        });

        Assert.False(result.NothingFound);
        Assert.Equal(new[] { "Protection", "Value" }, result.TaggerCategories);
        Assert.Contains("Scryfall Tagger", result.UsedSources);
        Assert.Equal(1, tagger.LookupCalls);
        Assert.Equal(0, store.RunCacheSweepCalls);
        Assert.False(result.CacheHarvestTriggered);
    }

    [Fact]
    public async Task SuggestAsync_AllModeIncludesTaggerAndCachedSuggestions()
    {
        var totals = new CardDeckTotals(4, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["mainboard"] = 4
        });
        var store = new FakeKnowledgeStore(new[] { new[] { "Draw" } }, processedDeckCount: 4, totals);
        var tagger = new FakeTaggerService("Value");
        var service = new CategorySuggestionService(store, new ArchidektParser(), new FakeImporter(), tagger, NullLogger<CategorySuggestionService>.Instance);

        var result = await service.SuggestAsync(new CategorySuggestionRequest
        {
            Mode = CategorySuggestionMode.All,
            CardName = "Rhystic Study"
        });

        Assert.Contains("Draw", result.InferredCategories);
        Assert.Contains("Value", result.TaggerCategories);
        Assert.Contains("cached store", result.UsedSources);
        Assert.Contains("Scryfall Tagger", result.UsedSources);
        Assert.Equal(1, store.RunCacheSweepCalls);
        Assert.Equal(1, tagger.LookupCalls);
        Assert.True(result.CacheHarvestTriggered);
    }

    private sealed class FakeKnowledgeStore : ICategoryKnowledgeStore
    {
        private readonly Queue<IReadOnlyList<string>> _responses;
        private readonly CardDeckTotals _totals;
        public int RunCacheSweepCalls { get; private set; }
        public int ProcessedDeckCount { get; private set; }
        private IReadOnlyList<string> _current = Array.Empty<string>();

        public FakeKnowledgeStore(IEnumerable<IReadOnlyList<string>> responses, int processedDeckCount, CardDeckTotals totals)
        {
            _responses = new Queue<IReadOnlyList<string>>(responses);
            ProcessedDeckCount = processedDeckCount;
            _totals = totals;
            _current = _responses.Count > 0 ? _responses.Dequeue() : Array.Empty<string>();
        }

        public Task<IReadOnlyList<CategoryKnowledgeRow>> GetCategoryRowsAsync(string cardName, string? boardFilter = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CategoryKnowledgeRow>>(Array.Empty<CategoryKnowledgeRow>());

        public Task<int> GetProcessedDeckCountAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ProcessedDeckCount);

        public Task<int> RunCacheSweepAsync(ILogger logger, int durationSeconds, CancellationToken cancellationToken = default)
        {
            RunCacheSweepCalls++;
            ProcessedDeckCount++;
            _current = _responses.Count > 0 ? _responses.Dequeue() : _current;
            return Task.FromResult(1);
        }

        public Task<IReadOnlyList<string>> GetCategoriesAsync(string cardName, CancellationToken cancellationToken = default)
            => Task.FromResult(_current);

        public Task PersistObservedCategoriesAsync(string source, string cardName, IReadOnlyList<string> categories, int quantity = 1, string board = "mainboard", int deckCountIncrement = 0, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<CardDeckTotals> GetCardDeckTotalsAsync(string cardName, string? boardFilter = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_totals);
    }

    private sealed class FakeImporter : IArchidektDeckImporter
    {
        private readonly IEnumerable<DeckEntry> _entries;

        public FakeImporter(IEnumerable<DeckEntry>? entries = null)
        {
            _entries = entries ?? Array.Empty<DeckEntry>();
        }

        public Task<List<DeckEntry>> ImportAsync(string urlOrId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_entries.ToList());
        }
    }

    private sealed class FakeTaggerService : IScryfallTaggerService
    {
        private readonly IReadOnlyList<string> _responses;

        public FakeTaggerService(params string[] responses)
        {
            _responses = responses;
        }

        public int LookupCalls { get; private set; }

        public Task<IReadOnlyList<string>> LookupOracleTagsAsync(string cardName, CancellationToken cancellationToken = default)
        {
            LookupCalls++;
            return Task.FromResult(_responses);
        }
    }
}
