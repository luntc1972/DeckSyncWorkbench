using DeckFlow.Core.Models;

namespace DeckFlow.Core.Modular;

/// <summary>
/// Immutable imported baseline and modules from which a deck configuration is compiled.
/// </summary>
public sealed record ModularDeckProject
{
    /// <summary>Gets the complete, fixed imported command zone.</summary>
    public required IReadOnlyList<DeckEntry> CommandZone { get; init; }

    /// <summary>Gets the imported baseline mainboard entries used by later swap analysis.</summary>
    public required IReadOnlyList<DeckEntry> BaselineMainboardEntries { get; init; }

    /// <summary>Gets the shared mainboard entries included in every configuration.</summary>
    public required IReadOnlyList<DeckEntry> CoreEntries { get; init; }

    /// <summary>Gets the mutually exclusive strategy modules available to select.</summary>
    public required IReadOnlyList<ModularStrategyModule> StrategyModules { get; init; }

    /// <summary>Gets the mana-support modules linked from strategy modules.</summary>
    public required IReadOnlyList<ModularManaSupportModule> ManaSupportModules { get; init; }
}
