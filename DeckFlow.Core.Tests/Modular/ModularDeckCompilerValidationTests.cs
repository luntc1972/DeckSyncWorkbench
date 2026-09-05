using DeckFlow.Core.Models;
using DeckFlow.Core.Modular;

namespace DeckFlow.Core.Tests.Modular;

public sealed class ModularDeckCompilerValidationTests
{
    [Fact]
    public void Compile_MissingSelection_ReturnsMissingSelectionDiagnostic()
    {
        var compilation = new ModularDeckCompiler().Compile(CreateValidProject(), null!);

        Assert.False(compilation.IsStructurallyValid);
        AssertDiagnostic(compilation, ModularDeckDiagnosticRule.MissingSelection, "selection");
    }

    [Fact]
    public void Compile_UnknownStrategyId_ReturnsUnknownStrategyDiagnostic()
    {
        var compilation = new ModularDeckCompiler().Compile(CreateValidProject(), Selection("unknown"));

        Assert.False(compilation.IsStructurallyValid);
        AssertDiagnostic(compilation, ModularDeckDiagnosticRule.UnknownStrategy, "unknown");
    }

    [Fact]
    public void Compile_AbsentLinkedManaSupport_ReturnsMissingLinkedManaSupportDiagnostic()
    {
        var project = CreateValidProject(strategyManaId: "missing-mana");

        var compilation = new ModularDeckCompiler().Compile(project, Selection("alpha"));

        AssertDiagnostic(compilation, ModularDeckDiagnosticRule.MissingLinkedManaSupport, "missing-mana");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Compile_InvalidStrategyCount_ReturnsStrategyCountDiagnostic(int strategyCount)
    {
        var project = CreateProject(strategyCount: strategyCount);

        var compilation = new ModularDeckCompiler().Compile(project, Selection("strategy-1"));

        AssertDiagnostic(
            compilation,
            ModularDeckDiagnosticRule.StrategyCount,
            Enumerable.Range(1, strategyCount).Select(index => $"strategy-{index}").ToArray());
    }

    [Fact]
    public void Compile_UnequalStrategyCardTotals_ReturnsUnequalStrategySizeDiagnostic()
    {
        var project = CreateProject(strategyCardQuantities: new[] { 1, 2 });

        var compilation = new ModularDeckCompiler().Compile(project, Selection("strategy-1"));

        AssertDiagnostic(compilation, ModularDeckDiagnosticRule.UnequalStrategySize, "strategy-1", "strategy-2");
    }

    [Fact]
    public void Compile_UnselectedStrategyOverlap_ReturnsOverlapWithOrderedDisplayNames()
    {
        var project = CreateProject(
            coreName: "zebra core",
            strategyNames: new[] { "Alpha", "zebra core" });

        var compilation = new ModularDeckCompiler().Compile(project, Selection("strategy-1"));

        AssertDiagnostic(compilation, ModularDeckDiagnosticRule.Overlap, "zebra core", "zebra core");
    }

    [Theory]
    [InlineData("core")]
    [InlineData("strategy")]
    public void Compile_CommanderBoardEntryInCoreOrModule_ReturnsCommandZoneMutationDiagnostic(string location)
    {
        var project = CreateProject(
            coreBoard: location == "core" ? "commander" : "mainboard",
            strategyBoard: location == "strategy" ? "commander" : "mainboard");

        var compilation = new ModularDeckCompiler().Compile(project, Selection("strategy-1"));

        AssertDiagnostic(
            compilation,
            ModularDeckDiagnosticRule.CommandZoneMutation,
            location == "core" ? new[] { "Core Card" } : new[] { "Strategy 1 Card", "Strategy 2 Card" });
    }

    [Fact]
    public void Compile_TotalOtherThanOneHundred_ReturnsTotalCardCountDiagnostic()
    {
        var project = CreateProject(coreQuantity: 95);

        var compilation = new ModularDeckCompiler().Compile(project, Selection("strategy-1"));

        AssertDiagnostic(compilation, ModularDeckDiagnosticRule.TotalCardCount, "99");
    }

    [Fact]
    public void Compile_SelectedModuleOnlyEntry_IsStructurallyValidWithoutRoleTaxonomy()
    {
        var compilation = new ModularDeckCompiler().Compile(CreateValidProject(), Selection("alpha"));

        Assert.True(compilation.IsStructurallyValid);
        Assert.Empty(compilation.Diagnostics);
    }

    private static void AssertDiagnostic(
        ModularDeckCompilation compilation,
        ModularDeckDiagnosticRule rule,
        params string[] affectedIdentifiers)
    {
        var diagnostic = Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Rule == rule);
        Assert.Equal(affectedIdentifiers, diagnostic.AffectedIdentifiers);
    }

    private static ModularDeckProject CreateValidProject(string strategyManaId = "alpha-mana") => new()
    {
        CommandZone = new[] { Entry("Commander One", 1, "commander"), Entry("Commander Two", 1, "commander") },
        BaselineMainboardEntries = Array.Empty<DeckEntry>(),
        CoreEntries = new[] { Entry("Core Card", 96) },
        StrategyModules = new[]
        {
            Module("alpha", "Alpha", "Alpha Card", strategyManaId),
            Module("beta", "Beta", "Beta Card", "beta-mana"),
        },
        ManaSupportModules = new[] { Mana("alpha-mana", "Alpha Mana", "Alpha Land"), Mana("beta-mana", "Beta Mana", "Beta Land") },
    };

    private static ModularDeckProject CreateProject(
        int strategyCount = 2,
        int[]? strategyCardQuantities = null,
        string coreName = "Core Card",
        string[]? strategyNames = null,
        string coreBoard = "mainboard",
        string strategyBoard = "mainboard",
        int coreQuantity = 96)
    {
        var strategies = Enumerable.Range(1, strategyCount)
            .Select(index => Module(
                $"strategy-{index}",
                $"Strategy {index}",
                strategyNames?[index - 1] ?? $"Strategy {index} Card",
                $"mana-{index}",
                strategyCardQuantities?[index - 1] ?? 1,
                strategyBoard))
            .ToArray();
        var mana = Enumerable.Range(1, strategyCount)
            .Select(index => Mana($"mana-{index}", $"Mana {index}", $"Mana {index} Card"))
            .ToArray();

        return new ModularDeckProject
        {
            CommandZone = new[] { Entry("Commander One", 1, "commander"), Entry("Commander Two", 1, "commander") },
            BaselineMainboardEntries = Array.Empty<DeckEntry>(),
            CoreEntries = new[] { Entry(coreName, coreQuantity, coreBoard) },
            StrategyModules = strategies,
            ManaSupportModules = mana,
        };
    }

    private static ModularDeckSelection Selection(string strategyId) => new() { StrategyId = strategyId };

    private static ModularStrategyModule Module(
        string id,
        string displayName,
        string cardName,
        string manaSupportId,
        int quantity = 1,
        string board = "mainboard")
    {
        return new ModularStrategyModule
        {
            Id = id,
            DisplayName = displayName,
            MainboardEntries = new[] { Entry(cardName, quantity, board) },
            ManaSupportModuleId = manaSupportId,
        };
    }

    private static ModularManaSupportModule Mana(string id, string displayName, string cardName) => new()
    {
        Id = id,
        DisplayName = displayName,
        MainboardEntries = new[] { Entry(cardName, 1) },
    };

    private static DeckEntry Entry(string name, int quantity, string board = "mainboard") => new()
    {
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        Quantity = quantity,
        Board = board,
    };
}
