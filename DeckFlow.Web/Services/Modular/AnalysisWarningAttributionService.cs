using DeckFlow.Core.Manabase;
using DeckFlow.Core.Modular;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Models.DeckModules;

namespace DeckFlow.Web.Services.Modular;

/// <summary>Attributes warnings using direct swap evidence before module-membership inference.</summary>
public sealed class AnalysisWarningAttributionService : IAnalysisWarningAttributionService
{
    /// <inheritdoc />
    public IReadOnlyList<ConfigurationAttributedFinding> AttributeFindings(IReadOnlyList<ColorSourceFinding> findings, ModularDeckSwapPlan swapPlan, ConfigurationModuleMap moduleMap)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(swapPlan);
        ArgumentNullException.ThrowIfNull(moduleMap);

        var swaps = swapPlan.ToAdd.Concat(swapPlan.ToRemove).ToDictionary(entry => entry.NormalizedName, StringComparer.Ordinal);
        return findings.Select(finding => AttributeFinding(finding, swaps, moduleMap)).ToArray();
    }

    private static ConfigurationAttributedFinding AttributeFinding(ColorSourceFinding finding, IReadOnlyDictionary<string, ModularDeckSwapEntry> swaps, ConfigurationModuleMap moduleMap)
    {
        if (TryFindSwap(finding.DrivingSpell, swaps, out var swap) || TryFindSwap(finding.WorstSpell, swaps, out swap))
        {
            return CreateFinding(finding) with { AttributedCard = swap.Name, SwapDirection = swap.Action == ModularDeckSwapAction.Add ? "added" : "removed", Strength = ConfigurationAttributionStrength.NamedCard };
        }

        if (moduleMap.TryResolve(finding.DrivingSpell, out var kind, out var module))
        {
            return CreateFinding(finding) with { AttributedModule = module, AttributedModuleKind = kind, Strength = ConfigurationAttributionStrength.ModuleMembership };
        }

        return CreateFinding(finding);
    }

    private static bool TryFindSwap(string cardName, IReadOnlyDictionary<string, ModularDeckSwapEntry> swaps, out ModularDeckSwapEntry entry)
        => swaps.TryGetValue(CardNormalizer.Normalize(cardName), out entry!);

    private static ConfigurationAttributedFinding CreateFinding(ColorSourceFinding finding) => new()
    {
        Color = finding.Color.ToString(),
        DisplayColor = finding.IsSpecialCategory ? finding.DisplayColor : finding.Color.ToString(),
        ActualSources = finding.ActualSources,
        RequiredSources = finding.RequiredSources,
        Deficit = finding.Deficit,
        DrivingSpell = finding.DrivingSpell,
        NeedsMoreSources = finding.NeedsMoreSources,
        Strength = ConfigurationAttributionStrength.None,
    };
}
