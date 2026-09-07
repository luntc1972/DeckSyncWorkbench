namespace DeckFlow.Web.Models.DeckModules;

/// <summary>Request to compare cached analyses for submitted Deck Modules configurations.</summary>
public sealed record ConfigurationComparisonRequest
{
    /// <summary>Minimum number of configuration analyses needed for a comparison.</summary>
    public const int MinSideCount = 2;

    /// <summary>Maximum number of configuration analyses accepted for a comparison.</summary>
    public const int MaxSideCount = DeckModulesCompilationRequest.MaxAlternativeCount;

    /// <summary>Gets the analyses to compare.</summary>
    public required IReadOnlyList<ConfigurationComparisonSide> Sides { get; init; }

    /// <summary>Gets the configuration identifier used as the comparison reference.</summary>
    public required string ReferenceConfigurationId { get; init; }
}

/// <summary>One configuration analysis participating in a comparison request.</summary>
public sealed record ConfigurationComparisonSide
{
    /// <summary>Gets the submitted configuration identifier.</summary>
    public required string ConfigurationId { get; init; }

    /// <summary>Gets the cache key for the configuration analysis.</summary>
    public required string AnalysisKey { get; init; }

    /// <summary>Gets the optional analysis payload used to restore an expired cache entry.</summary>
    public ConfigurationAnalysisResult? Analysis { get; init; }
}
