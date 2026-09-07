using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Core.Modular;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Services.Modular;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class AnalysisWarningAttributionServiceTests
{
    private readonly AnalysisWarningAttributionService _service = new();

    [Fact]
    public void AttributeFindings_DrivingSpellAddedCard_ReturnsNamedAddedCard()
    {
        var result = Attribute([Finding("Ancient Tomb")], Plan(add: [Swap("Ancient Tomb", ModularDeckSwapAction.Add)]));

        Assert.Equal(ConfigurationAttributionStrength.NamedCard, result[0].Strength);
        Assert.Equal("Ancient Tomb", result[0].AttributedCard);
        Assert.Equal("added", result[0].SwapDirection);
    }

    [Fact]
    public void AttributeFindings_DrivingSpellRemovedCard_ReturnsNamedRemovedCard()
    {
        var result = Attribute([Finding("Chrome Mox")], Plan(remove: [Swap("Chrome Mox", ModularDeckSwapAction.Remove)]));

        Assert.Equal("removed", result[0].SwapDirection);
        Assert.Equal("Chrome Mox", result[0].AttributedCard);
    }

    [Fact]
    public void AttributeFindings_DrivingSpellDiffersOnlyByCase_ReturnsNamedCard()
    {
        var result = Attribute([Finding("ancient tomb")], Plan(add: [Swap("Ancient Tomb", ModularDeckSwapAction.Add)]));

        Assert.Equal(ConfigurationAttributionStrength.NamedCard, result[0].Strength);
    }

    [Fact]
    public void AttributeFindings_WorstSpellMatchesSwap_ReturnsNamedCard()
    {
        var result = Attribute([Finding("Missing", "Force of Will")], Plan(add: [Swap("Force of Will", ModularDeckSwapAction.Add)]));

        Assert.Equal("Force of Will", result[0].AttributedCard);
    }

    [Fact]
    public void AttributeFindings_DrivingSpellInModuleMap_ReturnsInferredModule()
    {
        var result = Attribute([Finding("Demonic Consultation")], Plan(), Map(mainboard: [Entry("Demonic Consultation")]));

        Assert.Equal(ConfigurationAttributionStrength.ModuleMembership, result[0].Strength);
        Assert.Equal("Combo", result[0].AttributedModule);
        Assert.Null(result[0].AttributedCard);
    }

    [Fact]
    public void AttributeFindings_DrivingSpellInMultipleModules_ReturnsMultipleModuleLabel()
    {
        var result = Attribute([Finding("Sol Ring")], Plan(), Map(core: [Entry("Sol Ring")], mainboard: [Entry("Sol Ring")]));

        Assert.Equal(ConfigurationModuleKind.Multiple, result[0].AttributedModuleKind);
        Assert.Equal("multiple modules", result[0].AttributedModule);
    }

    [Fact]
    public void AttributeFindings_UnresolvedCards_ReturnsNone()
    {
        var result = Attribute([Finding("Missing", "Also Missing")], Plan());

        Assert.Equal(ConfigurationAttributionStrength.None, result[0].Strength);
        Assert.Null(result[0].AttributedCard);
        Assert.Null(result[0].AttributedModule);
    }

    [Fact]
    public void AttributeFindings_EmptyFindings_ReturnsEmpty()
        => Assert.Empty(Attribute([], Plan()));

    [Fact]
    public void AttributeFindings_NullFindings_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => _service.AttributeFindings(null!, Plan(), Map()));

    [Fact]
    public void AttributeFindings_AddAndRemoveNormalizeToTheSameKey_DoesNotThrowAndAttributesFirstWriter()
    {
        // WR-04: CardNormalizer.Normalize collapses split cards at " / ", so "Fire // Ice" and
        // "Fire // Something" both normalize to "fire". An unguarded ToDictionary threw
        // ArgumentException on this collision; TryAdd must keep the first writer instead.
        var result = Attribute(
            [Finding("Fire // Ice")],
            Plan(add: [Swap("Fire // Ice", ModularDeckSwapAction.Add)], remove: [Swap("Fire // Something", ModularDeckSwapAction.Remove)]));

        Assert.Equal(ConfigurationAttributionStrength.NamedCard, result[0].Strength);
        Assert.Equal("Fire // Ice", result[0].AttributedCard);
        Assert.Equal("added", result[0].SwapDirection);
    }

    [Fact]
    public void AttributeFindings_SwapComparerMatchesCompilerGrouping_ReturnsNamedCardOnCaseInsensitiveCollision()
    {
        // The dictionary must use the same comparer ModularDeckCompiler used to build the plan
        // (OrdinalIgnoreCase), not a stricter one, or a case-only collision would also throw.
        var result = Attribute(
            [Finding("Ancient Tomb")],
            Plan(add: [Swap("Ancient Tomb", ModularDeckSwapAction.Add)], remove: [Swap("ANCIENT TOMB", ModularDeckSwapAction.Remove)]));

        Assert.Equal(ConfigurationAttributionStrength.NamedCard, result[0].Strength);
    }

    [Fact]
    public void AttributeFindings_MultipleFindings_PreservesInputOrder()
    {
        var result = Attribute([Finding("Second"), Finding("First")], Plan(add: [Swap("First", ModularDeckSwapAction.Add), Swap("Second", ModularDeckSwapAction.Add)]));

        Assert.Equal(["Second", "First"], result.Select(row => row.DrivingSpell));
    }

    private IReadOnlyList<ConfigurationAttributedFinding> Attribute(IReadOnlyList<ColorSourceFinding> findings, ModularDeckSwapPlan plan, ConfigurationModuleMap? map = null)
        => _service.AttributeFindings(findings, plan, map ?? Map());

    private static ColorSourceFinding Finding(string drivingSpell, string worstSpell = "") => new()
    {
        Color = ManaColor.Blue,
        ActualSources = 12,
        RequiredSources = 15,
        DrivingSpell = drivingSpell,
        WorstSpell = worstSpell,
        ColorLimitedUnderSupportedCount = 1,
    };

    private static ModularDeckSwapPlan Plan(IReadOnlyList<ModularDeckSwapEntry>? add = null, IReadOnlyList<ModularDeckSwapEntry>? remove = null) => new() { ToAdd = add ?? [], ToRemove = remove ?? [], ToReset = [] };
    private static ModularDeckSwapEntry Swap(string name, ModularDeckSwapAction action) => new() { Name = name, NormalizedName = CardNormalizer.Normalize(name), Quantity = 1, Action = action };
    private static ConfigurationModuleMap Map(IReadOnlyList<DeckEntry>? core = null, IReadOnlyList<DeckEntry>? mainboard = null) => ConfigurationModuleMap.Build(new DeckModulesCompilationRequest { BaselineToken = "baseline", CommandZone = [], BaselineMainboardEntries = [], CoreEntries = core ?? [], Alternatives = [new DeckModulesAlternativeInput { Id = "selected", Name = "Combo", Profile = DeckModulesProfile.Cedh, PlayPlan = "Plan", MainboardEntries = mainboard ?? [], ManaSupportEntries = [] }], SelectedAlternativeId = "selected" });
    private static DeckEntry Entry(string name) => new() { Name = name, NormalizedName = CardNormalizer.Normalize(name), Quantity = 1, Board = "mainboard" };
}
