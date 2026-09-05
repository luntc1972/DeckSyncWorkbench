using DeckFlow.Core.Modular;
using DeckFlow.Core.Models;

namespace DeckFlow.Core.Tests.Modular;

public sealed class ModularDeckCompilerLegalityAndSwapTests
{
    [Fact]
    public void Compile_IllegalFacts_ReportsNamedColorBannedAndSingletonDiagnostics()
    {
        var compilation = new ModularDeckCompiler(new TestCatalog(new Dictionary<string, ModularCardLegalityFacts>(StringComparer.Ordinal)
        {
            ["commander"] = Facts("W"),
            ["off identity"] = Facts("U"),
            ["banned card"] = Facts(banned: true),
            ["duplicate card"] = Facts(),
            ["strategy alpha"] = Facts(),
            ["strategy beta"] = Facts(),
            ["alpha land"] = Facts(),
            ["beta land"] = Facts(),
        })).Compile(CreateProject(new[] { Entry("Off Identity", 1), Entry("Banned Card", 1), Entry("Duplicate Card", 2), Entry("Core", 90) }), Selection("alpha"));

        Assert.False(compilation.IsVerifiedLegal);
        AssertDiagnostic(compilation, ModularDeckDiagnosticRule.ColorIdentity, "Off Identity");
        AssertDiagnostic(compilation, ModularDeckDiagnosticRule.BannedCard, "Banned Card");
        AssertDiagnostic(compilation, ModularDeckDiagnosticRule.Singleton, "Duplicate Card");
    }

    [Fact]
    public void Compile_MissingFactsAndSingletonExemption_ReportsUnverifiableWithoutSingletonFailure()
    {
        var compilation = new ModularDeckCompiler(new TestCatalog(new Dictionary<string, ModularCardLegalityFacts>(StringComparer.Ordinal)
        {
            ["commander"] = Facts("W"),
            ["basic plains"] = Facts(singletonExempt: true),
            ["strategy alpha"] = Facts(),
            ["strategy beta"] = Facts(),
            ["alpha land"] = Facts(),
            ["beta land"] = Facts(),
        })).Compile(CreateProject(new[] { Entry("Basic Plains", 91), Entry("Unknown Card", 1) }), Selection("alpha"));

        Assert.False(compilation.IsVerifiedLegal);
        AssertDiagnostic(compilation, ModularDeckDiagnosticRule.UnverifiableCardFacts, "Unknown Card");
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Rule == ModularDeckDiagnosticRule.Singleton);
    }

    [Fact]
    public void Compile_BaselineDifferences_ProducesOrderedAddRemoveAndReverseResetQuantities()
    {
        var compilation = new ModularDeckCompiler(new TestCatalog(AllFacts())).Compile(CreateProject(
            new[] { Entry("Core", 94) },
            new[] { Entry("Core", 94), Entry("Strategy Beta", 3), Entry("Beta Land", 2) }),
            Selection("alpha"));

        Assert.Equal(new[] { ("Alpha Land", 2), ("Strategy Alpha", 3) }, compilation.SwapPlan.ToAdd.Select(entry => (entry.Name, entry.Quantity)));
        Assert.Equal(new[] { ("Beta Land", 2), ("Strategy Beta", 3) }, compilation.SwapPlan.ToRemove.Select(entry => (entry.Name, entry.Quantity)));
        Assert.Equal(new[] { ("Alpha Land", 2), ("Strategy Alpha", 3), ("Beta Land", 2), ("Strategy Beta", 3) }, compilation.SwapPlan.ToReset.Select(entry => (entry.Name, entry.Quantity)));
    }

    private static void AssertDiagnostic(ModularDeckCompilation compilation, ModularDeckDiagnosticRule rule, string identifier) =>
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Rule == rule && diagnostic.AffectedIdentifiers.Contains(identifier));

    private static Dictionary<string, ModularCardLegalityFacts> AllFacts() => new(StringComparer.Ordinal)
    {
        ["commander"] = Facts("W"),
        ["core"] = Facts(),
        ["strategy alpha"] = Facts(),
        ["strategy beta"] = Facts(),
        ["alpha land"] = Facts(),
        ["beta land"] = Facts(),
    };

    private static ModularCardLegalityFacts Facts(string? color = null, bool banned = false, bool singletonExempt = false) => new()
    {
        ColorIdentity = color is null ? Array.Empty<string>() : new[] { color },
        IsBanned = banned,
        IsSingletonExempt = singletonExempt,
    };

    private static ModularDeckProject CreateProject(IReadOnlyList<DeckEntry> core, IReadOnlyList<DeckEntry>? baseline = null) => new()
    {
        CommandZone = new[] { Entry("Commander", 1, "commander") },
        BaselineMainboardEntries = baseline ?? core,
        CoreEntries = core,
        StrategyModules = new[] { Module("alpha", "Strategy Alpha", "alpha-mana"), Module("beta", "Strategy Beta", "beta-mana") },
        ManaSupportModules = new[] { Mana("alpha-mana", "Alpha Land"), Mana("beta-mana", "Beta Land") },
    };

    private static ModularDeckSelection Selection(string id) => new() { StrategyId = id };
    private static ModularStrategyModule Module(string id, string card, string manaId) => new() { Id = id, DisplayName = id, ManaSupportModuleId = manaId, MainboardEntries = new[] { Entry(card, 3) } };
    private static ModularManaSupportModule Mana(string id, string card) => new() { Id = id, DisplayName = id, MainboardEntries = new[] { Entry(card, 2) } };
    private static DeckEntry Entry(string name, int quantity, string board = "mainboard") => new() { Name = name, NormalizedName = name.ToLowerInvariant(), Quantity = quantity, Board = board };

    private sealed class TestCatalog(IReadOnlyDictionary<string, ModularCardLegalityFacts> facts) : IModularCardLegalityCatalog
    {
        public ModularCardLegalityFacts? GetFacts(string normalizedCardName) => facts.TryGetValue(normalizedCardName, out var value) ? value : null;
    }
}
