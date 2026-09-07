using DeckFlow.Core.Models;
using DeckFlow.Core.Modular;

namespace DeckFlow.Web.Models.DeckModules;

/// <summary>
/// Browser-session Deck Modules baseline returned by import: the immutable, displayable command
/// zone and the baseline mainboard entries available for manual module assignment.
/// </summary>
public sealed record DeckModulesViewModel
{
    /// <summary>Gets the server-issued, time-limited baseline token required for compilation.</summary>
    public required string BaselineToken { get; init; }

    /// <summary>Gets the complete, fixed imported command zone. Displayable but never editable.</summary>
    public required IReadOnlyList<DeckEntry> CommandZone { get; init; }

    /// <summary>Gets the imported baseline mainboard entries available for manual module assignment.</summary>
    public required IReadOnlyList<DeckEntry> BaselineMainboardEntries { get; init; }

    /// <summary>Gets an optional, non-blocking notice surfaced by the import pathway (e.g. a detected companion).</summary>
    public string? ImportNotice { get; init; }
}

/// <summary>
/// Result of compiling the current browser-session project and active selection through the
/// Phase 1 compiler.
/// </summary>
public sealed record DeckModulesCompilationViewModel
{
    /// <summary>Gets whether the selection and project satisfy structural compilation rules.</summary>
    public required bool IsStructurallyValid { get; init; }

    /// <summary>Gets whether structural and injected legality validation found no diagnostics.</summary>
    public required bool IsVerifiedLegal { get; init; }

    /// <summary>Gets the diagnostics discovered while compiling.</summary>
    public required IReadOnlyList<ModularDeckDiagnostic> Diagnostics { get; init; }

    /// <summary>Gets the stable identifier of the selected strategy alternative.</summary>
    public required string SelectedStrategyId { get; init; }

    /// <summary>Gets the player-facing name of the selected strategy alternative.</summary>
    public required string SelectedStrategyName { get; init; }

    /// <summary>Gets the player-facing name of the selected mana-support module, or empty when none is linked.</summary>
    public required string SelectedManaSupportModuleName { get; init; }

    /// <summary>Gets the fixed imported command-zone entries, unchanged by compilation.</summary>
    public required IReadOnlyList<DeckEntry> CommandZoneEntries { get; init; }

    /// <summary>Gets the assembled mainboard entries for the compiled configuration.</summary>
    public required IReadOnlyList<DeckEntry> MainboardEntries { get; init; }

    /// <summary>Gets all compiled entries in command-zone-then-mainboard order.</summary>
    public required IReadOnlyList<DeckEntry> Entries { get; init; }

    /// <summary>Gets the sum of quantities across all compiled entries.</summary>
    public required int TotalCardCount { get; init; }

    /// <summary>Gets the exact baseline-relative add/remove/reset actions for this compiled configuration.</summary>
    public required ModularDeckSwapPlan SwapPlan { get; init; }
}
