using DeckFlow.Core.Knowledge;

namespace DeckFlow.Core.Knowledge.StatedRulesExtraction;

/// <summary>
/// Deterministic content type classifier using existing transcript distillation signals.
/// </summary>
// Why (WR-09, maintainer decision 2026-09-06 per WAITING.json): no production caller classifies
// content type yet - this is the intended classifier for a future stated-rules content-type
// gate, exercised today only by DeckFlow.Core.Tests. Kept rather than deleted; see
// ai-context-deckflow/repos/deckflow/notes/2026-09-06-cycle20-branch-divergence.md.
public static class ContentTypeHeuristic
{
    /// <summary>Deckbuilding theory content not anchored to a specific archetype.</summary>
    public const string DeckbuildingTheory = "deckbuilding-theory";

    /// <summary>Deck-tech content grounded in a concrete archetype and card-category discussion.</summary>
    public const string DeckTech = "deck-tech";

    /// <summary>Meta-commentary content with no card-category signal.</summary>
    public const string MetaCommentary = "meta-commentary";

    /// <summary>Gameplay-focused content identified from clip language rather than deckbuilding tags.</summary>
    public const string Gameplay = "gameplay";

    private static readonly string[] GameplayKeywords =
    [
        "on turn",
        "my opponent",
        "i cast",
        "combat",
        "attack",
        "block",
        "priority",
        "mulligan to",
        "keep this hand",
        "line of play",
    ];

    internal const int GameplayKeywordThreshold = 2;

    /// <summary>
    /// Classifies a distilled transcript into one locked content-type bucket using existing signals only.
    /// </summary>
    public static string Classify(
        IReadOnlyList<string> archetypeTags,
        IReadOnlyList<string> cardCategoryTags,
        IReadOnlyList<ClipItem> clips)
    {
        ArgumentNullException.ThrowIfNull(archetypeTags);
        ArgumentNullException.ThrowIfNull(cardCategoryTags);
        ArgumentNullException.ThrowIfNull(clips);

        // Why: gameplay is checked first so all four buckets remain reachable, this intentionally
        // ignores classification verdict text because only keep-videos reach this point, and the
        // keyword scan counts distinct literals so one repeated phrase cannot false-positive.
        if (CountDistinctGameplayKeywords(clips) >= GameplayKeywordThreshold)
        {
            return Gameplay;
        }

        if (cardCategoryTags.Count == 0)
        {
            return MetaCommentary;
        }

        if (archetypeTags.Count >= 1)
        {
            return DeckTech;
        }

        return DeckbuildingTheory;
    }

    private static int CountDistinctGameplayKeywords(IReadOnlyList<ClipItem> clips)
    {
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ClipItem clip in clips)
        {
            if (string.IsNullOrWhiteSpace(clip.Excerpt))
            {
                continue;
            }

            foreach (string keyword in GameplayKeywords)
            {
                if (clip.Excerpt.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(keyword);
                }
            }
        }

        return matched.Count;
    }
}
