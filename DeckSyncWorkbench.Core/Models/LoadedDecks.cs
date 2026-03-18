namespace DeckSyncWorkbench.Core.Models;

public sealed record LoadedDecks(List<DeckEntry> MoxfieldEntries, List<DeckEntry> ArchidektEntries);
