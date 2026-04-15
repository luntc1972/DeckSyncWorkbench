using System.Text.Json.Serialization;

namespace MtgDeckStudio.Web.Models;

public sealed class ChatGptCedhMetaGapResponse
{
    [JsonPropertyName("meta_gap")]
    public ChatGptCedhMetaGapData MetaGap { get; init; } = new();
}

public sealed class ChatGptCedhMetaGapData
{
    [JsonPropertyName("commander")]
    public string Commander { get; init; } = string.Empty;

    [JsonPropertyName("color_id")]
    public string ColorId { get; init; } = string.Empty;

    [JsonPropertyName("ref_deck_count")]
    public int RefDeckCount { get; init; }

    [JsonPropertyName("readiness_score")]
    public int ReadinessScore { get; init; }

    [JsonPropertyName("readiness_justification")]
    public string ReadinessJustification { get; init; } = string.Empty;

    [JsonPropertyName("win_lines")]
    public ChatGptCedhWinLines? WinLines { get; init; }

    [JsonPropertyName("interaction")]
    public ChatGptCedhInteraction? Interaction { get; init; }

    [JsonPropertyName("speed")]
    public ChatGptCedhSpeed? Speed { get; init; }

    [JsonPropertyName("mana_efficiency")]
    public ChatGptCedhManaEfficiency? ManaEfficiency { get; init; }

    [JsonPropertyName("core_convergence")]
    public IReadOnlyList<ChatGptCedhCoreConvergenceCard> CoreConvergence { get; init; } = Array.Empty<ChatGptCedhCoreConvergenceCard>();

    [JsonPropertyName("missing_staples")]
    public IReadOnlyList<ChatGptCedhMissingStaple> MissingStaples { get; init; } = Array.Empty<ChatGptCedhMissingStaple>();

    [JsonPropertyName("potential_cuts")]
    public IReadOnlyList<ChatGptCedhPotentialCut> PotentialCuts { get; init; } = Array.Empty<ChatGptCedhPotentialCut>();

    [JsonPropertyName("top_10_adds")]
    public IReadOnlyList<ChatGptCedhTopAdd> Top10Adds { get; init; } = Array.Empty<ChatGptCedhTopAdd>();

    [JsonPropertyName("top_10_cuts")]
    public IReadOnlyList<ChatGptCedhTopCut> Top10Cuts { get; init; } = Array.Empty<ChatGptCedhTopCut>();

    [JsonPropertyName("meta_summary")]
    public string MetaSummary { get; init; } = string.Empty;

    [JsonPropertyName("optimization_path")]
    public string OptimizationPath { get; init; } = string.Empty;
}

public sealed class ChatGptCedhWinLineSet
{
    [JsonPropertyName("primary")]
    public string Primary { get; init; } = string.Empty;

    [JsonPropertyName("backup")]
    public string Backup { get; init; } = string.Empty;
}

public sealed class ChatGptCedhWinLines
{
    [JsonPropertyName("my_deck")]
    public ChatGptCedhWinLineSet? MyDeck { get; init; }

    [JsonPropertyName("ref_consensus")]
    public ChatGptCedhWinLineSet? RefConsensus { get; init; }

    [JsonPropertyName("missing_lines")]
    public IReadOnlyList<string> MissingLines { get; init; } = Array.Empty<string>();
}

public sealed class ChatGptCedhInteraction
{
    [JsonPropertyName("my_count")]
    public int MyCount { get; init; }

    [JsonPropertyName("ref_avg_count")]
    public double RefAvgCount { get; init; }

    [JsonPropertyName("verdict")]
    public string Verdict { get; init; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;
}

public sealed class ChatGptCedhSpeed
{
    [JsonPropertyName("my_classification")]
    public string MyClassification { get; init; } = string.Empty;

    [JsonPropertyName("my_avg_turn")]
    public string MyAvgTurn { get; init; } = string.Empty;

    [JsonPropertyName("ref_classification")]
    public string RefClassification { get; init; } = string.Empty;

    [JsonPropertyName("ref_avg_turn")]
    public string RefAvgTurn { get; init; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;
}

public sealed class ChatGptCedhManaEfficiency
{
    [JsonPropertyName("my_fast_mana")]
    public int MyFastMana { get; init; }

    [JsonPropertyName("ref_avg_fast_mana")]
    public double RefAvgFastMana { get; init; }

    [JsonPropertyName("my_avg_cmc")]
    public double MyAvgCmc { get; init; }

    [JsonPropertyName("ref_avg_cmc")]
    public double RefAvgCmc { get; init; }

    [JsonPropertyName("my_lands")]
    public int MyLands { get; init; }

    [JsonPropertyName("ref_avg_lands")]
    public double RefAvgLands { get; init; }

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;
}

public sealed class ChatGptCedhCoreConvergenceCard
{
    [JsonPropertyName("card")]
    public string Card { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("in_my_deck")]
    public bool InMyDeck { get; init; }
}

public sealed class ChatGptCedhMissingStaple
{
    [JsonPropertyName("card")]
    public string Card { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("ref_count")]
    public int RefCount { get; init; }

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("why")]
    public string Why { get; init; } = string.Empty;
}

public sealed class ChatGptCedhPotentialCut
{
    [JsonPropertyName("card")]
    public string Card { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("ref_count")]
    public int RefCount { get; init; }

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("why")]
    public string Why { get; init; } = string.Empty;
}

public sealed class ChatGptCedhTopAdd
{
    [JsonPropertyName("card")]
    public string Card { get; init; } = string.Empty;

    [JsonPropertyName("replaces")]
    public string Replaces { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("why")]
    public string Why { get; init; } = string.Empty;
}

public sealed class ChatGptCedhTopCut
{
    [JsonPropertyName("card")]
    public string Card { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("why")]
    public string Why { get; init; } = string.Empty;
}
