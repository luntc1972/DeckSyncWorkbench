namespace DeckFlow.Core.Modular;

/// <summary>
/// Exact quantity changes between the imported baseline and a compiled configuration.
/// </summary>
public sealed record ModularDeckSwapPlan
{
    /// <summary>Gets cards entering the compiled configuration.</summary>
    public required IReadOnlyList<ModularDeckSwapEntry> ToAdd { get; init; }

    /// <summary>Gets cards leaving the imported baseline.</summary>
    public required IReadOnlyList<ModularDeckSwapEntry> ToRemove { get; init; }

    /// <summary>Gets reverse actions that restore the imported baseline.</summary>
    public required IReadOnlyList<ModularDeckSwapEntry> ToReset { get; init; }
}
