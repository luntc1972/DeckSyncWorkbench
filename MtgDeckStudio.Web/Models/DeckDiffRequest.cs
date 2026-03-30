using MtgDeckStudio.Core.Models;

namespace MtgDeckStudio.Web.Models;

public sealed class DeckDiffRequest
{
    public SyncDirection Direction { get; set; } = SyncDirection.MoxfieldToArchidekt;

    public DeckInputSource MoxfieldInputSource { get; set; } = DeckInputSource.PasteText;

    public string MoxfieldUrl { get; set; } = string.Empty;

    public string MoxfieldText { get; set; } = string.Empty;

    public DeckInputSource ArchidektInputSource { get; set; } = DeckInputSource.PasteText;

    public string ArchidektUrl { get; set; } = string.Empty;

    public string ArchidektText { get; set; } = string.Empty;

    public MatchMode Mode { get; set; } = MatchMode.Loose;

    /// <summary>
    /// Controls how category/tag data is used when producing exports.
    /// </summary>
    public CategorySyncMode CategorySyncMode { get; set; } = CategorySyncMode.TargetCategories;

    public Dictionary<string, PrintingChoice> Resolutions { get; set; } = new(StringComparer.Ordinal);
}
