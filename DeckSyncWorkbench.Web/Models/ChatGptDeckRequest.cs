namespace DeckSyncWorkbench.Web.Models;

public sealed class ChatGptDeckRequest
{
    public string DeckSource { get; set; } = string.Empty;

    public int WorkflowStep { get; set; } = 1;

    public bool SaveArtifactsToDisk { get; set; }

    public string Format { get; set; } = "Commander";

    public string DeckName { get; set; } = string.Empty;

    public string StrategyNotes { get; set; } = string.Empty;

    public string MetaNotes { get; set; } = string.Empty;

    public string ProbeResponseJson { get; set; } = string.Empty;

    public string DeckProfileJson { get; set; } = string.Empty;

    public string TargetCommanderBracket { get; set; } = string.Empty;

    public List<string> SelectedAnalysisQuestions { get; set; } = [];

    public string CardSpecificQuestionCardName { get; set; } = string.Empty;

    public string SetName { get; set; } = string.Empty;

    public List<string> SelectedSetCodes { get; set; } = [];

    public string SetPacketText { get; set; } = string.Empty;
}
