using System.Linq;
using System.Text.RegularExpressions;

namespace DeckFlow.Core.Analysis;

/// <summary>
/// Pure static deck-stat classifiers used to tally role counts (ramp, draw, interaction,
/// board wipes, recursion, closing power) in a deck's mainboard.  These are pure CPU domain
/// logic; their inputs (typeLine, oracleText) come from Scryfall card data.
/// </summary>
public static class DeckStatClassifier
{
    // A card-draw effect that benefits YOU (efficacy R2 M7). Matches "draw(s) a/N card(s)" —
    // imperative ("Draw a card"), activated ("<cost>: Draw a card"), "you (may) draw…", and
    // symmetric wheels ("each player draws seven cards", where you are a player too) — but EXCLUDES:
    //   * draws attributed to an opponent or an indeterminate other player ("target/that/another
    //     player draws", "opponent(s) draw"), which are not card advantage for the caster; and
    //   * draw-as-CONDITION, where the draw is a trigger/replacement rather than an effect
    //     ("whenever/when you draw a card, …" payoffs; "if you would draw a card, …" replacements) —
    //     those cards do not themselves draw. A real ETB draw ("When this enters, draw a card") has
    //     no "you" between the trigger word and "draw", so it is still matched.
    // Handling "draws?" (with the plural s) is the point: a "…draws two cards" card is now seen the
    // same by the v2 land-target credit (IsRepeatableRampOrDraw) and the budget draw count
    // (IsDrawPieceForBudget), instead of one subsystem crediting it while the other ignores it.
    private static readonly Regex YouCardDrawRegex = new(
        @"(?<!(?:opponent|opponents|target player|target opponent|that player|another player|whenever you|when you|would) )\bdraws?\s+(?:a|one|two|three|four|five|six|seven|eight|nine|ten|x|\d+)\s+cards?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReminderTextRegex = new(@"\([^)]*\)", RegexOptions.Compiled);

    // Why: broad role-tally ramp signal — intentionally distinct from the tuned, flag-gated
    // Manabase ramp predicates (castability/land math). Do NOT unify the two. See ADR
    // docs/decisions/0003-ramp-classifier-divergence.md.
    /// <summary>
    /// Returns <see langword="true"/> when the card is a ramp source: a land, an explicit
    /// mana-add effect, a mana-symbol producer (mana rocks, dorks, rituals), a land-search,
    /// or a Treasure producer.
    /// </summary>
    /// <param name="typeLine">Card type line (e.g. "Artifact — Treasure").</param>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsRampCard(string typeLine, string oracleText)
        => typeLine.Contains("Land", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("add one mana", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("add two mana", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("{T}: Add", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("Add {", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("search your library for a basic land", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("search your library for up to", StringComparison.OrdinalIgnoreCase) && oracleText.Contains("land", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("Treasure token", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("create a Treasure", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shared you-anchored literal card-draw predicate: "draw(s) a/N card(s)" that benefits YOU,
    /// excluding opponent/trigger/replacement draws. Regex-only (no clue/connive union) so Manabase's
    /// Karsten draw term can reuse the exact same signal without inheriting role-tally extras.
    /// </summary>
    /// <param name="oracleText">Normalized oracle text.</param>
    internal static bool MatchesYouCardDraw(string oracleText)
    {
        // Strip reminder text so cycling's "(..., Discard this card: Draw a card.)" does not make
        // every cycling card look like literal draw (for example, Raugrin Triome). Most oracle text
        // has no parentheses at all, so skip the regex pass (and its allocation) when there's
        // nothing to strip — this runs per card in the Cut Lab role-tally hot path.
        if (oracleText.IndexOf('(', StringComparison.Ordinal) < 0)
        {
            return YouCardDrawRegex.IsMatch(oracleText);
        }

        string text = ReminderTextRegex.Replace(oracleText, string.Empty);
        return YouCardDrawRegex.IsMatch(text);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the card has you-anchored literal card draw (any count),
    /// or is clue/connive card-advantage (investigate/connive). Role-tally draw signal.
    /// </summary>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsDrawCard(string oracleText)
        => MatchesYouCardDraw(oracleText)
            || oracleText.Contains("investigate", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("connive", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when the card is an interaction piece: an instant, or a
    /// spell that destroys, exiles, counters, bounces, or fights.
    /// </summary>
    /// <param name="typeLine">Card type line.</param>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsInteractionCard(string typeLine, string oracleText)
        => typeLine.Contains("Instant", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("destroy target", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("exile target", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("counter target", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("return target spell", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("fight target", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when the card clears multiple permanents at once.
    /// </summary>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsBoardWipeCard(string oracleText)
        => oracleText.Contains("destroy all creatures", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("destroy all artifacts", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("destroy all enchantments", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("each creature", StringComparison.OrdinalIgnoreCase) && oracleText.Contains("gets -", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("exile all", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when the card returns cards from the graveyard.
    /// </summary>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsRecursionCard(string oracleText)
        => oracleText.Contains("return target card from your graveyard", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("return all land cards from your graveyard", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("return target permanent card from your graveyard", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("reanimate", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("from your graveyard to your hand", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when the card is a win condition, extra-turn effect,
    /// damage doubler, or combat-draw engine.
    /// </summary>
    /// <param name="typeLine">Card type line.</param>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsClosingPowerCard(string typeLine, string oracleText)
        => oracleText.Contains("each opponent loses", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("you win the game", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("extra turn", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("double strike", StringComparison.OrdinalIgnoreCase)
            || typeLine.Contains("Craterhoof", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("combat damage to a player", StringComparison.OrdinalIgnoreCase) && oracleText.Contains("draw", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("whenever this creature attacks", StringComparison.OrdinalIgnoreCase) && oracleText.Contains("+X/+X", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when the card tutors a card from the library, excluding
    /// land-fetch ramp (basic-land search, generic land-card search, or land onto the battlefield).
    /// </summary>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsTutorCard(string oracleText)
        => oracleText.Contains("search your library for", StringComparison.OrdinalIgnoreCase)
            && !oracleText.Contains("basic land", StringComparison.OrdinalIgnoreCase)
            // Exclude land-fetch ramp ("a land card") but NOT nonland tutors: strip "nonland card"
            // first so its trailing "land card" substring does not trip the land-fetch exclusion.
            && !oracleText.Replace("nonland card", " ", StringComparison.OrdinalIgnoreCase)
                .Contains("land card", StringComparison.OrdinalIgnoreCase)
            && !oracleText.Contains("land onto the battlefield", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when the card is fast mana: a zero-mana-value artifact that
    /// produces mana (e.g. Mana Crypt, Jeweled Lotus). Mana rocks with MV &gt;= 1 (e.g. Sol Ring) are excluded.
    /// </summary>
    /// <param name="typeLine">Card type line.</param>
    /// <param name="oracleText">Normalized oracle text.</param>
    /// <param name="manaCost">Mana cost string (e.g. "{1}"); blank for zero-cost artifacts.</param>
    public static bool IsFastManaCard(string typeLine, string oracleText, string manaCost)
        => DeckStatAggregator.EstimateManaValue(manaCost) == 0
            && typeLine.Contains("Artifact", StringComparison.OrdinalIgnoreCase)
            && (oracleText.Contains("{T}: Add", StringComparison.OrdinalIgnoreCase)
                || oracleText.Contains("Add {", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns <see langword="true"/> when the card is a ramp or draw piece with estimated mana value
    /// of 2 or less — the early acceleration/consistency signal the multi-axis scorer consumes.
    /// </summary>
    /// <param name="typeLine">Card type line.</param>
    /// <param name="oracleText">Normalized oracle text.</param>
    /// <param name="manaCost">Mana cost string (e.g. "{1}{U}").</param>
    public static bool IsRampOrDrawUnderThreeMv(string typeLine, string oracleText, string manaCost)
        => DeckStatAggregator.EstimateManaValue(manaCost) <= 2
            && (IsRampCard(typeLine, oracleText) || IsDrawCard(oracleText));

    /// <summary>
    /// Returns <see langword="true"/> when the card counters a target spell. Ability counters
    /// (e.g. "counter target activated or triggered ability") are excluded.
    /// </summary>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsCounterspellCard(string oracleText)
        => oracleText.Contains("counter target spell", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when the card is hard targeted removal, excluding board wipes and self-targeted effects.
    /// </summary>
    /// <param name="typeLine">Card type line.</param>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsTargetedRemovalCard(string typeLine, string oracleText)
        => !IsBoardWipeCard(oracleText)
            && !oracleText.Contains("you control", StringComparison.OrdinalIgnoreCase)
            && (oracleText.Contains("destroy target ", StringComparison.OrdinalIgnoreCase)
                || oracleText.Contains("exile target ", StringComparison.OrdinalIgnoreCase)
                || oracleText.Contains("target creature", StringComparison.OrdinalIgnoreCase) && oracleText.Contains("gets -", StringComparison.OrdinalIgnoreCase)
                || oracleText.Contains("deal", StringComparison.OrdinalIgnoreCase) && oracleText.Contains("damage to target creature", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns <see langword="true"/> when a targeted interaction is constrained to objects you control.
    /// </summary>
    /// <param name="typeLine">Card type line.</param>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsSelfTargetedInteraction(string typeLine, string oracleText)
        => oracleText.Contains("you control", StringComparison.OrdinalIgnoreCase)
            && oracleText.Contains("target", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when the card is temporary or weak targeted removal such as bounce, tuck, or blink.
    /// </summary>
    /// <param name="typeLine">Card type line.</param>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsPseudoRemovalCard(string typeLine, string oracleText)
        => !oracleText.Contains("you control", StringComparison.OrdinalIgnoreCase)
            && (oracleText.Contains("return target", StringComparison.OrdinalIgnoreCase)
                && (oracleText.Contains("to its owner's hand", StringComparison.OrdinalIgnoreCase)
                    || oracleText.Contains("to their owner's hand", StringComparison.OrdinalIgnoreCase))
                || oracleText.Contains("target", StringComparison.OrdinalIgnoreCase)
                    && (oracleText.Contains("into its owner's library", StringComparison.OrdinalIgnoreCase)
                        || oracleText.Contains("on the bottom of", StringComparison.OrdinalIgnoreCase)
                        || oracleText.Contains("shuffles it into", StringComparison.OrdinalIgnoreCase))
                || oracleText.Contains("exile target", StringComparison.OrdinalIgnoreCase)
                    && oracleText.Contains("return", StringComparison.OrdinalIgnoreCase)
                    && (oracleText.Contains("end step", StringComparison.OrdinalIgnoreCase)
                        || oracleText.Contains("end of turn", StringComparison.OrdinalIgnoreCase)
                        || oracleText.Contains("next", StringComparison.OrdinalIgnoreCase)));

    // Why: a table (not an inline `||` chain) makes two properties checkable that an inline chain
    // cannot be asserted over — pairable subject forms (Success Criterion 2 of Phase 9.1), and a
    // future single source of truth for the research-report disclosure that
    // DeckFlow.CLI/RoleFloorResearchCommandRunner.cs currently hand-copies. That CLI array is NOT
    // retired by this table — it stays a stale hand-copied duplicate until plan 09.1-03 Task 3
    // deletes it and points the disclosure at this table.
    //
    // Why: every row below is corpus-derived, not guessed — corpus total and needle-exclusive match
    // counts for every ratified needle, plus the rejected candidates (bare `have hexproof`, bare
    // `shroud`, bare `regenerate`) and the real cards that caused each rejection, are recorded in
    // docs/research/protection-vocabulary-corpus-2026-09.md. A needle added here without a
    // corresponding row in that artifact does not belong. Rows are ordered by effect then subject
    // form so the singular/plural pairing is legible at a glance; `hexproof` and `shroud` both group
    // an Equipment/conditional "has/have" status form alongside their "gains/gain" grant form under
    // the same effect, so the pairing invariant is satisfied by the effect as a whole even though
    // "has hexproof" (Swiftfoot Boots) has no ratified "have hexproof" sibling — that plural was
    // measured and rejected (real hexproof-removal-enabler false positives), see the artifact's
    // "Rejected candidates" table. Regenerate and Ward have subject form "none": their oracle-text
    // phrasing is the ability text itself and does not inflect by subject number.
    /// <summary>
    /// The oracle vocabulary <see cref="IsProtectionCard"/> matches against, as data rather than an
    /// inline predicate chain.
    /// </summary>
    public static readonly IReadOnlyList<ProtectionNeedle> ProtectionOracleNeedles =
    [
        new ProtectionNeedle { Text = "gains hexproof", Effect = "hexproof", SubjectForm = "singular" },
        new ProtectionNeedle { Text = "has hexproof", Effect = "hexproof", SubjectForm = "singular" },
        new ProtectionNeedle { Text = "gain hexproof", Effect = "hexproof", SubjectForm = "plural" },
        new ProtectionNeedle { Text = "gains indestructible", Effect = "indestructible", SubjectForm = "singular" },
        new ProtectionNeedle { Text = "gain indestructible", Effect = "indestructible", SubjectForm = "plural" },
        new ProtectionNeedle { Text = "phases out", Effect = "phase-out", SubjectForm = "singular" },
        new ProtectionNeedle { Text = "phase out", Effect = "phase-out", SubjectForm = "plural" },
        new ProtectionNeedle { Text = "gains protection from", Effect = "protection-from", SubjectForm = "singular" },
        new ProtectionNeedle { Text = "gain protection from", Effect = "protection-from", SubjectForm = "plural" },
        new ProtectionNeedle { Text = "regenerate target", Effect = "regenerate", SubjectForm = "none" },
        new ProtectionNeedle { Text = "regenerate this creature", Effect = "regenerate", SubjectForm = "none" },
        new ProtectionNeedle { Text = "has shroud", Effect = "shroud", SubjectForm = "singular" },
        // Lightning Greaves's real oracle text ("Equipped creature has haste and shroud.") does NOT
        // match "has shroud" — shroud is the second item in the conjunction, not the word directly
        // after "has". A bare "and shroud" needle was measured and rejected (also matches Arcane
        // Lighthouse's anti-shroud removal-enabler clause); this needle is exhaustive at 1 corpus
        // match total. See docs/research/protection-vocabulary-corpus-2026-09.md.
        new ProtectionNeedle { Text = "haste and shroud", Effect = "shroud", SubjectForm = "singular" },
        new ProtectionNeedle { Text = "gains shroud", Effect = "shroud", SubjectForm = "singular" },
        new ProtectionNeedle { Text = "have shroud", Effect = "shroud", SubjectForm = "plural" },
        new ProtectionNeedle { Text = "gain shroud", Effect = "shroud", SubjectForm = "plural" },
        new ProtectionNeedle { Text = "ward—pay", Effect = "ward", SubjectForm = "none" },
    ];

    /// <summary>
    /// Returns <see langword="true"/> when the card is a curated protection staple, or its oracle text
    /// matches a corpus-derived protection needle — granting hexproof, indestructible, protection-from,
    /// or shroud (temporary grant or Equipment/conditional status), phasing out, regenerating, or
    /// carrying a Ward—Pay cost. See <see cref="ProtectionOracleNeedles"/> for the full vocabulary and
    /// docs/research/protection-vocabulary-corpus-2026-09.md for the corpus evidence behind it.
    /// </summary>
    /// <param name="name">Card name.</param>
    /// <param name="oracleText">Normalized oracle text.</param>
    public static bool IsProtectionCard(string name, string oracleText)
        => StaxProtectionCatalog.IsProtection(name)
            || ProtectionOracleNeedles.Any(needle => oracleText.Contains(needle.Text, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses a single mana symbol token (the text between <c>{</c> and <c>}</c>) into its
    /// converted mana cost contribution.  Numeric tokens return their integer value; X returns 0;
    /// hybrid symbols (containing '/') return 1; everything else returns 1.
    /// </summary>
    /// <param name="token">Token text without braces (e.g. "3", "X", "W/U").</param>
    public static int ParseManaToken(string token)
    {
        if (int.TryParse(token, out var numeric))
        {
            return numeric;
        }

        if (token.Contains('/', StringComparison.Ordinal))
        {
            return 1;
        }

        return token.Equals("X", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
}
