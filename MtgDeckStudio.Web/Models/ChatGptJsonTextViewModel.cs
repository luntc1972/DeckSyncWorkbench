namespace MtgDeckStudio.Web.Models;

public sealed class ChatGptJsonTextViewModel
{
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.ChatGptJsonToText;

    public ChatGptJsonTextRequest Request { get; init; } = new();

    public string? ErrorMessage { get; init; }

    public string? FormattedText { get; init; }

    public string? PrettyJson { get; init; }
}
