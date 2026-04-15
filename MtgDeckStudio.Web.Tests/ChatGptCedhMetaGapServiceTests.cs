using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Normalization;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Services;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class ChatGptCedhMetaGapServiceTests
{
    [Fact]
    public async Task BuildAsync_ParsesSavedResponseWithoutReloadingDeck()
    {
        var service = CreateService(
            new FakeMoxfieldDeckImporter(),
            new FakeArchidektDeckImporter(),
            new FakeEdhTop16Client());

        var result = await service.BuildAsync(new ChatGptCedhMetaGapRequest
        {
            WorkflowStep = 3,
            MetaGapResponseJson = """
                ```json
                {
                  "meta_gap": {
                    "commander": "Tymna / Kraum",
                    "ref_deck_count": 3,
                    "meta_summary": "Play more stack interaction.",
                    "optimization_path": "Trim clunkier cards."
                  }
                }
                ```
                """
        });

        Assert.NotNull(result.AnalysisResponse);
        Assert.Equal("Tymna / Kraum", result.AnalysisResponse!.MetaGap.Commander);
        Assert.Equal(3, result.AnalysisResponse.MetaGap.RefDeckCount);
        Assert.Equal("Play more stack interaction.", result.AnalysisResponse.MetaGap.MetaSummary);
        Assert.Equal("Trim clunkier cards.", result.AnalysisResponse.MetaGap.OptimizationPath);
        Assert.NotNull(result.SchemaJson);
        Assert.StartsWith("{", result.SchemaJson);
        Assert.Empty(result.FetchedEntries);
        Assert.Null(result.PromptText);
    }

    [Fact]
    public async Task BuildAsync_ParsesFencedResponseWithTrailingFenceNoise()
    {
        var service = CreateService(
            new FakeMoxfieldDeckImporter(),
            new FakeArchidektDeckImporter(),
            new FakeEdhTop16Client());

        var result = await service.BuildAsync(new ChatGptCedhMetaGapRequest
        {
            WorkflowStep = 3,
            MetaGapResponseJson = """
                ```json
                {
                  "meta_gap": {
                    "commander": "Tivit, Seller of Secrets",
                    "ref_deck_count": 4,
                    "meta_summary": "Closer to the midrange baseline than the turbo baseline.",
                    "optimization_path": "Raise free interaction density before adding extra win-more slots."
                  }
                }
                ```
                ```
                """
        });

        Assert.NotNull(result.AnalysisResponse);
        Assert.Equal("Tivit, Seller of Secrets", result.AnalysisResponse!.MetaGap.Commander);
        Assert.Equal(4, result.AnalysisResponse.MetaGap.RefDeckCount);
        Assert.Equal("Closer to the midrange baseline than the turbo baseline.", result.AnalysisResponse.MetaGap.MetaSummary);
    }

    [Fact]
    public async Task BuildAsync_GeneratesPromptFromDeckAndSortedReferenceEntries()
    {
        var importer = new FakeMoxfieldDeckImporter(new List<DeckEntry>
        {
            CreateDeckEntry("Kinnan, Bonder Prodigy", "commander"),
            CreateDeckEntry("Sol Ring"),
            CreateDeckEntry("Llanowar Elves")
        });

        var edhTop16Client = new FakeEdhTop16Client(
            new EdhTop16Entry
            {
                Standing = 2,
                PlayerName = "Later Pilot",
                TournamentName = "Modern Meta Cup",
                TournamentDate = new DateOnly(2026, 4, 10),
                MainDeck = new[]
                {
                    new EdhTop16Card { Name = "Mox Diamond", Type = "Artifact" },
                    new EdhTop16Card { Name = "Mana Crypt", Type = "Artifact" }
                }
            },
            new EdhTop16Entry
            {
                Standing = 1,
                PlayerName = "Earlier Pilot",
                TournamentName = "Open",
                TournamentDate = new DateOnly(2026, 3, 1),
                MainDeck = new[]
                {
                    new EdhTop16Card { Name = "Birds of Paradise", Type = "Creature" }
                }
            });

        var service = CreateService(importer, new FakeArchidektDeckImporter(), edhTop16Client);

        var result = await service.BuildAsync(new ChatGptCedhMetaGapRequest
        {
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-list",
            SelectedReferenceIndexes = new List<int> { 0, 1 }
        });

        Assert.Equal("Kinnan, Bonder Prodigy", result.ResolvedCommanderName);
        Assert.Equal(new[] { "Later Pilot", "Earlier Pilot" }, result.FetchedEntries.Select(entry => entry.PlayerName));
        Assert.Contains("Commander: Kinnan, Bonder Prodigy", result.InputSummary);
        Assert.Contains("Fetched EDH Top 16 entries: 2", result.InputSummary);
        Assert.Contains("Title this chat: Kinnan, Bonder Prodigy | cEDH Meta Gap Analysis", result.PromptText);
        Assert.Contains("Compare MY_DECK against 2 REF deck(s). Read every supplied decklist before answering.", result.PromptText);
        Assert.Contains("Return a single fenced ```json block only. No prose before or after.", result.PromptText);
        Assert.Contains("Top-level object must be meta_gap. Fill every field.", result.PromptText);
        Assert.Contains("Use empty strings, 0, 0.0, false, or [] when evidence is missing.", result.PromptText);
        Assert.Contains("1 Llanowar Elves", result.PromptText);
        Assert.Contains("1 Sol Ring", result.PromptText);
        Assert.Contains("R1 (Later Pilot, #2, Modern Meta Cup, 2026-04-10):", result.PromptText);
        Assert.Contains("\"meta_gap\"", result.SchemaJson);
        Assert.Equal("Kinnan, Bonder Prodigy", edhTop16Client.LastCommanderName);
    }

    [Fact]
    public async Task BuildAsync_RejectsMoreThanFiveSelectedReferences()
    {
        var entries = Enumerable.Range(1, 6)
            .Select(index => new EdhTop16Entry
            {
                Standing = index,
                PlayerName = $"Pilot {index}",
                TournamentDate = new DateOnly(2026, 4, index),
                MainDeck = new[] { new EdhTop16Card { Name = $"Card {index}", Type = "Spell" } }
            })
            .ToArray();

        var service = CreateService(
            new FakeMoxfieldDeckImporter(new List<DeckEntry>
            {
                CreateDeckEntry("Kinnan, Bonder Prodigy", "commander"),
                CreateDeckEntry("Sol Ring")
            }),
            new FakeArchidektDeckImporter(),
            new FakeEdhTop16Client(entries));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new ChatGptCedhMetaGapRequest
        {
            WorkflowStep = 2,
            DeckSource = "https://www.moxfield.com/decks/test-list",
            SelectedReferenceIndexes = new List<int> { 0, 1, 2, 3, 4, 5 }
        }));

        Assert.Equal("Select no more than 5 EDH Top 16 reference decks before generating the prompt.", exception.Message);
    }

    private static ChatGptCedhMetaGapService CreateService(
        IMoxfieldDeckImporter moxfieldDeckImporter,
        IArchidektDeckImporter archidektDeckImporter,
        IEdhTop16Client edhTop16Client)
        => new(
            moxfieldDeckImporter,
            archidektDeckImporter,
            new MoxfieldParser(),
            new ArchidektParser(),
            edhTop16Client);

    private static DeckEntry CreateDeckEntry(string name, string board = "mainboard")
        => new()
        {
            Name = name,
            NormalizedName = CardNormalizer.Normalize(name),
            Quantity = 1,
            Board = board
        };

    private sealed class FakeMoxfieldDeckImporter : IMoxfieldDeckImporter
    {
        private readonly List<DeckEntry> _entries;

        public FakeMoxfieldDeckImporter(List<DeckEntry>? entries = null)
        {
            _entries = entries ?? new List<DeckEntry>();
        }

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries);
    }

    private sealed class FakeArchidektDeckImporter : IArchidektDeckImporter
    {
        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<DeckEntry>());
    }

    private sealed class FakeEdhTop16Client : IEdhTop16Client
    {
        private readonly IReadOnlyList<EdhTop16Entry> _entries;

        public FakeEdhTop16Client(params EdhTop16Entry[] entries)
        {
            _entries = entries;
        }

        public string? LastCommanderName { get; private set; }

        public Task<IReadOnlyList<EdhTop16Entry>> SearchCommanderEntriesAsync(
            string commanderName,
            CedhMetaTimePeriod timePeriod,
            CedhMetaSortBy sortBy,
            int minEventSize,
            int? maxStanding,
            int count,
            CancellationToken cancellationToken = default)
        {
            LastCommanderName = commanderName;
            return Task.FromResult(_entries);
        }
    }
}
