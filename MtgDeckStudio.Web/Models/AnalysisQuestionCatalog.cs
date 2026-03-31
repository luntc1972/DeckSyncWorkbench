namespace MtgDeckStudio.Web.Models;

/// <summary>
/// Represents a single selectable analysis question in the ChatGPT workflow.
/// </summary>
public sealed record AnalysisQuestionOption(
    string Id,
    string Text);

/// <summary>
/// Groups related analysis questions under a shared heading.
/// </summary>
public sealed record AnalysisQuestionBucket(
    string Id,
    string Label,
    IReadOnlyList<AnalysisQuestionOption> Questions);

/// <summary>
/// Provides the available analysis questions and helper methods for normalizing selections.
/// </summary>
public static class AnalysisQuestionCatalog
{
    /// <summary>
    /// Gets the ordered analysis buckets shown on the ChatGPT workflow page.
    /// </summary>
    public static IReadOnlyList<AnalysisQuestionBucket> Buckets { get; } =
    [
        new(
            "core-analysis",
            "Core Deck Analysis",
            [
                new("strengths-weaknesses", "What are the strengths and weaknesses of this deck?"),
                new("primary-win-condition", "What is this deck’s primary win condition?"),
                new("consistency", "How consistent is this deck?"),
                new("power-level", "What power level would you rate this deck (1–10)? Why?"),
                new("best-meta", "What kind of meta does this deck perform best in?")
            ]),
        new(
            "construction-balance",
            "Deck Construction & Balance",
            [
                new("mana-curve-balance", "Is my mana curve balanced?"),
                new("lands-and-ramp", "Do I have enough lands and ramp?"),
                new("card-draw-count", "How many card draw sources should I add/remove?"),
                new("interaction-count", "Am I running enough interaction (removal, counterspells, wipes)?"),
                new("underperformers", "What cards are underperforming or unnecessary?")
            ]),
        new(
            "strategy-synergy",
            "Strategy & Synergy",
            [
                new("key-synergies", "What are the key synergies in this deck?"),
                new("anti-synergies", "Are there any anti-synergies or conflicting strategies?"),
                new("commander-support", "How well does this deck support my commander?"),
                new("protect-cards", "What are the most important cards to protect?"),
                new("game-plan", "What’s the ideal game plan for early / mid / late game?")
            ]),
        new(
            "optimization-upgrades",
            "Optimization & Upgrades",
            [
                new("cuts-for-strength", "What cards should I cut to make this deck stronger?"),
                new("budget-upgrades", "What are the best upgrades under $X budget?"),
                new("missing-staples", "What staples am I missing?"),
                new("faster-competitive", "How can I make this deck faster / more competitive?"),
                new("resilience-to-wipes", "How can I make this deck more resilient to board wipes?")
            ]),
        new(
            "meta-matchups",
            "Meta & Matchups",
            [
                new("vs-archetypes", "How does this deck perform against aggro/control/combo?"),
                new("pod-weaknesses", "What are this deck’s biggest weaknesses in a typical pod?"),
                new("tech-options", "How can I tech against graveyard decks / artifacts / combo players?"),
                new("natural-hate-pieces", "What hate pieces fit naturally into this deck?")
            ]),
        new(
            "play-patterns",
            "Play Pattern & Decision Making",
            [
                new("ideal-opening-hand", "What’s an ideal opening hand for this deck?"),
                new("tutor-priorities", "What should I tutor for in most situations?"),
                new("cast-commander", "When should I cast my commander?"),
                new("common-misplays", "What are common misplays with this deck?")
            ]),
        new(
            "card-level",
            "Specific Card-Level Questions",
            [
                new("card-worth-it", "Is [card] worth including in this deck?"),
                new("better-alternatives", "What are better alternatives to [card]?"),
                new("weakest-card", "What’s the weakest card in this list?"),
                new("too-many-high-cmc", "Do I have too many high-CMC cards?")
            ]),
        new(
            "advanced",
            "Advanced / Expert-Level Questions",
            [
                new("turn-clock", "What is the deck’s effective turn clock?"),
                new("disruption-vulnerability", "How vulnerable is this deck to disruption?"),
                new("keepable-hands", "What percentage of hands are keepable?"),
                new("redundancy", "What is the deck’s redundancy for key effects?"),
                new("mana-base-optimization", "How optimized is the mana base?")
            ]),
        new(
            "deck-versioning",
            "Deck Versioning & Upgrade Paths",
            [
                new("bracket-3-version", "Create a Bracket 3 version of this deck."),
                new("bracket-4-version", "Create a Bracket 4 version of this deck."),
                new("bracket-5-version", "Create a Bracket 5 version of this deck."),
                new("three-upgrade-paths", "Create 3 different versions of this deck with different upgrade paths.")
            ])
    ];

    public static IReadOnlySet<string> FullDecklistQuestionIds { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bracket-3-version",
            "bracket-4-version",
            "bracket-5-version",
            "three-upgrade-paths"
        };

    public static IReadOnlyList<AnalysisQuestionOption> AllQuestions { get; } = Buckets
        .SelectMany(bucket => bucket.Questions)
        .ToList();

    /// <summary>
    /// Normalizes raw selected IDs by trimming, validating, deduplicating, and ordering them.
    /// </summary>
    /// <param name="selections">Raw selected question IDs.</param>
    public static IReadOnlyList<string> NormalizeSelections(IEnumerable<string>? selections)
    {
        var allowed = AllQuestions
            .Select(question => question.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (selections ?? Array.Empty<string>())
            .Where(selection => !string.IsNullOrWhiteSpace(selection))
            .Select(selection => selection.Trim())
            .Where(selection => allowed.Contains(selection))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(selection => selection, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Resolves selected IDs to their question text using default placeholders.
    /// </summary>
    /// <param name="selections">Selected question IDs.</param>
    public static IReadOnlyList<string> ResolveTexts(IEnumerable<string>? selections)
    {
        return ResolveTexts(selections, null, null);
    }

    /// <summary>
    /// Resolves selected IDs to their question text using a specific card name where needed.
    /// </summary>
    /// <param name="selections">Selected question IDs.</param>
    /// <param name="cardName">Card name for card-specific questions.</param>
    public static IReadOnlyList<string> ResolveTexts(IEnumerable<string>? selections, string? cardName)
    {
        return ResolveTexts(selections, cardName, null);
    }

    /// <summary>
    /// Resolves selected IDs to their question text using card-name and budget placeholders.
    /// </summary>
    /// <param name="selections">Selected question IDs.</param>
    /// <param name="cardName">Card name for card-specific questions.</param>
    /// <param name="budgetAmount">Budget amount for budget-upgrade questions.</param>
    public static IReadOnlyList<string> ResolveTexts(IEnumerable<string>? selections, string? cardName, string? budgetAmount)
    {
        var selectedSet = NormalizeSelections(selections)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedCardName = string.IsNullOrWhiteSpace(cardName) ? "[card]" : cardName.Trim();
        var normalizedBudgetAmount = string.IsNullOrWhiteSpace(budgetAmount) ? "$X" : $"${budgetAmount.Trim()}";

        return AllQuestions
            .Where(question => selectedSet.Contains(question.Id))
            .Select(question => question.Text
                .Replace("[card]", normalizedCardName, StringComparison.Ordinal)
                .Replace("$X", normalizedBudgetAmount, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Determines whether the selected questions require ChatGPT to return full 100-card decklists.
    /// </summary>
    /// <param name="selections">Selected question IDs.</param>
    public static bool RequiresFullDecklistOutput(IEnumerable<string>? selections) =>
        NormalizeSelections(selections).Any(selection => FullDecklistQuestionIds.Contains(selection));
}
