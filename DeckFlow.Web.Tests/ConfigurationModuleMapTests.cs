using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Services.Modular;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class ConfigurationModuleMapTests
{
    [Fact]
    public void TryResolve_CoreOnlyCard_ReturnsCore()
    {
        var map = ConfigurationModuleMap.Build(Request(coreEntries: [Entry("Sol Ring")]));

        Assert.True(map.TryResolve("Sol Ring", out var kind, out var displayName));
        Assert.Equal(ConfigurationModuleKind.Core, kind);
        Assert.Equal("Core", displayName);
    }

    [Fact]
    public void TryResolve_SelectedStrategyCard_ReturnsStrategy()
    {
        var map = ConfigurationModuleMap.Build(Request(mainboardEntries: [Entry("Demonic Consultation")]));

        Assert.True(map.TryResolve("Demonic Consultation", out var kind, out var displayName));
        Assert.Equal(ConfigurationModuleKind.Strategy, kind);
        Assert.Equal("Combo", displayName);
    }

    [Theory]
    [InlineData("Lands", "Lands")]
    [InlineData(null, "Mana Support")]
    public void TryResolve_ManaSupportOnlyCard_ReturnsManaSupport(string? manaSupportName, string expectedDisplayName)
    {
        var map = ConfigurationModuleMap.Build(Request(manaSupportName: manaSupportName, manaSupportEntries: [Entry("Ancient Tomb")]));

        Assert.True(map.TryResolve("Ancient Tomb", out var kind, out var displayName));
        Assert.Equal(ConfigurationModuleKind.ManaSupport, kind);
        Assert.Equal(expectedDisplayName, displayName);
    }

    [Fact]
    public void TryResolve_CommandZoneOnlyCard_ReturnsCommandZone()
    {
        var map = ConfigurationModuleMap.Build(Request(commandZone: [Entry("Thrasios, Triton Hero", "commander")]));

        Assert.True(map.TryResolve("Thrasios, Triton Hero", out var kind, out var displayName));
        Assert.Equal(ConfigurationModuleKind.CommandZone, kind);
        Assert.Null(displayName);
    }

    [Fact]
    public void TryResolve_CardInCoreAndStrategy_ReturnsMultiple()
    {
        var map = ConfigurationModuleMap.Build(Request(coreEntries: [Entry("Force of Will")], mainboardEntries: [Entry("Force of Will")]));

        Assert.True(map.TryResolve("Force of Will", out var kind, out var displayName));
        Assert.Equal(ConfigurationModuleKind.Multiple, kind);
        Assert.Equal("multiple modules", displayName);
    }

    [Fact]
    public void TryResolve_DifferentCase_ReturnsMatchingCard()
    {
        var map = ConfigurationModuleMap.Build(Request(coreEntries: [Entry("Ancient Tomb")]));

        Assert.True(map.TryResolve("ancient tomb", out var kind, out _));
        Assert.Equal(ConfigurationModuleKind.Core, kind);
    }

    [Fact]
    public void TryResolve_UnknownCard_ReturnsFalseAndUnknown()
    {
        var map = ConfigurationModuleMap.Build(Request());

        Assert.False(map.TryResolve("Missing Card", out var kind, out var displayName));
        Assert.Equal(ConfigurationModuleKind.Unknown, kind);
        Assert.Null(displayName);
    }

    private static DeckModulesCompilationRequest Request(
        IReadOnlyList<DeckEntry>? commandZone = null,
        IReadOnlyList<DeckEntry>? coreEntries = null,
        IReadOnlyList<DeckEntry>? mainboardEntries = null,
        string? manaSupportName = null,
        IReadOnlyList<DeckEntry>? manaSupportEntries = null) => new()
        {
            BaselineToken = "baseline",
            CommandZone = commandZone ?? [],
            BaselineMainboardEntries = [],
            CoreEntries = coreEntries ?? [],
            Alternatives =
            [
                new DeckModulesAlternativeInput
                {
                    Id = "selected",
                    Name = "Combo",
                    Profile = DeckModulesProfile.Cedh,
                    PlayPlan = "Assemble a compact combo.",
                    MainboardEntries = mainboardEntries ?? [],
                    ManaSupportName = manaSupportName,
                    ManaSupportEntries = manaSupportEntries ?? [],
                },
            ],
            SelectedAlternativeId = "selected",
        };

    private static DeckEntry Entry(string name, string board = "mainboard") => new()
    {
        Name = name,
        NormalizedName = CardNormalizer.Normalize(name),
        Quantity = 1,
        Board = board,
    };
}
