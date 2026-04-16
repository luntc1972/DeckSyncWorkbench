namespace MtgDeckStudio.Web.Models;

public sealed class ChatGptDeckRequest
{
    private string _deckSource = string.Empty;
    private string _format = "Commander";
    private string _deckName = string.Empty;
    private string _strategyNotes = string.Empty;
    private string _metaNotes = string.Empty;
    private string _deckProfileJson = string.Empty;
    private string _targetCommanderBracket = string.Empty;
    private List<string> _selectedAnalysisQuestions = [];
    private string _cardSpecificQuestionCardName = string.Empty;
    private string _budgetUpgradeAmount = string.Empty;
    private List<string> _selectedSetCodes = [];
    private string _setPacketText = string.Empty;
    private string _protectedCards = string.Empty;
    private string _decklistExportFormat = string.Empty;
    private string _preferredCategories = string.Empty;

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

    public string BudgetUpgradeAmount
    {
        get => _budgetUpgradeAmount;
        set => _budgetUpgradeAmount = value ?? string.Empty;
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

    public string ProtectedCards
    {
        get => _protectedCards;
        set => _protectedCards = value ?? string.Empty;
    }

    public string DecklistExportFormat
    {
        get => _decklistExportFormat;
        set => _decklistExportFormat = value ?? string.Empty;
    }

    public bool IncludeCardVersions { get; set; }

    public bool IncludeSideboardInAnalysis { get; set; }

    public bool IncludeMaybeboardInAnalysis { get; set; }

    public string PreferredCategories
    {
        get => _preferredCategories;
        set => _preferredCategories = value ?? string.Empty;
    }

    private string _freeformQuestion = string.Empty;

    public string FreeformQuestion
    {
        get => _freeformQuestion;
        set => _freeformQuestion = value ?? string.Empty;
    }

    private string _setUpgradeFocus = string.Empty;

    /// <summary>
    /// Controls the focus of the set-upgrade prompt: "lateral-moves", "strict-upgrades", or empty (default: best additions).
    /// </summary>
    public string SetUpgradeFocus
    {
        get => _setUpgradeFocus;
        set => _setUpgradeFocus = value ?? string.Empty;
    }

    private string _setUpgradeResponseJson = string.Empty;

    public string SetUpgradeResponseJson
    {
        get => _setUpgradeResponseJson;
        set => _setUpgradeResponseJson = value ?? string.Empty;
    }

    private string _importArtifactsPath = string.Empty;

    /// <summary>
    /// Absolute or relative path to a previously saved ChatGPT Analysis artifact folder.
    /// When set, the service rehydrates DeckProfileJson and SetUpgradeResponseJson from the folder's JSON files.
    /// </summary>
    public string ImportArtifactsPath
    {
        get => _importArtifactsPath;
        set => _importArtifactsPath = value ?? string.Empty;
    }
}
