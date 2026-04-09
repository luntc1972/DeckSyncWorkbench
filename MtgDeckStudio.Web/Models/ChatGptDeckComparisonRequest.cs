namespace MtgDeckStudio.Web.Models;

public sealed class ChatGptDeckComparisonRequest
{
    private string _deckASource = string.Empty;
    private string _deckBSource = string.Empty;
    private string _deckAName = string.Empty;
    private string _deckBName = string.Empty;
    private string _deckABracket = string.Empty;
    private string _deckBBracket = string.Empty;
    private string _comparisonResponseJson = string.Empty;

    public int WorkflowStep { get; set; } = 1;

    public bool SaveArtifactsToDisk { get; set; }

    public string DeckASource
    {
        get => _deckASource;
        set => _deckASource = value ?? string.Empty;
    }

    public string DeckBSource
    {
        get => _deckBSource;
        set => _deckBSource = value ?? string.Empty;
    }

    public string DeckAName
    {
        get => _deckAName;
        set => _deckAName = value ?? string.Empty;
    }

    public string DeckBName
    {
        get => _deckBName;
        set => _deckBName = value ?? string.Empty;
    }

    public string DeckABracket
    {
        get => _deckABracket;
        set => _deckABracket = value ?? string.Empty;
    }

    public string DeckBBracket
    {
        get => _deckBBracket;
        set => _deckBBracket = value ?? string.Empty;
    }

    public string ComparisonResponseJson
    {
        get => _comparisonResponseJson;
        set => _comparisonResponseJson = value ?? string.Empty;
    }
}
