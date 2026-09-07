using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Models;
using DeckFlow.Core.Modular;
using DeckFlow.Core.Normalization;
using DeckFlow.Core.Parsing;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Services.Modular;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Covers baseline import, immutable command-zone preservation, manual alternative metadata
/// validation, and stateless no-persistence compilation in <see cref="DeckModulesPageService"/>.
/// </summary>
public sealed class DeckModulesPageServiceTests
{
    private static readonly IDataProtectionProvider DataProtectionProvider = Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("DeckModulesPageServiceTests");
    private const string CommanderModxfieldText =
        "Commander\n1 Command Card\n\nDeck\n1 Sol Ring\n2 Forest\n\nMaybeboard\n1 Extra Card\n";

    [Fact]
    public async Task ImportAsync_RejectsEmptyPublicUrl()
    {
        var service = CreateService(CreateLoader());

        var result = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PublicUrl,
            Url = null,
        });

        Assert.False(result.Succeeded);
        Assert.Equal("A public deck URL is required.", result.ErrorMessage);
    }

    [Fact]
    public async Task ImportAsync_RejectsEmptyPasteText()
    {
        var service = CreateService(CreateLoader());

        var result = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PasteText,
            PasteText = "   ",
        });

        Assert.False(result.Succeeded);
        Assert.Equal("Pasted decklist text is required.", result.ErrorMessage);
    }

    [Fact]
    public async Task ImportAsync_RejectsOversizedPasteText()
    {
        var service = CreateService(CreateLoader());
        var oversizedText = new string('a', DeckModulesImportRequest.MaxPasteTextLength + 1);

        var result = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PasteText,
            PasteText = oversizedText,
        });

        Assert.False(result.Succeeded);
        Assert.Contains("maximum accepted length", result.ErrorMessage);
    }

    [Fact]
    public async Task ImportAsync_RejectsOversizedUrl()
    {
        var service = CreateService(CreateLoader());
        var oversizedUrl = "https://moxfield.com/decks/" + new string('a', DeckModulesImportRequest.MaxUrlLength);

        var result = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PublicUrl,
            Url = oversizedUrl,
        });

        Assert.False(result.Succeeded);
        Assert.Contains("maximum accepted length", result.ErrorMessage);
    }

    [Fact]
    public async Task ImportAsync_PreservesCommandZoneVerbatimAndExcludesMaybeboardFromBaseline()
    {
        var service = CreateService(CreateLoader());

        var result = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PasteText,
            PasteText = CommanderModxfieldText,
        });

        Assert.True(result.Succeeded);
        var viewModel = result.Value!;
        var commandZoneEntry = Assert.Single(viewModel.CommandZone);
        Assert.Equal("Command Card", commandZoneEntry.Name);
        Assert.Equal(1, commandZoneEntry.Quantity);
        Assert.Equal("commander", commandZoneEntry.Board, ignoreCase: true);

        Assert.Equal(2, viewModel.BaselineMainboardEntries.Count);
        Assert.Contains(viewModel.BaselineMainboardEntries, entry => entry.Name == "Sol Ring" && entry.Quantity == 1);
        Assert.Contains(viewModel.BaselineMainboardEntries, entry => entry.Name == "Forest" && entry.Quantity == 2);
        Assert.DoesNotContain(viewModel.BaselineMainboardEntries, entry => entry.Name == "Extra Card");
        Assert.False(string.IsNullOrWhiteSpace(viewModel.BaselineToken));
        Assert.Null(viewModel.ImportNotice);
    }

    [Fact]
    public async Task ImportAsync_BaselineTokenRoundTripsThroughCompile()
    {
        var service = CreateService(CreateLoader());
        var import = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PasteText,
            PasteText = CommanderModxfieldText,
        });
        var request = BuildMinimalTwoAlternativeRequest() with
        {
            BaselineToken = import.Value!.BaselineToken,
            CommandZone = import.Value.CommandZone,
            BaselineMainboardEntries = import.Value.BaselineMainboardEntries,
        };

        var result = service.Compile(request);

        Assert.True(import.Succeeded);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task ImportAsync_BaselineTokenRoundTripPreservesDisplayNames()
    {
        var service = CreateService(CreateLoader());
        var import = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PasteText,
            PasteText = "Commander\n1 Krenko's Command\n\nDeck\n1 Mystic Remora\n",
        });
        var request = BuildMinimalTwoAlternativeRequest() with
        {
            BaselineToken = import.Value!.BaselineToken,
            CommandZone = import.Value.CommandZone,
            BaselineMainboardEntries = import.Value.BaselineMainboardEntries,
            Alternatives = BuildMinimalTwoAlternativeRequest().Alternatives
                .Select(alternative => alternative with
                {
                    MainboardEntries = new[] { Entry("Mystic Remora", 1) },
                }).ToArray(),
        };

        var result = service.Compile(request);

        Assert.True(import.Succeeded);
        Assert.True(result.Succeeded);
        Assert.Equal("Krenko's Command", Assert.Single(result.Value!.CommandZoneEntries).Name);
        Assert.Contains(result.Value.MainboardEntries, entry => entry.Name == "Mystic Remora");
        Assert.Contains(result.Value.Entries, entry => entry.Name == "Mystic Remora");
    }

    [Fact]
    public async Task ImportAsync_LoadsBaselineFromPublicUrlThroughEstablishedLoader()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Command Card", 1, "commander"),
            Entry("Sol Ring", 1),
        };
        var loader = new DeckEntryLoader(
            new FakeMoxfieldDeckImporter(entries),
            new FakeArchidektDeckImporter(new List<DeckEntry>()),
            new MoxfieldParser(),
            new ArchidektParser());
        var service = CreateService(loader);

        var result = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PublicUrl,
            Url = "https://moxfield.com/decks/test",
        });

        Assert.True(result.Succeeded);
        Assert.Single(result.Value!.CommandZone);
        Assert.Single(result.Value.BaselineMainboardEntries);
    }

    [Fact]
    public async Task ImportAsync_DoesNotRetainStateBetweenCalls()
    {
        var service = CreateService(CreateLoader());

        var first = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PasteText,
            PasteText = "Commander\n1 First Commander\n\nDeck\n1 Sol Ring\n",
        });
        var second = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PasteText,
            PasteText = "Commander\n1 Second Commander\n\nDeck\n1 Lotus Petal\n",
        });

        Assert.Equal("First Commander", first.Value!.CommandZone.Single().Name);
        Assert.Equal("Second Commander", second.Value!.CommandZone.Single().Name);
    }

    [Fact]
    public async Task ImportAsync_MapsUpstreamHttpFailureToFriendlyMessage()
    {
        var loader = new FakeDeckEntryLoader(_ => throw new HttpRequestException(
            "Moxfield API deck test returned 500 Internal Server Error: boom",
            null,
            HttpStatusCode.InternalServerError));
        var service = CreateService(loader);

        var result = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PublicUrl,
            Url = "https://moxfield.com/decks/test",
        });

        Assert.False(result.Succeeded);
        Assert.Equal("Moxfield returned HTTP 500. Try again shortly.", result.ErrorMessage);
    }

    [Fact]
    public async Task ImportAsync_MapsUnrecognizedInputToActionableValidationError()
    {
        var loader = new FakeDeckEntryLoader(_ => throw new InvalidOperationException(
            "The submitted deck was not recognized as a Moxfield URL, Archidekt URL, or a Moxfield, Archidekt, or MTG Arena deck export."));
        var service = CreateService(loader);

        var result = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PasteText,
            PasteText = "not a real deck export",
        });

        Assert.False(result.Succeeded);
        Assert.Contains("not recognized", result.ErrorMessage);
    }

    [Fact]
    public void Compile_ReturnsVerifiedLegalConfigurationWhenAllFactsAreResolved()
    {
        var commandZone = new[] { Entry("Command Card", 1, "commander") };
        var coreEntries = new[] { Entry("Sol Ring", 1), Entry("Basic Land", 97) };
        var alternativeA = new DeckModulesAlternativeInput
        {
            Id = "alt-a",
            Name = "Alt A",
            Profile = DeckModulesProfile.Casual,
            PlayPlan = "Ramp into big threats and win with combat damage.",
            MainboardEntries = new[] { Entry("Alt A Card", 1) },
        };
        var alternativeB = new DeckModulesAlternativeInput
        {
            Id = "alt-b",
            Name = "Alt B",
            Profile = DeckModulesProfile.Cedh,
            PlayPlan = "Assemble the combo as early as turn three.",
            MainboardEntries = new[] { Entry("Alt B Card", 1) },
        };

        var catalog = new FakeLegalityCatalog(new Dictionary<string, ModularCardLegalityFacts>(StringComparer.OrdinalIgnoreCase)
        {
            [CardNormalizer.Normalize("Command Card")] = ColorlessFacts(),
            [CardNormalizer.Normalize("Sol Ring")] = ColorlessFacts(),
            [CardNormalizer.Normalize("Basic Land")] = ColorlessFacts(isSingletonExempt: true),
            [CardNormalizer.Normalize("Alt A Card")] = ColorlessFacts(),
        });
        var service = CreateService(CreateLoader(), catalog);

        var request = new DeckModulesCompilationRequest
        {
            BaselineToken = CreateBaselineToken(commandZone, coreEntries),
            CommandZone = commandZone,
            BaselineMainboardEntries = coreEntries,
            CoreEntries = coreEntries,
            Alternatives = new[] { alternativeA, alternativeB },
            SelectedAlternativeId = "alt-a",
        };

        var result = service.Compile(request);

        Assert.True(result.Succeeded);
        var compiled = result.Value!;
        Assert.True(compiled.IsStructurallyValid);
        Assert.True(compiled.IsVerifiedLegal);
        Assert.Empty(compiled.Diagnostics);
        Assert.Equal(100, compiled.TotalCardCount);
        Assert.Equal("Alt A", compiled.SelectedStrategyName);
        Assert.Contains(compiled.Entries, entry => entry.Name == "Command Card" && entry.Board.Equals("commander", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(compiled.Entries, entry => entry.Name == "Basic Land" && entry.Quantity == 97);
    }

    [Fact]
    public void Compile_ReportsUnverifiableFactsWhenNoLegalityCatalogIsInjected()
    {
        var request = BuildMinimalTwoAlternativeRequest();
        var service = CreateService(CreateLoader());

        var result = service.Compile(request);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.IsVerifiedLegal);
        Assert.Contains(result.Value.Diagnostics, diagnostic => diagnostic.Rule == ModularDeckDiagnosticRule.UnverifiableCardFacts);
    }

    [Fact]
    public void Compile_RejectsWhenCommandZoneDivergesFromProtectedBaseline()
    {
        var request = BuildMinimalTwoAlternativeRequest();
        var tamperedCommandZone = new[] { Entry("Command Card", 2, "commander") };
        var tamperedRequest = request with { CommandZone = tamperedCommandZone };
        var service = CreateService(CreateLoader());

        var result = service.Compile(tamperedRequest);

        Assert.False(result.Succeeded);
        Assert.Contains("baseline no longer matches", result.ErrorMessage);
    }

    [Fact]
    public void Compile_RejectsTamperedTokenSignature()
    {
        var request = BuildMinimalTwoAlternativeRequest();
        var token = request.BaselineToken;
        var tamperedToken = token[..^1] + (token[^1] == 'A' ? "B" : "A");
        var service = CreateService(CreateLoader());

        var result = service.Compile(request with { BaselineToken = tamperedToken });

        Assert.False(result.Succeeded);
        Assert.Equal("Your Deck Modules session has expired or is invalid. Re-import the deck to continue.", result.ErrorMessage);
    }

    [Fact]
    public void Compile_RejectsTamperedTokenPayload()
    {
        var request = BuildMinimalTwoAlternativeRequest();
        var token = DataProtectionProvider.CreateProtector("DeckFlow.DeckModules.Baseline.v1")
            .ToTimeLimitedDataProtector()
            .Protect("{}", TimeProvider.System.GetUtcNow().AddMinutes(30));
        var service = CreateService(CreateLoader());

        var result = service.Compile(request with { BaselineToken = token });

        Assert.False(result.Succeeded);
        Assert.Equal("Your Deck Modules session has expired or is invalid. Re-import the deck to continue.", result.ErrorMessage);
    }

    [Fact]
    public async Task Compile_RejectsExpiredToken()
    {
        var service = CreateService(
            CreateLoader(),
            timeProvider: new FixedTimeProvider(TimeProvider.System.GetUtcNow().AddHours(-1)));
        var import = await service.ImportAsync(new DeckModulesImportRequest
        {
            ActiveSource = DeckInputSource.PasteText,
            PasteText = CommanderModxfieldText,
        });
        var request = BuildMinimalTwoAlternativeRequest() with
        {
            BaselineToken = import.Value!.BaselineToken,
            CommandZone = import.Value.CommandZone,
            BaselineMainboardEntries = import.Value.BaselineMainboardEntries,
        };

        var result = service.Compile(request);

        Assert.False(result.Succeeded);
        Assert.Equal("Your Deck Modules session has expired or is invalid. Re-import the deck to continue.", result.ErrorMessage);
    }

    [Fact]
    public void Compile_RejectsMismatchedProtectedBaselineMainboard()
    {
        var request = BuildMinimalTwoAlternativeRequest() with
        {
            BaselineMainboardEntries = new[] { Entry("Sol Ring", 1) },
        };
        var service = CreateService(CreateLoader());

        var result = service.Compile(request);

        Assert.False(result.Succeeded);
        Assert.Contains("baseline no longer matches", result.ErrorMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Compile_RejectsAlternativeCountOutsideBounds(int alternativeCount)
    {
        var alternatives = Enumerable.Range(0, alternativeCount)
            .Select(index => MinimalAlternative($"alt-{index}", $"Alt {index}"))
            .ToArray();
        var request = BuildMinimalTwoAlternativeRequest() with { Alternatives = alternatives, SelectedAlternativeId = alternatives[0].Id };
        var service = CreateService(CreateLoader());

        var result = service.Compile(request);

        Assert.False(result.Succeeded);
        Assert.Contains("between 2 and 4 alternatives", result.ErrorMessage);
    }

    [Fact]
    public void Compile_RejectsBlankAlternativeName()
    {
        var request = BuildMinimalTwoAlternativeRequest();
        var blankNamedAlternatives = request.Alternatives
            .Select((alternative, index) => index == 0 ? alternative with { Name = "  " } : alternative)
            .ToArray();
        var tamperedRequest = request with { Alternatives = blankNamedAlternatives };
        var service = CreateService(CreateLoader());

        var result = service.Compile(tamperedRequest);

        Assert.False(result.Succeeded);
        Assert.Contains("user-entered name", result.ErrorMessage);
    }

    [Fact]
    public void Compile_RejectsBlankPlayPlan()
    {
        var request = BuildMinimalTwoAlternativeRequest();
        var blankPlayPlanAlternatives = request.Alternatives
            .Select((alternative, index) => index == 0 ? alternative with { PlayPlan = string.Empty } : alternative)
            .ToArray();
        var tamperedRequest = request with { Alternatives = blankPlayPlanAlternatives };
        var service = CreateService(CreateLoader());

        var result = service.Compile(tamperedRequest);

        Assert.False(result.Succeeded);
        Assert.Contains("play-plan disclosure", result.ErrorMessage);
    }

    [Fact]
    public void Compile_RejectsInvalidProfileEnumValue()
    {
        var request = BuildMinimalTwoAlternativeRequest();
        var invalidProfileAlternatives = request.Alternatives
            .Select((alternative, index) => index == 0 ? alternative with { Profile = (DeckModulesProfile)99 } : alternative)
            .ToArray();
        var tamperedRequest = request with { Alternatives = invalidProfileAlternatives };
        var service = CreateService(CreateLoader());

        var result = service.Compile(tamperedRequest);

        Assert.False(result.Succeeded);
        Assert.Contains("declared table profiles", result.ErrorMessage);
    }

    [Fact]
    public void Compile_RejectsDuplicateAlternativeIds()
    {
        var request = BuildMinimalTwoAlternativeRequest();
        var duplicateIdAlternatives = request.Alternatives
            .Select(alternative => alternative with { Id = "duplicate-id" })
            .ToArray();
        var tamperedRequest = request with { Alternatives = duplicateIdAlternatives };
        var service = CreateService(CreateLoader());

        var result = service.Compile(tamperedRequest);

        Assert.False(result.Succeeded);
        Assert.Contains("used more than once", result.ErrorMessage);
    }

    [Fact]
    public void Compile_RejectsOversizedMainboardEntryList()
    {
        var oversizedEntries = Enumerable.Range(0, DeckModulesAlternativeInput.MaxEntriesPerList + 1)
            .Select(index => Entry($"Card {index}", 1))
            .ToArray();
        var request = BuildMinimalTwoAlternativeRequest();
        var oversizedAlternatives = request.Alternatives
            .Select((alternative, index) => index == 0 ? alternative with { MainboardEntries = oversizedEntries } : alternative)
            .ToArray();
        var tamperedRequest = request with { Alternatives = oversizedAlternatives };
        var service = CreateService(CreateLoader());

        var result = service.Compile(tamperedRequest);

        Assert.False(result.Succeeded);
        Assert.Contains("maximum accepted count", result.ErrorMessage);
    }

    [Fact]
    public void Compile_LinksOptionalManaSupportWithoutSpuriousDiagnostics()
    {
        var commandZone = new[] { Entry("Command Card", 1, "commander") };
        var coreEntries = Array.Empty<DeckEntry>();
        var alternativeWithManaSupport = new DeckModulesAlternativeInput
        {
            Id = "alt-a",
            Name = "Alt A",
            Profile = DeckModulesProfile.Bracket4HighPower,
            PlayPlan = "Attack with evasive creatures.",
            MainboardEntries = new[] { Entry("Alt A Card", 1) },
            ManaSupportName = "Alt A Lands",
            ManaSupportEntries = new[] { Entry("Alt A Land", 1) },
        };
        var alternativeWithoutManaSupport = new DeckModulesAlternativeInput
        {
            Id = "alt-b",
            Name = "Alt B",
            Profile = DeckModulesProfile.Casual,
            PlayPlan = "Grind out value over a long game.",
            MainboardEntries = new[] { Entry("Alt B Card", 2) },
        };
        var request = new DeckModulesCompilationRequest
        {
            BaselineToken = CreateBaselineToken(commandZone, coreEntries),
            CommandZone = commandZone,
            BaselineMainboardEntries = coreEntries,
            CoreEntries = coreEntries,
            Alternatives = new[] { alternativeWithManaSupport, alternativeWithoutManaSupport },
            SelectedAlternativeId = "alt-a",
        };
        var service = CreateService(CreateLoader());

        var result = service.Compile(request);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Value!.Diagnostics, diagnostic => diagnostic.Rule == ModularDeckDiagnosticRule.MissingLinkedManaSupport);
        Assert.Equal("Alt A Lands", result.Value.SelectedManaSupportModuleName);
    }

    [Fact]
    public void Compile_PreservesEntryQuantitiesAndBoardsExactly()
    {
        var request = BuildMinimalTwoAlternativeRequest();
        var service = CreateService(CreateLoader());

        var result = service.Compile(request);

        Assert.True(result.Succeeded);
        var commandZoneEntry = Assert.Single(result.Value!.CommandZoneEntries);
        Assert.Equal(request.CommandZone[0].Name, commandZoneEntry.Name);
        Assert.Equal(request.CommandZone[0].Quantity, commandZoneEntry.Quantity);
        Assert.Equal(request.CommandZone[0].Board, commandZoneEntry.Board);
    }

    private static DeckModulesCompilationRequest BuildMinimalTwoAlternativeRequest()
    {
        var commandZone = new[] { Entry("Command Card", 1, "commander") };
        var coreEntries = Array.Empty<DeckEntry>();
        var alternativeA = MinimalAlternative("alt-a", "Alt A");
        var alternativeB = MinimalAlternative("alt-b", "Alt B");

        return new DeckModulesCompilationRequest
        {
            BaselineToken = CreateBaselineToken(commandZone, coreEntries),
            CommandZone = commandZone,
            BaselineMainboardEntries = coreEntries,
            CoreEntries = coreEntries,
            Alternatives = new[] { alternativeA, alternativeB },
            SelectedAlternativeId = "alt-a",
        };
    }

    private static DeckModulesAlternativeInput MinimalAlternative(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Profile = DeckModulesProfile.Casual,
        PlayPlan = "Play a straightforward midrange game plan.",
        MainboardEntries = new[] { Entry($"{name} Card", 1) },
    };

    private static DeckEntry Entry(string name, int quantity, string board = "mainboard") => new()
    {
        Name = name,
        NormalizedName = CardNormalizer.Normalize(name),
        Quantity = quantity,
        Board = board,
    };

    private static ModularCardLegalityFacts ColorlessFacts(bool isSingletonExempt = false) => new()
    {
        ColorIdentity = Array.Empty<string>(),
        IsBanned = false,
        IsSingletonExempt = isSingletonExempt,
    };

    private static IDeckEntryLoader CreateLoader() => new DeckEntryLoader(
        new FakeMoxfieldDeckImporter(new List<DeckEntry>()),
        new FakeArchidektDeckImporter(new List<DeckEntry>()),
        new MoxfieldParser(),
        new ArchidektParser());

    private static DeckModulesPageService CreateService(
        IDeckEntryLoader loader,
        IModularCardLegalityCatalog? catalog = null,
        TimeProvider? timeProvider = null)
        => new(loader, DataProtectionProvider, catalog, timeProvider);

    private static string CreateBaselineToken(
        IReadOnlyList<DeckEntry> commandZone,
        IReadOnlyList<DeckEntry> baselineMainboard,
        DateTimeOffset? expiresAt = null)
    {
        var payload = JsonSerializer.Serialize(new { CommandZone = commandZone, BaselineMainboardEntries = baselineMainboard });
        return DataProtectionProvider.CreateProtector("DeckFlow.DeckModules.Baseline.v1")
            .ToTimeLimitedDataProtector()
            .Protect(payload, expiresAt ?? TimeProvider.System.GetUtcNow().AddMinutes(30));
    }

    private sealed class FakeLegalityCatalog : IModularCardLegalityCatalog
    {
        private readonly Dictionary<string, ModularCardLegalityFacts> _facts;

        public FakeLegalityCatalog(Dictionary<string, ModularCardLegalityFacts> facts) => _facts = facts;

        public ModularCardLegalityFacts? GetFacts(string normalizedCardName)
            => _facts.TryGetValue(normalizedCardName, out var facts) ? facts : null;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeDeckEntryLoader : IDeckEntryLoader
    {
        private readonly Func<string, Task<DeckSourceLoadResult>> _loadFromSourceAsync;

        public FakeDeckEntryLoader(Func<string, Task<DeckSourceLoadResult>> loadFromSourceAsync) => _loadFromSourceAsync = loadFromSourceAsync;

        public Task<List<DeckEntry>> LoadAsync(DeckLoadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not exercised by DeckModulesPageService.");

        public Task<DeckSourceLoadResult> LoadFromSourceAsync(
            string deckSource,
            UnrecognizedPasteBehavior unrecognizedBehavior = UnrecognizedPasteBehavior.ThrowNotRecognized,
            CancellationToken cancellationToken = default)
            => _loadFromSourceAsync(deckSource);

        public void ValidateCommanderDeckSize(string systemName, IReadOnlyList<DeckEntry> entries, int requiredDeckSize = 100)
        {
        }
    }

    private sealed class FakeMoxfieldDeckImporter : IMoxfieldDeckImporter
    {
        private readonly Func<string, List<DeckEntry>> _entriesFactory;

        public FakeMoxfieldDeckImporter(List<DeckEntry> entries)
            : this(_ => entries)
        {
        }

        public FakeMoxfieldDeckImporter(Func<string, List<DeckEntry>> entriesFactory) => _entriesFactory = entriesFactory;

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(_entriesFactory(urlOrDeckId));
    }

    private sealed class FakeArchidektDeckImporter : IArchidektDeckImporter
    {
        private readonly Func<string, List<DeckEntry>> _entriesFactory;

        public FakeArchidektDeckImporter(List<DeckEntry> entries)
            : this(_ => entries)
        {
        }

        public FakeArchidektDeckImporter(Func<string, List<DeckEntry>> entriesFactory) => _entriesFactory = entriesFactory;

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(_entriesFactory(urlOrDeckId));
    }
}
