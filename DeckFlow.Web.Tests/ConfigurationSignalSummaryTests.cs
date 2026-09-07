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

    private static ConfigurationAnalysisService CreateService(FakeManabaseAnalysisService manabase, IReadOnlyList<DeckEntry> commandZone, IReadOnlyList<DeckEntry> mainboard)
        => new(new StubDeckModulesPageService(Compilation(commandZone, mainboard)), manabase, NullLogger<ConfigurationAnalysisService>.Instance, new StubGameChangerCatalogService());

    private static DeckModulesCompilationViewModel Compilation(IReadOnlyList<DeckEntry> commandZone, IReadOnlyList<DeckEntry> mainboard) => new()
    {
        IsStructurallyValid = true,
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

    private static ConfigurationAnalysisRequest Request() => new()
    {
        Configuration = new DeckModulesCompilationRequest
        {
            BaselineToken = "token",
            CommandZone = [],
            BaselineMainboardEntries = [],
            CoreEntries = [],
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
        public GameChangerCatalog GetCatalog() => new(new DateOnly(2026, 9, 1), ["Commander Changer", "Mainboard Changer"], ["Land Denial"], ["Extra Turn"], []);
    }

    private sealed class FakeManabaseAnalysisService : IManabaseAnalysisService
    {
        public int CallCount { get; private set; }
        public Task<ManabaseAnalysisResult> AnalyzeAsync(string deckSource, string? deckName, ManabaseAnalysisOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ManabaseAnalysisResult(new ManabaseReport { ActualLands = 36, TargetLands = 36, ColorFindings = [], Summary = "" }, "", [], null, "", [], null, null, false));
        }
        public Task<ManabaseLoadResult> LoadAsync(string deckSource, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
