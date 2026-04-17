using System.Net;
using System.IO;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Models;
using DeckFlow.Core.Parsing;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Covers staged prompt generation, validation, and artifact output for the ChatGPT workflow.
/// </summary>
public sealed class ChatGptDeckPacketServiceTests
{
    /// <summary>
    /// Builds the deck summary and schema from pasted deck text on the setup step.
    /// </summary>
    [Fact]
    public async Task BuildAsync_GeneratesSummaryAndSchema_ForPastedDeckText()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

    /// <summary>
    /// Keeps commander, decklist, sideboard, and maybeboard sections distinct in the first prompt.
    /// </summary>
    [Fact]
    public async Task BuildAsync_SeparatesCommanderDecklistAndPossibleIncludesInInitialProbePrompt()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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
            CardSpecificQuestionCardName = "Sol Ring"
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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
            IncludeSideboardInAnalysis = true,
            IncludeMaybeboardInAnalysis = true
        });

        Assert.NotNull(result.ReferenceText);
        Assert.Contains("[candidate_include:sideboard] Swords to Plowshares", result.ReferenceText);
        Assert.Contains("[candidate_include:maybeboard] Smothering Tithe", result.ReferenceText);
        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Possible Includes", result.AnalysisPromptText);
        Assert.Contains("1 Swords to Plowshares", result.AnalysisPromptText);
        Assert.Contains("1 Smothering Tithe", result.AnalysisPromptText);
    }

    [Fact]
    public async Task BuildAsync_ExcludesOptionalCandidateBoardsInAnalysis_WhenNotSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
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
    }

    [Fact]
    public async Task BuildAsync_DoesNotGenerateAnalysis_WhenOnSetupStep()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckRequest
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckRequest
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

        Assert.Equal("The submitted ChatGPT response did not contain a valid deck_profile payload.", exception.Message);
    }

    [Fact]
    public async Task BuildAsync_RendersAnalysisResponse_FromDeckProfileJsonWithoutDeckSource()
    {
        var service = CreateService(
            executeCollectionAsync: (_, _) => throw new InvalidOperationException("Scryfall lookup should not run for saved Step 3 JSON."));

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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
      "must_test": ["New Ramp"],
      "optional": ["Unproven Card"],
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
        Assert.Contains("New Ramp", result.SetUpgradeResponse.FinalShortlist!.MustTest);
        Assert.Contains("Shiny Trap", result.SetUpgradeResponse.FinalShortlist.Skip);
        Assert.Null(result.SetUpgradePromptText);
    }

    [Fact]
    public async Task BuildAsync_Throws_WhenSetUpgradeJsonIsMissingReport()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckRequest
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckRequest
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

        Assert.Equal("Enter a card name for the selected card-specific analysis questions.", exception.Message);
    }

    /// <summary>
    /// Requires a budget amount when the selected questions include budget upgrades.
    /// </summary>
    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenBudgetQuestionMissingBudgetAmount()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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
    public async Task BuildAsync_RequiresFullDecklists_WhenDeckVersionQuestionsSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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
        var result = await service.BuildAsync(new ChatGptDeckRequest
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
        var result = await service.BuildAsync(new ChatGptDeckRequest
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

    /// <summary>
    /// When the first two leading entries are both 1-of, both are treated as partner commanders.
    /// </summary>
    [Fact]
    public async Task BuildAsync_DetectsPartnerCommandersFromLeadingEntries_WhenNoCommanderSectionHeader()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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
                Data = new ScryfallCollectionResponse([], [new ScryfallCollectionIdentifier("Ya viene el coco")])
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

    [Fact]
    public async Task BuildAsync_GeneratesSetUpgradePrompt_WhenDeckProfileAndSetPacketProvided()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
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
        Assert.Contains("explain the reasoning briefly", result.SetUpgradePromptText);
        Assert.Contains("set_upgrade_report", result.SetUpgradePromptText);
        Assert.Contains("```json", result.SetUpgradePromptText);
        Assert.Contains("\"final_shortlist\"", result.SetUpgradePromptText);
        Assert.Contains("discussion_summary.txt", result.SetUpgradePromptText);
        Assert.Contains("```text", result.SetUpgradePromptText);
        Assert.Contains("per-set analysis in condensed form", result.SetUpgradePromptText);
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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
        Assert.Contains("discussion_summary.txt", result.SetUpgradePromptText);
        Assert.Contains("per-set analysis in condensed form", result.SetUpgradePromptText);
        Assert.DoesNotContain("Off Color Test Card", result.SetUpgradePromptText);
        Assert.DoesNotContain("Paste the condensed set packet", result.SetUpgradePromptText);
    }

    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenMultipleSetsSelectedForGeneratedPacket()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckRequest
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

    [Fact]
    public async Task BuildAsync_SavesArtifactsToDisk_WhenRequested()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), "DeckFlowTests", Guid.NewGuid().ToString("N"));
        var service = CreateService(artifactsRoot);

        var result = await service.BuildAsync(new ChatGptDeckRequest
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
            CardSpecificQuestionCardName = "Sol Ring",
            Format = "Commander",
            DeckName = "Atraxa Test Deck",
            StrategyNotes = "Play value engines and counters.",
            MetaNotes = "Mid-power pods with removal.",
            SaveArtifactsToDisk = true
        });

        Assert.False(string.IsNullOrWhiteSpace(result.SavedArtifactsDirectory));
        Assert.True(Directory.Exists(result.SavedArtifactsDirectory));
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "00-input-summary.txt")));
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "31-analysis-prompt.txt")));
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "01-request-context.txt")));
        var combinedPrompts = await File.ReadAllTextAsync(Path.Combine(result.SavedArtifactsDirectory!, "all-prompts.txt"));
        Assert.Contains("===== INPUT SUMMARY", combinedPrompts);
        Assert.Contains("===== ANALYSIS PROMPT", combinedPrompts);
        var requestContext = await File.ReadAllTextAsync(Path.Combine(result.SavedArtifactsDirectory!, "01-request-context.txt"));
        Assert.Contains("deck_name: Atraxa Test Deck", requestContext);
        Assert.Contains("strategy_notes:", requestContext);
        Assert.Contains("Play value engines and counters.", requestContext);
        Assert.Contains("meta_notes:", requestContext);
        Assert.Contains("Mid-power pods with removal.", requestContext);
        Assert.Contains("card_specific_question_card_name: Sol Ring", requestContext);
    }

    [Fact]
    public async Task BuildAsync_AllowsNullOptionalRequestFields_AndOmitsEmptyContextBlocks()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), "DeckFlowTests", Guid.NewGuid().ToString("N"));
        var service = CreateService(artifactsRoot);

        var result = await service.BuildAsync(new ChatGptDeckRequest
        {
            WorkflowStep = 2,
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["consistency"],
            SelectedSetCodes = null!,
            StrategyNotes = null!,
            MetaNotes = null!,
            CardSpecificQuestionCardName = null!,
            SaveArtifactsToDisk = true
        });

        Assert.NotNull(result.ReferenceText);
        Assert.NotNull(result.AnalysisPromptText);

        var requestContext = await File.ReadAllTextAsync(Path.Combine(result.SavedArtifactsDirectory!, "01-request-context.txt"));
        Assert.DoesNotContain("strategy_notes:", requestContext);
        Assert.DoesNotContain("meta_notes:", requestContext);
        Assert.DoesNotContain("set_name:", requestContext);
        Assert.Contains("selected_set_codes:", requestContext);
        Assert.DoesNotContain("selected_set_codes:\n-", requestContext.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>
    /// Appends the freeform question as an extra bullet in the analysis prompt.
    /// </summary>
    [Fact]
    public async Task BuildAsync_IncludesFreeformQuestion_InAnalysisPrompt()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
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
    }

    [Fact]
    public async Task BuildAsync_IncludesStrategyAndMetaNotes_InAnalysisPrompt()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

        var result = await service.BuildAsync(new ChatGptDeckRequest
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

    private static ChatGptDeckPacketService CreateService(
        string? contentRootPath = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeSearchAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCard>>>? executeNamedAsync = null)
    {
        var rootPath = contentRootPath ?? Path.Combine(Path.GetTempPath(), "DeckFlowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        return new ChatGptDeckPacketService(
            new FakeMoxfieldDeckImporter(),
            new FakeArchidektDeckImporter(),
            new MoxfieldParser(),
            new ArchidektParser(),
            new FakeMechanicLookupService(),
            new FakeCommanderBanListService(),
            new FakeScryfallSetService(),
            new FakeCommanderSpellbookService(),
            executeCollectionAsync: executeCollectionAsync ?? ((request, _) => Task.FromResult(CreateCollectionResponse(request))),
            executeSearchAsync: executeSearchAsync ?? ((request, _) => Task.FromResult(CreateSearchResponse(request))),
            executeNamedAsync: executeNamedAsync ?? ((request, _) => Task.FromResult(CreateNamedResponse(request))),
            chatGptArtifactsPath: rootPath);
    }

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
        new("Swords to Plowshares", "{W}", "Instant", "Exile target creature. Its controller gains life equal to its power.", null, null, null, ["W"], null, null, null),
        new("Smothering Tithe", "{3}{W}", "Enchantment", "Whenever an opponent draws a card, that player may pay {2}. If the player doesn't, you create a Treasure token.", null, null, ["Treasure"], ["W"], null, null, null),
        new("Atraxa, Praetors' Voice", "{G}{W}{U}{B}", "Legendary Creature — Phyrexian Angel Horror", "Flying, vigilance, deathtouch, lifelink. At the beginning of your end step, proliferate.", "4", "4", ["Flying", "Vigilance", "Deathtouch", "Lifelink", "Proliferate"], ["G", "W", "U", "B"], null, null, null),
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
        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<DeckEntry>());
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

}
