namespace DeckFlow.Core.Knowledge.MeasuredStyleExtraction;

/// <summary>
/// Host-agnostic input bundle for pure measured-style extraction routines.
/// </summary>
// Why (WR-09, maintainer decision 2026-09-06 per WAITING.json): no production caller builds this
// bundle yet - it is the intended parameter shape for a future host-agnostic measured-style
// extraction entry point, exercised today only by DeckFlow.Core.Tests. Kept rather than deleted;
// see ai-context-deckflow/repos/deckflow/notes/2026-09-06-cycle20-branch-divergence.md.
public sealed record MeasuredStyleInputs
{
    /// <summary>Creator deck samples already fetched and normalized by the host tier.</summary>
    public required IReadOnlyList<CreatorDeckSample> Samples { get; init; }

    /// <summary>Resolved multi-bucket categories keyed by card name.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> CardCategories { get; init; }

    /// <summary>Shared global baseline used by lift calculations.</summary>
    public required GlobalCategoryBaseline Baseline { get; init; }

    /// <summary>
    /// Indicates the host has no curated folder-weight map, so every sample should remain at full weight.
    /// </summary>
    public bool WeightsUncurated { get; init; }
}
