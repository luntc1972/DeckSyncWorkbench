using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.StatedRulesExtraction;

namespace DeckFlow.Core.Knowledge.ProfileFusion;

/// <summary>
/// Evaluates whether a measured value conflicts with a stated rule.
/// </summary>
public static class ConflictCalculator
{
    // Why: D-02's prototype goldens require draw at 11.1 vs 13-18 to conflict while keeping
    // the 10%-beyond-edge boundary itself as agree; 0.10 cleanly separates those cases.
    private const double ConflictThresholdPercent = 0.10;
    private const double MinDenominator = 1e-9;

    /// <summary>
    /// Evaluates a single stated-rule/measured-value pair.
    /// </summary>
    /// <param name="rule">Stated rule to evaluate.</param>
    /// <param name="measuredValue">Measured value already matched to the rule.</param>
    /// <param name="effectiveSampleSize">Profile-level effective sample size for the measured profile.</param>
    /// <returns>Verdict plus optional conflict details.</returns>
    public static ConflictCalculationResult Evaluate(
        StatedRuleCandidate rule,
        double measuredValue,
        double? effectiveSampleSize)
    {
        ArgumentNullException.ThrowIfNull(rule);

        // Why: RESEARCH Pitfall 2 interpretation 1 says the coverage floor is the profile-level
        // EffectiveSampleSize gate reused against CreatorStyleProfile.MinDeckFloor, not a per-metric signal.
        if (!effectiveSampleSize.HasValue || effectiveSampleSize.Value < CreatorStyleProfile.MinDeckFloor)
        {
            return new ConflictCalculationResult("insufficient-measured", "low-sample", null, "measured");
        }

        Band band = GetBand(rule);
        if (band.Min is null && band.Max is null)
        {
            return new ConflictCalculationResult("insufficient-measured", "malformed-band", null, "measured");
        }

        if (IsInsideBand(band, measuredValue))
        {
            return new ConflictCalculationResult("agree", null, null, "measured");
        }

        double violatedEdge = GetViolatedEdge(band, measuredValue);
        double delta = measuredValue - violatedEdge;
        double bandRelativePercent = GetBandRelativePercent(measuredValue, violatedEdge);

        if (bandRelativePercent <= ConflictThresholdPercent)
        {
            return new ConflictCalculationResult("agree", null, null, "measured");
        }

        return new ConflictCalculationResult(
            "conflict",
            null,
            new FusedConflict
            {
                StatedValue = violatedEdge,
                MeasuredValue = measuredValue,
                Delta = delta,
                BandRelativePercent = bandRelativePercent,
                Winner = "measured",
            },
            "measured");
    }

    private static Band GetBand(StatedRuleCandidate rule)
    {
        return rule.Comparator switch
        {
            "range" => new Band(rule.ValueMin, rule.ValueMax),
            "lte" => new Band(null, rule.Value ?? rule.ValueMax),
            "gte" => new Band(rule.Value ?? rule.ValueMin, null),
            "eq" => new Band(rule.Value, rule.Value),
            _ => throw new InvalidOperationException($"Unsupported comparator '{rule.Comparator}'."),
        };
    }

    private static bool IsInsideBand(Band band, double measuredValue)
    {
        if (band.Min.HasValue && measuredValue < band.Min.Value)
        {
            return false;
        }

        if (band.Max.HasValue && measuredValue > band.Max.Value)
        {
            return false;
        }

        return true;
    }

    private static double GetViolatedEdge(Band band, double measuredValue)
    {
        if (band.Min.HasValue && measuredValue < band.Min.Value)
        {
            return band.Min.Value;
        }

        if (band.Max.HasValue && measuredValue > band.Max.Value)
        {
            return band.Max.Value;
        }

        throw new InvalidOperationException("Measured value does not violate the band.");
    }

    private static double GetBandRelativePercent(double measuredValue, double violatedEdge)
    {
        double distance = Math.Abs(measuredValue - violatedEdge);
        double denominator = Math.Abs(violatedEdge) < MinDenominator ? 1.0 : Math.Abs(violatedEdge);
        return distance / denominator;
    }

    private sealed record Band(double? Min, double? Max);
}

/// <summary>
/// Result of comparing one stated rule against one measured value.
/// </summary>
/// <param name="Verdict">Conflict verdict.</param>
/// <param name="VerdictReason">Optional low-sample reason when insufficient.</param>
/// <param name="Conflict">Optional conflict details when the verdict is conflict.</param>
/// <param name="Winner">Winning leg for the resolved observable target.</param>
public sealed record ConflictCalculationResult(
    string Verdict,
    string? VerdictReason,
    FusedConflict? Conflict,
    string Winner);
