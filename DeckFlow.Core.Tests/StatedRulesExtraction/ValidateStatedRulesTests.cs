using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.StatedRulesExtraction;
using Xunit;

namespace DeckFlow.Core.Tests.StatedRulesExtraction;

public sealed class ValidateStatedRulesTests
{
    [Fact]
    public void ValidateStatedRules_AcceptsValidRangeRule()
    {
        DistillationValidation.ValidateStatedRules(
            [CreateValidRule(comparator: "range", value: null, valueMin: 37, valueMax: 39)]);
    }

    [Fact]
    public void ValidateStatedRules_ThrowsWhenRangeBoundsAreReversed()
    {
        var rule = CreateValidRule(comparator: "range", value: null, valueMin: 40, valueMax: 37);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DistillationValidation.ValidateStatedRules([rule]));

        Assert.Contains("Range stated rules require", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStatedRules_ThrowsWhenSingleValueComparatorHasNullValue()
    {
        var rule = CreateValidRule(comparator: "gte", value: null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DistillationValidation.ValidateStatedRules([rule]));

        Assert.Contains("Non-range stated rules require", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStatedRules_ThrowsWhenMetricIsUnknown()
    {
        var rule = CreateValidRule(metric: "unknown_metric");

        var ex = Assert.Throws<InvalidOperationException>(
            () => DistillationValidation.ValidateStatedRules([rule]));

        Assert.Contains("metric", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStatedRules_ThrowsWhenComparatorIsUnknown()
    {
        var rule = CreateValidRule(comparator: "gt");

        var ex = Assert.Throws<InvalidOperationException>(
            () => DistillationValidation.ValidateStatedRules([rule]));

        Assert.Contains("comparator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStatedRules_ThrowsWhenConfidenceIsOutOfRange()
    {
        var rule = CreateValidRule(confidence: 1.5);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DistillationValidation.ValidateStatedRules([rule]));

        Assert.Contains("confidence", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStatedRules_ThrowsWhenSourceClipIsEmpty()
    {
        var rule = CreateValidRule(sourceClip: " ");

        var ex = Assert.Throws<InvalidOperationException>(
            () => DistillationValidation.ValidateStatedRules([rule]));

        Assert.Contains("source_clip", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStatedRules_ThrowsWhenVideoDateIsDefault()
    {
        var rule = CreateValidRule(videoDateUtc: DateTimeOffset.MinValue);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DistillationValidation.ValidateStatedRules([rule]));

        Assert.Contains("video_date", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStatedRules_AcceptsNullOrPopulatedCardReference()
    {
        var rules = new[]
        {
            CreateValidRule(cardReference: "Sol Ring"),
            CreateValidRule(metric: "draw", cardReference: null),
        };

        DistillationValidation.ValidateStatedRules(rules);
    }

    [Fact]
    public void SanitizeStatedRules_DropsBrokenRows_MapsCardReference_AndCapsOutput()
    {
        var payloadRules = new List<StatedRulePayload>
        {
            new(
                "mana",
                "ramp",
                10,
                null,
                null,
                "gte",
                null,
                42,
                "Play at least ten ramp pieces.",
                0.8,
                "Sol Ring"),
            new(
                "mana",
                "draw",
                8,
                null,
                null,
                "gte",
                null,
                60,
                "Draw eight cards.",
                0.7),
            new(
                "mana",
                "not-a-metric",
                5,
                null,
                null,
                "gte",
                null,
                75,
                "Broken row should drop.",
                0.5),
        };

        for (var i = 0; i < DistillationValidation.MaxStatedRulesPerVideo + 5; i++)
        {
            payloadRules.Add(
                new StatedRulePayload(
                    "mana",
                    "ramp",
                    6 + i,
                    null,
                    null,
                    "gte",
                    null,
                    90 + i,
                    $"Ramp rule {i}",
                    0.6));
        }

        var videoDateUtc = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        var sanitized = DistillationValidation.SanitizeStatedRules(new RulesPayload(payloadRules), videoDateUtc);

        Assert.Equal(DistillationValidation.MaxStatedRulesPerVideo, sanitized.Count);
        Assert.All(sanitized, rule => Assert.Equal(videoDateUtc, rule.VideoDateUtc));
        Assert.All(sanitized, rule => Assert.Null(rule.CardGrounded));
        Assert.Equal("Sol Ring", sanitized[0].CardReference);
        Assert.Null(sanitized[1].CardReference);
        Assert.DoesNotContain(sanitized, rule => string.Equals(rule.Metric, "not-a-metric", StringComparison.Ordinal));
    }

    [Fact]
    public void SanitizeStatedRules_DropsNullMetric()
    {
        var payload = new RulesPayload([new StatedRulePayload("mana", null!, 10, null, null, "gte", null, 42, "Ramp.", 0.8)]);

        var sanitized = DistillationValidation.SanitizeStatedRules(payload, DateTimeOffset.UtcNow);

        Assert.Empty(sanitized);
    }

    [Fact]
    public void ValidateStatedRules_RejectsNullMetric()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => DistillationValidation.ValidateStatedRules([CreateValidRule(metric: null!)]));

        Assert.Contains("not in the stated rule vocabulary", exception.Message, StringComparison.Ordinal);
    }

    private static StatedRuleCandidate CreateValidRule(
        string category = "mana",
        string metric = "ramp",
        double? value = 10,
        double? valueMin = null,
        double? valueMax = null,
        string comparator = "gte",
        string? sourceClip = "Play at least ten ramp pieces.",
        double confidence = 0.8,
        string? cardReference = null,
        DateTimeOffset? videoDateUtc = null)
    {
        return new StatedRuleCandidate
        {
            Category = category,
            Metric = metric,
            Value = value,
            ValueMin = valueMin,
            ValueMax = valueMax,
            Comparator = comparator,
            Condition = null,
            ClipTimestampSeconds = 42,
            SourceClip = sourceClip!,
            Confidence = confidence,
            CardReference = cardReference,
            CardGrounded = null,
            VideoDateUtc = videoDateUtc ?? new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero),
        };
    }
}
