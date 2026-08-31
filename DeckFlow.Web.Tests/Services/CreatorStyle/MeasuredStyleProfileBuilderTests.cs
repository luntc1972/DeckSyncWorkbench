using System.Net;
using DeckFlow.Core.Content;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.MeasuredStyleExtraction;
using DeckFlow.Core.Models;
using DeckFlow.Core.Storage;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.CreatorStyle;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.Data.Sqlite;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Automated fixture coverage for the measured-style extractor.
/// The reduced Snail seed corpus below is automated and deterministic; the live 39-deck crawl is manual-only.
/// </summary>
public sealed class MeasuredStyleProfileBuilderTests
{
    [Fact]
    public async Task BuildAsync_PersistsProfile_RoundTripsMetricsAndHandlesNullComboGracefully()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        await using var harness = await TestHarness.CreateAsync(now);
        await harness.SeedSourceAsync(
            "builder-general",
            "builder-general",
            SnailSeedCorpusFixture.DeckSummaries,
            SnailSeedCorpusFixture.Samples);
        await harness.SeedCategoriesAsync();
        await harness.SeedBaselineAsync();

        var comboService = new FakeCommanderSpellbookService(new Dictionary<string, CommanderSpellbookResult?>(StringComparer.Ordinal)
        {
            ["builder-general-current-1"] = new CommanderSpellbookResult(
                [new SpellbookCombo(["Viscera Seer"], ["Loop"], "Loop line"), new SpellbookCombo(["Skullclamp"], ["Cards"], "Clamp line")],
                []),
            ["builder-general-current-2"] = null
        });
        var builder = harness.CreateBuilder(comboService);

        var profile = await builder.BuildAsync("builder-general", SnailSeedCorpusFixture.Platform);
        var stored = await harness.ProfileStore.GetBySlugAsync("builder-general");

        Assert.NotNull(stored);
        Assert.Equal(profile.Slug, stored!.Slug);
        Assert.Equal(profile.Platform, stored.Platform);
        Assert.Equal(profile.MinDecks, stored.MinDecks);
        Assert.Equal(profile.InsufficientSample, stored.InsufficientSample);
        Assert.Equal(profile.UpdatedUtc, stored.UpdatedUtc);
        Assert.True(profile.MeasuredMetrics.SequenceEqual(stored.MeasuredMetrics));
        Assert.Equal(SnailSeedCorpusFixture.Samples.Count, stored.MinDecks);
        Assert.False(stored.InsufficientSample);
        Assert.NotEmpty(stored.MeasuredMetrics);
        Assert.All(stored.MeasuredMetrics, metric =>
        {
            Assert.Equal(SnailSeedCorpusFixture.Samples.Count, metric.NumDecks);
            Assert.NotNull(metric.Distribution);
            Assert.NotNull(metric.Distribution!.EffectiveSampleSize);
            Assert.Equal(5.0, metric.Distribution.EffectiveSampleSize!.Value);
        });

        var comboMetric = Assert.Single(stored.MeasuredMetrics, metric => metric.Metric == "combo_density:included_per_deck");
        Assert.Equal(2d / SnailSeedCorpusFixture.Samples.Count, comboMetric.Value, 6);

        var liftMetric = stored.MeasuredMetrics.FirstOrDefault(metric => metric.Metric.StartsWith("lift:", StringComparison.Ordinal));
        Assert.NotNull(liftMetric);
        Assert.Contains(stored.MeasuredMetrics, metric => metric.Metric == "category_ratio:ramp");
    }

    [Fact]
    public async Task MeasuredStyleProfileBuilder_SnailSeedCorpus_ExtractorInvariants()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        await using var harness = await TestHarness.CreateAsync(now);
        await harness.SeedSourceAsync(
            SnailSeedCorpusFixture.CreatorSlug,
            SnailSeedCorpusFixture.Username,
            SnailSeedCorpusFixture.DeckSummaries,
            SnailSeedCorpusFixture.Samples);
        await harness.SeedSourceAsync(
            "snail-seed-thin",
            "snail-seed-thin",
            SnailSeedCorpusFixture.DeckSummaries.Take(4).ToArray(),
            SnailSeedCorpusFixture.BelowMinFloorSubset);
        await harness.SeedCategoriesAsync();
        await harness.SeedBaselineAsync();

        var builder = harness.CreateBuilder(new FakeCommanderSpellbookService(new Dictionary<string, CommanderSpellbookResult?>(StringComparer.Ordinal)
        {
            ["snail-seed-current-1"] = new CommanderSpellbookResult(
                [new SpellbookCombo(["Viscera Seer"], ["Loop"], "Loop line")],
                []),
            ["snail-seed-budget-1"] = null
        }));

        var strippedSamples = StapleStripper.StripStaples(
            StapleStripper.FlagNearPrecons(StapleStripper.FilterOversized(SnailSeedCorpusFixture.Samples)),
            StapleStripper.ComputePersonalStaples(SnailSeedCorpusFixture.Samples));
        var profile = await builder.BuildAsync(SnailSeedCorpusFixture.CreatorSlug, SnailSeedCorpusFixture.Platform);
        var thinProfile = await builder.BuildAsync("snail-seed-thin", SnailSeedCorpusFixture.Platform);

        Assert.All(strippedSamples, sample =>
        {
            Assert.DoesNotContain(sample.Entries, entry => string.Equals(entry.Name, "Sol Ring", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(sample.Entries, entry => string.Equals(entry.Name, "Command Tower", StringComparison.OrdinalIgnoreCase));
        });

        Assert.All(profile.MeasuredMetrics, metric =>
        {
            Assert.Equal(SnailSeedCorpusFixture.Samples.Count, metric.NumDecks);
            Assert.NotNull(metric.Distribution);
            Assert.NotNull(metric.Distribution!.EffectiveSampleSize);
        });

        double staplePairLift = Assert.Single(profile.MeasuredMetrics, metric => metric.Metric == "lift:draw|ramp").Value;
        double discriminatingPairLift = Assert.Single(profile.MeasuredMetrics, metric => metric.Metric == "lift:blink|tokens").Value;
        Assert.True(discriminatingPairLift > staplePairLift);

        Assert.All(profile.MeasuredMetrics, metric => Assert.Equal(5.0, metric.Distribution!.EffectiveSampleSize));
        Assert.True(thinProfile.InsufficientSample);
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly Dictionary<string, IReadOnlyList<ArchidektDeckSummary>> _deckSummariesByUsername = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DeckEntry>> _decksById = new(StringComparer.Ordinal);

        private TestHarness(
            string directory,
            CreatorProfileSourceStore sourceStore,
            CreatorDeckCacheStore cacheStore,
            CreatorStyleProfileStore profileStore,
            CategoryKnowledgeRepository categoryKnowledgeRepository,
            DateTimeOffset now)
        {
            Directory = directory;
            SourceStore = sourceStore;
            CacheStore = cacheStore;
            ProfileStore = profileStore;
            CategoryKnowledgeRepository = categoryKnowledgeRepository;
            Now = now;
        }

        public string Directory { get; }

        public DateTimeOffset Now { get; }

        public CreatorProfileSourceStore SourceStore { get; }

        public CreatorDeckCacheStore CacheStore { get; }

        public CreatorStyleProfileStore ProfileStore { get; }

        public CategoryKnowledgeRepository CategoryKnowledgeRepository { get; }

        public static Task<TestHarness> CreateAsync(DateTimeOffset now)
        {
            var directory = Path.Combine(Path.GetTempPath(), "deckflow-95-07-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var creatorDb = Path.Combine(directory, "creator-style.sqlite");
            var knowledgeDb = Path.Combine(directory, "category-knowledge.sqlite");
            return Task.FromResult(new TestHarness(
                directory,
                new CreatorProfileSourceStore(creatorDb),
                new CreatorDeckCacheStore(creatorDb),
                new CreatorStyleProfileStore(creatorDb),
                new CategoryKnowledgeRepository(RelationalDatabaseConnection.FromSqlitePath(knowledgeDb)),
                now));
        }

        public async Task SeedSourceAsync(
            string slug,
            string username,
            IReadOnlyList<ArchidektDeckSummary> summaries,
            IReadOnlyList<CreatorDeckSample> samples)
        {
            _deckSummariesByUsername[username] = summaries
                .Select(summary => summary with { Id = summary.Id.Replace("snail", slug, StringComparison.Ordinal) })
                .ToArray();

            foreach (CreatorDeckSample sample in samples)
            {
                string deckId = sample.DeckId.Replace("snail", slug, StringComparison.Ordinal);
                _decksById[deckId] = sample.Entries.ToList();
            }

            await SourceStore.UpsertAsync(new CreatorProfileSource
            {
                Slug = slug,
                Platform = SnailSeedCorpusFixture.Platform,
                ProfileUsername = username,
                FolderWeights = SnailSeedCorpusFixture.FolderWeights,
                UpdatedUtc = Now
            });
        }

        public async Task SeedCategoriesAsync()
        {
            foreach ((string cardName, IReadOnlyList<string> categories) in CardCategoryMap)
            {
                await CategoryKnowledgeRepository.PersistObservedCategoriesAsync("fixture-categories", cardName, categories);
            }
        }

        public async Task SeedBaselineAsync()
        {
            await SeedProcessedDeckAsync("baseline-1", "Commander One", ["ramp", "draw"]);
            await SeedProcessedDeckAsync("baseline-2", "Commander Two", ["ramp", "draw"]);
            await SeedProcessedDeckAsync("baseline-3", "Commander Three", ["ramp", "removal"]);
            await SeedProcessedDeckAsync("baseline-4", "Commander Four", ["ramp", "removal"]);
            await SeedProcessedDeckAsync("baseline-5", "Commander Five", ["draw", "removal"]);
            await SeedProcessedDeckAsync("baseline-6", "Commander Six", ["blink", "tokens"]);
            await SeedProcessedDeckAsync("baseline-7", "Commander Seven", ["ramp"]);
            await SeedProcessedDeckAsync("baseline-8", "Commander Eight", ["draw"]);
            await SeedProcessedDeckAsync("baseline-9", "Commander Nine", ["blink"]);
            await SeedProcessedDeckAsync("baseline-10", "Commander Ten", ["tokens"]);
        }

        public MeasuredStyleProfileBuilder CreateBuilder(ICommanderSpellbookService comboService)
        {
            var ownerClient = new FakeOwnerClient(_deckSummariesByUsername);
            var importer = new FakeDeckImporter(_decksById);
            var crawler = new CreatorProfileDeckCrawler(
                ownerClient,
                importer,
                SourceStore,
                CacheStore,
                freshnessWindow: TimeSpan.Zero,
                nowUtc: () => Now);
            var tagger = new FakeTaggerLookupService(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bident of Thassa"] = ["draw"],
                ["Reconnaissance Mission"] = ["draw"],
                ["Young Pyromancer"] = ["tokens"]
            });
            var resolver = new CreatorDeckCategoryResolver(CategoryKnowledgeRepository, tagger);
            var scryfallResolver = new FakeScryfallCardResolver(BuildScryfallCardMap());

            return new MeasuredStyleProfileBuilder(
                crawler,
                resolver,
                CategoryKnowledgeRepository,
                comboService,
                scryfallResolver,
                ProfileStore,
                nowUtc: () => Now,
                logger: null);
        }

        public async ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
            }
            catch
            {
            }

            await ValueTask.CompletedTask;
        }

        private async Task SeedProcessedDeckAsync(string deckId, string commanderName, IReadOnlyList<string> categories)
        {
            await CategoryKnowledgeRepository.AddDeckIdsAsync([deckId]);
            await CategoryKnowledgeRepository.MarkDeckProcessedAsync(deckId, commanderName);
            for (var index = 0; index < categories.Count; index++)
            {
                string category = categories[index];
                await CategoryKnowledgeRepository.PersistObservedCategoriesAsync(
                    $"archidekt_live:{deckId}",
                    $"{deckId}-card-{index}",
                    [category]);
            }
        }

        private static Dictionary<string, ScryfallCard> BuildScryfallCardMap()
        {
            var cards = new Dictionary<string, ScryfallCard>(StringComparer.OrdinalIgnoreCase);
            foreach (CreatorDeckSample sample in SnailSeedCorpusFixture.Samples)
            {
                foreach (DeckEntry entry in sample.Entries)
                {
                    if (!cards.ContainsKey(entry.Name))
                    {
                        cards[entry.Name] = CreateScryfallCard(entry.Name);
                    }
                }
            }

            return cards;
        }
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> CardCategoryMap =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Arcane Signet"] = ["ramp"],
            ["Skullclamp"] = ["draw"],
            ["Viscera Seer"] = ["sacrifice"],
            ["Lingering Souls"] = ["tokens"],
            ["Swords to Plowshares"] = ["removal"],
            ["Rhystic Study"] = ["draw"],
            ["Restoration Angel"] = ["blink"],
            ["Ephemerate"] = ["blink"],
            ["Eternal Witness"] = ["value"],
            ["Satyr Wayfinder"] = ["value"],
            ["Cultivate"] = ["ramp"],
            ["Village Rites"] = ["draw", "sacrifice"],
            ["Fellwar Stone"] = ["ramp"],
            ["Defiant Strike"] = ["draw"],
            ["Young Pyromancer"] = ["tokens"],
            ["Bident of Thassa"] = ["draw"],
            ["Reconnaissance Mission"] = ["draw"]
        };

    private static ScryfallCard CreateScryfallCard(string name)
    {
        bool isLand = name is "Command Tower" or "Plains" or "Swamp" or "Island" or "Forest" or "Mountain";
        string manaCost = name switch
        {
            "Sol Ring" => "{1}",
            "Arcane Signet" => "{2}",
            "Skullclamp" => "{1}",
            "Viscera Seer" => "{B}",
            "Lingering Souls" => "{2}{W}",
            "Swords to Plowshares" => "{W}",
            "Rhystic Study" => "{2}{U}",
            "Restoration Angel" => "{3}{W}",
            "Ephemerate" => "{W}",
            "Eternal Witness" => "{1}{G}{G}",
            "Satyr Wayfinder" => "{1}{G}",
            "Cultivate" => "{2}{G}",
            "Village Rites" => "{B}",
            "Fellwar Stone" => "{2}",
            "Defiant Strike" => "{W}",
            "Young Pyromancer" => "{1}{R}",
            "Bident of Thassa" => "{2}{U}{U}",
            "Reconnaissance Mission" => "{2}{U}{U}",
            _ when isLand => null!,
            _ => "{3}"
        };

        string typeLine = isLand
            ? "Basic Land"
            : name switch
            {
                "Sol Ring" or "Arcane Signet" or "Skullclamp" or "Fellwar Stone" or "Bident of Thassa" => "Artifact",
                "Rhystic Study" or "Reconnaissance Mission" or "Lingering Souls" => "Enchantment",
                "Restoration Angel" or "Eternal Witness" or "Satyr Wayfinder" or "Young Pyromancer" or "Viscera Seer" => "Creature",
                _ => "Instant"
            };

        return new ScryfallCard(
            Name: name,
            ManaCost: isLand ? null : manaCost,
            TypeLine: typeLine,
            OracleText: isLand ? "{T}: Add mana." : "Fixture text.",
            Power: null,
            Toughness: null,
            Keywords: null,
            ColorIdentity: null,
            SetCode: "tst",
            SetName: "Test Set",
            CollectorNumber: Math.Abs(name.GetHashCode(StringComparison.Ordinal)).ToString(),
            CardFaces: null,
            Id: null,
            Layout: null,
            ReleasedAt: null,
            Cmc: isLand ? 0 : CountManaValue(manaCost),
            ProducedMana: isLand ? ["W"] : null,
            Rarity: "common");
    }

    private static double CountManaValue(string manaCost)
    {
        return manaCost.Count(character => character == '{');
    }

    private sealed class FakeOwnerClient : IArchidektOwnerClient
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<ArchidektDeckSummary>> _summariesByUsername;

        public FakeOwnerClient(IReadOnlyDictionary<string, IReadOnlyList<ArchidektDeckSummary>> summariesByUsername)
        {
            _summariesByUsername = summariesByUsername;
        }

        public Task<string?> ResolveUsernameAsync(string usernameOrUrl, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(usernameOrUrl);

        public Task<ArchidektDeckListResult> ListDeckSummariesAsync(string ownerUsername, CancellationToken cancellationToken = default)
            => Task.FromResult(new ArchidektDeckListResult
            {
                Decks = _summariesByUsername.TryGetValue(ownerUsername, out var summaries)
                    ? summaries
                    : Array.Empty<ArchidektDeckSummary>(),
                HasUpstreamFailure = false
            });
    }

    private sealed class FakeDeckImporter : IArchidektDeckImporter
    {
        private readonly IReadOnlyDictionary<string, List<DeckEntry>> _decksById;

        public FakeDeckImporter(IReadOnlyDictionary<string, List<DeckEntry>> decksById)
        {
            _decksById = decksById;
        }

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken ct = default)
        {
            if (!_decksById.TryGetValue(urlOrDeckId, out var entries))
            {
                throw new InvalidOperationException($"Missing fake deck for {urlOrDeckId}.");
            }

            return Task.FromResult(entries);
        }
    }

    private sealed class FakeTaggerLookupService : IScryfallTaggerLookupService
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _tagsByCardName;

        public FakeTaggerLookupService(IReadOnlyDictionary<string, IReadOnlyList<string>> tagsByCardName)
        {
            _tagsByCardName = tagsByCardName;
        }

        public Task<IReadOnlyList<string>> LookupOracleTagsAsync(string cardName, CancellationToken cancellationToken = default)
            => Task.FromResult(_tagsByCardName.TryGetValue(cardName, out var tags) ? tags : Array.Empty<string>());
    }

    private sealed class FakeCommanderSpellbookService : ICommanderSpellbookService
    {
        private readonly IReadOnlyDictionary<string, CommanderSpellbookResult?> _resultsByDeckId;

        public FakeCommanderSpellbookService(IReadOnlyDictionary<string, CommanderSpellbookResult?> resultsByDeckId)
        {
            _resultsByDeckId = resultsByDeckId;
        }

        public Task<CommanderSpellbookResult?> FindCombosAsync(IReadOnlyList<DeckEntry> entries, CancellationToken cancellationToken)
        {
            string deckId = entries.First(entry => entry.Board == "commander").Name switch
            {
                "Teysa Karlov" when entries.Any(entry => entry.Name == "Village Rites") => "snail-seed-secondary-2",
                "Teysa Karlov" => "builder-general-current-1",
                "Brago, King Eternal" => "builder-general-current-2",
                "Feather, the Redeemed" => "snail-seed-budget-1",
                _ => entries.First(entry => entry.Board == "commander").Name
            };

            return Task.FromResult(_resultsByDeckId.TryGetValue(deckId, out var result) ? result : null);
        }
    }

    private sealed class FakeScryfallCardResolver : IScryfallCardResolver
    {
        private readonly IReadOnlyDictionary<string, ScryfallCard> _cardsByName;

        public FakeScryfallCardResolver(IReadOnlyDictionary<string, ScryfallCard> cardsByName)
        {
            _cardsByName = cardsByName;
        }

        public Task<RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCollectionResponse(_cardsByName.Values.ToList(), null)
            });

        public Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult(_cardsByName.TryGetValue(cardName, out var card) ? card : null);

        public Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => SearchFallbackCardAsync(cardName, cancellationToken);

        public Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken)
            => SearchFallbackCardAsync(cardName, cancellationToken);
    }
}
