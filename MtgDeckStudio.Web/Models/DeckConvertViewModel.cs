namespace MtgDeckStudio.Web.Models;

public sealed class DeckConvertViewModel
{
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.Convert;
    public DeckConvertRequest Request { get; init; } = new();
    public string? ConvertedText { get; init; }
    public string? ErrorMessage { get; init; }
    public bool MissingCommander { get; init; }
}
