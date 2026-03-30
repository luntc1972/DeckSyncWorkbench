using MtgDeckStudio.Core.Models;

namespace MtgDeckStudio.Web.Services;

internal static class DeckSyncSupport
{
    public static List<DeckEntry> GetSourceEntries(SyncDirection direction, LoadedDecks loadedDecks)
        => direction == SyncDirection.MoxfieldToArchidekt ? loadedDecks.MoxfieldEntries : loadedDecks.ArchidektEntries;

    public static List<DeckEntry> GetTargetEntries(SyncDirection direction, LoadedDecks loadedDecks)
        => direction == SyncDirection.MoxfieldToArchidekt ? loadedDecks.ArchidektEntries : loadedDecks.MoxfieldEntries;

    public static string GetSourceSystem(SyncDirection direction)
        => direction == SyncDirection.MoxfieldToArchidekt ? "Moxfield" : "Archidekt";

    public static string GetTargetSystem(SyncDirection direction)
        => direction == SyncDirection.MoxfieldToArchidekt ? "Archidekt" : "Moxfield";
}
