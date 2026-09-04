using DeckFlow.Core.Models;

namespace DeckFlow.Core.Modular;

/// <summary>
/// A named, selectable mainboard strategy package.
/// </summary>
public sealed record ModularStrategyModule
{
    /// <summary>Gets the stable module identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the player-facing module name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the strategy's mainboard entries.</summary>
    public required IReadOnlyList<DeckEntry> MainboardEntries { get; init; }

    /// <summary>Gets the stable identifier of this strategy's linked mana-support module.</summary>
    public required string ManaSupportModuleId { get; init; }
}
