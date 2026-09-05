using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;

namespace DeckFlow.Core.Knowledge.MeasuredStyleExtraction;

/// <summary>
/// Pure helper for oversize filtering, near-precon detection, and staple stripping.
/// </summary>
public static class StapleStripper
{
    internal const int MaxDeckSize = 105;
    private const double NearPreconJaccardThreshold = 0.70; // Why: CONTEXT D-03 / RESEARCH Pitfall 5 left the near-precon cut to Claude's discretion, so this plan documents a conservative >0.70 overlap rule instead of pretending the number was locked.

    /// <summary>
    /// Drops samples whose declared card count exceeds the contamination guardrail.
    /// </summary>
    /// <param name="samples">Creator deck samples to evaluate.</param>
    /// <returns>Samples with <c>CardCount &lt;= 105</c>.</returns>
    public static IReadOnlyList<CreatorDeckSample> FilterOversized(IReadOnlyList<CreatorDeckSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        return samples
            .Where(sample => sample.CardCount <= MaxDeckSize)
            .ToList();
    }

    /// <summary>
    /// Marks later decks as near-precon duplicates when their Jaccard card overlap exceeds the configured threshold.
    /// </summary>
    /// <param name="samples">Creator deck samples to compare pairwise.</param>
    /// <returns>Samples with later duplicates re-marked as <c>near-precon</c>.</returns>
    public static IReadOnlyList<CreatorDeckSample> FlagNearPrecons(IReadOnlyList<CreatorDeckSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var flagged = new List<CreatorDeckSample>(samples.Count);
        var cardSets = new List<HashSet<string>>(samples.Count);

        foreach (var sample in samples)
        {
            var currentSet = BuildCardNameSet(sample.Entries);
            var confidenceMarker = sample.ConfidenceMarker;

            foreach (var priorSet in cardSets)
            {
                if (ComputeJaccard(currentSet, priorSet) > NearPreconJaccardThreshold)
                {
                    confidenceMarker = "near-precon";
                    break;
                }
            }

            flagged.Add(sample with { ConfidenceMarker = confidenceMarker });
            cardSets.Add(currentSet);
        }

        return flagged;
    }

    /// <summary>
    /// Computes card names that appear in strictly more than the supplied fraction of decks.
    /// </summary>
    /// <param name="samples">Creator deck samples already filtered for oversize contamination.</param>
    /// <param name="frequencyThreshold">Presence threshold expressed as a deck fraction.</param>
    /// <returns>Case-insensitive personal staple set.</returns>
    public static IReadOnlySet<string> ComputePersonalStaples(
        IReadOnlyList<CreatorDeckSample> samples,
        double frequencyThreshold = 0.60)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var appearances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in samples)
        {
            foreach (var cardName in BuildCardNameSet(sample.Entries))
            {
                appearances[cardName] = appearances.TryGetValue(cardName, out var count) ? count + 1 : 1;
            }
        }

        var minimumCount = samples.Count * frequencyThreshold;

        return new HashSet<string>(
            appearances
                .Where(pair => pair.Value > minimumCount)
                .Select(pair => pair.Key),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes curated and creator-personal staples from every sample before downstream ratio math.
    /// </summary>
    /// <param name="samples">Creator deck samples to rewrite.</param>
    /// <param name="personalStaples">Per-creator staple set computed from deck frequency.</param>
    /// <returns>Samples whose entries exclude every staple in the union set.</returns>
    public static IReadOnlyList<CreatorDeckSample> StripStaples(
        IReadOnlyList<CreatorDeckSample> samples,
        IReadOnlySet<string> personalStaples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(personalStaples);

        var staples = new HashSet<string>(
            ContentTagVocabulary.Staples.Select(CardNormalizer.Normalize),
            StringComparer.OrdinalIgnoreCase);
        staples.UnionWith(personalStaples.Select(CardNormalizer.Normalize));

        return samples
            .Select(sample => sample with
            {
                Entries = sample.Entries
                    .Where(entry => !staples.Contains(CardNormalizer.Normalize(GetComparableName(entry))))
                    .ToList()
            })
            .ToList();
    }

    private static HashSet<string> BuildCardNameSet(IEnumerable<DeckEntry> entries)
    {
        return new HashSet<string>(
            entries
                .Select(GetComparableName)
                .Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static double ComputeJaccard(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (left.Count == 0 && right.Count == 0)
        {
            return 0;
        }

        var intersectionCount = left.Count(card => right.Contains(card));
        var unionCount = left.Count + right.Count - intersectionCount;

        return unionCount == 0 ? 0 : (double)intersectionCount / unionCount;
    }

    private static string GetComparableName(DeckEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.NormalizedName)
            ? entry.Name.Trim()
            : entry.NormalizedName.Trim();
    }
}
