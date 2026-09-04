using DeckFlow.Core.Knowledge.StatedRulesExtraction;

namespace DeckFlow.Core.Knowledge.ProfileFusion;

/// <summary>
/// Composes recency collapse, metric mapping, classification, and conflict evaluation into a fused ledger.
/// </summary>
public static class ProfileFusionEngine
{
    private const string MeasuredSource = "measured-weighted";
    private const string StatedSource = "stated";
    private const string SupersededSource = "stated-superseded";

    /// <summary>
    /// Fuses measured creator metrics with stated creator rules into deterministic ledger rows.
    /// </summary>
    /// <param name="measured">Measured metrics for a creator profile.</param>
    /// <param name="statedRules">Stated rules extracted from creator content.</param>
    /// <returns>Deterministically ordered fused targets.</returns>
    public static IReadOnlyList<FusedTarget> Fuse(
        IReadOnlyList<MeasuredMetric> measured,
        IReadOnlyList<StatedRuleCandidate> statedRules)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(statedRules);

        if (statedRules.Count == 0)
        {
            // Why (CR-04): no production path currently populates StatedRules end-to-end, so this
            // interim fallback emits measured targets until stated rules are actually populated.
            return measured
                .OrderBy(static metric => metric.Metric, StringComparer.OrdinalIgnoreCase)
                .Select(static metric => new FusedTarget
                {
                    Metric = metric.Metric,
                    Value = metric.Value,
                    Weight = 1.0,
                    Source = MeasuredSource,
                    MeasuredValue = metric.Value,
                    NumDecks = metric.NumDecks,
                    EffectiveSampleSize = metric.Distribution?.EffectiveSampleSize,
                    Verdict = "measured-only",
                })
                .ToList();
        }

        RecencyCollapseResult collapse = StatedRuleRecencyCollapser.Collapse(statedRules);
        // Why (WR-02): measured metrics are read back from persisted JSON (and, for lift metrics,
        // built from a category-pair string) with no uniqueness enforced at the storage boundary,
        // so a case-duplicate key must be deduplicated deterministically here rather than throwing.
        var measuredByMetric = measured
            .GroupBy(static item => item.Metric, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var fused = new List<FusedTarget>(collapse.Active.Count + collapse.Superseded.Count);

        foreach (StatedRuleCandidate rule in collapse.Active.OrderBy(static rule => rule.Metric, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static rule => rule.Condition ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            fused.Add(FuseActiveRule(rule, measuredByMetric));
        }

        foreach (StatedRuleCandidate rule in collapse.Superseded.OrderBy(static rule => rule.Metric, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static rule => rule.Condition ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                     .ThenByDescending(static rule => rule.VideoDateUtc))
        {
            fused.Add(CreateSupersededHistory(rule));
        }

        return fused;
    }

    private static FusedTarget FuseActiveRule(
        StatedRuleCandidate rule,
        IReadOnlyDictionary<string, MeasuredMetric> measuredByMetric)
    {
        if (MetricClassification.Classify(rule.Metric) == MetricKind.Philosophy)
        {
            return CreateStatedTarget(rule, verdict: "philosophy-stated-only", source: StatedSource);
        }

        MeasuredResolution? measured = ResolveMeasured(rule, measuredByMetric);
        double statedValue = GetRepresentativeStatedValue(rule);
        (double? statedMin, double? statedMax) = GetStatedBand(rule);

        if (!string.IsNullOrWhiteSpace(rule.Condition))
        {
            // Why (WR-01): source must reflect what the value actually came from — a stated
            // fallback is not "measured-weighted" just because a measured counterpart exists for
            // the metric in general; the discriminator is whether THIS value came from `measured`.
            return CreateFusedTarget(
                rule,
                value: measured?.Value ?? statedValue,
                source: measured is null ? StatedSource : MeasuredSource,
                statedMin: statedMin,
                statedMax: statedMax,
                measuredValue: measured?.Value,
                numDecks: measured?.NumDecks,
                effectiveSampleSize: measured?.EffectiveSampleSize,
                verdict: "insufficient-measured",
                verdictReason: "no-condition-breakdown",
                conflict: null);
        }

        if (measured is null)
        {
            return CreateFusedTarget(
                rule,
                value: statedValue,
                source: StatedSource,
                statedMin: statedMin,
                statedMax: statedMax,
                measuredValue: null,
                numDecks: null,
                effectiveSampleSize: null,
                verdict: "insufficient-measured",
                verdictReason: null,
                conflict: null);
        }

        MeasuredResolution matched = measured.Value;

        if (!IsSupportedComparator(rule.Comparator))
        {
            return CreateFusedTarget(
                rule,
                value: matched.Value,
                source: MeasuredSource,
                statedMin: statedMin,
                statedMax: statedMax,
                measuredValue: matched.Value,
                numDecks: matched.NumDecks,
                effectiveSampleSize: matched.EffectiveSampleSize,
                verdict: "insufficient-measured",
                verdictReason: null,
                conflict: null);
        }

        ConflictCalculationResult conflict = ConflictCalculator.Evaluate(rule, matched.Value, matched.EffectiveSampleSize);

        return CreateFusedTarget(
            rule,
            value: matched.Value,
            source: MeasuredSource,
            statedMin: statedMin,
            statedMax: statedMax,
            measuredValue: matched.Value,
            numDecks: matched.NumDecks,
            effectiveSampleSize: matched.EffectiveSampleSize,
            verdict: conflict.Verdict,
            verdictReason: conflict.VerdictReason,
            conflict: conflict.Conflict);
    }

    private static FusedTarget CreateStatedTarget(StatedRuleCandidate rule, string verdict, string source)
    {
        (double? statedMin, double? statedMax) = GetStatedBand(rule);

        return CreateFusedTarget(
            rule,
            value: GetRepresentativeStatedValue(rule),
            source: source,
            statedMin: statedMin,
            statedMax: statedMax,
            measuredValue: null,
            numDecks: null,
            effectiveSampleSize: null,
            verdict: verdict,
            verdictReason: null,
            conflict: null);
    }

    private static FusedTarget CreateSupersededHistory(StatedRuleCandidate rule)
    {
        (double? statedMin, double? statedMax) = GetStatedBand(rule);

        return CreateFusedTarget(
            rule,
            value: GetRepresentativeStatedValue(rule),
            source: SupersededSource,
            statedMin: statedMin,
            statedMax: statedMax,
            measuredValue: null,
            numDecks: null,
            effectiveSampleSize: null,
            verdict: "superseded",
            verdictReason: null,
            conflict: null);
    }

    private static FusedTarget CreateFusedTarget(
        StatedRuleCandidate rule,
        double value,
        string source,
        double? statedMin,
        double? statedMax,
        double? measuredValue,
        int? numDecks,
        double? effectiveSampleSize,
        string verdict,
        string? verdictReason,
        FusedConflict? conflict)
    {
        return new FusedTarget
        {
            Metric = rule.Metric,
            Condition = rule.Condition,
            Value = value,
            Weight = 1.0,
            Source = source,
            StatedMin = statedMin,
            StatedMax = statedMax,
            MeasuredValue = measuredValue,
            NumDecks = numDecks,
            EffectiveSampleSize = effectiveSampleSize,
            Verdict = verdict,
            VerdictReason = verdictReason,
            SourceClip = rule.SourceClip,
            VideoDateUtc = rule.VideoDateUtc,
            Confidence = ToConfidenceBand(rule.Confidence),
            Conflict = conflict,
        };
    }

    private static MeasuredResolution? ResolveMeasured(
        StatedRuleCandidate rule,
        IReadOnlyDictionary<string, MeasuredMetric> measuredByMetric)
    {
        return StatedMetricKeyMapper.GetMapKind(rule.Metric) switch
        {
            StatedMetricMapKind.Direct => TryResolveDirect(rule.Metric, measuredByMetric, out MeasuredResolution direct)
                ? direct
                : null,
            StatedMetricMapKind.Derived when rule.Metric.Equals("land_count", StringComparison.OrdinalIgnoreCase) =>
                TryResolveLandCount(measuredByMetric, out MeasuredResolution derived)
                    ? derived
                    : null,
            _ => null,
        };
    }

    private static bool TryResolveDirect(
        string metric,
        IReadOnlyDictionary<string, MeasuredMetric> measuredByMetric,
        out MeasuredResolution measured)
    {
        measured = default;

        if (!StatedMetricKeyMapper.TryMapToMeasuredKey(metric, out string measuredKey) ||
            !measuredByMetric.TryGetValue(measuredKey, out MeasuredMetric? metricValue))
        {
            return false;
        }

        measured = new MeasuredResolution(
            metricValue.Value,
            metricValue.NumDecks,
            metricValue.Distribution?.EffectiveSampleSize);
        return true;
    }

    private static bool TryResolveLandCount(
        IReadOnlyDictionary<string, MeasuredMetric> measuredByMetric,
        out MeasuredResolution measured)
    {
        measured = default;

        if (!measuredByMetric.TryGetValue("karsten:target_lands", out MeasuredMetric? targetLands) ||
            !measuredByMetric.TryGetValue("karsten:land_delta", out MeasuredMetric? landDelta))
        {
            return false;
        }

        // Why: the phase plan explicitly adopts RESEARCH Assumption A2: approximate land_count as target_lands + land_delta.
        measured = new MeasuredResolution(
            targetLands.Value + landDelta.Value,
            targetLands.NumDecks,
            targetLands.Distribution?.EffectiveSampleSize ?? landDelta.Distribution?.EffectiveSampleSize);
        return true;
    }

    private static (double? Min, double? Max) GetStatedBand(StatedRuleCandidate rule)
    {
        return rule.Comparator switch
        {
            "range" => (rule.ValueMin, rule.ValueMax),
            "lte" => (rule.ValueMin, rule.Value ?? rule.ValueMax),
            "gte" => (rule.Value ?? rule.ValueMin, rule.ValueMax),
            "eq" => (rule.Value, rule.Value),
            _ => (rule.ValueMin ?? rule.Value, rule.ValueMax ?? rule.Value),
        };
    }

    private static double GetRepresentativeStatedValue(StatedRuleCandidate rule)
    {
        if (rule.Value.HasValue)
        {
            return rule.Value.Value;
        }

        if (rule.ValueMin.HasValue && rule.ValueMax.HasValue)
        {
            return (rule.ValueMin.Value + rule.ValueMax.Value) / 2.0;
        }

        if (rule.ValueMin.HasValue)
        {
            return rule.ValueMin.Value;
        }

        if (rule.ValueMax.HasValue)
        {
            return rule.ValueMax.Value;
        }

        return 0.0;
    }

    private static string ToConfidenceBand(double confidence)
    {
        if (confidence >= 0.8)
        {
            return "high";
        }

        if (confidence >= 0.5)
        {
            return "med";
        }

        return "low";
    }

    private static bool IsSupportedComparator(string comparator)
        => comparator is "range" or "lte" or "gte" or "eq";

    private readonly record struct MeasuredResolution(
        double Value,
        int NumDecks,
        double? EffectiveSampleSize);
}
