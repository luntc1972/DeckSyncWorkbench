namespace MtgDeckStudio.Core.Models;

/// <summary>
/// Determines how category/tag data is combined when exporting decks.
/// </summary>
public enum CategorySyncMode
{
    /// <summary>
    /// Uses the categories defined by the target system (Archidekt categories when targeting Archidekt, or source tags when targeting Moxfield).
    /// </summary>
    TargetCategories,

    /// <summary>
    /// Always uses the source tags/categories (the deck you are syncing from) in the export.
    /// </summary>
    SourceTags,

    /// <summary>
    /// Combines both target categories and source tags, avoiding duplicates.
    /// </summary>
    Combined,
}
