namespace MtgDeckStudio.Web.Models;

public sealed class DeckConvertRequest
{
    public string SourceFormat { get; set; } = "Moxfield";
    public DeckInputSource InputSource { get; set; } = DeckInputSource.PasteText;
    public string DeckUrl { get; set; } = string.Empty;
    public string DeckText { get; set; } = string.Empty;
    public string TargetFormat { get; set; } = "Archidekt";
    public string? CommanderOverride { get; set; }
}
