using DeckFlow.Core.Knowledge;
using Xunit;

namespace DeckFlow.Core.Tests;

internal static class CreatorStyleProfileTestData
{
    internal static readonly DateTimeOffset FullProfileUpdatedUtc = DateTimeOffset.Parse("2026-07-11T12:34:56Z");

    internal static CreatorStyleProfile CreateFullProfile(string slug)
        => new()
        {
            Slug = slug,
            Platform = "youtube",
            MinDecks = CreatorStyleProfile.MinDeckFloor + 2,
            InsufficientSample = false,
            StatedRules = new[]
            {
                new StatedRule
                {
                    Category = "curve",
                    TargetMetric = "avg_cmc",
                    TargetValue = 2.3,
                    Comparator = "<=",
                    SourceClip = "Keep the curve low.",
                    Confidence = 0.87,
                    VideoDateUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                }
            },
            MeasuredMetrics = new[]
            {
                new MeasuredMetric
                {
                    Metric = "lands",
                    Value = 35.4,
                    NumDecks = 7,
                    Distribution = new MetricDistribution
                    {
                        Mean = 35.4,
                        Min = 33.0,
                        Max = 37.0,
                        StdDev = 1.2,
                        EffectiveSampleSize = 8.5
                    }
                }
            },
            FusedTargets = new[]
            {
                new FusedTarget
                {
                    Metric = "interaction",
                    Value = 11.5,
                    Weight = 0.65,
                    Source = "fused",
                    Conflict = new FusedConflict
                    {
                        StatedValue = 12.0,
                        MeasuredValue = 11.0,
                        Delta = 1.0
                    }
                }
            },
            UpdatedUtc = FullProfileUpdatedUtc
        };

    internal static FusedTarget CreateFullyPopulatedFusedTarget()
        => new()
        {
            Metric = "draw",
            Value = 11.1,
            Weight = 0.8,
            Source = "measured",
            Condition = "archetype:control",
            StatedMin = 13.0,
            StatedMax = 18.0,
            MeasuredValue = 11.1,
            NumDecks = 39,
            EffectiveSampleSize = 10.5,
            Verdict = "conflict",
            VerdictReason = "no-condition-breakdown",
            SourceClip = "I want 13 to 18 draw spells in control shells.",
            VideoDateUtc = DateTimeOffset.Parse("2026-07-05T00:00:00Z"),
            Confidence = "high",
            Conflict = new FusedConflict
            {
                StatedValue = 15.5,
                MeasuredValue = 11.1,
                Delta = -4.4,
                BandRelativePercent = 0.2821,
                Winner = "measured"
            }
        };

    internal static void AssertProfilesEqual(CreatorStyleProfile expected, CreatorStyleProfile actual)
    {
        Assert.Equal(expected.Slug, actual.Slug);
        Assert.Equal(expected.Platform, actual.Platform);
        Assert.Equal(expected.MinDecks, actual.MinDecks);
        Assert.Equal(expected.InsufficientSample, actual.InsufficientSample);
        Assert.Single(actual.StatedRules);
        Assert.Equal(expected.StatedRules[0], actual.StatedRules[0]);
        Assert.Single(actual.MeasuredMetrics);
        Assert.Equal(expected.MeasuredMetrics[0], actual.MeasuredMetrics[0]);
        Assert.Equal(
            expected.MeasuredMetrics[0].Distribution?.EffectiveSampleSize,
            actual.MeasuredMetrics[0].Distribution?.EffectiveSampleSize);
        Assert.Single(actual.FusedTargets);
        Assert.Equal(expected.FusedTargets[0], actual.FusedTargets[0]);
        AssertCloseTo(expected.UpdatedUtc, actual.UpdatedUtc);
    }

    internal static void AssertCloseTo(DateTimeOffset expected, DateTimeOffset actual)
    {
        var delta = (expected - actual).Duration();
        Assert.True(
            delta <= TimeSpan.FromSeconds(1),
            $"Expected UpdatedUtc within 1 second. Expected {expected:o}, actual {actual:o}.");
    }
}
