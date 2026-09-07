using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Bracket;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Web.Controllers;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Bracket;
using DeckFlow.Web.Services.Manabase;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Verifies how explicit and fallback brackets resolve for the manabase community baseline.
/// </summary>
public sealed class ManabaseBracketResolutionTests
{
    [Fact]
    public async Task AnalyzeAsync_ExplicitAutoBracket_PreservesAutoSource()
    {
        ManabaseAnalysisResult result = await AnalyzeAsync(
            new ManabaseAnalysisOptions
            {
                Mode = ManabaseMode.Casual,
                Bracket = 4,
                BracketSource = ManabaseBracketSource.Auto,
            },
            new Dictionary<int, ManabaseBracketBaseline>
            {
                [4] = new() { Bracket = 4, AvgLands = 33.8, DeckCount = 8123, Source = "edhrec-pilot-aggregate" },
            });

        Assert.NotNull(result.Report);
        Assert.NotNull(result.CommunityBaseline);
        Assert.Equal(4, result.CommunityBaseline!.Bracket);
        Assert.Equal(ManabaseBracketSource.Auto, result.CommunityBaseline.BracketSource);
    }

    [Fact]
    public async Task AnalyzeAsync_ExplicitOverrideBracket_PreservesOverrideSource()
    {
        ManabaseAnalysisResult result = await AnalyzeAsync(
            new ManabaseAnalysisOptions
            {
                Mode = ManabaseMode.Casual,
                Bracket = 3,
                BracketSource = ManabaseBracketSource.Override,
            },
            new Dictionary<int, ManabaseBracketBaseline>
            {
                [3] = new() { Bracket = 3, AvgLands = 35.1, DeckCount = 19044, Source = "edhrec-pilot-aggregate" },
            });

        Assert.NotNull(result.Report);
        Assert.NotNull(result.CommunityBaseline);
        Assert.Equal(3, result.CommunityBaseline!.Bracket);
        Assert.Equal(ManabaseBracketSource.Override, result.CommunityBaseline.BracketSource);
    }

    [Fact]
    public async Task AnalyzeAsync_NullBracket_UsesModeFallbackSource()
    {
        ManabaseAnalysisResult result = await AnalyzeAsync(
            new ManabaseAnalysisOptions
            {
                Mode = ManabaseMode.Focused,
            },
            new Dictionary<int, ManabaseBracketBaseline>
            {
                [3] = new() { Bracket = 3, AvgLands = 35.1, DeckCount = 19044, Source = "edhrec-pilot-aggregate" },
            });

        Assert.NotNull(result.Report);
        Assert.NotNull(result.CommunityBaseline);
        Assert.Equal(3, result.CommunityBaseline!.Bracket);
        Assert.Equal(ManabaseBracketSource.Fallback, result.CommunityBaseline.BracketSource);
    }

    [Fact]
    public async Task AnalyzeAsync_BracketTwo_StrongCommanderCell_UsesCommanderValue()
    {
        ManabaseAnalysisResult result = await AnalyzeAsync(
            new ManabaseAnalysisOptions
            {
                Mode = ManabaseMode.Casual,
                Bracket = 2,
            },
            new Dictionary<int, ManabaseBracketBaseline>
            {
                [2] = new() { Bracket = 2, AvgLands = 35.9, DeckCount = 124221, Source = "edhrec-pilot-aggregate" },
            },
            new Dictionary<string, ManabaseCommanderBaseline>
            {
                ["Kinnan, Bonder Prodigy"] = new() { Name = "Kinnan, Bonder Prodigy", AvgLands = 33, DeckCount = 450 },
            });

        Assert.NotNull(result.Report);
        Assert.NotNull(result.CommunityBaseline);
        Assert.Equal(ManabaseBaselineSource.Commander, result.CommunityBaseline!.ValueSource);
        Assert.Equal(33, result.CommunityBaseline.AvgLands, 3);
        Assert.Equal(450, result.CommunityBaseline.DeckCount);
        Assert.Equal("Kinnan, Bonder Prodigy", result.CommunityBaseline.CommanderDisplayName);
        Assert.Equal("edhrec-averages", result.CommunityBaseline.Source);
    }

    [Fact]
    public async Task AnalyzeAsync_BracketThree_MidSample_UsesBlendedValue()
    {
        ManabaseAnalysisResult result = await AnalyzeAsync(
            new ManabaseAnalysisOptions
            {
                Mode = ManabaseMode.Focused,
                Bracket = 3,
            },
            new Dictionary<int, ManabaseBracketBaseline>
            {
                [3] = new() { Bracket = 3, AvgLands = 35.5, DeckCount = 140632, Source = "edhrec-pilot-aggregate" },
            },
            new Dictionary<string, ManabaseCommanderBaseline>
            {
                ["Kinnan, Bonder Prodigy"] = new() { Name = "Kinnan, Bonder Prodigy", AvgLands = 34, DeckCount = 250 },
            });

        Assert.NotNull(result.Report);
        Assert.NotNull(result.CommunityBaseline);
        Assert.Equal(ManabaseBaselineSource.Blended, result.CommunityBaseline!.ValueSource);
        Assert.Equal(((150d / 300d) * 34) + ((150d / 300d) * 35.5), result.CommunityBaseline.AvgLands, 3);
        Assert.Equal(250, result.CommunityBaseline.DeckCount);
        Assert.Equal("Kinnan, Bonder Prodigy", result.CommunityBaseline.CommanderDisplayName);
        Assert.Equal("edhrec-averages", result.CommunityBaseline.Source);
    }

    [Fact]
    public async Task AnalyzeAsync_BracketTwo_ThinCommanderCell_FallsBackToGlobal()
    {
        ManabaseAnalysisResult result = await AnalyzeAsync(
            new ManabaseAnalysisOptions
            {
                Mode = ManabaseMode.Casual,
                Bracket = 2,
            },
            new Dictionary<int, ManabaseBracketBaseline>
            {
                [2] = new() { Bracket = 2, AvgLands = 35.9, DeckCount = 124221, Source = "edhrec-pilot-aggregate" },
            },
            new Dictionary<string, ManabaseCommanderBaseline>
            {
                ["Kinnan, Bonder Prodigy"] = new() { Name = "Kinnan, Bonder Prodigy", AvgLands = 33, DeckCount = 50 },
            });

        Assert.NotNull(result.Report);
        Assert.NotNull(result.CommunityBaseline);
        Assert.Equal(ManabaseBaselineSource.Global, result.CommunityBaseline!.ValueSource);
        Assert.Equal(35.9, result.CommunityBaseline.AvgLands, 3);
        Assert.Equal(124221, result.CommunityBaseline.DeckCount);
        Assert.Null(result.CommunityBaseline.CommanderDisplayName);
        Assert.Equal("edhrec-pilot-aggregate", result.CommunityBaseline.Source);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task AnalyzeAsync_HighBrackets_IgnoreCommanderCell(int bracket)
    {
        ManabaseAnalysisResult result = await AnalyzeAsync(
            new ManabaseAnalysisOptions
            {
                Mode = bracket == 5 ? ManabaseMode.Cedh : ManabaseMode.Casual,
                Bracket = bracket,
            },
            new Dictionary<int, ManabaseBracketBaseline>
            {
                [bracket] = new() { Bracket = bracket, AvgLands = bracket == 5 ? 30.5 : 33.8, DeckCount = 8123, Source = "edhrec-pilot-aggregate" },
            },
            new Dictionary<string, ManabaseCommanderBaseline>
            {
                ["Kinnan, Bonder Prodigy"] = new() { Name = "Kinnan, Bonder Prodigy", AvgLands = 35, DeckCount = 48000 },
            });

        Assert.NotNull(result.Report);
        Assert.NotNull(result.CommunityBaseline);
        Assert.Equal(ManabaseBaselineSource.Global, result.CommunityBaseline!.ValueSource);
        Assert.Null(result.CommunityBaseline.CommanderDisplayName);
        Assert.Equal("edhrec-pilot-aggregate", result.CommunityBaseline.Source);
    }

    [Fact]
    public async Task AnalyzeAsync_NoResolvedCommander_UsesGlobal()
    {
        ManabaseAnalysisResult result = await AnalyzeAsync(
            new ManabaseAnalysisOptions
            {
                Mode = ManabaseMode.Casual,
                Bracket = 2,
            },
            new Dictionary<int, ManabaseBracketBaseline>
            {
                [2] = new() { Bracket = 2, AvgLands = 35.9, DeckCount = 124221, Source = "edhrec-pilot-aggregate" },
            },
            new Dictionary<string, ManabaseCommanderBaseline>
            {
                ["Kinnan, Bonder Prodigy"] = new() { Name = "Kinnan, Bonder Prodigy", AvgLands = 33, DeckCount = 450 },
            },
            entries: FixtureWithoutCommander().Entries,
            cards: FixtureWithoutCommander().Cards);

        Assert.NotNull(result.Report);
        Assert.NotNull(result.CommunityBaseline);
        Assert.Equal(ManabaseBaselineSource.Global, result.CommunityBaseline!.ValueSource);
        Assert.Equal(35.9, result.CommunityBaseline.AvgLands, 3);
        Assert.Null(result.CommunityBaseline.CommanderDisplayName);
    }

    [Fact]
    public async Task AnalyzeAsync_CedhMetaRange_SuppressesCommunityBaseline()
    {
        ManabaseAnalysisResult result = await AnalyzeAsync(
            new ManabaseAnalysisOptions
            {
                Mode = ManabaseMode.Cedh,
                Bracket = 5,
            },
            new Dictionary<int, ManabaseBracketBaseline>
            {
                [5] = new() { Bracket = 5, AvgLands = 30.5, DeckCount = 4761, Source = "edhrec-pilot-aggregate" },
            },
            commanderRows: new Dictionary<string, ManabaseCommanderBaseline>(),
            flags: new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.BaselineFlagKey] = true,
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = true,
            },
            cedhLandBaseline: new FakeCedhLandBaselineProvider(success: true, mean: 30.2, deckCount: 42, sd: 1.3, generated: "2026-07"));

        Assert.NotNull(result.Report);
        Assert.NotNull(result.Report!.TargetLandsRangeLow);
        Assert.NotNull(result.Report.TargetLandsRangeHigh);
        Assert.NotNull(result.Report.BaselineLandsMean);
        Assert.NotNull(result.Report.BaselineDeckCount);
        Assert.NotNull(result.Report.BaselineLandsSd);
        Assert.Null(result.CommunityBaseline);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(3, 3)]
    [InlineData(6, null)]
    public async Task Post_NormalizesBracketToSupportedRange_AndWritesBackOntoRequest(
        int postedBracket,
        int? expectedBracket)
    {
        var service = new CapturingAnalysisService(BuildReport(ManabaseMode.Casual));
        ManabaseController controller = BuildController(service);
        var request = new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            Bracket = postedBracket,
        };

        IActionResult result = await controller.Manabase(request);

        Assert.Equal(expectedBracket, request.Bracket);
        Assert.Equal(expectedBracket, ModelOf(result).Request.Bracket);
    }

    [Fact]
    public async Task Post_ExplicitBracket_UsesOverrideSource_AndSkipsClassifier()
    {
        var service = new CapturingAnalysisService(BuildReport(ManabaseMode.Casual));
        var classifier = new FakeBracketClassificationService
        {
            Result = FakeBracketClassificationService.CreateResult(4),
        };
        ManabaseController controller = BuildController(
            service,
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.BaselineFlagKey] = true,
            }),
            classifier);

        await controller.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            Bracket = 5,
        });

        Assert.NotNull(service.LastOptions);
        Assert.Equal(5, service.LastOptions!.Bracket);
        Assert.Equal(ManabaseBracketSource.Override, service.LastOptions.BracketSource);
        Assert.Equal(0, classifier.CallCount);
    }

    [Fact]
    public async Task Post_FlagOnWithoutOverride_AutoClassifies_AndMapsBracketOneToTwo()
    {
        var service = new CapturingAnalysisService(BuildReport(ManabaseMode.Casual));
        var classifier = new FakeBracketClassificationService
        {
            Result = FakeBracketClassificationService.CreateResult(1),
        };
        ManabaseController controller = BuildController(
            service,
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.BaselineFlagKey] = true,
            }),
            classifier);

        await controller.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            DeckName = "Bracket Deck",
        });

        Assert.NotNull(service.LastOptions);
        Assert.Equal(2, service.LastOptions!.Bracket);
        Assert.Equal(ManabaseBracketSource.Auto, service.LastOptions.BracketSource);
        Assert.Equal(1, classifier.CallCount);
        Assert.Equal("1 Sol Ring", classifier.LastDeckSource);
        Assert.Equal("Bracket Deck", classifier.LastDeckName);
    }

    [Fact]
    public async Task Post_FlagOnClassifierFailure_FallsBackToModeDerivedBracket()
    {
        var service = new CapturingAnalysisService(BuildReport(ManabaseMode.Casual));
        var classifier = new FakeBracketClassificationService
        {
            Exception = new InvalidOperationException("boom"),
        };
        ManabaseController controller = BuildController(
            service,
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.BaselineFlagKey] = true,
            }),
            classifier);

        await controller.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
        });

        Assert.NotNull(service.LastOptions);
        Assert.Null(service.LastOptions!.Bracket);
        Assert.Null(service.LastOptions.BracketSource);
        Assert.Equal(1, classifier.CallCount);
    }

    [Fact]
    public async Task Post_FlagOff_DoesNotCallClassifier_AndLeavesBracketNull()
    {
        var service = new CapturingAnalysisService(BuildReport(ManabaseMode.Casual));
        var classifier = new FakeBracketClassificationService
        {
            Result = FakeBracketClassificationService.CreateResult(4),
        };
        ManabaseController controller = BuildController(
            service,
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.BaselineFlagKey] = false,
            }),
            classifier);

        IActionResult result = await controller.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
        });

        Assert.NotNull(service.LastOptions);
        Assert.Null(service.LastOptions!.Bracket);
        Assert.Null(service.LastOptions.BracketSource);
        Assert.Equal(0, classifier.CallCount);
        Assert.False(ModelOf(result).ShowCommunityBaseline);
    }

    private static async Task<ManabaseAnalysisResult> AnalyzeAsync(
        ManabaseAnalysisOptions options,
        IReadOnlyDictionary<int, ManabaseBracketBaseline> rows,
        IReadOnlyDictionary<string, ManabaseCommanderBaseline>? commanderRows = null,
        IReadOnlyDictionary<string, bool>? flags = null,
        IReadOnlyList<DeckEntry>? entries = null,
        IReadOnlyList<ScryfallCard>? cards = null,
        ICedhLandBaselineProvider? cedhLandBaseline = null)
    {
        (List<DeckEntry> fixtureEntries, List<ScryfallCard> fixtureCards) = Fixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader((entries ?? fixtureEntries).ToList()),
            new FakeResolver((cards ?? fixtureCards).ToList()),
            new FakeFeatureFlagCache((flags ?? new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.BaselineFlagKey] = true,
            }).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)),
            manabaseBaseline: new FakeManabaseBaselineProvider(rows, commanderRows),
            cedhLandBaseline: cedhLandBaseline);

        return await service.AnalyzeAsync("paste", "Baseline Deck", options);
    }

    private static ManabaseController BuildController(
        IManabaseAnalysisService service,
        FakeFeatureFlagCache? featureFlags = null,
        IBracketClassificationService? bracketClassificationService = null)
    {
        var controller = new ManabaseController(
            service,
            new StubCardSearchService(),
            featureFlags ?? new FakeFeatureFlagCache(),
            bracketClassificationService ?? new FakeBracketClassificationService(),
            NullLogger<ManabaseController>.Instance,
            new PacketSessionCache())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    private static ManabaseViewModel ModelOf(IActionResult result)
    {
        ViewResult view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<ManabaseViewModel>(view.Model);
    }

    private static ManabaseReport BuildReport(ManabaseMode mode) => new()
    {
        ActualLands = 36,
        TargetLands = 37.0,
        ColorFindings = [],
        Mode = mode,
        Castability =
        [
            new CardCastability { Name = "Counterspell", ManaValue = 2, OnCurveTurn = 2, CastPercent = 62, LimitingFactor = "color:U" },
        ],
        Summary = "ok",
    };

    private static (List<DeckEntry> Entries, List<ScryfallCard> Cards) Fixture()
    {
        List<DeckEntry> entries =
        [
            Entry("Kinnan, Bonder Prodigy", 1, "commander"),
            Land("Forest", 18),
            Land("Island", 18),
            Entry("Arcane Signet", 1, "mainboard"),
            Entry("Cultivate", 1, "mainboard"),
        ];
        for (int i = 0; i < 20; i++)
        {
            entries.Add(Entry($"Filler Spell {i}", 1, "mainboard"));
        }

        List<ScryfallCard> cards =
        [
            BasicLand("Forest", "G"),
            BasicLand("Island", "U"),
            Spell("Kinnan, Bonder Prodigy", "{G}{U}", 2, "Legendary Creature — Human Druid"),
            Spell("Arcane Signet", "{2}", 2, "Artifact"),
            Spell("Cultivate", "{2}{G}", 3, "Sorcery"),
        ];
        for (int i = 0; i < 20; i++)
        {
            cards.Add(Spell($"Filler Spell {i}", "{2}", 3, "Sorcery"));
        }

        return (entries, cards);
    }

    private static (List<DeckEntry> Entries, List<ScryfallCard> Cards) FixtureWithoutCommander()
    {
        List<DeckEntry> entries =
        [
            Land("Forest", 18),
            Land("Island", 18),
            Entry("Arcane Signet", 2, "mainboard"),
            Entry("Cultivate", 1, "mainboard"),
        ];
        for (int i = 0; i < 20; i++)
        {
            entries.Add(Entry($"Filler Spell {i}", 1, "mainboard"));
        }

        List<ScryfallCard> cards =
        [
            BasicLand("Forest", "G"),
            BasicLand("Island", "U"),
            Spell("Arcane Signet", "{2}", 2, "Artifact"),
            Spell("Cultivate", "{2}{G}", 3, "Sorcery"),
        ];
        for (int i = 0; i < 20; i++)
        {
            cards.Add(Spell($"Filler Spell {i}", "{2}", 3, "Sorcery"));
        }

        return (entries, cards);
    }

    private static DeckEntry Entry(string name, int qty, string board) => new()
    {
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        Quantity = qty,
        Board = board,
    };

    private static DeckEntry Land(string name, int qty) => Entry(name, qty, "mainboard");

    private static ScryfallCard BasicLand(string name, string color) => new(
        Name: name, ManaCost: null, TypeLine: $"Basic Land — {name}", OracleText: null,
        Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
        SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
        Layout: "normal", Cmc: 0, ProducedMana: new[] { color }, Rarity: "common");

    private static ScryfallCard Spell(string name, string manaCost, double cmc, string typeLine) => new(
        Name: name, ManaCost: manaCost, TypeLine: typeLine, OracleText: "...",
        Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
        SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
        Layout: "normal", Cmc: cmc, ProducedMana: null, Rarity: "rare");

    private sealed class FakeLoader : IDeckEntryLoader
    {
        private readonly List<DeckEntry> _entries;

        public FakeLoader(List<DeckEntry> entries)
        {
            _entries = entries;
        }

        public Task<DeckSourceLoadResult> LoadFromSourceAsync(
            string deckSource,
            UnrecognizedPasteBehavior unrecognizedBehavior = UnrecognizedPasteBehavior.ThrowNotRecognized,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DeckSourceLoadResult(_entries, null, null));

        public Task<List<DeckEntry>> LoadAsync(DeckLoadRequest request, CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();

        public void ValidateCommanderDeckSize(string systemName, IReadOnlyList<DeckEntry> entries, int requiredDeckSize = 100)
        {
        }
    }

    private sealed class FakeResolver : IScryfallCardResolver
    {
        private readonly List<ScryfallCard> _cards;

        public FakeResolver(List<ScryfallCard> cards)
        {
            _cards = cards;
        }

        public Task<RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCollectionResponse(_cards, null),
            });

        public Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult(_cards.FirstOrDefault(card => string.Equals(card.Name, cardName, System.StringComparison.OrdinalIgnoreCase)));

        public Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult(_cards.FirstOrDefault(card => string.Equals(card.Name, cardName, System.StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class StubCardSearchService : ICardSearchService
    {
        public Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> SearchCommandersAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeManabaseBaselineProvider : IManabaseBaselineProvider
    {
        private readonly IReadOnlyDictionary<int, ManabaseBracketBaseline> _rows;
        private readonly IReadOnlyDictionary<string, ManabaseCommanderBaseline> _commanderRows;

        public FakeManabaseBaselineProvider(
            IReadOnlyDictionary<int, ManabaseBracketBaseline> rows,
            IReadOnlyDictionary<string, ManabaseCommanderBaseline>? commanderRows = null)
        {
            _rows = rows;
            _commanderRows = commanderRows is null
                ? new Dictionary<string, ManabaseCommanderBaseline>(System.StringComparer.Ordinal)
                : commanderRows.Values.ToDictionary(
                    row => ManabaseCommanderKey.Create(row.Name, row.PartnerName),
                    row => row,
                    System.StringComparer.Ordinal);
        }

        public void EnsureLoaded()
        {
        }

        public ManabaseBracketBaseline? TryGetBracketBaseline(int bracket)
            => _rows.TryGetValue(bracket, out ManabaseBracketBaseline? row) ? row : null;

        public ManabaseCommanderBaseline? TryGetCommanderBaseline(IReadOnlyList<string> commanderNames)
            => commanderNames.Count is 1 or 2
                && _commanderRows.TryGetValue(
                    commanderNames.Count == 2
                        ? ManabaseCommanderKey.Create(commanderNames[0], commanderNames[1])
                        : ManabaseCommanderKey.Create(commanderNames[0]),
                    out ManabaseCommanderBaseline? row)
                    ? row
                    : null;
    }

    private sealed class FakeCedhLandBaselineProvider : ICedhLandBaselineProvider
    {
        private readonly bool _success;
        private readonly double _mean;
        private readonly int _deckCount;
        private readonly double _sd;
        private readonly string? _generated;

        public FakeCedhLandBaselineProvider(bool success, double mean, int deckCount, double sd, string? generated)
        {
            _success = success;
            _mean = mean;
            _deckCount = deckCount;
            _sd = sd;
            _generated = generated;
        }

        public void EnsureLoaded()
        {
        }

        public bool TryGetBaseline(IReadOnlyList<string> commanderNames, out double mean, out int n, out double sd, out string? generated)
        {
            mean = _mean;
            n = _deckCount;
            sd = _sd;
            generated = _generated;
            return _success;
        }
    }

    private sealed class CapturingAnalysisService : IManabaseAnalysisService
    {
        private readonly ManabaseReport _report;

        public CapturingAnalysisService(ManabaseReport report)
        {
            _report = report;
        }

        public ManabaseAnalysisOptions? LastOptions { get; private set; }

        public Task<ManabaseAnalysisResult> AnalyzeAsync(
            string deckSource,
            string? deckName,
            ManabaseAnalysisOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options ?? new ManabaseAnalysisOptions();
            return Task.FromResult(CreateResult(
                _report,
                "1 cards · 36 lands",
                "prompt",
                [],
                null,
                null,
                false));
        }

        public Task<ManabaseLoadResult> LoadAsync(
            string deckSource,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ManabaseLoadResult(
                "1 cards · 36 lands", [], null, []));
    }

    private static ManabaseAnalysisResult CreateResult(
        ManabaseReport report,
        string inputSummary,
        string chatGptSwapPrompt,
        IReadOnlyList<CostSuggestion> suggestions,
        ManabaseVerdict? verdict,
        ManabaseRampDrawBudget? budget,
        bool showPlainLanguage)
    {
        ConstructorInfo constructor = typeof(ManabaseAnalysisResult).GetConstructors().Single();
        object?[] args = constructor.GetParameters().Length == 9
            ? new object?[] { report, inputSummary, Array.Empty<string>(), null, chatGptSwapPrompt, suggestions, verdict, budget, showPlainLanguage }
            : new object?[] { report, inputSummary, Array.Empty<string>(), null, chatGptSwapPrompt, suggestions };
        return (ManabaseAnalysisResult)constructor.Invoke(args);
    }
}

internal sealed class FakeBracketClassificationService : IBracketClassificationService
{
    public BracketClassificationResult Result { get; set; } = CreateResult(3);

    public Exception? Exception { get; set; }

    public int CallCount { get; private set; }

    public string? LastDeckSource { get; private set; }

    public string? LastDeckName { get; private set; }

    public Task<BracketClassificationResult> ClassifyAsync(
        string deckSource,
        int? targetBracketNumber,
        string platform,
        string? deckName,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastDeckSource = deckSource;
        LastDeckName = deckName;

        if (Exception is not null)
        {
            throw Exception;
        }

        return Task.FromResult(Result);
    }

    public static BracketClassificationResult CreateResult(int bracketNumber)
        => new(
            new BracketClassification(
                bracketNumber,
                [],
                [],
                [],
                [],
                true,
                "2026-07-17"),
            [],
            string.Empty,
            null,
            null);
}
