namespace MtgDeckStudio.Web.Models;

public sealed class ChatGptJsonTextRequest
{
    private string _jsonInput = string.Empty;

    public string JsonInput
    {
        get => _jsonInput;
        set => _jsonInput = value ?? string.Empty;
    }
}
