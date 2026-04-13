using System.Text.Json.Serialization;

namespace MtgDeckStudio.Web.Models;

public sealed class ChatGptDeckAnalysisResponse
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = string.Empty;

    [JsonPropertyName("commander")]
    public string Commander { get; init; } = string.Empty;

    [JsonPropertyName("game_plan")]
    public string GamePlan { get; init; } = string.Empty;

    [JsonPropertyName("primary_axes")]
    public IReadOnlyList<string> PrimaryAxes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("speed")]
    public string Speed { get; init; } = string.Empty;

    [JsonPropertyName("strengths")]
    public IReadOnlyList<string> Strengths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("weaknesses")]
    public IReadOnlyList<string> Weaknesses { get; init; } = Array.Empty<string>();

    [JsonPropertyName("deck_needs")]
    public IReadOnlyList<string> DeckNeeds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("weak_slots")]
    public IReadOnlyList<ChatGptWeakSlot> WeakSlots { get; init; } = Array.Empty<ChatGptWeakSlot>();

    [JsonPropertyName("synergy_tags")]
    public IReadOnlyList<string> SynergyTags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("question_answers")]
    public IReadOnlyList<ChatGptQuestionAnswer> QuestionAnswers { get; init; } = Array.Empty<ChatGptQuestionAnswer>();

    [JsonPropertyName("deck_versions")]
    public IReadOnlyList<ChatGptDeckVersion> DeckVersions { get; init; } = Array.Empty<ChatGptDeckVersion>();
}

public sealed class ChatGptWeakSlot
{
    [JsonPropertyName("card")]
    public string Card { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}

public sealed class ChatGptQuestionAnswer
{
    [JsonPropertyName("question_number")]
    public int QuestionNumber { get; init; }

    [JsonPropertyName("question")]
    public string Question { get; init; } = string.Empty;

    [JsonPropertyName("answer")]
    public string Answer { get; init; } = string.Empty;

    [JsonPropertyName("basis")]
    public string Basis { get; init; } = string.Empty;
}

public sealed class ChatGptDeckVersion
{
    [JsonPropertyName("version_name")]
    public string VersionName { get; init; } = string.Empty;

    [JsonPropertyName("decklist")]
    public string Decklist { get; init; } = string.Empty;

    [JsonPropertyName("cards_added")]
    public IReadOnlyList<string> CardsAdded { get; init; } = Array.Empty<string>();

    [JsonPropertyName("cards_cut")]
    public IReadOnlyList<string> CardsCut { get; init; } = Array.Empty<string>();
}
