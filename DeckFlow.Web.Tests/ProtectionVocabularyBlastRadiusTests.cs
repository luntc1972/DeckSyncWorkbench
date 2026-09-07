using System.Reflection;
using DeckFlow.Core.Analysis;
using DeckFlow.Core.Manabase;
using DeckFlow.Web.Services.CutLab;
using DeckFlow.Web.Services.Manabase;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Fixture-driven measurement of what Phase 9.1's widened <see cref="DeckStatClassifier.ProtectionOracleNeedles"/>
/// actually changed, across the nine real committed decks in <c>DeckFlow.Web.Tests/Manabase/fixtures/</c>
/// and at all three production consumers of <see cref="DeckStatClassifier.IsProtectionCard"/>:
/// <see cref="CutLabRoleAssigner"/>'s <c>protection</c> role, <see cref="PlanRoleClassifier"/>'s
/// <see cref="PlanRole.Interaction"/> grant, and <see cref="InteractionAuditAggregator"/>'s
/// protection/recursion bucket. A failure here means the shipped vocabulary now classifies a
/// DIFFERENT set of real cards than the set plan 09.1-03's Task 2 checkpoint measured and a human
/// explicitly accepted — re-measure (docs/research/protection-vocabulary-blast-radius-2026-09.md),
/// get the new delta re-reviewed, and only then update the pinned arrays below. Never edit the
/// arrays to make a failure go away without a fresh human sign-off; that is exactly the "golden
/// regenerated to make a test pass" failure mode Success Criterion 3 exists to prevent.
/// </summary>
public sealed class ProtectionVocabularyBlastRadiusTests
{
    // Real removal spells whose oracle text says a destroyed permanent "can't be regenerated" --
    // the over-match family Success Criterion 4 (Phase 9.1) bounds. Present in these fixtures.
    // Asserted absent from all three consumers independently of the pinned arrays below, so a
    // future needle change that reintroduces the over-match fails here even if the arrays are
    // updated to match it.
    private static readonly string[] CannotBeRegeneratedRemoval =
    [
        "Artifact Mutation",
        "Damn",
        "Damnation",
        "Putrefy",
        "Terminate",
    ];

    // ---- AFTER (widened, shipped) sets -- measured by running the real production consumers over
    // the nine fixtures and reading back what they produced. Populated from the actual run, not
    // transcribed from PLAN.md. See docs/research/protection-vocabulary-blast-radius-2026-09.md for
    // the added/removed card breakdown and the needle that moved each one. ----

    // Shared by both the Cut Lab protection role assertion and the interaction-audit
    // protection-attributable assertion below: the docs confirm the two consumers land on the
    // identical 20-card set for this fixture corpus ("no recursion collision in this fixture
    // set"), so one array expresses that fact instead of two copies that could silently diverge.
    private static readonly string[] ExpectedProtectionAttributableNames =
    [
        "Amalia Benavides Aguirre",
        "Boromir, Warden of the Tower",
        "Brave the Elements",
        "Deflecting Swat",
        "Flare of Fortitude",
        "Flawless Maneuver",
        "Giver of Runes",
        "Heroic Intervention",
        "Kytheon, Hero of Akros // Gideon, Battle-Forged",
        "Lightning Greaves",
        "Loran's Escape",
        "Mother of Runes",
        "Plaza of Heroes",
        "Revitalizing Repast // Old-Growth Grove",
        "Seasoned Dungeoneer",
        "Swiftfoot Boots",
        "Sylvan Safekeeper",
        "Teferi's Protection",
        "The One Ring",
        "Whispersilk Cloak",
    ];

    private static readonly string[] ExpectedCutLabInteractionTargetedNames =
    [
        "Abrupt Decay",
        "Akroma's Will",
        "Amalia Benavides Aguirre",
        "Anguished Unmaking",
        "Artifact Mutation",
        "Assassin's Trophy",
        "Beast Within",
        "Bojuka Bog",
        "Bone Shards",
        "Boromir, Warden of the Tower",
        "Bounty Agent",
        "Brave the Elements",
        "Cabal Ritual",
        "Casualties of War",
        "Corrupted Conviction",
        "Crop Rotation",
        "Culling the Weak",
        "Cyclonic Rift",
        "Damn",
        "Dark Ritual",
        "Dawnbringer Cleric",
        "Deadly Dispute",
        "Deadly Rollick",
        "Deduce",
        "Deflecting Swat",
        "Dispatch",
        "Displace",
        "Elspeth Conquers Death",
        "Entomb",
        "Ephemerate",
        "Erode",
        "Fell the Profane // Fell Mire",
        "Flare of Fortitude",
        "Flawless Maneuver",
        "Generous Gift",
        "Get Lost",
        "Ghostly Flicker",
        "Ghostway",
        "Giver of Runes",
        "Go for the Throat",
        "Grisly Salvage",
        "Harrow",
        "Heliod's Intervention",
        "Heroic Intervention",
        "Hide on the Ceiling",
        "Infernal Grasp",
        "Kytheon, Hero of Akros // Gideon, Battle-Forged",
        "Lightning Greaves",
        "Loran's Escape",
        "Mother of Runes",
        "Nasty End",
        "Path to Exile",
        "Plaza of Heroes",
        "Putrefy",
        "Ravenous Chupacabra",
        "Revitalizing Repast // Old-Growth Grove",
        "Seasoned Dungeoneer",
        "Songs of the Damned",
        "Strip Mine",
        "Swiftfoot Boots",
        "Swords to Plowshares",
        "Sylvan Safekeeper",
        "Teferi's Protection",
        "Teferi, Hero of Dominaria",
        "Terminate",
        "The One Ring",
        "Thrill of Possibility",
        "Umezawa's Jitte",
        "Vampiric Tutor",
        "Venser, Shaper Savant",
        "Venser, the Sojourner",
        "Village Rites",
        "Wasteland",
        "Whispersilk Cloak",
        "Witch Enchanter // Witch-Blessed Meadow",
        "Wither and Bloom",
        "Y'shtola Rhul",
    ];

    private static readonly string[] ExpectedInteractionTargetedWideningNames =
    [
        "Amalia Benavides Aguirre",
        "Boromir, Warden of the Tower",
        "Giver of Runes",
        "Lightning Greaves",
        "Mother of Runes",
        "Seasoned Dungeoneer",
        "Swiftfoot Boots",
        "Sylvan Safekeeper",
        "Whispersilk Cloak",
    ];

    private static readonly string[] ExpectedPlanRoleInteractionViaProtectionNames =
    [
        "Amalia Benavides Aguirre",
        "Boromir, Warden of the Tower",
        "Giver of Runes",
        "Kytheon, Hero of Akros // Gideon, Battle-Forged",
        "Lightning Greaves",
        "Mother of Runes",
        "Plaza of Heroes",
        "Seasoned Dungeoneer",
        "Swiftfoot Boots",
        "Sylvan Safekeeper",
        "The One Ring",
        "Whispersilk Cloak",
    ];

    // ---- BEFORE (narrow, historical) sets -- a test-local reproduction of the FOUR needles that
    // shipped before this phase ("gains hexproof", "gains indestructible", "gain protection from",
    // "phases out", each an OrdinalIgnoreCase substring, plus the curated StaxProtectionCatalog
    // list), run for real over the same nine fixtures. These four needles are frozen historical
    // fact -- they will never change -- so hard-coding them locally here is a measurement of
    // history, not a guessed vocabulary standing in for the shipped one; the shipped table lives
    // only in DeckStatClassifier.ProtectionOracleNeedles. Pinned here so the delta this test proves
    // (added/removed card lists in the docs artifact) is itself asserted, not merely computed and
    // discarded. ----

    private static readonly string[] ExpectedBeforeCutLabAndAuditProtectionNames =
    [
        "Brave the Elements",
        "Deflecting Swat",
        "Flawless Maneuver",
        "Heroic Intervention",
        "Kytheon, Hero of Akros // Gideon, Battle-Forged",
        "Loran's Escape",
        "Plaza of Heroes",
        "Revitalizing Repast // Old-Growth Grove",
        "Teferi's Protection",
        "The One Ring",
    ];

    private static readonly string[] ExpectedBeforePlanRoleInteractionViaProtectionNames =
    [
        "Kytheon, Hero of Akros // Gideon, Battle-Forged",
        "Plaza of Heroes",
        "The One Ring",
    ];

    [Fact]
    public void ProtectionRole_AcrossNineFixtures_MatchesMeasuredAcceptedSet()
    {
        IReadOnlyList<CardFact> cards = DistinctFixtureCardsCache.Value;

        string[] afterNames = cards
            .Where(fact => CutLabRoleAssigner
                .AssignRoles(fact, Array.Empty<string>(), isComboPiece: false, ManabaseMode.Casual)
                .Contains("protection"))
            .Select(fact => fact.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedProtectionAttributableNames, afterNames);
    }

    [Fact]
    public void InteractionTargetedRole_AcrossNineFixtures_MatchesMeasuredAcceptedSet()
    {
        IReadOnlyList<CardFact> cards = DistinctFixtureCardsCache.Value;

        string[] afterNames = cards
            .Where(fact => CutLabRoleAssigner
                .AssignRoles(fact, Array.Empty<string>(), isComboPiece: false, ManabaseMode.Casual)
                .Contains("interaction-targeted"))
            .Select(fact => fact.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(77, afterNames.Length);
        Assert.Equal(ExpectedCutLabInteractionTargetedNames, afterNames);
        Assert.Equal(9, afterNames.Intersect(ExpectedInteractionTargetedWideningNames).Count());
        Assert.Equal(68, afterNames.Except(ExpectedInteractionTargetedWideningNames).Count());
    }

    [Fact]
    public void PlanRoleInteractionViaProtection_AcrossNineFixtures_MatchesMeasuredAcceptedSet()
    {
        IReadOnlyList<CardFact> cards = DistinctFixtureCardsCache.Value;

        string[] afterNames = cards
            .Where(fact =>
            {
                string oracle = fact.FrontOracleText;
                bool isProtection = DeckStatClassifier.IsProtectionCard(fact.Name, oracle);
                PlanRole roles = PlanRoleClassifier.Classify(
                    fact, Array.Empty<string>(), isComboPiece: false, ManabaseMode.Casual, out _);
                return isProtection && roles.HasFlag(PlanRole.Interaction);
            })
            .Select(fact => fact.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedPlanRoleInteractionViaProtectionNames, afterNames);
    }

    [Fact]
    public void InteractionAuditProtectionRecursionBucket_AttributableToProtection_MatchesMeasuredAcceptedSet()
    {
        IReadOnlyList<CardFact> cards = DistinctFixtureCardsCache.Value;

        var inputs = cards
            .Select(fact => new InteractionCardInput(1, fact.Name, fact.TypeLine, fact.FrontOracleText, fact.ManaCost ?? string.Empty))
            .ToArray();
        InteractionAudit audit = InteractionAuditAggregator.Compute(inputs);
        Dictionary<string, CardFact> byName = cards.ToDictionary(fact => fact.Name, StringComparer.Ordinal);

        // The bucket merges recursion OR protection. Attribute a bucket member to protection only
        // when the widened predicate matches AND the recursion predicate does NOT -- a card that is
        // ALSO a recursion card was already going to be in this bucket regardless of the widening,
        // so counting it here would overstate the protection-specific movement.
        string[] afterNames = audit.ProtectionRecursion.Confident
            .Select(card => card.Name)
            .Where(name =>
            {
                string oracle = byName[name].FrontOracleText;
                return DeckStatClassifier.IsProtectionCard(name, oracle) && !DeckStatClassifier.IsRecursionCard(oracle);
            })
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedProtectionAttributableNames, afterNames);
    }

    [Fact]
    public void HistoricalNarrowVocabulary_AcrossNineFixtures_MatchesMeasuredBeforeSets()
    {
        IReadOnlyList<CardFact> cards = DistinctFixtureCardsCache.Value;

        string[] beforeCutLabAndAudit = cards
            .Where(fact => BeforeProtection(fact.Name, fact.FrontOracleText))
            .Select(fact => fact.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedBeforeCutLabAndAuditProtectionNames, beforeCutLabAndAudit);

        // The audit bucket's protection-attributable set collapses to the same list as Cut Lab's
        // before-set here: none of the ten narrow-vocabulary protection cards in these fixtures are
        // also recursion cards, so the recursion exclusion changes nothing pre-widening.
        string[] beforeAudit = cards
            .Where(fact => BeforeProtection(fact.Name, fact.FrontOracleText) && !DeckStatClassifier.IsRecursionCard(fact.FrontOracleText))
            .Select(fact => fact.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedBeforeCutLabAndAuditProtectionNames, beforeAudit);

        string[] beforePlanRole = cards
            .Where(fact =>
            {
                string oracle = fact.FrontOracleText;
                bool beforeIsProtection = BeforeProtection(fact.Name, oracle);
                bool grantsInteraction = BeforeGrantsInteraction(fact.Name, fact.TypeLine, oracle)
                    && !CardTypeLine.IsNonPermanentFront(fact.TypeLine);
                return beforeIsProtection && grantsInteraction;
            })
            .Select(fact => fact.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedBeforePlanRoleInteractionViaProtectionNames, beforePlanRole);
    }

    [Fact]
    public void CannotBeRegeneratedRemovalFamily_NeverAppearsAsProtectionAtAnyConsumer()
    {
        IReadOnlyList<CardFact> cards = DistinctFixtureCardsCache.Value;
        Dictionary<string, CardFact> byName = cards.ToDictionary(fact => fact.Name, StringComparer.Ordinal);

        var inputs = cards
            .Select(fact => new InteractionCardInput(1, fact.Name, fact.TypeLine, fact.FrontOracleText, fact.ManaCost ?? string.Empty))
            .ToArray();
        InteractionAudit audit = InteractionAuditAggregator.Compute(inputs);
        HashSet<string> auditBucketNames = audit.ProtectionRecursion.Confident
            .Select(card => card.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string name in CannotBeRegeneratedRemoval)
        {
            Assert.True(byName.ContainsKey(name), $"Fixture sanity: expected {name} to be present in the committed fixtures.");
            CardFact fact = byName[name];
            string oracle = fact.FrontOracleText;

            // Independent of the pinned arrays above: the classifier itself must never call this
            // card protection. Since every one of the three consumers requires IsProtectionCard to
            // be true before it can add the card to a protection-adjacent set, this single assertion
            // structurally guards all three at their common root.
            Assert.False(DeckStatClassifier.IsProtectionCard(name, oracle),
                $"{name} is a 'can't be regenerated' removal spell and must never classify as protection.");

            IReadOnlyList<string> cutLabRoles = CutLabRoleAssigner.AssignRoles(fact, Array.Empty<string>(), isComboPiece: false, ManabaseMode.Casual);
            Assert.DoesNotContain("protection", cutLabRoles);

            PlanRole roles = PlanRoleClassifier.Classify(fact, Array.Empty<string>(), isComboPiece: false, ManabaseMode.Casual, out _);
            bool attributableToProtectionViaPlanRole = DeckStatClassifier.IsProtectionCard(name, oracle) && roles.HasFlag(PlanRole.Interaction);
            Assert.False(attributableToProtectionViaPlanRole);

            bool attributableToProtectionViaAudit = auditBucketNames.Contains(name)
                && DeckStatClassifier.IsProtectionCard(name, oracle)
                && !DeckStatClassifier.IsRecursionCard(oracle);
            Assert.False(attributableToProtectionViaAudit);
        }
    }

    // Test-local reproduction of GrantsInteraction's non-protection arms (PlanRoleClassifier.cs),
    // needed only to recompute the HISTORICAL (pre-widening) Interaction grant with the narrow
    // BeforeProtection predicate substituted for the shipped DeckStatClassifier.IsProtectionCard.
    // CountersASpell is reproduced inline (it is a private helper on PlanRoleClassifier) since its
    // two-substring check is itself frozen historical fact, not a guessed vocabulary.
    private static bool BeforeGrantsInteraction(string name, string typeLine, string oracle)
    {
        bool countersASpell = oracle.Contains("counter target", StringComparison.OrdinalIgnoreCase)
            && oracle.Contains("spell", StringComparison.OrdinalIgnoreCase);
        bool interactionMerit = DeckStatClassifier.IsInteractionCard(typeLine, oracle) && !countersASpell;

        return interactionMerit
            || DeckStatClassifier.IsBoardWipeCard(oracle)
            || DeckStatClassifier.IsTargetedRemovalCard(typeLine, oracle)
            || BeforeProtection(name, oracle);
    }

    // The four needles ProtectionOracleNeedles carried before Phase 9.1 widened it, read via
    // ProtectionNeedle.IsPreWideningBaseline rather than retyped here, plus the curated
    // StaxProtectionCatalog list. Frozen historical fact -- reproduced by filtering the shipped
    // table (not a second hand-copy of it) so the before/after delta in
    // docs/research/protection-vocabulary-blast-radius-2026-09.md is a real in-test measurement,
    // not a number quoted from memory.
    private static bool BeforeProtection(string name, string oracle)
        => StaxProtectionCatalog.IsProtection(name)
            || DeckStatClassifier.ProtectionOracleNeedles
                .Where(needle => needle.IsPreWideningBaseline)
                .Any(needle => oracle.Contains(needle.Text, StringComparison.OrdinalIgnoreCase));

    // The nine fixtures are immutable for the life of the test run, so every [Fact] in this class
    // reads the same cached load instead of re-globbing the directory and re-deserializing 9 JSON
    // files apiece.
    private static readonly Lazy<IReadOnlyList<CardFact>> DistinctFixtureCardsCache = new(LoadDistinctFixtureCardsFromDisk);

    // Loads all nine dot-prefixed real-deck fact fixtures (they are dot-prefixed, so a bare
    // "*.json" glob would silently match nothing -- the leading dot is part of the pattern) and
    // deduplicates cards by name across decks. Shares RepoPaths/CardFactFixtureFile with
    // BragoRealDeckHarness/ManabaseHealthBandRegressionTests rather than each re-implementing the
    // repo-root walk and the fixture deserialize.
    private static List<CardFact> LoadDistinctFixtureCardsFromDisk()
    {
        string fixturesDir = Path.Combine(RepoPaths.Root(), "DeckFlow.Web.Tests", "Manabase", "fixtures");
        string[] files = Directory.GetFiles(fixturesDir, ".manabase-*-facts.json");
        Assert.True(files.Length == 9, $"Expected nine dot-prefixed fixtures, found {files.Length} at {fixturesDir}.");

        var byName = new Dictionary<string, CardFact>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            List<CardFact> facts = CardFactFixtureFile.Load(file);
            foreach (CardFact fact in facts)
            {
                byName.TryAdd(fact.Name, fact);
            }
        }

        return byName.Values.ToList();
    }
}
