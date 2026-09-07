using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Services.Modular;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class ConfigurationDeltaServiceTests
{
    [Fact]
    public void ComputeDelta_ThreeConfigurations_ReturnsNonReferenceColumnsInListOrder()
    {
        var result = new ConfigurationDeltaService().ComputeDelta([Analysis("reference"), Analysis("first"), Analysis("second")], 0);

        Assert.Equal(["first", "second"], result.Columns.Select(column => column.ConfigurationId));
    }

    [Fact]
    public void ComputeDelta_TwoConfigurations_ReturnsOneDeltaColumn()
    {
        var result = new ConfigurationDeltaService().ComputeDelta([Analysis("reference"), Analysis("other")], 0);

        Assert.Single(result.Columns);
    }

    [Fact]
    public void ComputeDelta_InvalidArguments_ThrowsSpecifiedExceptions()
    {
        var service = new ConfigurationDeltaService();

        Assert.Throws<ArgumentNullException>(() => service.ComputeDelta(null!, 0));
        Assert.Throws<ArgumentException>(() => service.ComputeDelta([Analysis("only")], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.ComputeDelta([Analysis("one"), Analysis("two")], 2));
    }

    [Fact]
    public void ComputeDelta_EqualLandCounts_ReportsZeroDeltaAndNoChange()
    {
        var result = new ConfigurationDeltaService().ComputeDelta([Analysis("reference", landCount: 35), Analysis("other", landCount: 35)], 0);

        Assert.Equal(0, result.Columns[0].LandCountDelta);
        Assert.False(result.Columns[0].HasLandCountChange);
    }

    [Fact]
    public void ComputeDelta_ReferenceOnlyColor_ReportsOtherValueAsAbsent()
    {
        var result = new ConfigurationDeltaService().ComputeDelta([Analysis("reference", colors: [Color("W")]), Analysis("other")], 0);

        Assert.False(result.ColorRows[0].Values[1].IsPresent);
        Assert.Null(result.ColorRows[0].Values[1].ActualSources);
    }

    [Fact]
    public void ComputeDelta_ColorRows_ValuesAlignOneToOneWithAllAnalyses()
    {
        var analyses = new[]
        {
            Analysis("reference", colors: [Color("W")]),
            Analysis("other", colors: [Color("W")]),
        };

        var result = new ConfigurationDeltaService().ComputeDelta(analyses, 0);

        // Every color row must carry one value per analysis (reference included), so it aligns
        // 1:1 with the rendered [Reference, ...Columns] header. See CR-01.
        Assert.Equal(analyses.Length, result.ColorRows[0].Values.Count);
        Assert.True(result.ColorRows[0].Values[0].IsPresent);
        Assert.Null(result.ColorRows[0].Values[0].ActualSourcesDelta);
    }

    [Fact]
    public void ComputeDelta_OtherOnlyColor_AppendsItAfterReferenceOrderedRows()
    {
        var result = new ConfigurationDeltaService().ComputeDelta([Analysis("reference", colors: [Color("U")]), Analysis("other", colors: [Color("B")])], 0);

        Assert.Equal(["U", "B"], result.ColorRows.Select(row => row.Color));
    }

    [Fact]
    public void ComputeDelta_ColorRows_PreserveReferenceOrderForEqualDeltas()
    {
        var analyses = new[]
        {
            Analysis("reference", colors: [Color("U"), Color("W")]),
            Analysis("other", colors: [Color("U"), Color("W")]),
        };

        var first = new ConfigurationDeltaService().ComputeDelta(analyses, 0);
        var second = new ConfigurationDeltaService().ComputeDelta(analyses, 0);

        Assert.Equal(["U", "W"], first.ColorRows.Select(row => row.Color));
        Assert.Equal(first.ColorRows.Select(row => row.Color), second.ColorRows.Select(row => row.Color));
    }

    [Fact]
    public void ComputeDelta_NullAnalysis_ReturnsUnanalyzedColumnWithAbsentMetrics()
    {
        var result = new ConfigurationDeltaService().ComputeDelta([Analysis("reference"), null], 0);

        Assert.False(result.Columns[0].IsAnalyzed);
        Assert.Null(result.Columns[0].LandCount);
        Assert.Null(result.Columns[0].TargetLandCount);
        Assert.Null(result.Columns[0].LandTargetDelta);
        Assert.Null(result.Columns[0].RampSourceCount);
        Assert.Null(result.Columns[0].HardToCastCount);
        Assert.Null(result.Columns[0].LandCountDelta);
        Assert.Null(result.Columns[0].RampSourceCountDelta);
        Assert.Null(result.Columns[0].HardToCastCountDelta);
    }

    [Fact]
    public void ComputeDelta_Interactions_AlignsByModuleKindAndPreservesAbsence()
    {
        var result = new ConfigurationDeltaService().ComputeDelta(
            [Analysis("reference", interactions: [Interaction(ConfigurationModuleKind.Strategy, "Strategy", 2)]), Analysis("other", interactions: [Interaction(ConfigurationModuleKind.ManaSupport, "Mana Support", 3)])],
            0);

        Assert.Equal([ConfigurationModuleKind.Strategy, ConfigurationModuleKind.ManaSupport], result.InteractionRows.Select(row => row.ModuleKind));
        Assert.False(result.InteractionRows[0].Values[1].IsPresent);
        Assert.False(result.InteractionRows[1].Values[0].IsPresent);
    }

    [Fact]
    public void ConfigurationComparisonDeltaModel_ContainsNoPromptSchemaOrTextMembers()
    {
        var memberNames = new[]
            {
                typeof(ConfigurationComparisonDeltaModel),
                typeof(ConfigurationComparisonColumn),
                typeof(ConfigurationColorSourceDeltaRow),
                typeof(ConfigurationColorSourceDeltaValue),
                typeof(ConfigurationInteractionDeltaRow),
                typeof(ConfigurationInteractionDeltaValue),
            }
            .SelectMany(type => type.GetMembers())
            .Select(member => member.Name);

        Assert.DoesNotContain(memberNames, name => name.Contains("Prompt", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Schema", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Text", StringComparison.OrdinalIgnoreCase));
    }

    private static ConfigurationAnalysisResult Analysis(
        string id,
        int landCount = 35,
        IReadOnlyList<ConfigurationAttributedFinding>? colors = null,
        IReadOnlyList<ConfigurationModuleInteractionCount>? interactions = null) => new()
        {
            ConfigurationId = id,
            ConfigurationName = id,
            AnalyzedCardCount = 100,
            LandCount = landCount,
            TargetLandCount = 36,
            LandDelta = landCount - 36,
            Health = "Healthy",
            RampSourceCount = 10,
            HardToCastCount = 0,
            IsCoreOnly = false,
            AttributedFindings = colors ?? [],
            Signals = interactions is null ? null : new ConfigurationSignalSummary
            {
                BracketNumber = 3,
                ComboDetectionAvailable = true,
                CatalogEffectiveDate = "2026-01-01",
                InteractionsByModule = interactions,
            },
        };

    private static ConfigurationAttributedFinding Color(string color) => new()
    {
        Color = color,
        DisplayColor = color,
        ActualSources = 10,
        RequiredSources = 12,
        Deficit = 2,
        DrivingSpell = "Test Spell",
        NeedsMoreSources = true,
        Strength = ConfigurationAttributionStrength.NamedCard,
    };

    private static ConfigurationModuleInteractionCount Interaction(ConfigurationModuleKind kind, string name, int count) => new()
    {
        ModuleKind = kind,
        ModuleName = name,
        InteractionCount = count,
    };
}
