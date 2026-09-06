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
    // Why (WR-09, maintainer decision 2026-09-06 per WAITING.json): no production caller uses this
    // single-arg overload - StatedRulesExtractor.ExtractAsync always has separate chunk-evidence to
    // pass and calls the two-arg overload below. Kept as the simpler single-source convenience
    // overload StatedRuleReducerTests.cs already exercises, rather than deleted; see
    // ai-context-deckflow/repos/deckflow/notes/2026-09-06-cycle20-branch-divergence.md.
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
            var key = StatedRuleReducerKey.From(candidate);

            if (!buckets.TryGetValue(key, out var current) || ShouldReplace(current.Candidate, candidate))
            {
                // Why: DeckFlow adds this reduce step because Claimify itself has no cross-sentence merge; deduping on (metric, condition, comparator) collapses repeated advice without merging genuinely different rules.
                buckets[key] = (candidate, index);
            }
        }

        var evidenceKeys = chunkEvidence
            .Select(StatedRuleReducerKey.From)
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
    string Comparator)
{
    // Why (WR-10): Metric/Comparator are only ever validated case-insensitively (the vocabulary
    // sets are StringComparer.OrdinalIgnoreCase) and never canonicalized, so this dedupe key must
    // fold case too or two spellings of the same rule survive as distinct buckets.
    public static StatedRuleReducerKey From(StatedRuleCandidate candidate) => new(
        candidate.Metric.ToLowerInvariant(),
        (candidate.Condition ?? string.Empty).ToLowerInvariant(),
        candidate.Comparator.ToLowerInvariant());
}
