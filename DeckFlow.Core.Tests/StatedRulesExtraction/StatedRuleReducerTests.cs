using System.Globalization;
using DeckFlow.Core.Knowledge.StatedRulesExtraction;
using Xunit;

namespace DeckFlow.Core.Tests.StatedRulesExtraction;

public sealed class StatedRuleReducerTests
{
    [Fact]
    public void Reduce_CollapsesDuplicateBucketKeepingHigherConfidence()
    {
        var lower = CreateCandidate(
            metric: "land_count",
            condition: "archetype:control",
            comparator: "gte",
            confidence: 0.65,
            videoDateUtc: "2026-01-01T00:00:00Z");
        var higher = CreateCandidate(
            metric: "land_count",
            condition: "archetype:control",
            comparator: "gte",
            confidence: 0.90,
            videoDateUtc: "2025-12-01T00:00:00Z");

        IReadOnlyList<StatedRuleCandidate> reduced = StatedRuleReducer.Reduce([lower, higher]);

        StatedRuleCandidate survivor = Assert.Single(reduced);
        Assert.Same(higher, survivor);
    }

    [Fact]
    public void Reduce_BreaksConfidenceTiesByNewerVideoDate()
    {
        var older = CreateCandidate(
            metric: "interaction",
            condition: "curve:low",
            comparator: "eq",
            confidence: 0.75,
            videoDateUtc: "2025-01-01T00:00:00Z");
        var newer = CreateCandidate(
            metric: "interaction",
            condition: "curve:low",
            comparator: "eq",
            confidence: 0.75,
            videoDateUtc: "2026-02-03T00:00:00Z");

        IReadOnlyList<StatedRuleCandidate> reduced = StatedRuleReducer.Reduce([older, newer]);

        StatedRuleCandidate survivor = Assert.Single(reduced);
        Assert.Same(newer, survivor);
    }

    [Fact]
    public void Reduce_TreatsNullAndEmptyConditionAsSameBucket()
    {
        var nullCondition = CreateCandidate(
            metric: "opener_probability",
            condition: null,
            comparator: "gte",
            confidence: 0.50,
            videoDateUtc: "2025-01-01T00:00:00Z");
        var emptyCondition = CreateCandidate(
            metric: "opener_probability",
            condition: string.Empty,
            comparator: "gte",
            confidence: 0.80,
            videoDateUtc: "2025-02-01T00:00:00Z");

        IReadOnlyList<StatedRuleCandidate> reduced = StatedRuleReducer.Reduce([nullCondition, emptyCondition]);

        StatedRuleCandidate survivor = Assert.Single(reduced);
        Assert.Same(emptyCondition, survivor);
    }

    [Fact]
    public void Reduce_DoesNotMergeDifferingMetric()
    {
        var first = CreateCandidate(
            metric: "land_count",
            condition: "archetype:control",
            comparator: "gte",
            confidence: 0.70,
            videoDateUtc: "2025-01-01T00:00:00Z");
        var second = CreateCandidate(
            metric: "interaction",
            condition: "archetype:control",
            comparator: "gte",
            confidence: 0.90,
            videoDateUtc: "2025-02-01T00:00:00Z");

        IReadOnlyList<StatedRuleCandidate> reduced = StatedRuleReducer.Reduce([first, second]);

        Assert.Equal(2, reduced.Count);
        Assert.Same(first, reduced[0]);
        Assert.Same(second, reduced[1]);
    }

    [Fact]
    public void Reduce_WithChunkEvidence_RejectsHallucinatedRuleNotTraceableToChunkEvidence()
    {
        // Why (WR-17): the LLM reduce pass may only merge existing chunk rules; a rule with a
        // vocabulary-valid metric/comparator but a (Metric, Condition, Comparator) triple that
        // never appeared in the chunk evidence is a hallucination and must be dropped, not
        // survive into CreatorStyleProfile.StatedRules with full provenance fields.
        var evidenceRule = CreateCandidate(
            metric: "land_count",
            condition: "archetype:control",
            comparator: "gte",
            confidence: 0.70,
            videoDateUtc: "2025-01-01T00:00:00Z");
        var hallucinated = CreateCandidate(
            metric: "interaction",
            condition: "archetype:control",
            comparator: "gte",
            confidence: 0.95,
            videoDateUtc: "2025-02-01T00:00:00Z");

        IReadOnlyList<StatedRuleCandidate> reduced = StatedRuleReducer.Reduce(
            candidates: [evidenceRule, hallucinated],
            chunkEvidence: [evidenceRule]);

        StatedRuleCandidate survivor = Assert.Single(reduced);
        Assert.Same(evidenceRule, survivor);
    }

    [Fact]
    public void Reduce_WithChunkEvidence_KeepsRuleTraceableToChunkEvidenceAndStillDedupes()
    {
        var chunkOriginal = CreateCandidate(
            metric: "land_count",
            condition: "archetype:control",
            comparator: "gte",
            confidence: 0.65,
            videoDateUtc: "2026-01-01T00:00:00Z");
        var reducedMerge = CreateCandidate(
            metric: "land_count",
            condition: "archetype:control",
            comparator: "gte",
            confidence: 0.90,
            videoDateUtc: "2025-12-01T00:00:00Z");

        IReadOnlyList<StatedRuleCandidate> reduced = StatedRuleReducer.Reduce(
            candidates: [chunkOriginal, reducedMerge],
            chunkEvidence: [chunkOriginal]);

        StatedRuleCandidate survivor = Assert.Single(reduced);
        Assert.Same(reducedMerge, survivor);
    }

    private static StatedRuleCandidate CreateCandidate(
        string metric,
        string? condition,
        string comparator,
        double confidence,
        string videoDateUtc)
    {
        return new StatedRuleCandidate
        {
            Category = "deckbuilding",
            Metric = metric,
            Value = 10,
            Comparator = comparator,
            Condition = condition,
            ClipTimestampSeconds = 42,
            SourceClip = "Rule excerpt.",
            Confidence = confidence,
            CardReference = null,
            CardGrounded = null,
            VideoDateUtc = DateTimeOffset.Parse(videoDateUtc, CultureInfo.InvariantCulture),
        };
    }
}
