namespace DeckFlow.Core.Modular;

/// <summary>
/// The direction of a baseline-relative card quantity change.
/// </summary>
public enum ModularDeckSwapAction
{
    /// <summary>Add cards to the active configuration.</summary>
    Add,

    /// <summary>Remove cards from the active configuration.</summary>
    Remove,
}

/// <summary>
/// A card quantity action required to move between baseline and compiled configurations.
/// </summary>
public sealed record ModularDeckSwapEntry
{
    /// <summary>Gets whether this entry adds or removes cards.</summary>
    public required ModularDeckSwapAction Action { get; init; }

    /// <summary>Gets the player-facing card name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the normalized card name used to aggregate quantities.</summary>
    public required string NormalizedName { get; init; }

    /// <summary>Gets the quantity to add or remove.</summary>
    public required int Quantity { get; init; }
}
