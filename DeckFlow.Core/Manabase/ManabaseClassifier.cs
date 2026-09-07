using System.Text.RegularExpressions;

using DeckFlow.Core.Analysis;

namespace DeckFlow.Core.Manabase;

/// <summary>
/// Turns a list of <see cref="CardFact"/> (Scryfall-shaped data) into a
/// <see cref="ManabaseDeck"/> ready for <see cref="ManabaseAnalyzer"/>. Applies Karsten's
/// source-counting weights (full-weight lands, mana dorks at 0.5, rocks at 0.75, basic
/// fetches in 3+ color decks at ~0.67) and models each land's tapped/untapped state for the
/// castability sim. Spell//land MDFC backs are always counted as real lands (full color weight,
/// tapped/pay-life state read from the land face). Several other behaviors are flag-gated (see the
/// <see cref="Classify"/> parameters, bundled in prod under <c>analysis.manabase.accuracy</c>):
/// pay-life shocklands and bond/check/Snarl lands are modeled untapped when their condition reliably
/// holds; with those flags off, those lands stay on the historic always-tapped path.
/// </summary>
public static class ManabaseClassifier
{
    private static readonly Regex ArtifactTokenCreationRegex = new(
        @"create\b[\s\S]*?\b(?:treasure|clue|food|blood|gold|powerstone|map|artifact token)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches an always-on static generic reducer: an optional type scope (instant/sorcery/
    // creature/artifact words) immediately before "spells you cast cost {N} less". The "you cast"
    // anchor excludes opponent-only and activated-ability discounts. Oracle text is lower-cased.
    private static readonly Regex StaticReducerRegex = new(
        @"(?<scope>(?:[a-z]+ )*?)spells you cast cost \{(?<amt>\d+)\} less",
        RegexOptions.Compiled);

    // Self-cost detection (DetectSelfCost). Oracle text is lower-cased before matching.
    // Evoke / suspend may carry a braced mana cost (Shriekmaw "evoke {1}{B}", Crashing Footfalls
    // "suspend 1—{g}") or a non-mana cost (Grief "evoke—exile a black card"); capture the braced
    // cost when present, else treat the alternative as free. Dash variants -/–/— are tolerated.
    private static readonly Regex EvokeCostRegex = new(
        @"evoke[\s—–-]*((?:\{[^}]+\})+)", RegexOptions.Compiled);

    private static readonly Regex SuspendCostRegex = new(
        @"suspend\s+\d+[\s—–-]*((?:\{[^}]+\})+)", RegexOptions.Compiled);

    // "This spell costs {N} less to cast for each <thing>" — a board-scaling SELF reduction
    // (Blasphemous Act). Self-anchored on "this spell" so it never fires on a card that discounts
    // OTHER spells with a "for each" rider. Distinct from the deck-wide StaticReducerRegex.
    private static readonly Regex ScalingSelfReducerRegex = new(
        @"this spell costs \{\d+\} less to cast for each", RegexOptions.Compiled);

    // "This spell costs {X} less to cast, where X is the greatest power among creatures you control"
    // (The Skullspore Nexus). The reduction is board-dependent, so it is resolved against the deck's
    // greatest FIXED creature power as the optimistic on-board value (see DetectSelfCost).
    private static readonly Regex GreatestPowerReducerRegex = new(
        @"this spell costs \{x\} less to cast,? where x is the greatest power among creatures you control",
        RegexOptions.Compiled);

    // Cheap setup smoothing Karsten treats as a small any-color source credit. Reminder text is
    // stripped before matching, so parenthesized glossary text never fabricates a real scry effect.
    private static readonly Regex ScryRegex = new(@"\bscry\s+([1-9]\d*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Detect "{T}, Tap ..." mana abilities only when the tap-cost and the "add" text stay inside
    // one clause; otherwise unrelated quoted abilities can fabricate a false positive.
    private static readonly Regex TapClauseAddRegex = new(@"\{t\}, tap[^.\n""]*:\s*add", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Build a <see cref="ManabaseDeck"/> from classified card facts.</summary>
    /// <param name="cards">All cards in the deck (including any commanders, flagged).</param>
    /// <param name="isSingleton">True for Commander/singleton; false for 60-card constructed.</param>
    /// <param name="rampCreditV2">
    /// MQ-03 flag. When false (default), the ramp/draw land-target credit uses the historic broad
    /// <see cref="IsRampOrDraw"/> predicate (byte-identical). When true, it uses the narrowed
    /// <see cref="IsRepeatableRampOrDraw"/> — only repeatable ramp (mana permanents incl. enchantment
    /// ramp, land-ramp onto the battlefield) and true card draw earn the −0.28 credit; one-shot
    /// rituals and Treasure-makers no longer do. Affects only <see cref="ManabaseDeck.RampAndDrawUnderThree"/>.
    /// </param>
    /// <param name="landRampSim">
    /// MQ-03 70-03b flag. When true, repeatable land-ramp-to-battlefield spells (Cultivate / Rampant
    /// Growth) are added to <see cref="ManabaseDeck.Sources"/> as colorless, non-land ramp sources
    /// (deploy cost = the spell's mana value) so the castability simulator models the fetched land's
    /// mana. Colorless + non-land → never changes color counts or the land total. When false (default),
    /// no such source is added (byte-identical sim).
    /// </param>
    /// <param name="payLifeUntapped">
    /// When true, shock-style lands whose oracle offers "pay N life" to avoid entering tapped are
    /// modeled as untapped lands. When false (default), they remain on the historic tapped path.
    /// </param>
    /// <param name="checkLandUntapped">
    /// When true, board/hand-conditional lands are modeled untapped where the condition is reliably
    /// met: bond lands (always, in multiplayer Commander) and check lands / Snarls when the deck runs
    /// enough matching-type sources. When false (default), they stay on the historic always-tapped path.
    /// </param>
    /// <param name="restrictedLands">
    /// When true, restricted-color lands (Cavern of Souls, Unclaimed Territory, Ancient Ziggurat,
    /// Nykthos, Shrine to Nyx) use the D-03 composition-gated approximation. When false (default),
    /// they stay on the historic unrestricted classifier path.
    /// </param>
    public static ManabaseDeck Classify(IReadOnlyList<CardFact> cards, bool isSingleton = true, bool rampCreditV2 = false, bool landRampSim = false, bool payLifeUntapped = false, bool checkLandUntapped = false, bool restrictedLands = false)
    {
        ArgumentNullException.ThrowIfNull(cards);

        int deckColorCount = CountDeckColors(cards);
        CreatureComposition creatureComposition = restrictedLands
            ? ComputeCreatureComposition(cards)
            : CreatureComposition.Empty;
        (Dictionary<string, HashSet<ManaColor>> fetchTypeColors, HashSet<ManaColor> fetchBasicColors) =
            BuildFetchableColors(cards);
        LandClassificationContext landClassificationContext = new()
        {
            DeckColorCount = deckColorCount,
            FetchTypeColors = fetchTypeColors,
            FetchBasicColors = fetchBasicColors,
            PayLifeUntapped = payLifeUntapped,
            CheckLandUntapped = checkLandUntapped,
            RestrictedLands = restrictedLands,
            AllCards = cards,
            CreatureComposition = creatureComposition,
        };

        var sources = new List<ManaSource>();
        var spells = new List<SpellRequirement>();
        var reducers = new List<CostReducer>();
        var granters = new List<GranterScope>();
        var suggestions = new List<CostSuggestion>();
        var suggestedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unsupported = new List<UnsupportedInteraction>();
        var unsupportedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var restrictedSourceLandNames = new List<string>();
        var restrictedSourceLandNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalCards = 0;
        int commanderCount = 0;
        double mvSum = 0;
        int nonlandCount = 0;
        int rampUnderThree = 0;
        int scrySourceCreditCopies = 0;
        int rampPieces = 0;
        int drawPieces = 0;
        int bothPieces = 0;
        var rampNames = new List<string>();
        int fastMana = 0;
        var oneShots = new List<OneShotMana>();

        // Greatest FIXED creature power in the deck — the optimistic on-board value for board-scaling
        // self reducers keyed on "the greatest power among creatures you control" (The Skullspore
        // Nexus). Variable-power creatures (*goyf, Power == null) contribute nothing.
        int maxCreaturePower = 0;
        foreach (CardFact c in cards)
        {
            if (c.Power is int p && p > maxCreaturePower && IsType(c.TypeLine, "Creature"))
            {
                maxCreaturePower = p;
            }
        }

        foreach (CardFact card in cards)
        {
            totalCards += card.Quantity;
            if (card.IsCommander)
            {
                commanderCount += card.Quantity;
            }

            bool frontIsLand = IsLandType(card.TypeLine);
            if (frontIsLand)
            {
                AddLandCopies(
                    sources,
                    card,
                    landClassificationContext,
                    restrictedSourceLandNames,
                    restrictedSourceLandNameSet);
                continue;
            }

            // Spell front: contributes to the curve.
            if (!card.IsCommander)
            {
                mvSum += card.ManaValue * card.Quantity;
                nonlandCount += card.Quantity;
            }

            ParsedManaCost cost = ManaCostParser.Parse(card.ManaCost);

            // Detect a below-printed effective cost once; reused for the auto-apply decision AND the
            // visible suggestion entry below (so the two never disagree).
            (string EffectiveCost, string Reason)? selfCost = DetectSelfCost(card, maxCreaturePower);

            // Intrinsic, always-on "costs {X} less, where X is the greatest power among creatures you
            // control" reducer (The Skullspore Nexus): unlike evoke/pitch (opt-in alternative casts),
            // this discount is automatic, so apply it to the default analysis using the deck's
            // greatest fixed creature power. User overrides (the cost box) still take precedence later.
            string? intrinsicReduced = GreatestPowerEffectiveCost(card, maxCreaturePower);

            // P3 free-cost auto-apply: a SELF-ANCHORED free cast ("rather than pay this spell's mana
            // cost" / "cast this spell without paying its mana cost" — Force of Negation, Fierce
            // Guardianship, Deflecting Swat, Flawless Maneuver) is realistically cast for free in the
            // decks that run it (the commander is on the battlefield / a pitch card is in hand). Apply
            // it to the default analysis like the greatest-power reducer, so these stop reading as false
            // "demanding" cards at their printed colored cost. The visible suggestion below still shows
            // it (now noted as auto-applied) and a user override still wins. Only the FREE category
            // (effective "0") auto-applies — evoke/suspend stay opt-in suggestions (a player may choose
            // not to evoke), and the greatest-power case is handled by intrinsicReduced above.
            bool freeAutoApplied = false;
            if (intrinsicReduced is not null)
            {
                ParsedManaCost reduced = ManaCostParser.Parse(ManaCostParser.NormalizeToBraced(intrinsicReduced));
                AddSpellRequirement(spells, card, reduced, costOverridden: true);
            }
            else if (selfCost is (string freeCost, "free / alternative cost"))
            {
                ParsedManaCost reduced = ManaCostParser.Parse(ManaCostParser.NormalizeToBraced(freeCost));
                AddSpellRequirement(spells, card, reduced, costOverridden: true);
                freeAutoApplied = true;
            }
            else
            {
                AddSpellRequirement(spells, card, cost);
            }

            // MQ-04: disclose what the analysis cannot fully model rather than silently absorbing it.
            // X/variable spells are dropped from castability entirely; hybrid/Phyrexian pips are
            // flexible so they carry no hard color requirement (Karsten-correct, but then the color
            // need is approximated). One entry per card, X taking priority over hybrid.
            string? unsupportedReason = cost.HasVariableCost
                ? "Variable (X) cost — castability not simulated"
                : (card.ManaCost?.Contains('/', StringComparison.Ordinal) ?? false)
                    ? "Flexible split pips (hybrid / Phyrexian / twobrid) — color requirement approximated"
                    : null;
            if (unsupportedReason is not null && unsupportedNames.Add(card.Name))
            {
                unsupported.Add(new UnsupportedInteraction { Name = card.Name, Reason = unsupportedReason });
            }

            if (card.ManaValue <= 2 && (rampCreditV2 ? IsRepeatableRampOrDraw(card) : IsRampOrDraw(card)))
            {
                rampUnderThree += card.Quantity;
                rampNames.Add(card.Name);
            }

            if (QualifiesForScrySourceCredit(card))
            {
                scrySourceCreditCopies += card.Quantity;
            }

            SourceCapabilities sourceCapabilities = GetSourceCapabilities(card);

            bool isRampPieceForBudget = IsRampPieceForBudget(card);
            bool isDrawPieceForBudget = IsDrawPieceForBudget(card);
            if (isRampPieceForBudget)
            {
                rampPieces += card.Quantity;
            }

            if (isDrawPieceForBudget)
            {
                drawPieces += card.Quantity;
            }

            if (isRampPieceForBudget && isDrawPieceForBudget)
            {
                bothPieces += card.Quantity;
            }

            // 0-cost artifact fast mana (Lotus Petal, Mana Crypt) earns a land-target credit. MDFC land
            // backs are modeled as real lands (§1.4), so they raise actualLands directly and never enter
            // the fast-mana bucket.
            if (!card.HasLandFace && card.ManaValue == 0 && IsType(card.TypeLine, "Artifact") && ProducesMana(card))
            {
                fastMana += card.Quantity;
            }

            // One-shot burst mana (instant/sorcery rituals — Dark Ritual). Inert data on the deck until
            // the ritual-burst sim path consumes it; artifact fast mana stays in the FastMana lane above.
            OneShotMana? oneShot = DetectOneShotBurstMana(card);
            if (oneShot is not null)
            {
                for (int i = 0; i < card.Quantity; i++)
                {
                    oneShots.Add(oneShot);
                }
            }

            AddPartialSources(sources, card, sourceCapabilities);

            // 70-03b: model repeatable land-ramp as a colorless, non-land ramp source (one per copy) so
            // the simulator credits the fetched land's mana. Colorless (Produces empty) → no color-count
            // change; non-land → no land-count / mulligan inflation; deploy cost = the spell's MV.
            // Why: the sim intentionally does NOT thin the library after the fetch resolves; keeping the
            // fetched land as delayed mana while leaving draw density untouched is a known approximation,
            // and the two errors partially offset over the short turn window we simulate.
            if (landRampSim && IsLandRampToBattlefield(card))
            {
                for (int i = 0; i < card.Quantity; i++)
                {
                    sources.Add(new ManaSource
                    {
                        Name = card.Name,
                        Produces = System.Array.Empty<ManaColor>(),
                        IsLand = false,
                        Weight = 1.0,
                        ManaAmount = 1,
                        DeployCost = Math.Max(1, (int)Math.Round(card.ManaValue, MidpointRounding.AwayFromZero)),
                        IsSnow = sourceCapabilities.IsSnow,
                        ProducesColorless = sourceCapabilities.ProducesColorless,
                    });
                }
            }

            // Detect always-on static cost reducers and mana-ability granters (one per copy).
            CostReducer? reducer = DetectCostReducer(card);
            if (reducer is not null)
            {
                for (int i = 0; i < card.Quantity; i++)
                {
                    reducers.Add(reducer);
                }
            }

            GranterScope? grant = DetectGranter(card);
            if (grant is not null)
            {
                for (int i = 0; i < card.Quantity; i++)
                {
                    granters.Add(grant.Value);
                }
            }

            // Alt/reduced-cost suggestion (free/pitch, board-scaling self-reducer, evoke/suspend).
            // One per distinct card name. Most stay suggestion-only (pre-fill the override box); the
            // free/alt-cost category is now AUTO-APPLIED above (P3), so we annotate its reason to say so
            // — the entry stays in the list for visibility, no longer a false "demanding" flag.
            if (suggestedNames.Add(card.Name) && selfCost is (string effCost, string reason))
            {
                suggestions.Add(new CostSuggestion
                {
                    Name = card.Name,
                    EffectiveCost = effCost,
                    Reason = freeAutoApplied ? reason + " — auto-applied" : reason,
                });
            }
        }

        // Mana-ability granters add conditional weighted any-color sources for the creatures they
        // enable (a second pass: it needs the full creature list and the already-built sources).
        AddGrantedSources(sources, cards, granters, deckColorCount);

        int commanderColorMask = ManabaseColorMask.CommanderColorMask(spells);
        int legendaryPermanentCount = CountByQuantity(cards, IsLegendaryAmberSupport);
        int artifactCount = CountByQuantity(cards, card => IsType(card.TypeLine, "Artifact"));
        int artifactTokenCreatorCount = CountByQuantity(cards, CreatesArtifactTokens);
        double effectiveArtifactSupport = artifactCount + (0.5 * artifactTokenCreatorCount);
        (IReadOnlyList<ManaSource> adjustedSources, int adjustedFastMana) = ConditionalMoxHeuristics.Apply(
            sources,
            fastMana,
            commanderColorMask,
            legendaryPermanentCount,
            effectiveArtifactSupport);

        double avgMv = nonlandCount > 0 ? mvSum / nonlandCount : 0;

        return new ManabaseDeck
        {
            TotalCards = totalCards,
            CommanderCount = commanderCount,
            Sources = adjustedSources,
            Spells = spells,
            AverageManaValue = Math.Round(avgMv, 2),
            RampAndDrawUnderThree = rampUnderThree,
            RampAndDrawNames = rampNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RampPieceCount = rampPieces - (0.5 * bothPieces),
            DrawPieceCount = drawPieces - (0.5 * bothPieces),
            RampDrawBothCount = bothPieces,
            FastMana = adjustedFastMana,
            OneShots = oneShots,
            ScrySourceCreditCopies = scrySourceCreditCopies,
            IsSingleton = isSingleton,
            CostReduction = reducers,
            CostSuggestions = suggestions,
            UnsupportedInteractions = unsupported,
            RestrictedSourceLandNames = restrictedLands
                ? restrictedSourceLandNames
                : Array.Empty<string>(),
        };
    }

    private static int CountByQuantity(IReadOnlyList<CardFact> cards, Func<CardFact, bool> predicate)
    {
        int count = 0;
        foreach (CardFact card in cards)
        {
            if (predicate(card))
            {
                count += card.Quantity;
            }
        }

        return count;
    }

    // Whole-type-line check (not the front-face-only IsLegendary): Mox Amber sees any legendary
    // creature/planeswalker permanent you control, so a legendary back face still counts.
    private static bool IsLegendaryAmberSupport(CardFact card)
        => IsType(card.TypeLine, "Legendary")
            && (IsType(card.TypeLine, "Creature") || IsType(card.TypeLine, "Planeswalker"));

    private static bool CreatesArtifactTokens(CardFact card)
        => !string.IsNullOrWhiteSpace(card.OracleText)
            && ArtifactTokenCreationRegex.IsMatch(card.OracleText);

    private static int CountDeckColors(IReadOnlyList<CardFact> cards)
    {
        // Deck color count = colors the deck actually demands (hard pips in card costs incl.
        // the commander). Off-color fixers (Signet, Birds, Treasures) must NOT inflate it,
        // or a 2-color deck reads as 5-color and the fetch-weighting heuristic over-penalizes.
        var colors = new HashSet<ManaColor>();
        foreach (CardFact card in cards)
        {
            foreach (KeyValuePair<ManaColor, int> pip in ManaCostParser.Parse(card.ManaCost).Pips)
            {
                if (pip.Value > 0 && pip.Key != ManaColor.Colorless)
                {
                    colors.Add(pip.Key);
                }
            }
        }

        return colors.Count;
    }

    private sealed record LandSourceClassification
    {
        public required IReadOnlyList<ManaColor> Produces { get; init; }

        public required bool EntersUntapped { get; init; }

        public double Weight { get; init; } = 1.0;

        public CountConditionKind CountCondition { get; init; } = CountConditionKind.None;

        public int CountThreshold { get; init; }

        public IReadOnlyList<string> CountTypeFilter { get; init; } = Array.Empty<string>();

        public ManaSource? ConditionalAnyColorSource { get; init; }

        public bool IsRestrictedSourceApproximation { get; init; }
    }

    /// <summary>Loop-invariant deck context used while classifying land copies.</summary>
    private readonly record struct LandClassificationContext
    {
        public required int DeckColorCount { get; init; }

        public required Dictionary<string, HashSet<ManaColor>> FetchTypeColors { get; init; }

        public required HashSet<ManaColor> FetchBasicColors { get; init; }

        public required bool PayLifeUntapped { get; init; }

        public required bool CheckLandUntapped { get; init; }

        public required bool RestrictedLands { get; init; }

        public required IReadOnlyList<CardFact> AllCards { get; init; }

        public required CreatureComposition CreatureComposition { get; init; }
    }

    /// <summary>Builds a special-land classification from a regex match and the current land context.</summary>
    private delegate LandSourceClassification? SpecialLandRuleBuilder(
        Match match,
        CardFact card,
        IReadOnlyList<CardFact> allCards,
        IReadOnlyList<ManaColor> produces,
        bool defaultUntapped,
        CreatureComposition creatureComposition,
        string text);

    /// <summary>Ordered first-match-wins rule for special-land classification.</summary>
    private sealed record SpecialLandRule
    {
        public bool RequiresCheckLandUntapped { get; init; }

        public bool RequiresRestrictedLands { get; init; }

        public required Regex Pattern { get; init; }

        public required SpecialLandRuleBuilder Build { get; init; }
    }

    private static void AddLandCopies(
        List<ManaSource> sources,
        CardFact card,
        LandClassificationContext context,
        List<string> restrictedSourceLandNames,
        HashSet<string> restrictedSourceLandNameSet)
    {
        IReadOnlyList<ManaColor> produces = MapColors(card.ProducedMana);
        if (produces.Count == 0)
        {
            // Fetchlands report empty produced_mana on Scryfall (they tap for no mana directly),
            // but they effectively supply the colors of the lands they can fetch. Derive those
            // from the basic land types named in the fetch's oracle text so a Flooded Strand
            // counts as a white AND blue source, not colorless.
            produces = FetchLandColors(card, context.FetchTypeColors, context.FetchBasicColors);
        }

        bool basicFetch = IsBasicFetch(card);
        // A choice-fetch in a 3+ color deck can only grab one color at a time.
        double weight = basicFetch && context.DeckColorCount >= 3 ? 0.67 : 1.0;
        bool untapped = !EntersTapped(card)
            || (context.PayLifeUntapped && HasPayLifeUntappedClause(card))
            || (context.CheckLandUntapped && IsConditionallyUntapped(card, context.AllCards));
        LandSourceClassification classification = (context.CheckLandUntapped || context.RestrictedLands)
            ? ClassifySpecialLand(
                card,
                context.AllCards,
                produces,
                untapped,
                context.CheckLandUntapped,
                context.RestrictedLands,
                context.CreatureComposition)
            : new LandSourceClassification
            {
                Produces = produces,
                EntersUntapped = untapped,
            };

        if (classification.IsRestrictedSourceApproximation
            && restrictedSourceLandNameSet.Add(card.Name))
        {
            restrictedSourceLandNames.Add(card.Name);
        }

        SourceCapabilities sourceCapabilities = GetSourceCapabilities(card);

        for (int i = 0; i < card.Quantity; i++)
        {
            sources.Add(new ManaSource
            {
                Name = card.Name,
                Produces = classification.Produces,
                Weight = weight * classification.Weight,
                EntersUntapped = classification.EntersUntapped,
                IsCommander = card.IsCommander,
                ManaAmount = card.ManaAmount, // MQ-02: e.g. Ancient Tomb (a land) makes 2.
                IsSnow = sourceCapabilities.IsSnow,
                ProducesColorless = sourceCapabilities.ProducesColorless,
                CountCondition = classification.CountCondition,
                CountThreshold = classification.CountThreshold,
                CountTypeFilter = classification.CountTypeFilter,
            });

            if (classification.ConditionalAnyColorSource is ManaSource conditionalSource)
            {
                sources.Add(conditionalSource with { IsCommander = card.IsCommander });
            }
        }
    }

    private static LandSourceClassification ClassifySpecialLand(
        CardFact card,
        IReadOnlyList<CardFact> allCards,
        IReadOnlyList<ManaColor> produces,
        bool defaultUntapped,
        bool checkLandUntapped,
        bool restrictedLands,
        CreatureComposition creatureComposition)
    {
        string text = ReminderTextRegex.Replace(card.OracleText ?? string.Empty, string.Empty);
        if (text.Length == 0)
        {
            return new LandSourceClassification
            {
                Produces = produces,
                EntersUntapped = defaultUntapped,
            };
        }

        foreach (SpecialLandRule rule in SpecialLandRules)
        {
            if (rule.RequiresCheckLandUntapped && !checkLandUntapped)
            {
                continue;
            }

            if (rule.RequiresRestrictedLands && !restrictedLands)
            {
                continue;
            }

            Match match = rule.Pattern.Match(text);
            if (!match.Success)
            {
                continue;
            }

            if (rule.Build(match, card, allCards, produces, defaultUntapped, creatureComposition, text) is LandSourceClassification classification)
            {
                return classification;
            }
        }

        return new LandSourceClassification
        {
            Produces = produces,
            EntersUntapped = defaultUntapped,
        };
    }

    private static LandSourceClassification BuildFastLandClassification(
        Match match,
        CardFact card,
        IReadOnlyList<CardFact> allCards,
        IReadOnlyList<ManaColor> produces,
        bool defaultUntapped,
        CreatureComposition creatureComposition,
        string text) =>
        new()
        {
            Produces = produces,
            EntersUntapped = defaultUntapped,
            CountCondition = CountConditionKind.FastLand,
            CountThreshold = 2,
        };

    private static LandSourceClassification BuildSlowLandClassification(
        Match match,
        CardFact card,
        IReadOnlyList<CardFact> allCards,
        IReadOnlyList<ManaColor> produces,
        bool defaultUntapped,
        CreatureComposition creatureComposition,
        string text) =>
        new()
        {
            Produces = produces,
            EntersUntapped = defaultUntapped,
            CountCondition = CountConditionKind.SlowLand,
            CountThreshold = 2,
        };

    private static LandSourceClassification BuildEldThresholdClassification(
        Match match,
        CardFact card,
        IReadOnlyList<CardFact> allCards,
        IReadOnlyList<ManaColor> produces,
        bool defaultUntapped,
        CreatureComposition creatureComposition,
        string text) =>
        new()
        {
            Produces = produces,
            EntersUntapped = defaultUntapped,
            CountCondition = CountConditionKind.EldThreshold,
            CountThreshold = 3,
            CountTypeFilter = new[] { match.Groups[1].Value },
        };

    private static LandSourceClassification? BuildVergeClassification(
        Match match,
        CardFact card,
        IReadOnlyList<CardFact> allCards,
        IReadOnlyList<ManaColor> produces,
        bool defaultUntapped,
        CreatureComposition creatureComposition,
        string text)
    {
        if (produces.Count < 2)
        {
            return null;
        }

        IReadOnlyList<string> namedTypes = new[]
        {
            match.Groups[1].Value,
            match.Groups[2].Value,
        };
        bool secondColorOnline = namedTypes.Count > 0
            && CountLandsBearingAnyType(allCards, namedTypes, card) >= CheckLandMatchTypeThreshold;
        var vergeColors = new List<ManaColor> { produces[0] };
        if (secondColorOnline && !vergeColors.Contains(produces[1]))
        {
            vergeColors.Add(produces[1]);
        }

        return new LandSourceClassification
        {
            Produces = vergeColors,
            EntersUntapped = true,
        };
    }

    private static LandSourceClassification BuildTrainingCompoundClassification(
        Match match,
        CardFact card,
        IReadOnlyList<CardFact> allCards,
        IReadOnlyList<ManaColor> produces,
        bool defaultUntapped,
        CreatureComposition creatureComposition,
        string text)
    {
        bool colorsOnline = CountBasicLands(allCards) >= CheckLandMatchTypeThreshold;
        var trainingColors = new List<ManaColor> { ManaColor.Colorless };
        if (colorsOnline)
        {
            foreach (ManaColor color in produces)
            {
                if (color != ManaColor.Colorless && !trainingColors.Contains(color))
                {
                    trainingColors.Add(color);
                }
            }
        }

        return new LandSourceClassification
        {
            Produces = trainingColors,
            EntersUntapped = true,
        };
    }

    private static LandSourceClassification BuildVividChargeClassification(
        Match match,
        CardFact card,
        IReadOnlyList<CardFact> allCards,
        IReadOnlyList<ManaColor> produces,
        bool defaultUntapped,
        CreatureComposition creatureComposition,
        string text)
    {
        IReadOnlyList<ManaColor> deckColors = DeckColors(allCards);
        ManaColor? baseColor = ExtractNthTapAddColor(text, 1);
        IReadOnlyList<ManaColor> vividBase = baseColor is ManaColor color
            ? new[] { color }
            : produces.Where(c => c != ManaColor.Colorless).Take(1).ToArray();
        SourceCapabilities sourceCapabilities = GetSourceCapabilities(card);

        return new LandSourceClassification
        {
            Produces = vividBase,
            EntersUntapped = false,
            ConditionalAnyColorSource = new ManaSource
            {
                Name = card.Name + " (vivid)",
                Produces = deckColors,
                Weight = 0.25,
                IsLand = false,
                ManaAmount = 1,
                IsConditional = true,
                IsSnow = sourceCapabilities.IsSnow,
                ProducesColorless = sourceCapabilities.ProducesColorless,
            },
        };
    }

    private static LandSourceClassification BuildSpendOnlyCreatureClassification(
        Match match,
        CardFact card,
        IReadOnlyList<CardFact> allCards,
        IReadOnlyList<ManaColor> produces,
        bool defaultUntapped,
        CreatureComposition creatureComposition,
        string text)
    {
        bool chosenTypeOnly = match.Groups["chosenType"].Success;
        double restrictedWeight = chosenTypeOnly
            ? ClampRestrictedLandWeight(creatureComposition.DominantTypeShare)
            : creatureComposition.CreatureShare;
        return new LandSourceClassification
        {
            Produces = produces,
            EntersUntapped = defaultUntapped,
            Weight = restrictedWeight,
            IsRestrictedSourceApproximation = true,
        };
    }

    private static LandSourceClassification BuildNykthosDevotionClassification(
        Match match,
        CardFact card,
        IReadOnlyList<CardFact> allCards,
        IReadOnlyList<ManaColor> produces,
        bool defaultUntapped,
        CreatureComposition creatureComposition,
        string text)
    {
        IReadOnlyList<ManaColor> deckColors = DeckColors(allCards);
        IReadOnlyList<ManaColor> nykthosBase = produces.Contains(ManaColor.Colorless)
            ? new[] { ManaColor.Colorless }
            : produces;
        SourceCapabilities sourceCapabilities = GetSourceCapabilities(card);
        return new LandSourceClassification
        {
            Produces = nykthosBase,
            EntersUntapped = defaultUntapped,
            IsRestrictedSourceApproximation = true,
            ConditionalAnyColorSource = new ManaSource
            {
                Name = card.Name + " (devotion)",
                Produces = deckColors,
                Weight = RestrictedLandMinWeight,
                IsLand = false,
                ManaAmount = 1,
                IsConditional = true,
                IsSnow = sourceCapabilities.IsSnow,
                ProducesColorless = sourceCapabilities.ProducesColorless,
            },
        };
    }

    private static void AddSpellRequirement(List<SpellRequirement> spells, CardFact card, ParsedManaCost cost, bool costOverridden = false)
    {
        // X/Y/Z spells: printed mana value is not the real cast turn, so an on-curve source
        // check at that turn is meaningless. Skip them rather than strain colors at a bogus turn.
        if (cost.HasVariableCost)
        {
            return;
        }

        // Colorless fixed-cost payoffs (Ugin, Wurmcoil) now become SpellRequirements too (empty
        // Pips); they show in the castability rows with a mana-only cast chance. Only mana
        // rocks/dorks are flagged IsManaSource so they are hidden from the rows but kept in pools.
        spells.Add(new SpellRequirement
        {
            Name = card.Name,
            // True printed mana value (0-cost cards stay 0 for display). The min-1 cast-turn
            // floor is enforced downstream by EffectiveTurn and the simulator, not here. When the
            // cost is an applied intrinsic reduction, use the reduced value so the cast turn drops.
            ManaValue = costOverridden ? cost.ManaValue : Math.Max(0, (int)Math.Round(card.ManaValue)),
            Pips = cost.Pips,
            TrueColorlessPips = cost.TrueColorlessPips,
            SnowPips = cost.SnowPips,
            IsGold = cost.DistinctColors >= 2,
            IsManaSource = IsRockOrDork(card),
            Kinds = ClassifyKinds(card.TypeLine),
            IsCommander = card.IsCommander,
            IsCostOverridden = costOverridden,
        });
    }

    // The intrinsic, always-on effective cost for a "costs {X} less, where X is the greatest power
    // among creatures you control" reducer (The Skullspore Nexus). Resolves X against the deck's
    // greatest FIXED creature power (the optimistic on-board value); falls back to the colored-pip
    // floor when there is no fixed-power creature to measure. Returns null when the card has no such
    // reducer. Shared by the spell-cost auto-apply path and the DetectSelfCost suggestion.
    private static string? GreatestPowerEffectiveCost(CardFact card, int maxCreaturePower)
    {
        string text = (card.OracleText ?? string.Empty).ToLowerInvariant();
        if (text.Length == 0 || !GreatestPowerReducerRegex.IsMatch(text))
        {
            return null;
        }

        // Guard: ManaCostParser keeps hybrid / Phyrexian / twobrid symbols OUT of Pips (they have no
        // hard color), so the generic = ManaValue − pip-count math below would mistake them for
        // reducible generic mana and could collapse a cost like {2}{G/U}{G/U} below its real floor.
        // No printed greatest-power card has such a cost today; if one appears, score it at full cost
        // (no auto-discount) rather than over-reduce. Revisit when ParsedManaCost tracks flexible pips.
        if (card.ManaCost?.Contains('/', StringComparison.Ordinal) ?? false)
        {
            return null;
        }

        ParsedManaCost parsed = ManaCostParser.Parse(card.ManaCost);
        if (maxCreaturePower > 0)
        {
            return ReduceGenericCost(parsed, maxCreaturePower);
        }

        string colored = RenderColoredPips(parsed.Pips);
        return colored.Length == 0 ? "0" : colored;
    }

    // The exact rock/dork test AddPartialSources uses, factored out so the row-exclusion set ==
    // the partial-source set. MDFC land-backs are NOT rocks/dorks; they are real spells with a
    // land face. Requires a REPEATABLE front-face mana ability (efficacy R2 finding H2): bare
    // produced_mana is too broad — Scryfall sets it on Treasure-makers (Dockside Extortionist,
    // whose reminder text contains "Add one mana of any color"), one-shot sacrifice mana (Lotus
    // Petal, Lion's Eye Diamond) and sac-outlets (Phyrexian/Ashnod's Altar), all of which
    // previously counted as PERMANENT weighted color sources and were hidden from the
    // castability rows as IsManaSource.
    private static bool IsRockOrDork(CardFact card)
    {
        if (card.HasLandFace || card.ProducedMana.Count == 0 || !HasRepeatableManaAbility(card))
        {
            return false;
        }

        return IsType(card.TypeLine, "Creature") || IsType(card.TypeLine, "Artifact");
    }

    private static bool QualifiesForScrySourceCredit(CardFact card)
    {
        if (card.ManaValue > 2 || IsLandType(card.TypeLine))
        {
            return false;
        }

        string rawText = card.OracleText ?? string.Empty;
        if (!rawText.Contains("scry", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string text = ReminderTextRegex.Replace(rawText, string.Empty);
        return ScryRegex.IsMatch(text);
    }

    // Strips parenthesized reminder text ("(Treasure tokens are artifacts with ... Add one mana
    // of any color.)") so token reminder wording never reads as the card's own mana ability.
    private static readonly Regex ReminderTextRegex = new(@"\([^)]*\)", RegexOptions.Compiled);

    // Shockland ETB template ("you may pay N life. If you don't, it enters tapped"). Compiled +
    // hoisted to match this file's regex convention; see TextPayLifeUntapped for why it is anchored
    // to "you may pay" rather than a bare "pay N life".
    private static readonly Regex PayLifeRegex =
        new(@"you may pay \d+ life", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Bond / "crowd" lands (Battlebond + Commander Legends — Sea of Clouds, Training Center, …):
    // "enters tapped unless you have two or more opponents". DeckFlow only models Commander, which is
    // always multiplayer (2+ opponents), so the condition always holds → they always enter untapped.
    private static readonly Regex BondLandRegex = new(
        @"tapped unless you (?:have|control) (?:two or more|2 or more) opponents",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Check lands (M10/Innistrad/Ixalan — Glacial Fortress …): "enters tapped unless you control a
    // Plains or an Island". Anchored to the "control (a|an) <type>" template so it captures ONLY the
    // check-land family. This deliberately excludes:
    //   * fast / slow / ELD threshold lands, which use dedicated count-condition regexes below; and
    //   * Verge / Training Compound / Vivid, which gate colors rather than tapped state.
    // The captured group is the named-type clause ("Plains or an Island"); types are pulled from it.
    private static readonly Regex CheckLandRegex = new(
        @"tapped unless you control (?:a|an) ([^.]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Snarls (Strixhaven — Frostboil Snarl …): "you may reveal an Island or Mountain card from your
    // hand. If you don't, this land enters tapped." Hand-reveal trigger; the named types come from
    // the reveal clause and feed the same matching-type census as check lands.
    private static readonly Regex SnarlRevealRegex = new(
        @"reveal ([^.]+?) card from your hand",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // [ASSUMED] Verified by ManabaseLiveOracleCanaryTests.cs against Botanical Sanctum's live clause.
    private static readonly Regex FastLandRegex = new(
        @"enters(?: the battlefield)? tapped unless you control two or fewer other lands",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // [ASSUMED] Verified by ManabaseLiveOracleCanaryTests.cs against Deathcap Glade's live clause.
    private static readonly Regex SlowLandRegex = new(
        @"enters(?: the battlefield)? tapped unless you control two or more other lands",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // [ASSUMED] Verified by ManabaseLiveOracleCanaryTests.cs against Mystic Sanctuary's live clause.
    private static readonly Regex EldThresholdRegex = new(
        @"enters(?: the battlefield)? tapped unless you control three or more other ([A-Za-z]+)s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // [ASSUMED] Verified by ManabaseLiveOracleCanaryTests.cs against Floodfarm Verge's live clause.
    private static readonly Regex VergeSecondColorRegex = new(
        @"Activate only if you control (?:a|an) (Plains|Island|Swamp|Mountain|Forest) or (?:a|an) (Plains|Island|Swamp|Mountain|Forest)\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // [ASSUMED] Verified by ManabaseLiveOracleCanaryTests.cs against Training Compound's live clause.
    private static readonly Regex TrainingCompoundRegex = new(
        @"Activate only if this land entered this turn or if you control a basic land",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // [ASSUMED] Verified by ManabaseLiveOracleCanaryTests.cs against Vivid Meadow's live clause.
    private static readonly Regex VividChargeRegex = new(
        @"with two charge counters on it",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // [ASSUMED] Verified by ManabaseLiveOracleCanaryTests.cs against Cavern of Souls, Unclaimed
    // Territory, and Ancient Ziggurat live clauses.
    private static readonly Regex SpendOnlyCreatureRegex = new(
        @"Spend this mana only to cast a creature spell(?<chosenType> of the chosen type)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // [ASSUMED] Verified by ManabaseLiveOracleCanaryTests.cs against Nykthos, Shrine to Nyx's live
    // devotion clause.
    private static readonly Regex NykthosDevotionRegex = new(
        @"devotion to that color",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Oracle templates whose first capture group is the named-basic-type clause a conditional land
    // keys off. Tried in order, first match wins. This remains the tapped-state census path for
    // check lands and Snarls only; the newer MBGAP-02 families use dedicated helpers below because
    // they gate per-trial count metadata or conditional colors instead.
    private static readonly Regex[] ConditionalTypeTemplates = { CheckLandRegex, SnarlRevealRegex };

    private static readonly SpecialLandRule[] SpecialLandRules =
    {
        new()
        {
            RequiresCheckLandUntapped = true,
            Pattern = FastLandRegex,
            Build = BuildFastLandClassification,
        },
        new()
        {
            RequiresCheckLandUntapped = true,
            Pattern = SlowLandRegex,
            Build = BuildSlowLandClassification,
        },
        new()
        {
            RequiresCheckLandUntapped = true,
            Pattern = EldThresholdRegex,
            Build = BuildEldThresholdClassification,
        },
        new()
        {
            RequiresCheckLandUntapped = true,
            Pattern = VergeSecondColorRegex,
            Build = BuildVergeClassification,
        },
        new()
        {
            RequiresCheckLandUntapped = true,
            Pattern = TrainingCompoundRegex,
            Build = BuildTrainingCompoundClassification,
        },
        new()
        {
            RequiresCheckLandUntapped = true,
            Pattern = VividChargeRegex,
            Build = BuildVividChargeClassification,
        },
        new()
        {
            RequiresRestrictedLands = true,
            Pattern = SpendOnlyCreatureRegex,
            Build = BuildSpendOnlyCreatureClassification,
        },
        new()
        {
            RequiresRestrictedLands = true,
            Pattern = NykthosDevotionRegex,
            Build = BuildNykthosDevotionClassification,
        },
    };

    // A conditional-untapped land (check/Snarl) is modeled untapped when the deck runs at least this
    // many lands bearing one of its named basic types — enough that you almost always control/hold a
    // trigger by the turn it is played. Heuristic constant; tune during calibration.
    private const int CheckLandMatchTypeThreshold = 6;

    // D-03 restricted lands: the minimum colored-source weight for a restrictive land in a deck with
    // little or no matching creature composition. Named so the Cavern/Unclaimed floor is explicit.
    private const double RestrictedLandMinWeight = 0.25;

    // A quoted span in oracle text ('... have "{T}: Add one mana of any color."'). Quoted
    // abilities are GRANTS: they live on whatever permanent the surrounding clause names, so a
    // quote only counts as THIS card's own mana ability when the granting clause includes the
    // card itself — a self pronoun ("it has", "this creature has" — Honored Hierarch, Mul Daya
    // Channelers) or a collective naming one of the card's own types ("All Slivers have" on
    // Gemhide Sliver, "Human creatures you control have" on Katilda, "Creatures you control
    // have" on Citanul Hierophants). Other-grants (Paradise Mantle's "Equipped creature has",
    // Chromatic Lantern's "Lands you control have", Goldspan's Treasure grant) are ignored here —
    // they are modeled separately by DetectGranter/AddGrantedSources.
    private static readonly Regex QuotedSpanRegex = new("\"([^\"]*)\"", RegexOptions.Compiled);

    // Self pronoun immediately before a quoted grant: "..., it has "..."" / "this creature gains".
    // Subjects are singular, so only the singular verbs has/gains can follow.
    private static readonly Regex SelfPronounGrantRegex = new(
        @"\b(?:it|this creature|this artifact|this enchantment|this land|this permanent)\s+(?:has|gains?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Constant separators for the grant-clause scan, hoisted so each GrantIncludesSelf call
    // does not allocate a fresh char[].
    private static readonly char[] ClauseBoundaryChars = { '.', ',', ';' };
    private static readonly char[] WordSeparatorChars = { ' ', '\t' };

    // Words in a collective-grant subject that never name a type ("All Slivers have", "Sliver
    // creatures you control have", "Equipped creature has").
    private static readonly HashSet<string> GrantSubjectStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "other", "each", "you", "control", "and", "have", "has", "gains", "gain",
        "equipped", "enchanted", "target", "token", "tokens", "nontoken", "tapped", "untapped",
    };

    // One-shot burst mana: an instant/sorcery ritual whose "Add {…}" clause produces more mana than
    // its own cost (Dark Ritual {B} → Add {B}{B}{B}, net +2 B). Front-face Instant/Sorcery ONLY, so
    // artifact fast mana (Lotus Petal, LED — kept in the FastMana land-target lane) can never qualify
    // here, and no card lands in both lanes. {X} in the produced clause or the own cost is skipped
    // (model only a fixed floor). See .planning/captures/manabase-ritual-burst-mana-spec.md.
    private static readonly Regex AddClauseRegex =
        new(@"\bAdd\s+((?:\{[^}]+\}\s*)+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SacrificeClauseRegex =
        new(@"\bsacrifice\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static OneShotMana? DetectOneShotBurstMana(CardFact card)
    {
        string front = CardTypeLine.FrontFace(card.TypeLine);
        if (!IsType(front, "Instant") && !IsType(front, "Sorcery"))
        {
            return null;
        }

        string text = card.FrontOracleText;
        if (text.Length == 0)
        {
            return null;
        }

        string normalizedText = ReminderTextRegex.Replace(text, string.Empty);

        // Instant/sorcery rituals with any Sacrifice clause carry an additional cost or downside
        // the sim cannot model, so exclude them conservatively per SPEC §3.1.
        if (SacrificeClauseRegex.IsMatch(normalizedText))
        {
            return null;
        }

        Match add = AddClauseRegex.Match(normalizedText);
        if (!add.Success)
        {
            return null;
        }

        ParsedManaCost produced = ManaCostParser.Parse(add.Groups[1].Value);
        ParsedManaCost own = ManaCostParser.Parse(card.ManaCost);
        if (produced.HasVariableCost || own.HasVariableCost || produced.ManaValue <= 0)
        {
            return null;
        }

        // Net-positive only — a "ritual" that adds no more than it costs is not acceleration.
        if (produced.ManaValue - own.ManaValue <= 0)
        {
            return null;
        }

        List<ManaColor> colors = produced.Pips.Where(p => p.Value > 0).Select(p => p.Key).ToList();
        if (colors.Count == 0)
        {
            return null;
        }

        return new OneShotMana
        {
            Name = card.Name,
            ProducedColors = colors,
            ProducedAmount = produced.ManaValue,
            OwnPips = own.Pips,
            OwnManaValue = own.ManaValue,
        };
    }

    // A repeatable, self-contained mana ability on the card's FRONT face: an activated
    // "<cost>: Add ..." line whose cost does not sacrifice anything. The sacrifice check drops
    // one-shot mana (Lotus Petal "{T}, Sacrifice this artifact: Add ...") and sac-outlet engines
    // (Ashnod's Altar "Sacrifice a creature: Add {C}{C}") — neither is the persistent source the
    // 0.5/0.75 Karsten partial weights model. Triggered Treasure creation carries no "<cost>: Add"
    // line at all once reminder text is stripped, so Dockside/Goldspan-class cards fall out too
    // (Goldspan's granted Treasure ability is also excluded by its "Sacrifice" cost).
    private static bool HasRepeatableManaAbility(CardFact card)
    {
        string text = card.FrontOracleText;
        if (text.Length == 0)
        {
            return false;
        }

        text = ReminderTextRegex.Replace(text, string.Empty);
        foreach (string rawLine in text.Split('\n'))
        {
            // Quoted grants that include the card itself count as its own (conditional) mana
            // ability — evaluate the QUOTED ability's cost/effect. Other-grants are skipped.
            foreach (Match quote in QuotedSpanRegex.Matches(rawLine))
            {
                if (GrantIncludesSelf(card, rawLine[..quote.Index])
                    && LineHasActivatedAdd(quote.Groups[1].Value))
                {
                    return true;
                }
            }

            // The card's own unquoted ability lines, with all quoted grants removed.
            if (LineHasActivatedAdd(QuotedSpanRegex.Replace(rawLine, string.Empty)))
            {
                return true;
            }
        }

        return false;
    }

    // An activated "<cost>: Add ..." whose cost does not sacrifice anything (one-shot mana and
    // sac-outlets are not the persistent source the Karsten partial weights model).
    private static bool LineHasActivatedAdd(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return false;
        }

        string cost = line[..colon];
        if (cost.Contains("Sacrifice", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string effect = line[(colon + 1)..].TrimStart();
        return effect.StartsWith("Add ", StringComparison.Ordinal);
    }

    // True when the granting clause before a quote includes the card itself: a self pronoun
    // ("it has", "this creature has"), or a collective whose subject names a type on the card's
    // own type line ("All Slivers have" on a Sliver, "Human creatures you control have" on a
    // Human, "Creatures you control have" on any creature). "Equipped creature has" on an
    // Equipment and "Lands/Treasures you control have" on a non-land never match.
    private static bool GrantIncludesSelf(CardFact card, string prefix)
    {
        prefix = prefix.TrimEnd();
        if (SelfPronounGrantRegex.IsMatch(prefix))
        {
            return true;
        }

        // Collective grant: examine the clause after the last sentence/clause boundary, e.g.
        // "All Slivers have" or "Sliver creatures you control have".
        int clauseStart = prefix.LastIndexOfAny(ClauseBoundaryChars) + 1;
        string[] words = prefix[clauseStart..].Split(
            WordSeparatorChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string word in words)
        {
            if (GrantSubjectStopWords.Contains(word))
            {
                continue;
            }

            // Match the word (and its naive singular) against the card's own type line:
            // "Slivers" -> "Sliver", "creatures" -> "creature", "Frogs" -> "Frog".
            string singular = word.Length > 1 && word.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                ? word[..^1]
                : word;
            if (IsType(card.TypeLine, singular))
            {
                return true;
            }
        }

        return false;
    }

    private static SpellKinds ClassifyKinds(string typeLine)
    {
        string front = typeLine.Split("//")[0];
        SpellKinds kinds = SpellKinds.None;
        if (IsType(front, "Creature"))
        {
            kinds |= SpellKinds.Creature;
        }

        if (IsType(front, "Artifact"))
        {
            kinds |= SpellKinds.Artifact;
        }

        if (IsType(front, "Instant"))
        {
            kinds |= SpellKinds.Instant;
        }

        if (IsType(front, "Sorcery"))
        {
            kinds |= SpellKinds.Sorcery;
        }

        if (kinds == SpellKinds.None)
        {
            kinds = SpellKinds.Other;
        }

        return kinds;
    }

    private static void AddPartialSources(List<ManaSource> sources, CardFact card, SourceCapabilities sourceCapabilities)
    {
        // Land/spell MDFC back face: a real land at full color weight 1.0. Its tapped-or-pay-life
        // timing (read from the isolated land face) is the only penalty, carried by the sim — a color
        // discount on top would double-count the downside. It counts as a full land in actualLands.
        if (card.HasLandFace)
        {
            IReadOnlyList<ManaColor> produces = MapColors(card.ProducedMana);
            if (produces.Count == 0)
            {
                return;
            }

            bool untapped = !LandFaceEntersTapped(card) || LandFacePayLifeUntapped(card);
            for (int i = 0; i < card.Quantity; i++)
            {
                sources.Add(new ManaSource
                {
                    Name = card.Name,
                    Produces = produces,
                    Weight = 1.0,
                    IsLand = true,
                    EntersUntapped = untapped,
                    ManaAmount = 1,
                    IsCommander = card.IsCommander,
                    IsSnow = sourceCapabilities.IsSnow,
                    ProducesColorless = sourceCapabilities.ProducesColorless,
                });
            }

            return;
        }

        if (!IsRockOrDork(card))
        {
            return;
        }

        // Mana dork (creature) ≈ 0.5; mana rock (artifact) ≈ 0.75.
        if (IsType(card.TypeLine, "Creature"))
        {
            AddWeighted(sources, card, 0.5, sourceCapabilities);
        }
        else if (IsType(card.TypeLine, "Artifact"))
        {
            AddWeighted(sources, card, 0.75, sourceCapabilities);
        }
    }

    private static void AddWeighted(List<ManaSource> sources, CardFact card, double weight, SourceCapabilities sourceCapabilities)
    {
        IReadOnlyList<ManaColor> produces = MapColors(card.ProducedMana);
        if (produces.Count == 0)
        {
            return;
        }

        for (int i = 0; i < card.Quantity; i++)
        {
            sources.Add(new ManaSource
            {
                Name = card.Name,
                Produces = produces,
                Weight = weight,
                IsLand = false,
                IsCommander = card.IsCommander,
                ManaAmount = card.ManaAmount,
                IsSnow = sourceCapabilities.IsSnow,
                ProducesColorless = sourceCapabilities.ProducesColorless,
            });
        }
    }

    private static IReadOnlyList<ManaColor> MapColors(IReadOnlyList<string> produced)
    {
        var colors = new List<ManaColor>();
        foreach (string letter in produced)
        {
            ManaColor? c = ManaCostParser.MapSymbol(letter.ToUpperInvariant());
            if (c is not null && !colors.Contains(c.Value))
            {
                colors.Add(c.Value);
            }
        }

        return colors;
    }

    private static bool IsSnowPermanent(CardFact card) =>
        IsType(CardTypeLine.FrontFace(card.TypeLine), "Snow");

    private static bool ProducesTrueColorless(CardFact card)
    {
        if (card.ProducedMana.Contains("C", StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        string text = ReminderTextRegex.Replace(card.OracleText ?? string.Empty, string.Empty);
        return text.Contains("{C}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLandType(string typeLine)
    {
        // Use the front face only (before "//") so MDFC spell-fronts aren't treated as lands.
        return IsType(CardTypeLine.FrontFace(typeLine), "Land");
    }

    private static SourceCapabilities GetSourceCapabilities(CardFact card) =>
        new(IsSnowPermanent(card), ProducesTrueColorless(card));

    private readonly record struct SourceCapabilities(bool IsSnow, bool ProducesColorless);

    private static bool IsType(string typeLine, string type) =>
        typeLine.Contains(type, StringComparison.OrdinalIgnoreCase);

    private static bool ProducesMana(CardFact card) =>
        (card.OracleText?.Contains("Add ", StringComparison.OrdinalIgnoreCase) ?? false)
        || card.ProducedMana.Count > 0;

    private static bool IsBasicFetch(CardFact card)
    {
        string? text = card.OracleText;
        return text is not null
            && text.Contains("Search your library for a", StringComparison.OrdinalIgnoreCase)
            && text.Contains("basic land", StringComparison.OrdinalIgnoreCase);
    }

    // Basic land type -> the color it taps for, used to color fetchlands whose produced_mana is empty.
    internal static readonly (string Type, ManaColor Color)[] BasicLandColors =
    {
        ("Plains", ManaColor.White),
        ("Island", ManaColor.Blue),
        ("Swamp", ManaColor.Black),
        ("Mountain", ManaColor.Red),
        ("Forest", ManaColor.Green),
    };

    // Pre-pass: map each basic land type to the colors that every NON-fetch land in the deck bearing
    // that type can produce. A typed fetch ("Plains or Island card") can grab not just basics but any
    // land with a matching type — a Plains-typed shock (Hallowed Fountain → W,U) or a triome
    // (Raffine's Tower → W,U,B) — so the fetch's real colors are the union over its fetched types.
    private static (Dictionary<string, HashSet<ManaColor>> TypeColors, HashSet<ManaColor> BasicColors)
        BuildFetchableColors(IReadOnlyList<CardFact> cards)
    {
        var typeColors = new Dictionary<string, HashSet<ManaColor>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string type, ManaColor _) in BasicLandColors)
        {
            typeColors[type] = new HashSet<ManaColor>();
        }

        var basicColors = new HashSet<ManaColor>();
        foreach (CardFact card in cards)
        {
            if (!IsLandType(card.TypeLine))
            {
                continue;
            }

            IReadOnlyList<ManaColor> colors = MapColors(card.ProducedMana);
            if (colors.Count == 0)
            {
                continue; // a fetch (empty produced_mana) or colorless utility land seeds no color
            }

            string front = card.TypeLine.Split("//")[0];
            bool isBasic = front.Contains("Basic", StringComparison.OrdinalIgnoreCase);
            foreach ((string type, ManaColor color) in BasicLandColors)
            {
                if (!front.Contains(type, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (ManaColor c in colors)
                {
                    typeColors[type].Add(c);
                }

                if (isBasic)
                {
                    basicColors.Add(color);
                }
            }
        }

        return (typeColors, basicColors);
    }

    // Colors a fetchland can provide. Typed fetches (Flooded Strand: "Plains or Island card") return
    // the union of every deck land sharing a named type (basics, duals, triomes) plus the named
    // basics' own colors; a generic "basic land" fetch (Prismatic Vista, Evolving Wilds) grabs any
    // basic, so it counts for the basic colors actually in the deck (or all five if none parsed).
    private static IReadOnlyList<ManaColor> FetchLandColors(CardFact card,
        Dictionary<string, HashSet<ManaColor>> typeColors, HashSet<ManaColor> basicColors)
    {
        string? text = card.OracleText;
        if (text is null || !text.Contains("Search your library", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<ManaColor>();
        }

        var colors = new List<ManaColor>();
        bool namedAny = false;
        foreach ((string type, ManaColor color) in BasicLandColors)
        {
            if (!text.Contains(type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            namedAny = true;
            // Only colors actually reachable: the union of deck lands bearing this type (basics,
            // duals, triomes). If the deck runs no land of this subtype, the fetch genuinely can't
            // get that color — do NOT credit the named basic's color speculatively (Codex MEDIUM).
            foreach (ManaColor c in typeColors[type])
            {
                if (!colors.Contains(c))
                {
                    colors.Add(c);
                }
            }
        }

        if (!namedAny && text.Contains("basic land", StringComparison.OrdinalIgnoreCase))
        {
            // Generic basic fetch (Prismatic Vista, Evolving Wilds): only the basic colors the deck
            // actually runs. A deck with no basics can't fetch anything — empty, not all five.
            return basicColors.ToList();
        }

        return colors;
    }

    // Scryfall's Aug-2024 oracle update reworded "enters the battlefield tapped" to "enters
    // tapped" ("This land enters tapped."). Live API data uses the new phrasing exclusively, so
    // matching only the old one classified every tapland as untapped (efficacy R2 finding H1).
    // Both literals are required: "enters tapped" is NOT a substring of the old form ("the
    // battlefield" sits between the words), so the second check is load-bearing for any stale
    // fixture/cache still holding the pre-2024 wording — do not delete it.
    private static bool TextEntersTapped(string? text) =>
        text is not null
        && (text.Contains("enters tapped", StringComparison.OrdinalIgnoreCase)
            || text.Contains("enters the battlefield tapped", StringComparison.OrdinalIgnoreCase));

    private static bool EntersTapped(CardFact card) => TextEntersTapped(card.OracleText);

    // MDFC land backs read the land-face oracle text; single-faced lands fall back to OracleText.
    private static bool LandFaceEntersTapped(CardFact card) =>
        TextEntersTapped(card.LandFaceOracleText ?? card.OracleText);

    /// <summary>
    /// Detects shock-style "pay N life or enters tapped" lands, which should be treated as entering
    /// untapped in practice when the pay-life path is enabled.
    /// </summary>
    private static bool TextPayLifeUntapped(string? text)
    {
        if (text is null)
        {
            return false;
        }

        text = ReminderTextRegex.Replace(text, string.Empty);
        // Anchor to the shockland replacement template ("you may pay N life. If you don't, it enters
        // tapped"). A bare "pay N life" would misfire on always-tapped lands with a life-payment
        // ACTIVATED ability (Boseiju Who Shelters All, Hall of the Bandit Lord, Untaidake) — those use
        // "{T}, Pay N life:" as a cost, not the "you may pay" ETB choice.
        return PayLifeRegex.IsMatch(text) && TextEntersTapped(text);
    }

    private static bool HasPayLifeUntappedClause(CardFact card) => TextPayLifeUntapped(card.OracleText);

    private static bool LandFacePayLifeUntapped(CardFact card) =>
        TextPayLifeUntapped(card.LandFaceOracleText ?? card.OracleText);

    // A bond/crowd land ("tapped unless you have two or more opponents") always enters untapped in
    // this tool's Commander (multiplayer) model. Reminder text stripped first for consistency.
    private static bool IsBondLand(CardFact card)
    {
        string? text = card.OracleText;
        return text is not null && BondLandRegex.IsMatch(ReminderTextRegex.Replace(text, string.Empty));
    }

    // The named basic land types a check land or Snarl keys off ("a Plains or an Island" / "reveal an
    // Island or Mountain card"). Empty when the card is neither — including slow/fast lands, whose
    // "other lands" clause names no basic type. Scans only the matched trigger clause so a basic-type
    // word elsewhere in the oracle can't false-trigger.
    private static IReadOnlyList<string> ConditionalUntappedTypes(CardFact card)
    {
        string? text = card.OracleText;
        if (text is null)
        {
            return Array.Empty<string>();
        }

        text = ReminderTextRegex.Replace(text, string.Empty);
        Match clause = Match.Empty;
        foreach (Regex template in ConditionalTypeTemplates)
        {
            clause = template.Match(text);
            if (clause.Success)
            {
                break;
            }
        }

        if (!clause.Success)
        {
            return Array.Empty<string>();
        }

        string named = clause.Groups[1].Value;
        return ExtractNamedBasicTypes(named);
    }

    private static IReadOnlyList<string> ExtractNamedBasicTypes(string named)
    {
        var types = new List<string>();
        foreach ((string type, ManaColor _) in BasicLandColors)
        {
            if (named.Contains(type, StringComparison.OrdinalIgnoreCase))
            {
                types.Add(type);
            }
        }

        return types;
    }

    private static ManaColor? ExtractNthTapAddColor(string text, int occurrence)
    {
        const string marker = "{t}: add {";
        string lower = text.ToLowerInvariant();
        int start = 0;
        for (int i = 0; i < occurrence; i++)
        {
            start = lower.IndexOf(marker, start, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
        }

        if (start >= lower.Length)
        {
            return null;
        }

        return ManaCostParser.MapSymbol(char.ToUpperInvariant(lower[start]).ToString());
    }

    // Count of OTHER deck land COPIES bearing at least one of the given basic land types (basics,
    // duals, shocks, triomes). Union — a dual bearing two named types is counted once. The candidate
    // land is excluded so it never counts itself toward its own trigger (the real condition is
    // "control a [type]" on OTHER permanents). Cheap: only the few check/Snarl lands trigger this,
    // and it walks the (already in-memory) card list once each.
    private static int CountLandsBearingAnyType(IReadOnlyList<CardFact> cards, IReadOnlyList<string> types, CardFact candidate)
    {
        int count = 0;
        foreach (CardFact card in cards)
        {
            if (ReferenceEquals(card, candidate) || !IsLandType(card.TypeLine))
            {
                continue;
            }

            string front = CardTypeLine.FrontFace(card.TypeLine);
            foreach (string type in types)
            {
                if (front.Contains(type, StringComparison.OrdinalIgnoreCase))
                {
                    count += card.Quantity;
                    break;
                }
            }
        }

        return count;
    }

    private static int CountBasicLands(IReadOnlyList<CardFact> cards)
    {
        int count = 0;
        foreach (CardFact card in cards)
        {
            if (!IsLandType(card.TypeLine))
            {
                continue;
            }

            string front = CardTypeLine.FrontFace(card.TypeLine);
            if (front.Contains("Basic", StringComparison.OrdinalIgnoreCase))
            {
                count += card.Quantity;
            }
        }

        return count;
    }

    private sealed record CreatureComposition
    {
        public static CreatureComposition Empty { get; } = new()
        {
            DominantTypeShare = 0.0,
            CreatureShare = 0.0,
        };

        public required double DominantTypeShare { get; init; }

        public required double CreatureShare { get; init; }
    }

    private static CreatureComposition ComputeCreatureComposition(IReadOnlyList<CardFact> cards)
    {
        var histogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int totalCreatureCount = 0;
        int nonlandCount = 0;

        foreach (CardFact card in cards)
        {
            if (IsLandType(card.TypeLine))
            {
                continue;
            }

            nonlandCount += card.Quantity;
            if (!IsType(CardTypeLine.FrontFace(card.TypeLine), "Creature"))
            {
                continue;
            }

            totalCreatureCount += card.Quantity;
            string[] typeLineParts = CardTypeLine.FrontFace(card.TypeLine).Split('—', 2, StringSplitOptions.TrimEntries);
            if (typeLineParts.Length < 2)
            {
                continue;
            }

            string[] subtypes = typeLineParts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string subtype in subtypes)
            {
                histogram[subtype] = histogram.GetValueOrDefault(subtype) + card.Quantity;
            }
        }

        double dominantTypeShare = totalCreatureCount > 0 && histogram.Count > 0
            ? (double)histogram.Values.Max() / totalCreatureCount
            : 0.0;
        double creatureShare = nonlandCount > 0
            ? (double)totalCreatureCount / nonlandCount
            : 0.0;

        return new CreatureComposition
        {
            DominantTypeShare = dominantTypeShare,
            CreatureShare = creatureShare,
        };
    }

    private static double ClampRestrictedLandWeight(double weight) =>
        Math.Clamp(weight, RestrictedLandMinWeight, 1.0);

    // Whether a board/hand-conditional land (bond, check, Snarl) should be modeled untapped. Bond is
    // unconditional (Commander is multiplayer); check/Snarl require enough matching-type sources that
    // a trigger is reliably available by the turn the land is played.
    private static bool IsConditionallyUntapped(CardFact card, IReadOnlyList<CardFact> cards)
    {
        if (IsBondLand(card))
        {
            return true;
        }

        IReadOnlyList<string> types = ConditionalUntappedTypes(card);
        return types.Count > 0 && CountLandsBearingAnyType(cards, types, card) >= CheckLandMatchTypeThreshold;
    }

    // v1 (rampCreditV2 OFF) legacy baseline — DELIBERATELY FROZEN to the historic broad predicate
    // (ManaRampCreditTests guards "flag-off == historic"). The M7 you-anchored unification applies to
    // the ACTIVE v2 + budget paths only; this off-path is not prod and is left byte-identical.
    // Why: Manabase's ramp predicates (this + IsRampPieceForBudget / IsRepeatableRampOrDraw /
    // IsRockOrDork) are tuned + flag-gated for castability/land math and intentionally diverge from
    // the broad role-tally DeckStatClassifier.IsRampCard. Do NOT unify. See ADR
    // docs/decisions/0003-ramp-classifier-divergence.md.
    private static bool IsRampOrDraw(CardFact card)
    {
        string text = card.OracleText ?? string.Empty;
        bool ramp = (text.Contains("Search your library for", StringComparison.OrdinalIgnoreCase)
                && text.Contains("land", StringComparison.OrdinalIgnoreCase))
            || text.Contains("Add ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("create a Treasure", StringComparison.OrdinalIgnoreCase);
        bool draw = text.Contains("draw a card", StringComparison.OrdinalIgnoreCase)
            || text.Contains("draw two cards", StringComparison.OrdinalIgnoreCase);
        return ramp || draw;
    }

    private static bool IsRampPieceForBudget(CardFact card)
    {
        string text = card.OracleText ?? string.Empty;
        return (text.Contains("Search your library for", StringComparison.OrdinalIgnoreCase)
                && text.Contains("land", StringComparison.OrdinalIgnoreCase))
            || text.Contains("Add ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("create a Treasure", StringComparison.OrdinalIgnoreCase)
            || IsRockOrDork(card)
            || IsLandRampToBattlefield(card);
    }

    private static bool IsDrawPieceForBudget(CardFact card) =>
        IsYouCardDraw(card);

    // Shared you-anchored card-draw predicate (M7).
    private static bool IsYouCardDraw(CardFact card) =>
        DeckStatClassifier.MatchesYouCardDraw(card.OracleText ?? string.Empty);

    // MQ-03 (rampCreditV2): narrowed land-target credit. Only REPEATABLE ramp and true card draw earn
    // the Karsten −0.28 credit; one-shot rituals ("Add" on an instant/sorcery) and Treasure-makers do
    // NOT, because they give the regression credit for mana the source model / sim never represents as
    // persistent access. Positive keep-rules (not a broad "is a permanent" filter):
    //   * true card draw (Karsten's draw term; deck-thinning, no mana path needed), OR
    //   * land-ramp that puts a land ONTO THE BATTLEFIELD (Cultivate/Rampant Growth — persistent;
    //     land-search-to-hand does not count), OR
    //   * a mana-producing PERMANENT — ProducesMana on a non-Instant/Sorcery: rocks, dorks, AND
    //     enchantment ramp (Utopia Sprawl, Wild Growth). The type-gate is what drops one-shot rituals.
    private static bool IsRepeatableRampOrDraw(CardFact card)
    {
        // True card draw for YOU (M7 shared predicate — same one the budget count uses, so a
        // "draws two cards" card can't be credited by one subsystem and ignored by the other).
        if (IsYouCardDraw(card))
        {
            return true;
        }

        if (IsLandRampToBattlefield(card))
        {
            return true;
        }

        // Repeatable mana permanent (rock/dork/enchantment ramp). Check the FRONT face only: joined
        // oracle text would leak a one-shot mana adventure/back face into this front-face permanent
        // test (a creature with a "{...} Add" adventure is NOT repeatable ramp). A front-face "Add "
        // that is NOT one-shot is the signal — card-level produced_mana is intentionally NOT used
        // here (also leaky).
        // Deliberately tests the WHOLE type line (both faces), NOT CardTypeLine.IsNonPermanentFront:
        // any instant/sorcery face disqualifies a card from repeatable-ramp credit here, so an Adventure
        // creature with a spell back does not earn it. (The plan-role gate wants the opposite — front-face
        // only — which is why the two intentionally differ.)
        string typeLine = card.TypeLine ?? string.Empty;
        bool permanent = !IsType(typeLine, "Instant") && !IsType(typeLine, "Sorcery");
        return permanent && HasNonOneShotFrontAdd(card);
    }

    // A front-face "Add ..." mana ability that is not a one-shot sacrifice (efficacy R2 M4 + M4b).
    // Looser than HasRepeatableManaAbility (which requires a "<cost>: Add" line and drives partial
    // SOURCE weight): here any front-face "Add " counts — bare mana abilities, triggered enchantment
    // ramp ("...adds an additional one mana...") — EXCEPT when the only "Add " sits after a
    // sacrifice cost. M4 strips the parenthesized token reminder ("(...Sacrifice this token: Add
    // one mana...)") so a Treasure/Food maker never reads as its own source. M4b then drops a
    // permanent whose sole mana ability is a one-shot sac ("{T}, Sacrifice this artifact: Add three
    // mana" — Lotus Bloom, Lion's Eye Diamond, Chromatic Star class): those give no persistent mana,
    // so they must not earn the −0.28 repeatable-ramp land credit — matching the H2 sac-guard.
    private static bool HasNonOneShotFrontAdd(CardFact card)
    {
        string text = card.FrontOracleText;
        if (text.Length == 0)
        {
            return false;
        }

        text = ReminderTextRegex.Replace(text, string.Empty);
        foreach (string line in text.Split('\n'))
        {
            int add = line.IndexOf("Add ", StringComparison.OrdinalIgnoreCase);
            if (add < 0)
            {
                continue;
            }

            // An "Add" preceded on the same line by a "<cost>: " whose cost sacrifices is one-shot
            // (Lotus Bloom) or a sac-outlet (Ashnod's Altar) — skip it; keep scanning other lines.
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0 && colon < add
                && line[..colon].Contains("Sacrifice", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    // 70-03b: repeatable land-ramp that puts a land ONTO THE BATTLEFIELD (Cultivate / Rampant Growth /
    // Nature's Lore) — persistent mana access. Land-search-to-HAND does not count. Shared by the MQ-03
    // land-target credit (IsRepeatableRampOrDraw) and the land-ramp sim source, so the two never drift.
    private static bool IsLandRampToBattlefield(CardFact card)
    {
        string text = card.OracleText ?? string.Empty;
        return text.Contains("Search your library for", StringComparison.OrdinalIgnoreCase)
            && text.Contains("land", StringComparison.OrdinalIgnoreCase)
            && text.Contains("onto the battlefield", StringComparison.OrdinalIgnoreCase);
    }

    // ---- REDUCE-01: always-on static generic cost reducers --------------------------------

    /// <summary>
    /// Detect an always-on static generic cost reducer ("&lt;Type&gt;? spells you cast cost {N}
    /// less"). The "you cast" anchor is required. Excludes (v1): "for each", "costs {N} less for",
    /// affinity/improvise/convoke/delve, one-shot/ritual discounts, opponent-symmetric/opponent-only
    /// text. Returns <see langword="null"/> when not a recognized reducer.
    /// </summary>
    private static CostReducer? DetectCostReducer(CardFact card)
    {
        string text = card.OracleText ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        string lower = text.ToLowerInvariant();

        // The required always-on anchor: "spells you cast cost {N} less".
        var match = StaticReducerRegex.Match(lower);
        if (!match.Success)
        {
            return null;
        }

        // v1 exclusions: scaling / non-static / opponent-facing reducers.
        if (lower.Contains("for each", StringComparison.Ordinal)
            || lower.Contains("less for", StringComparison.Ordinal)
            || lower.Contains("affinity", StringComparison.Ordinal)
            || lower.Contains("improvise", StringComparison.Ordinal)
            || lower.Contains("convoke", StringComparison.Ordinal)
            || lower.Contains("delve", StringComparison.Ordinal)
            || lower.Contains("opponent", StringComparison.Ordinal)
            || lower.Contains("opponents", StringComparison.Ordinal))
        {
            return null;
        }

        if (!int.TryParse(match.Groups["amt"].Value, out int amount) || amount <= 0)
        {
            return null;
        }

        // The matched scope prefix sits just before "spells you cast"; classify it. An unrecognized
        // non-empty (tribal/supertype) scope returns null → no modeled reducer (M5).
        ReductionScope? scope = ClassifyReducerScope(match.Groups["scope"].Value);
        if (scope is null)
        {
            return null;
        }

        return new CostReducer
        {
            GenericReduction = amount,
            Scope = scope.Value,
            SourceManaValue = Math.Max(0, (int)Math.Round(card.ManaValue)),
        };
    }

    // Detect a card whose realistic cost is below its printed mana value: free/pitch spells,
    // board-scaling self-reducers (Blasphemous Act), and evoke/suspend. Returns a canonical braced
    // effective cost ("0", "{R}", "{1}{B}") plus a short reason, or null when nothing applies.
    // This is a SUGGESTION only — it pre-populates the override box; it never changes the analysis
    // by itself. Most-specific category first.
    private static (string EffectiveCost, string Reason)? DetectSelfCost(CardFact card, int maxCreaturePower)
    {
        // Known limitation: OracleText is joined across faces, so a multi-face card could inherit a
        // suggestion from a non-front face. Low harm — a suggestion only pre-fills the editable box
        // and applying it is opt-in; the user sees and can clear a wrong line.
        string text = (card.OracleText ?? string.Empty).ToLowerInvariant();
        if (text.Length == 0)
        {
            return null;
        }

        // 1) Free / pitch — SELF-ANCHORED free-cast wording. Two forms, both referring to THIS spell:
        //      a) "... rather than pay this spell's mana cost"      (Force of Negation, the pitch cycle)
        //      b) "cast this spell without paying its mana cost"    (Fierce Guardianship / Deflecting
        //         Swat / Flawless Maneuver — the commander-conditional free cycle)
        //    Both name "this spell ... its mana cost", so they are unambiguously self-anchored. We must
        //    NOT match the OTHER-spell forms ("cast that spell / cast spells ... without paying their/its
        //    mana cost", e.g. Omniscience, cascade), which free a DIFFERENT spell — those say "that
        //    spell" / "spells" / "their", never "this spell ... its".
        if (text.Contains("rather than pay this spell's mana cost", StringComparison.Ordinal)
            || text.Contains("cast this spell without paying its mana cost", StringComparison.Ordinal))
        {
            return ("0", "free / alternative cost");
        }

        // 2) "Costs {X} less, where X is the greatest power among creatures you control" (The
        //    Skullspore Nexus). X is board-dependent; resolve it against the deck's greatest fixed
        //    creature power as the optimistic on-board value and reduce the generic cost by it
        //    (floored at the colored pips). Falls back to the colored-pip floor when the deck has no
        //    fixed-power creature (all *goyf / no creatures) — the same practical floor as case 3.
        // Greatest-power reducer (The Skullspore Nexus). This one is AUTO-APPLIED to the default
        // analysis (see the spell-cost path); the suggestion just surfaces the value in the editable
        // box so the user can see it and override to a different on-board assumption.
        if (GreatestPowerEffectiveCost(card, maxCreaturePower) is string powerCost)
        {
            string reason = maxCreaturePower > 0
                ? $"costs {{X}} less, X = greatest creature power (~{maxCreaturePower} here) — auto-applied, editable"
                : "costs {X} less, X = greatest creature power — auto-applied (assumed fully online), editable";
            return (powerCost, reason);
        }

        // 3) Board-scaling self-reduction ("this spell costs {1} less to cast for each ..."):
        //    drop all generic, keep the colored pips — the practical floor when fully online.
        if (ScalingSelfReducerRegex.IsMatch(text))
        {
            string colored = RenderColoredPips(ManaCostParser.Parse(card.ManaCost).Pips);
            return (colored.Length == 0 ? "0" : colored,
                "scales down with the board — assuming the reduction is fully online");
        }

        // 4) Evoke — use its braced mana cost when it has one (Shriekmaw "evoke {1}{B}"); a
        //    non-mana evoke cost (Grief "exile a black card") is free of mana, so 0.
        if (text.Contains("evoke", StringComparison.Ordinal))
        {
            Match evoke = EvokeCostRegex.Match(text);
            return evoke.Success
                ? (NormalizeBracedCost(evoke.Groups[1].Value), "evoke cost")
                : ("0", "evoke (alternative cost)");
        }

        // 5) Suspend — the suspend cost is a mana cost (Crashing Footfalls "suspend 1—{g}").
        Match suspend = SuspendCostRegex.Match(text);
        if (suspend.Success)
        {
            return (NormalizeBracedCost(suspend.Groups[1].Value), "suspend cost");
        }

        return null;
    }

    // Reduce a parsed cost's GENERIC portion by `reduction`, keeping all colored (and {C}) pips, and
    // render the canonical braced result. Generic = ManaValue − colored-pip count (ManaCostParser
    // tracks generic only in the value, not in Pips). Floors at the colored pips; "0" when nothing
    // is left. e.g. {4}{G}{G} reduced by 5 → "{G}{G}"; reduced by 2 → "{2}{G}{G}".
    private static string ReduceGenericCost(ParsedManaCost cost, int reduction)
    {
        int coloredCount = cost.Pips.Values.Sum();
        int generic = Math.Max(0, cost.ManaValue - coloredCount);
        int newGeneric = Math.Max(0, generic - reduction);
        string colored = RenderColoredPips(cost.Pips);

        if (newGeneric > 0)
        {
            return colored.Length > 0 ? $"{{{newGeneric}}}{colored}" : $"{{{newGeneric}}}";
        }

        return colored.Length > 0 ? colored : "0";
    }

    // Render hard colored pips as a canonical braced cost in WUBRG(+C) order (e.g. "{R}", "{U}{U}").
    private static string RenderColoredPips(IReadOnlyDictionary<ManaColor, int> pips)
    {
        var sb = new System.Text.StringBuilder();
        foreach (ManaColor color in new[]
                 {
                     ManaColor.White, ManaColor.Blue, ManaColor.Black,
                     ManaColor.Red, ManaColor.Green, ManaColor.Colorless,
                 })
        {
            int count = pips.GetValueOrDefault(color);
            for (int i = 0; i < count; i++)
            {
                sb.Append('{').Append(ColorSymbol(color)).Append('}');
            }
        }

        return sb.ToString();
    }

    private static char ColorSymbol(ManaColor color) => color switch
    {
        ManaColor.White => 'W',
        ManaColor.Blue => 'U',
        ManaColor.Black => 'B',
        ManaColor.Red => 'R',
        ManaColor.Green => 'G',
        _ => 'C',
    };

    // Re-render an already-braced cost (captured from oracle text, lower-cased) into canonical
    // upper-case braced form so the stored suggestion matches ManaCostParser's expectations.
    private static string NormalizeBracedCost(string braced) =>
        braced.ToUpperInvariant();

    private static readonly char[] ScopeWordSeparators = { ' ', '-' };

    private static ReductionScope? ClassifyReducerScope(string scopePhrase)
    {
        string[] words = scopePhrase.Trim()
            .Split(ScopeWordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return ReductionScope.All; // bare "spells you cast cost {N} less" = every spell
        }

        // Match on WHOLE words, not substrings: "noncreature"/"nonartifact" must NOT read as the
        // creature/artifact scope (a substring check classified "noncreature" as Creature — the
        // opposite subset). Unmatched non-empty scopes (tribal/supertype/"noncreature") fall through
        // to null below.
        if (words.Contains("instant") || words.Contains("sorcery"))
        {
            return ReductionScope.InstantSorcery;
        }

        if (words.Contains("creature"))
        {
            return ReductionScope.Creature;
        }

        if (words.Contains("artifact"))
        {
            return ReductionScope.Artifact;
        }

        // M5: an unrecognized non-empty scope is a tribal / supertype / "noncreature" narrowing
        // ("Giant", "Goblin", "Historic", "multicolored", "noncreature" spells you cast cost less).
        // It discounts only that subset, not every spell; modeling per-subset is out of scope, and
        // defaulting to ReductionScope.All over-credits the whole deck (cap −2). Drop the reducer
        // entirely — a safe under-credit.
        return null;
    }

    // ---- GRANT-01: mana-ability granters --------------------------------------------------

    /// <summary>Which creatures a granter turns into conditional any-color sources.</summary>
    private enum GranterScope
    {
        /// <summary>All creatures you control (Cryptolith Rite, Song of Freyalise).</summary>
        AllCreatures,

        /// <summary>Legendary creatures you control (Relic of Legends).</summary>
        LegendaryCreatures,
    }

    /// <summary>
    /// Detect a mana-ability granter (Cryptolith Rite / Song of Freyalise / Relic of Legends, or
    /// any "creatures you control have '{T}: Add'" text), including Equipment/Aura that grant the
    /// equipped/enchanted creature a mana ability (Paradise Mantle). Returns the scope or null.
    /// </summary>
    private static GranterScope? DetectGranter(CardFact card)
    {
        string text = card.OracleText ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        string lower = text.ToLowerInvariant();

        // MEDIUM-5: Equipment/Aura granters — "equipped/enchanted creature has '{T}: Add'".
        // A single equip/aura only ever enables one creature, so treat it as the broad
        // any-creature scope (the eligible-count cap in AddGrantedSources keeps it from stacking).
        if (lower.Contains("equipped creature has \"{t}: add", StringComparison.Ordinal)
            || lower.Contains("enchanted creature has \"{t}: add", StringComparison.Ordinal))
        {
            return GranterScope.AllCreatures;
        }

        bool tapForMana = lower.Contains("{t}: add", StringComparison.Ordinal)
            || TapClauseAddRegex.IsMatch(text);
        if (!tapForMana && !lower.Contains("have \"{t}", StringComparison.Ordinal))
        {
            return null;
        }

        if (lower.Contains("legendary creatures you control", StringComparison.Ordinal)
            || lower.Contains("legendary creature you control", StringComparison.Ordinal))
        {
            return GranterScope.LegendaryCreatures;
        }

        if (lower.Contains("creatures you control have", StringComparison.Ordinal))
        {
            return GranterScope.AllCreatures;
        }

        return null;
    }

    private static void AddGrantedSources(
        List<ManaSource> sources,
        IReadOnlyList<CardFact> cards,
        IReadOnlyList<GranterScope> granters,
        int deckColorCount)
    {
        if (granters.Count == 0)
        {
            return;
        }

        // Only the broadest scope present matters — eligible counts don't stack per-creature.
        bool anyAllCreatures = granters.Contains(GranterScope.AllCreatures);

        IReadOnlyList<ManaColor> deckColors = DeckColors(cards);
        if (deckColors.Count == 0)
        {
            return;
        }

        foreach (CardFact card in cards)
        {
            // MEDIUM-3: commanders ARE eligible granted sources (a commander creature is on the
            // battlefield like any other). Only exclude non-creatures and existing rocks/dorks.
            if (!IsType(card.TypeLine, "Creature"))
            {
                continue;
            }

            // A creature that is already a dork contributes a full weighted color source — don't
            // blanket-add a second any-color source on top of it.
            if (IsRockOrDork(card))
            {
                continue;
            }

            bool eligible = anyAllCreatures || IsLegendary(card.TypeLine);
            if (!eligible)
            {
                continue;
            }

            bool isSnow = IsSnowPermanent(card);

            for (int i = 0; i < card.Quantity; i++)
            {
                sources.Add(new ManaSource
                {
                    Name = card.Name + " (granted)",
                    Produces = deckColors,
                    Weight = 0.25,
                    IsLand = false,
                    IsCommander = card.IsCommander,
                    IsSnow = isSnow,

                    // MQ-02: conditional/granted sources stay at 1 mana — the Bernoulli activation
                    // gates a single speculative unit; multi-unit granted bundles are out of scope.
                    ManaAmount = 1,

                    // Why: granted any-color abilities intentionally don't pay {C}.

                    // Enabler-conditional: this source only produces if the granter (Cryptolith Rite,
                    // Relic of Legends, ...) is on the battlefield AND this creature survives. That is
                    // genuinely speculative and out of scope to model fully, so the simulator keeps the
                    // per-trial Bernoulli activation at the 0.25 weight ONLY for these. Deployable ramp
                    // is full-value in the sim (its friction is the deploy cost + online-turn).
                    IsConditional = true,
                });
            }
        }
    }

    private static bool IsLegendary(string typeLine) =>
        IsType(typeLine.Split("//")[0], "Legendary");

    private static IReadOnlyList<ManaColor> DeckColors(IReadOnlyList<CardFact> cards)
    {
        var colors = new List<ManaColor>();
        foreach (CardFact card in cards)
        {
            foreach (KeyValuePair<ManaColor, int> pip in ManaCostParser.Parse(card.ManaCost).Pips)
            {
                if (pip.Value > 0 && pip.Key != ManaColor.Colorless && !colors.Contains(pip.Key))
                {
                    colors.Add(pip.Key);
                }
            }
        }

        return colors;
    }
}
