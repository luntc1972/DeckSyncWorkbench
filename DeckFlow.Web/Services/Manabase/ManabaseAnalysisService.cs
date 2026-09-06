using System.Net;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Core.Parsing;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.FeatureFlags;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreScryfallCollectionIdentifier = DeckFlow.Core.Normalization.ScryfallCollectionIdentifier;

namespace DeckFlow.Web.Services.Manabase;

/// <summary>
/// Loads a deck, resolves its cards through Scryfall, and runs the Core mana-base analyzer,
/// returning a <see cref="ManabaseAnalysisResult"/>. All HTTP stays here (via
/// <see cref="IScryfallCardResolver"/>); the Core pipeline stays pure.
/// </summary>
public interface IManabaseAnalysisService
{
    /// <summary>Analyze the mana base of the deck identified by <paramref name="deckSource"/>.</summary>
    /// <param name="deckSource">A public deck URL or pasted decklist text.</param>
    /// <param name="deckName">Optional display name for the deck (used in the ChatGPT prompt).</param>
    /// <param name="options">Mode + commander-importance knobs; defaults to Casual / Standard.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ManabaseAnalysisResult> AnalyzeAsync(
        string deckSource,
        string? deckName,
        ManabaseAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve the deck and detect its reduced/alternative-cost suggestions WITHOUT running the
    /// (expensive) castability simulation. Backs the "Load deck" step so the user can review and
    /// edit the detected overrides before analysis.
    /// </summary>
    /// <param name="deckSource">A public deck URL or pasted decklist text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ManabaseLoadResult> LoadAsync(
        string deckSource,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The user-selected analysis knobs threaded from the form into the Core analyzer. Bundled into
/// one object so the parameter list does not telescope as more modes are added. Defaults keep the
/// historic Casual / Standard behavior for any caller that omits them.
/// </summary>
public sealed class ManabaseAnalysisOptions
{
    /// <summary>The analysis profile (Casual default, cEDH lowers the land target).</summary>
    public ManabaseMode Mode { get; init; } = ManabaseMode.Casual;

    /// <summary>How heavily to weight the commander's colors (Standard default).</summary>
    public CommanderImportance CommanderImportance { get; init; } = CommanderImportance.Standard;

    /// <summary>
    /// Optional per-card effective-cost overrides (card name → canonical braced cost). Replaces the
    /// printed cost in the castability math for alt/reduced-cost cards. Empty/null = no overrides.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CostOverrides { get; init; }

    /// <summary>Optional user-supplied companion designator; blank/null means "no manual override".</summary>
    public string? CompanionDesignator { get; init; }

    /// <summary>
    /// Optional explicit commander choice from the UI. When supplied it overrides inferred/imported
    /// commander flags, but the selected card must still pass commander eligibility validation.
    /// </summary>
    public string? SelectedCommander { get; init; }

    /// <summary>
    /// Optional explicit bracket (2-5) for the community baseline. Null in Increment 1a (the mode
    /// picks the bracket); the controller sets it from deck classification / the selector in 1b.
    /// </summary>
    public int? Bracket { get; init; }

    /// <summary>
    /// How <see cref="Bracket"/> was chosen (Auto = deck-classified, Override = user selector).
    /// Null lets the service label it (Override for an explicit bracket, Fallback for the mode default).
    /// </summary>
    public ManabaseBracketSource? BracketSource { get; init; }

    /// <summary>
    /// Whether to classify spells into plan roles. This costs crowd-category and Commander Spellbook
    /// lookups, so it defaults to <see langword="false"/> to keep mana-base page behavior
    /// byte-identical for existing callers.
    /// </summary>
    public bool ClassifyPlanRoles { get; init; }
}

/// <summary>The outcome of a mana-base analysis: the report plus presentation context.</summary>
/// <param name="Report">
/// The computed Karsten §6 report, or <see langword="null"/> when commander selection is required
/// before analysis can continue.
/// </param>
/// <param name="InputSummary">Short human summary of what was analyzed.</param>
/// <param name="Unresolved">Card names Scryfall could not resolve (excluded from the math).</param>
/// <param name="ImportWarning">Optional notice from the deck importer (e.g. a fallback path).</param>
/// <param name="PromptSwapPrompt">Paste-ready prompt asking an LLM for specific land swaps.</param>
/// <param name="Suggestions">Auto-detected alt/reduced-cost suggestions to pre-populate the override box.</param>
/// <param name="Verdict">Optional synthesized plain-language verdict (Casual only when the flag is on).</param>
/// <param name="Budget">Optional ramp/draw slot-budget advisory (Casual only when the flag is on).</param>
/// <param name="ShowPlainLanguage">Whether the UI should surface the plain-language glosses/verdict gate.</param>
public sealed record ManabaseAnalysisResult(
    ManabaseReport? Report,
    string InputSummary,
    IReadOnlyList<string> Unresolved,
    string? ImportWarning,
    string PromptSwapPrompt,
    IReadOnlyList<CostSuggestion> Suggestions,
    ManabaseVerdict? Verdict,
    ManabaseRampDrawBudget? Budget,
    bool ShowPlainLanguage)
{
    /// <summary>Whether the UI should surface the Focused deck-type pill.</summary>
    public bool ShowFocusedTier { get; init; }

    /// <summary>
    /// Whether the deck resolved but no valid commander remained after eligibility validation, so a
    /// user selection is required before a report can be produced.
    /// </summary>
    public bool CommanderSelectionRequired { get; init; }

    /// <summary>
    /// Commander-eligible resolved card names surfaced to the caller when manual commander selection
    /// is required.
    /// </summary>
    public IReadOnlyList<string> CommanderChoices { get; init; } = Array.Empty<string>();

    /// <summary>Whether the command-zone castability affordances were enabled for this result.</summary>
    public bool CommanderCastabilityEnabled { get; init; }

    /// <summary>Whether the tap-analyzer card and paste-artifact section were enabled for this result.</summary>
    public bool ShowTapAnalyzer { get; init; }

    /// <summary>Whether the opening-hand / mulligan-evaluator block was enabled for this result.</summary>
    public bool ShowMulliganEval { get; init; }

    /// <summary>Whether the plan-presence opener stat was enabled (flag on) for this result.</summary>
    public bool ShowPlanPresence { get; init; }

    /// <summary>Whether the cEDH keep-shapes / casual curve-coverage read was enabled for this result.</summary>
    public bool ShowKeepShapes { get; init; }

    /// <summary>Whether the display-only mana-source disclosure sections were enabled for this result.</summary>
    public bool ShowSourceList { get; init; }

    /// <summary>Whether the cEDH-only early-interaction lens was enabled for this result.</summary>
    public bool ShowCedhInteractionLens { get; init; }

    /// <summary>Optional companion castability row modeled outside the analyzed 99.</summary>
    public CardCastability? CompanionRow { get; init; }

    /// <summary>Optional empirical per-bracket community land baseline (present only when the flag is on).</summary>
    public ManabaseCommunityBaseline? CommunityBaseline { get; init; }

    /// <summary>
    /// Override card names that matched no card in the analyzed deck (typo or not-in-deck), so their
    /// line was silently dropped. Surfaced to the user as "not applied" feedback. Empty when every
    /// override bound to a spell.
    /// </summary>
    public IReadOnlyList<string> UnmatchedOverrideNames { get; init; } = Array.Empty<string>();

    /// <summary>Spells resolved for this completed analysis, including their optional plan roles.</summary>
    public IReadOnlyList<SpellRequirement> AnalyzedSpells { get; init; } = Array.Empty<SpellRequirement>();
}

/// <summary>
/// The outcome of the cheap "Load deck" step: the deck resolved and classified, with its detected
/// cost suggestions, but no simulation/report. Feeds the review-and-edit-then-analyze flow.
/// </summary>
/// <param name="InputSummary">Short human summary (card/land counts) of what was loaded.</param>
/// <param name="Unresolved">Card names Scryfall could not resolve (excluded from the math).</param>
/// <param name="ImportWarning">Optional notice from the deck importer (e.g. a fallback path).</param>
/// <param name="Suggestions">Auto-detected alt/reduced-cost suggestions to pre-populate the override box.</param>
public sealed record ManabaseLoadResult(
    string InputSummary,
    IReadOnlyList<string> Unresolved,
    string? ImportWarning,
    IReadOnlyList<CostSuggestion> Suggestions);

/// <inheritdoc />
public sealed class ManabaseAnalysisService : IManabaseAnalysisService
{
    // Why: ScryfallCardNameIndex precedence bands, declared together so the whole ladder is
    // readable in one place. Only this class knows how much to trust each card. The bands sit ABOVE
    // the index's default priority of 0 on purpose: a caller that states nothing must lose to every
    // caller that does, so a future bare Add() cannot silently outrank a paired card.
    //   paired to a submission  strongest -- earliest deck position wins
    //   name-search repair      weaker -- the search may return a different printing than the batch
    //   unpaired returned card  weakest -- matched no submission uniquely
    //   (index default, 0)      no stated preference
    private const int SearchFallbackPriority = 2;
    private const int UnpairedPriority = 1;

    // Why: earliest deck position wins, so the priority falls as the position rises. Subtracting
    // from int.MaxValue keeps every paired card above the two sentinel bands for any real deck.
    private static int PairedPriority(int globalPosition) => int.MaxValue - globalPosition;

    // Abuse caps for this anonymous public endpoint: bound the pasted payload and the number
    // of cards so one request can't force unbounded allocations or upstream Scryfall calls.
    // A Commander deck is ~100 cards; these leave generous headroom while rejecting abuse.
    private const int MaxDeckSourceChars = 100_000;
    private const int MaxDeckCards = 500;
    private const int MaxCompanionNameLength = 200;

    // Only these boards make up the deck under analysis; a sideboard/maybeboard would skew the
    // land target.
    private static readonly HashSet<string> AnalyzedBoards =
        new(StringComparer.OrdinalIgnoreCase) { "mainboard", "commander" };

    /// <summary>
    /// Flag key: bundles the settled sim-accuracy knobs (mana quantity, ramp-credit-v2,
    /// color-aware mulligan, land-ramp sim, health-band headline floor, pay-life untapped, and the
    /// conditional-untapped land census). MDFC land backs are modeled as real lands unconditionally
    /// (no longer gated by this flag). Seeded ON.
    /// </summary>
    public const string AccuracyFlagKey = "analysis.manabase.accuracy";

    /// <summary>
    /// MQ-health-band flag key: when enabled, the composite-weakest color's worst-spell cast %
    /// feeds the health-band verdict (Functional→Workable when below the mode's support threshold).
    /// Seeded OFF — promoted to ON after a full 9-deck calibration regression guard passes.
    /// </summary>
    public const string HealthBandCastabilityFlagKey = "analysis.manabase.health-band-castability";

    /// <summary>
    /// Phase-71 flag key: when enabled, Casual mode computes a deterministic plain-language verdict
    /// plus ramp/draw budget advisory; cEDH uses the same gate for UI glosses only. Seeded OFF.
    /// </summary>
    public const string PlainLanguageVerdictFlagKey = "analysis.manabase.plain-language-verdict";

    /// <summary>
    /// Phase-72 flag key: seeded OFF; gates the command-zone castability callout plus companion
    /// modeling in Casual mode.
    /// </summary>
    public const string CommanderCastabilityFlagKey = "analysis.manabase.commander-castability";

    /// <summary>
    /// Phase-75 flag key: seeded OFF; gates the tap-analyzer card on the mana base page plus the
    /// "Untapped Sources:" block in the paste artifact. Read fail-safe OFF; off = byte-identical output.
    /// </summary>
    public const string TapAnalyzerFlagKey = "analysis.manabase.tap-analyzer";

    /// <summary>
    /// Phase-81 flag key: seeded OFF; gates the opening-hand / mulligan-evaluator block on the mana
    /// base page plus the "Opening Hand (mulligan)" block in the paste artifact. Read fail-safe OFF;
    /// off = byte-identical output.
    /// </summary>
    public const string MulliganEvalFlagKey = "analysis.manabase.mulligan-eval";

    /// <summary>
    /// cEDH interaction-lens flag key: seeded ON; gates the cEDH-only early-interaction lens, the
    /// full castability-table exposure in cEDH mode, and the related prompt-artifact blocks. Read
    /// fail-safe OFF; off = byte-identical output.
    /// </summary>
    public const string CedhInteractionLensFlagKey = "analysis.manabase.cedh-interaction-lens";

    /// <summary>
    /// Plan-presence flag key: seeded OFF. Gates the "with a plan" opener stat; the role-classification
    /// I/O it shares with the cEDH interaction lens (batched category lookup + Commander Spellbook combo
    /// fetch) also runs when that lens flag is on in cEDH mode. Read fail-safe OFF.
    /// </summary>
    public const string PlanPresenceFlagKey = "analysis.manabase.plan-presence";

    /// <summary>
    /// cEDH three-shape opening-hand keep gate (explosive / early-engine / interaction-bridge) plus the
    /// casual curve-coverage read. Seeded OFF; flip after UAT. Off = byte-identical output.
    /// </summary>
    public const string KeepShapesFlagKey = "analysis.manabase.keep-shapes";

    /// <summary>
    /// Focused mid-power tier flag key: seeded OFF. Gates the third deck-type radio and the 85%
    /// color-support threshold path; off = byte-identical to Casual.
    /// </summary>
    public const string FocusedTierFlagKey = "analysis.manabase.focused-tier";

    /// <summary>
    /// Display-only source-list flag key: seeded OFF. Gates the nested source disclosures inside the
    /// untapped-sources lens; analyzer data stays unconditional and deterministic.
    /// </summary>
    public const string SourceListFlagKey = "analysis.manabase.source-list"; // Why: display-only; never mutates prompt or packet artifacts.

    /// <summary>
    /// Ritual-burst flag key: seeded OFF. Credits instant/sorcery rituals (Dark Ritual, Rite of Flame,
    /// Cabal Ritual) as one-shot burst mana in the castability sim, cEDH mode only. Read fail-safe OFF;
    /// off = byte-identical output.
    /// </summary>
    public const string RitualBurstFlagKey = "analysis.manabase.ritual-burst-mana";

    /// <summary>
    /// Ritual land-credit flag key: seeded OFF. Applies a cEDH-only strategic land-target credit
    /// for net-positive rituals. Deliberately separate from ritual-burst-mana, which changes only
    /// the tactical castability sim.
    /// </summary>
    public const string RitualLandCreditFlagKey = "analysis.manabase.ritual-land-credit";

    /// <summary>
    /// Cheap scry source-credit flag key: seeded ON. Adds 0.2 any-color effective sources per
    /// qualifying cheap scry spell copy, analyzer-only; castability and land target stay unchanged.
    /// </summary>
    public const string ScryCreditFlagKey = "analysis.manabase.scry-credit";

    /// <summary>
    /// Colorless/snow requirement flag key: seeded ON. Tracks true <c>{C}</c> and snow <c>{S}</c>
    /// costs as separate source categories in the analyzer and castability sim; off = byte-identical.
    /// </summary>
    public const string ColorlessSnowFlagKey = "analysis.manabase.colorless-snow";

    /// <summary>
    /// Restricted-lands flag key: seeded OFF. When enabled, the classifier applies the D-03
    /// composition-gated approximation for Cavern/Unclaimed/Ziggurat/Nykthos and surfaces the
    /// deck-level disclosure names; off = byte-identical historic output.
    /// </summary>
    public const string RestrictedLandsFlagKey = "analysis.manabase.restricted-lands";

    /// <summary>
    /// cEDH land-target flag key: seeded OFF. When enabled, cEDH uses the hybrid curve-anchored land
    /// target with an optional commander baseline nudge; off = byte-identical historic behavior.
    /// </summary>
    public const string CedhLandTargetFlagKey = "analysis.manabase.cedh-land-target";

    /// <summary>
    /// Community-baseline flag key: when ON, attaches the empirical per-bracket land baseline block
    /// to the result (display-only, beside Karsten). Seeded OFF; OFF → byte-identical output.
    /// </summary>
    public const string BaselineFlagKey = "analysis.manabase.baseline";

    private readonly IDeckEntryLoader _deckEntryLoader;
    private readonly IScryfallCardResolver _scryfallCardResolver;
    private readonly IScryfallCollectionProtocol _collectionProtocol;
    private readonly IFeatureFlagCache? _featureFlags;
    private readonly ICategoryKnowledgeStore? _categoryKnowledge;
    private readonly ICommanderSpellbookService? _spellbook;
    private readonly ILogger<ManabaseAnalysisService> _logger;
    private readonly ICedhLandBaselineProvider? _cedhLandBaseline;
    private readonly IManabaseBaselineProvider? _manabaseBaseline;
    private readonly ScryfallCollectionCardCache? _collectionCardCache;

    /// <summary>Creates the analysis service.</summary>
    public ManabaseAnalysisService(
        IDeckEntryLoader deckEntryLoader,
        IScryfallCardResolver scryfallCardResolver,
        IFeatureFlagCache? featureFlags = null,
        ICategoryKnowledgeStore? categoryKnowledge = null,
        ICommanderSpellbookService? spellbook = null,
        ILogger<ManabaseAnalysisService>? logger = null,
        ICedhLandBaselineProvider? cedhLandBaseline = null,
        IManabaseBaselineProvider? manabaseBaseline = null,
        ScryfallCollectionCardCache? collectionCardCache = null,
        IScryfallCollectionProtocol? collectionProtocol = null)
    {
        ArgumentNullException.ThrowIfNull(deckEntryLoader);
        ArgumentNullException.ThrowIfNull(scryfallCardResolver);

        _deckEntryLoader = deckEntryLoader;
        _scryfallCardResolver = scryfallCardResolver;
        _collectionProtocol = collectionProtocol ?? new ScryfallCollectionProtocol(scryfallCardResolver);
        _featureFlags = featureFlags;
        _categoryKnowledge = categoryKnowledge;
        _spellbook = spellbook;
        _logger = logger ?? NullLogger<ManabaseAnalysisService>.Instance;
        _cedhLandBaseline = cedhLandBaseline;
        _manabaseBaseline = manabaseBaseline;
        _collectionCardCache = collectionCardCache;
    }

    /// <inheritdoc />
    public async Task<ManabaseAnalysisResult> AnalyzeAsync(
        string deckSource,
        string? deckName,
        ManabaseAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ManabaseAnalysisOptions();

        // Read the bundled manabase accuracy flag BEFORE classification — the ramp/draw land-target
        // credit, land-ramp sim source, and pay-life untapped land handling are all built before the
        // analyzer path runs, so reading this after Resolve would be too late.
        bool accuracy = IsFlagOn(AccuracyFlagKey);
        bool rampCreditV2 = accuracy;
        bool landRampSim = accuracy;
        bool payLifeUntapped = accuracy;
        bool checkLandUntapped = accuracy;
        bool commanderCastability = IsFlagOn(CommanderCastabilityFlagKey);
        bool showTapAnalyzer = IsFlagOn(TapAnalyzerFlagKey);
        bool showMulliganEval = IsFlagOn(MulliganEvalFlagKey);
        bool keepShapesFlag = IsFlagOn(KeepShapesFlagKey);
        bool showFocusedTier = IsFlagOn(FocusedTierFlagKey);
        bool showSourceList = IsFlagOn(SourceListFlagKey);
        // Read BEFORE resolve: the flag gates the plan-role tagging (and its category + Spellbook I/O)
        // done during classification. Off = no extra I/O and PlanRoles stay None (byte-identical path).
        // ALSO require the opening-hand block (mulligan-eval): the "With a plan" line renders only inside
        // that block, so enabling plan-presence alone must not do the extra I/O + sim for a line that can
        // never show (Codex MED). Both flags on = the stat runs and surfaces.
        bool showPlanPresence = IsFlagOn(PlanPresenceFlagKey) && showMulliganEval;
        bool keepShapes = keepShapesFlag && showMulliganEval;
        bool interactionLens = IsFlagOn(CedhInteractionLensFlagKey);
        bool showCedhInteractionLens = interactionLens && options.Mode == ManabaseMode.Cedh;
        bool ritualBurst = IsFlagOn(RitualBurstFlagKey);
        bool ritualLandCredit = IsFlagOn(RitualLandCreditFlagKey);
        bool scryCredit = IsFlagOn(ScryCreditFlagKey);
        bool colorlessSnow = IsFlagOn(ColorlessSnowFlagKey);
        bool restrictedLands = IsFlagOn(RestrictedLandsFlagKey);
        bool cedhLandTarget = IsFlagOn(CedhLandTargetFlagKey);

        ResolvedManabaseDeck resolved = await ResolveAndClassifyAsync(
                deckSource,
                rampCreditV2,
                landRampSim,
                payLifeUntapped,
                checkLandUntapped,
                restrictedLands,
                commanderCastability,
                // Keep-shapes in cEDH also needs PlanRoles tagged: shapes B/C read roles and shape A
                // reads Payoff/TutorCombo. keepShapes is already gated on showMulliganEval, so this
                // extra I/O stays off whenever the opening-hand block is hidden.
                classifyPlanRoles: options.ClassifyPlanRoles || showPlanPresence || showCedhInteractionLens || (keepShapes && options.Mode == ManabaseMode.Cedh),
                options.Mode,
                options.CompanionDesignator,
                options.SelectedCommander,
                cancellationToken)
            .ConfigureAwait(false);

        if (resolved.CommanderSelectionRequired)
        {
            return new ManabaseAnalysisResult(
                Report: null,
                resolved.InputSummary,
                resolved.Unresolved,
                resolved.FallbackNotice,
                PromptSwapPrompt: string.Empty,
                resolved.Deck.CostSuggestions,
                Verdict: null,
                Budget: null,
                ShowPlainLanguage: false)
            {
                CommanderSelectionRequired = true,
                CommanderChoices = resolved.CommanderChoices,
                CommanderCastabilityEnabled = commanderCastability,
                ShowFocusedTier = showFocusedTier,
                ShowTapAnalyzer = showTapAnalyzer,
                ShowMulliganEval = showMulliganEval,
                ShowPlanPresence = showPlanPresence,
                ShowKeepShapes = keepShapes,
                ShowSourceList = showSourceList,
                ShowCedhInteractionLens = showCedhInteractionLens,
            };
        }

        // Fan the bundled accuracy flag out to the existing Core bools so the internal analyzer/classifier
        // plumbing stays stable. Fail-safe OFF still comes from IsFlagOn above.
        bool useManaQuantity = accuracy;
        bool colorAwareMulligan = accuracy;

        // P4 gated-ramp is always on (efficacy R2 M3): before the sim credits a ramp piece's mana it
        // verifies the ramp's OWN colored cost is payable from the current board (mirrors 17Lands),
        // otherwise a {G} dork gets deployed from a green-less hand and its mana inflates cast %. This
        // was previously coupled to the land-ramp-sim flag, but rocks and dorks are modeled in the sim
        // unconditionally — land-ramp-sim only adds land-ramp SPELLS (Cultivate) — so the gate is
        // relevant whenever the deck runs any ramp, not only when land-ramp spells are simulated.
        // Decoupled and hardcoded on: it is pure correctness, was already live in prod (land-ramp-sim
        // enabled), and the 9-deck calibration guard confirms a <=1pt delta with no band change.
        // MQ-health-band: couple the verdict tier to the sim's composite-worst-color cast %. Fail-safe
        // OFF — seeded OFF; promoted to ON once the 9-deck calibration regression guard confirms no
        // Solid/Excellent deck regresses.
        bool useHealthBandCastability = IsFlagOn(HealthBandCastabilityFlagKey);
        bool useHealthBandHeadlineFloor = accuracy;
        // cEDH-only. Enabled with a resolved commander baseline (N>=10) nudges the target toward the
        // meta mean; enabled WITHOUT a baseline is the intended recalibrated path (drop the flat-28
        // floor to the curve target) — so Enabled stays true even when the lookup misses. Core's
        // N>=10 guard decides whether the mean is actually applied. Flag off / non-cEDH = Disabled.
        CedhLandContext cedhContext = CedhLandContext.Disabled;
        if (cedhLandTarget && options.Mode == ManabaseMode.Cedh)
        {
            double? baselineMean = null;
            int baselineN = 0;
            double? baselineSd = null;
            string? generated = null;
            if (_cedhLandBaseline is not null
                && _cedhLandBaseline.TryGetBaseline(resolved.CommanderNames, out double mean, out int n, out double sd, out generated))
            {
                baselineMean = mean;
                baselineN = n;
                baselineSd = sd;
            }

            cedhContext = new CedhLandContext(baselineMean, baselineN, Enabled: true, BaselineSd: baselineSd, BaselineMonth: generated);
        }

        ManabaseReport report = ManabaseAnalyzer.Analyze(
            resolved.Deck, options.Mode, options.CommanderImportance, options.CostOverrides,
            useManaQuantity, colorAwareMulligan, gateRampOnCastable: true,
            ritualBurst: ritualBurst,
            ritualLandCredit: ritualLandCredit,
            scryCredit: scryCredit,
            colorlessSnow: colorlessSnow,
            keepShapes: keepShapes,
            interactionLens: interactionLens,
            useHealthBandCastability: useHealthBandCastability,
            useHealthBandHeadlineFloor: useHealthBandHeadlineFloor,
            cedhContext: cedhContext);

        bool plainLanguage = IsFlagOn(PlainLanguageVerdictFlagKey);
        ManabaseRampDrawBudget? budget = null;
        ManabaseVerdict? verdict = null;
        CardCastability? companionRow = null;
        string swapPrompt;

        if (commanderCastability && resolved.CompanionCard is not null)
        {
            ParsedManaCost printedCost = ManaCostParser.Parse(resolved.CompanionCard.ManaCost);
            SpellRequirement companionRequirement = ManabaseAnalyzer.BuildCompanionSpell(
                resolved.CompanionCard.Name, printedCost, resolved.CompanionCard.Cmc);
            companionRow = ManabaseAnalyzer.SimulateCompanion(
                resolved.Deck,
                companionRequirement,
                useManaQuantity,
                colorAwareMulligan,
                gateRampOnCastable: true); // always on — see the report Analyze call above (R2 M3)
        }

        if (plainLanguage)
        {
            if (options.Mode != ManabaseMode.Cedh)
            {
                budget = ManabaseRampDrawBudgetCalculator.Calculate(resolved.Deck);
                verdict = ManabaseVerdictSynthesizer.Synthesize(report, options.Mode, budget);
            }

            swapPrompt = ManabaseSwapPromptBuilder.Build(
                report, deckName, resolved.DecklistText, options.Mode, verdict, budget, commanderCastability, companionRow,
                interactionLens: report.InteractionLens);
        }
        else
        {
            swapPrompt = ManabaseSwapPromptBuilder.Build(
                report, deckName, resolved.DecklistText, options.Mode, null, null, commanderCastability, companionRow,
                interactionLens: report.InteractionLens);
        }

        return new ManabaseAnalysisResult(
            report, resolved.InputSummary, resolved.Unresolved, resolved.FallbackNotice,
            swapPrompt, resolved.Deck.CostSuggestions, verdict, budget, plainLanguage)
        {
            CommanderChoices = resolved.CommanderChoices,
            CommanderCastabilityEnabled = commanderCastability,
            CompanionRow = companionRow,
            CommunityBaseline = BuildCommunityBaseline(options, resolved.CommanderNames, report),
            ShowFocusedTier = showFocusedTier,
            ShowTapAnalyzer = showTapAnalyzer,
            ShowMulliganEval = showMulliganEval,
            ShowPlanPresence = showPlanPresence,
            ShowKeepShapes = keepShapes,
            ShowSourceList = showSourceList,
            ShowCedhInteractionLens = showCedhInteractionLens,
            UnmatchedOverrideNames = report.UnmatchedOverrideNames,
            AnalyzedSpells = resolved.Deck.Spells,
        };
    }

    /// <inheritdoc />
    public async Task<ManabaseLoadResult> LoadAsync(
        string deckSource,
        CancellationToken cancellationToken = default)
    {
        // Load surfaces cost suggestions only; neither the ramp-credit land target nor the land-ramp sim
        // source is used here, so the flag values are immaterial — pass false.
        ResolvedManabaseDeck resolved = await ResolveAndClassifyAsync(
                deckSource,
                rampCreditV2: false,
                landRampSim: false,
                payLifeUntapped: false,
                checkLandUntapped: false,
                restrictedLands: false,
                commanderCastability: false,
                classifyPlanRoles: false,
                mode: ManabaseMode.Casual,
                companionDesignator: null,
                selectedCommander: null,
                cancellationToken)
            .ConfigureAwait(false);

        // No simulation here — Load just surfaces the detected cost suggestions for review/edit.
        return new ManabaseLoadResult(
            resolved.InputSummary, resolved.Unresolved, resolved.FallbackNotice, resolved.Deck.CostSuggestions);
    }

    // Decide the baseline bracket AND how it was chosen in one place, so the value and its
    // provenance label can never disagree. Casual -> Core(2), Focused -> Upgraded(3), Cedh -> cEDH(5)
    // when no explicit bracket is given (Fallback); an explicit options.Bracket is an Override
    // (the controller sets it from classification / the selector in Increment 1b).
    private static (int Bracket, ManabaseBracketSource Source) ResolveBaseline(ManabaseAnalysisOptions options)
        => options.Bracket is int explicitBracket
            ? (explicitBracket, options.BracketSource ?? ManabaseBracketSource.Override)
            : (options.Mode switch
            {
                ManabaseMode.Cedh => 5,
                ManabaseMode.Focused => 3,
                _ => 2,
            }, ManabaseBracketSource.Fallback);

    private ManabaseCommunityBaseline? BuildCommunityBaseline(
        ManabaseAnalysisOptions options,
        IReadOnlyList<string> commanderNames,
        ManabaseReport report)
    {
        if (!IsFlagOn(BaselineFlagKey) || _manabaseBaseline is null)
        {
            return null;
        }

        (int bracket, ManabaseBracketSource bracketSource) = ResolveBaseline(options);
        ManabaseBracketBaseline? row = _manabaseBaseline.TryGetBracketBaseline(bracket);
        if (row is null)
        {
            return null;
        }

        // The commander-keyed cEDH meta range (CedhLandBaselineProvider) supersedes the community
        // line — never show two differently-sourced community baselines at once. This predicate must
        // mirror the view's meta-range render condition MEMBER-FOR-MEMBER.
        // Suppression = the range renders.
        if (report.HasCedhMetaRange)
        {
            return null;
        }

        // Commander cell participates only at brackets 2-3: dump means are bracket-agnostic and the
        // EDHREC population is casual-dominated, so a popular commander's mean would drown the
        // optimized/cEDH bracket signal.
        ManabaseCommanderBaseline? commanderRow = bracket is 2 or 3 && commanderNames.Count is 1 or 2
            ? _manabaseBaseline.TryGetCommanderBaseline(commanderNames)
            : null;

        ManabaseBaselineResult weighted = ManabaseBaselineWeighting.Compute(
            commanderRow?.AvgLands, null, null, commanderRow?.DeckCount ?? 0,
            row.AvgLands, null, null);

        bool commanderContributed = weighted.Lands.Source
            is ManabaseBaselineSource.Commander or ManabaseBaselineSource.Blended;

        return new ManabaseCommunityBaseline
        {
            Bracket = bracket,
            AvgLands = weighted.Lands.Value ?? row.AvgLands,
            DeckCount = commanderContributed ? commanderRow!.DeckCount : row.DeckCount,
            Source = commanderContributed ? ManabaseBaselineSnapshot.EdhrecAveragesSource : row.Source,
            BracketSource = bracketSource,
            ValueSource = weighted.Lands.Source,
            CommanderDisplayName = commanderContributed
                ? commanderRow!.PartnerName is null ? commanderRow.Name : $"{commanderRow.Name} + {commanderRow.PartnerName}"
                : null,
        };
    }

    // True only when the named flag exists in the snapshot AND is enabled. Fail-safe OFF: a missing
    // key returns false (unlike IFeatureFlagCache.IsEnabled, which defaults missing keys ON).
    private bool IsFlagOn(string key)
        => _featureFlags is { } flags
            && flags.Snapshot().TryGetValue(key, out bool enabled)
            && enabled;

    // Shared front half of both entry points: validate input, load + board-filter the deck, resolve
    // every card through Scryfall, and classify it into a ManabaseDeck (which carries the detected
    // cost suggestions). Stops short of the castability simulation so Load can reuse it cheaply.
    private async Task<ResolvedManabaseDeck> ResolveAndClassifyAsync(
        string deckSource,
        bool rampCreditV2,
        bool landRampSim,
        bool payLifeUntapped,
        bool checkLandUntapped,
        bool restrictedLands,
        bool commanderCastability,
        bool classifyPlanRoles,
        ManabaseMode mode,
        string? companionDesignator,
        string? selectedCommander,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deckSource))
        {
            throw new InvalidOperationException("Provide a public deck URL or paste a decklist.");
        }

        if (deckSource.Length > MaxDeckSourceChars)
        {
            throw new InvalidOperationException("That deck input is too large to analyze.");
        }

        DeckSourceLoadResult load;
        try
        {
            load = await _deckEntryLoader.LoadFromSourceAsync(deckSource, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DeckParseException exception)
        {
            // Surface a parse failure as a user-facing validation error, not a 500.
            throw new InvalidOperationException(exception.Message, exception);
        }

        // Moxfield plaintext exports carry no "Commander" section header — the commander is
        // simply the leading card. Reflag inferred commander(s) to the commander board so the
        // analyzer weights their colors and the callout names them, matching the deck-analysis
        // tool's behavior. No-op when the source already tagged a commander board.
        var entries = ReflagInferredCommanders(load.Entries);

        var deckCards = entries
            .Where(e => AnalyzedBoards.Contains(e.Board))
            .ToList();

        if (deckCards.Count == 0)
        {
            throw new InvalidOperationException("No mainboard or commander cards were found in that deck.");
        }

        if (deckCards.Count > MaxDeckCards)
        {
            throw new InvalidOperationException($"That deck has too many cards to analyze (limit {MaxDeckCards}).");
        }

        string? companionName = commanderCastability
            ? ResolveCompanionName(companionDesignator, load.DetectedCompanionName)
            : null;
        string? normalizedCompanionName = companionName is null ? null : CardNormalizer.Normalize(companionName);
        DeckEntry? excludedCompanionEntry = normalizedCompanionName is null
            ? null
            : deckCards.FirstOrDefault(entry =>
                string.Equals(entry.Board, "mainboard", StringComparison.OrdinalIgnoreCase)
                && string.Equals(CardNormalizer.Normalize(entry.Name), normalizedCompanionName, StringComparison.Ordinal));

        ScryfallCardNameIndex index = await ResolveCardsAsync(deckCards, cancellationToken).ConfigureAwait(false);
        ScryfallCardData? companionCard = null;
        if (commanderCastability && companionName is not null)
        {
            companionCard = excludedCompanionEntry is not null
                ? await ResolveCompanionFromDeckEntryAsync(index, excludedCompanionEntry, cancellationToken).ConfigureAwait(false)
                : await ResolveSingleCardAsync(companionName, cancellationToken).ConfigureAwait(false);
        }

        var deckEntries = new List<DeckCardEntry>();
        var unresolved = new List<string>();
        foreach (DeckEntry entry in deckCards)
        {
            if (excludedCompanionEntry is not null && ReferenceEquals(entry, excludedCompanionEntry))
            {
                continue;
            }

            ScryfallCardData? card;
            if (!index.TryResolve(entry.Name, entry.SetCode, entry.CollectorNumber, out card))
            {
                // The batch lookup missed this card — typically an exact printing Scryfall has no
                // record of (e.g. an etched/promo collector number). Reuse the shared exact-name
                // fallback that the comparison/analysis paths already use, then cache it in the
                // index so duplicate entries don't re-query.
                ScryfallCard? fallback = await _scryfallCardResolver
                    .SearchFallbackCardAsync(entry.Name, cancellationToken).ConfigureAwait(false);
                if (fallback is not null)
                {
                    card = ScryfallCardDataMapper.ToCardData(fallback);
                    index.Add(card, priority: SearchFallbackPriority);
                }
            }

            if (card is not null)
            {
                deckEntries.Add(new DeckCardEntry
                {
                    Card = card,
                    Quantity = entry.Quantity,
                    IsCommander = string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase),
                });
            }
            else
            {
                unresolved.Add(entry.Name);
            }
        }

        if (deckEntries.Count == 0)
        {
            throw new InvalidOperationException("Scryfall could not resolve any of the deck's cards; try again shortly.");
        }

        string? normalizedSelectedCommander = BoundCompanionName(selectedCommander) is { } boundedSelectedCommander
            ? CardNormalizer.Normalize(boundedSelectedCommander)
            : null;
        if (normalizedSelectedCommander is not null)
        {
            deckEntries = deckEntries
                .Select(entry =>
                {
                    bool isCommander = string.Equals(
                        CardNormalizer.Normalize(entry.Card.Name),
                        normalizedSelectedCommander,
                        StringComparison.Ordinal);
                    return entry.IsCommander == isCommander ? entry : entry with { IsCommander = isCommander };
                })
                .ToList();
        }

        CommanderValidationResult commanderValidation = ValidateResolvedCommanders(
            deckEntries,
            selectedCommanderSupplied: normalizedSelectedCommander is not null);
        deckEntries = commanderValidation.Entries;

        IReadOnlyList<CardFact> facts = ScryfallCardFactMapper.ToCardFacts(deckEntries);
        ManabaseDeck deck = ManabaseClassifier.Classify(
            facts,
            isSingleton: true,
            rampCreditV2: rampCreditV2,
            landRampSim: landRampSim,
            payLifeUntapped: payLifeUntapped,
            checkLandUntapped: checkLandUntapped,
            restrictedLands: restrictedLands);

        if (classifyPlanRoles)
        {
            deck = await TagPlanRolesAsync(deck, facts, deckCards, mode, cancellationToken).ConfigureAwait(false);
        }

        string decklistText = string.Join(
            "\n",
            deckCards.Select(e => $"{e.Quantity} {e.Name}"));

        // Land count matches ManabaseReport.ActualLands (the analyzer counts IsLand sources the
        // same way), so the loaded summary reads identically to the analyzed one.
        int landCount = deck.Sources.Count(s => s.IsLand);
        int cardCount = deckCards.Sum(e => e.Quantity);
        string inputSummary = $"{cardCount} cards · {landCount} lands"
            + (unresolved.Count > 0 ? $" · {unresolved.Count} unresolved" : string.Empty);

        return new ResolvedManabaseDeck(
            deck,
            unresolved,
            load.FallbackNotice,
            decklistText,
            inputSummary,
            deckEntries.Where(e => e.IsCommander).Select(e => e.Card.Name).ToList(),
            companionCard,
            commanderValidation.CommanderChoices,
            commanderValidation.SelectionRequired);
    }

    /// <summary>
    /// Tag each spell with its win-directed <see cref="PlanRole"/>s for the plan-presence stat and the
    /// cEDH interaction lens. Fetches the deck's Commander Spellbook combo pieces once and each spell's
    /// crowd categories, both fail-open (a network/DB error yields no roles for that card, never a
    /// failed analysis). Called when plan-presence is on, or when the cEDH interaction-lens flag is on
    /// in cEDH mode; otherwise the default path still skips this extra I/O.
    /// </summary>
    private async Task<ManabaseDeck> TagPlanRolesAsync(
        ManabaseDeck deck,
        IReadOnlyList<CardFact> facts,
        IReadOnlyList<DeckEntry> deckCards,
        ManabaseMode mode,
        CancellationToken cancellationToken)
    {
        // Source 2 (combo pieces), fetched once. Fail-open: a Spellbook outage leaves the set empty.
        var comboNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_spellbook is not null)
        {
            try
            {
                CommanderSpellbookResult? combos =
                    await _spellbook.FindCombosAsync(deckCards, cancellationToken).ConfigureAwait(false);
                if (combos is not null)
                {
                    foreach (SpellbookCombo combo in combos.IncludedCombos)
                    {
                        foreach (string cardName in combo.CardNames)
                        {
                            comboNames.Add(cardName);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Plan-presence: Commander Spellbook fetch failed; continuing without combo roles.");
            }
        }

        var factByName = new Dictionary<string, CardFact>(StringComparer.OrdinalIgnoreCase);
        foreach (CardFact fact in facts)
        {
            factByName[fact.Name] = fact;
        }

        // Source 1 (crowd categories): ONE batched lookup for the spells we will actually classify (those
        // with a resolved fact). A per-card loop here issued one DB query per non-land card, which serially
        // exhausted the request timeout on a full decklist (~65 sequential Postgres round-trips ~= 20s).
        // Batching collapses it to a single query.
        IReadOnlyDictionary<string, IReadOnlyList<string>> categoriesByName =
            await GetCategoriesFailOpenAsync(
                deck.Spells.Where(s => factByName.ContainsKey(s.Name)).Select(s => s.Name).ToList(),
                cancellationToken).ConfigureAwait(false);

        var tagged = new List<SpellRequirement>(deck.Spells.Count);
        foreach (SpellRequirement spell in deck.Spells)
        {
            PlanRole roles = PlanRole.None;
            bool interactionMeritPreGate = false;
            if (factByName.TryGetValue(spell.Name, out CardFact? fact))
            {
                IReadOnlyList<string> categories = categoriesByName.TryGetValue(spell.Name, out IReadOnlyList<string>? hit)
                    ? hit
                    : Array.Empty<string>();
                roles = PlanRoleClassifier.Classify(
                    fact,
                    categories,
                    comboNames.Contains(spell.Name),
                    mode,
                    out interactionMeritPreGate);
            }

            tagged.Add(spell with { PlanRoles = roles, IsInteractionSpell = interactionMeritPreGate });
        }

        return deck with { Spells = tagged };
    }

    // Source 1 (crowd categories), fail-open for the whole batch so a DB hiccup drops every card to the
    // heuristic tier rather than failing the analysis. One query, never one-per-card.
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetCategoriesFailOpenAsync(
        IReadOnlyCollection<string> cardNames, CancellationToken cancellationToken)
    {
        if (_categoryKnowledge is null || cardNames.Count == 0)
        {
            return EmptyCategories;
        }

        try
        {
            return await _categoryKnowledge.GetCategoriesForNamesAsync(cardNames, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Plan-presence: batch category lookup failed; using heuristics only.");
            return EmptyCategories;
        }
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyCategories =
        new Dictionary<string, IReadOnlyList<string>>();

    // Reflags the leading Moxfield-ordering commander(s) to the commander board when the
    // source carried no explicit commander tag. Returns the input unchanged when a commander
    // board already exists or none can be inferred.
    private static List<DeckEntry> ReflagInferredCommanders(List<DeckEntry> entries)
    {
        IReadOnlyList<string> commanderNames = CommanderInference.InferLeadingCommanderNames(entries);
        if (commanderNames.Count == 0)
        {
            return entries;
        }

        var commanderNameSet = commanderNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only reflag the analyzed boards. The inferred commander is always a leading mainboard
        // entry, so restricting the promotion here keeps a same-named sideboard/maybeboard copy
        // from being pulled into the analyzed set as a second "commander".
        return entries
            .Select(entry => commanderNameSet.Contains(entry.Name)
                && !string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                ? entry with { Board = "commander" }
                : entry)
            .ToList();
    }

    private static CommanderValidationResult ValidateResolvedCommanders(
        List<DeckCardEntry> entries,
        bool selectedCommanderSupplied)
    {
        var validatedEntries = new List<DeckCardEntry>(entries.Count);
        var commanderChoices = new List<string>();
        var seenChoices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hadFlaggedCommander = entries.Any(entry => entry.IsCommander);

        foreach (DeckCardEntry entry in entries)
        {
            string typeLine = entry.Card.TypeLine ?? string.Empty;

            // Only the planeswalker path reads oracle text ("can be your commander"); the other
            // eligibility branches look at the type line alone, so skip the per-card oracle flatten
            // for the common (creature/vehicle/enchantment) cards.
            string? oracleText = typeLine.Contains("Planeswalker", StringComparison.OrdinalIgnoreCase)
                ? NormalizeOracleText(entry.Card)
                : null;
            bool eligible = CommanderEligibility.IsEligible(typeLine, oracleText);
            if (eligible && seenChoices.Add(entry.Card.Name))
            {
                commanderChoices.Add(entry.Card.Name);
            }

            validatedEntries.Add(entry.IsCommander && !eligible
                ? entry with { IsCommander = false }
                : entry);
        }

        bool noCommanderRemains = validatedEntries.All(entry => !entry.IsCommander);
        bool selectionRequired = noCommanderRemains && (selectedCommanderSupplied || hadFlaggedCommander);
        return new CommanderValidationResult(validatedEntries, commanderChoices, selectionRequired);
    }

    private static string NormalizeOracleText(ScryfallCardData card)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(card.OracleText))
        {
            parts.Add(CollapseWhitespace(card.OracleText));
        }

        foreach (ScryfallFaceData face in card.CardFaces ?? Array.Empty<ScryfallFaceData>())
        {
            var faceParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(face.Name))
            {
                faceParts.Add(face.Name.Trim());
            }

            if (!string.IsNullOrWhiteSpace(face.ManaCost))
            {
                faceParts.Add(face.ManaCost.Trim());
            }

            if (!string.IsNullOrWhiteSpace(face.TypeLine))
            {
                faceParts.Add(CollapseWhitespace(face.TypeLine));
            }

            if (!string.IsNullOrWhiteSpace(face.OracleText))
            {
                faceParts.Add(CollapseWhitespace(face.OracleText));
            }

            if (faceParts.Count > 0)
            {
                parts.Add(string.Join(" | ", faceParts));
            }
        }

        return string.Join(" ", parts);
    }

    private static string CollapseWhitespace(string value)
        => string.Join(" ", (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // Internal carrier for the shared resolve+classify stage (no report yet).
    private sealed record ResolvedManabaseDeck(
        ManabaseDeck Deck,
        IReadOnlyList<string> Unresolved,
        string? FallbackNotice,
        string DecklistText,
        string InputSummary,
        IReadOnlyList<string> CommanderNames,
        ScryfallCardData? CompanionCard,
        IReadOnlyList<string> CommanderChoices,
        bool CommanderSelectionRequired);

    private sealed record CommanderValidationResult(
        List<DeckCardEntry> Entries,
        IReadOnlyList<string> CommanderChoices,
        bool SelectionRequired);

    private static string? ResolveCompanionName(string? designator, string? detected)
        => BoundCompanionName(designator) ?? BoundCompanionName(detected);

    private static string? BoundCompanionName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim();
        return trimmed.Length <= MaxCompanionNameLength
            ? trimmed
            : trimmed[..MaxCompanionNameLength];
    }

    private async Task<ScryfallCardData?> ResolveCompanionFromDeckEntryAsync(
        ScryfallCardNameIndex index,
        DeckEntry companionEntry,
        CancellationToken cancellationToken)
    {
        if (index.TryResolve(companionEntry.Name, companionEntry.SetCode, companionEntry.CollectorNumber, out ScryfallCardData? hit))
        {
            return hit;
        }

        ScryfallCard? fallback = await _scryfallCardResolver
            .SearchFallbackCardAsync(companionEntry.Name, cancellationToken).ConfigureAwait(false);
        if (fallback is null)
        {
            return null;
        }

        ScryfallCardData data = ScryfallCardDataMapper.ToCardData(fallback);
        index.Add(data, priority: SearchFallbackPriority);
        return data;
    }

    private async Task<ScryfallCardData?> ResolveSingleCardAsync(string cardName, CancellationToken cancellationToken)
    {
        // The single-name Scryfall resolve (collection lookup + exact-name fallback) lives on the
        // resolver; this service only maps the result into its ScryfallCardData shape.
        ScryfallCard? card = await _scryfallCardResolver.ResolveSingleAsync(cardName, cancellationToken).ConfigureAwait(false);
        return card is null ? null : ScryfallCardDataMapper.ToCardData(card);
    }

    // Batch-resolve the deck's cards through Scryfall's collection endpoint, preferring an exact
    // printing (set + collector number) so alternate / flavor names still resolve.
    private async Task<ScryfallCardNameIndex> ResolveCardsAsync(
        IReadOnlyList<DeckEntry> deckCards,
        CancellationToken cancellationToken)
    {
        // Distinct identifiers: printing key when known, else a name key.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identifiers = new List<(string? SetCode, string? CollectorNumber, string? Name)>();
        foreach (DeckEntry entry in deckCards)
        {
            string? printing = ScryfallCardNameIndex.PrintingKey(entry.SetCode, entry.CollectorNumber);
            string? normalizedName = printing is null
                ? CoreScryfallCollectionIdentifier.ToFaceIdentifier(entry.Name)
                : null;
            string key = printing ?? $"name:{normalizedName}";
            if (!seen.Add(key))
            {
                continue;
            }

            identifiers.Add(printing is not null
                ? (entry.SetCode, entry.CollectorNumber, null)
                : (null, null, normalizedName));
        }

        var identifiersToSubmit = new List<(string? SetCode, string? CollectorNumber, string? Name, int GlobalPosition)>(identifiers.Count);
        var positionedCards = new List<(ScryfallCard Card, int GlobalPosition)>(identifiers.Count);
        for (int globalPosition = 0; globalPosition < identifiers.Count; globalPosition++)
        {
            var identifier = identifiers[globalPosition];
            ScryfallCard? cachedCard = null;
            bool cached = identifier.Name is not null
                ? _collectionCardCache?.TryGetName(identifier.Name, out cachedCard) == true
                : _collectionCardCache?.TryGetPrinting(identifier.SetCode!, identifier.CollectorNumber!, out cachedCard) == true;
            if (cached)
            {
                if (cachedCard is not null)
                {
                    positionedCards.Add((cachedCard, globalPosition));
                }

                continue;
            }

            identifiersToSubmit.Add((identifier.SetCode, identifier.CollectorNumber, identifier.Name, globalPosition));
        }

        var unpairedCards = new List<ScryfallCard>();
        foreach (var batch in identifiersToSubmit.Chunk(ScryfallLimits.CollectionBatchSize))
        {
            var positionedReturnedCards = new Dictionary<int, int>();
            var request = new ScryfallCollectionProtocolRequest(batch.Select(identifier => identifier.Name is null
                ? ScryfallCollectionNameIdentifier.ForPrinting(identifier.SetCode!, identifier.CollectorNumber!)
                : ScryfallCollectionNameIdentifier.ForName(identifier.Name!)).ToArray());

            ScryfallCollectionProtocolResponse response =
                await _collectionProtocol.ResolveAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices || !response.HasPayload)
            {
                throw new HttpRequestException(
                    $"Scryfall card lookup (cards/collection) returned HTTP {(int)response.StatusCode} during mana-base analysis.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            var returnedCards = response.Cards.Select((card, cardIndex) => (Card: card, CardIndex: cardIndex)).ToList();
            var pairedCardIndexes = new HashSet<int>();
            foreach (var submission in batch.Where(identifier => identifier.Name is null))
            {
                var matchingCards = returnedCards.Where(card => string.Equals(card.Card.SetCode, submission.SetCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(card.Card.CollectorNumber, submission.CollectorNumber, StringComparison.OrdinalIgnoreCase)).ToArray();
                // Why: the seen de-dup guarantees submission uniqueness, so only returned-card ambiguity is live.
                if (matchingCards.Length == 1)
                {
                    _collectionCardCache?.SetPrintingPositive(submission.SetCode!, submission.CollectorNumber!, matchingCards[0].Card);
                    pairedCardIndexes.Add(matchingCards[0].CardIndex);
                    positionedReturnedCards.Add(matchingCards[0].CardIndex, submission.GlobalPosition);
                }
            }

            foreach (var submission in batch.Where(identifier => identifier.Name is not null))
            {
                var matchingCards = returnedCards.Where(card => !pairedCardIndexes.Contains(card.CardIndex)
                    && string.Equals(CoreScryfallCollectionIdentifier.ToFaceIdentifier(card.Card.Name), submission.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                // Why: the seen de-dup guarantees submission uniqueness, so only returned-card ambiguity is live.
                if (matchingCards.Length == 1)
                {
                    _collectionCardCache?.SetNamePositive(submission.Name!, matchingCards[0].Card);
                    positionedReturnedCards.Add(matchingCards[0].CardIndex, submission.GlobalPosition);
                }
                else if (response.NotFound.Any(notFound => string.Equals(notFound.Name, submission.Name, StringComparison.Ordinal)))
                {
                    _collectionCardCache?.SetNameCollectionMiss(submission.Name!);
                }
            }

            foreach (var positionedCard in positionedReturnedCards)
            {
                positionedCards.Add((returnedCards[positionedCard.Key].Card, positionedCard.Value));
            }

            unpairedCards.AddRange(returnedCards.Where(card => !positionedReturnedCards.ContainsKey(card.CardIndex)).Select(card => card.Card));
        }

        // Why: precedence is now stated per card rather than implied by call order, so neither the
        // order of these loops nor chunk membership (which shifts with cache warmth) can change the
        // winner. The index resolves collisions from the priorities below.
        var index = new ScryfallCardNameIndex();
        foreach (var (card, globalPosition) in positionedCards)
        {
            index.Add(ScryfallCardDataMapper.ToCardData(card), priority: PairedPriority(globalPosition));
        }

        foreach (var card in unpairedCards)
        {
            index.Add(ScryfallCardDataMapper.ToCardData(card), priority: UnpairedPriority);
        }

        return index;
    }
}
