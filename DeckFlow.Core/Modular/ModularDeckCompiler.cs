using DeckFlow.Core.Exporting;
using DeckFlow.Core.Models;

namespace DeckFlow.Core.Modular;

/// <summary>
/// Assembles a selected modular deck configuration from already-resolved card entries.
/// </summary>
public sealed class ModularDeckCompiler
{
    private readonly IModularCardLegalityCatalog? _legalityCatalog;

    /// <summary>
    /// Initializes a compiler with optional caller-injected card legality facts.
    /// </summary>
    /// <param name="legalityCatalog">The local catalog supplying card facts.</param>
    public ModularDeckCompiler(IModularCardLegalityCatalog? legalityCatalog = null)
    {
        _legalityCatalog = legalityCatalog;
    }

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
        AddDiagnostic(diagnostics, ModularDeckDiagnosticRule.InvalidQuantity,
            project.BaselineMainboardEntries.Concat(project.CoreEntries)
                .Concat(project.StrategyModules.SelectMany(module => module.MainboardEntries))
                .Concat(project.ManaSupportModules.SelectMany(module => module.MainboardEntries))
                .Concat(project.CommandZone).Where(entry => entry.Quantity <= 0).Select(entry => entry.Name));

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

        AddLegalityDiagnostics(entries, project.CommandZone, diagnostics);
        var orderedDiagnostics = diagnostics
            .OrderBy(diagnostic => diagnostic.Rule)
            .ToArray()
            .AsReadOnly();

        return new ModularDeckCompilation
        {
            IsStructurallyValid = !orderedDiagnostics.Any(diagnostic => IsStructuralRule(diagnostic.Rule)),
            IsVerifiedLegal = orderedDiagnostics.Count == 0,
            Diagnostics = orderedDiagnostics,
            SwapPlan = CreateSwapPlan(project.BaselineMainboardEntries, mainboardEntries),
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

    private void AddLegalityDiagnostics(
        IReadOnlyList<DeckEntry> entries,
        IReadOnlyList<DeckEntry> commandZone,
        List<ModularDeckDiagnostic> diagnostics)
    {
        var factsByName = entries
            .GroupBy(entry => entry.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => _legalityCatalog?.GetFacts(group.Key), StringComparer.OrdinalIgnoreCase);
        AddDiagnostic(
            diagnostics,
            ModularDeckDiagnosticRule.UnverifiableCardFacts,
            entries.Where(entry => factsByName[entry.NormalizedName] is null).Select(entry => entry.Name));

        AddDiagnostic(
            diagnostics,
            ModularDeckDiagnosticRule.BannedCard,
            entries.Where(entry => factsByName[entry.NormalizedName]?.IsBanned == true).Select(entry => entry.Name));

        AddDiagnostic(
            diagnostics,
            ModularDeckDiagnosticRule.Singleton,
            entries
                .Where(entry => !string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
                .GroupBy(entry => entry.NormalizedName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Sum(entry => entry.Quantity) > 1 && factsByName[group.Key]?.IsSingletonExempt == false)
                .Select(group => group.First().Name));

        if (commandZone.Any(entry => factsByName[entry.NormalizedName] is null))
        {
            return;
        }

        if (commandZone.Count == 0)
        {
            AddDiagnostic(diagnostics, ModularDeckDiagnosticRule.EmptyCommandZone, "command zone");
            return;
        }

        var commanderIdentity = commandZone
            .SelectMany(entry => factsByName[entry.NormalizedName]!.ColorIdentity)
            .ToHashSet(StringComparer.Ordinal);
        AddDiagnostic(
            diagnostics,
            ModularDeckDiagnosticRule.ColorIdentity,
            entries
                .Where(entry => factsByName[entry.NormalizedName] is not null)
                .Where(entry => CommanderIdentityCheck.IsWithinCommanderIdentity(factsByName[entry.NormalizedName]!.ColorIdentity, commanderIdentity) == CommanderIdentityCheckResult.Illegal)
                .Select(entry => entry.Name));
    }

    private static ModularDeckSwapPlan CreateSwapPlan(IReadOnlyList<DeckEntry> baseline, IReadOnlyList<DeckEntry> compiled)
    {
        var baselineByName = AggregateEntries(baseline);
        var compiledByName = AggregateEntries(compiled);
        var add = new List<ModularDeckSwapEntry>();
        var remove = new List<ModularDeckSwapEntry>();
        foreach (var key in baselineByName.Keys.Concat(compiledByName.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            baselineByName.TryGetValue(key, out var baselineEntry);
            compiledByName.TryGetValue(key, out var compiledEntry);
            var delta = (compiledEntry?.Quantity ?? 0) - (baselineEntry?.Quantity ?? 0);
            if (delta > 0 && compiledEntry is not null)
            {
                add.Add(CreateSwapEntry(compiledEntry!, delta, ModularDeckSwapAction.Add));
            }
            else if (delta < 0 && baselineEntry is not null)
            {
                remove.Add(CreateSwapEntry(baselineEntry!, -delta, ModularDeckSwapAction.Remove));
            }
        }

        var orderedAdd = OrderSwapEntries(add);
        var orderedRemove = OrderSwapEntries(remove);
        return new ModularDeckSwapPlan
        {
            ToAdd = orderedAdd,
            ToRemove = orderedRemove,
            ToReset = OrderSwapEntries(orderedAdd.Select(entry => entry with { Action = ModularDeckSwapAction.Remove }).Concat(orderedRemove.Select(entry => entry with { Action = ModularDeckSwapAction.Add }))),
        };
    }

    private static Dictionary<string, DeckEntry> AggregateEntries(IReadOnlyList<DeckEntry> entries) => entries
        .GroupBy(entry => entry.NormalizedName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.First() with { Quantity = group.Sum(entry => entry.Quantity) },
            StringComparer.OrdinalIgnoreCase);

    private static ModularDeckSwapEntry CreateSwapEntry(DeckEntry entry, int quantity, ModularDeckSwapAction action) => new()
    {
        Action = action,
        Name = entry.Name,
        NormalizedName = entry.NormalizedName,
        Quantity = quantity,
    };

    private static bool IsStructuralRule(ModularDeckDiagnosticRule rule) => rule is
        ModularDeckDiagnosticRule.MissingSelection or
        ModularDeckDiagnosticRule.UnknownStrategy or
        ModularDeckDiagnosticRule.MissingLinkedManaSupport or
        ModularDeckDiagnosticRule.StrategyCount or
        ModularDeckDiagnosticRule.UnequalStrategySize or
        ModularDeckDiagnosticRule.Overlap or
        ModularDeckDiagnosticRule.CommandZoneMutation or
        ModularDeckDiagnosticRule.TotalCardCount;

    private static IReadOnlyList<ModularDeckSwapEntry> OrderSwapEntries(IEnumerable<ModularDeckSwapEntry> entries) => entries
        .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Name, StringComparer.Ordinal)
        .ToArray()
        .AsReadOnly();

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
        var sourceEntries = new[]
        {
            project.CoreEntries,
            project.StrategyModules.SelectMany(module => module.MainboardEntries).ToArray(),
            project.ManaSupportModules.SelectMany(module => module.MainboardEntries).ToArray(),
        };
        foreach (var overlap in sourceEntries
            .SelectMany(source => source.GroupBy(entry => entry.NormalizedName, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            .GroupBy(entry => entry.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
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
