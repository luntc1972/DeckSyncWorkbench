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

public sealed class ChatGptDeckPacketServiceTests
{
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
        Assert.Contains("commander_status", result.ProbePromptText);
        Assert.Contains("legendary creature or a legendary artifact", result.ProbePromptText);
        Assert.Contains("enter one before continuing", result.ProbePromptText);
        Assert.DoesNotContain("unknown_mechanics", result.ProbePromptText);
        Assert.Contains("```json", result.ProbePromptText);
        Assert.Contains("Atraxa, Praetors' Voice", result.ProbePromptText);
        Assert.Contains("Sol Ring", result.ProbePromptText);
        Assert.Contains("\"game_plan\"", result.DeckProfileSchemaJson);
    }

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
        Assert.Contains("What are the strengths and weaknesses of this deck?", result.AnalysisPromptText);
        Assert.Contains("How consistent is this deck?", result.AnalysisPromptText);
        Assert.Contains("Is Sol Ring worth including in this deck?", result.AnalysisPromptText);
        Assert.Contains("Bracket 3: Upgraded", result.AnalysisPromptText);
        Assert.Contains("Expect to play at least six turns before you win or lose.", result.AnalysisPromptText);
        Assert.Contains("```json", result.AnalysisPromptText);
    }

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
            SetName = "Test Set",
            SetPacketText = "SET: Test Set\nCARDS:\nTest Card | 2G | Creature | Example text."
        });

        Assert.NotNull(result.SetUpgradePromptText);
        Assert.Contains("Do not recommend cards from the official Commander banned list.", result.SetUpgradePromptText);
        Assert.Contains("Dockside Extortionist", result.SetUpgradePromptText);
        Assert.Contains("real upgrades", result.SetUpgradePromptText);
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

    private static ChatGptDeckPacketService CreateService(string? contentRootPath = null)
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
            new FakeWebHostEnvironment(rootPath),
            executeCollectionAsync: (request, _) => Task.FromResult(CreateCollectionResponse(request)));
    }

    private static RestResponse<ScryfallCollectionResponse> CreateCollectionResponse(RestRequest request)
    {
        return new RestResponse<ScryfallCollectionResponse>(request)
        {
            StatusCode = HttpStatusCode.OK,
            Data = new ScryfallCollectionResponse(
                new List<ScryfallCard>
                {
                    new("Sol Ring", "{1}", "Artifact", "{T}: Add {C}{C}.", null, null, null, null, null, null),
                    new("Atraxa, Praetors' Voice", "{G}{W}{U}{B}", "Legendary Creature — Phyrexian Angel Horror", "Flying, vigilance, deathtouch, lifelink. At the beginning of your end step, proliferate.", "4", "4", ["Flying", "Vigilance", "Deathtouch", "Lifelink", "Proliferate"], null, null, null)
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
                [new ScryfallSetOption("dsk", "Test Set", "2026-01-01", "expansion", 2)]);

        public Task<string> BuildSetPacketAsync(IReadOnlyList<string> setCodes, CancellationToken cancellationToken = default)
            => Task.FromResult("""
set_packet:
generated_at_utc: 2026-03-26T00:00:00Z
sets:
- Test Set (DSK)

mechanics:
Survival: A test mechanic summary.

set: Test Set (DSK)
cards:
Test Card | 1W | Creature | Survival — Test text.
""");
    }

    private sealed class FakeCommanderBanListService : ICommanderBanListService
    {
        public Task<IReadOnlyList<string>> GetBannedCardsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(["Dockside Extortionist", "Mana Crypt"]);
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
