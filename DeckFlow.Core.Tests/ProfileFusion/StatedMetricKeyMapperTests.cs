using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.ProfileFusion;
using Xunit;

namespace DeckFlow.Core.Tests.ProfileFusion;

public sealed class StatedMetricKeyMapperTests
{
    public static TheoryData<string, string> CategoryMappings =>
        new()
        {
            { "ramp", "category_ratio:ramp" },
            { "removal", "category_ratio:removal" },
            { "draw", "category_ratio:draw" },
            { "finishers", "category_ratio:finishers" },
            { "win-cons", "category_ratio:win-cons" },
            { "counter", "category_ratio:counter" },
            { "protection", "category_ratio:protection" },
            { "board-wipe", "category_ratio:board-wipe" },
            { "tutor", "category_ratio:tutor" },
            { "recursion", "category_ratio:recursion" },
            { "utility", "category_ratio:utility" },
        };

    public static TheoryData<string> IdentityMappings =>
        new()
        {
            "karsten:target_lands",
            "karsten:land_delta",
            "karsten:health_score",
            "combo_density:included_per_deck",
        };

    public static TheoryData<string> StatedOnlyMappings =>
        new()
        {
            "interaction",
            "opener_probability",
            "pip_distribution",
            "power_level_philosophy",
        };

    [Theory]
    [MemberData(nameof(CategoryMappings))]
    public void TryMapToMeasuredKey_MapsEachCategoryMetricByPrefix(string statedMetric, string expectedMeasuredKey)
    {
        var mapped = StatedMetricKeyMapper.TryMapToMeasuredKey(statedMetric.ToUpperInvariant(), out var measuredKey);

        Assert.True(mapped);
        Assert.Equal(StatedMetricMapKind.Direct, StatedMetricKeyMapper.GetMapKind(statedMetric));
        Assert.Equal(expectedMeasuredKey, measuredKey);
    }

    [Theory]
    [MemberData(nameof(IdentityMappings))]
    public void TryMapToMeasuredKey_MapsEachIdentityMetricToItself(string statedMetric)
    {
        var mapped = StatedMetricKeyMapper.TryMapToMeasuredKey(statedMetric, out var measuredKey);

        Assert.True(mapped);
        Assert.Equal(StatedMetricMapKind.Direct, StatedMetricKeyMapper.GetMapKind(statedMetric));
        Assert.Equal(statedMetric, measuredKey);
    }

    [Fact]
    public void GetMapKind_ReportsLandCountAsDerived()
    {
        var mapped = StatedMetricKeyMapper.TryMapToMeasuredKey("land_count", out var measuredKey);

        Assert.False(mapped);
        Assert.Equal(StatedMetricMapKind.Derived, StatedMetricKeyMapper.GetMapKind("land_count"));
        Assert.Equal(string.Empty, measuredKey);
    }

    [Fact]
    public void TryGetDerivedValue_LandCount_SumsTargetLandsAndLandDelta()
    {
        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["karsten:target_lands"] = 37,
            ["karsten:land_delta"] = 1,
        };

        var derived = StatedMetricKeyMapper.TryGetDerivedValue("land_count", metrics, out double value);

        Assert.True(derived);
        Assert.Equal(38, value);
    }

    [Theory]
    [MemberData(nameof(StatedOnlyMappings))]
    public void TryMapToMeasuredKey_LeavesStatedOnlyMetricsUnmapped(string statedMetric)
    {
        var mapped = StatedMetricKeyMapper.TryMapToMeasuredKey(statedMetric, out var measuredKey);

        Assert.False(mapped);
        Assert.Equal(StatedMetricMapKind.StatedOnly, StatedMetricKeyMapper.GetMapKind(statedMetric));
        Assert.Equal(string.Empty, measuredKey);
    }

    [Fact]
    public void TryMapToMeasuredKey_ReturnsFalseForUnknownMetricWithoutThrowing()
    {
        var mapped = StatedMetricKeyMapper.TryMapToMeasuredKey("stax", out var measuredKey);

        Assert.False(mapped);
        Assert.Equal(StatedMetricMapKind.StatedOnly, StatedMetricKeyMapper.GetMapKind("stax"));
        Assert.Equal(string.Empty, measuredKey);
    }

    [Fact]
    public void PrefixMappedCategories_MatchClosedCardCategoryVocabularyExactly()
    {
        Assert.Equal(
            ContentTagVocabulary.CardCategories.OrderBy(static category => category, StringComparer.OrdinalIgnoreCase),
            StatedMetricKeyMapper.PrefixMappedCategories.OrderBy(static category => category, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }
}
