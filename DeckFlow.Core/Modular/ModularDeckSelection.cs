namespace DeckFlow.Core.Modular;

/// <summary>
/// Identifies the single strategy selected for a modular deck compilation.
/// </summary>
public sealed record ModularDeckSelection
{
    /// <summary>Gets the stable identifier of the selected strategy module.</summary>
    public required string StrategyId { get; init; }
}
