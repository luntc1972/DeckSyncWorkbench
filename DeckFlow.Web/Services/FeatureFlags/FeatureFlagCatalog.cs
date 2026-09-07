using DeckFlow.Core.Content;

namespace DeckFlow.Web.Services.FeatureFlags;

/// <summary>
/// Human-readable descriptions for the known runtime feature flags, surfaced on the
/// /Admin/Flags page so an operator can see what each toggle does without reading code.
/// Keep this in sync with the seed list in <see cref="FeatureFlagStore"/>; the
/// <c>FeatureFlagCatalogTests</c> guard fails if a seeded key has no description here.
/// Unknown keys (e.g. a flag added to the DB out-of-band) degrade gracefully to an empty
/// string via <see cref="Describe"/>.
/// </summary>
public static class FeatureFlagCatalog
{
    /// <summary>Flag key (dotted namespace) → one-line operator description.</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["service.scryfall-tagger.enabled"] =
                "Scrape Scryfall Tagger to enrich category suggestions. Off falls back to the other suggestion sources.",
            ["tool.help.enabled"] =
                "Show the in-app Help section and its navigation link.",
            ["service.harvest-cron.enabled"] =
                "Run the scheduled background content-harvest job. Off pauses automated harvesting (manual harvest still works).",
            ["tool.categories.enabled"] =
                "Enable the Commander Categories page and the category-suggestion tools.",
            ["tool.knowledge-base.enabled"] =
                "Serve the browsable Content Knowledge Base (creator videos) on the public site.",
            ["tool.manabase.enabled"] =
                "Enable the Mana Base analyzer tool and its navigation link.",
            ["tool.deck-analysis.enabled"] =
                "Enable the Deck Analysis tool that builds the ChatGPT deck-analysis prompt packet.",
            ["tool.deck-comparison.enabled"] =
                "Enable the Deck Comparison tool that builds the head-to-head comparison prompt for two decks.",
            ["tool.cedh-meta-gap.enabled"] =
                "Enable the cEDH Meta Gap tool that builds the prompt comparing a deck against the cEDH metagame.",
            ["tool.deck-sync.enabled"] =
                "Enable the Deck Sync tool (and its /api/deck/diff endpoint) that diffs two decks card-by-card.",
            ["tool.convert.enabled"] =
                "Enable the Convert tool that rewrites a decklist between Moxfield and Archidekt formats.",
            ["tool.deck-primer.enabled"] =
                "Enable the Deck Primer tool that generates a primer write-up prompt for a deck.",
            ["tool.deck-history.enabled"] =
                "Deck History tool: version a deck into a downloadable snapshot-history JSON file with notes, pair diffs, and an evolution prompt.",
            ["tool.cut-lab.enabled"] =
                "Cut Lab tool: intake an oversized (101–150 card) Commander pool, declare deck intent (primary/secondary plan, bracket, play experience), and lock cards, named packages, and role groups so later cut rounds can never propose them. Seeded OFF.",
            ["analysis.cut-lab.proven-equivalence"] =
                "Disclosure-only deterministic Cut Lab evidence for complete distinct-card semantic profiles. " +
                "NOT YET WIRED -- no runtime path reads this flag; toggling it currently has no effect. Seeded OFF.",
            ["tool.deck-modules.enabled"] =
                "Deck Modules tool: import one baseline deck, manually assign 2-4 named strategy alternatives with linked mana support, and compile a complete 100-card configuration with legality diagnostics and an exact swap/reset checklist. Session-scoped only, no saved projects. Seeded OFF.",
            ["service.scryfall-collection-cache.enabled"] =
                "Cache individual Scryfall cards/collection results in process memory (24h TTL). Off makes every lookup hit Scryfall as before. Seeded OFF.",
            ["tool.card-lookup.enabled"] =
                "Enable the Card Lookup tool that fetches Scryfall card details for a list of card names.",
            ["tool.mechanic-lookup.enabled"] =
                "Enable the Mechanic Lookup tool that finds cards matching a rules mechanic or keyword.",
            ["tool.judge-questions.enabled"] =
                "Enable the Judge Questions tool that builds rules-question prompts for a deck.",
            ["tool.commander-categories.enabled"] =
                "Enable the Commander Categories page and its commander-specific category lookup.",
            ["analysis.reference.full-oracle-text"] =
                "Include full Oracle rules text for reference cards in the deck-analysis prompt (larger but more precise).",
            ["analysis.reference.deck-stats"] =
                "Append computed deck statistics to the deck-analysis prompt.",
            ["analysis.manabase.accuracy"] =
                "Bundled manabase sim-accuracy improvements (mana quantity, repeatable-ramp credit, color-aware mulligan, land-ramp simulation, health-band headline floor, pay-life untapped lands, and MDFC land backs modeled as real lands).",
            // Legacy sim sub-flags below: all folded into analysis.manabase.accuracy and no longer
            // read by the analyzer. They are not seeded anymore, but linger as rows on databases
            // seeded before the consolidation (flipping the seed default never deletes stored rows).
            // Kept here only so /Admin/Flags shows what they were instead of a blank cell; toggling
            // any of them has no effect and the rows are safe to delete.
            ["analysis.manabase.source-mana-quantity"] =
                "Legacy sim sub-flag (counted a source's actual mana quantity), now folded into analysis.manabase.accuracy and no longer read. Lingering row on older databases; safe to delete.",
            ["analysis.manabase.ramp-credit-v2"] =
                "Legacy sim sub-flag (repeatable ramp/draw land-target credit), now folded into analysis.manabase.accuracy and no longer read. Lingering row on older databases; safe to delete.",
            ["analysis.manabase.color-aware-mulligan"] =
                "Legacy sim sub-flag (color-aware London mulligan in the castability sim), now folded into analysis.manabase.accuracy and no longer read. Lingering row on older databases; safe to delete.",
            ["analysis.manabase.land-ramp-sim"] =
                "Legacy sim sub-flag (credited land-ramp spells such as Cultivate in the sim), now folded into analysis.manabase.accuracy and no longer read. Lingering row on older databases; safe to delete.",
            ["analysis.manabase.health-band-headline-floor"] =
                "Legacy sim sub-flag (health-band headline castability floor), now folded into analysis.manabase.accuracy and no longer read. Lingering row on older databases; safe to delete.",
            ["analysis.manabase.health-band-castability"] =
                "Let the deck's weakest color affect the overall health rating: if that color's hardest spell is cast below the target (80% Casual, 88% cEDH), it counts as a color problem and can drop the verdict from Solid to Workable. On by default.",
            ["analysis.manabase.plain-language-verdict"] =
                "Show a plain-language 'Reading your deck' verdict, friendly one-line explanations for each manabase metric, and (Casual only) a ramp vs. draw slot-budget advisory. On by default; recommendations are heuristic and never change the land count, color counts, castability, or health rating.",
            ["analysis.manabase.commander-castability"] =
                "Shows command-zone castability - individual cast probability for each commander/partner/background, plus (Casual only) a companion's on-curve chance including the +3 generic 'to hand' rule tax (a heuristic); on by default.",
            ["analysis.manabase.tap-analyzer"] =
                "Surface untapped-source frequency and turn-1 untapped availability on the mana base page and its " +
                "paste artifact. Off = byte-identical output.",
            ["analysis.command-zone-awareness"] =
                "Names the full command zone - all partners/Background plus any companion as side metadata - in the /deck-analysis prompt for all three AI variants; off by default, output byte-identical when off.",
            ["tool.bracket.enabled"] =
                "Enable the Bracket Check tool — auto-classify a Commander deck into its official 1-5 bracket " +
                "and generate a balancer prompt. Off = byte-identical to pre-Phase-76.",
            ["analysis.multi-axis-score"] =
                "Show a four-axis Power/Speed/Control/Consistency score block in the deck-analysis " +
                "Step-3 results and include the score in all three prompt artifacts. Off = byte-identical.",
            ["analysis.interaction-audit"] =
                "Show the deck-analysis interaction and answers audit block - bucketed card-backed " +
                "interaction counts plus coverage-gap advisories - in the Step-3 readout and all " +
                "three prompt artifacts. Off = byte-identical to pre-Phase-79.",
            ["analysis.wincon-map"] =
                "Show the deck-analysis win-condition & combo map block - ranked combos, one-card-away " +
                "near-combos, an assembly-path count, a coarse assembly band, and closing cards - in the " +
                "Step-3 readout and all three prompt artifacts. Off = byte-identical to pre-Phase-80.",
            ["tool.primer.stale-flag"] =
                "Surface a 'deck changed since this primer was generated' stale banner on the Deck Primer " +
                "page, shown only on resume-without-rebuild when the current deck differs from the generated " +
                "primer's deck. Never auto-rebuilds or re-fetches. Off = byte-identical output and zips.",
            ["analysis.manabase.mulligan-eval"] =
                "Show the opening-hand / mulligan evaluator block on the mana base page and its paste " +
                "artifact - a keepable-hand band, London mulligan keep-depth process, and representative " +
                "openers with a per-play on-curve and has-a-plan read, all a heuristic consistency signal " +
                "derived from the existing simulation. Off = byte-identical output.",
            ["analysis.manabase.plan-presence"] =
                "Add a 'with a plan' line to the mana base opening-hand block and paste artifact: the share " +
                "of keepable openers holding a win-directed card (payoff / engine / tutor-combo / interaction) " +
                "castable on curve, with a per-role breakdown. Role coverage from your category knowledge + " +
                "Commander Spellbook + a heuristic; a consistency signal, not keep/mulligan advice. Turning it " +
                "on adds a per-analysis category lookup and a Commander Spellbook fetch. Off = byte-identical " +
                "output; note the same role-classification I/O still runs in cEDH when the " +
                "cedh-interaction-lens flag (seeded ON) is active.",
            ["analysis.manabase.keep-shapes"] =
                "Show the cEDH three-shape keep gate plus the casual curve-coverage line in the mana base " +
                "opening-hand block and paste artifact: headline mana-keepable and plan-keepable rates, " +
                "shape-labeled representative openers, a turn cap so a turn-6 payoff is never called workable, " +
                "the commander surfaced for commander-central decks, and a casual 'plays a spell on ~N of the " +
                "first 5 turns' read. cEDH gate + casual metric; off = byte-identical output.",
            ["analysis.manabase.focused-tier"] =
                "Show the Focused mid-power manabase mode between Casual and cEDH. Focused keeps the " +
                "Casual land target and display surfaces, but raises the color-support threshold from 80% to 85%. " +
                "Seeded OFF; off = byte-identical to today's two-mode UI and behavior.",
            ["analysis.cut-lab.commander-floors"] =
                "Cut Lab: enable the commander-aware floor defaults layer on the role-floors table and floor resolution. " +
                "Seeded OFF; off = byte-identical to the pre-Phase-3 bracket-only UI and behavior.",
            ["analysis.cut-lab.functional-twins"] =
                "Cut Lab: surface the Slot Congestion structural finding - three or more unlocked, " +
                "non-commander cards sharing the same role, the same exact mana value, and the same primary " +
                "card type. This finding is disclosure-only: it is excluded from the cut-round tally, so turning " +
                "it ON cannot change which card Cut Lab proposes next, the queue order, or combo-protection " +
                "composition. Seeded OFF; off = no Slot Congestion finding is produced.",
            ["analysis.manabase.source-list"] =
                "Show two display-only disclosures inside the mana base untapped-sources lens: a full " +
                "mana-source list with pip letters plus a tapped-sources subset. Page HTML only; off = byte-identical output.",
            ["analysis.manabase.cedh-interaction-lens"] =
                "cEDH-only kill switch for the 'Early interaction' lens, the full castability table " +
                "exposure in cEDH mode, and the two prompt-artifact interaction blocks. Seeded ON; off = " +
                "byte-identical output.",
            ["analysis.manabase.ritual-burst-mana"] =
                "Credit instant/sorcery rituals (Dark Ritual, Rite of Flame, Cabal Ritual) as one-shot " +
                "burst mana in the manabase castability sim, cEDH mode only. Raises early-turn cast % " +
                "for ritual-fueled lists; land count and color counts stay unchanged. Off = byte-identical output.",
            ["analysis.manabase.ritual-land-credit"] =
                "Apply a cEDH-only land-target credit for net-positive rituals when recommending land " +
                "count. Separate from analysis.manabase.ritual-burst-mana: this changes the strategic " +
                "land target, not the tactical castability burst sim. Off = byte-identical output.",
            ["analysis.manabase.scry-credit"] =
                "Credit qualifying cheap scry spells as +0.2 any-color effective sources per copy in " +
                "the analyzer's Karsten color-count lane only. Separate from the ≤2 MV ramp/draw land " +
                "credit, so draw+scry cards can count in both places; castability and land target stay unchanged. Off = byte-identical output.",
            ["analysis.manabase.colorless-snow"] =
                "Track true {C} and snow {S} costs as separate source-requirement categories. The " +
                "sim requires real colorless producers for {C} and snow permanents for {S}; off keeps " +
                "the historic colorless-pip drop path byte-identical.",
            ["analysis.manabase.restricted-lands"] =
                "Apply the restricted-land approximation for Cavern of Souls, Unclaimed Territory, " +
                "Ancient Ziggurat, and Nykthos, Shrine to Nyx, plus the related disclosure marker on " +
                "the mana base page. Discounts colored-source weight by deck composition; the deck's " +
                "land/source rows disclose the approximation when present. Off = byte-identical output.",
            ["analysis.manabase.cedh-land-target"] =
                "Enable the hybrid cEDH land target: keep the Karsten curve anchor, but drop the flat 28 " +
                "floor and optionally nudge toward the commander's committed cEDH land baseline when sample " +
                "size is deep enough. cEDH only; off = byte-identical output.",
            ["analysis.manabase.baseline"] =
                "Manabase: show the empirical community land baseline (per bracket) beside the Karsten target.",
            [ContentKbFeatureFlagKeys.DirectPushGitBody] =
                "Serve a Content-KB body exclusively from the git-shipped /app tree, dropping the legacy " +
                "/data-SFTP-first overlay fallback. Off = today's byte-identical git-then-overlay serving.",
            ["sync.reconcile"] =
                "Gate the Studio Reconcile page's destructive soft-hide Apply action, which removes " +
                "seed-managed rows absent from the current git seed. Detection and the read-only dry-run " +
                "stay always-available regardless of this flag; off blocks Apply only.",
        };

    /// <summary>
    /// Returns the operator description for <paramref name="key"/>, or an empty string when the
    /// key is not catalogued (so the view renders a blank cell rather than throwing).
    /// </summary>
    /// <param name="key">Dotted-namespace flag key.</param>
    public static string Describe(string key) =>
        Descriptions.TryGetValue(key, out string? description) ? description : string.Empty;
}
