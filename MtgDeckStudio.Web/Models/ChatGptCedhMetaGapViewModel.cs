namespace MtgDeckStudio.Web.Models;

public sealed class ChatGptCedhMetaGapViewModel
{
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.ChatGptCedhMetaGap;

    public ChatGptCedhMetaGapRequest Request { get; init; } = new();

    public string? ErrorMessage { get; init; }

    public string? InputSummary { get; init; }

    public string? ResolvedCommanderName { get; init; }

    public string? PromptText { get; init; }

    public string? SchemaJson { get; init; }

    public IReadOnlyList<EdhTop16Entry> FetchedEntries { get; init; } = Array.Empty<EdhTop16Entry>();

    public ChatGptCedhMetaGapResponse? AnalysisResponse { get; init; }

    public string? SavedArtifactsDirectory { get; init; }
}
