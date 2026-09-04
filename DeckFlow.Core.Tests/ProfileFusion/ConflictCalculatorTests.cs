using System.Globalization;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.ProfileFusion;
using DeckFlow.Core.Knowledge.StatedRulesExtraction;
using Xunit;

namespace DeckFlow.Core.Tests.ProfileFusion;

public sealed class ConflictCalculatorTests
{
    [Fact]
    public void Evaluate_LandGoldenVerdict_AgreesWhenMeasuredValueFallsInsideRange()
    {
        StatedRuleCandidate rule = CreateRule(
            metric: "karsten:target_lands",
            comparator: "range",
            valueMin: 37,
            valueMax: 42);

        ConflictCalculationResult result = ConflictCalculator.Evaluate(rule, measuredValue: 37.4, effectiveSampleSize: 39);

        Assert.Equal("agree", result.Verdict);
        Assert.Null(result.VerdictReason);
        Assert.Null(result.Conflict);
        Assert.Equal("measured", result.Winner);
    }

    [Fact]
    public void Evaluate_RampGoldenVerdict_AgreesOnInclusiveUpperEdge()
    {
        StatedRuleCandidate rule = CreateRule(
            metric: "ramp",
            comparator: "range",
            valueMin: 7,
            valueMax: 12);

        ConflictCalculationResult result = ConflictCalculator.Evaluate(rule, measuredValue: 12.0, effectiveSampleSize: 39);

        Assert.Equal("agree", result.Verdict);
        Assert.Null(result.Conflict);
    }

    [Fact]
    public void Evaluate_DrawGoldenVerdict_FiresConflictWhenMeasuredValueFallsWellBelowRange()
    {
        StatedRuleCandidate rule = CreateRule(
            metric: "draw",
            comparator: "range",
            valueMin: 13,
            valueMax: 18);

        ConflictCalculationResult result = ConflictCalculator.Evaluate(rule, measuredValue: 11.1, effectiveSampleSize: 39);

        Assert.Equal("conflict", result.Verdict);
        FusedConflict conflict = Assert.IsType<FusedConflict>(result.Conflict);
        Assert.Equal(13, conflict.StatedValue);
        Assert.Equal(11.1, conflict.MeasuredValue, 6);
        Assert.Equal(-1.9, conflict.Delta, 6);
        Assert.Equal(1.9 / 13.0, Assert.IsType<double>(conflict.BandRelativePercent), 6);
        Assert.Equal("measured", conflict.Winner);
        Assert.Equal("measured", result.Winner);
    }

    [Fact]
    public void Evaluate_BoardWipeGoldenVerdict_AgreesWhenMeasuredValueUndershootsUpperBoundRule()
    {
        StatedRuleCandidate rule = CreateRule(
            metric: "board-wipe",
            comparator: "lte",
            value: 5,
            valueMin: 3,
            valueMax: 5);

        ConflictCalculationResult result = ConflictCalculator.Evaluate(rule, measuredValue: 1.2, effectiveSampleSize: 39);

        Assert.Equal("agree", result.Verdict);
        Assert.Null(result.Conflict);
    }

    [Fact]
    public void Evaluate_CountersGoldenVerdict_AgreesWhenMeasuredValueExceedsGteFloor()
    {
        StatedRuleCandidate rule = CreateRule(
            metric: "counter",
            comparator: "gte",
            value: 8,
            condition: "archetype:control");

        ConflictCalculationResult result = ConflictCalculator.Evaluate(rule, measuredValue: 12.0, effectiveSampleSize: 39);

        Assert.Equal("agree", result.Verdict);
        Assert.Null(result.Conflict);
    }

    [Fact]
    public void Evaluate_ReturnsInsufficientMeasuredWhenCoverageFallsBelowMinDeckFloor()
    {
        StatedRuleCandidate rule = CreateRule(
            metric: "draw",
            comparator: "range",
            valueMin: 13,
            valueMax: 18);

        ConflictCalculationResult result = ConflictCalculator.Evaluate(
            rule,
            measuredValue: 11.1,
            effectiveSampleSize: CreatorStyleProfile.MinDeckFloor - 0.01);

        Assert.Equal("insufficient-measured", result.Verdict);
        Assert.Equal("low-sample", result.VerdictReason);
        Assert.Null(result.Conflict);
        Assert.Equal("measured", result.Winner);
    }

    [Fact]
    public void Evaluate_UsesThresholdBoundaryAsStrictGreaterThan()
    {
        StatedRuleCandidate rule = CreateRule(
            metric: "draw",
            comparator: "range",
            valueMin: 13,
            valueMax: 18);

        ConflictCalculationResult justInside = ConflictCalculator.Evaluate(rule, measuredValue: 11.71, effectiveSampleSize: 39);
        ConflictCalculationResult justOutside = ConflictCalculator.Evaluate(rule, measuredValue: 11.69, effectiveSampleSize: 39);

        Assert.Equal("agree", justInside.Verdict);
        Assert.Null(justInside.Conflict);
        Assert.Equal("conflict", justOutside.Verdict);
        Assert.NotNull(justOutside.Conflict);
    }

    [Fact]
    public void Evaluate_NearZeroBandEdge_UsesFiniteReasonableRelativePercent()
    {
        StatedRuleCandidate rule = CreateRule(
            metric: "near-zero",
            comparator: "gte",
            value: 1e-300);

        ConflictCalculationResult result = ConflictCalculator.Evaluate(rule, measuredValue: -1, effectiveSampleSize: 39);

        FusedConflict conflict = Assert.IsType<FusedConflict>(result.Conflict);
        double bandRelativePercent = Assert.IsType<double>(conflict.BandRelativePercent);
        Assert.True(double.IsFinite(bandRelativePercent));
        Assert.InRange(bandRelativePercent, 0, 2);
    }

    [Fact]
    public void Evaluate_MalformedRangeWithoutBounds_ReturnsInsufficientMeasured()
    {
        StatedRuleCandidate rule = CreateRule(
            metric: "malformed-range",
            comparator: "range");

        ConflictCalculationResult result = ConflictCalculator.Evaluate(rule, measuredValue: 2, effectiveSampleSize: 39);

        Assert.Equal("insufficient-measured", result.Verdict);
        Assert.Equal("malformed-band", result.VerdictReason);
        Assert.Null(result.Conflict);
        Assert.Equal("measured", result.Winner);
    }

    private static StatedRuleCandidate CreateRule(
        string metric,
        string comparator,
        double? value = null,
        double? valueMin = null,
        double? valueMax = null,
        string? condition = null)
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
            SourceClip = "Prototype fixture.",
            Confidence = 0.8,
            CardReference = null,
            CardGrounded = null,
            VideoDateUtc = DateTimeOffset.Parse("2026-07-05T00:00:00Z", CultureInfo.InvariantCulture),
        };
    }
}
