using System.IO;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Services;
using RestSharp;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class ChatGptDeckComparisonServiceTests
{
    [Fact]
    public async Task BuildAsync_ParsesBothDecksAndGeneratesArtifacts()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "MtgDeckStudioComparisonTests", Guid.NewGuid().ToString("N"));
        var service = CreateService(rootPath);

        var result = await service.BuildAsync(new ChatGptDeckComparisonRequest
        {
            WorkflowStep = 2,
            SaveArtifactsToDisk = true,
            DeckAName = "Atraxa Blink",
            DeckABracket = "Upgraded",
            DeckASource = """
Commander
1 Atraxa, Praetors' Voice

Deck
1 Sol Ring
1 Arcane Signet
1 Smothering Tithe
1 Cultivate
""",
            DeckBName = "Tymna Thrasios Midrange",
            DeckBBracket = "Optimized",
            DeckBSource = """
Commander
1 Tymna the Weaver
1 Thrasios, Triton Hero

Deck
1 Sol Ring
1 Arcane Signet
1 Counterspell
1 Wrath of God
"""
        });

        Assert.Contains("Atraxa Blink", result.InputSummary);
        Assert.Contains("Tymna Thrasios Midrange", result.InputSummary);
        Assert.NotNull(result.SavedArtifactsDirectory);
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "00-comparison-input-summary.txt")));
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "10-deck-a-list.txt")));
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "11-deck-b-list.txt")));
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "20-comparison-context.txt")));
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "30-comparison-prompt.txt")));
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "32-comparison-follow-up-prompt.txt")));
    }

    [Fact]
    public async Task BuildAsync_PromptUsesInstructionFirstEvidenceBasedFormat()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckComparisonRequest
        {
            WorkflowStep = 2,
            DeckAName = "Atraxa Blink",
            DeckABracket = "Upgraded",
            DeckASource = """
Commander
1 Atraxa, Praetors' Voice

Deck
1 Sol Ring
1 Arcane Signet
""",
            DeckBName = "Partner Midrange",
            DeckBBracket = "Optimized",
            DeckBSource = """
Commander
1 Tymna the Weaver
1 Thrasios, Triton Hero

Deck
1 Sol Ring
1 Counterspell
"""
        });

        var taskIndex = result.ComparisonPromptText.IndexOf("### Task", StringComparison.Ordinal);
        var rulesIndex = result.ComparisonPromptText.IndexOf("### Rules", StringComparison.Ordinal);
        var deckAIndex = result.ComparisonPromptText.IndexOf("### Deck A", StringComparison.Ordinal);
        var deckBIndex = result.ComparisonPromptText.IndexOf("### Deck B", StringComparison.Ordinal);
        var contextIndex = result.ComparisonPromptText.IndexOf("### Comparison Context", StringComparison.Ordinal);

        Assert.True(taskIndex >= 0);
        Assert.True(rulesIndex > taskIndex);
        Assert.True(deckAIndex > rulesIndex);
        Assert.True(deckBIndex > deckAIndex);
        Assert.True(contextIndex > deckBIndex);
        Assert.Contains("Treat the supplied decklists, commander names, bracket selections, combo findings, and derived comparison context as the source of truth.", result.ComparisonPromptText);
        Assert.Contains("If a conclusion is not well-supported by the provided deck contents, say that explicitly instead of guessing.", result.ComparisonPromptText);
        Assert.Contains("When uncertain, mark the statement as low-confidence and add the reason to confidence_notes.", result.ComparisonPromptText);
        Assert.Contains("Return valid JSON only inside the fenced block.", result.ComparisonPromptText);
        Assert.Contains("Do not omit required fields.", result.ComparisonPromptText);
        Assert.Contains("Name: Atraxa Blink", result.ComparisonPromptText);
        Assert.Contains("Commander: Atraxa, Praetors' Voice", result.ComparisonPromptText);
        Assert.Contains("Bracket: Bracket 3: Upgraded", result.ComparisonPromptText);
        Assert.Contains("Normalized decklist:", result.ComparisonPromptText);
        Assert.Contains("Combo summary:", result.ComparisonPromptText);
        Assert.Contains("\"deck_a_gameplan\": \"\"", result.ComparisonPromptText);
        Assert.Contains("\"mana_consistency_comparison\": \"\"", result.ComparisonPromptText);
        Assert.Contains("commander_bracket_definitions:", result.ComparisonContextText);
        Assert.Contains("combo_gap:", result.ComparisonContextText);
        Assert.Contains("Preserve the original comparison structure.", result.FollowUpPromptText);
    }

    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenDeckAMissing()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckComparisonRequest
        {
            WorkflowStep = 2,
            DeckBSource = "Commander\n1 Tymna the Weaver"
        }));

        Assert.Equal("Deck A URL or deck text is required.", exception.Message);
    }

    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenDeckBMissing()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckComparisonRequest
        {
            WorkflowStep = 2,
            DeckASource = "Commander\n1 Atraxa, Praetors' Voice"
        }));

        Assert.Equal("Deck B URL or deck text is required.", exception.Message);
    }

    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenDeckABracketMissing()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckComparisonRequest
        {
            WorkflowStep = 2,
            DeckASource = "Commander\n1 Atraxa, Praetors' Voice",
            DeckBSource = "Commander\n1 Tymna the Weaver",
            DeckBBracket = "Core"
        }));

        Assert.Equal("Choose a Commander bracket for Deck A before generating the comparison packet.", exception.Message);
    }

    [Fact]
    public async Task BuildAsync_SurfacesParseFailureForSingleSide()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckComparisonRequest
        {
            WorkflowStep = 2,
            DeckABracket = "Upgraded",
            DeckASource = """
Commander
1 Atraxa, Praetors' Voice

Deck
1 Sol Ring
""",
            DeckBBracket = "Core",
            DeckBSource = "this is not a deck list"
        }));

        Assert.StartsWith("Deck B parse failed:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_AcceptsReturnedComparisonJson()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckComparisonRequest
        {
            WorkflowStep = 3,
            DeckABracket = "Upgraded",
            DeckASource = """
Commander
1 Atraxa, Praetors' Voice

Deck
1 Sol Ring
1 Arcane Signet
""",
            DeckBBracket = "Optimized",
            DeckBSource = """
Commander
1 Tymna the Weaver
1 Thrasios, Triton Hero

Deck
1 Sol Ring
1 Counterspell
""",
            ComparisonResponseJson = """
```json
{
  "deck_comparison": {
    "deck_a_name": "Atraxa Blink",
    "deck_b_name": "Partner Midrange",
    "deck_a_commander": "Atraxa, Praetors' Voice",
    "deck_b_commander": "Tymna the Weaver",
    "deck_a_gameplan": "Build a value-oriented battlefield and snowball through permanent-based advantage.",
    "deck_b_gameplan": "Trade resources efficiently and win through compact value engines.",
    "deck_a_bracket": "Bracket 3: Upgraded",
    "deck_b_bracket": "Bracket 4: Optimized",
    "shared_themes": ["Value engines", "Cheap acceleration"],
    "major_differences": ["Deck A is more snowball-oriented", "Deck B is more reactive"],
    "deck_a_strengths": ["Board presence"],
    "deck_b_strengths": ["Interaction density"],
    "deck_a_weaknesses": ["Softer to stack interaction"],
    "deck_b_weaknesses": ["Less battlefield pressure"],
    "speed_comparison": "Deck B starts affecting the stack earlier.",
    "resilience_comparison": "Deck A rebuilds better from creature attrition.",
    "interaction_comparison": "Deck B has the cleaner answer suite.",
    "mana_consistency_comparison": "Deck B has the smoother early mana requirements.",
    "closing_power_comparison": "Deck A closes faster once established.",
    "combo_comparison": "Deck A has the clearer deterministic combo finish.",
    "overall_verdict": "Deck A is stronger in a vacuum; Deck B is better into unknown pods.",
    "key_gap_cards_or_packages": ["Smothering Tithe package", "Counterspell suite", "Board wipes", "Blink engine", "Partner card advantage"],
    "deck_a_key_combos": ["Atraxa, Praetors' Voice + Sol Ring -> mana snowball"],
    "deck_b_key_combos": ["Tymna the Weaver + Thrasios, Triton Hero -> partner value loop"],
    "recommended_for": {
      "deck_a": ["Battlecruiser tables"],
      "deck_b": ["Interactive mid-power pods"]
    },
    "confidence_notes": ["Based on partial role inference from the lists."]
  }
}
```
"""
        });

        Assert.NotNull(result.ComparisonResponse);
        Assert.Equal("Atraxa Blink", result.ComparisonResponse!.DeckAName);
        Assert.Equal("Partner Midrange", result.ComparisonResponse.DeckBName);
        Assert.Contains("value-oriented battlefield", result.ComparisonResponse.DeckAGameplan, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Bracket 3: Upgraded", result.ComparisonResponse.DeckABracket);
        Assert.Contains("Value engines", result.ComparisonResponse.SharedThemes);
        Assert.Contains("deterministic combo", result.ComparisonResponse.ComboComparison, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smoother early mana", result.ComparisonResponse.ManaConsistencyComparison, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Battlecruiser tables", result.ComparisonResponse.RecommendedFor.DeckA);
    }

    private static ChatGptDeckComparisonService CreateService(string? rootPath = null)
    {
        var contentRootPath = rootPath ?? Path.Combine(Path.GetTempPath(), "MtgDeckStudioComparisonTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRootPath);

        return new ChatGptDeckComparisonService(
            new FakeMoxfieldDeckImporter(),
            new FakeArchidektDeckImporter(),
            new MoxfieldParser(),
            new ArchidektParser(),
            new FakeCommanderSpellbookService(),
            new FakeWebHostEnvironment(contentRootPath),
            executeCollectionAsync: (request, _) => Task.FromResult(CreateCollectionResponse(request)),
            artifactsPath: contentRootPath);
    }

    private static RestResponse<ScryfallCollectionResponse> CreateCollectionResponse(RestRequest request)
    {
        return new RestResponse<ScryfallCollectionResponse>(request)
        {
            StatusCode = HttpStatusCode.OK,
            Data = new ScryfallCollectionResponse(GetDefaultCards().ToList(), [])
        };
    }

    private static IReadOnlyList<ScryfallCard> GetDefaultCards() =>
    [
        new("Atraxa, Praetors' Voice", "{G}{W}{U}{B}", "Legendary Creature — Phyrexian Angel Horror", "Flying, vigilance, deathtouch, lifelink. At the beginning of your end step, proliferate.", "4", "4", ["Flying"], ["G", "W", "U", "B"], null, null, null),
        new("Tymna the Weaver", "{1}{W}{B}", "Legendary Creature — Human Cleric", "Lifelink\nAt the beginning of your postcombat main phase, you may pay X life. If you do, draw X cards.", "2", "2", ["Lifelink"], ["W", "B"], null, null, null),
        new("Thrasios, Triton Hero", "{G/U}", "Legendary Creature — Merfolk Wizard", "{4}: Scry 1, then reveal the top card of your library. If it's a land card, put it onto the battlefield tapped. Otherwise, draw a card.", "1", "3", [], ["G", "U"], null, null, null),
        new("Sol Ring", "{1}", "Artifact", "{T}: Add {C}{C}.", null, null, [], [], null, null, null),
        new("Arcane Signet", "{2}", "Artifact", "{T}: Add one mana of any color in your commander's color identity.", null, null, [], [], null, null, null),
        new("Smothering Tithe", "{3}{W}", "Enchantment", "Whenever an opponent draws a card, that player may pay {2}. If the player doesn't, you create a Treasure token.", null, null, ["Treasure"], ["W"], null, null, null),
        new("Cultivate", "{2}{G}", "Sorcery", "Search your library for up to two basic land cards, reveal those cards, put one onto the battlefield tapped and the other into your hand, then shuffle.", null, null, [], ["G"], null, null, null),
        new("Counterspell", "{U}{U}", "Instant", "Counter target spell.", null, null, [], ["U"], null, null, null),
        new("Wrath of God", "{2}{W}{W}", "Sorcery", "Destroy all creatures. They can't be regenerated.", null, null, [], ["W"], null, null, null)
    ];

    private sealed class FakeMoxfieldDeckImporter : IMoxfieldDeckImporter
    {
        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<DeckEntry>());
    }

    private sealed class FakeArchidektDeckImporter : IArchidektDeckImporter
    {
        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<DeckEntry>());
    }

    private sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MtgDeckStudio.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeCommanderSpellbookService : ICommanderSpellbookService
    {
        public Task<CommanderSpellbookResult?> FindCombosAsync(IReadOnlyList<DeckEntry> entries, CancellationToken cancellationToken = default)
        {
            var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Contains("Atraxa, Praetors' Voice"))
            {
                return Task.FromResult<CommanderSpellbookResult?>(new CommanderSpellbookResult(
                    [
                        new SpellbookCombo(
                            ["Atraxa, Praetors' Voice", "Sol Ring"],
                            ["Infinite value setup"],
                            "Test combo instructions.")
                    ],
                    [
                        new SpellbookAlmostCombo(
                            "Doubling Season",
                            ["Atraxa, Praetors' Voice"],
                            ["Planeswalker burst"],
                            "Test almost-combo instructions.")
                    ]));
            }

            if (names.Contains("Tymna the Weaver"))
            {
                return Task.FromResult<CommanderSpellbookResult?>(new CommanderSpellbookResult(
                    [
                        new SpellbookCombo(
                            ["Tymna the Weaver", "Thrasios, Triton Hero"],
                            ["Value engine"],
                            "Partner test combo.")
                    ],
                    []));
            }

            return Task.FromResult<CommanderSpellbookResult?>(null);
        }
    }
}
