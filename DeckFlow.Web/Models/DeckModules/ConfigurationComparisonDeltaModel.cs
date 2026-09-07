namespace DeckFlow.Web.Models.DeckModules;

/// <summary>Numeric, list-shaped differences between analyzed Deck Modules configurations.</summary>
public sealed record ConfigurationComparisonDeltaModel
{
    public required ConfigurationComparisonColumn Reference { get; init; }

    public IReadOnlyList<ConfigurationComparisonColumn> Columns { get; init; } = [];

    public IReadOnlyList<ConfigurationColorSourceDeltaRow> ColorRows { get; init; } = [];

    public IReadOnlyList<ConfigurationInteractionDeltaRow> InteractionRows { get; init; } = [];
}

/// <summary>Metrics for one configuration, optionally compared with the reference configuration.</summary>
public sealed record ConfigurationComparisonColumn
{
    public string? ConfigurationId { get; init; }
    public string? ConfigurationName { get; init; }
    public required bool IsAnalyzed { get; init; }
    public required bool IsCoreOnly { get; init; }
    public string? Health { get; init; }
    public int? LandCount { get; init; }
    public double? TargetLandCount { get; init; }
    public double? LandTargetDelta { get; init; }
    public int? RampSourceCount { get; init; }
    public int? HardToCastCount { get; init; }
    public int? LandCountDelta { get; init; }
    public int? RampSourceCountDelta { get; init; }
    public int? HardToCastCountDelta { get; init; }
    public required bool HasLandCountChange { get; init; }
}

/// <summary>Color-source values aligned across configurations.</summary>
public sealed record ConfigurationColorSourceDeltaRow
{
    public required string Color { get; init; }
    public required string DisplayColor { get; init; }
    public double? ReferenceActualSources { get; init; }
    public int? ReferenceRequiredSources { get; init; }
    public IReadOnlyList<ConfigurationColorSourceDeltaValue> Values { get; init; } = [];
}

/// <summary>One configuration's color-source value and delta.</summary>
public sealed record ConfigurationColorSourceDeltaValue
{
    public string? ConfigurationId { get; init; }
    public double? ActualSources { get; init; }
    public int? RequiredSources { get; init; }
    public double? ActualSourcesDelta { get; init; }
    public required bool IsPresent { get; init; }
}

/// <summary>Interaction values aligned by configuration module kind.</summary>
public sealed record ConfigurationInteractionDeltaRow
{
    public required Services.Modular.ConfigurationModuleKind ModuleKind { get; init; }
    public required string ModuleName { get; init; }
    public IReadOnlyList<ConfigurationInteractionDeltaValue> Values { get; init; } = [];
}

/// <summary>One configuration's interaction value and delta.</summary>
public sealed record ConfigurationInteractionDeltaValue
{
    public string? ConfigurationId { get; init; }
    public int? InteractionCount { get; init; }
    public int? Delta { get; init; }
    public required bool IsPresent { get; init; }
}
