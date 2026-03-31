using MtgDeckStudio.Core.Models;

namespace MtgDeckStudio.Web.Services;

/// <summary>
/// Resolves deck-system labels and source-target mappings for compare workflows.
/// </summary>
internal static class DeckSyncSupport
{
    /// <summary>
    /// Returns the entries that should be treated as the source deck.
    /// </summary>
    /// <param name="direction">Selected compare direction.</param>
    /// <param name="loadedDecks">Loaded decks for both sides of the compare.</param>
    public static List<DeckEntry> GetSourceEntries(SyncDirection direction, LoadedDecks loadedDecks)
        => direction switch
        {
            SyncDirection.MoxfieldToArchidekt or SyncDirection.MoxfieldToMoxfield => loadedDecks.MoxfieldEntries,
            _ => loadedDecks.ArchidektEntries,
        };

    /// <summary>
    /// Returns the entries that should be treated as the target deck.
    /// </summary>
    /// <param name="direction">Selected compare direction.</param>
    /// <param name="loadedDecks">Loaded decks for both sides of the compare.</param>
    public static List<DeckEntry> GetTargetEntries(SyncDirection direction, LoadedDecks loadedDecks)
        => direction switch
        {
            SyncDirection.MoxfieldToArchidekt or SyncDirection.MoxfieldToMoxfield => loadedDecks.ArchidektEntries,
            _ => loadedDecks.MoxfieldEntries,
        };

    /// <summary>
    /// Gets the display name for the source system in the selected direction.
    /// </summary>
    /// <param name="direction">Selected compare direction.</param>
    public static string GetSourceSystem(SyncDirection direction)
        => direction switch
        {
            SyncDirection.MoxfieldToArchidekt or SyncDirection.MoxfieldToMoxfield => "Moxfield",
            _ => "Archidekt",
        };

    /// <summary>
    /// Gets the display name for the target system in the selected direction.
    /// </summary>
    /// <param name="direction">Selected compare direction.</param>
    public static string GetTargetSystem(SyncDirection direction)
        => direction switch
        {
            SyncDirection.MoxfieldToArchidekt => "Archidekt",
            SyncDirection.ArchidektToMoxfield => "Moxfield",
            SyncDirection.MoxfieldToMoxfield => "Moxfield",
            SyncDirection.ArchidektToArchidekt => "Archidekt",
            _ => "Archidekt",
        };

    /// <summary>
    /// Gets the system shown in the left-hand panel of the compare UI.
    /// </summary>
    /// <param name="direction">Selected compare direction.</param>
    public static string GetLeftPanelSystem(SyncDirection direction)
        => direction switch
        {
            SyncDirection.ArchidektToArchidekt => "Archidekt",
            _ => "Moxfield",
        };

    /// <summary>
    /// Gets the system shown in the right-hand panel of the compare UI.
    /// </summary>
    /// <param name="direction">Selected compare direction.</param>
    public static string GetRightPanelSystem(SyncDirection direction)
        => direction switch
        {
            SyncDirection.MoxfieldToMoxfield => "Moxfield",
            _ => "Archidekt",
        };

    /// <summary>
    /// Indicates whether the left-hand panel is also the source side for export decisions.
    /// </summary>
    /// <param name="direction">Selected compare direction.</param>
    public static bool IsLeftPanelSource(SyncDirection direction)
        => direction is SyncDirection.MoxfieldToArchidekt or SyncDirection.MoxfieldToMoxfield;
}
