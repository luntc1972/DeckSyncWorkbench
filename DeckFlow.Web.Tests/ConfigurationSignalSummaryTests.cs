using DeckFlow.Core.Bracket;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Core.Modular;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Services.Bracket;
using DeckFlow.Web.Services.Manabase;
using DeckFlow.Web.Services.Modular;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class ConfigurationSignalSummaryTests
{
    [Theory]
    [InlineData("Commander Changer", true)]
    [InlineData("Mainboard Changer", false)]
    public async Task AnalyzeAsync_CataloguedGameChangerOnActiveBoard_ProjectsSignals(string cardName, bool commandZone)
    {
        var manabase = new FakeManabaseAnalysisService();
        var service = CreateService(manabase, commandZone ? [Entry(cardName)] : [], commandZone ? [] : [Entry(cardName)]);

        var result = await service.AnalyzeAsync(Request());

        var analysis = Assert.IsType<ConfigurationAnalysisResult>(result.Value);
        var signals = Assert.IsType<ConfigurationSignalSummary>(analysis.Signals);
        Assert.Equal(3, signals.BracketNumber);
        Assert.Contains(cardName, signals.GameChangers);
        Assert.Empty(signals.MassLandDenialCards);
        Assert.Empty(signals.ExtraTurnCards);
        Assert.False(signals.ComboDetectionAvailable);
        Assert.Equal("2026-09-01", signals.CatalogEffectiveDate);
        Assert.Equal(1, manabase.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_NoCataloguedSignals_ClassifiesAtZeroSignalBracket_AndCallsManabaseOncePerInvocation()
    {
        var manabase = new FakeManabaseAnalysisService();
        var service = CreateService(manabase, [Entry("Commander")], [Entry("Ordinary Card")]);
        var request = Request();

        var first = await service.AnalyzeAsync(request);
        var second = await service.AnalyzeAsync(request);

        Assert.Equal(2, Assert.IsType<ConfigurationSignalSummary>(first.Value!.Signals).BracketNumber);
        Assert.False(Assert.IsType<ConfigurationSignalSummary>(second.Value!.Signals).ComboDetectionAvailable);
        Assert.Equal(2, manabase.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_CataloguedMassLandDenialAndExtraTurn_ProjectsBothSignals()
    {
        var service = CreateService(new FakeManabaseAnalysisService(), [Entry("Commander")], [Entry("Land Denial"), Entry("Extra Turn")]);

        var result = await service.AnalyzeAsync(Request());

        var signals = Assert.IsType<ConfigurationSignalSummary>(result.Value!.Signals);
        Assert.Equal(4, signals.BracketNumber);
        Assert.Contains("Land Denial", signals.MassLandDenialCards);
        Assert.Contains("Extra Turn", signals.ExtraTurnCards);
    }

    [Theory]
    [InlineData(DeckModulesProfile.Casual, "Plan", 4, true)]
    [InlineData(DeckModulesProfile.Casual, "Plan", 5, true)]
    [InlineData(DeckModulesProfile.Bracket4HighPower, "Plan", 4, false)]
    [InlineData(DeckModulesProfile.Cedh, "Plan", 5, false)]
    [InlineData(DeckModulesProfile.Cedh, "Plan", 2, true)]
    public async Task AnalyzeAsync_DeclaredProfile_ReportsOnlyBracketDisagreements(DeckModulesProfile profile, string playPlan, int expectedBracket, bool expectsNote)
    {
        var mainboard = expectedBracket switch
        {
            4 => new[] { Entry("Land Denial"), Entry("Extra Turn") },
            5 => [Entry("Mainboard Changer"), Entry("Changer 1"), Entry("Changer 2"), Entry("Changer 3"), Entry("Changer 4"), Entry("Changer 5"), Entry("Changer 6"), Entry("Changer 7"), Entry("Changer 8"), Entry("Changer 9")],
            _ => new[] { Entry("Ordinary Card") },
        };
        var service = CreateService(new FakeManabaseAnalysisService(), [Entry("Commander")], mainboard);

        var result = await service.AnalyzeAsync(Request(profile, playPlan));

        var declared = Assert.IsType<ConfigurationSignalSummary>(result.Value!.Signals).Declared;
        Assert.Equal(expectedBracket, Assert.IsType<ConfigurationSignalSummary>(result.Value!.Signals).BracketNumber);
        Assert.Equal(expectsNote, declared!.ProfileDisagreementNote is not null);
        if (expectsNote)
        {
            Assert.Contains(profile == DeckModulesProfile.Bracket4HighPower ? "Bracket 4 High Power" : profile == DeckModulesProfile.Cedh ? "cEDH" : "Casual", declared.ProfileDisagreementNote);
            Assert.Contains(expectedBracket.ToString(), declared.ProfileDisagreementNote);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_ProfileWithoutADeclaredRange_SkipsDisclosureInsteadOfThrowing()
    {
        // WR-05: DeclaredProfileRanges[...] was an unguarded indexer over a hand-maintained
        // dictionary, safe only because DeckModulesPageService.ValidateAlternative runs
        // Enum.IsDefined first -- an implicit, undocumented coupling this test bypasses via the
        // stub page service, the same way a newly added enum member with no matching range entry
        // would in production.
        var service = CreateService(new FakeManabaseAnalysisService(), [Entry("Commander")], [Entry("Ordinary Card")]);

        var result = await service.AnalyzeAsync(Request((DeckModulesProfile)99, "Plan"));

        Assert.True(result.Succeeded);
        Assert.Null(Assert.IsType<ConfigurationSignalSummary>(result.Value!.Signals).Declared);
    }

    [Fact]
    public void DeclaredProfileRanges_CoversEveryDeckModulesProfileMember()
    {
        // Guards WR-05's root cause directly: every DeckModulesProfile enum member must have a
        // matching entry in the hand-maintained DeclaredProfileRanges dictionary, or a future
        // profile addition compiles clean and 500s at runtime.
        var field = typeof(ConfigurationAnalysisService).GetField("DeclaredProfileRanges", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var ranges = Assert.IsAssignableFrom<System.Collections.IDictionary>(field!.GetValue(null));

        var coveredProfiles = ranges.Keys.Cast<DeckModulesProfile>().ToHashSet();

        Assert.Equal(Enum.GetValues<DeckModulesProfile>().ToHashSet(), coveredProfiles);
    }

    [Fact]
    public async Task AnalyzeAsync_DeclaredPlayPlan_PreservesExactPlayerText()
    {
        const string playPlan = "  A grindy — \"value\" plan  ";
        var service = CreateService(new FakeManabaseAnalysisService(), [Entry("Commander")], [Entry("Ordinary Card")]);

        var result = await service.AnalyzeAsync(Request(DeckModulesProfile.Casual, playPlan));

        Assert.Equal(playPlan, Assert.IsType<ConfigurationSignalSummary>(result.Value!.Signals).Declared!.PlayPlan);
    }

    [Fact]
    public async Task AnalyzeAsync_InteractionSpellInStrategy_ProjectsFixedModuleRows()
    {
        var manabase = new FakeManabaseAnalysisService([Interaction("Strategy Card", PlanRole.Interaction)]);
        var service = CreateService(manabase, [Entry("Commander")], [Entry("Strategy Card")]);

        var result = await service.AnalyzeAsync(Request());

        var signals = Assert.IsType<ConfigurationSignalSummary>(result.Value!.Signals);
        Assert.True(signals.InteractionAttributionAvailable);
        Assert.True(manabase.LastOptions!.ClassifyPlanRoles);
        Assert.Equal(
            [ConfigurationModuleKind.CommandZone, ConfigurationModuleKind.Core, ConfigurationModuleKind.Strategy, ConfigurationModuleKind.ManaSupport, ConfigurationModuleKind.Multiple],
            signals.InteractionsByModule.Select(row => row.ModuleKind));
        Assert.Equal(1, Assert.Single(signals.InteractionsByModule, row => row.ModuleKind == ConfigurationModuleKind.Strategy).InteractionCount);
    }

    [Fact]
    public async Task AnalyzeAsync_InteractionSpellInCore_CountsCore()
    {
        var service = CreateService(new FakeManabaseAnalysisService([Interaction("Core Card", PlanRole.Interaction)]), [Entry("Commander")], [Entry("Core Card")]);

        var result = await service.AnalyzeAsync(RequestWithCoreOnly("Core Card"));

        Assert.Equal(1, Assert.Single(Assert.IsType<ConfigurationSignalSummary>(result.Value!.Signals).InteractionsByModule, row => row.ModuleKind == ConfigurationModuleKind.Core).InteractionCount);
    }

    [Fact]
    public async Task AnalyzeAsync_OneShotInteractionWithoutPlanRole_CountsInteraction()
    {
        var service = CreateService(new FakeManabaseAnalysisService([Interaction("Strategy Card", PlanRole.None)]), [Entry("Commander")], [Entry("Strategy Card")]);

        var result = await service.AnalyzeAsync(Request());

        Assert.Equal(1, Assert.Single(Assert.IsType<ConfigurationSignalSummary>(result.Value!.Signals).InteractionsByModule, row => row.ModuleKind == ConfigurationModuleKind.Strategy).InteractionCount);
    }

    [Fact]
    public async Task AnalyzeAsync_InteractionSpellInMultipleModule_CountsOnlyMultiple()
    {
        var service = CreateService(new FakeManabaseAnalysisService([Interaction("Shared Card", PlanRole.Interaction)]), [Entry("Commander")], [Entry("Shared Card")]);

        var result = await service.AnalyzeAsync(RequestWithCore("Shared Card"));

        var rows = Assert.IsType<ConfigurationSignalSummary>(result.Value!.Signals).InteractionsByModule;
        Assert.Equal(1, Assert.Single(rows, row => row.ModuleKind == ConfigurationModuleKind.Multiple).InteractionCount);
        Assert.Equal(0, Assert.Single(rows, row => row.ModuleKind == ConfigurationModuleKind.Core).InteractionCount);
    }

    [Fact]
    public async Task AnalyzeAsync_NoAnalyzedSpells_MarksInteractionAttributionUnavailable()
    {
        var service = CreateService(new FakeManabaseAnalysisService(), [Entry("Commander")], [Entry("Strategy Card")]);

        var result = await service.AnalyzeAsync(Request());

        var signals = Assert.IsType<ConfigurationSignalSummary>(result.Value!.Signals);
        Assert.False(signals.InteractionAttributionAvailable);
        Assert.Empty(signals.InteractionsByModule);
    }

    [Fact]
    public async Task AnalyzeAsync_StructurallyInvalidCompilation_StillAnalysesButDisclosesTheCaveat()
    {
        // WR-06: Export refuses a structurally invalid compilation, but Analyze previously
        // produced a confident Health/TargetLandCount/bracket with no caveat at all -- the only
        // notice was the core-only one, which does not fire for Overlap/UnknownStrategy/etc.
        // Analyze must stay advisory-only (D-22: no IsValid/IsStructurallyValid verdict on this
        // record), so it still returns the numbers with the same caveat Export enforces.
        var manabase = new FakeManabaseAnalysisService();
        var service = new ConfigurationAnalysisService(
            new StubDeckModulesPageService(Compilation([Entry("Commander")], [Entry("Ordinary Card")], isStructurallyValid: false)),
            manabase,
            NullLogger<ConfigurationAnalysisService>.Instance,
            new StubGameChangerCatalogService());

        var result = await service.AnalyzeAsync(Request());

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value!.AnalysisNotice);
        Assert.Contains("not a legality verdict", result.Value.AnalysisNotice);
        Assert.False(result.Value.IsCoreOnly);
    }

    [Fact]
    public async Task AnalyzeAsync_NullReportWithCommanderSelectionRequired_ReportsCommanderMessage()
    {
        var manabase = new FakeManabaseAnalysisService(nullReport: true, commanderSelectionRequired: true, commanderChoices: ["Alpha", "Beta"]);
        var service = CreateService(manabase, [Entry("Commander")], [Entry("Ordinary Card")]);

        var result = await service.AnalyzeAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Contains("Commander selection is required", result.ErrorMessage);
        Assert.Contains("2 eligible commanders found", result.ErrorMessage);
    }

    [Fact]
    public async Task AnalyzeAsync_NullReportForNonCommanderReason_DoesNotReportCommanderMessage()
    {
        // WR-07: a null Report was previously always reported as a commander-selection gap,
        // producing nonsense like "(0 eligible commanders found)" when the report was null for an
        // unrelated reason. ManabaseController already distinguishes CommanderSelectionRequired
        // from a bare null Report; this service must too.
        var manabase = new FakeManabaseAnalysisService(nullReport: true, commanderSelectionRequired: false);
        var service = CreateService(manabase, [Entry("Commander")], [Entry("Ordinary Card")]);

        var result = await service.AnalyzeAsync(Request());

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("Commander selection is required", result.ErrorMessage);
        Assert.DoesNotContain("eligible commanders found", result.ErrorMessage);
    }

    private static ConfigurationAnalysisService CreateService(FakeManabaseAnalysisService manabase, IReadOnlyList<DeckEntry> commandZone, IReadOnlyList<DeckEntry> mainboard)
        => new(new StubDeckModulesPageService(Compilation(commandZone, mainboard)), manabase, NullLogger<ConfigurationAnalysisService>.Instance, new StubGameChangerCatalogService());

    private static DeckModulesCompilationViewModel Compilation(IReadOnlyList<DeckEntry> commandZone, IReadOnlyList<DeckEntry> mainboard, bool isStructurallyValid = true) => new()
    {
        IsStructurallyValid = isStructurallyValid,
        IsVerifiedLegal = true,
        Diagnostics = [],
        SelectedStrategyId = "strategy",
        SelectedStrategyName = "Strategy",
        SelectedManaSupportModuleName = "",
        CommandZoneEntries = commandZone,
        MainboardEntries = mainboard,
        Entries = commandZone.Concat(mainboard).ToList(),
        TotalCardCount = commandZone.Sum(entry => entry.Quantity) + mainboard.Sum(entry => entry.Quantity),
        SwapPlan = new ModularDeckSwapPlan { ToAdd = [], ToRemove = [], ToReset = [] },
    };

    private static DeckEntry Entry(string name) => new() { Name = name, NormalizedName = name.ToLowerInvariant(), Quantity = 1, Board = "ignored" };

    private static SpellRequirement Interaction(string name, PlanRole planRoles) => new()
    {
        Name = name,
        ManaValue = 2,
        Pips = new Dictionary<ManaColor, int>(),
        PlanRoles = planRoles,
        IsInteractionSpell = true,
    };

    private static ConfigurationAnalysisRequest Request(DeckModulesProfile profile = DeckModulesProfile.Casual, string playPlan = "Plan") => new()
    {
        Configuration = new DeckModulesCompilationRequest
        {
            BaselineToken = "token",
            CommandZone = [],
            BaselineMainboardEntries = [],
            CoreEntries = [],
            SelectedAlternativeId = "strategy",
            Alternatives = [new DeckModulesAlternativeInput { Id = "strategy", Name = "Strategy", Profile = profile, PlayPlan = playPlan, MainboardEntries = [Entry("Strategy Card")] }],
        },
    };

    private static ConfigurationAnalysisRequest RequestWithCore(string coreCard) => new()
    {
        Configuration = new DeckModulesCompilationRequest
        {
            BaselineToken = "token",
            CommandZone = [],
            BaselineMainboardEntries = [],
            CoreEntries = [Entry(coreCard)],
            SelectedAlternativeId = "strategy",
            Alternatives = [new DeckModulesAlternativeInput { Id = "strategy", Name = "Strategy", Profile = DeckModulesProfile.Casual, PlayPlan = "Plan", MainboardEntries = [Entry(coreCard)] }],
        },
    };

    private static ConfigurationAnalysisRequest RequestWithCoreOnly(string coreCard) => new()
    {
        Configuration = new DeckModulesCompilationRequest
        {
            BaselineToken = "token",
            CommandZone = [],
            BaselineMainboardEntries = [],
            CoreEntries = [Entry(coreCard)],
            SelectedAlternativeId = "strategy",
            Alternatives = [new DeckModulesAlternativeInput { Id = "strategy", Name = "Strategy", Profile = DeckModulesProfile.Casual, PlayPlan = "Plan", MainboardEntries = [Entry("Strategy Card")] }],
        },
    };

    private sealed class StubDeckModulesPageService(DeckModulesCompilationViewModel compilation) : IDeckModulesPageService
    {
        public DeckModulesServiceResult<DeckModulesCompilationViewModel> Compile(DeckModulesCompilationRequest request) => DeckModulesServiceResult<DeckModulesCompilationViewModel>.Success(compilation);
        public Task<DeckModulesServiceResult<DeckModulesViewModel>> ImportAsync(DeckModulesImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubGameChangerCatalogService : IGameChangerCatalogService
    {
        public GameChangerCatalog GetCatalog() => new(new DateOnly(2026, 9, 1), ["Commander Changer", "Mainboard Changer", "Changer 1", "Changer 2", "Changer 3", "Changer 4", "Changer 5", "Changer 6", "Changer 7", "Changer 8", "Changer 9"], ["Land Denial"], ["Extra Turn"], []);
    }

    private sealed class FakeManabaseAnalysisService(IReadOnlyList<SpellRequirement>? analyzedSpells = null, bool nullReport = false, bool commanderSelectionRequired = false, IReadOnlyList<string>? commanderChoices = null) : IManabaseAnalysisService
    {
        public int CallCount { get; private set; }
        public ManabaseAnalysisOptions? LastOptions { get; private set; }
        public Task<ManabaseAnalysisResult> AnalyzeAsync(string deckSource, string? deckName, ManabaseAnalysisOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastOptions = options;
            var report = nullReport ? null : new ManabaseReport { ActualLands = 36, TargetLands = 36, ColorFindings = [], Summary = "" };
            return Task.FromResult(new ManabaseAnalysisResult(report, "", [], null, "", [], null, null, false)
            {
                AnalyzedSpells = analyzedSpells ?? [],
                CommanderSelectionRequired = commanderSelectionRequired,
                CommanderChoices = commanderChoices ?? [],
            });
        }
        public Task<ManabaseLoadResult> LoadAsync(string deckSource, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
