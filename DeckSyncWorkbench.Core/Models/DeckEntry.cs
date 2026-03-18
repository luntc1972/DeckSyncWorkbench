namespace DeckSyncWorkbench.Core.Models;

public sealed record DeckEntry
{
    public required string Name { get; init; }

    public required string NormalizedName { get; init; }

    public required int Quantity { get; init; }

    public required string Board { get; init; }

    public string? SetCode { get; init; }

    public string? CollectorNumber { get; init; }

    public string? Category { get; init; }

    public bool IsFoil { get; init; }
}
