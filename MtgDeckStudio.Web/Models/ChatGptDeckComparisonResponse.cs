using System.Text.Json.Serialization;

namespace MtgDeckStudio.Web.Models;

public sealed class ChatGptDeckComparisonResponse
{
    [JsonPropertyName("deck_a_name")]
    public string DeckAName { get; init; } = string.Empty;

    [JsonPropertyName("deck_b_name")]
    public string DeckBName { get; init; } = string.Empty;

    [JsonPropertyName("deck_a_commander")]
    public string DeckACommander { get; init; } = string.Empty;

    [JsonPropertyName("deck_b_commander")]
    public string DeckBCommander { get; init; } = string.Empty;

    [JsonPropertyName("deck_a_gameplan")]
    public string DeckAGameplan { get; init; } = string.Empty;

    [JsonPropertyName("deck_b_gameplan")]
    public string DeckBGameplan { get; init; } = string.Empty;

    [JsonPropertyName("deck_a_bracket")]
    public string DeckABracket { get; init; } = string.Empty;

    [JsonPropertyName("deck_b_bracket")]
    public string DeckBBracket { get; init; } = string.Empty;

    [JsonPropertyName("shared_themes")]
    public IReadOnlyList<string> SharedThemes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("major_differences")]
    public IReadOnlyList<string> MajorDifferences { get; init; } = Array.Empty<string>();

    [JsonPropertyName("deck_a_strengths")]
    public IReadOnlyList<string> DeckAStrengths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("deck_b_strengths")]
    public IReadOnlyList<string> DeckBStrengths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("deck_a_weaknesses")]
    public IReadOnlyList<string> DeckAWeaknesses { get; init; } = Array.Empty<string>();

    [JsonPropertyName("deck_b_weaknesses")]
    public IReadOnlyList<string> DeckBWeaknesses { get; init; } = Array.Empty<string>();

    [JsonPropertyName("speed_comparison")]
    public string SpeedComparison { get; init; } = string.Empty;

    [JsonPropertyName("resilience_comparison")]
    public string ResilienceComparison { get; init; } = string.Empty;

    [JsonPropertyName("interaction_comparison")]
    public string InteractionComparison { get; init; } = string.Empty;

    [JsonPropertyName("mana_consistency_comparison")]
    public string ManaConsistencyComparison { get; init; } = string.Empty;

    [JsonPropertyName("closing_power_comparison")]
    public string ClosingPowerComparison { get; init; } = string.Empty;

    [JsonPropertyName("combo_comparison")]
    public string ComboComparison { get; init; } = string.Empty;

    [JsonPropertyName("overall_verdict")]
    public string OverallVerdict { get; init; } = string.Empty;

    [JsonPropertyName("key_gap_cards_or_packages")]
    public IReadOnlyList<string> KeyGapCardsOrPackages { get; init; } = Array.Empty<string>();

    [JsonPropertyName("deck_a_key_combos")]
    public IReadOnlyList<string> DeckAKeyCombos { get; init; } = Array.Empty<string>();

    [JsonPropertyName("deck_b_key_combos")]
    public IReadOnlyList<string> DeckBKeyCombos { get; init; } = Array.Empty<string>();

    [JsonPropertyName("recommended_for")]
    public ChatGptDeckComparisonRecommendation RecommendedFor { get; init; } = new();

    [JsonPropertyName("confidence_notes")]
    public IReadOnlyList<string> ConfidenceNotes { get; init; } = Array.Empty<string>();
}

public sealed class ChatGptDeckComparisonRecommendation
{
    [JsonPropertyName("deck_a")]
    public IReadOnlyList<string> DeckA { get; init; } = Array.Empty<string>();

    [JsonPropertyName("deck_b")]
    public IReadOnlyList<string> DeckB { get; init; } = Array.Empty<string>();
}
