using System.Net;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.CreatorStyle;
using DeckFlow.Web.Services.Manabase;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests.Services.CreatorStyle;

public sealed class SubmittedDeckStatsBuilderTests
{
    [Fact]
    public async Task BuildAsync_QuantityWeightedCategoriesAndCommanderInference_ReturnsExpectedStats()
    {
        var categoryLookupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        DeckEntry[] entries =
        [
            Entry("Tatyova, Benthic Druid", 1, "mainboard"),
            Entry("Rampant Growth", 2, "mainboard"),
            Entry("Cultivate", 1, "mainboard"),
            Entry("Sol Ring", 1, "mainboard"),
            Entry("Swords to Plowshares", 1, "sideboard"),
            Entry("Arcane Signet", 1, "maybeboard"),
        ];

        var builder = new SubmittedDeckStatsBuilder(
            loadDeckAsync: (_, _) => Task.FromResult(new DeckSourceLoadResult(entries.ToList(), "fallback notice")),
            getCategoriesAsync: (cardName, _) =>
            {
                categoryLookupCounts[cardName] = categoryLookupCounts.TryGetValue(cardName, out int count)
                    ? count + 1
                    : 1;

                IReadOnlyList<string> categories = cardName switch
                {
                    "Rampant Growth" => ["ramp"],
                    "Cultivate" => ["ramp"],
                    "Sol Ring" => ["ramp"],
                    _ => Array.Empty<string>(),
                };

                return Task.FromResult(categories);
            },
            findCombosAsync: (_, _) => Task.FromResult<CommanderSpellbookResult?>(new CommanderSpellbookResult(
                [
                    new SpellbookCombo(["Card A"], ["Result A"], "Line A"),
                    new SpellbookCombo(["Card B"], ["Result B"], "Line B"),
                ],
                [])),
            analyzeSubmittedDeckAsync: (_, _) => Task.FromResult(EmptyAnalysis()));

        SubmittedDeckAnalysis result = await builder.BuildAsync("fixture");

        Assert.Equal(4d, result.Stats.Metrics["category_ratio:ramp"]);
        Assert.Equal(2d, result.Stats.Metrics["combo_density:included_per_deck"]);
        Assert.Equal(["Card A", "Card B"], result.IncludedComboCardNames);
        Assert.Equal(5, result.Stats.DeckSize);
        Assert.Equal(1, result.Stats.CommanderCount);
        Assert.True(result.DeckResolutionDegraded);
        Assert.Equal("Tatyova, Benthic Druid", result.Entries.Single(entry => entry.Board == "commander").Name);
        Assert.Equal("fallback notice", result.ImportNotice);
        Assert.Equal(1, categoryLookupCounts["Tatyova, Benthic Druid"]);
        Assert.Equal(1, categoryLookupCounts["Rampant Growth"]);
        Assert.Equal(1, categoryLookupCounts["Cultivate"]);
        Assert.Equal(1, categoryLookupCounts["Sol Ring"]);
        Assert.DoesNotContain("Swords to Plowshares", categoryLookupCounts.Keys);
        Assert.DoesNotContain("Arcane Signet", categoryLookupCounts.Keys);
    }

    [Fact]
    public async Task BuildAsync_NullSpellbookResult_ReturnsZeroComboDensity()
    {
        DeckEntry[] entries =
        [
            Entry("Azusa, Lost but Seeking", 1, "commander"),
            Entry("Forest", 10, "mainboard"),
        ];

        var builder = new SubmittedDeckStatsBuilder(
            loadDeckAsync: (_, _) => Task.FromResult(new DeckSourceLoadResult(entries.ToList(), null)),
            getCategoriesAsync: (_, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            findCombosAsync: (_, _) => Task.FromResult<CommanderSpellbookResult?>(null),
            analyzeSubmittedDeckAsync: (_, _) => Task.FromResult(EmptyAnalysis()));

        SubmittedDeckAnalysis result = await builder.BuildAsync("fixture");

        Assert.Equal(0d, result.Stats.Metrics["combo_density:included_per_deck"]);
    }

    [Fact]
    public async Task BuildAsync_ResolvedDeck_UsesCasualSingletonKarstenParityAndDeckContext()
    {
        DeckEntry[] entries =
        [
            Entry("Tatyova, Benthic Druid", 1, "commander"),
            Entry("Forest", 10, "mainboard"),
            Entry("Island", 9, "mainboard"),
            Entry("Command Tower", 1, "mainboard"),
            Entry("Cultivate", 2, "mainboard"),
            Entry("Growth Spiral", 1, "mainboard"),
        ];

        Dictionary<string, ScryfallCard> cardsByName = CreateParityCards();
        IReadOnlyList<DeckCardEntry> deckEntries = entries
            .Select(entry => new DeckCardEntry
            {
                Card = ScryfallCardDataMapper.ToCardData(cardsByName[entry.Name]),
                Quantity = entry.Quantity,
                IsCommander = string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase)
            })
            .ToArray();
        IReadOnlyList<CardFact> facts = ScryfallCardFactMapper.ToCardFacts(deckEntries);
        ManabaseDeck deck = ManabaseClassifier.Classify(facts, isSingleton: true);
        ManabaseReport expectedReport = ManabaseAnalyzer.Analyze(deck, ManabaseMode.Casual);
        HashSet<char> expectedProducedColors = deck.Sources
            .SelectMany(source => source.Produces)
            .Select(ToColorChar)
            .ToHashSet();

        var builder = new SubmittedDeckStatsBuilder(
            loadDeckAsync: (_, _) => Task.FromResult(new DeckSourceLoadResult(entries.ToList(), null)),
            getCategoriesAsync: (_, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            findCombosAsync: (_, _) => Task.FromResult<CommanderSpellbookResult?>(null),
            executeCollectionAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCollectionResponse(cardsByName.Values.ToList(), null)
            }),
            searchFallbackCardAsync: (cardName, _) => Task.FromResult(cardsByName.TryGetValue(cardName, out ScryfallCard? card) ? card : null));

        SubmittedDeckAnalysis result = await builder.BuildAsync("fixture");

        Assert.Equal(expectedReport.TargetLands, result.Stats.Metrics["karsten:target_lands"], 6);
        Assert.Equal(expectedReport.LandDelta, result.Stats.Metrics["karsten:land_delta"], 6);
        Assert.Equal(ToExpectedHealthScore(expectedReport.Health), result.Stats.Metrics["karsten:health_score"]);
        Assert.False(result.DeckResolutionDegraded);
        Assert.Equal("Tatyova, Benthic Druid", result.ResolvedCommanderName);
        Assert.Equal(["G", "U"], result.DeckContext.CommanderColorIdentity.OrderBy(static color => color, StringComparer.Ordinal).ToArray());
        Assert.Equal(expectedProducedColors.OrderBy(static color => color).ToArray(), result.DeckContext.DeckProducedColors.OrderBy(static color => color).ToArray());
        Assert.Contains(CardNormalizer.Normalize("Tatyova, Benthic Druid"), result.DeckContext.DeckCardNames);
        Assert.Contains(CardNormalizer.Normalize("Growth Spiral"), result.DeckContext.DeckCardNames);
    }

    [Theory]
    [InlineData(ManabaseHealth.Healthy, 3d)]
    [InlineData(ManabaseHealth.Functional, 2d)]
    [InlineData(ManabaseHealth.Workable, 1d)]
    [InlineData(ManabaseHealth.NeedsWork, 0d)]
    public async Task BuildAsync_HealthMappingVariants_MapsToExpectedScore(ManabaseHealth health, double expectedScore)
    {
        DeckEntry[] entries =
        [
            Entry("Jodah, the Unifier", 1, "commander"),
            Entry("Command Tower", 1, "mainboard"),
        ];

        var builder = new SubmittedDeckStatsBuilder(
            loadDeckAsync: (_, _) => Task.FromResult(new DeckSourceLoadResult(entries.ToList(), null)),
            getCategoriesAsync: (_, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            findCombosAsync: (_, _) => Task.FromResult<CommanderSpellbookResult?>(null),
            analyzeSubmittedDeckAsync: (_, _) => Task.FromResult(CreateAnalysis(
                CreateReport(health),
                new CardGroundingDeckContext
                {
                    CommanderColorIdentity = new HashSet<string>(StringComparer.Ordinal) { "W", "U", "B", "R", "G" },
                    DeckProducedColors = new HashSet<char> { 'W', 'U', 'B', 'R', 'G' },
                    DeckCardNames = new HashSet<string>(StringComparer.Ordinal) { CardNormalizer.Normalize("Command Tower") }
                },
                "Jodah, the Unifier")));

        SubmittedDeckAnalysis result = await builder.BuildAsync("fixture");

        Assert.Equal(expectedScore, result.Stats.Metrics["karsten:health_score"]);
    }

    [Fact]
    public async Task BuildAsync_UnresolvableDeck_OmitsKarstenMetricsAndMarksResolutionDegraded()
    {
        DeckEntry[] entries =
        [
            Entry("Mystery Commander", 1, "commander"),
            Entry("Unknown Spell", 3, "mainboard"),
        ];

        var builder = new SubmittedDeckStatsBuilder(
            loadDeckAsync: (_, _) => Task.FromResult(new DeckSourceLoadResult(entries.ToList(), null)),
            getCategoriesAsync: (_, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            findCombosAsync: (_, _) => Task.FromResult<CommanderSpellbookResult?>(null),
            executeCollectionAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCollectionResponse([], null)
            }),
            searchFallbackCardAsync: (_, _) => Task.FromResult<ScryfallCard?>(null));

        SubmittedDeckAnalysis result = await builder.BuildAsync("fixture");

        Assert.DoesNotContain("karsten:target_lands", result.Stats.Metrics.Keys);
        Assert.DoesNotContain("karsten:land_delta", result.Stats.Metrics.Keys);
        Assert.DoesNotContain("karsten:health_score", result.Stats.Metrics.Keys);
        Assert.True(result.DeckResolutionDegraded);
        Assert.Empty(result.DeckContext.CommanderColorIdentity);
        Assert.Empty(result.DeckContext.DeckProducedColors);
        Assert.Empty(result.DeckContext.DeckCardNames);
        Assert.Null(result.ResolvedCommanderName);
    }

    [Fact]
    public async Task BuildAsync_CollectionRequestFails_OmitsKarstenMetricsAndMarksResolutionDegraded()
    {
        DeckEntry[] entries =
        [
            Entry("Mystery Commander", 1, "commander"),
            Entry("Unknown Spell", 3, "mainboard"),
        ];

        var builder = new SubmittedDeckStatsBuilder(
            loadDeckAsync: (_, _) => Task.FromResult(new DeckSourceLoadResult(entries.ToList(), null)),
            getCategoriesAsync: (_, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            findCombosAsync: (_, _) => Task.FromResult<CommanderSpellbookResult?>(null),
            executeCollectionAsync: (_, _) => Task.FromException<RestResponse<ScryfallCollectionResponse>>(new HttpRequestException()),
            searchFallbackCardAsync: (_, _) => Task.FromResult<ScryfallCard?>(null));

        SubmittedDeckAnalysis result = await builder.BuildAsync("fixture");

        Assert.DoesNotContain("karsten:target_lands", result.Stats.Metrics.Keys);
        Assert.True(result.DeckResolutionDegraded);
    }

    private static DeckEntry Entry(string name, int quantity, string board)
    {
        return new DeckEntry
        {
            Name = name,
            NormalizedName = CardNormalizer.Normalize(name),
            Quantity = quantity,
            Board = board,
        };
    }

    private static SubmittedDeckResolution EmptyAnalysis()
    {
        SubmittedDeckResolution analysis = CreateAnalysis(
            CreateReport(ManabaseHealth.NeedsWork),
            new CardGroundingDeckContext
            {
                CommanderColorIdentity = new HashSet<string>(StringComparer.Ordinal),
                DeckProducedColors = new HashSet<char>(),
                DeckCardNames = new HashSet<string>(StringComparer.Ordinal),
            },
            null);

        return analysis with { HasResolvedDeck = false };
    }

    private static SubmittedDeckResolution CreateAnalysis(
        ManabaseReport report,
        CardGroundingDeckContext deckContext,
        string? resolvedCommanderName)
    {
        return new SubmittedDeckResolution
        {
            Report = report,
            DeckContext = deckContext,
            ResolvedCommanderName = resolvedCommanderName,
            HasResolvedDeck = true,
        };
    }

    private static ManabaseReport CreateReport(ManabaseHealth health)
    {
        IReadOnlyList<ColorSourceFinding> colorFindings = health switch
        {
            ManabaseHealth.Healthy => Array.Empty<ColorSourceFinding>(),
            ManabaseHealth.Functional => Array.Empty<ColorSourceFinding>(),
            ManabaseHealth.Workable => [CreateColorFinding(ManaColor.White, actualSources: 1, requiredSources: 3)],
            _ => [CreateColorFinding(ManaColor.White, actualSources: 0, requiredSources: 4)],
        };

        return new ManabaseReport
        {
            ActualLands = health switch
            {
                ManabaseHealth.Healthy => 37,
                ManabaseHealth.Functional => 35,
                ManabaseHealth.Workable => 37,
                _ => 35,
            },
            TargetLands = 37,
            ColorFindings = colorFindings,
            Mode = ManabaseMode.Casual,
            Castability = Array.Empty<CardCastability>(),
            ColorSpellCounts = new Dictionary<ManaColor, int>(),
            CommanderColors = Array.Empty<ManaColor>(),
            LandTarget = null,
            TapAnalysis = null,
            MulliganEvaluation = null,
            DemandingCards = Array.Empty<DemandingCard>(),
            RampSourceNames = Array.Empty<string>(),
            RampAndDrawNames = Array.Empty<string>(),
            UnsupportedInteractions = Array.Empty<UnsupportedInteraction>(),
            Summary = string.Empty
        };
    }

    private static ColorSourceFinding CreateColorFinding(ManaColor color, double actualSources, int requiredSources)
    {
        return new ColorSourceFinding
        {
            Color = color,
            ActualSources = actualSources,
            RequiredSources = requiredSources,
            DrivingSpell = "Fixture Spell",
            UnderSupportedCount = 0,
            ColorLimitedUnderSupportedCount = 0,
            AverageCastPercent = 100,
            WorstSpellCastPercent = 100,
            WorstSpell = "Fixture Spell",
            DirectSources = actualSources,
            SharedSources = 0,
            ConditionalSources = 0,
            UntappedSources = actualSources
        };
    }

    private static Dictionary<string, ScryfallCard> CreateParityCards()
    {
        return new Dictionary<string, ScryfallCard>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tatyova, Benthic Druid"] = new ScryfallCard(
                "Tatyova, Benthic Druid",
                "{3}{G}{U}",
                "Legendary Creature — Merfolk Druid",
                "Whenever a land enters, draw a card.",
                "3",
                "3",
                [],
                ["G", "U"],
                "tst",
                "Test Set",
                "1",
                null,
                null,
                null,
                null,
                5,
                null,
                "rare"),
            ["Forest"] = new ScryfallCard(
                "Forest",
                null,
                "Basic Land — Forest",
                "({T}: Add {G}.)",
                null,
                null,
                [],
                ["G"],
                "tst",
                "Test Set",
                "2",
                null,
                null,
                null,
                null,
                0,
                ["G"],
                "common"),
            ["Island"] = new ScryfallCard(
                "Island",
                null,
                "Basic Land — Island",
                "({T}: Add {U}.)",
                null,
                null,
                [],
                ["U"],
                "tst",
                "Test Set",
                "3",
                null,
                null,
                null,
                null,
                0,
                ["U"],
                "common"),
            ["Command Tower"] = new ScryfallCard(
                "Command Tower",
                null,
                "Land",
                "{T}: Add one mana of any color in your commander's color identity.",
                null,
                null,
                [],
                ["G", "U"],
                "tst",
                "Test Set",
                "4",
                null,
                null,
                null,
                null,
                0,
                ["G", "U"],
                "common"),
            ["Cultivate"] = new ScryfallCard(
                "Cultivate",
                "{2}{G}",
                "Sorcery",
                "Search your library for up to two basic land cards, reveal those cards, put one onto the battlefield tapped and the other into your hand, then shuffle.",
                null,
                null,
                [],
                ["G"],
                "tst",
                "Test Set",
                "5",
                null,
                null,
                null,
                null,
                3,
                null,
                "common"),
            ["Growth Spiral"] = new ScryfallCard(
                "Growth Spiral",
                "{G}{U}",
                "Instant",
                "Draw a card. You may put a land card from your hand onto the battlefield.",
                null,
                null,
                [],
                ["G", "U"],
                "tst",
                "Test Set",
                "6",
                null,
                null,
                null,
                null,
                2,
                null,
                "common"),
        };
    }

    private static double ToExpectedHealthScore(ManabaseHealth health)
    {
        return health switch
        {
            ManabaseHealth.Healthy => 3,
            ManabaseHealth.Functional => 2,
            ManabaseHealth.Workable => 1,
            _ => 0,
        };
    }

    private static char ToColorChar(ManaColor color)
    {
        return color switch
        {
            ManaColor.White => 'W',
            ManaColor.Blue => 'U',
            ManaColor.Black => 'B',
            ManaColor.Red => 'R',
            ManaColor.Green => 'G',
            _ => '\0',
        };
    }
}
