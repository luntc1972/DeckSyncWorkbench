using DeckFlow.Core.Models;

namespace DeckFlow.Core.Modular;

/// <summary>
/// Assembles a selected modular deck configuration from already-resolved card entries.
/// </summary>
public sealed class ModularDeckCompiler
{
    /// <summary>
    /// Compiles the selected strategy and its linked mana support in deterministic source-list order.
    /// </summary>
    /// <param name="project">The imported baseline and available modules.</param>
    /// <param name="selection">The selected strategy.</param>
    /// <returns>The assembled command zone and mainboard configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> or <paramref name="selection"/> is <see langword="null"/>.</exception>
    public ModularDeckCompilation Compile(ModularDeckProject project, ModularDeckSelection selection)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(selection);

        var strategy = project.StrategyModules.Single(module => module.Id == selection.StrategyId);
        var manaSupport = project.ManaSupportModules.Single(module => module.Id == strategy.ManaSupportModuleId);
        var mainboardEntries = new List<DeckEntry>(project.CoreEntries.Count + strategy.MainboardEntries.Count + manaSupport.MainboardEntries.Count);
        mainboardEntries.AddRange(project.CoreEntries);
        mainboardEntries.AddRange(strategy.MainboardEntries);
        mainboardEntries.AddRange(manaSupport.MainboardEntries);

        var entries = new List<DeckEntry>(project.CommandZone.Count + mainboardEntries.Count);
        entries.AddRange(project.CommandZone);
        entries.AddRange(mainboardEntries);

        return new ModularDeckCompilation
        {
            SelectedStrategyId = strategy.Id,
            SelectedStrategyName = strategy.DisplayName,
            SelectedManaSupportModuleId = manaSupport.Id,
            SelectedManaSupportModuleName = manaSupport.DisplayName,
            CommandZoneEntries = Array.AsReadOnly(project.CommandZone.ToArray()),
            MainboardEntries = mainboardEntries.AsReadOnly(),
            Entries = entries.AsReadOnly(),
            TotalCardCount = entries.Sum(entry => entry.Quantity),
        };
    }
}
