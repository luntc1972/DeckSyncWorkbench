namespace MtgDeckStudio.Core.Reporting;

public sealed record DeckCategoryEntry(
    string DeckId,
    string? DeckName,
    string CardName,
    string NormalizedCardName,
    string Category,
    string Board,
    int Count,
    string LastSeenUtc);
