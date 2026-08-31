using System.Net;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.CreatorStyle;
using DeckFlow.Web.Services.Manabase;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests.Services.CreatorStyle;

public sealed class CreatorStyleDeckAnalysisTests
{
    [Fact]
    public async Task AnalyzeSubmittedDeckAsync_UsesSharedResolverAndCasualSingletonManabasePath()
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

        Dictionary<string, ScryfallCard> cardsByName = SubmittedDeckStatsBuilderTests_CreateParityCards();
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

        SubmittedDeckResolution result = await CreatorStyleDeckAnalysis.AnalyzeSubmittedDeckAsync(
            entries,
            executeCollectionAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCollectionResponse(cardsByName.Values.ToList(), null)
            }),
            searchFallbackCardAsync: (cardName, _) => Task.FromResult(cardsByName.TryGetValue(cardName, out ScryfallCard? card) ? card : null),
            unresolvedCardLogger: static _ => { },
            errorMessageSuffix: "submitted-deck manabase analysis.",
            CancellationToken.None);

        Assert.True(result.HasResolvedDeck);
        Assert.Equal(expectedReport.TargetLands, result.Report.TargetLands);
        Assert.Equal(expectedReport.LandDelta, result.Report.LandDelta);
        Assert.Equal("Tatyova, Benthic Druid", result.ResolvedCommanderName);
        Assert.Equal(["G", "U"], result.DeckContext.CommanderColorIdentity.OrderBy(static color => color, StringComparer.Ordinal).ToArray());
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

    private static Dictionary<string, ScryfallCard> SubmittedDeckStatsBuilderTests_CreateParityCards()
    {
        return new Dictionary<string, ScryfallCard>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tatyova, Benthic Druid"] = CreateCard(
                "Tatyova, Benthic Druid",
                manaCost: "{3}{G}{U}",
                typeLine: "Legendary Creature — Merfolk Druid",
                oracleText: "Whenever a land enters the battlefield under your control, you gain 1 life and draw a card.",
                producedMana: null,
                colorIdentity: ["G", "U"]),
            ["Forest"] = CreateCard("Forest", string.Empty, "Basic Land — Forest", "{T}: Add {G}.", ["G"], ["G"]),
            ["Island"] = CreateCard("Island", string.Empty, "Basic Land — Island", "{T}: Add {U}.", ["U"], ["U"]),
            ["Command Tower"] = CreateCard("Command Tower", string.Empty, "Land", "{T}: Add one mana of any color in your commander's color identity.", ["W", "U", "B", "R", "G"], ["W", "U", "B", "R", "G"]),
            ["Cultivate"] = CreateCard("Cultivate", "{2}{G}", "Sorcery", "Search your library for up to two basic land cards, reveal those cards, put one onto the battlefield tapped and the other into your hand, then shuffle.", null, ["G"]),
            ["Growth Spiral"] = CreateCard("Growth Spiral", "{G}{U}", "Instant", "Draw a card. You may put a land card from your hand onto the battlefield.", null, ["G", "U"]),
        };
    }

    private static ScryfallCard CreateCard(
        string name,
        string manaCost,
        string typeLine,
        string oracleText,
        IReadOnlyList<string>? producedMana,
        IReadOnlyList<string> colorIdentity)
    {
        return new ScryfallCard(
            Name: name,
            ManaCost: manaCost,
            TypeLine: typeLine,
            OracleText: oracleText,
            Power: null,
            Toughness: null,
            Keywords: null,
            ColorIdentity: colorIdentity.ToArray(),
            SetCode: null,
            SetName: null,
            CollectorNumber: null,
            CardFaces: null,
            Id: Guid.NewGuid().ToString("N"),
            Layout: "normal",
            ReleasedAt: null,
            Cmc: 0,
            ProducedMana: producedMana?.ToArray(),
            Rarity: null,
            Legalities: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["commander"] = "legal"
            });
    }
}
