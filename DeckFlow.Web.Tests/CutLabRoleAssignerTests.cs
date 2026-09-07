using DeckFlow.Core.Manabase;
using DeckFlow.Web.Services.CutLab;

using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>Coverage for pure Cut Lab role assignment across the nine fixed slot keys.</summary>
public sealed class CutLabRoleAssignerTests
{
    [Fact]
    public void AssignRoles_Forest_MapsToExactlyLands()
    {
        CardFact fact = Fact(
            "Forest",
            "Basic Land — Forest");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["lands"], roles);
    }

    [Fact]
    public void AssignRoles_Cultivate_MapsToRampOnly()
    {
        CardFact fact = Fact(
            "Cultivate",
            "Sorcery",
            oracle: "Search your library for up to two basic land cards, reveal those cards, put one onto the battlefield tapped and the other into your hand, then shuffle.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["ramp"], roles);
    }

    // Why: singular "gains protection from" is the ROADMAP-named D-06 defect (09.1-RESEARCH.md).
    // Assert.Contains rather than Assert.Equal, because a Cleric with a tap ability may
    // legitimately earn other roles beyond protection and pinning the whole list would make this
    // test fail for reasons unrelated to protection.
    [Fact]
    public void AssignRoles_MotherOfRunes_IncludesProtection()
    {
        CardFact fact = Fact(
            "Mother of Runes",
            "Creature — Human Cleric",
            oracle: "{T}: Target creature you control gains protection from the color of your choice until end of turn.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Contains("protection", roles);
    }

    [Fact]
    public void AssignRoles_ManaSymbolRock_IncludesRamp()
    {
        CardFact fact = Fact(
            "Talisman of Dominance",
            "Artifact",
            oracle: "{T}: Add {C}.\n{T}, Pay 1 life: Add {U} or {B}.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Contains("ramp", roles);
    }

    [Fact]
    public void AssignRoles_ModalDfcLandFront_MapsToLandsAndNotRamp()
    {
        CardFact fact = Fact(
            "Bala Ged Sanctuary // Bala Ged Recovery",
            "Land // Sorcery",
            oracle: "{T}: Add {G}. // Return target card from your graveyard to your hand.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["lands"], roles);
    }

    [Fact]
    public void AssignRoles_ModalDfcSpellFrontWithLandBack_MapsToLandsAndNotRamp()
    {
        CardFact fact = Fact(
            "Sea Gate Restoration // Sea Gate, Reborn",
            "Sorcery // Land",
            oracle: "Draw cards equal to the number of cards in your hand plus one. You have no maximum hand size. // As Sea Gate, Reborn enters, you may pay 3 life. If you don't, it enters tapped.\n{T}: Add {U}.")
            with
        {
            FrontFaceOracleText = "Draw cards equal to the number of cards in your hand plus one. You have no maximum hand size.",
            HasLandFace = true,
        };

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Contains("lands", roles);
        Assert.DoesNotContain("ramp", roles);
    }

    [Fact]
    public void AssignRoles_UsesFrontFaceOracleTextBeforeJoinedOracleText()
    {
        CardFact fact = Fact(
            "Invasion of Insight // Insight Engine",
            "Battle // Artifact",
            oracle: "When Invasion of Insight enters, scry 2. // At the beginning of your upkeep, draw a card.")
            with
        {
            FrontFaceOracleText = "When Invasion of Insight enters, scry 2.",
        };

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.DoesNotContain("draw", roles);
    }

    [Fact]
    public void AssignRoles_UnclassifiedResolvedCard_FallsBackToOther()
    {
        CardFact fact = Fact(
            "Hill Giant",
            "Creature — Giant",
            oracle: "A simple creature.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["other"], roles);
    }

    [Fact]
    public void AssignRoles_WipeByOracleHeuristic_IsMassOnly()
    {
        CardFact fact = Fact(
            "Wrath of God",
            "Sorcery",
            oracle: "Destroy all creatures. They can't be regenerated.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["interaction-mass"], roles);
    }

    [Theory]
    [InlineData("wipe")]
    [InlineData("Board Wipe")]
    [InlineData("Board Wipes")]
    [InlineData("BOARD WIPE")]
    public void AssignRoles_WipeCategoryTagWithoutHeuristic_IsMassOnly(string category)
    {
        CardFact fact = Fact(
            "Cyclonic Redirection",
            "Instant",
            oracle: "Return all nonland permanents that player controls to their owner's hand.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            [category],
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["interaction-mass"], roles);
    }

    [Fact]
    public void AssignRoles_SwordsToPlowshares_IsTargetedOnlyInCasualViaPreGateSignal()
    {
        CardFact fact = Fact(
            "Swords to Plowshares",
            "Instant",
            oracle: "Exile target creature. Its controller gains life equal to its power.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["interaction-targeted"], roles);
    }

    [Theory]
    [InlineData(ManabaseMode.Cedh, new[] { "interaction-targeted" })]
    [InlineData(ManabaseMode.Casual, new[] { "other" })]
    public void AssignRoles_Counterspell_RespectsModeGate(ManabaseMode mode, string[] expected)
    {
        CardFact fact = Fact(
            "Counterspell",
            "Instant",
            oracle: "Counter target spell.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: false,
            mode);

        Assert.Equal(expected, roles);
    }

    [Fact]
    public void AssignRoles_GenericInteractionCategoryWithoutPredicate_IsTargetedOnly()
    {
        CardFact fact = Fact(
            "Flexible Answer",
            "Artifact",
            oracle: "Artifacts you control enter untapped.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            ["answer"],
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["interaction-targeted"], roles);
    }

    [Fact]
    public void AssignRoles_InteractionRolesStayMutuallyExclusiveAcrossRepresentativeCards()
    {
        (CardFact Fact, IReadOnlyList<string> Categories, bool IsComboPiece, ManabaseMode Mode)[] samples =
        [
            (Fact("Wrath of God", "Sorcery", "Destroy all creatures. They can't be regenerated."), Array.Empty<string>(), false, ManabaseMode.Casual),
            (Fact("Cyclonic Redirection", "Instant", "Return all nonland permanents that player controls to their owner's hand."), ["Board Wipe"], false, ManabaseMode.Casual),
            (Fact("Swords to Plowshares", "Instant", "Exile target creature. Its controller gains life equal to its power."), Array.Empty<string>(), false, ManabaseMode.Casual),
            (Fact("Counterspell", "Instant", "Counter target spell."), Array.Empty<string>(), false, ManabaseMode.Cedh),
            (Fact("Flexible Answer", "Artifact", "Artifacts you control enter untapped."), ["answer"], false, ManabaseMode.Casual),
        ];

        foreach ((CardFact sampleFact, IReadOnlyList<string> categories, bool isComboPiece, ManabaseMode mode) in samples)
        {
            IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(sampleFact, categories, isComboPiece, mode);
            Assert.False(
                roles.Contains("interaction-targeted", StringComparer.Ordinal)
                && roles.Contains("interaction-mass", StringComparer.Ordinal),
                sampleFact.Name);
        }
    }

    [Fact]
    public void AssignRoles_CanonicalEmissionOrder_PutsTargetedBeforeLaterRoles()
    {
        CardFact fact = Fact(
            "Swords to Plowshares",
            "Instant",
            oracle: "Exile target creature. Its controller gains life equal to its power.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: true,
            ManabaseMode.Casual);

        Assert.Equal(["interaction-targeted", "wincons"], roles);
    }

    [Fact]
    public void AssignRoles_RhysticStudy_CanHoldDrawAndEngineRoles()
    {
        CardFact fact = Fact(
            "Rhystic Study",
            "Enchantment",
            oracle: "Whenever an opponent casts a spell, you may draw a card unless that player pays {1}.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            new[] { "Card Draw" },
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["draw", "engines"], roles);
    }

    [Fact]
    public void AssignRoles_OneShotDrawSpell_NotEngine()
    {
        CardFact fact = Fact(
            "Quick Study",
            "Instant",
            oracle: "Draw two cards.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            new[] { "Card Draw" },
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["draw"], roles);
    }

    [Fact]
    public void AssignRoles_PermanentDrawEngine_IsEngine()
    {
        CardFact fact = Fact(
            "Phyrexian Arena",
            "Enchantment",
            oracle: "At the beginning of your upkeep, draw a card and you lose 1 life.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            new[] { "Card Draw" },
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["draw", "engines"], roles);
    }

    [Fact]
    public void AssignRoles_ComboPiece_IsWinconEvenWithoutClosingPower()
    {
        CardFact fact = Fact(
            "Isochron Scepter",
            "Artifact",
            oracle: "Imprint — When Isochron Scepter enters, you may exile an instant card with mana value 2 or less from your hand.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            Array.Empty<string>(),
            isComboPiece: true,
            ManabaseMode.Casual);

        Assert.Equal(["wincons"], roles);
    }

    [Fact]
    public void AssignRoles_TormentOfHailfire_IsWinconDespitePlanRolePermanentGate()
    {
        CardFact fact = Fact(
            "Torment of Hailfire",
            "Sorcery",
            oracle: "Repeat the following process X times. Each opponent loses 3 life unless that player sacrifices a nonland permanent or discards a card.");

        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(
            fact,
            new[] { "Win Condition" },
            isComboPiece: false,
            ManabaseMode.Casual);

        Assert.Equal(["wincons"], roles);
    }

    [Theory]
    [InlineData("cEDH", ManabaseMode.Cedh)]
    [InlineData("CeDh", ManabaseMode.Cedh)]
    [InlineData("Focused", ManabaseMode.Focused)]
    [InlineData("Casual", ManabaseMode.Casual)]
    [InlineData("", ManabaseMode.Casual)]
    [InlineData("unknown", ManabaseMode.Casual)]
    public void ResolveMode_MapsPlayExperienceLabels(string? playExperience, ManabaseMode expected)
    {
        Assert.Equal(expected, CutLabRoleAssigner.ResolveMode(playExperience));
    }

    [Fact]
    public void ResolveMode_Null_FallsBackToCasual()
    {
        Assert.Equal(ManabaseMode.Casual, CutLabRoleAssigner.ResolveMode(null));
    }

    private static CardFact Fact(string name, string typeLine, string? oracle = null) => new()
    {
        Name = name,
        Quantity = 1,
        TypeLine = typeLine,
        OracleText = oracle,
        ManaValue = 0,
    };
}
