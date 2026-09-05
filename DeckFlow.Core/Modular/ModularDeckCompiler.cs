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
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is <see langword="null"/>.</exception>
    public ModularDeckCompilation Compile(ModularDeckProject project, ModularDeckSelection? selection)
    {
        ArgumentNullException.ThrowIfNull(project);

        var diagnostics = new List<ModularDeckDiagnostic>();
        AddProjectDiagnostics(project, diagnostics);

        if (selection is null)
        {
            AddDiagnostic(diagnostics, ModularDeckDiagnosticRule.MissingSelection, "selection");
        }

        var strategy = selection is null
            ? null
            : project.StrategyModules.FirstOrDefault(module => module.Id == selection.StrategyId);
        if (selection is not null && strategy is null)
        {
            AddDiagnostic(diagnostics, ModularDeckDiagnosticRule.UnknownStrategy, selection.StrategyId);
        }

        var manaSupport = strategy is null
            ? null
            : project.ManaSupportModules.FirstOrDefault(module => module.Id == strategy.ManaSupportModuleId);
        var mainboardEntries = new List<DeckEntry>(project.CoreEntries.Count + (strategy?.MainboardEntries.Count ?? 0) + (manaSupport?.MainboardEntries.Count ?? 0));
        mainboardEntries.AddRange(project.CoreEntries);
        if (strategy is not null)
        {
            mainboardEntries.AddRange(strategy.MainboardEntries);
        }

        if (manaSupport is not null)
        {
            mainboardEntries.AddRange(manaSupport.MainboardEntries);
        }

        var entries = new List<DeckEntry>(project.CommandZone.Count + mainboardEntries.Count);
        entries.AddRange(project.CommandZone);
        entries.AddRange(mainboardEntries);
        var totalCardCount = entries.Sum(entry => entry.Quantity);
        if (totalCardCount != 100)
        {
            AddDiagnostic(
                diagnostics,
                ModularDeckDiagnosticRule.TotalCardCount,
                totalCardCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return new ModularDeckCompilation
        {
            IsStructurallyValid = diagnostics.Count == 0,
            Diagnostics = diagnostics
                .OrderBy(diagnostic => diagnostic.Rule)
                .ToArray()
                .AsReadOnly(),
            SelectedStrategyId = strategy?.Id ?? selection?.StrategyId ?? string.Empty,
            SelectedStrategyName = strategy?.DisplayName ?? string.Empty,
            SelectedManaSupportModuleId = manaSupport?.Id ?? strategy?.ManaSupportModuleId ?? string.Empty,
            SelectedManaSupportModuleName = manaSupport?.DisplayName ?? string.Empty,
            CommandZoneEntries = Array.AsReadOnly(project.CommandZone.ToArray()),
            MainboardEntries = mainboardEntries.AsReadOnly(),
            Entries = entries.AsReadOnly(),
            TotalCardCount = totalCardCount,
        };
    }

    private static void AddProjectDiagnostics(ModularDeckProject project, List<ModularDeckDiagnostic> diagnostics)
    {
        if (project.StrategyModules.Count is < 2 or > 4)
        {
            AddDiagnostic(diagnostics, ModularDeckDiagnosticRule.StrategyCount, project.StrategyModules.Select(module => module.Id));
        }

        if (project.StrategyModules.Select(module => module.MainboardEntries.Sum(entry => entry.Quantity)).Distinct().Skip(1).Any())
        {
            AddDiagnostic(diagnostics, ModularDeckDiagnosticRule.UnequalStrategySize, project.StrategyModules.Select(module => module.Id));
        }

        var missingManaSupportIds = project.StrategyModules
            .Where(strategy => !project.ManaSupportModules.Any(manaSupport => manaSupport.Id == strategy.ManaSupportModuleId))
            .Select(strategy => strategy.ManaSupportModuleId);
        AddDiagnostic(diagnostics, ModularDeckDiagnosticRule.MissingLinkedManaSupport, missingManaSupportIds);

        var configurableEntries = project.CoreEntries
            .Concat(project.StrategyModules.SelectMany(module => module.MainboardEntries))
            .Concat(project.ManaSupportModules.SelectMany(module => module.MainboardEntries))
            .ToArray();
        foreach (var overlap in configurableEntries.GroupBy(entry => entry.NormalizedName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            AddDiagnostic(diagnostics, ModularDeckDiagnosticRule.Overlap, overlap.Select(entry => entry.Name));
        }

        AddDiagnostic(
            diagnostics,
            ModularDeckDiagnosticRule.CommandZoneMutation,
            configurableEntries.Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Name));
    }

    private static void AddDiagnostic(
        List<ModularDeckDiagnostic> diagnostics,
        ModularDeckDiagnosticRule rule,
        IEnumerable<string> affectedIdentifiers)
    {
        var orderedIdentifiers = affectedIdentifiers
            .OrderBy(identifier => identifier, StringComparer.OrdinalIgnoreCase)
            .ThenBy(identifier => identifier, StringComparer.Ordinal)
            .ToArray();
        if (orderedIdentifiers.Length > 0)
        {
            diagnostics.Add(new ModularDeckDiagnostic
            {
                Rule = rule,
                AffectedIdentifiers = Array.AsReadOnly(orderedIdentifiers),
            });
        }
    }

    private static void AddDiagnostic(List<ModularDeckDiagnostic> diagnostics, ModularDeckDiagnosticRule rule, params string[] affectedIdentifiers)
    {
        AddDiagnostic(diagnostics, rule, (IEnumerable<string>)affectedIdentifiers);
    }
}
