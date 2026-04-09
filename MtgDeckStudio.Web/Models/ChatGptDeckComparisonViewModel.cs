namespace MtgDeckStudio.Web.Models;

public sealed class ChatGptDeckComparisonViewModel
{
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.ChatGptDeckComparison;

    public ChatGptDeckComparisonRequest Request { get; init; } = new();

    public string? ErrorMessage { get; init; }

    public string? InputSummary { get; init; }

    public string? DeckAListText { get; init; }

    public string? DeckBListText { get; init; }

    public string? DeckAComboText { get; init; }

    public string? DeckBComboText { get; init; }

    public string? ComparisonContextText { get; init; }

    public string? ComparisonPromptText { get; init; }

    public string? FollowUpPromptText { get; init; }

    public string? ComparisonSchemaJson { get; init; }

    public ChatGptDeckComparisonResponse? ComparisonResponse { get; init; }

    public string? SavedArtifactsDirectory { get; init; }

    public string? TimingSummary { get; init; }
}
