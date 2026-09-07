using System;

using DeckFlow.Core.Manabase;
using DeckFlow.Web.Services.Manabase;

using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Phase 2 (plan-presence): <see cref="PlanRoleClassifier"/> — the pure 3-source role resolver
/// (crowd categories → combo piece → oracle-text heuristic, first-hit-wins). The I/O that fetches
/// categories and the combo set lives in the service; this tests the pure decision. Counter handling
/// is mode-dependent: a pure counterspell earns Interaction only in cEDH.
/// </summary>
public sealed class PlanRoleClassifierTests
{
    private static CardFact Fact(string typeLine, string? oracle = null, string name = "Card") => new()
    {
        Name = name,
        Quantity = 1,
        TypeLine = typeLine,
        OracleText = oracle,
    };

    [Theory]
    [InlineData("Win Condition", PlanRole.Payoff)]
    [InlineData("Finisher", PlanRole.Payoff)]
    [InlineData("Removal", PlanRole.Interaction)]
    [InlineData("Protection", PlanRole.Interaction)]
    [InlineData("Tutor", PlanRole.TutorCombo)]
    [InlineData("Combo Piece", PlanRole.TutorCombo)]
    [InlineData("Card Draw", PlanRole.Engine)]
    [InlineData("Value Engine", PlanRole.Engine)]
    public void FromCategories_MapsKeywordToRole(string category, PlanRole expected)
    {
        // These keywords are mode-independent; Casual is representative.
        Assert.Equal(expected, PlanRoleClassifier.FromCategories(new[] { category }, ManabaseMode.Casual));
    }

    [Theory]
    [InlineData(ManabaseMode.Cedh, PlanRole.Interaction)]
    [InlineData(ManabaseMode.Casual, PlanRole.None)]
    public void FromCategories_CounterTag_IsInteractionOnlyInCedh(ManabaseMode mode, PlanRole expected)
    {
        Assert.Equal(expected, PlanRoleClassifier.FromCategories(new[] { "Counterspell" }, mode));
    }

    // Why: harvested corpus cards like Cordial Vampire and Blade of the Bloodchief wear "Counters"
    // for the +1/+1-counters theme, not because they counter spells.
    [Theory]
    [InlineData("Counters")]
    [InlineData("Counters Matter")]
    [InlineData("+1/+1 Counters")]
    [InlineData("counters")]
    [InlineData("Encounter")]
    [InlineData("Encounters")]
    public void FromCategories_CountersSynergyTag_IsNotACounterspell(string category)
    {
        Assert.Equal(PlanRole.None, PlanRoleClassifier.FromCategories(new[] { category }, ManabaseMode.Cedh));
    }

    [Theory]
    [InlineData("Counterspell")]
    [InlineData("Counterspells")]
    [InlineData("Counter")]
    [InlineData("Counter Magic")]
    [InlineData("Countermagic")]
    [InlineData("Counter-magic")]
    public void FromCategories_CounterspellTagVariants_StillEarnInteractionInCedhOnly(string category)
    {
        Assert.Equal(PlanRole.Interaction, PlanRoleClassifier.FromCategories(new[] { category }, ManabaseMode.Cedh));
        Assert.Equal(PlanRole.None, PlanRoleClassifier.FromCategories(new[] { category }, ManabaseMode.Casual));
    }

    [Theory]
    [InlineData("Counters")]
    [InlineData("Counters Matter")]
    [InlineData("+1/+1 Counters")]
    [InlineData("counters")]
    [InlineData("Encounter")]
    [InlineData("Encounters")]
    [InlineData("Counterspell")]
    [InlineData("Counterspells")]
    [InlineData("Counter")]
    [InlineData("Counter Magic")]
    [InlineData("Countermagic")]
    [InlineData("Counter-magic")]
    public void CategoryMapsToPlanRole_MatchesCedhFromCategories_ForCounterVocabulary(string categoryName)
    {
        Assert.Equal(
            PlanRoleClassifier.CategoryMapsToPlanRole(categoryName),
            PlanRoleClassifier.FromCategories(new[] { categoryName }, ManabaseMode.Cedh) != PlanRole.None);
    }

    [Theory]
    [InlineData("Ramp")]
    [InlineData("Mana Rock")]
    [InlineData("Lands")]
    [InlineData("Utility")]
    public void FromCategories_ResourceTags_YieldNone(string category)
    {
        Assert.Equal(PlanRole.None, PlanRoleClassifier.FromCategories(new[] { category }, ManabaseMode.Casual));
    }

    [Fact]
    public void FromCategories_MultipleTags_CombineAsFlags()
    {
        PlanRole roles = PlanRoleClassifier.FromCategories(new[] { "Win Condition", "Card Draw" }, ManabaseMode.Casual);

        Assert.Equal(PlanRole.Payoff | PlanRole.Engine, roles);
    }

    [Fact]
    public void FromHeuristic_PermanentDraw_IsEngine_ButOneShotDrawSpellIsNot()
    {
        PlanRole engine = PlanRoleClassifier.FromHeuristic(
            Fact("Artifact", "At the beginning of your upkeep, draw a card."), ManabaseMode.Casual);
        Assert.True(engine.HasFlag(PlanRole.Engine));

        PlanRole oneShot = PlanRoleClassifier.FromHeuristic(
            Fact("Sorcery", "Draw two cards."), ManabaseMode.Casual);
        Assert.False(oneShot.HasFlag(PlanRole.Engine));
    }

    [Fact]
    public void FromHeuristic_UsesFrontFaceOracleTextBeforeJoinedOracleText()
    {
        CardFact fact = Fact("Battle // Artifact", "When this enters, scry 2.")
            with
        {
            FrontFaceOracleText = "When this enters, scry 2.",
            OracleText = "When this enters, scry 2. // At the beginning of your upkeep, draw a card.",
        };

        PlanRole roles = PlanRoleClassifier.FromHeuristic(fact, ManabaseMode.Casual);

        Assert.False(roles.HasFlag(PlanRole.Engine));
    }

    [Fact]
    public void FromHeuristic_TutorAndInteractionAndPayoff_Detected()
    {
        Assert.True(PlanRoleClassifier.FromHeuristic(
            Fact("Sorcery", "Search your library for a card, then shuffle."), ManabaseMode.Casual).HasFlag(PlanRole.TutorCombo));

        Assert.True(PlanRoleClassifier.FromHeuristic(
            Fact("Instant", "Destroy target creature."), ManabaseMode.Casual).HasFlag(PlanRole.Interaction));

        Assert.True(PlanRoleClassifier.FromHeuristic(
            Fact("Sorcery", "Take an extra turn after this one."), ManabaseMode.Casual).HasFlag(PlanRole.Payoff));
    }

    [Fact]
    public void FromHeuristic_PureCounterspell_StrippedInCasual_KeptInCedh()
    {
        CardFact counter = Fact("Instant", "Counter target spell.");
        Assert.False(PlanRoleClassifier.FromHeuristic(counter, ManabaseMode.Casual).HasFlag(PlanRole.Interaction));
        Assert.True(PlanRoleClassifier.FromHeuristic(counter, ManabaseMode.Cedh).HasFlag(PlanRole.Interaction));
    }

    [Fact]
    public void FromHeuristic_NarrowCounter_StrippedInCasual_KeptInCedh()
    {
        // Negate-style narrow counter: DeckStatClassifier.IsCounterspellCard misses it (exact
        // "counter target spell" only), but the casual carve-out still strips it.
        CardFact negate = Fact("Instant", "Counter target noncreature spell.");
        Assert.False(PlanRoleClassifier.FromHeuristic(negate, ManabaseMode.Casual).HasFlag(PlanRole.Interaction));
        Assert.True(PlanRoleClassifier.FromHeuristic(negate, ManabaseMode.Cedh).HasFlag(PlanRole.Interaction));
    }

    [Fact]
    public void FromHeuristic_RemovalAndCounterWithRemoval_KeptInCasual()
    {
        // Real removal always counts, even in casual.
        Assert.True(PlanRoleClassifier.FromHeuristic(
            Fact("Instant", "Destroy target creature."), ManabaseMode.Casual).HasFlag(PlanRole.Interaction));

        // A counter that ALSO removes has removal merit beyond the counter, so it stays.
        Assert.True(PlanRoleClassifier.FromHeuristic(
            Fact("Instant", "Counter target spell. Destroy target creature."), ManabaseMode.Casual)
            .HasFlag(PlanRole.Interaction));

        // Board wipes and non-counter instants (burn, combat tricks) are interaction in both modes.
        Assert.True(PlanRoleClassifier.FromHeuristic(
            Fact("Sorcery", "Destroy all creatures."), ManabaseMode.Casual).HasFlag(PlanRole.Interaction));
        Assert.True(PlanRoleClassifier.FromHeuristic(
            Fact("Instant", "Deal 3 damage to any target."), ManabaseMode.Casual).HasFlag(PlanRole.Interaction));
    }

    [Theory]
    [InlineData(ManabaseMode.Casual)]
    [InlineData(ManabaseMode.Cedh)]
    public void FromHeuristic_ProtectionPermanent_EarnsInteractionInBothModes(ManabaseMode mode)
    {
        // Why: spike 002 found this artifact case is invisible today because it is not an Instant so
        // IsInteractionCard misses it, there is no destroy/exile verb so IsBoardWipeCard misses it,
        // and IsTargetedRemovalCard excludes "you control". The third case covers the singular
        // "phases out" arm that IsProtectionCard actually matches; the plural-subject "permanents
        // you control phase out" phrasing stays documented under D-06 instead of asserted here.
        Assert.True(PlanRoleClassifier.FromHeuristic(
            Fact("Artifact", "{T}: Target creature you control gains hexproof until end of turn."), mode)
            .HasFlag(PlanRole.Interaction));

        Assert.True(PlanRoleClassifier.FromHeuristic(
            Fact("Creature — Human Soldier", "{2}{W}: This creature gains indestructible until end of turn."), mode)
            .HasFlag(PlanRole.Interaction));

        Assert.True(PlanRoleClassifier.FromHeuristic(
            Fact("Artifact", "{T}: Target creature phases out."), mode)
            .HasFlag(PlanRole.Interaction));
    }

    // Why: proves plan 09.1-01's Mother of Runes classification (singular "gains protection from")
    // reaches PlanRoleClassifier.FromHeuristic in both modes. Asserted with HasFlag, not equality —
    // a Creature with a tap ability may legitimately earn more than Interaction, and an equality
    // assertion would encode a claim this plan has not measured.
    [Theory]
    [InlineData(ManabaseMode.Casual)]
    [InlineData(ManabaseMode.Cedh)]
    public void FromHeuristic_MotherOfRunes_EarnsInteractionInBothModes(ManabaseMode mode)
    {
        CardFact fact = Fact(
            "Creature — Human Cleric",
            "{T}: Target creature you control gains protection from the color of your choice until end of turn.",
            name: "Mother of Runes");

        Assert.True(PlanRoleClassifier.FromHeuristic(fact, mode).HasFlag(PlanRole.Interaction));
    }

    // Why: the permanent-only gate in Classify must not strip Interaction here — a Creature is a
    // permanent, so Mother of Runes's protection merit survives Classify, not just FromHeuristic.
    [Fact]
    public void Classify_MotherOfRunes_HasInteractionFlag()
    {
        CardFact fact = Fact(
            "Creature — Human Cleric",
            "{T}: Target creature you control gains protection from the color of your choice until end of turn.",
            name: "Mother of Runes");

        PlanRole roles = PlanRoleClassifier.Classify(fact, Array.Empty<string>(), isComboPiece: false, ManabaseMode.Casual);

        Assert.True(roles.HasFlag(PlanRole.Interaction));
    }

    [Fact]
    public void Classify_ProtectionPermanent_IsInteractionAndNothingElse()
    {
        CardFact artifactFact = Fact("Artifact", "{T}: Target creature you control gains hexproof until end of turn.");

        Assert.Equal(
            PlanRole.Interaction,
            PlanRoleClassifier.Classify(artifactFact, Array.Empty<string>(), isComboPiece: false, ManabaseMode.Casual));
    }

    [Fact]
    public void Classify_ProtectionInstant_IsStillStrippedByPermanentGate()
    {
        CardFact instantFact = Fact(
            "Instant",
            "Permanents you control gain indestructible until end of turn.",
            name: "Heroic Intervention");

        Assert.True(PlanRoleClassifier.FromHeuristic(instantFact, ManabaseMode.Casual).HasFlag(PlanRole.Interaction));
        Assert.Equal(
            PlanRole.None,
            PlanRoleClassifier.Classify(instantFact, Array.Empty<string>(), false, ManabaseMode.Casual));
    }

    [Fact]
    public void FromHeuristic_PlainCreature_YieldsNone()
    {
        Assert.Equal(PlanRole.None, PlanRoleClassifier.FromHeuristic(
            Fact("Creature — Bear", "Vanilla 2/2."), ManabaseMode.Casual));
    }

    [Fact]
    public void Classify_CategoriesWinOverComboAndHeuristic()
    {
        // Card is a combo piece AND its oracle text would tutor, but a Payoff category is present:
        // first-hit-wins means the category role is used, not TutorCombo.
        CardFact fact = Fact("Creature", "Search your library for a card.");

        PlanRole roles = PlanRoleClassifier.Classify(fact, new[] { "Win Condition" }, isComboPiece: true, ManabaseMode.Casual);

        Assert.Equal(PlanRole.Payoff, roles);
    }

    [Fact]
    public void Classify_ComboPieceWins_WhenNoCategoryRole()
    {
        // Resource-only category (no role) + combo piece -> TutorCombo, heuristic not consulted.
        CardFact fact = Fact("Creature", "Vanilla.");

        PlanRole roles = PlanRoleClassifier.Classify(fact, new[] { "Ramp" }, isComboPiece: true, ManabaseMode.Casual);

        Assert.Equal(PlanRole.TutorCombo, roles);
    }

    [Theory]
    [InlineData(ManabaseMode.Cedh)]
    [InlineData(ManabaseMode.Casual)]
    public void Classify_PureCounterspell_IsNeverAPlan_PermanentGate(ManabaseMode mode)
    {
        // Permanents-only plan gate: an instant counterspell leaves nothing on the board to advance the
        // win, so it earns no plan role in EITHER mode — even cEDH, where the heuristic would otherwise
        // grant Interaction. The mode carve-out still lives in FromHeuristic; the permanent gate at
        // Classify overrides it for non-permanent front faces.
        CardFact fact = Fact("Instant", "Counter target spell.");

        PlanRole roles = PlanRoleClassifier.Classify(fact, Array.Empty<string>(), isComboPiece: false, mode);

        Assert.Equal(PlanRole.None, roles);
    }

    [Fact]
    public void Classify_CedhInstantCounter_PreservesPreGateInteractionSignal_WhileReturningNone()
    {
        CardFact fact = Fact("Instant", "Counter target spell.");

        PlanRole roles = PlanRoleClassifier.Classify(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            ManabaseMode.Cedh,
            out bool interactionMeritPreGate);

        Assert.True(interactionMeritPreGate);
        Assert.False(roles.HasFlag(PlanRole.Interaction));
        Assert.Equal(PlanRole.None, roles);
    }

    [Theory]
    [InlineData("Instant", "Destroy target creature.")]          // removal → Interaction (stripped)
    [InlineData("Sorcery", "Take an extra turn after this one.")] // extra-turn finisher → Payoff (stripped)
    [InlineData("Sorcery", "Destroy all creatures.")]            // board wipe → Interaction (stripped)
    public void Classify_NonPermanentPayoffOrInteraction_EarnsNoPlan(string typeLine, string oracle)
    {
        // Each oracle IS detected by FromHeuristic, but Payoff/Interaction require a permanent: a one-shot
        // instant/sorcery threat or answer leaves nothing on the board, so the gate strips it.
        Assert.NotEqual(PlanRole.None, PlanRoleClassifier.FromHeuristic(Fact(typeLine, oracle), ManabaseMode.Cedh));

        Assert.Equal(
            PlanRole.None,
            PlanRoleClassifier.Classify(Fact(typeLine, oracle), Array.Empty<string>(), isComboPiece: false, ManabaseMode.Cedh));
    }

    [Fact]
    public void Classify_NonPermanentTutor_IsKept()
    {
        // A sorcery tutor points at the permanent win, so TutorCombo survives the permanent gate — both
        // from the oracle heuristic and from combo-piece membership.
        Assert.Equal(
            PlanRole.TutorCombo,
            PlanRoleClassifier.Classify(Fact("Sorcery", "Search your library for a card."), Array.Empty<string>(), isComboPiece: false, ManabaseMode.Casual));

        Assert.Equal(
            PlanRole.TutorCombo,
            PlanRoleClassifier.Classify(Fact("Instant", "Vanilla."), Array.Empty<string>(), isComboPiece: true, ManabaseMode.Casual));
    }

    [Fact]
    public void Classify_NonPermanentCardDraw_IsKept()
    {
        // Card advantage furthers the plan even as a one-shot: a sorcery tagged "Card Draw" keeps Engine.
        Assert.Equal(
            PlanRole.Engine,
            PlanRoleClassifier.Classify(Fact("Sorcery", "Draw two cards."), new[] { "Card Draw" }, isComboPiece: false, ManabaseMode.Casual));
    }

    [Fact]
    public void Classify_NonPermanentPayoffCategory_IsStripped()
    {
        // A sorcery tagged "Win Condition" earns Payoff from categories, which the permanent gate strips —
        // the category wins first-hit, so combo membership is not consulted here.
        CardFact sorcery = Fact("Sorcery", "Deal 5 damage to each opponent.");

        Assert.Equal(
            PlanRole.None,
            PlanRoleClassifier.Classify(sorcery, new[] { "Win Condition" }, isComboPiece: true, ManabaseMode.Casual));
    }

    [Fact]
    public void Classify_PermanentPayoff_IsKept()
    {
        // A creature finisher is a permanent, so its Payoff role survives the gate.
        CardFact creature = Fact("Creature — Avatar", "Whenever this attacks, each opponent loses 3 life.");

        PlanRole roles = PlanRoleClassifier.Classify(creature, new[] { "Win Condition" }, isComboPiece: false, ManabaseMode.Casual);

        Assert.Equal(PlanRole.Payoff, roles);
    }

    [Fact]
    public void Classify_AdventureCreatureFront_IsPermanent_RoleKept()
    {
        // Bonecrusher Giant: "Creature — Giant // Instant — Adventure". The FRONT is a permanent, so the
        // card is a valid plan even though its adventure back is an instant.
        CardFact adventure = Fact("Creature — Giant // Instant — Adventure", "Whenever this attacks...");

        PlanRole roles = PlanRoleClassifier.Classify(adventure, new[] { "Finisher" }, isComboPiece: false, ManabaseMode.Casual);

        Assert.Equal(PlanRole.Payoff, roles);
    }

    [Fact]
    public void Classify_SpellLandMdfc_JudgedOnInstantFront_EarnsNoPlan()
    {
        // Malakir Rebirth // Malakir Mire: the FRONT is an instant (the land back is a land, never a
        // plan), so even a "Removal"/combo tag earns nothing.
        CardFact mdfc = Fact("Instant // Land", "Return target creature card... // Malakir Mire enters tapped.");

        Assert.Equal(
            PlanRole.None,
            PlanRoleClassifier.Classify(mdfc, new[] { "Removal" }, isComboPiece: true, ManabaseMode.Cedh));
    }

    [Fact]
    public void Classify_PermanentFrontDfc_IsKept()
    {
        // A creature/creature (or creature/land back) DFC whose FRONT is a permanent qualifies.
        CardFact dfc = Fact("Creature — Werewolf // Creature — Werewolf", "Whenever this attacks...");

        Assert.Equal(
            PlanRole.Payoff,
            PlanRoleClassifier.Classify(dfc, new[] { "Win Condition" }, isComboPiece: false, ManabaseMode.Casual));
    }

    [Theory]
    [InlineData("Win Condition")]
    [InlineData("Card Draw")]
    [InlineData("Removal")]
    [InlineData("counterspells")]
    [InlineData("tutor")]
    [InlineData("Ramp")]
    [InlineData("landfall")]
    [InlineData("tokens")]
    public void CategoryMapsToPlanRole_MatchesCedhFromCategories(string categoryName)
    {
        Assert.Equal(
            PlanRoleClassifier.CategoryMapsToPlanRole(categoryName),
            PlanRoleClassifier.FromCategories(new[] { categoryName }, ManabaseMode.Cedh) != PlanRole.None);
    }
}
