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

/// <summary>Placeholder for a player-declared configuration disclosure.</summary>
public sealed record ConfigurationDeclaredDisclosure
{
    /// <summary>Gets whether the configuration has a declaration.</summary>
    public required bool IsDeclared { get; init; }
}
