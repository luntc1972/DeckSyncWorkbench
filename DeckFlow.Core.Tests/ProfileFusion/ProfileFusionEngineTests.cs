using System.Globalization;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.ProfileFusion;
using DeckFlow.Core.Knowledge.StatedRulesExtraction;
using Xunit;

namespace DeckFlow.Core.Tests.ProfileFusion;

public sealed class ProfileFusionEngineTests
{
    [Fact]
    public void Fuse_MeasuredMetricsWithoutStatedRules_ReturnsMeasuredOnlyTargetsInMetricOrder()
    {
        MeasuredMetric[] measuredMetrics =
        [
            CreateMeasuredMetric("zeta", 7.5, effectiveSampleSize: 8.5),
            CreateMeasuredMetric("Alpha", 3.25, effectiveSampleSize: 9.5),
        ];

        IReadOnlyList<FusedTarget> result = ProfileFusionEngine.Fuse(measuredMetrics, []);

        Assert.Collection(
            result,
            target => AssertMeasuredOnlyTarget(target, "Alpha", 3.25),
            target => AssertMeasuredOnlyTarget(target, "zeta", 7.5));
    }

    [Fact]
    public void Fuse_ConditionScopedRuleWithoutMeasuredBreakdown_ReturnsInsufficientMeasuredWithoutConflict()
    {
        MeasuredMetric[] measured =
        [
            CreateMeasuredMetric("category_ratio:counter", 12.0, effectiveSampleSize: 9.5),
        ];
        StatedRuleCandidate[] statedRules =
        [
            CreateRule(
                metric: "counter",
                comparator: "gte",
                value: 8,
                condition: "archetype:control")
        ];

        IReadOnlyList<FusedTarget> result = ProfileFusionEngine.Fuse(measured, statedRules);

        FusedTarget row = Assert.Single(result);
        Assert.Equal("counter", row.Metric);
        Assert.Equal("archetype:control", row.Condition);
        Assert.Equal("insufficient-measured", row.Verdict);
        Assert.Equal("no-condition-breakdown", row.VerdictReason);
        Assert.Null(row.Conflict);
        Assert.NotEqual("agree", row.Verdict);
        Assert.Equal(12.0, row.MeasuredValue);
        Assert.Equal(9.5, row.EffectiveSampleSize);
        Assert.True(
            row.EffectiveSampleSize > CreatorStyleProfile.MinDeckFloor,
            "Expected the aggregate measured sample to be above the floor so the insufficient verdict is caused by missing condition breakdown, not low sample.");
    }

    [Fact]
    public void Fuse_ObservableMetric_ResolvesValueToMeasuredAndRetainsStatedBand()
    {
        MeasuredMetric[] measured =
        [
            CreateMeasuredMetric("category_ratio:ramp", 12.0, effectiveSampleSize: 10.5),
        ];
        StatedRuleCandidate[] statedRules =
        [
            CreateRule(
                metric: "ramp",
                comparator: "range",
                valueMin: 7,
                valueMax: 12)
        ];

        IReadOnlyList<FusedTarget> result = ProfileFusionEngine.Fuse(measured, statedRules);

        FusedTarget row = Assert.Single(result);
        Assert.Equal(12.0, row.Value);
        Assert.Equal(12.0, row.MeasuredValue);
        Assert.Equal(7, row.StatedMin);
        Assert.Equal(12, row.StatedMax);
        Assert.Equal("agree", row.Verdict);
        Assert.Equal("measured-weighted", row.Source);
        Assert.Equal(1.0, row.Weight);
    }

    [Fact]
    public void Fuse_PhilosophyMetric_ResolvesValueToStatedAndNeverProducesConflict()
    {
        StatedRuleCandidate[] statedRules =
        [
            CreateRule(
                metric: "power_level_philosophy",
                comparator: "eq",
                value: 2)
        ];

        IReadOnlyList<FusedTarget> result = ProfileFusionEngine.Fuse([], statedRules);

        FusedTarget row = Assert.Single(result);
        Assert.Equal("power_level_philosophy", row.Metric);
        Assert.Equal(2, row.Value);
        Assert.Equal("philosophy-stated-only", row.Verdict);
        Assert.Equal("stated", row.Source);
        Assert.Null(row.MeasuredValue);
        Assert.Null(row.Conflict);
    }

    [Fact]
    public void Fuse_SupersededRuleAppearsAsHistoryAndNotAsActiveTarget()
    {
        MeasuredMetric[] measured =
        [
            CreateMeasuredMetric("category_ratio:draw", 14.0, effectiveSampleSize: 10.5),
        ];
        StatedRuleCandidate older = CreateRule(
            metric: "draw",
            comparator: "range",
            valueMin: 13,
            valueMax: 18,
            sourceClip: "Older draw target.",
            videoDateUtc: "2025-07-05T00:00:00Z");
        StatedRuleCandidate newer = CreateRule(
            metric: "draw",
            comparator: "range",
            valueMin: 12,
            valueMax: 16,
            sourceClip: "New draw target.",
            videoDateUtc: "2026-07-05T00:00:00Z");

        IReadOnlyList<FusedTarget> result = ProfileFusionEngine.Fuse(measured, [older, newer]);

        Assert.Equal(2, result.Count);
        Assert.Collection(
            result,
            active =>
            {
                Assert.Equal("draw", active.Metric);
                Assert.Equal("agree", active.Verdict);
                Assert.Equal("New draw target.", active.SourceClip);
            },
            history =>
            {
                Assert.Equal("draw", history.Metric);
                Assert.Equal("superseded", history.Verdict);
                Assert.Equal("stated-superseded", history.Source);
                Assert.Equal("Older draw target.", history.SourceClip);
                Assert.Null(history.Conflict);
            });
    }

    [Fact]
    public void Fuse_LandCountDerivedValue_UsesTargetLandsPlusLandDelta()
    {
        MeasuredMetric[] measured =
        [
            CreateMeasuredMetric("karsten:target_lands", 37.0, effectiveSampleSize: 10.0),
            CreateMeasuredMetric("karsten:land_delta", 0.4, effectiveSampleSize: 10.0),
        ];
        StatedRuleCandidate[] statedRules =
        [
            CreateRule(
                metric: "land_count",
                comparator: "range",
                valueMin: 37,
                valueMax: 42)
        ];

        IReadOnlyList<FusedTarget> result = ProfileFusionEngine.Fuse(measured, statedRules);

        FusedTarget row = Assert.Single(result);
        Assert.Equal("land_count", row.Metric);
        Assert.Equal(37.4, row.Value, 6);
        Assert.Equal(37.4, Assert.IsType<double>(row.MeasuredValue), 6);
        Assert.Equal("agree", row.Verdict);
    }

    [Fact]
    public void Fuse_IsDeterministicAcrossRepeatedCalls()
    {
        MeasuredMetric[] measured =
        [
            CreateMeasuredMetric("category_ratio:ramp", 12.0, effectiveSampleSize: 10.5),
            CreateMeasuredMetric("karsten:target_lands", 37.0, effectiveSampleSize: 10.0),
            CreateMeasuredMetric("karsten:land_delta", 0.4, effectiveSampleSize: 10.0),
        ];
        StatedRuleCandidate[] statedRules =
        [
            CreateRule(metric: "ramp", comparator: "range", valueMin: 7, valueMax: 12),
            CreateRule(metric: "land_count", comparator: "range", valueMin: 37, valueMax: 42),
            CreateRule(
                metric: "power_level_philosophy",
                comparator: "eq",
                value: 2,
                sourceClip: "No fast mana arms race.")
        ];

        IReadOnlyList<FusedTarget> first = ProfileFusionEngine.Fuse(measured, statedRules);
        IReadOnlyList<FusedTarget> second = ProfileFusionEngine.Fuse(measured, statedRules);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Fuse_SnailPrototypeScenario_ReproducesSayVsDoLedger()
    {
        MeasuredMetric[] measured =
        [
            CreateMeasuredMetric("category_ratio:ramp", 12.0, effectiveSampleSize: 10.5),
            CreateMeasuredMetric("category_ratio:draw", 11.1, effectiveSampleSize: 10.5),
            CreateMeasuredMetric("category_ratio:board-wipe", 1.2, effectiveSampleSize: 10.5),
            CreateMeasuredMetric("category_ratio:counter", 12.0, effectiveSampleSize: 9.5),
            CreateMeasuredMetric("karsten:target_lands", 37.0, effectiveSampleSize: 10.0),
            CreateMeasuredMetric("karsten:land_delta", 0.4, effectiveSampleSize: 10.0),
        ];
        StatedRuleCandidate[] statedRules =
        [
            CreateRule(metric: "land_count", comparator: "range", valueMin: 37, valueMax: 42),
            CreateRule(metric: "ramp", comparator: "range", valueMin: 7, valueMax: 12),
            CreateRule(metric: "draw", comparator: "range", valueMin: 13, valueMax: 18),
            CreateRule(metric: "board-wipe", comparator: "lte", value: 5, valueMin: 3, valueMax: 5),
            CreateRule(metric: "counter", comparator: "gte", value: 8, condition: "archetype:control"),
            CreateRule(metric: "power_level_philosophy", comparator: "eq", value: 2),
        ];

        IReadOnlyList<FusedTarget> result = ProfileFusionEngine.Fuse(measured, statedRules);

        Assert.Equal(6, result.Count);

        FusedTarget land = Assert.Single(result, static row => row.Metric == "land_count");
        Assert.Equal("agree", land.Verdict);
        Assert.Equal(37.4, land.Value, 6);

        FusedTarget ramp = Assert.Single(result, static row => row.Metric == "ramp");
        Assert.Equal("agree", ramp.Verdict);

        FusedTarget draw = Assert.Single(result, static row => row.Metric == "draw");
        Assert.Equal("conflict", draw.Verdict);
        Assert.NotNull(draw.Conflict);

        FusedTarget boardWipe = Assert.Single(result, static row => row.Metric == "board-wipe");
        Assert.Equal("agree", boardWipe.Verdict);
        Assert.NotEqual("conflict", boardWipe.Verdict);

        FusedTarget counters = Assert.Single(result, static row => row.Metric == "counter");
        Assert.Equal("insufficient-measured", counters.Verdict);
        Assert.Equal("no-condition-breakdown", counters.VerdictReason);
        Assert.NotEqual("agree", counters.Verdict);
        Assert.Null(counters.Conflict);
        Assert.True(
            counters.EffectiveSampleSize > CreatorStyleProfile.MinDeckFloor,
            "Expected counter aggregate sample to be above the floor so the rejection is condition-scope, not low-sample.");

        FusedTarget philosophy = Assert.Single(result, static row => row.Metric == "power_level_philosophy");
        Assert.Equal("philosophy-stated-only", philosophy.Verdict);
        Assert.Null(philosophy.Conflict);
    }

    private static MeasuredMetric CreateMeasuredMetric(
        string metric,
        double value,
        double effectiveSampleSize,
        int numDecks = 39)
    {
        return new MeasuredMetric
        {
            Metric = metric,
            Value = value,
            NumDecks = numDecks,
            Distribution = new MetricDistribution
            {
                Mean = value,
                Min = value,
                Max = value,
                StdDev = 0.1,
                EffectiveSampleSize = effectiveSampleSize,
            }
        };
    }

    private static void AssertMeasuredOnlyTarget(FusedTarget target, string metric, double value)
    {
        Assert.Equal(metric, target.Metric);
        Assert.Equal(value, target.Value);
        Assert.Equal(value, target.MeasuredValue);
        Assert.Equal("measured-weighted", target.Source);
        Assert.Equal("measured-only", target.Verdict);
    }

    private static StatedRuleCandidate CreateRule(
        string metric,
        string comparator,
        double? value = null,
        double? valueMin = null,
        double? valueMax = null,
        string? condition = null,
        string sourceClip = "Prototype fixture.",
        string videoDateUtc = "2026-07-05T00:00:00Z")
    {
        return new StatedRuleCandidate
        {
            Category = "deckbuilding",
            Metric = metric,
            Value = value,
            ValueMin = valueMin,
            ValueMax = valueMax,
            Comparator = comparator,
            Condition = condition,
            ClipTimestampSeconds = 42,
            SourceClip = sourceClip,
            Confidence = 0.8,
            CardReference = null,
            CardGrounded = null,
            VideoDateUtc = DateTimeOffset.Parse(videoDateUtc, CultureInfo.InvariantCulture),
        };
    }
}
