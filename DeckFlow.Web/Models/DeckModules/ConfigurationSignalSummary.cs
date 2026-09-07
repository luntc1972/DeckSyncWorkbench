using DeckFlow.Web.Services.Modular;

namespace DeckFlow.Web.Models.DeckModules;

/// <summary>Interaction spells attributed to one configuration module.</summary>
public sealed record ConfigurationModuleInteractionCount
{
    /// <summary>Gets the module that supplied the counted spells.</summary>
    public required ConfigurationModuleKind ModuleKind { get; init; }

    /// <summary>Gets the stable display name for the module.</summary>
    public required string ModuleName { get; init; }

    /// <summary>Gets the number of interaction spells supplied by the module.</summary>
    public required int InteractionCount { get; init; }
}

/// <summary>Bracket and Game Changer signals detected for one compiled configuration.</summary>
public sealed record ConfigurationSignalSummary
{
    /// <summary>Gets the detected Commander bracket number.</summary>
    public required int BracketNumber { get; init; }

    /// <summary>Gets catalogued Game Changers detected in the active deck.</summary>
    public IReadOnlyList<string> GameChangers { get; init; } = Array.Empty<string>();

    /// <summary>Gets catalogued mass-land-denial cards detected in the active deck.</summary>
    public IReadOnlyList<string> MassLandDenialCards { get; init; } = Array.Empty<string>();

    /// <summary>Gets catalogued extra-turn cards detected in the active deck.</summary>
    public IReadOnlyList<string> ExtraTurnCards { get; init; } = Array.Empty<string>();

    /// <summary>Gets whether two-card combo detection ran; false means detection did not run and must never render as no combos found.</summary>
    public required bool ComboDetectionAvailable { get; init; }

    /// <summary>Gets the effective date of the catalog used for classification.</summary>
    public required string CatalogEffectiveDate { get; init; }

    /// <summary>Gets interaction counts for each module when analyzed spells were available.</summary>
    public IReadOnlyList<ConfigurationModuleInteractionCount> InteractionsByModule { get; init; } = Array.Empty<ConfigurationModuleInteractionCount>();

    /// <summary>Gets whether interaction attribution had analyzed-spell data to measure.</summary>
    public bool InteractionAttributionAvailable { get; init; }

    /// <summary>Gets the optional player-declared bracket disclosure.</summary>
    public ConfigurationDeclaredDisclosure? Declared { get; init; }
}

/// <summary>Player-declared configuration disclosure.</summary>
public sealed record ConfigurationDeclaredDisclosure
{
    /// <summary>Gets the declared profile display label.</summary>
    public required string Profile { get; init; }

    /// <summary>Gets the player text exactly as typed; it is disclosure, not a computed judgement, and must never be paraphrased, trimmed, truncated, or re-cased.</summary>
    public required string PlayPlan { get; init; }

    /// <summary>Gets whether the configuration has a declaration.</summary>
    public required bool IsDeclared { get; init; }

    /// <summary>Gets the optional neutral note when the declared profile and bracket rubric differ.</summary>
    public string? ProfileDisagreementNote { get; init; }
}
