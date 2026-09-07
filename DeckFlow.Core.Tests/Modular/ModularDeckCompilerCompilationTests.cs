using DeckFlow.Core.Modular;
using DeckFlow.Core.Models;

namespace DeckFlow.Core.Tests.Modular;

public sealed class ModularDeckCompilerCompilationTests
{
    [Fact]
    public void Compile_SelectedStrategy_CompilesDeterministicCompleteConfiguration()
    {
        var commandZone = new[]
        {
            Entry("Commander One", 1, "commander"),
            Entry("Commander Two", 1, "commander"),
        };
        var core = new[] { Entry("Shared Core", 70) };
        var alpha = new ModularStrategyModule
        {
            Id = "alpha",
            DisplayName = "Alpha Strategy",
            MainboardEntries = new[] { Entry("Alpha Card", 8) },
            ManaSupportModuleId = "alpha-mana",
        };
        var beta = new ModularStrategyModule
        {
            Id = "beta",
            DisplayName = "Beta Strategy",
            MainboardEntries = new[] { Entry("Beta Card", 8) },
            ManaSupportModuleId = "beta-mana",
        };
        var alphaMana = new ModularManaSupportModule
        {
            Id = "alpha-mana",
            DisplayName = "Alpha Mana Support",
            MainboardEntries = new[] { Entry("Alpha Land", 20) },
        };
        var betaMana = new ModularManaSupportModule
        {
            Id = "beta-mana",
            DisplayName = "Beta Mana Support",
            MainboardEntries = new[] { Entry("Beta Land", 20) },
        };
        var project = new ModularDeckProject
        {
            CommandZone = commandZone,
            BaselineMainboardEntries = core.Concat(alpha.MainboardEntries).Concat(alphaMana.MainboardEntries).ToArray(),
            CoreEntries = core,
            StrategyModules = new[] { alpha, beta },
            ManaSupportModules = new[] { alphaMana, betaMana },
        };
        var compiler = new ModularDeckCompiler();

        var alphaCompilation = compiler.Compile(project, new ModularDeckSelection { StrategyId = alpha.Id });
        var repeatAlphaCompilation = compiler.Compile(project, new ModularDeckSelection { StrategyId = alpha.Id });
        var betaCompilation = compiler.Compile(project, new ModularDeckSelection { StrategyId = beta.Id });

        Assert.Equal("alpha", alphaCompilation.SelectedStrategyId);
        Assert.Equal("Alpha Strategy", alphaCompilation.SelectedStrategyName);
        Assert.Equal("alpha-mana", alphaCompilation.SelectedManaSupportModuleId);
        Assert.Equal("Alpha Mana Support", alphaCompilation.SelectedManaSupportModuleName);
        Assert.Equal(100, alphaCompilation.TotalCardCount);
        Assert.Equal(commandZone, alphaCompilation.CommandZoneEntries);
        Assert.Equal(new[] { core[0], alpha.MainboardEntries[0], alphaMana.MainboardEntries[0] }, alphaCompilation.MainboardEntries);
        Assert.Equal(commandZone.Concat(alphaCompilation.MainboardEntries), alphaCompilation.Entries);
        Assert.DoesNotContain(alphaCompilation.Entries, entry => entry.Name is "Beta Card" or "Beta Land");
        Assert.Equal(alphaCompilation.Entries, repeatAlphaCompilation.Entries);
        Assert.Equal(alphaCompilation.Entries.Select(entry => entry.Quantity), repeatAlphaCompilation.Entries.Select(entry => entry.Quantity));
        Assert.Equal(100, betaCompilation.TotalCardCount);
        Assert.DoesNotContain(betaCompilation.Entries, entry => entry.Name is "Alpha Card" or "Alpha Land");
    }

    private static DeckEntry Entry(string name, int quantity, string board = "mainboard") => new()
    {
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        Quantity = quantity,
        Board = board,
    };
}
