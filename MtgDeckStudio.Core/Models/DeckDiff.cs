namespace MtgDeckStudio.Core.Models;

public sealed record DeckDiff(
    IReadOnlyList<DeckEntry> ToAdd,
    IReadOnlyList<DeckEntry> CountMismatch,
    IReadOnlyList<DeckEntry> OnlyInArchidekt,
    IReadOnlyList<PrintingConflict> PrintingConflicts);
