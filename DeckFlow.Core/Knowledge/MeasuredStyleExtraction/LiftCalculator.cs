namespace DeckFlow.Core.Knowledge.MeasuredStyleExtraction;

/// <summary>
/// Pure helper for D-07 creator-vs-global category lift calculations.
/// </summary>
public static class LiftCalculator
{
    /// <summary>
    /// Computes category-pair lift as creator <c>Pr(A∩B)</c> over global <c>Pr(A)·Pr(B)</c>.
    /// </summary>
    /// <param name="creatorDecks">Creator deck samples contributing the numerator.</param>
    /// <param name="cardCategories">Resolved category map keyed by card name.</param>
    /// <param name="baseline">Shared global category baseline supplying the denominator.</param>
    /// <returns>Computed category-pair lift values ordered by strongest lift first.</returns>
    public static IReadOnlyList<CategoryLift> ComputeLift(
        IReadOnlyList<CreatorDeckSample> creatorDecks,
        IReadOnlyDictionary<string, IReadOnlyList<string>> cardCategories,
        GlobalCategoryBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(creatorDecks);
        ArgumentNullException.ThrowIfNull(cardCategories);
        ArgumentNullException.ThrowIfNull(baseline);

        if (creatorDecks.Count == 0 || baseline.TotalDecks <= 0)
        {
            return [];
        }

        var pairCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var deck in creatorDecks)
        {
            var categories = CategoryCounter.DeckCategoryPresence(deck, cardCategories)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            for (var left = 0; left < categories.Length; left++)
            {
                for (var right = left + 1; right < categories.Length; right++)
                {
                    var pairKey = BuildPairKey(categories[left], categories[right]);
                    pairCounts[pairKey] = pairCounts.TryGetValue(pairKey, out var count) ? count + 1 : 1;
                }
            }
        }

        var lifts = new List<CategoryLift>(pairCounts.Count);

        // Why (WR-11): pairCounts keys are built OrdinalIgnoreCase above, but baseline's
        // dictionaries are typed as bare IReadOnlyDictionary<string, int> with no comparer
        // contract; a case-sensitive baseline would silently drop every pair whose casing
        // differs. Assert the comparer contract at this boundary instead of trusting baseline's.
        var baselinePairs = new HashSet<string>(baseline.DecksWithCategoryPair.Keys, StringComparer.OrdinalIgnoreCase);
        var baselineMarginals = new Dictionary<string, int>(baseline.DecksWithCategory, StringComparer.OrdinalIgnoreCase);

        foreach (var pairCount in pairCounts)
        {
            // Why (D-07): the pair baseline gates which pairs are comparable; the lift ratio
            // itself uses only the marginals below.
            if (!baselinePairs.Contains(pairCount.Key))
            {
                continue;
            }

            var split = pairCount.Key.Split('|', 2);
            var categoryA = split[0];
            var categoryB = split[1];

            if (!TryGetGlobalProbability(categoryA, baseline, baselineMarginals, out var globalProbabilityA) ||
                !TryGetGlobalProbability(categoryB, baseline, baselineMarginals, out var globalProbabilityB))
            {
                // Why: omitting pairs with missing baseline marginals keeps downstream consumers free of NaN/Infinity while still signaling that the shared corpus has no usable denominator for this pair.
                continue;
            }

            var denominator = globalProbabilityA * globalProbabilityB;
            if (denominator <= 0)
            {
                // Why: omitting pairs with missing baseline marginals keeps downstream consumers free of NaN/Infinity while still signaling that the shared corpus has no usable denominator for this pair.
                continue;
            }

            var creatorProbability = pairCount.Value / (double)creatorDecks.Count;
            lifts.Add(new CategoryLift
            {
                CategoryA = categoryA,
                CategoryB = categoryB,
                Lift = creatorProbability / denominator,
                CreatorDecksWithBoth = pairCount.Value,
            });
        }

        return lifts
            .OrderByDescending(item => item.Lift)
            .ThenBy(item => item.CategoryA, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.CategoryB, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildPairKey(string left, string right)
    {
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{left}|{right}"
            : $"{right}|{left}";
    }

    private static bool TryGetGlobalProbability(
        string category,
        GlobalCategoryBaseline baseline,
        IReadOnlyDictionary<string, int> baselineMarginals,
        out double probability)
    {
        if (!baselineMarginals.TryGetValue(category, out var deckCount) || deckCount <= 0)
        {
            probability = 0;
            return false;
        }

        probability = deckCount / (double)baseline.TotalDecks;
        return probability > 0;
    }
}

/// <summary>
/// Category-pair lift result emitted by <see cref="LiftCalculator"/>.
/// </summary>
public sealed record CategoryLift
{
    /// <summary>The first category in canonical sorted order.</summary>
    public required string CategoryA { get; init; }

    /// <summary>The second category in canonical sorted order.</summary>
    public required string CategoryB { get; init; }

    /// <summary>The D-07 lift score for the category pair.</summary>
    public required double Lift { get; init; }

    /// <summary>The number of creator decks containing both categories.</summary>
    public required int CreatorDecksWithBoth { get; init; }
}
