using DeckSyncWorkbench.Core.Models;

namespace DeckSyncWorkbench.Web.Services;

internal static class DeckSyncSupport
{
    public static List<DeckEntry> GetSourceEntries(SyncDirection direction, LoadedDecks loadedDecks)
        => direction == SyncDirection.DeckSyncWorkbench ? loadedDecks.MoxfieldEntries : loadedDecks.ArchidektEntries;

    public static List<DeckEntry> GetTargetEntries(SyncDirection direction, LoadedDecks loadedDecks)
        => direction == SyncDirection.DeckSyncWorkbench ? loadedDecks.ArchidektEntries : loadedDecks.MoxfieldEntries;

    public static string GetSourceSystem(SyncDirection direction)
        => direction == SyncDirection.DeckSyncWorkbench ? "Moxfield" : "Archidekt";

    public static string GetTargetSystem(SyncDirection direction)
        => direction == SyncDirection.DeckSyncWorkbench ? "Archidekt" : "Moxfield";
}
