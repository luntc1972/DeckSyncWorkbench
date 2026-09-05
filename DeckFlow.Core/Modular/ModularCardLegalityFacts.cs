namespace DeckFlow.Core.Modular;

/// <summary>
/// Caller-supplied legality facts for one normalized card name.
/// </summary>
public sealed record ModularCardLegalityFacts
{
    /// <summary>Gets the card's Commander color identity.</summary>
    public required IReadOnlyList<string> ColorIdentity { get; init; }

    /// <summary>Gets whether the card is banned in Commander.</summary>
    public required bool IsBanned { get; init; }

    /// <summary>Gets whether multiple copies of the card are allowed.</summary>
    public required bool IsSingletonExempt { get; init; }
}
