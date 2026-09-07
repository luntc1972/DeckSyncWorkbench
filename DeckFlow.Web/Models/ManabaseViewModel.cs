using DeckFlow.Core.Manabase;

namespace DeckFlow.Web.Models;

/// <summary>
/// View model for the mana-base page: the form request, an optional computed report, and
/// presentation extras (error, summary, unresolved cards, ChatGPT swap prompt).
/// </summary>
public sealed class ManabaseViewModel
{
    /// <summary>The active deck-tool tab (always <see cref="DeckPageTab.Manabase"/>).</summary>
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.Manabase;

    /// <summary>The form-bound request, re-rendered so inputs persist across the postback.</summary>
    public ManabaseRequest Request { get; init; } = new();

    /// <summary>User-facing error message, or null when the request succeeded.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>User-facing informational notice, or null when none applies.</summary>
    public string? NoticeMessage { get; init; }

    /// <summary>The computed report, or null before a successful analysis.</summary>
    public ManabaseReport? Report { get; init; }

    /// <summary>Short summary of what was analyzed (card/land counts).</summary>
    public string? InputSummary { get; init; }

    /// <summary>Card names Scryfall could not resolve (excluded from the math).</summary>
    public IReadOnlyList<string> Unresolved { get; init; } = Array.Empty<string>();

    /// <summary>Optional importer notice (e.g. a Moxfield fallback path was used).</summary>
    public string? ImportWarning { get; init; }

    /// <summary>Whether the page should render the commander picker instead of a report.</summary>
    public bool CommanderSelectionRequired { get; init; }

    /// <summary>The deck's eligible commander choices surfaced in the picker.</summary>
    public IReadOnlyList<string> CommanderChoices { get; init; } = Array.Empty<string>();

    /// <summary>Paste-ready prompt asking an LLM for specific land swaps.</summary>
    public string? PromptSwapPrompt { get; init; }

    /// <summary>Auto-detected alt/reduced-cost suggestions used to pre-populate the override box.</summary>
    public IReadOnlyList<CostSuggestion> Suggestions { get; init; } = Array.Empty<CostSuggestion>();

    /// <summary>Optional synthesized plain-language verdict for the analyzed deck.</summary>
    public ManabaseVerdict? PlainLanguageVerdict { get; init; }

    /// <summary>Optional ramp/draw slot-budget advisory for Casual-mode verdicts.</summary>
    public ManabaseRampDrawBudget? RampDrawBudget { get; init; }

    /// <summary>Whether the UI should surface the plain-language glossary/verdict affordances.</summary>
    public bool ShowPlainLanguage { get; init; }

    /// <summary>Whether the UI should surface the command-zone castability affordances.</summary>
    public bool ShowCommanderCastability { get; init; }

    /// <summary>Whether the UI should surface the tap-analyzer card and its paste-artifact section.</summary>
    public bool ShowTapAnalyzer { get; init; }

    /// <summary>Whether the UI should surface the opening-hand/mulligan lens card and its paste-artifact section.</summary>
    public bool ShowMulliganEval { get; init; }

    /// <summary>Whether the UI should surface the "with a plan" plan-presence line inside the opening-hand block.</summary>
    public bool ShowPlanPresence { get; init; }

    /// <summary>Whether the UI should surface the cEDH keep-shape and casual curve-coverage reads.</summary>
    public bool ShowKeepShapes { get; init; }

    /// <summary>Whether the UI should surface the Focused deck-type pill.</summary>
    public bool ShowFocusedTier { get; init; }

    /// <summary>Whether the UI should surface the display-only mana-source disclosure sections.</summary>
    public bool ShowSourceList { get; init; }

    /// <summary>Whether the UI should surface the cEDH early-interaction lens and related table rows.</summary>
    public bool ShowCedhInteractionLens { get; init; }

    /// <summary>Optional companion castability row surfaced outside the 99 table.</summary>
    public CardCastability? CompanionCallout { get; init; }

    /// <summary>Optional empirical community land baseline (present only on a successful flag-on analysis).</summary>
    public ManabaseCommunityBaseline? CommunityBaseline { get; init; }

    /// <summary>Whether the community-baseline flag is on for this request (drives the bracket selector, even pre-analysis).</summary>
    public bool ShowCommunityBaseline { get; init; }

    /// <summary>
    /// Override card names / lines that were NOT applied to the analysis: syntactically bad lines and
    /// valid lines whose card name matched nothing in the deck (a typo or a card not in the list).
    /// Surfaced so a dropped override is never silent. Empty when every line applied.
    /// </summary>
    public IReadOnlyList<string> NotAppliedOverrides { get; init; } = Array.Empty<string>();

    /// <summary>True when at least one override line was not applied and should be surfaced.</summary>
    public bool HasNotAppliedOverrides => NotAppliedOverrides.Count > 0;

    /// <summary>The detected suggestions rendered as override-box lines (<c>Name: cost</c>).</summary>
    public string SuggestedOverridesText =>
        string.Join("\n", Suggestions.Select(s => $"{s.Name}: {s.EffectiveCost}"));

    /// <summary>
    /// What to show in the override box. Once the user has touched the box
    /// (<see cref="ManabaseRequest.OverridesTouched"/>), their text is echoed verbatim — including a
    /// deliberately cleared (empty) box, so rejecting the suggestions sticks instead of silently
    /// refilling. Until then, an empty box pre-fills with the detected suggestions
    /// (preserve-vs-prepopulate).
    /// </summary>
    public string OverridesBoxText =>
        !Request.OverridesTouched && string.IsNullOrWhiteSpace(Request.CostOverridesText)
            ? SuggestedOverridesText
            : Request.CostOverridesText;

    /// <summary>True when there is at least one detected suggestion to surface to the user.</summary>
    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>True when a report is present and should be rendered.</summary>
    public bool HasResult => Report is not null;

    /// <summary>
    /// True after the "Load deck" step resolved the deck and detected cost suggestions, but before a
    /// full analysis ran. Drives the review-then-analyze hint.
    /// </summary>
    public bool Loaded { get; init; }

    /// <summary>
    /// True when the castability table should render: a report exists, it carries at least one
    /// castability row, and either the run was non-cEDH or it was cEDH with the interaction-lens
    /// surface enabled.
    /// </summary>
    public bool ShowCastability =>
        Report is { Castability.Count: > 0 } report
        && (report.Mode != ManabaseMode.Cedh
            || (report.Mode == ManabaseMode.Cedh && ShowCedhInteractionLens));
}
