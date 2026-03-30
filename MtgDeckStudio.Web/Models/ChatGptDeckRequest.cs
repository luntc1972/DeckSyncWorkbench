namespace MtgDeckStudio.Web.Models;

public sealed class ChatGptDeckRequest
{
    private string _deckSource = string.Empty;
    private string _format = "Commander";
    private string _deckName = string.Empty;
    private string _strategyNotes = string.Empty;
    private string _metaNotes = string.Empty;
    private string _probeResponseJson = string.Empty;
    private string _deckProfileJson = string.Empty;
    private string _targetCommanderBracket = string.Empty;
    private List<string> _selectedAnalysisQuestions = [];
    private string _cardSpecificQuestionCardName = string.Empty;
    private List<string> _selectedSetCodes = [];
    private string _setPacketText = string.Empty;

    public string DeckSource
    {
        get => _deckSource;
        set => _deckSource = value ?? string.Empty;
    }

    public int WorkflowStep { get; set; } = 1;

    public bool SaveArtifactsToDisk { get; set; }

    public string Format
    {
        get => _format;
        set => _format = value ?? "Commander";
    }

    public string DeckName
    {
        get => _deckName;
        set => _deckName = value ?? string.Empty;
    }

    public string StrategyNotes
    {
        get => _strategyNotes;
        set => _strategyNotes = value ?? string.Empty;
    }

    public string MetaNotes
    {
        get => _metaNotes;
        set => _metaNotes = value ?? string.Empty;
    }

    public string ProbeResponseJson
    {
        get => _probeResponseJson;
        set => _probeResponseJson = value ?? string.Empty;
    }

    public string DeckProfileJson
    {
        get => _deckProfileJson;
        set => _deckProfileJson = value ?? string.Empty;
    }

    public string TargetCommanderBracket
    {
        get => _targetCommanderBracket;
        set => _targetCommanderBracket = value ?? string.Empty;
    }

    public List<string> SelectedAnalysisQuestions
    {
        get => _selectedAnalysisQuestions;
        set => _selectedAnalysisQuestions = value ?? [];
    }

    public string CardSpecificQuestionCardName
    {
        get => _cardSpecificQuestionCardName;
        set => _cardSpecificQuestionCardName = value ?? string.Empty;
    }

    public List<string> SelectedSetCodes
    {
        get => _selectedSetCodes;
        set => _selectedSetCodes = value ?? [];
    }

    public string SetPacketText
    {
        get => _setPacketText;
        set => _setPacketText = value ?? string.Empty;
    }
}
