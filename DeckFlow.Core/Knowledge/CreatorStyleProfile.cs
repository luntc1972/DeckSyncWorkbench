namespace DeckFlow.Core.Knowledge;

/// <summary>
/// Immutable creator style-profile substrate consumed by downstream measured, stated, and fused profile phases.
/// </summary>
public sealed record CreatorStyleProfile
{
    /// <summary>Minimum EDHREC deck-count floor required for a non-insufficient creator profile sample.</summary>
    public const int MinDeckFloor = 5;

    /// <summary>URL-safe creator slug produced from the source name.</summary>
    public required string Slug { get; init; }

    /// <summary>Platform identifier associated with the creator profile.</summary>
    public required string Platform { get; init; }

    /// <summary>Deck count used when computing this profile.</summary>
    public required int MinDecks { get; init; }

    /// <summary>Whether the profile was built from fewer decks than <see cref="MinDeckFloor"/>.</summary>
    public bool InsufficientSample { get; init; }

    /// <summary>Stated creator rules distilled from public clips or commentary.</summary>
    public IReadOnlyList<StatedRule> StatedRules { get; init; } = Array.Empty<StatedRule>();

    /// <summary>Measured metrics computed from observed deck samples.</summary>
    public IReadOnlyList<MeasuredMetric> MeasuredMetrics { get; init; } = Array.Empty<MeasuredMetric>();

    /// <summary>Fused targets that combine stated guidance with measured observations.</summary>
    public IReadOnlyList<FusedTarget> FusedTargets { get; init; } = Array.Empty<FusedTarget>();

    /// <summary>UTC timestamp indicating when this profile was last updated.</summary>
    public required DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>
/// Immutable stated creator rule captured from a source clip.
/// </summary>
public sealed record StatedRule
{
    /// <summary>Rule category used to group related creator guidance.</summary>
    public required string Category { get; init; }

    /// <summary>Metric targeted by the stated rule.</summary>
    public required string TargetMetric { get; init; }

    /// <summary>Target metric value expressed by the creator.</summary>
    public double? TargetValue { get; init; }

    /// <summary>Inclusive lower bound for range rules.</summary>
    public double? TargetValueMin { get; init; }

    /// <summary>Inclusive upper bound for range rules.</summary>
    public double? TargetValueMax { get; init; }

    /// <summary>Comparator describing how the target value should be interpreted.</summary>
    public required string Comparator { get; init; }

    /// <summary>Optional conditional scope for the rule.</summary>
    public string? Condition { get; init; }

    /// <summary>Source clip excerpt supporting the stated rule.</summary>
    public required string SourceClip { get; init; }

    /// <summary>Confidence assigned to the extracted rule.</summary>
    public required double Confidence { get; init; }

    /// <summary>UTC publish date of the source video.</summary>
    public required DateTimeOffset VideoDateUtc { get; init; }
}

/// <summary>
/// Immutable measured metric derived from observed deck data.
/// </summary>
public sealed record MeasuredMetric
{
    /// <summary>Metric name measured from the creator's deck sample.</summary>
    public required string Metric { get; init; }

    /// <summary>Measured value for the metric.</summary>
    public required double Value { get; init; }

    /// <summary>Deck count contributing to the measured value.</summary>
    public required int NumDecks { get; init; }

    /// <summary>Optional distribution details for the measured metric.</summary>
    public MetricDistribution? Distribution { get; init; }
}

/// <summary>
/// Immutable fused target that blends stated guidance with measured results.
/// </summary>
public sealed record FusedTarget
{
    /// <summary>Metric name associated with the fused target.</summary>
    public required string Metric { get; init; }

    /// <summary>Fused value derived for the metric.</summary>
    public required double Value { get; init; }

    /// <summary>Weight assigned to the fused value.</summary>
    public required double Weight { get; init; }

    /// <summary>Source description for how the fused target was produced.</summary>
    public required string Source { get; init; }

    /// <summary>Optional condition segment paired with the metric when evaluating the fused target.</summary>
    public string? Condition { get; init; }

    /// <summary>Optional lower bound from the creator's stated target band.</summary>
    public double? StatedMin { get; init; }

    /// <summary>Optional upper bound from the creator's stated target band.</summary>
    public double? StatedMax { get; init; }

    /// <summary>Optional measured value retained for the ledger even when the resolved source is stated-only.</summary>
    public double? MeasuredValue { get; init; }

    /// <summary>Optional raw contributing deck count for the measured leg of the fused target.</summary>
    public int? NumDecks { get; init; }

    /// <summary>Optional effective sample size retained as the coverage-floor signal for the measured leg.</summary>
    public double? EffectiveSampleSize { get; init; }

    /// <summary>Optional verdict badge describing how the stated and measured legs resolved.</summary>
    public string? Verdict { get; init; }

    /// <summary>Optional verdict discriminator explaining why an insufficient-measured verdict landed.</summary>
    public string? VerdictReason { get; init; }

    /// <summary>Optional source clip excerpt supporting the stated leg of the fused target.</summary>
    public string? SourceClip { get; init; }

    /// <summary>Optional UTC video date for the stated source that contributed this fused target.</summary>
    public DateTimeOffset? VideoDateUtc { get; init; }

    /// <summary>Optional coarse confidence band for the stated leg of the fused target.</summary>
    public string? Confidence { get; init; }

    /// <summary>Optional conflict details between stated and measured inputs.</summary>
    public FusedConflict? Conflict { get; init; }
}

/// <summary>
/// Immutable distribution summary attached to a measured metric.
/// </summary>
public sealed record MetricDistribution
{
    /// <summary>Mean value across the contributing sample.</summary>
    public required double Mean { get; init; }

    /// <summary>Minimum observed value across the contributing sample.</summary>
    public required double Min { get; init; }

    /// <summary>Maximum observed value across the contributing sample.</summary>
    public required double Max { get; init; }

    /// <summary>Standard deviation across the contributing sample.</summary>
    public required double StdDev { get; init; }

    /// <summary>D-10 folder-weighted effective sample size (fractional), distinct from raw <see cref="MeasuredMetric.NumDecks"/>.</summary>
    public double? EffectiveSampleSize { get; init; }
}

/// <summary>
/// Immutable conflict details recorded when stated and measured values diverge.
/// </summary>
public sealed record FusedConflict
{
    /// <summary>Value extracted from the creator's stated guidance.</summary>
    public required double StatedValue { get; init; }

    /// <summary>Value measured from observed deck data.</summary>
    public required double MeasuredValue { get; init; }

    /// <summary>Difference between the stated and measured values.</summary>
    public required double Delta { get; init; }

    /// <summary>Optional band-relative percent quantifying how far the measured value sits beyond the stated band.</summary>
    public double? BandRelativePercent { get; init; }

    /// <summary>Optional winner marker identifying whether the measured or stated leg prevailed.</summary>
    public string? Winner { get; init; }
}
