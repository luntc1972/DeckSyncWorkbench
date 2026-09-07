using DeckFlow.Core.Analysis;
using DeckFlow.Core.Manabase;

namespace DeckFlow.Web.Services.Manabase;

/// <summary>
/// Classifies a spell into its win-directed <see cref="PlanRole"/>s for the "plan presence" opener
/// read. Pure: the caller (<see cref="ManabaseAnalysisService"/>) does the I/O — it fetches each
/// card's crowd-sourced categories and the Commander Spellbook combo-piece set — and passes them in.
/// Source precedence is FIRST-HIT-WINS per the locked plan-presence decisions:
/// <list type="number">
/// <item>crowd categories (a card's Archidekt category tags → role);</item>
/// <item>Commander Spellbook combo piece → <see cref="PlanRole.TutorCombo"/>;</item>
/// <item>an oracle-text heuristic fallback (<see cref="DeckStatClassifier"/>).</item>
/// </list>
/// Ramp, lands, and filler card draw deliberately never earn a role — that is resource/velocity, a
/// different axis already measured by keepable-% and on-curve castability. The <see cref="ManabaseMode"/>
/// tunes one role: a pure counterspell counts as <see cref="PlanRole.Interaction"/> only in
/// <see cref="ManabaseMode.Cedh"/> (it protects the combo turn); in Casual a counter is reactive
/// insurance, not a card that advances the win plan, so it earns nothing, and the plural
/// counters-synergy sense does not count as a counterspell tag in either mode. The classifier also
/// exposes a pre-permanent-gate interaction signal for the cEDH early-interaction lens, while
/// leaving the returned plan-presence roles unchanged.
/// </summary>
public static class PlanRoleClassifier
{
    /// <summary>
    /// Resolve a card's plan roles. Categories win when they map to any role; otherwise a known combo
    /// piece is <see cref="PlanRole.TutorCombo"/>; otherwise the oracle-text heuristic decides. Returns
    /// <see cref="PlanRole.None"/> for a pure resource/ramp/filler card.
    /// </summary>
    /// <param name="fact">The resolved card (type line + oracle text drive the heuristic fallback).</param>
    /// <param name="categories">The card's crowd-sourced category tags (free text; may be empty).</param>
    /// <param name="isComboPiece">True when Commander Spellbook lists the card in an included combo.</param>
    /// <param name="mode">Analysis profile; gates whether a pure counterspell earns Interaction.</param>
    public static PlanRole Classify(CardFact fact, IReadOnlyList<string> categories, bool isComboPiece, ManabaseMode mode)
        => Classify(fact, categories, isComboPiece, mode, out _);

    /// <summary>
    /// Resolve a card's plan roles, while also reporting whether it earned
    /// <see cref="PlanRole.Interaction"/> before the permanent gate strips one-shot instants/sorceries.
    /// The returned value is byte-identical to <see cref="Classify(CardFact, IReadOnlyList{string}, bool, ManabaseMode)"/>.
    /// </summary>
    public static PlanRole Classify(
        CardFact fact,
        IReadOnlyList<string> categories,
        bool isComboPiece,
        ManabaseMode mode,
        out bool interactionMeritPreGate)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(categories);

        // Resolve roles first (categories → combo piece → heuristic, first-hit-wins), THEN apply the
        // permanent gate below to whatever won.
        PlanRole roles;
        PlanRole fromCategories = FromCategories(categories, mode);
        if (fromCategories != PlanRole.None)
        {
            roles = fromCategories;
        }
        else if (isComboPiece)
        {
            roles = PlanRole.TutorCombo;
        }
        else
        {
            roles = FromHeuristic(fact, mode);
        }

        interactionMeritPreGate = roles.HasFlag(PlanRole.Interaction);

        // PERMANENT gate (user decisions 2026-07-09): a hand "has a plan" when it holds a card that
        // advances the win castable on curve. PAYOFF and INTERACTION require a PERMANENT — a one-shot
        // burn/extra-turn finisher (Torment of Hailfire) or a one-shot removal/counter (Swords,
        // Counterspell) leaves nothing on the board, so it is not by itself a plan. TUTORS and CARD-DRAW
        // (TutorCombo / Engine) still count even as instants/sorceries: a sorcery tutor (Demonic Tutor)
        // points at the permanent win, and card advantage furthers the plan. So for a non-permanent front
        // face we strip only the permanent-only roles and keep the rest. (The lower-level
        // FromCategories/FromHeuristic detectors stay type-agnostic; the type rule lives here, at the
        // single service entry.)
        if (CardTypeLine.IsNonPermanentFront(fact.TypeLine))
        {
            roles &= ~PermanentOnlyRoles;
        }

        return roles;
    }

    // Roles that only "count" on a permanent: a board threat (Payoff) and reactive interaction that must
    // stick to matter. TutorCombo and Engine are deliberately absent — they advance the plan even as a
    // one-shot instant/sorcery.
    private const PlanRole PermanentOnlyRoles = PlanRole.Payoff | PlanRole.Interaction;

    /// <summary>
    /// Returns <see langword="true"/> when a category string belongs to this classifier's own
    /// plan-role vocabulary. Cut Lab structural findings uses this helper so its stranded-subtheme
    /// exclusion cannot drift from <see cref="FromCategories(IReadOnlyList{string}, ManabaseMode)"/>.
    /// </summary>
    /// <param name="categoryName">A free-text category tag.</param>
    internal static bool CategoryMapsToPlanRole(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);

        string c = categoryName.ToLowerInvariant();
        return IsPayoffCategory(c)
            || IsTutorComboCategory(c)
            || IsInteractionCategory(c)
            || IsCounterCategory(c)
            || IsEngineCategory(c);
    }

    /// <summary>
    /// Map a card's free-text category tags to roles by keyword. User-typed Archidekt tags are not a
    /// controlled vocabulary, so this is substring matching over the common role words, not an exact
    /// switch. A card tagged both "Win Condition" and "Card Draw" earns Payoff | Engine. Ramp / land /
    /// fixing tags contribute nothing. A "counter" tag earns Interaction only in cEDH, and the plural
    /// counters-synergy sense does not count as a counterspell tag in either mode.
    /// </summary>
    /// <param name="categories">The card's crowd-sourced category tags (free text; may be empty).</param>
    /// <param name="mode">Analysis profile; gates whether a "counter" tag earns Interaction.</param>
    public static PlanRole FromCategories(IReadOnlyList<string> categories, ManabaseMode mode)
    {
        ArgumentNullException.ThrowIfNull(categories);

        bool countsCounters = mode == ManabaseMode.Cedh;
        PlanRole roles = PlanRole.None;
        foreach (string category in categories)
        {
            string c = category.ToLowerInvariant();

            if (IsPayoffCategory(c))
            {
                roles |= PlanRole.Payoff;
            }

            if (IsTutorComboCategory(c))
            {
                roles |= PlanRole.TutorCombo;
            }

            if (IsInteractionCategory(c))
            {
                roles |= PlanRole.Interaction;
            }

            // A counterspell tag advances the plan only in competitive play. In casual a counter is
            // reactive insurance, not a card that furthers the win plan, so it earns no role there.
            if (countsCounters && IsCounterCategory(c))
            {
                roles |= PlanRole.Interaction;
            }

            // Draw/advantage/engine tags earn Engine — but only when the tag is NOT itself a ramp/mana
            // tag (e.g. "mana ramp / card draw" would already be split into separate tags upstream).
            if (IsEngineCategory(c))
            {
                roles |= PlanRole.Engine;
            }
        }

        return roles;
    }

    /// <summary>
    /// Oracle-text heuristic fallback for a card with no useful category tags and not a known combo
    /// piece. Reuses the shared <see cref="DeckStatClassifier"/> signals, including the Interaction
    /// determination for removal, board wipes, protection, and pure counters. Engine requires a
    /// PERMANENT draw source (repeatable) — a one-shot instant/sorcery "draw two" is filler, not an
    /// engine, and stays None.
    /// </summary>
    /// <param name="fact">The resolved card (type line + oracle text).</param>
    /// <param name="mode">Analysis profile; gates whether a pure counterspell earns Interaction.</param>
    public static PlanRole FromHeuristic(CardFact fact, ManabaseMode mode)
    {
        ArgumentNullException.ThrowIfNull(fact);

        string typeLine = fact.TypeLine;
        string oracle = fact.FrontOracleText;

        PlanRole roles = PlanRole.None;

        if (DeckStatClassifier.IsClosingPowerCard(typeLine, oracle))
        {
            roles |= PlanRole.Payoff;
        }

        if (DeckStatClassifier.IsTutorCard(oracle))
        {
            roles |= PlanRole.TutorCombo;
        }

        if (GrantsInteraction(fact.Name, typeLine, oracle, mode))
        {
            roles |= PlanRole.Interaction;
        }

        // Repeatable draw only: a permanent that draws is an engine; a one-shot instant/sorcery draw
        // is filler velocity (excluded, per the locked "filler draw never qualifies" decision).
        bool isSpellOnTheStack = typeLine.Contains("Instant", StringComparison.OrdinalIgnoreCase)
            || typeLine.Contains("Sorcery", StringComparison.OrdinalIgnoreCase);
        if (DeckStatClassifier.IsDrawCard(oracle) && !isSpellOnTheStack)
        {
            roles |= PlanRole.Engine;
        }

        return roles;
    }

    /// <summary>
    /// Whether a card earns <see cref="PlanRole.Interaction"/> from the oracle-text heuristic. Removal,
    /// board wipes, protection (curated staple, or oracle text recognized by
    /// <see cref="DeckStatClassifier.IsProtectionCard"/>), and non-counter instants always qualify. A
    /// pure counterspell — one that counters a spell and does nothing else — qualifies only in
    /// <see cref="ManabaseMode.Cedh"/>: a casual counter is reactive insurance, not a card that
    /// advances the win plan. A card that both counters and removes still counts in casual (it has
    /// removal merit beyond the counter).
    /// </summary>
    private static bool GrantsInteraction(string name, string typeLine, string oracle, ManabaseMode mode)
    {
        // A pure counterspell hits IsInteractionCard (it's an instant / "counter target ...") but in
        // Casual earns nothing: it is reactive insurance, not a card that advances the win plan. cEDH
        // keeps it (it protects the combo turn). A card that ALSO removes still qualifies via the hard-
        // removal checks below (removal merit beyond the counter). Removal / board wipes are checked
        // second so IsInteractionCard short-circuits the extra oracle scans for the common instant case.
        bool interactionMerit = DeckStatClassifier.IsInteractionCard(typeLine, oracle)
            && (mode == ManabaseMode.Cedh || !CountersASpell(oracle));

        return interactionMerit
            || DeckStatClassifier.IsBoardWipeCard(oracle)
            || DeckStatClassifier.IsTargetedRemovalCard(typeLine, oracle)
            // Why: spike 002 found the "protect" category path already grants Interaction in both
            // modes, so mode-gating the oracle arm would make tagged and untagged copies disagree; and
            // FromHeuristic stays type-agnostic because Classify already strips Interaction from the
            // seven curated protection instants via PermanentOnlyRoles.
            || DeckStatClassifier.IsProtectionCard(name, oracle);
    }

    // Broader than DeckStatClassifier.IsCounterspellCard (exact "counter target spell" only): also
    // catches narrow-target counters (Negate, Swan Song, Dovin's Veto) so the casual carve-out covers
    // them. Ability-only counters (Stifle) lack "spell" and stay generic interaction.
    private static bool CountersASpell(string oracle)
        => oracle.Contains("counter target", StringComparison.OrdinalIgnoreCase)
            && oracle.Contains("spell", StringComparison.OrdinalIgnoreCase);

    private static bool IsPayoffCategory(string category)
        => Has(category, "win", "finisher", "payoff", "wincon", "win con", "win-con", "closer", "beater");

    private static bool IsTutorComboCategory(string category)
        => Has(category, "tutor", "combo");

    private static bool IsInteractionCategory(string category)
        => Has(category, "removal", "interaction", "protect", "wipe", "answer");

    // Why: spike 002 found that the bare "counter" substring collided with the crowd tag
    // "Counters" (+1/+1-counters synergy on cards like Cordial Vampire and Blade of the
    // Bloodchief), which wrongly earned PlanRole.Interaction in cEDH. The singular token is now
    // word-bounded, while the closed compounds counterspell/countermagic stay explicit; and
    // "counterspell" already prefixes "counterspells", so the plural needs no separate needle.
    private static bool IsCounterCategory(string category)
        => Has(category, "counterspell", "countermagic")
            || HasWord(category, "counter");

    private static bool IsEngineCategory(string category)
        => Has(category, "engine", "advantage", "card draw", "value")
            || (Has(category, "draw") && !Has(category, "ramp", "mana"));

    private static bool Has(string haystack, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasWord(string haystack, string needle)
    {
        int startIndex = 0;
        while (startIndex <= haystack.Length - needle.Length)
        {
            int index = haystack.IndexOf(needle, startIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            bool startBounded = index == 0 || !char.IsAsciiLetterOrDigit(haystack[index - 1]);
            int endIndex = index + needle.Length;
            bool endBounded = endIndex == haystack.Length || !char.IsAsciiLetterOrDigit(haystack[endIndex]);
            if (startBounded && endBounded)
            {
                return true;
            }

            startIndex = index + 1;
        }

        return false;
    }
}
