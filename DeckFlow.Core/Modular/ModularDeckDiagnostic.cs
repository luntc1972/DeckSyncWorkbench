namespace DeckFlow.Core.Modular;

/// <summary>
/// Identifies a structural rule evaluated while compiling a modular deck configuration.
/// </summary>
public enum ModularDeckDiagnosticRule
{
    /// <summary>The compilation did not provide a strategy selection.</summary>
    MissingSelection,

    /// <summary>The selected strategy identifier does not exist in the project.</summary>
    UnknownStrategy,

    /// <summary>A strategy references a mana-support module that does not exist.</summary>
    MissingLinkedManaSupport,

    /// <summary>The project does not contain between two and four strategy modules.</summary>
    StrategyCount,

    /// <summary>The strategy modules do not contain equal card quantities.</summary>
    UnequalStrategySize,

    /// <summary>A card occurs in more than one configurable mainboard source.</summary>
    Overlap,

    /// <summary>A configurable entry attempts to occupy the imported command zone.</summary>
    CommandZoneMutation,

    /// <summary>The assembled deck does not contain exactly one hundred cards.</summary>
    TotalCardCount,

    /// <summary>A compiled card is banned in Commander.</summary>
    BannedCard,

    /// <summary>A non-exempt compiled card has multiple copies.</summary>
    Singleton,

    /// <summary>A compiled card falls outside the command-zone color identity.</summary>
    ColorIdentity,

    /// <summary>Injected legality facts are unavailable for a compiled card.</summary>
    UnverifiableCardFacts,
}

/// <summary>
/// Describes a structural issue found while compiling a modular deck configuration.
/// </summary>
public sealed record ModularDeckDiagnostic
{
    /// <summary>Gets the stable rule that failed.</summary>
    public required ModularDeckDiagnosticRule Rule { get; init; }

    /// <summary>Gets the ordinal, case-insensitive ordered card names or module identifiers affected by the rule.</summary>
    public required IReadOnlyList<string> AffectedIdentifiers { get; init; }
}
