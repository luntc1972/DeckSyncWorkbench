using System.Net;
using System.IO;
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

/// <summary>
/// Covers staged prompt generation, validation, and artifact output for the ChatGPT workflow.
/// </summary>
public sealed class ChatGptDeckPacketServiceTests
{
    /// <summary>
    /// Builds the initial probe prompt and schema from pasted deck text.
    /// </summary>
    [Fact]
    public async Task BuildAsync_GeneratesProbePrompt_ForPastedDeckText()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
        {
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
"""
        });

        Assert.Contains("unknown_cards", result.ProbePromptText);
        Assert.DoesNotContain("unknown_mechanics", result.ProbePromptText);
        Assert.Contains("```json", result.ProbePromptText);
        Assert.Contains("Atraxa, Praetors' Voice", result.ProbePromptText);
        Assert.Contains("Sol Ring", result.ProbePromptText);
        Assert.Contains("Suggested chat title: Atraxa, Praetors' Voice | AI Deck Analysis", result.ProbePromptText);
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

        var probeText = result.ProbePromptText.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("Commander\n1 Atraxa, Praetors' Voice [Commander]", probeText);
        Assert.Contains("Decklist\n1 Arcane Signet\n1 Sol Ring", probeText);
        Assert.Contains("Sideboard\n1 Swords to Plowshares", probeText);
        Assert.Contains("Maybeboard\n1 Smothering Tithe", probeText);
        Assert.Contains("Treat the Commander and Decklist sections as the actual deck being evaluated.", probeText);
        Assert.Contains("Treat the Sideboard and Maybeboard sections only as candidate additions, not part of the current deck.", probeText);
        Assert.Contains("Main deck cards: 2", result.InputSummary);
        Assert.Contains("Commander cards: 1", result.InputSummary);
        Assert.Contains("Sideboard cards: 1", result.InputSummary);
        Assert.Contains("Maybeboard cards: 1", result.InputSummary);
    }

    /// <summary>
    /// Preserves set code and collector number in probe card lines, normalizes DFC names to the
    /// front face, and annotates the commander line with [Commander].
    /// </summary>
    [Fact]
    public async Task BuildAsync_PreservesSetInfoAndNormalizesDfcNamesInProbePrompt()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
        {
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice (ONE) 196

1 Sol Ring (2ED) 271
1 Delver of Secrets // Insectile Aberration (ISD) 51
"""
        });

        var probeText = result.ProbePromptText.Replace("\r\n", "\n", StringComparison.Ordinal);
        // Commander line: set code preserved and [Commander] annotation added
        Assert.Contains("1 Atraxa, Praetors' Voice (ONE) 196 [Commander]", probeText);
        // Mainboard line: set code preserved
        Assert.Contains("1 Sol Ring (2ED) 271", probeText);
        // DFC: only front-face name used, set code preserved, no back-face name
        Assert.Contains("1 Delver of Secrets (ISD) 51", probeText);
        Assert.DoesNotContain("Insectile Aberration", probeText);
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
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
 "unknown_cards": ["Sol Ring"]
}
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
        Assert.Contains("Do not recommend cards from the official Commander banned list.", result.AnalysisPromptText);
        Assert.Contains("Dockside Extortionist", result.AnalysisPromptText);
        Assert.Contains("including any newly supplied keywords or mechanics", result.AnalysisPromptText);
        Assert.Contains("What are the strengths and weaknesses of this deck?", result.AnalysisPromptText);
        Assert.Contains("How consistent is this deck?", result.AnalysisPromptText);
        Assert.Contains("Is Sol Ring worth including in this deck?", result.AnalysisPromptText);
        Assert.Contains("1. top adds", result.AnalysisPromptText);
        Assert.Contains("2. top cuts", result.AnalysisPromptText);
        Assert.Contains("For every recommended add and cut, explain the reasoning briefly", result.AnalysisPromptText);
        Assert.Contains("Bracket 3: Upgraded", result.AnalysisPromptText);
        Assert.Contains("Expect to play at least six turns before you win or lose.", result.AnalysisPromptText);
        Assert.Contains("```json", result.AnalysisPromptText);
    }

    /// <summary>
    /// Requires a target Commander bracket before the analysis packet can be generated.
    /// </summary>
    [Fact]
    public async Task BuildAsync_ThrowsValidationError_WhenProbeJsonProvidedWithoutTargetBracket()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptDeckRequest
        {
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
  "unknown_cards": ["Sol Ring"]
}
"""
        }));

        Assert.Equal("Choose a target Commander bracket before generating the analysis packet.", exception.Message);
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
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
  "unknown_cards": ["Sol Ring"]
}
""",
            TargetCommanderBracket = "Upgraded"
        }));

        Assert.Equal("Select at least one analysis question before generating the analysis packet.", exception.Message);
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
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
  "unknown_cards": ["Sol Ring"]
}
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
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
  "unknown_cards": ["Sol Ring"]
}
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
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
 "unknown_cards": ["Sol Ring"]
}
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
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
 "unknown_cards": ["Sol Ring"]
}
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["bracket-3-version", "three-upgrade-paths"]
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Create a Bracket 3 version of this deck.", result.AnalysisPromptText);
        Assert.Contains("Create 3 different upgrade-path versions of this deck.", result.AnalysisPromptText);
        Assert.Contains("you must output the full, complete 100-card Commander decklist", result.AnalysisPromptText);
        Assert.Contains("exactly 1 commander and 99 other cards", result.AnalysisPromptText);
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
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
 "unknown_cards": []
}
""",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["bracket-2-version"]
        });

        Assert.NotNull(result.AnalysisPromptText);
        Assert.Contains("Create a Bracket 2 version of this deck.", result.AnalysisPromptText);
        Assert.Contains("you must output the full, complete 100-card Commander decklist", result.AnalysisPromptText);
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
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
 "unknown_cards": []
}
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
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
 "unknown_cards": []
}
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
    /// as the commander so the probe prompt's suggested title and deck_context are populated.
    /// </summary>
    [Fact]
    public async Task BuildAsync_DetectsCommanderFromLeadingEntries_WhenNoCommanderSectionHeader()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
        {
            DeckSource = """
1 Atraxa, Praetors' Voice
1 Sol Ring
1 Arcane Signet
"""
        });

        Assert.Contains("Atraxa, Praetors' Voice", result.ProbePromptText);
        Assert.Contains("Suggested chat title: Atraxa, Praetors' Voice | AI Deck Analysis", result.ProbePromptText);
        var probeText = result.ProbePromptText.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("Commander\n1 Atraxa, Praetors' Voice [Commander]", probeText);
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

        var probeText = result.ProbePromptText.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("Tymna the Weaver [Commander]", probeText);
        Assert.Contains("Thrasios, Triton Hero [Commander]", probeText);
    }

    [Fact]
    public async Task BuildAsync_IncludesBothFacesForDoubleFacedCardsInReferenceText()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
        {
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
 "unknown_cards": ["Blex, Vexing Pest // Search for Blex"]
}
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
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
 "unknown_cards": ["Ya viene el coco"]
}
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
        Assert.Contains("Do not recommend cards from the official Commander banned list.", result.SetUpgradePromptText);
        Assert.Contains("Dockside Extortionist", result.SetUpgradePromptText);
        Assert.Contains("per-set analysis", result.SetUpgradePromptText);
        Assert.Contains("top adds from that set", result.SetUpgradePromptText);
        Assert.Contains("suggested removals for each add from that set", result.SetUpgradePromptText);
        Assert.Contains("For every recommended add or cut, explain the reasoning briefly", result.SetUpgradePromptText);
        Assert.Contains("set_upgrade_report", result.SetUpgradePromptText);
        Assert.Contains("```json", result.SetUpgradePromptText);
        Assert.Contains("\"final_shortlist\"", result.SetUpgradePromptText);
        Assert.Contains("discussion_summary.txt", result.SetUpgradePromptText);
        Assert.Contains("```text", result.SetUpgradePromptText);
        Assert.Contains("per-set analysis in condensed form", result.SetUpgradePromptText);
        Assert.Contains("SET: Test Set", result.SetUpgradePromptText);
        Assert.Contains("\"game_plan\": \"Midrange value\"", result.SetUpgradePromptText);
    }

    [Fact]
    public async Task BuildAsync_UsesGeneratedSetPacket_WhenSetCodesSelected()
    {
        var service = CreateService();

        var result = await service.BuildAsync(new ChatGptDeckRequest
        {
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
        Assert.Contains("set_packet:", result.SetUpgradePromptText);
        Assert.Contains("Test Set (DSK)", result.SetUpgradePromptText);
        Assert.Contains("Survival", result.SetUpgradePromptText);
        Assert.Contains("final cross-set ranked shortlist", result.SetUpgradePromptText);
        Assert.Contains("set_upgrade_report", result.SetUpgradePromptText);
        Assert.Contains("\"sets\": [", result.SetUpgradePromptText);
        Assert.Contains("discussion_summary.txt", result.SetUpgradePromptText);
        Assert.Contains("per-set analysis in condensed form", result.SetUpgradePromptText);
        Assert.DoesNotContain("Off Color Test Card", result.SetUpgradePromptText);
        Assert.DoesNotContain("Paste the condensed set packet", result.SetUpgradePromptText);
    }

    [Fact]
    public async Task BuildAsync_SavesArtifactsToDisk_WhenRequested()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), "MtgDeckStudioTests", Guid.NewGuid().ToString("N"));
        var service = CreateService(artifactsRoot);

        var result = await service.BuildAsync(new ChatGptDeckRequest
        {
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
```json
{
  "unknown_cards": ["Sol Ring"]
}
```
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
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "10-probe-prompt.txt")));
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "20-probe-response.json")));
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "31-analysis-prompt.txt")));
        Assert.True(File.Exists(Path.Combine(result.SavedArtifactsDirectory!, "01-request-context.txt")));
        var combinedPrompts = await File.ReadAllTextAsync(Path.Combine(result.SavedArtifactsDirectory!, "all-prompts.txt"));
        Assert.Contains("===== PROBE PROMPT", combinedPrompts);
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
        var artifactsRoot = Path.Combine(Path.GetTempPath(), "MtgDeckStudioTests", Guid.NewGuid().ToString("N"));
        var service = CreateService(artifactsRoot);

        var result = await service.BuildAsync(new ChatGptDeckRequest
        {
            DeckSource = """
Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet
""",
            ProbeResponseJson = """
{
  "unknown_cards": ["Sol Ring"]
}
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

    private static ChatGptDeckPacketService CreateService(
        string? contentRootPath = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeSearchAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCard>>>? executeNamedAsync = null)
    {
        var rootPath = contentRootPath ?? Path.Combine(Path.GetTempPath(), "MtgDeckStudioTests", Guid.NewGuid().ToString("N"));
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
            new FakeWebHostEnvironment(rootPath),
            executeCollectionAsync: executeCollectionAsync ?? ((request, _) => Task.FromResult(CreateCollectionResponse(request))),
            executeSearchAsync: executeSearchAsync,
            executeNamedAsync: executeNamedAsync,
            chatGptArtifactsPath: rootPath);
    }

    private static RestResponse<ScryfallCollectionResponse> CreateCollectionResponse(RestRequest request)
    {
        return new RestResponse<ScryfallCollectionResponse>(request)
        {
            StatusCode = HttpStatusCode.OK,
            Data = new ScryfallCollectionResponse(
                new List<ScryfallCard>
                {
                    new("Sol Ring", "{1}", "Artifact", "{T}: Add {C}{C}.", null, null, null, [], null, null, null),
                    new("Atraxa, Praetors' Voice", "{G}{W}{U}{B}", "Legendary Creature — Phyrexian Angel Horror", "Flying, vigilance, deathtouch, lifelink. At the beginning of your end step, proliferate.", "4", "4", ["Flying", "Vigilance", "Deathtouch", "Lifelink", "Proliferate"], ["G", "W", "U", "B"], null, null, null),
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
                },
                [])
        };
    }

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

    private sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MtgDeckStudio.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
