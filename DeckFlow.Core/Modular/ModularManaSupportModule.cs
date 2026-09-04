using DeckFlow.Core.Models;

namespace DeckFlow.Core.Modular;

/// <summary>
/// A named mainboard land and ramp package linked to a strategy module.
/// </summary>
public sealed record ModularManaSupportModule
{
    /// <summary>Gets the stable module identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the player-facing module name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the mana-support mainboard entries.</summary>
    public required IReadOnlyList<DeckEntry> MainboardEntries { get; init; }
}
