using DeckSyncWorkbench.Core.Models;

namespace DeckSyncWorkbench.Web.Models;

public sealed class DeckDiffRequest
{
    public SyncDirection Direction { get; set; } = SyncDirection.DeckSyncWorkbench;

    public DeckInputSource MoxfieldInputSource { get; set; } = DeckInputSource.PasteText;

    public string MoxfieldUrl { get; set; } = string.Empty;

    public string MoxfieldText { get; set; } = string.Empty;

    public DeckInputSource ArchidektInputSource { get; set; } = DeckInputSource.PasteText;

    public string ArchidektUrl { get; set; } = string.Empty;

    public string ArchidektText { get; set; } = string.Empty;

    public MatchMode Mode { get; set; } = MatchMode.Loose;

    public Dictionary<string, PrintingChoice> Resolutions { get; set; } = new(StringComparer.Ordinal);
}
