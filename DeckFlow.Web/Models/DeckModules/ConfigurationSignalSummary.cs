namespace DeckFlow.Web.Models.DeckModules;

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
