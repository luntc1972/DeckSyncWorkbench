using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.CreatorStyleRubric;
using Xunit;

namespace DeckFlow.Core.Tests;

public sealed class CreatorStyleRubricScorerTests
{
    [Fact]
    public void Score_RampMetricBridgesToMeasuredCategoryRatioKey()
    {
        FusedTarget[] creatorTargets =
        [
            CreateTarget("ramp", 12, confidence: "high"),
        ];
        SubmittedDeckStats submittedStats = CreateSubmittedDeckStats(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["category_ratio:ramp"] = 8,
            });

        RubricScoreResult result = CreatorStyleRubricScorer.Score("snail", creatorTargets, submittedStats);

        RubricMetricScore score = Assert.Single(result.MetricScores);
        Assert.Equal("category_ratio:ramp", score.Metric);
        Assert.Equal(12, score.TargetValue);
        Assert.Equal(8, score.SubmittedValue);
        Assert.Equal(-4, score.Delta);
        Assert.Equal("under", score.Verdict);
        Assert.Equal("high", score.Confidence);
    }

    [Fact]
    public void Score_KarstenMetric_UsesSelfMappedMeasuredKey()
    {
        FusedTarget[] creatorTargets =
        [
            CreateTarget("karsten:target_lands", 37, confidence: "med"),
        ];
        SubmittedDeckStats submittedStats = CreateSubmittedDeckStats(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["karsten:target_lands"] = 38,
            });

        RubricScoreResult result = CreatorStyleRubricScorer.Score("snail", creatorTargets, submittedStats);

        RubricMetricScore score = Assert.Single(result.MetricScores);
        Assert.Equal("karsten:target_lands", score.Metric);
        Assert.Equal(1, score.Delta);
        Assert.Equal("over", score.Verdict);
        Assert.Equal("med", score.Confidence);
    }

    [Fact]
    public void Score_FullyMappedFixtureProfile_ProducesNoInsufficientMeasuredRows()
    {
        FusedTarget[] creatorTargets = ContentTagVocabulary.CardCategories
            .Select((metric, index) => CreateTarget(metric, index + 1, confidence: "low"))
            .Concat(
            [
                CreateTarget("karsten:target_lands", 37),
                CreateTarget("karsten:land_delta", 0.5),
                CreateTarget("karsten:health_score", 0.9),
                CreateTarget("combo_density:included_per_deck", 1.4),
            ])
            .Reverse()
            .ToArray();

        var submittedMetrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int categoryIndex = 1;
        foreach (string category in ContentTagVocabulary.CardCategories)
        {
            submittedMetrics[$"category_ratio:{category}"] = categoryIndex++;
        }

        submittedMetrics["karsten:target_lands"] = 37;
        submittedMetrics["karsten:land_delta"] = 0.5;
        submittedMetrics["karsten:health_score"] = 0.9;
        submittedMetrics["combo_density:included_per_deck"] = 1.4;

        SubmittedDeckStats submittedStats = CreateSubmittedDeckStats(submittedMetrics);

        RubricScoreResult result = CreatorStyleRubricScorer.Score("snail", creatorTargets, submittedStats);

        Assert.Equal(15, result.MetricScores.Count);
        Assert.DoesNotContain(result.MetricScores, static score => score.Verdict == "insufficient-measured");
    }

    [Fact]
    public void Score_StatedOnlyMetric_EmitsInsufficientMeasured()
    {
        FusedTarget[] creatorTargets =
        [
            CreateTarget("land_count", 38),
        ];

        RubricScoreResult result = CreatorStyleRubricScorer.Score("snail", creatorTargets, CreateSubmittedDeckStats(new Dictionary<string, double>()));

        RubricMetricScore score = Assert.Single(result.MetricScores);
        Assert.Equal("land_count", score.Metric);
        Assert.Null(score.SubmittedValue);
        Assert.Null(score.Delta);
        Assert.Equal("insufficient-measured", score.Verdict);
    }

    [Fact]
    public void Score_ConditionalTarget_EmitsConditionalUnscored()
    {
        FusedTarget[] creatorTargets =
        [
            CreateTarget("ramp", 12, condition: "when commander costs five or more"),
        ];
        SubmittedDeckStats submittedStats = CreateSubmittedDeckStats(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["category_ratio:ramp"] = 12,
            });

        RubricScoreResult result = CreatorStyleRubricScorer.Score("snail", creatorTargets, submittedStats);

        RubricMetricScore score = Assert.Single(result.MetricScores);
        Assert.Equal("category_ratio:ramp", score.Metric);
        Assert.Null(score.SubmittedValue);
        Assert.Null(score.Delta);
        Assert.Equal("conditional-unscored", score.Verdict);
    }

    [Fact]
    public void Score_LandCount_DerivesSubmittedValueFromKarstenMetrics()
    {
        FusedTarget[] creatorTargets =
        [
            CreateTarget("land_count", 38),
        ];
        SubmittedDeckStats submittedStats = CreateSubmittedDeckStats(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["karsten:target_lands"] = 37,
                ["karsten:land_delta"] = 1,
            });

        RubricScoreResult result = CreatorStyleRubricScorer.Score("snail", creatorTargets, submittedStats);

        RubricMetricScore score = Assert.Single(result.MetricScores);
        Assert.Equal("land_count", score.Metric);
        Assert.Equal(38, score.SubmittedValue);
        Assert.Equal(0, score.Delta);
        Assert.Equal("on-target", score.Verdict);
    }

    [Fact]
    public void Score_MissingSubmittedMetric_EmitsInsufficientMeasured()
    {
        FusedTarget[] creatorTargets =
        [
            CreateTarget("draw", 14),
        ];

        RubricScoreResult result = CreatorStyleRubricScorer.Score("snail", creatorTargets, CreateSubmittedDeckStats(new Dictionary<string, double>()));

        RubricMetricScore score = Assert.Single(result.MetricScores);
        Assert.Equal("category_ratio:draw", score.Metric);
        Assert.Null(score.SubmittedValue);
        Assert.Null(score.Delta);
        Assert.Equal("insufficient-measured", score.Verdict);
    }

    [Fact]
    public void Score_UnorderedInputs_ReturnsMetricScoresOrderedByMetricOrdinal()
    {
        FusedTarget[] creatorTargets =
        [
            CreateTarget("ramp", 10),
            CreateTarget("karsten:target_lands", 37),
            CreateTarget("draw", 12),
        ];
        SubmittedDeckStats submittedStats = CreateSubmittedDeckStats(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["karsten:target_lands"] = 37,
                ["category_ratio:draw"] = 12,
                ["category_ratio:ramp"] = 10,
            });

        RubricScoreResult result = CreatorStyleRubricScorer.Score("snail", creatorTargets, submittedStats);

        Assert.Equal(
            [
                "category_ratio:draw",
                "category_ratio:ramp",
                "karsten:target_lands",
            ],
            result.MetricScores.Select(static score => score.Metric).ToArray());
    }

    [Fact]
    public void Score_SubEpsilonDelta_ReturnsOnTarget()
    {
        FusedTarget[] creatorTargets =
        [
            CreateTarget("ramp", 10),
            CreateTarget("draw", 10),
            CreateTarget("karsten:target_lands", 10),
        ];
        SubmittedDeckStats submittedStats = CreateSubmittedDeckStats(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["category_ratio:ramp"] = 10.000001,
                ["category_ratio:draw"] = 9.999,
                ["karsten:target_lands"] = 10.001,
            });

        RubricScoreResult result = CreatorStyleRubricScorer.Score("snail", creatorTargets, submittedStats);

        Assert.Equal(
            ["under", "on-target", "over"],
            result.MetricScores.Select(static score => score.Verdict).ToArray());
    }

    [Fact]
    public void Score_NullArguments_ThrowsArgumentNullException()
    {
        SubmittedDeckStats submittedStats = CreateSubmittedDeckStats(new Dictionary<string, double>());
        FusedTarget[] creatorTargets =
        [
            CreateTarget("ramp", 10),
        ];

        Assert.Throws<ArgumentNullException>(() => CreatorStyleRubricScorer.Score(null!, creatorTargets, submittedStats));
        Assert.Throws<ArgumentNullException>(() => CreatorStyleRubricScorer.Score("snail", null!, submittedStats));
        Assert.Throws<ArgumentNullException>(() => CreatorStyleRubricScorer.Score("snail", creatorTargets, null!));
    }

    [Fact]
    public void Score_WhitespaceCreatorSlug_ThrowsArgumentException()
    {
        SubmittedDeckStats submittedStats = CreateSubmittedDeckStats(new Dictionary<string, double>());

        Assert.Throws<ArgumentException>(() => CreatorStyleRubricScorer.Score(" ", [CreateTarget("ramp", 10)], submittedStats));
    }

    private static FusedTarget CreateTarget(
        string metric,
        double value,
        double weight = 1.0,
        string? confidence = null,
        string? condition = null)
        => new()
        {
            Metric = metric,
            Value = value,
            Weight = weight,
            Source = "fixture",
            Confidence = confidence,
            Condition = condition,
        };

    private static SubmittedDeckStats CreateSubmittedDeckStats(IReadOnlyDictionary<string, double> metrics)
        => new()
        {
            Metrics = metrics,
            DeckSize = 100,
            CommanderCount = 1,
        };
}
