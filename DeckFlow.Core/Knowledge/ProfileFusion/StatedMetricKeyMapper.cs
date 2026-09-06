using DeckFlow.Core.Knowledge;

namespace DeckFlow.Core.Knowledge.ProfileFusion;

/// <summary>
/// Describes how a stated metric relates to measured-profile keys.
/// </summary>
public enum StatedMetricMapKind
{
    /// <summary>A single measured key exists for this stated metric.</summary>
    Direct,

    /// <summary>The stated metric resolves through a derived measured value.</summary>
    Derived,

    /// <summary>No measured counterpart exists for this stated metric.</summary>
    StatedOnly,
}

/// <summary>
/// Translates stated-rule metric keys into measured-profile metric keys.
/// </summary>
public static class StatedMetricKeyMapper
{
    private static readonly IReadOnlyDictionary<string, string> DirectMappings =
        BuildDirectMappings();

    /// <summary>
    /// Gets the closed category set that maps via the <c>category_ratio:</c> measured prefix.
    /// </summary>
    // Why (WR-09, maintainer decision 2026-09-06 per WAITING.json): no production caller reads
    // this set today - GetMapKind below is the only production entry point, and it doesn't need
    // the enumerated set. Kept as the documented closed-vocabulary anchor
    // (StatedMetricKeyMapperTests.cs pins it against ContentTagVocabulary.CardCategories) rather
    // than deleted; see ai-context-deckflow/repos/deckflow/notes/2026-09-06-cycle20-branch-divergence.md.
    public static IReadOnlySet<string> PrefixMappedCategories { get; } =
        new HashSet<string>(ContentTagVocabulary.CardCategories, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies whether a stated metric maps directly, derives from measured data, or remains stated-only.
    /// </summary>
    /// <param name="statedMetric">The stated metric key to classify.</param>
    /// <returns>The mapping kind for the supplied stated metric key.</returns>
    public static StatedMetricMapKind GetMapKind(string statedMetric)
    {
        ArgumentNullException.ThrowIfNull(statedMetric);

        if (DirectMappings.ContainsKey(statedMetric))
        {
            return StatedMetricMapKind.Direct;
        }

        return statedMetric.Equals("land_count", StringComparison.OrdinalIgnoreCase)
            ? StatedMetricMapKind.Derived
            : StatedMetricMapKind.StatedOnly;
    }

    /// <summary>
    /// Attempts to translate a stated metric key into its single measured metric key.
    /// </summary>
    /// <param name="statedMetric">The stated metric key to translate.</param>
    /// <param name="measuredKey">Receives the measured metric key when a direct mapping exists.</param>
    /// <returns><see langword="true"/> when the stated metric maps directly to one measured key.</returns>
    public static bool TryMapToMeasuredKey(string statedMetric, out string measuredKey)
    {
        ArgumentNullException.ThrowIfNull(statedMetric);

        if (DirectMappings.TryGetValue(statedMetric, out string? mappedKey))
        {
            measuredKey = mappedKey;
            return true;
        }

        measuredKey = string.Empty;
        return false;
    }

    /// <summary>
    /// Attempts to calculate a derived stated metric from its measured inputs.
    /// </summary>
    /// <param name="statedMetric">The stated metric key to calculate.</param>
    /// <param name="measuredMetrics">Measured metric values keyed by measured metric names.</param>
    /// <param name="value">Receives the derived value when all required inputs are available.</param>
    /// <returns><see langword="true"/> when the supplied metric is derivable from the measured inputs.</returns>
    public static bool TryGetDerivedValue(
        string statedMetric,
        IReadOnlyDictionary<string, double> measuredMetrics,
        out double value)
    {
        ArgumentNullException.ThrowIfNull(statedMetric);
        ArgumentNullException.ThrowIfNull(measuredMetrics);

        if (statedMetric.Equals("land_count", StringComparison.OrdinalIgnoreCase) &&
            measuredMetrics.TryGetValue("karsten:target_lands", out double targetLands) &&
            measuredMetrics.TryGetValue("karsten:land_delta", out double landDelta))
        {
            value = targetLands + landDelta;
            return true;
        }

        value = default;
        return false;
    }

    private static IReadOnlyDictionary<string, string> BuildDirectMappings()
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Why: the join is driven by the closed stated vocabulary, so prefix-mapping exactly the
        // 11 CardCategories is correct; extra measured category_ratio:* keys outside that set are
        // a safe superset that simply never join.
        foreach (string category in ContentTagVocabulary.CardCategories)
        {
            mappings[category] = $"category_ratio:{category}";
        }

        mappings["karsten:target_lands"] = "karsten:target_lands";
        mappings["karsten:land_delta"] = "karsten:land_delta";
        mappings["karsten:health_score"] = "karsten:health_score";
        mappings["combo_density:included_per_deck"] = "combo_density:included_per_deck";

        return mappings;
    }
}
