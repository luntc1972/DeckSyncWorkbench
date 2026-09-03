namespace DeckFlow.Core.Knowledge.StatedRulesExtraction;

/// <summary>
/// Pure helper for deterministic cross-chunk stated-rule deduplication.
/// </summary>
public static class StatedRuleReducer
{
    /// <summary>
    /// Collapses duplicate stated-rule candidates into a deterministic survivor set.
    /// </summary>
    /// <param name="candidates">Candidates to reduce.</param>
    /// <returns>A new list containing one survivor per dedupe bucket.</returns>
    public static IReadOnlyList<StatedRuleCandidate> Reduce(IReadOnlyList<StatedRuleCandidate> candidates)
        => Reduce(candidates, candidates);

    /// <summary>
    /// Collapses duplicate stated-rule candidates while retaining only rules supported by chunk evidence.
    /// </summary>
    /// <param name="candidates">Candidates emitted by the LLM reduce pass.</param>
    /// <param name="chunkEvidence">Rules decomposed from individual transcript chunks.</param>
    /// <returns>A new list containing supported survivors, one per dedupe bucket.</returns>
    public static IReadOnlyList<StatedRuleCandidate> Reduce(
        IReadOnlyList<StatedRuleCandidate> candidates,
        IReadOnlyList<StatedRuleCandidate> chunkEvidence)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(chunkEvidence);

        if (candidates.Count == 0)
        {
            return [];
        }

        var buckets = new Dictionary<StatedRuleReducerKey, (StatedRuleCandidate Candidate, int Index)>();

        for (int index = 0; index < candidates.Count; index++)
        {
            StatedRuleCandidate candidate = candidates[index];
            var key = new StatedRuleReducerKey(
                candidate.Metric,
                candidate.Condition ?? string.Empty,
                candidate.Comparator);

            if (!buckets.TryGetValue(key, out var current) || ShouldReplace(current.Candidate, candidate))
            {
                // Why: DeckFlow adds this reduce step because Claimify itself has no cross-sentence merge; deduping on (metric, condition, comparator) collapses repeated advice without merging genuinely different rules.
                buckets[key] = (candidate, index);
            }
        }

        var evidenceKeys = chunkEvidence
            .Select(static candidate => new StatedRuleReducerKey(
                candidate.Metric,
                candidate.Condition ?? string.Empty,
                candidate.Comparator))
            .ToHashSet();

        return buckets
            .Where(pair => evidenceKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Value.Index)
            .Select(pair => pair.Value.Candidate)
            .ToList();
    }

    private static bool ShouldReplace(StatedRuleCandidate current, StatedRuleCandidate challenger)
    {
        if (challenger.Confidence > current.Confidence)
        {
            return true;
        }

        if (challenger.Confidence < current.Confidence)
        {
            return false;
        }

        return challenger.VideoDateUtc > current.VideoDateUtc;
    }
}

internal sealed record StatedRuleReducerKey(
    string Metric,
    string Condition,
    string Comparator);
