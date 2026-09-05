using DeckFlow.Core.Models;

namespace DeckFlow.Core.Modular;

/// <summary>
/// Immutable result of assembling a selected strategy with its linked mana support.
/// </summary>
public sealed record ModularDeckCompilation
{
    /// <summary>Gets whether the selection and project satisfy structural compilation rules.</summary>
    public required bool IsStructurallyValid { get; init; }

    /// <summary>Gets the structural diagnostics discovered while compiling.</summary>
    public required IReadOnlyList<ModularDeckDiagnostic> Diagnostics { get; init; }

    /// <summary>Gets the stable identifier of the selected strategy module.</summary>
    public required string SelectedStrategyId { get; init; }

    /// <summary>Gets the player-facing name of the selected strategy module.</summary>
    public required string SelectedStrategyName { get; init; }

    /// <summary>Gets the stable identifier of the selected mana-support module.</summary>
    public required string SelectedManaSupportModuleId { get; init; }

    /// <summary>Gets the player-facing name of the selected mana-support module.</summary>
    public required string SelectedManaSupportModuleName { get; init; }

    /// <summary>Gets the fixed imported command-zone entries.</summary>
    public required IReadOnlyList<DeckEntry> CommandZoneEntries { get; init; }

    /// <summary>Gets the assembled mainboard entries.</summary>
    public required IReadOnlyList<DeckEntry> MainboardEntries { get; init; }

    /// <summary>Gets all entries in command-zone then mainboard order.</summary>
    public required IReadOnlyList<DeckEntry> Entries { get; init; }

    /// <summary>Gets the sum of quantities across all compiled entries.</summary>
    public required int TotalCardCount { get; init; }
}
