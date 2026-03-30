namespace MtgDeckStudio.Core.Models;

public sealed record PrintingConflict
{
    public required string CardName { get; init; }

    public required DeckEntry MoxfieldVersion { get; init; }

    public required DeckEntry ArchidektVersion { get; init; }

    public PrintingChoice Resolution { get; init; } = PrintingChoice.Unresolved;
}
