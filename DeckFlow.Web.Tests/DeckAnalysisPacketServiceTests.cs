using System.Net;
using System.IO;
using System.Reflection;
using System.Text;
using DeckFlow.Core.Bracket;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Models;
using DeckFlow.Core.Parsing;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Bracket;
using DeckFlow.Web.Services.FeatureFlags;
using DeckFlow.Web.Services.PromptBuilders.Analysis;
using DeckFlow.Web.Services.PromptBuilders.SetUpgrade;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.Extensions.Logging.Abstractions;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Covers staged prompt generation, validation, and artifact output for the deck-analysis
/// workflow served by <see cref="DeckAnalysisPacketService"/> across all supported AI platforms.
/// </summary>
public sealed partial class DeckAnalysisPacketServiceTests
{
    /// <summary>
    /// Builds the deck summary and schema from pasted deck text on the setup step.
    /// </summary>
    [Fact]
    public async Task BuildAsync_GeneratesSummaryAndSchema_ForPastedDeckText()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 1,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
"""
        });

        Assert.Contains("Atraxa, Praetors' Voice", result.InputSummary);
        Assert.Equal("Atraxa, Praetors' Voice | AI Deck Analysis", result.SuggestedChatTitle);
        Assert.Contains("\"game_plan\"", result.DeckProfileSchemaJson);
    }

    [Fact]
    public async Task BuildAsync_UsesDeckNameForSuggestedTitleAndSummaryTitleLine_WhenPresent()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            DeckName = "  My Brew  ",
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
"""
        });

        Assert.Equal("My Brew | AI Deck Analysis", result.SuggestedChatTitle);
        Assert.StartsWith("Deck: My Brew\n\n", result.InputSummary.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSuggestedChatTitle_PrefersDeckNameThenCommanderThenDefault()
    {
        Assert.Equal(
            "Deck Name | AI Deck Analysis",
            InvokeBuildSuggestedChatTitle(new DeckAnalysisRequest { DeckName = "  Deck Name  " }, "Atraxa, Praetors' Voice"));
        Assert.Equal(
            "Atraxa, Praetors' Voice | AI Deck Analysis",
            InvokeBuildSuggestedChatTitle(new DeckAnalysisRequest { DeckName = "   " }, "  Atraxa, Praetors' Voice "));
        Assert.Equal(
            "Commander Deck | AI Deck Analysis",
            InvokeBuildSuggestedChatTitle(new DeckAnalysisRequest { DeckName = "   " }, "   "));
    }

    [Fact]
    public void BuildAnalysisSummaryFromSavedJson_PrependsDeckTitleLine_UsingCommanderFallback()
    {
        var withCommander = InvokeBuildAnalysisSummaryFromSavedJson(new DeckAnalysisResponse
        {
            Commander = "  Atraxa, Praetors' Voice ",
            Format = "Commander",
            GamePlan = "Value",
            Speed = "Medium"
        });
        var withoutCommander = InvokeBuildAnalysisSummaryFromSavedJson(new DeckAnalysisResponse
        {
            Commander = "   ",
            Format = "Commander"
        });

        Assert.StartsWith("Deck: Atraxa, Praetors' Voice\n\n", withCommander.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.StartsWith("Deck: Commander Deck\n\n", withoutCommander.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>
    /// Keeps commander, decklist, sideboard, and maybeboard sections distinct in the first prompt.
    /// </summary>
    [Fact]
    public async Task BuildAsync_SeparatesCommanderDecklistAndPossibleIncludesInInitialProbePrompt()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 1,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

Deck
1 Sol Ring
1 Arcane Signet

Sideboard
1 Swords to Plowshares

Maybeboard
1 Smothering Tithe
"""
        });

        Assert.Contains("Main deck cards: 2", result.InputSummary);
        Assert.Contains("Commander cards: 1", result.InputSummary);
        Assert.Contains("Sideboard cards: 1", result.InputSummary);
        Assert.Contains("Maybeboard cards: 1", result.InputSummary);
    }

    /// <summary>
    /// Builds the reference and analysis packets after probe JSON is supplied.
    /// </summary>
    [Fact]
    public async Task BuildAsync_GeneratesReferenceAndAnalysis_WhenProbeJsonProvided()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses", "consistency", "card-worth-it"],
            CardSpecificQuestionCardNames = ["Sol Ring"]
        });

        Assert.NotNull(result.ReferenceText);
        Assert.Contains("Dockside Extortionist", result.ReferenceText);
        Assert.Contains("Proliferate", result.ReferenceText);
        Assert.Contains("Sol Ring", result.ReferenceText);
        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Do not recommend cards from the official Commander banned list", result.AnalysisPromptText);
        Assert.Contains("Commander bracket definitions:", result.AnalysisPromptText);
        Assert.Contains("Bracket 1: Exhibition", result.AnalysisPromptText);
        Assert.Contains("Bracket 5: cEDH", result.AnalysisPromptText);
        Assert.Contains("Dockside Extortionist", result.AnalysisPromptText);
        Assert.Contains("Read all supplied card entries before beginning the analysis", result.AnalysisPromptText);
        Assert.Contains("1. What are the strengths and weaknesses of this deck?", result.AnalysisPromptText);
        Assert.Contains("2. How consistent is this deck?", result.AnalysisPromptText);
        Assert.Contains("3. Is Sol Ring worth including in this deck?", result.AnalysisPromptText);
        Assert.Contains("Start with a section titled Requested Question Answers.", result.AnalysisPromptText);
        Assert.Contains("Answer every question using the same numbering", result.AnalysisPromptText);
        Assert.Contains("label it as an inference", result.AnalysisPromptText);
        Assert.Contains("Top Adds", result.AnalysisPromptText);
        Assert.Contains("Top Cuts", result.AnalysisPromptText);
        Assert.Contains("reasoning per card", result.AnalysisPromptText);
        Assert.Contains("Bracket 3: Upgraded", result.AnalysisPromptText);
        Assert.Contains("Expect to play at least six turns before you win or lose.", result.AnalysisPromptText);
        Assert.Contains("```json", result.AnalysisPromptText);
        Assert.Contains("\"question_answers\"", result.DeckProfileSchemaJson);
        Assert.Contains("\"question_number\"", result.DeckProfileSchemaJson);
        Assert.Contains("\"basis\": \"authoritative|inference|mixed\"", result.DeckProfileSchemaJson);
    }

    [Fact]
    public async Task BuildAsync_IncludesBracketAssessmentQuestionAndDefinitions_WhenSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["bracket-assessment"]
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("1. Based on the provided Commander bracket definitions, what bracket is this deck closest to and why?", result.AnalysisPromptText);
        Assert.Contains("Commander bracket definitions:", result.AnalysisPromptText);
        Assert.Contains("Bracket 1: Exhibition", result.AnalysisPromptText);
        Assert.Contains("Bracket 2: Core", result.AnalysisPromptText);
        Assert.Contains("Bracket 3: Upgraded", result.AnalysisPromptText);
        Assert.Contains("Bracket 4: Optimized", result.AnalysisPromptText);
        Assert.Contains("Bracket 5: cEDH", result.AnalysisPromptText);
    }

    [Fact]
    public async Task BuildAsync_GeneratesReferenceAndAnalysis_WithoutProbeJson()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        });

        Assert.NotNull(result.ReferenceText);
        Assert.Contains("[current_deck] Atraxa, Praetors' Voice", result.ReferenceText);
        Assert.Contains("[current_deck] Sol Ring", result.ReferenceText);
        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Read all supplied card entries before beginning the analysis", result.AnalysisPromptText);
    }

    [Fact]
    public async Task BuildAsync_IncludesOptionalCandidateBoardsInAnalysis_WhenSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

Deck
1 Sol Ring
1 Arcane Signet

Sideboard
1 Swords to Plowshares

Maybeboard
1 Smothering Tithe
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses"],
            IncludeCandidateReferencesInAnalysis = true
        });

        Assert.NotNull(result.ReferenceText);
        Assert.Contains("[candidate_include:sideboard] Swords to Plowshares", result.ReferenceText);
        Assert.Contains("[candidate_include:maybeboard] Smothering Tithe", result.ReferenceText);
        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Possible Includes", result.AnalysisPromptText);
        Assert.Contains("1 Swords to Plowshares", result.AnalysisPromptText);
        Assert.Contains("1 Smothering Tithe", result.AnalysisPromptText);
        // Opted in: the reference legend and the candidate_include evidence rule are present.
        Assert.Contains("[candidate_include:sideboard] and [candidate_include:maybeboard] = optional candidates only", result.ReferenceText);
        Assert.Contains("Cards labeled candidate_include", result.AnalysisPromptText);
    }

    [Fact]
    public async Task BuildAsync_ExcludesOptionalCandidateBoardsInAnalysis_WhenNotSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

Deck
1 Sol Ring
1 Arcane Signet

Sideboard
1 Swords to Plowshares

Maybeboard
1 Smothering Tithe
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        });

        Assert.NotNull(result.ReferenceText);
        Assert.DoesNotContain("[candidate_include:sideboard] Swords to Plowshares", result.ReferenceText);
        Assert.DoesNotContain("[candidate_include:maybeboard] Smothering Tithe", result.ReferenceText);
        Assert.NotNull(result.AnalysisPromptText);
        Assert.DoesNotContain("Possible Includes\n1 Swords to Plowshares", result.AnalysisPromptText.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain("Swords to Plowshares", result.AnalysisPromptText);
        Assert.DoesNotContain("Smothering Tithe", result.AnalysisPromptText);
        // Opted out: no candidate_include legend or evidence rule anywhere in the prompt or reference.
        Assert.DoesNotContain("candidate_include", result.ReferenceText);
        Assert.DoesNotContain("candidate_include", result.AnalysisPromptText);
    }

    [Theory]
    [InlineData("ChatGPT")]
    [InlineData("Claude")]
    [InlineData("Gemini")]
    public async Task BuildAsync_GatesCandidateIncludeRule_PerPlatform(string platform)
    {
        var service = CreateService();
        const string deckSource = """
Commander
1 Atraxa, Praetors' Voice

Deck
1 Sol Ring
1 Arcane Signet

Sideboard
1 Swords to Plowshares

Maybeboard
1 Smothering Tithe
""";

        var optInResult = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = deckSource,
            TargetCommanderBracket = "Upgraded",
            TargetAiPlatform = platform,
            SelectedAnalysisQuestions = ["strengths-weaknesses"],
            IncludeCandidateReferencesInAnalysis = true
        });

        var optOutResult = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = deckSource,
            TargetCommanderBracket = "Upgraded",
            TargetAiPlatform = platform,
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        });

        // Each platform's prompt variant emits the candidate_include rule only when opted in.
        Assert.Contains("candidate_include", optInResult.AnalysisPromptText);
        Assert.DoesNotContain("candidate_include", optOutResult.AnalysisPromptText);
    }

    [Fact]
    public async Task BuildAsync_DoesNotGenerateAnalysis_WhenOnSetupStep()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 1,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
"""
        });

        Assert.NotNull(result.InputSummary);
        Assert.Null(result.AnalysisPromptText);
    }

    [Fact]
    public async Task BuildAsync_RendersAnalysisSummaryWithoutAnalysisDependencies_WhenOnResultsStep()
    {
        var service = CreateService(
            executeCollectionAsync: (_, _) => throw new InvalidOperationException("Scryfall lookup should not run for Step 3."));

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 3,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            DeckProfileJson = """
{
  "format": "Commander",
  "commander": "Atraxa, Praetors' Voice",
  "game_plan": "Midrange value",
  "primary_axes": ["counters"],
  "speed": "medium",
  "strengths": ["Resilient board presence"],
  "weaknesses": [],
  "deck_needs": [],
  "weak_slots": [],
  "synergy_tags": []
}
"""
        });

        Assert.NotNull(result.AnalysisResponse);
        Assert.Equal("Atraxa, Praetors' Voice", result.AnalysisResponse!.Commander);
        Assert.Null(result.AnalysisPromptText);
        Assert.Null(result.ReferenceText);
        Assert.Null(result.SetUpgradePromptText);
    }

    /// <summary>
    /// Requires at least one selected analysis question before the analysis packet can be generated.
    /// </summary>
    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenQuestionsMissingForAnalysisStep()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded"
        }));

        Assert.Equal("Select at least one analysis question before generating the analysis packet.", exception.Message);
    }

    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenDeckProfileJsonDoesNotMatchSchema()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 3,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            DeckProfileJson = """
{
  "foo": "bar"
}
"""
        }));

        Assert.Equal("The submitted AI response did not contain a valid deck_profile payload.", exception.Message);
    }

    [Fact]
    public async Task BuildAsync_RendersAnalysisResponse_FromDeckProfileJsonWithoutDeckSource()
    {
        var service = CreateService(
            executeCollectionAsync: (_, _) => throw new InvalidOperationException("Scryfall lookup should not run for saved Step 3 JSON."));

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 3,
            DeckProfileJson = """
{
  "deck_profile": {
    "format": "Commander",
    "commander": "Atraxa, Praetors' Voice",
    "game_plan": "Midrange value",
    "primary_axes": ["counters", "value"],
    "speed": "medium",
    "strengths": ["Resilient board presence"],
    "weaknesses": ["Mana base is slow"],
    "deck_needs": [],
    "weak_slots": [],
    "synergy_tags": ["proliferate"],
    "question_answers": [],
    "deck_versions": []
  }
}
"""
        });

        Assert.NotNull(result.AnalysisResponse);
        Assert.Equal("Atraxa, Praetors' Voice", result.AnalysisResponse!.Commander);
        Assert.Contains("Commander: Atraxa, Praetors' Voice", result.InputSummary);
        Assert.Equal("Atraxa, Praetors' Voice | AI Deck Analysis", result.SuggestedChatTitle);
        Assert.Null(result.AnalysisPromptText);
        Assert.Null(result.ReferenceText);
    }

    [Fact]
    public async Task BuildAsync_RendersSetUpgradeResponse_FromResponseJsonWithoutDeckSource()
    {
        var service = CreateService(
            executeCollectionAsync: (_, _) => throw new InvalidOperationException("Scryfall lookup should not run for saved Step 5 JSON."));

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 5,
            SetUpgradeResponseJson = """
```json
{
  "set_upgrade_report": {
    "sets": [
      {
        "set_code": "ABC",
        "set_name": "Alpha Beta Core",
        "top_adds": [
          { "card": "New Ramp", "reason": "Faster turn 2", "suggested_cut": "Old Ramp", "cut_reason": "Slower" }
        ],
        "traps": [
          { "card": "Shiny Trap", "reason": "Looks great, under-delivers" }
        ],
        "speculative_tests": [
          { "card": "Unproven Card", "reason": "Worth testing" }
        ]
      }
    ],
    "final_shortlist": {
      "must_test": [
        {
          "card": "New Ramp",
          "reason": "Immediate upgrade for early acceleration.",
          "suggested_cut": "Old Ramp",
          "cut_reason": "Costs more mana for less burst."
        }
      ],
      "optional": [
        {
          "card": "Unproven Card",
          "reason": "High upside if the table slows down.",
          "suggested_cut": "Flex Slot",
          "cut_reason": "Lowest-impact filler when testing."
        }
      ],
      "skip": ["Shiny Trap"]
    }
  }
}
```
"""
        });

        Assert.NotNull(result.SetUpgradeResponse);
        Assert.Single(result.SetUpgradeResponse!.Sets);
        var set = result.SetUpgradeResponse.Sets[0];
        Assert.Equal("ABC", set.SetCode);
        Assert.Equal("Alpha Beta Core", set.SetName);
        Assert.Single(set.TopAdds);
        Assert.Equal("New Ramp", set.TopAdds[0].Card);
        Assert.Equal("Old Ramp", set.TopAdds[0].SuggestedCut);
        Assert.Single(set.Traps);
        Assert.Single(set.SpeculativeTests);
        Assert.NotNull(result.SetUpgradeResponse.FinalShortlist);
        Assert.Contains(result.SetUpgradeResponse.FinalShortlist!.MustTest, entry => entry.Card == "New Ramp");
        Assert.Contains("Shiny Trap", result.SetUpgradeResponse.FinalShortlist.Skip);
        Assert.Null(result.SetUpgradePromptText);
    }

    [Fact]
    public async Task BuildAsync_Throws_WhenSetUpgradeJsonIsMissingReport()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 5,
            SetUpgradeResponseJson = "{ \"unrelated\": 123 }"
        }));

        Assert.Contains("set_upgrade_report", exception.Message);
    }

    /// <summary>
    /// Requires a card name when the selected questions are card-specific.
    /// </summary>
    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenCardSpecificQuestionMissingCardName()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["card-worth-it"]
        }));

        Assert.Equal("Enter at least one card name for the selected card-specific analysis questions.", exception.Message);
    }

    /// <summary>
    /// Requires a budget amount when the selected questions include budget upgrades.
    /// </summary>
    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenBudgetQuestionMissingBudgetAmount()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["budget-upgrades"]
        }));

        Assert.Equal("Enter a budget amount for the selected budget upgrade question.", exception.Message);
    }

    [Fact]
    public async Task BuildAsync_ReplacesBudgetPlaceholder_WhenBudgetQuestionSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["budget-upgrades"],
            BudgetUpgradeAmount = "50"
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("What are the best upgrades under $50 budget?", result.AnalysisPromptText);
    }

    [Fact]
    public async Task BuildAsync_ExpandsCardPlaceholderQuestions_ForEveryProvidedCard()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["card-worth-it", "better-alternatives", "strengths-weaknesses"],
            CardSpecificQuestionCardNames = ["Sol Ring", "Arcane Signet", "Swords to Plowshares"]
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("1. What are the strengths and weaknesses of this deck?", result.AnalysisPromptText);
        Assert.Contains("2. Is Sol Ring worth including in this deck?", result.AnalysisPromptText);
        Assert.Contains("3. Is Arcane Signet worth including in this deck?", result.AnalysisPromptText);
        Assert.Contains("4. Is Swords to Plowshares worth including in this deck?", result.AnalysisPromptText);
        Assert.Contains("5. What are better alternatives to Sol Ring?", result.AnalysisPromptText);
        Assert.Contains("6. What are better alternatives to Arcane Signet?", result.AnalysisPromptText);
        Assert.Contains("7. What are better alternatives to Swords to Plowshares?", result.AnalysisPromptText);
    }

    [Fact]
    public async Task BuildAsync_RequiresFullDecklists_WhenDeckVersionQuestionsSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["bracket-3-version", "three-upgrade-paths"]
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Create a Bracket 3 version of this deck.", result.AnalysisPromptText);
        Assert.Contains("Create 3 different upgrade-path versions of this deck.", result.AnalysisPromptText);
        Assert.Contains("output the full 100-card Commander decklist", result.AnalysisPromptText);
        Assert.Contains("1 commander and 99 other cards", result.AnalysisPromptText);
        Assert.Contains("clearly labeled ```text fenced code block", result.AnalysisPromptText);
    }

    /// <summary>
    /// Bracket 2 version question triggers the full 100-card list instruction, same as bracket 3/4/5.
    /// </summary>
    [Fact]
    public async Task BuildAsync_RequiresFullDecklists_WhenBracket2VersionQuestionSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["bracket-2-version"]
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Create a Bracket 2 version of this deck.", result.AnalysisPromptText);
        Assert.Contains("output the full 100-card Commander decklist", result.AnalysisPromptText);
    }

    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenCategoryQuestionSelectedWithoutExportFormat()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["add-categories"]
        }));

        Assert.Equal("Choose Moxfield or Archidekt as the export format when assigning or updating categories — plain text does not support inline category formatting.", exception.Message);
    }

    [Fact]
    public async Task BuildAsync_RequiresTextCodeBlock_WhenCategoryQuestionSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["add-categories"],
            DecklistExportFormat = "moxfield"
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Return the categorized decklist only inside a fenced ```text code block", result.AnalysisPromptText);
        Assert.Contains("Format for Moxfield bulk edit", result.AnalysisPromptText);
    }

    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenFallbackCommanderIsNotCommanderEligible()
    {
        var service = CreateService(
            executeSearchAsync: (request, _) =>
            {
                var query = request.Parameters.FirstOrDefault(parameter => parameter.Name?.ToString() == "q")?.Value?.ToString() ?? string.Empty;
                var cards = query.Contains("Aerith's Curaga Magic", StringComparison.Ordinal)
                    ? new[]
                    {
                        new ScryfallCard(
                            "Aerith's Curaga Magic",
                            "{1}{G}",
                            "Instant",
                            "Prevent all damage that would be dealt to target creature this turn.",
                            null,
                            null,
                            [],
                            ["G"],
                            "sld",
                            "Secret Lair Drop",
                            "1872")
                    }
                    : Array.Empty<ScryfallCard>();

                return Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallSearchResponse(cards.ToList())
                });
            },
            executeNamedAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallCard>(request)
            {
                StatusCode = HttpStatusCode.NotFound
            }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new DeckAnalysisRequest
        {
            DeckName = "Earthfall",
            DeckSource = """
1 Aerith's Curaga Magic (SLD) 1872 #ProtectEngine #Protection
1 Aftermath Analyst (EOC) 91 #ComboPiece #Graveyard #Mill #Rebuild #Recursion
1 Arid Archway (OTJ) 252 #Land #ManaBase #Utility
"""
        }));

        Assert.Equal("The commander isn't in the deck text. \"Aerith's Curaga Magic\" is not a legal commander by this workflow's rules.", exception.Message);
    }

    /// <summary>
    /// Protected cards list is injected into the analysis prompt when versioning questions are selected.
    /// </summary>
    [Fact]
    public async Task BuildAsync_InjectsProtectedCards_WhenVersioningQuestionsAndProtectedCardsSet()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["bracket-3-version"],
            ProtectedCards = "Sol Ring\nArcane Signet"
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Protected cards:", result.AnalysisPromptText);
        Assert.Contains("Sol Ring", result.AnalysisPromptText);
        Assert.Contains("Arcane Signet", result.AnalysisPromptText);
        Assert.Contains("Keep every protected card in all requested deck versions", result.AnalysisPromptText);
    }

    /// <summary>
    /// Protected cards are NOT injected when no versioning questions are selected.
    /// </summary>
    [Fact]
    public async Task BuildAsync_DoesNotInjectProtectedCards_WhenNoVersioningQuestionsSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses"],
            ProtectedCards = "Sol Ring"
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.DoesNotContain("Protected cards:", result.AnalysisPromptText);
        Assert.DoesNotContain("Keep every protected card", result.AnalysisPromptText);
    }

    /// <summary>
    /// When a Moxfield export has no Commander section header, the first 1-of entry is treated
    /// as the commander so the suggested title and input summary are populated.
    /// </summary>
    [Fact]
    public async Task BuildAsync_DetectsCommanderFromLeadingEntries_WhenNoCommanderSectionHeader()
    {
        var service = CreateService();

        // Mainboard is A-Z sorted as Moxfield exports it: Arcane Signet (A) before Sol Ring (S).
        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            DeckSource = """
1 Atraxa, Praetors' Voice
1 Arcane Signet
1 Sol Ring
"""
        });

        Assert.Contains("Atraxa, Praetors' Voice", result.InputSummary);
        Assert.Equal("Atraxa, Praetors' Voice | AI Deck Analysis", result.SuggestedChatTitle);
    }

    /// <summary>
    /// A single commander followed by an early-alphabet mainboard card does not produce two commanders.
    /// </summary>
    [Fact]
    public async Task BuildAsync_DoesNotTreatFirstMainboardCard_AsSecondCommander_WhenItSortsBeforeThirdEntry()
    {
        var service = CreateService();

        // Tannuk (T) is the commander; Aerith's (A) is the first mainboard card and sorts before
        // Aftermath Analyst (Af) — confirming it belongs to the A-Z sorted mainboard, not as a partner.
        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            DeckSource = """
1 Tannuk, Memorial Ensign
1 Aerith's Curaga Magic
1 Aftermath Analyst
1 Sol Ring
"""
        });

        var summary = result.InputSummary;
        Assert.Contains("Commander: Tannuk, Memorial Ensign", summary);
        Assert.Contains("Commander cards: 1", summary);
    }

    [Fact]
    public async Task BuildAsync_DoesNotLeakCompanionDeckContent_WhenImportCarriesDetectedCompanionMetadata()
    {
        var baselineEntries = CreateCompanionFixtureEntries(includeBackgroundCommander: false);
        var companionImporter = new FakeMoxfieldDeckImporter(
            entries: baselineEntries,
            detectedCompanionName: "Jegantha, the Wellspring");
        var baselineImporter = new FakeMoxfieldDeckImporter(entries: baselineEntries);

        var companionService = CreateService(moxfieldDeckImporter: companionImporter);
        var baselineService = CreateService(moxfieldDeckImporter: baselineImporter);

        var request = new DeckAnalysisRequest
        {
            DeckInputSource = DeckInputSource.PublicUrl,
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-companion",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        };

        var companionResult = await companionService.BuildAsync(request);
        var baselineResult = await baselineService.BuildAsync(request);
        var companionPacketText = FlattenPacketText(companionResult);

        Assert.DoesNotContain("Jegantha, the Wellspring", companionPacketText);
        Assert.Equal(PacketBytes(baselineResult), PacketBytes(companionResult));
    }

    [Fact]
    public async Task BuildAsync_IsByteIdentical_WhenCommanderCastabilityFlagTogglesForCompanionBackgroundDeck()
    {
        var importer = new FakeMoxfieldDeckImporter(
            entries: CreateCompanionFixtureEntries(includeBackgroundCommander: true),
            detectedCompanionName: "Jegantha, the Wellspring");
        var flagOff = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["analysis.manabase.commander-castability"] = false
        });
        var flagOn = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["analysis.manabase.commander-castability"] = true
        });

        var serviceFlagOff = CreateService(moxfieldDeckImporter: importer, flagCache: flagOff);
        var serviceFlagOn = CreateService(moxfieldDeckImporter: importer, flagCache: flagOn);

        var request = new DeckAnalysisRequest
        {
            DeckInputSource = DeckInputSource.PublicUrl,
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-companion-background",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        };

        var offResult = await serviceFlagOff.BuildAsync(request);
        var onResult = await serviceFlagOn.BuildAsync(request);

        Assert.Equal(PacketBytes(offResult), PacketBytes(onResult));
    }

    [Fact]
    public async Task BuildAsync_UsesInjectedClock_ForTimestampIdentity()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        // Each arm uses a separate cache so observed differences come from the shared clock, not packet replay.
        var firstService = CreateService(
            moxfieldDeckImporter: new FakeMoxfieldDeckImporter(entries: CreateCompanionFixtureEntries(includeBackgroundCommander: false)),
            timeProvider: clock);
        var request = new DeckAnalysisRequest
        {
            DeckInputSource = DeckInputSource.PublicUrl,
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-clock",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        };

        var first = await firstService.BuildAsync(request);
        var sameInstant = await CreateService(
            moxfieldDeckImporter: new FakeMoxfieldDeckImporter(entries: CreateCompanionFixtureEntries(includeBackgroundCommander: false)),
            timeProvider: clock).BuildAsync(request);
        clock.Advance(TimeSpan.FromSeconds(1));
        var advanced = await CreateService(
            moxfieldDeckImporter: new FakeMoxfieldDeckImporter(entries: CreateCompanionFixtureEntries(includeBackgroundCommander: false)),
            timeProvider: clock).BuildAsync(request);

        Assert.Equal(PacketBytes(first), PacketBytes(sameInstant));
        Assert.NotEqual(PacketBytes(sameInstant), PacketBytes(advanced));
    }

    /// <summary>
    /// Flag-OFF command-zone awareness must be byte-identical to baseline for ALL THREE AI variants,
    /// even for a companion+Background deck. We do NOT assert the companion name is absent — Archidekt
    /// leaves a companion classified mainboard so it may legitimately appear in the deck text; per-platform
    /// byte-identity alone proves there is no flag-OFF regression (Codex HIGH-1, MED-2).
    /// </summary>
    [Theory]
    [InlineData("ChatGPT")]
    [InlineData("Claude")]
    [InlineData("Gemini")]
    public async Task BuildAsync_IsByteIdentical_WhenCommandZoneAwarenessFlagOff(string targetAiPlatform)
    {
        var importer = new FakeMoxfieldDeckImporter(
            entries: CreateCompanionFixtureEntries(includeBackgroundCommander: true),
            detectedCompanionName: "Jegantha, the Wellspring");
        var flagOff = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["analysis.command-zone-awareness"] = false
        });

        var serviceFlagOff = CreateService(moxfieldDeckImporter: importer, flagCache: flagOff);
        var serviceBaseline = CreateService(moxfieldDeckImporter: importer);

        var request = new DeckAnalysisRequest
        {
            DeckInputSource = DeckInputSource.PublicUrl,
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-command-zone-off",
            TargetCommanderBracket = "Upgraded",
            TargetAiPlatform = targetAiPlatform,
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        };

        var flagOffResult = await serviceFlagOff.BuildAsync(request);
        var baselineResult = await serviceBaseline.BuildAsync(request);

        Assert.Equal(PacketBytes(baselineResult), PacketBytes(flagOffResult));
    }

    /// <summary>
    /// Flag ON: a deck with two command-zone entries (partner pair / commander+Background) names both,
    /// joined "A &amp; B", in the analysis prompt. The existing variant already renders commanderName,
    /// so this passes without the Plan 03 variant work.
    /// </summary>
    [Fact]
    public async Task BuildAsync_CommandZoneAwareness_RendersPartnerPair()
    {
        var importer = new FakeMoxfieldDeckImporter(
            entries: CreateCompanionFixtureEntries(includeBackgroundCommander: true));
        var flagOn = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["analysis.command-zone-awareness"] = true
        });

        var service = CreateService(moxfieldDeckImporter: importer, flagCache: flagOn);

        var request = new DeckAnalysisRequest
        {
            DeckInputSource = DeckInputSource.PublicUrl,
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-command-zone-pair",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        };

        var result = await service.BuildAsync(request);

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Kraum, Ludevic's Opus & Passionate Archaeologist", result.AnalysisPromptText);
    }

    /// <summary>
    /// Flag ON with a single commander must not introduce a spurious " &amp; " — the rendered prompt is
    /// identical to flag-OFF for a solo-commander deck (no companion supplied).
    /// </summary>
    [Fact]
    public async Task BuildAsync_CommandZoneAwareness_SingleCommanderUnchanged()
    {
        var importer = new FakeMoxfieldDeckImporter(
            entries: CreateCompanionFixtureEntries(includeBackgroundCommander: false));
        var flagOn = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["analysis.command-zone-awareness"] = true
        });

        var serviceFlagOn = CreateService(moxfieldDeckImporter: importer, flagCache: flagOn);
        var serviceFlagOff = CreateService(moxfieldDeckImporter: importer);

        var request = new DeckAnalysisRequest
        {
            DeckInputSource = DeckInputSource.PublicUrl,
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-command-zone-solo",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        };

        var onResult = await serviceFlagOn.BuildAsync(request);
        var offResult = await serviceFlagOff.BuildAsync(request);

        Assert.NotNull(onResult.AnalysisPromptText);
        Assert.Contains("Kraum, Ludevic's Opus", onResult.AnalysisPromptText);
        Assert.DoesNotContain("Kraum, Ludevic's Opus &", onResult.AnalysisPromptText);
        Assert.Equal(offResult.AnalysisPromptText, onResult.AnalysisPromptText);
    }

    /// <summary>
    /// Flag ON with a companion present: each variant surfaces the companion in its native format
    /// (ChatGpt/Gemini a <c>companion:</c> DECK CONTEXT line, Claude a <c>&lt;companion&gt;</c> element).
    /// Per Codex HIGH-1 this is awareness-only side metadata: the decklist region of the prompt stays
    /// byte-identical between flag-ON and flag-OFF, proving the deck text is never mutated or filtered.
    /// We do NOT assert the companion is absent from the decklist.
    /// </summary>
    [Fact]
    public async Task BuildAsync_CommandZoneAwareness_RendersCompanion()
    {
        // (platform, the marker proving the companion rendered, the marker where the decklist region begins)
        var platforms = new[]
        {
            ("ChatGPT", "companion: ", "## DECKLIST"),
            ("Gemini", "companion: ", "## DECKLIST"),
            ("Claude", "<companion>", "decklist:"),
        };

        foreach (var (platform, companionMarker, decklistMarker) in platforms)
        {
            var importer = new FakeMoxfieldDeckImporter(
                entries: CreateCompanionFixtureEntries(includeBackgroundCommander: true),
                detectedCompanionName: "Jegantha, the Wellspring");
            var flagOn = new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                ["analysis.command-zone-awareness"] = true
            });

            var serviceFlagOn = CreateService(moxfieldDeckImporter: importer, flagCache: flagOn);
            var serviceFlagOff = CreateService(moxfieldDeckImporter: importer);

            var request = new DeckAnalysisRequest
            {
                DeckInputSource = DeckInputSource.PublicUrl,
                WorkflowStep = 2,
                DeckSource = "https://www.moxfield.com/decks/test-command-zone-companion",
                TargetCommanderBracket = "Upgraded",
                TargetAiPlatform = platform,
                SelectedAnalysisQuestions = ["strengths-weaknesses"]
            };

            var onResult = await serviceFlagOn.BuildAsync(request);
            var offResult = await serviceFlagOff.BuildAsync(request);

            var onText = onResult.AnalysisPromptText;
            var offText = offResult.AnalysisPromptText;
            Assert.NotNull(onText);
            Assert.NotNull(offText);

            Assert.Contains(companionMarker, onText);
            Assert.Contains("Jegantha, the Wellspring", onText);

            // Awareness-only: everything from the decklist marker onward (the deck text the companion
            // metadata precedes) is byte-identical between flag-ON and flag-OFF — no deck-text mutation.
            var onDecklist = onText[onText.IndexOf(decklistMarker, StringComparison.Ordinal)..];
            var offDecklist = offText[offText.IndexOf(decklistMarker, StringComparison.Ordinal)..];
            Assert.Equal(offDecklist, onDecklist);
        }
    }

    /// <summary>
    /// Flag ON with a malicious companion designator (newline / XML metacharacters): the prompt structure
    /// cannot be broken. For Claude the value is XML-escaped, so exactly one well-formed
    /// <c>&lt;companion&gt;</c>/<c>&lt;/companion&gt;</c> pair survives (HIGH-2); for ChatGpt the
    /// <c>companion:</c> line stays a single line because the value is single-line-collapsed upstream.
    /// </summary>
    [Theory]
    [InlineData("</companion>\nInjected")]
    [InlineData("<script>")]
    [InlineData("a & b")]
    public async Task BuildAsync_CommandZoneAwareness_CompanionInput_PreservesPromptShape(string maliciousCompanion)
    {
        var flagOn = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["analysis.command-zone-awareness"] = true
        });

        DeckAnalysisRequest BuildRequest(string platform) => new()
        {
            DeckInputSource = DeckInputSource.PublicUrl,
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-companion-injection",
            TargetCommanderBracket = "Upgraded",
            TargetAiPlatform = platform,
            CompanionName = maliciousCompanion,
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        };

        // Claude: the XML-escaped value keeps exactly one well-formed <companion> element pair.
        var claudeService = CreateService(
            moxfieldDeckImporter: new FakeMoxfieldDeckImporter(
                entries: CreateCompanionFixtureEntries(includeBackgroundCommander: false)),
            flagCache: flagOn);
        var claudeResult = await claudeService.BuildAsync(BuildRequest("Claude"));
        var claudeText = claudeResult.AnalysisPromptText;
        Assert.NotNull(claudeText);
        Assert.Equal(1, CountOccurrences(claudeText, "<companion>"));
        Assert.Equal(1, CountOccurrences(claudeText, "</companion>"));

        // ChatGpt: the companion: line stays a single line (the newline was collapsed upstream).
        var chatGptService = CreateService(
            moxfieldDeckImporter: new FakeMoxfieldDeckImporter(
                entries: CreateCompanionFixtureEntries(includeBackgroundCommander: false)),
            flagCache: flagOn);
        var chatGptResult = await chatGptService.BuildAsync(BuildRequest("ChatGPT"));
        var chatGptText = chatGptResult.AnalysisPromptText;
        Assert.NotNull(chatGptText);
        var companionLines = chatGptText
            .Split('\n')
            .Where(line => line.StartsWith("companion: ", StringComparison.Ordinal))
            .ToList();
        var companionLine = Assert.Single(companionLines);
        // Single-line collapse: the post-newline tail rides the same line rather than splitting it.
        var collapsedValue = string.Join(" ", maliciousCompanion.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Contains(collapsedValue, companionLine);
    }

    /// <summary>
    /// Codex 73 HIGH-1: the session cache key intentionally omits the command-zone flag and companion,
    /// so while the flag is ON the service must report NO cache key (null) — otherwise the controller /
    /// download path could replay a stale flag-OFF packet or a prior companion designator. Flag OFF must
    /// still return a usable key so normal caching is unaffected.
    /// </summary>
    [Fact]
    public async Task TryComputeCacheKeyAsync_ReturnsNull_WhenCommandZoneAwarenessEnabled_AndKey_WhenOff()
    {
        var request = new DeckAnalysisRequest
        {
            DeckInputSource = DeckInputSource.PublicUrl,
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-command-zone-cache-bypass",
            TargetCommanderBracket = "Upgraded",
            TargetAiPlatform = "ChatGPT",
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        };

        var flagOn = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["analysis.command-zone-awareness"] = true
        });

        var serviceFlagOn = CreateService(
            moxfieldDeckImporter: new FakeMoxfieldDeckImporter(
                entries: CreateCompanionFixtureEntries(includeBackgroundCommander: false)),
            flagCache: flagOn);
        var serviceFlagOff = CreateService(
            moxfieldDeckImporter: new FakeMoxfieldDeckImporter(
                entries: CreateCompanionFixtureEntries(includeBackgroundCommander: false)));

        var keyWhenOn = await serviceFlagOn.TryComputeCacheKeyAsync(request, CancellationToken.None);
        var keyWhenOff = await serviceFlagOff.TryComputeCacheKeyAsync(request, CancellationToken.None);

        Assert.Null(keyWhenOn);
        Assert.False(string.IsNullOrEmpty(keyWhenOff));
    }

    /// <summary>
    /// Codex 73 MEDIUM: a lone carriage return (no line feed) must not survive into the rendered
    /// companion line — the shared whitespace collapse only splits on \n, so the companion path
    /// normalizes bare \r first. The companion: line stays single and carries no CR.
    /// </summary>
    [Fact]
    public async Task BuildAsync_CommandZoneAwareness_CompanionInput_StripsBareCarriageReturn()
    {
        var flagOn = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["analysis.command-zone-awareness"] = true
        });

        var service = CreateService(
            moxfieldDeckImporter: new FakeMoxfieldDeckImporter(
                entries: CreateCompanionFixtureEntries(includeBackgroundCommander: false)),
            flagCache: flagOn);

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            DeckInputSource = DeckInputSource.PublicUrl,
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-companion-bare-cr",
            TargetCommanderBracket = "Upgraded",
            TargetAiPlatform = "ChatGPT",
            CompanionName = "Jegantha\rInjected",
            SelectedAnalysisQuestions = ["strengths-weaknesses"]
        });

        var text = result.AnalysisPromptText;
        Assert.NotNull(text);
        // Normalize the prompt's own CRLF line endings away first so the only \r we could observe would
        // be one that leaked from the (bare-CR) companion value into the rendered line.
        var companionLine = Assert.Single(text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => line.StartsWith("companion: ", StringComparison.Ordinal))
            .ToList());
        Assert.DoesNotContain('\r', companionLine);
        // The bare CR is collapsed to a single space — value stays on one line, both halves intact.
        Assert.Contains("Jegantha Injected", companionLine);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
    /// When the first two leading entries are both 1-of, both are treated as partner commanders.
    /// </summary>
    [Fact]
    public async Task BuildAsync_DetectsPartnerCommandersFromLeadingEntries_WhenNoCommanderSectionHeader()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            DeckSource = """
1 Tymna the Weaver
1 Thrasios, Triton Hero
1 Sol Ring
1 Arcane Signet
"""
        });

        Assert.Contains("Tymna the Weaver", result.InputSummary);
        Assert.Equal("Tymna the Weaver | AI Deck Analysis", result.SuggestedChatTitle);
    }

    [Fact]
    public async Task BuildAsync_IncludesBothFacesForDoubleFacedCardsInReferenceText()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Blex, Vexing Pest // Search for Blex
1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["consistency"]
        });

        Assert.NotNull(result.ReferenceText);
        Assert.Contains("Blex, Vexing Pest", result.ReferenceText);
        Assert.Contains("Search for Blex", result.ReferenceText);
        Assert.Contains("Legendary Creature — Pest", result.ReferenceText);
        Assert.Contains("Sorcery", result.ReferenceText);
        Assert.Contains("other Pests", result.ReferenceText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Look at the top five cards of your library", result.ReferenceText);
    }

    [Fact]
    public async Task BuildAsync_UsesAlternatePrintedNameFallback_ForUnknownCardsInReferenceText()
    {
        var service = CreateService(
            executeCollectionAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCollectionResponse([], [new ScryfallCollectionNameIdentifier("Ya viene el coco")])
            }),
            executeSearchAsync: (request, _) =>
            {
                var query = request.Parameters.First(parameter => parameter.Name?.ToString() == "q").Value?.ToString() ?? string.Empty;
                var cards = query == "Ya viene el coco"
                    ? new[]
                    {
                        new ScryfallCard(
                            "Perfect Defense // Denting Blows",
                            null,
                            "Instant // Sorcery",
                            null,
                            null,
                            null,
                            [],
                            ["W", "R"],
                            "who",
                            "Doctor Who",
                            "200",
                            [
                                new ScryfallCardFace(
                                    "Perfect Defense",
                                    "{1}{W}",
                                    "Instant",
                                    "Prevent all combat damage that would be dealt this turn.",
                                    null,
                                    null),
                                new ScryfallCardFace(
                                    "Denting Blows",
                                    "{2}{R}",
                                    "Sorcery",
                                    "Denting Blows deals 4 damage to target creature.",
                                    null,
                                    null)
                            ])
                    }
                    : Array.Empty<ScryfallCard>();

                return Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallSearchResponse(cards.ToList())
                });
            });

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Ya viene el coco
1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["consistency"]
        });

        Assert.NotNull(result.ReferenceText);
        Assert.Contains("submitted_name: Ya viene el coco | resolved_card: Perfect Defense // Denting Blows", result.ReferenceText);
        Assert.Contains("Perfect Defense", result.ReferenceText);
        Assert.Contains("Denting Blows", result.ReferenceText);
        Assert.Contains("Prevent all combat damage", result.ReferenceText);
        Assert.Contains("deals 4 damage to target creature", result.ReferenceText);
    }

    /// <summary>
    /// Post-83-06 regression: a card that misses the <c>cards/collection</c> batch and is recovered
    /// via <c>SearchPrintingFallbackCardAsync</c> (through the shared <c>ScryfallReferenceResolver</c>)
    /// must still carry its <c>ReleasedAt</c> and MDFC-land classification into the resolved
    /// <c>CardReference</c> — proving the collaborator's fallback path feeds the same 9-field
    /// construction the private inline loop used to build directly.
    /// </summary>
    [Fact]
    public async Task BuildAsync_CollectionMissResolvedViaPrintingFallback_PreservesReleasedAtAndMdfcLand()
    {
        var fallbackCard = new ScryfallCard(
            "Riverglide Pathway // Lavaglide Pathway",
            null,
            "Land // Land",
            null,
            null,
            null,
            [],
            ["U", "R"],
            "znr",
            "Zendikar Rising",
            "260",
            [
                new ScryfallCardFace("Riverglide Pathway", null, "Land", "Riverglide Pathway enters tapped unless you control two or more other lands.", null, null),
                new ScryfallCardFace("Lavaglide Pathway", null, "Land", "Lavaglide Pathway enters tapped unless you control two or more other lands.", null, null)
            ],
            Layout: "modal_dfc",
            ReleasedAt: "2020-09-25");

        var service = CreateService(
            executeSearchAsync: (request, _) =>
            {
                var query = request.Parameters.First(parameter => parameter.Name?.ToString() == "q").Value?.ToString() ?? string.Empty;
                var cards = query.Contains("Riverglide Pathway", StringComparison.Ordinal)
                    ? new[] { fallbackCard }
                    : Array.Empty<ScryfallCard>();

                return Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallSearchResponse(cards.ToList())
                });
            });

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Riverglide Pathway // Lavaglide Pathway
1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["consistency"]
        });

        Assert.NotNull(result.ReferenceText);
        Assert.Contains("Riverglide Pathway // Lavaglide Pathway", result.ReferenceText);
        Assert.Contains("enters tapped unless you control two or more other lands", result.ReferenceText);
        Assert.Contains("[MDFC-land]", result.ReferenceText);
    }

    [Fact]
    public async Task BuildAsync_GeneratesSetUpgradePrompt_WhenDeckProfileAndSetPacketProvided()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 1,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            DeckProfileJson = """
{
  "format": "Commander",
  "commander": "Atraxa, Praetors' Voice",
  "game_plan": "Midrange value",
  "primary_axes": ["counters"],
  "speed": "medium",
  "strengths": [],
  "weaknesses": [],
  "deck_needs": [],
  "weak_slots": [],
  "synergy_tags": []
}
""",
            SetPacketText = "SET: Test Set\nCARDS:\nTest Card | 2G | Creature | Example text."
        });

        Assert.NotNull(result.SetUpgradePromptText);
        Assert.Contains("Do not recommend cards from the official Commander banned list", result.SetUpgradePromptText);
        Assert.Contains("Dockside Extortionist", result.SetUpgradePromptText);
        Assert.Contains("Per-set analysis", result.SetUpgradePromptText);
        Assert.Contains("Top adds from that set", result.SetUpgradePromptText);
        Assert.Contains("Suggested removals for each add", result.SetUpgradePromptText);
        Assert.Contains("must_test: cards you would actively slot in", result.SetUpgradePromptText);
        Assert.Contains("suggested card to cut", result.SetUpgradePromptText);
        Assert.Contains("set_upgrade_report", result.SetUpgradePromptText);
        Assert.Contains("```json", result.SetUpgradePromptText);
        Assert.Contains("\"final_shortlist\"", result.SetUpgradePromptText);
        Assert.DoesNotContain("discussion_summary", result.SetUpgradePromptText);
        Assert.DoesNotContain("```text", result.SetUpgradePromptText);
        Assert.DoesNotContain("per-set analysis in condensed form", result.SetUpgradePromptText);
        Assert.Contains("SET: Test Set", result.SetUpgradePromptText);
        Assert.Contains("\"game_plan\": \"Midrange value\"", result.SetUpgradePromptText);
    }

    /// <summary>
    /// Injects lateral-move focus instructions when SetUpgradeFocus is "lateral-moves".
    /// </summary>
    [Fact]
    public async Task BuildAsync_InjectsLateralMoveFocus_WhenSetUpgradeFocusIsLateralMoves()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 1,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            DeckProfileJson = """
{
  "format": "Commander",
  "commander": "Atraxa, Praetors' Voice",
  "game_plan": "Midrange value",
  "primary_axes": ["counters"],
  "speed": "medium",
  "strengths": [],
  "weaknesses": [],
  "deck_needs": [],
  "weak_slots": [],
  "synergy_tags": []
}
""",
            SetPacketText = "SET: Test Set\nCARDS:\nTest Card | 2G | Creature | Example text.",
            SetUpgradeFocus = "lateral-moves"
        });

        Assert.NotNull(result.SetUpgradePromptText);
        Assert.Contains("LATERAL MOVES ONLY", result.SetUpgradePromptText);
        Assert.Contains("fills the same role as a card already in the deck", result.SetUpgradePromptText);
        Assert.Contains("why the swap is worth considering", result.SetUpgradePromptText);
        Assert.DoesNotContain("STRICT UPGRADES ONLY", result.SetUpgradePromptText);
    }

    /// <summary>
    /// Injects strict-upgrade focus instructions when SetUpgradeFocus is "strict-upgrades".
    /// </summary>
    [Fact]
    public async Task BuildAsync_InjectsStrictUpgradeFocus_WhenSetUpgradeFocusIsStrictUpgrades()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 1,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            DeckProfileJson = """
{
  "format": "Commander",
  "commander": "Atraxa, Praetors' Voice",
  "game_plan": "Midrange value",
  "primary_axes": ["counters"],
  "speed": "medium",
  "strengths": [],
  "weaknesses": [],
  "deck_needs": [],
  "weak_slots": [],
  "synergy_tags": []
}
""",
            SetPacketText = "SET: Test Set\nCARDS:\nTest Card | 2G | Creature | Example text.",
            SetUpgradeFocus = "strict-upgrades"
        });

        Assert.NotNull(result.SetUpgradePromptText);
        Assert.Contains("STRICT UPGRADES ONLY", result.SetUpgradePromptText);
        Assert.Contains("meaningfully more powerful, more efficient", result.SetUpgradePromptText);
        Assert.DoesNotContain("LATERAL MOVES ONLY", result.SetUpgradePromptText);
    }

    /// <summary>
    /// Injects both-focus instructions and labels when SetUpgradeFocus is "both".
    /// </summary>
    [Fact]
    public async Task BuildAsync_InjectsBothFocus_WhenSetUpgradeFocusIsBoth()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 1,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            DeckProfileJson = """
{
  "format": "Commander",
  "commander": "Atraxa, Praetors' Voice",
  "game_plan": "Midrange value",
  "primary_axes": ["counters"],
  "speed": "medium",
  "strengths": [],
  "weaknesses": [],
  "deck_needs": [],
  "weak_slots": [],
  "synergy_tags": []
}
""",
            SetPacketText = "SET: Test Set\nCARDS:\nTest Card | 2G | Creature | Example text.",
            SetUpgradeFocus = "both"
        });

        Assert.NotNull(result.SetUpgradePromptText);
        Assert.Contains("STRICT UPGRADES AND LATERAL MOVES", result.SetUpgradePromptText);
        Assert.Contains("'Strict Upgrade' or 'Lateral Move'", result.SetUpgradePromptText);
    }

    /// <summary>
    /// No focus instructions are injected when SetUpgradeFocus is empty (default behaviour).
    /// </summary>
    [Fact]
    public async Task BuildAsync_OmitsFocusInstructions_WhenSetUpgradeFocusIsDefault()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 1,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            DeckProfileJson = """
{
  "format": "Commander",
  "commander": "Atraxa, Praetors' Voice",
  "game_plan": "Midrange value",
  "primary_axes": ["counters"],
  "speed": "medium",
  "strengths": [],
  "weaknesses": [],
  "deck_needs": [],
  "weak_slots": [],
  "synergy_tags": []
}
""",
            SetPacketText = "SET: Test Set\nCARDS:\nTest Card | 2G | Creature | Example text."
        });

        Assert.NotNull(result.SetUpgradePromptText);
        Assert.DoesNotContain("LATERAL MOVES ONLY", result.SetUpgradePromptText);
        Assert.DoesNotContain("STRICT UPGRADES ONLY", result.SetUpgradePromptText);
        Assert.DoesNotContain("STRICT UPGRADES AND LATERAL MOVES", result.SetUpgradePromptText);
    }

    [Fact]
    public async Task BuildAsync_UsesGeneratedSetPacket_WhenSetCodesSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 1,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            DeckProfileJson = """
{
  "format": "Commander",
  "commander": "Atraxa, Praetors' Voice",
  "game_plan": "Midrange value",
  "primary_axes": ["counters"],
  "speed": "medium",
  "strengths": [],
  "weaknesses": [],
  "deck_needs": [],
  "weak_slots": [],
  "synergy_tags": []
}
""",
            SelectedSetCodes = ["dsk"]
        });

        Assert.NotNull(result.SetUpgradePromptText);
        Assert.Contains("## SET PACKET", result.SetUpgradePromptText);
        Assert.Contains("Test Set (DSK)", result.SetUpgradePromptText);
        Assert.Contains("Survival", result.SetUpgradePromptText);
        Assert.Contains("Final cross-set ranked shortlist", result.SetUpgradePromptText);
        Assert.Contains("set_upgrade_report", result.SetUpgradePromptText);
        Assert.Contains("\"sets\": [", result.SetUpgradePromptText);
        Assert.DoesNotContain("discussion_summary", result.SetUpgradePromptText);
        Assert.DoesNotContain("per-set analysis in condensed form", result.SetUpgradePromptText);
        Assert.DoesNotContain("Off Color Test Card", result.SetUpgradePromptText);
        Assert.DoesNotContain("Paste the condensed set packet", result.SetUpgradePromptText);
    }

    [Fact]
    public async Task BuildAsync_UsesCommanderColorIdentity_ForGeneratedSetPacket()
    {
        var service = CreateService(
            executeCollectionAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCollectionResponse(
                    [new ScryfallCard(
                        Name: "Atraxa, Praetors' Voice", ManaCost: null, TypeLine: string.Empty,
                        OracleText: null, Power: null, Toughness: null, Keywords: null,
                        ColorIdentity: [" r ", "R", ""], SetCode: null, SetName: null,
                        CollectorNumber: null)],
                    [])
            }));

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 1,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
""",
            DeckProfileJson = "{}",
            SelectedSetCodes = ["dsk"]
        });

        Assert.Contains("Off Color Test Card", result.SetUpgradePromptText);
    }

    [Fact]
    public async Task BuildAsync_UsesSingleFaceCommanderIdentifier_ForGeneratedSetPacket()
    {
        RestRequest? submittedRequest = null;
        var service = CreateService(
            executeCollectionAsync: (request, _) =>
            {
                submittedRequest = request;
                return Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallCollectionResponse(
                        [new ScryfallCard(
                            Name: "Etali, Primal Conqueror // Etali, Primal Sickness", ManaCost: null, TypeLine: string.Empty,
                            OracleText: null, Power: null, Toughness: null, Keywords: null, ColorIdentity: ["G", "R"],
                            SetCode: null, SetName: null, CollectorNumber: null)], [])
                });
            });

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 1,
            DeckSource = "Commander\n1 Etali, Primal Conqueror // Etali, Primal Sickness\n\n1 Sol Ring\n",
            DeckProfileJson = "{}",
            SelectedSetCodes = ["dsk"]
        });

        var body = System.Text.Json.JsonSerializer.Serialize(submittedRequest!.Parameters.Single(parameter => parameter.Type == ParameterType.RequestBody).Value);
        Assert.Contains("Etali, Primal Conqueror", body);
        Assert.DoesNotContain("Etali, Primal Conqueror // Etali, Primal Sickness", body);
        Assert.Contains("Off Color Test Card", result.SetUpgradePromptText);
    }

    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenMultipleSetsSelectedForGeneratedPacket()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 4,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            DeckProfileJson = """
{
  "format": "Commander",
  "commander": "Atraxa, Praetors' Voice",
  "game_plan": "Midrange value",
  "primary_axes": ["counters"],
  "speed": "medium",
  "strengths": [],
  "weaknesses": [],
  "deck_needs": [],
  "weak_slots": [],
  "synergy_tags": []
}
""",
            SelectedSetCodes = ["dsk", "fdn"]
        }));

        Assert.Equal("Choose only one set or paste a condensed set packet override before generating the set-upgrade packet.", exception.Message);
    }

    /// <summary>
    /// Appends the freeform question as an extra bullet in the analysis prompt.
    /// </summary>
    [Fact]
    public async Task BuildAsync_IncludesFreeformQuestion_InAnalysisPrompt()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses"],
            FreeformQuestion = "Would this deck benefit from a dedicated stax package?"
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Would this deck benefit from a dedicated stax package?", result.AnalysisPromptText);
        Assert.Contains("1. What are the strengths and weaknesses of this deck?", result.AnalysisPromptText);
        Assert.Contains("2. Would this deck benefit from a dedicated stax package?", result.AnalysisPromptText);
        Assert.Contains("question_answers array must contain one entry per question", result.AnalysisPromptText);
        Assert.Contains("Do not omit any question. If there are 8 questions, return exactly 8 question_answers entries numbered 1 through 8.", result.AnalysisPromptText);
        Assert.Contains("The JSON question_answers entries must mirror the readable Requested Question Answers section one-for-one.", result.AnalysisPromptText);
        Assert.Contains("Before returning the JSON, count the numbered questions above and verify that question_answers has the same count.", result.AnalysisPromptText);
        Assert.Contains("Deliver the ENTIRE required output — the Requested Question Answers, Top Adds, Top Cuts, and the complete deck_profile JSON — in this single response.", result.AnalysisPromptText);
        Assert.Contains("If the full output would genuinely approach your hard output limit, do not refuse or drop any section: shorten each question answer to 4-6 sentences", result.AnalysisPromptText);
    }

    [Fact]
    public async Task BuildAsync_IncludesStrategyAndMetaNotes_InAnalysisPrompt()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

            1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["strengths-weaknesses"],
            StrategyNotes = "Use the graveyard as a second hand.",
            MetaNotes = "Expect graveyard hate and fast combo."
        });

        Assert.Contains("strategy_notes:", result.AnalysisPromptText);
        Assert.Contains("meta_notes:", result.AnalysisPromptText);
    }

    /// <summary>
    /// A freeform question alone satisfies the requirement for at least one analysis question.
    /// </summary>
    [Fact]
    public async Task BuildAsync_AllowsFreeformQuestionAlone_WithoutCatalogQuestions()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            FreeformQuestion = "How does this deck compare to a typical Atraxa superfriends build?"
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("How does this deck compare to a typical Atraxa superfriends build?", result.AnalysisPromptText);
    }

    /// <summary>
    /// When the score flag is ON and a combo question is ALSO selected, the widened combo gate must
    /// still fire Commander Spellbook exactly once — the single fetch is reused for both the prompt
    /// combo-reference text and the score's combo-density signal (no double-fetch; Codex HIGH).
    /// </summary>
    [Fact]
    public async Task BuildAsync_ScoreFlagOn_WithComboQuestion_FetchesCommanderSpellbookExactlyOnce()
    {
        var importer = new FakeMoxfieldDeckImporter(
            entries: CreateCompanionFixtureEntries(includeBackgroundCommander: true));
        var spellbook = new CountingCommanderSpellbookService();
        var flagOn = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["analysis.multi-axis-score"] = true
        });

        var service = CreateService(
            moxfieldDeckImporter: importer,
            flagCache: flagOn,
            spellbookService: spellbook);

        var request = new DeckAnalysisRequest
        {
            DeckInputSource = DeckInputSource.PublicUrl,
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-score-combo-once",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["combo-in-deck"]
        };

        var result = await service.BuildAsync(request);

        Assert.Equal(1, spellbook.CallCount);
        Assert.NotNull(result.Score);
    }

    /// <summary>
    /// Counts FindCombosAsync invocations to prove the combo fetch is not duplicated when both the
    /// score flag and a combo question are active. Returns an empty result (detection ran, no combos).
    /// </summary>
    private sealed class CountingCommanderSpellbookService : ICommanderSpellbookService
    {
        public int CallCount { get; private set; }

        public Task<CommanderSpellbookResult?> FindCombosAsync(
            IReadOnlyList<DeckEntry> entries,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<CommanderSpellbookResult?>(
                new CommanderSpellbookResult(Array.Empty<SpellbookCombo>(), Array.Empty<SpellbookAlmostCombo>()));
        }
    }

    [Fact]
    public async Task BuildAsync_PrintingFallbackHttpFailure_PropagatesOriginalMessage_NotCollectionReWrap()
    {
        // Why (Phase 83 WR-01): a per-name printing-fallback HTTP failure must keep its ORIGINAL upstream
        // message (no "cards/collection"/"analysis packet" text). The pre-refactor code let it propagate,
        // so UpstreamErrorMessageBuilder produced the generic "Scryfall returned HTTP {n}" copy; the
        // migration's catch wrongly re-labeled it with the collection-call message, flipping which
        // BuildDetailedScryfallMessage branch fires. Only the cards/collection-CALL failure gets that
        // re-wrap now.
        var service = CreateService(
            executeCollectionAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCollectionResponse([], [])
            }),
            executeSearchAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                Data = null
            }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["consistency"]
        }));

        Assert.DoesNotContain("cards/collection", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("analysis packet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_CollectionCallHttpFailure_ReWrapsWithAnalysisPacketMessage()
    {
        // The cards/collection-CALL failure (distinct from a printing-fallback failure) keeps the
        // analysis-packet re-wrap, matching the pre-refactor collection-call message exactly.
        var service = CreateService(
            executeCollectionAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                Data = null
            }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.BuildAsync(new DeckAnalysisRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["consistency"]
        }));

        Assert.Contains("cards/collection", exception.Message, StringComparison.Ordinal);
        Assert.Contains("analysis packet", exception.Message, StringComparison.Ordinal);
    }

    private static DeckAnalysisPacketService CreateService(
        IMoxfieldDeckImporter? moxfieldDeckImporter = null,
        IFeatureFlagCache? flagCache = null,
        IGameChangerCatalogService? catalogService = null,
        ICommanderSpellbookService? spellbookService = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeSearchAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCard>>>? executeNamedAsync = null,
        TimeProvider? timeProvider = null)
    {
        var cardResolver = new ScryfallCardResolver(
            new FakeScryfallRestClientFactory(new HttpClient
            {
                BaseAddress = new Uri("https://api.scryfall.com/")
            }),
            new FakeResiliencePipelineProvider(),
            executeCollectionAsyncOverride: executeCollectionAsync ?? ((request, _) => Task.FromResult(CreateCollectionResponse(request))),
            executeSearchAsyncOverride: executeSearchAsync ?? ((request, _) => Task.FromResult(CreateSearchResponse(request))),
            executeNamedAsyncOverride: executeNamedAsync ?? ((request, _) => Task.FromResult(CreateNamedResponse(request))));
        return new DeckAnalysisPacketService(
            cardResolver,
            new ScryfallReferenceResolver(cardResolver, new ScryfallCollectionCardCache()),
            new DeckEntryLoader(
                moxfieldDeckImporter ?? new FakeMoxfieldDeckImporter(),
                new FakeArchidektDeckImporter(),
                new MoxfieldParser(),
                new ArchidektParser()),
            new FakeMechanicLookupService(),
            new FakeCommanderBanListService(),
            new FakeScryfallSetService(),
            spellbookService ?? new FakeCommanderSpellbookService(),
            catalogService ?? new FakeGameChangerCatalogService(EmptyGameChangerCatalog()),
            new AnalysisPromptVariantRegistry(new IAnalysisPromptVariant[]
            {
                new ChatGptAnalysisPromptVariant(),
                new ClaudeAnalysisPromptVariant(),
                new GeminiAnalysisPromptVariant(),
            }),
            new SetUpgradePromptVariantRegistry(new ISetUpgradePromptVariant[]
            {
                new ChatGptSetUpgradePromptVariant(),
                new ClaudeSetUpgradePromptVariant(),
                new GeminiSetUpgradePromptVariant(),
            }),
            new PacketSessionCache(),
            flagCache: flagCache,
            logger: NullLogger<DeckAnalysisPacketService>.Instance,
            timeProvider: timeProvider ?? new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>An empty catalog: no Game Changers / MLD / extra-turn cards. Bracket classification still
    /// runs (combo-driven gating), which is all the score wiring needs from the catalog here.</summary>
    private static GameChangerCatalog EmptyGameChangerCatalog()
        => new(
            new DateOnly(2026, 2, 1),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<BracketTier>());

    private sealed class FakeGameChangerCatalogService : IGameChangerCatalogService
    {
        private readonly GameChangerCatalog _catalog;

        public FakeGameChangerCatalogService(GameChangerCatalog catalog) => _catalog = catalog;

        public GameChangerCatalog GetCatalog() => _catalog;
    }

    private static List<DeckEntry> CreateCompanionFixtureEntries(bool includeBackgroundCommander)
    {
        var entries = new List<DeckEntry>
        {
            CreateDeckEntry("Kraum, Ludevic's Opus", 1, "commander", "c16", "39"),
            CreateDeckEntry("Command Tower", 1, "mainboard", "c16", "285"),
            CreateDeckEntry("Arcane Signet", 1, "mainboard", "eld", "331"),
            CreateDeckEntry("Ponder", 1, "mainboard", "c21", "118"),
            CreateDeckEntry("Sol Ring", 1, "mainboard", "c16", "272"),
        };

        if (includeBackgroundCommander)
        {
            entries.Insert(1, CreateDeckEntry("Passionate Archaeologist", 1, "commander", "clb", "189"));
        }

        return entries;
    }

    private static DeckEntry CreateDeckEntry(
        string name,
        int quantity,
        string board,
        string? setCode,
        string? collectorNumber,
        string? category = null)
        => new()
        {
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Quantity = quantity,
            Board = board,
            SetCode = setCode,
            CollectorNumber = collectorNumber,
            Category = category
        };

    private static string FlattenPacketText(DeckAnalysisPacketResult result)
        => string.Join(
            "\n",
            new[]
            {
                result.InputSummary,
                result.ReferenceText,
                result.AnalysisPromptText,
                result.SetUpgradePromptText,
                result.RequestContextText,
                result.DeckProfileSchemaJson,
                // Timing is environmental (Stopwatch ms) and excluded from content byte-identity checks.
                result.SuggestedChatTitle,
                result.ResolvedCommanderName
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string InvokeBuildSuggestedChatTitle(DeckAnalysisRequest request, string? commanderName)
    {
        MethodInfo method = typeof(DeckAnalysisPacketService).GetMethod("BuildSuggestedChatTitle", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Xunit.Sdk.XunitException("BuildSuggestedChatTitle not found.");

        return (string)(method.Invoke(null, new object?[] { request, commanderName }) ?? throw new Xunit.Sdk.XunitException("BuildSuggestedChatTitle returned null."));
    }

    private static string InvokeBuildAnalysisSummaryFromSavedJson(DeckAnalysisResponse response)
    {
        MethodInfo method = typeof(DeckAnalysisPacketService).GetMethod("BuildAnalysisSummaryFromSavedJson", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Xunit.Sdk.XunitException("BuildAnalysisSummaryFromSavedJson not found.");

        return (string)(method.Invoke(null, new object?[] { response }) ?? throw new Xunit.Sdk.XunitException("BuildAnalysisSummaryFromSavedJson returned null."));
    }

    private static byte[] PacketBytes(DeckAnalysisPacketResult result)
        => Encoding.UTF8.GetBytes(FlattenPacketText(result));

    private static RestResponse<ScryfallCollectionResponse> CreateCollectionResponse(RestRequest request)
    {
        var requestedNames = request.Parameters
            .FirstOrDefault(parameter => string.Equals(parameter.Name?.ToString(), "application/json", StringComparison.OrdinalIgnoreCase))
            ?.Value?
            .ToString();

        return new RestResponse<ScryfallCollectionResponse>(request)
        {
            StatusCode = HttpStatusCode.OK,
            Data = new ScryfallCollectionResponse(
                GetDefaultTestCards().ToList(),
                [])
        };
    }

    private static RestResponse<ScryfallSearchResponse> CreateSearchResponse(RestRequest request)
    {
        var query = request.Parameters.FirstOrDefault(parameter => parameter.Name?.ToString() == "q")?.Value?.ToString() ?? string.Empty;
        var match = FindDefaultCard(query);
        return new RestResponse<ScryfallSearchResponse>(request)
        {
            StatusCode = HttpStatusCode.OK,
            Data = new ScryfallSearchResponse(match is null ? [] : [match])
        };
    }

    private static RestResponse<ScryfallCard> CreateNamedResponse(RestRequest request)
    {
        var fuzzy = request.Parameters.FirstOrDefault(parameter => parameter.Name?.ToString() == "fuzzy")?.Value?.ToString() ?? string.Empty;
        var match = FindDefaultCard(fuzzy);
        return new RestResponse<ScryfallCard>(request)
        {
            StatusCode = match is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
            Data = match
        };
    }

    private static ScryfallCard? FindDefaultCard(string query)
    {
        var normalizedQuery = NormalizeTestLookup(query);
        return GetDefaultTestCards().FirstOrDefault(card =>
            normalizedQuery.Contains(NormalizeTestLookup(card.Name), StringComparison.Ordinal)
            || (card.CardFaces?.Any(face => normalizedQuery.Contains(NormalizeTestLookup(face.Name), StringComparison.Ordinal)) ?? false));
    }

    private static string NormalizeTestLookup(string value)
        => value
            .Trim()
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    private static IReadOnlyList<ScryfallCard> GetDefaultTestCards() =>
    [
        new("Sol Ring", "{1}", "Artifact", "{T}: Add {C}{C}.", null, null, null, [], null, null, null),
        new("Arcane Signet", "{2}", "Artifact", "{T}: Add one mana of any color in your commander's color identity.", null, null, null, [], null, null, null),
        new("Command Tower", null, "Land", "{T}: Add one mana of any color in your commander's color identity.", null, null, [], [], null, null, null),
        new("Ponder", "{U}", "Sorcery", "Look at the top three cards of your library, then put them back in any order. You may shuffle. Draw a card.", null, null, [], ["U"], null, null, null),
        new("Swords to Plowshares", "{W}", "Instant", "Exile target creature. Its controller gains life equal to its power.", null, null, null, ["W"], null, null, null),
        new("Smothering Tithe", "{3}{W}", "Enchantment", "Whenever an opponent draws a card, that player may pay {2}. If the player doesn't, you create a Treasure token.", null, null, ["Treasure"], ["W"], null, null, null),
        new("Atraxa, Praetors' Voice", "{G}{W}{U}{B}", "Legendary Creature — Phyrexian Angel Horror", "Flying, vigilance, deathtouch, lifelink. At the beginning of your end step, proliferate.", "4", "4", ["Flying", "Vigilance", "Deathtouch", "Lifelink", "Proliferate"], ["G", "W", "U", "B"], null, null, null),
        new("Kraum, Ludevic's Opus", "{3}{U}{R}", "Legendary Creature — Zombie Horror", "Flying, haste\nWhenever an opponent casts their second spell each turn, draw a card.", "4", "4", ["Flying", "Haste"], ["U", "R"], "c16", "Commander 2016", "39"),
        new("Passionate Archaeologist", "{2}{R}", "Legendary Enchantment — Background", "Commander creatures you own have \"Whenever you cast a spell from exile, this creature deals damage equal to that spell's mana value to target opponent.\"", null, null, ["Background"], ["R"], "clb", "Commander Legends: Battle for Baldur's Gate", "189"),
        new("Tymna the Weaver", "{1}{W}{B}", "Legendary Creature — Human Cleric", "Lifelink\nAt the beginning of your postcombat main phase, you may pay X life, where X is the number of opponents that were dealt combat damage this turn. If you do, draw X cards.", "2", "2", ["Lifelink"], ["W", "B"], null, null, null),
        new("Thrasios, Triton Hero", "{G/U}", "Legendary Creature — Merfolk Wizard", "{4}: Scry 1, then reveal the top card of your library. If it's a land card, put it onto the battlefield tapped. Otherwise, draw a card.", "1", "3", [], ["G", "U"], null, null, null),
        new("Tannuk, Memorial Ensign", "{3}{G}{W}", "Legendary Creature — Human Scout", "Vigilance\nWhenever one or more cards leave your graveyard during your turn, create a 2/2 white and black Soldier creature token. This ability triggers only once each turn.", "3", "4", ["Vigilance"], ["G", "W"], null, null, null),
        new("Aftermath Analyst", "{1}{G}", "Creature — Elf Detective", "When Aftermath Analyst enters, mill three cards.\n{3}{G}, Exile Aftermath Analyst from your graveyard: Return all land cards from your graveyard to the battlefield tapped.", "2", "1", [], ["G"], null, null, null),
        new("Aerith's Curaga Magic", "{1}{G}", "Instant", "Prevent all damage that would be dealt to target creature this turn.", null, null, [], ["G"], "sld", "Secret Lair Drop", "1872"),
        new(
            "Blex, Vexing Pest // Search for Blex",
            null,
            "Legendary Creature — Pest // Sorcery",
            null,
            null,
            null,
            ["Pest"],
            ["B", "G"],
            null,
            null,
            null,
            [
                new ScryfallCardFace(
                    "Blex, Vexing Pest",
                    "{2}{B}{G}",
                    "Legendary Creature — Pest",
                    "Other Pests, Bats, Insects, Snakes, and Spiders you control get +1/+1.",
                    "3",
                    "2"),
                new ScryfallCardFace(
                    "Search for Blex",
                    "{X}{2}{B/G}{B/G}",
                    "Sorcery",
                    "Look at the top five cards of your library. You may reveal any number of creature cards with mana value X or less from among them and put the revealed cards into your hand. Put the rest on the bottom of your library in a random order. You lose 3 life.",
                    null,
                    null)
            ])
    ];

    private sealed class FakeMoxfieldDeckImporter : IMoxfieldDeckImporter
    {
        private readonly List<DeckEntry> _entries;
        private readonly string? _detectedCompanionName;

        public FakeMoxfieldDeckImporter(List<DeckEntry>? entries = null, string? detectedCompanionName = null)
        {
            _entries = entries ?? [];
            _detectedCompanionName = detectedCompanionName;
        }

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries.Select(CloneEntry).ToList());

        public Task<MoxfieldImportResult> ImportWithSourceAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(new MoxfieldImportResult(
                ImportAsync(urlOrDeckId, cancellationToken).GetAwaiter().GetResult(),
                MoxfieldImportSource.Direct,
                DetectedCompanionName: _detectedCompanionName));

        private static DeckEntry CloneEntry(DeckEntry entry)
            => CreateDeckEntry(entry.Name, entry.Quantity, entry.Board, entry.SetCode, entry.CollectorNumber, entry.Category);
    }

    private sealed class FakeArchidektDeckImporter : IArchidektDeckImporter
    {
        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<DeckEntry>());
    }

    private sealed class FakeMechanicLookupService : IMechanicLookupService
    {
        public Task<MechanicLookupResult> LookupAsync(string mechanicName, CancellationToken cancellationToken = default)
            => Task.FromResult(new MechanicLookupResult(
                mechanicName,
                true,
                mechanicName,
                "702.108",
                "Exact rules section",
                "702.108a Prowess is a triggered ability.",
                "A keyword ability that boosts a creature when its controller casts a noncreature spell.",
                "https://magic.wizards.com/en/rules",
                "https://media.wizards.com/test.txt"));
    }

    private sealed class FakeScryfallSetService : IScryfallSetService
    {
        public Task<IReadOnlyList<ScryfallSetOption>> GetSetsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ScryfallSetOption>>(
                [new ScryfallSetOption("dsk", "Test Set", "2026-01-01")]);

        public Task<string> BuildSetPacketAsync(IReadOnlyList<string> setCodes, IReadOnlyList<string>? commanderColorIdentity = null, CancellationToken cancellationToken = default)
        {
            var allowRed = (commanderColorIdentity ?? Array.Empty<string>())
                .Any(color => string.Equals(color, "R", StringComparison.OrdinalIgnoreCase));

            var packet = """
set_packet:
generated_at_utc: 2026-03-26T00:00:00Z
sets:
- Test Set (DSK)

mechanics:
Survival: A test mechanic summary.

set: Test Set (DSK)
cards:
Test Card | 1W | Creature | Survival — Test text.
""";

            if (allowRed)
            {
                packet += "Off Color Test Card | 1R | Creature | Haste.\n";
            }

            return Task.FromResult(packet);
        }
    }

    private sealed class FakeCommanderBanListService : ICommanderBanListService
    {
        public Task<IReadOnlyList<string>> GetBannedCardsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(["Dockside Extortionist", "Mana Crypt"]);
    }

    private sealed class FakeCommanderSpellbookService : ICommanderSpellbookService
    {
        public Task<CommanderSpellbookResult?> FindCombosAsync(
            IReadOnlyList<DeckEntry> entries,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CommanderSpellbookResult?>(null);
    }

    /// <summary>
    /// Recency gate off: Oracle text is always emitted (legacy behavior), regardless of release date.
    /// </summary>
    [Theory]
    [InlineData("2000-01-01")]
    [InlineData("2099-01-01")]
    [InlineData(null)]
    [InlineData("not-a-date")]
    public void ShouldIncludeOracleText_GateOff_AlwaysIncludes(string? releasedAt)
    {
        var cutoff = new DateOnly(2025, 6, 20);
        Assert.True(DeckAnalysisPacketService.ShouldIncludeOracleText(releasedAt, recencyGateEnabled: false, cutoff));
    }

    /// <summary>
    /// Recency gate on: Oracle text is dropped for cards released before the cutoff and kept on/after it.
    /// </summary>
    [Theory]
    [InlineData("2024-06-19", false)] // before cutoff -> drop
    [InlineData("2025-06-19", false)] // day before cutoff -> drop
    [InlineData("2025-06-20", true)]  // on cutoff -> keep
    [InlineData("2026-01-01", true)]  // after cutoff -> keep
    public void ShouldIncludeOracleText_GateOn_GatesByReleaseDate(string releasedAt, bool expected)
    {
        var cutoff = new DateOnly(2025, 6, 20);
        Assert.Equal(expected, DeckAnalysisPacketService.ShouldIncludeOracleText(releasedAt, recencyGateEnabled: true, cutoff));
    }

    /// <summary>
    /// Recency gate on with a missing or unparseable release date fails open — Oracle text is kept so
    /// grounding is never silently lost for a card we could not date.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void ShouldIncludeOracleText_GateOn_FailsOpenOnUnparseableDate(string? releasedAt)
    {
        var cutoff = new DateOnly(2025, 6, 20);
        Assert.True(DeckAnalysisPacketService.ShouldIncludeOracleText(releasedAt, recencyGateEnabled: true, cutoff));
    }

    /// <summary>
    /// Parses pipe-delimited card lines into a name → rules-text map and skips non-card lines.
    /// </summary>
    [Fact]
    public void ParseSetPacketCardText_MapsCardLinesAndIgnoresHeaders()
    {
        var packet = string.Join("\n",
            "set_packet:",
            "set: Duskmourn (DSK)",
            "cards:",
            "Atraxa's Fall | {2}{G} | Instant | Destroy target creature with flying or a planeswalker.",
            "Overlord of the Mistmoors | {4}{W}{W} | Creature | Flying, trample 6/5",
            "mechanics:",
            "delirium: Some rules text without a pipe.");

        var map = DeckAnalysisPacketService.ParseSetPacketCardText(packet);

        Assert.Equal(2, map.Count);
        Assert.Equal("Destroy target creature with flying or a planeswalker.", map["Atraxa's Fall"]);
        Assert.Equal("Flying, trample 6/5", map["Overlord of the Mistmoors"]);
        Assert.False(map.ContainsKey("mechanics:"));
    }

    /// <summary>
    /// Card-name lookup is case-insensitive and empty/whitespace packets yield an empty map.
    /// </summary>
    [Fact]
    public void ParseSetPacketCardText_IsCaseInsensitiveAndHandlesEmptyInput()
    {
        var map = DeckAnalysisPacketService.ParseSetPacketCardText("Sol Ring | {1} | Artifact | {T}: Add {C}{C}.");
        Assert.Equal("{T}: Add {C}{C}.", map["sol ring"]);

        Assert.Empty(DeckAnalysisPacketService.ParseSetPacketCardText(null));
        Assert.Empty(DeckAnalysisPacketService.ParseSetPacketCardText("   "));
    }

}
