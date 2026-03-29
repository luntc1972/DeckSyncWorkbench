namespace DeckSyncWorkbench.Web.Models;

public sealed class ChatGptDeckViewModel
{
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.ChatGptPackets;

    public ChatGptDeckRequest Request { get; init; } = new();

    public IReadOnlyList<ScryfallSetOption> AvailableSets { get; init; } = Array.Empty<ScryfallSetOption>();

    public string? ErrorMessage { get; init; }

    public string? InputSummary { get; init; }

    public string? ProbePromptText { get; init; }

    public string? ProbeResponseSchemaJson { get; init; }

    public string? ReferenceText { get; init; }

    public string? AnalysisPromptText { get; init; }

    public string? DeckProfileSchemaJson { get; init; }

    public string? SetUpgradePromptText { get; init; }

    public string? SavedArtifactsDirectory { get; init; }
}
