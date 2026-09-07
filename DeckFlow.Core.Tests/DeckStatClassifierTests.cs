using DeckFlow.Core.Analysis;

namespace DeckFlow.Core.Tests;

/// <summary>
/// Unit tests for <see cref="DeckStatClassifier"/> — covers each classifier's true and false
/// cases plus all <see cref="DeckStatClassifier.ParseManaToken"/> variants.
/// </summary>
public sealed class DeckStatClassifierTests
{
    // -----------------------------------------------------------------------
    // IsRampCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Basic Land — Forest", "", true)]              // land type line
    [InlineData("", "add one mana of any color", true)]        // add one mana
    [InlineData("", "add two mana of any color", true)]        // add two mana
    [InlineData("", "search your library for a basic land", true)]  // basic land search
    [InlineData("", "search your library for up to two land cards", true)]  // generic land search
    [InlineData("", "create a Treasure token", true)]          // Treasure creation phrase 1
    [InlineData("", "you create a Treasure token.", true)]     // Treasure token phrase 2
    [InlineData("Artifact", "search your library for up to two basic land cards, put them", true)]  // up-to + land
    [InlineData("Artifact", "{T}: Add {C}.", true)]            // Mind Stone / Talisman colorless line
    [InlineData("Artifact", "{T}, Pay 1 life: Add {U} or {B}.", true)]  // Talisman of Dominance second line
    [InlineData("Artifact", "{1}, {T}: Add {U}{B}.", true)]    // Dimir Signet
    [InlineData("Artifact", "{T}: Add {C}{C}.", true)]         // Sol Ring
    [InlineData("Creature — Elf", "{T}: Add {G}.", true)]      // mana dork (Llanowar Elves)
    [InlineData("Instant", "Add {B}{B}{B}.", true)]            // Dark Ritual (ritual acceleration)
    public void IsRampCard_TrueCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsRampCard(typeLine, oracleText));
    }

    [Theory]
    [InlineData("Creature — Elf", "Tap: deal 1 damage to target creature.", false)]  // vanilla creature
    [InlineData("Instant", "Counter target spell.", false)]                           // counterspell, not ramp
    [InlineData("Enchantment", "Whenever a creature dies, draw a card.", false)]     // draw, not ramp
    public void IsRampCard_FalseCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsRampCard(typeLine, oracleText));
    }

    // -----------------------------------------------------------------------
    // IsDrawCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Draw three cards", true)]
    [InlineData("Investigate.", true)]
    [InlineData("Connive.", true)]
    [InlineData("Draw a card.", true)]
    [InlineData("When this enters, draw a card.", true)]
    public void IsDrawCard_TrueCases(string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsDrawCard(oracleText));
    }

    [Theory]
    [InlineData("Target player draws two cards", false)]
    [InlineData("Destroy target creature.", false)]
    [InlineData("Search your library for a basic land card.", false)]
    [InlineData("Raugrin Triome enters the battlefield tapped.\n{T}: Add {R}, {U}, or {W}.\nCycling {3} ({3}, Discard this card: Draw a card.)", false)]
    public void IsDrawCard_FalseCases(string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsDrawCard(oracleText));
    }

    // -----------------------------------------------------------------------
    // IsInteractionCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Instant", "", true)]                                         // instant type
    [InlineData("Sorcery", "destroy target creature.", true)]                 // destroy target
    [InlineData("Sorcery", "exile target nonland permanent.", true)]          // exile target
    [InlineData("Instant", "counter target spell.", true)]                    // counter target
    [InlineData("Instant", "return target spell to its owner's hand.", true)] // return target spell
    [InlineData("Creature", "fight target creature you don't control.", true)]// fight target
    public void IsInteractionCard_TrueCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsInteractionCard(typeLine, oracleText));
    }

    [Theory]
    [InlineData("Artifact", "add one mana of any color.", false)]  // ramp rock, not interaction
    [InlineData("Creature — Elf", "Tap: add G.", false)]           // mana dork, not interaction
    public void IsInteractionCard_FalseCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsInteractionCard(typeLine, oracleText));
    }

    // -----------------------------------------------------------------------
    // IsBoardWipeCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("destroy all creatures.", true)]
    [InlineData("destroy all artifacts and enchantments.", true)]   // "destroy all artifacts"
    [InlineData("destroy all enchantments.", true)]
    [InlineData("each creature gets -1/-1 until end of turn.", true)]  // each creature + gets -
    [InlineData("exile all nonland permanents.", true)]
    public void IsBoardWipeCard_TrueCases(string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsBoardWipeCard(oracleText));
    }

    [Theory]
    [InlineData("destroy target creature.", false)]    // single-target, not a wipe
    [InlineData("exile target artifact.", false)]      // single-target
    public void IsBoardWipeCard_FalseCases(string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsBoardWipeCard(oracleText));
    }

    // -----------------------------------------------------------------------
    // IsRecursionCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("return target card from your graveyard to your hand.", true)]
    [InlineData("return all land cards from your graveyard to the battlefield.", true)]
    [InlineData("return target permanent card from your graveyard to the battlefield.", true)]
    [InlineData("reanimate target creature card.", true)]
    [InlineData("put that card from your graveyard to your hand.", true)]
    public void IsRecursionCard_TrueCases(string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsRecursionCard(oracleText));
    }

    [Theory]
    [InlineData("draw a card.", false)]
    [InlineData("destroy target creature.", false)]
    public void IsRecursionCard_FalseCases(string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsRecursionCard(oracleText));
    }

    // -----------------------------------------------------------------------
    // IsClosingPowerCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("", "each opponent loses 1 life.", true)]
    [InlineData("", "you win the game.", true)]
    [InlineData("", "take an extra turn.", true)]
    [InlineData("", "this creature has double strike.", true)]
    [InlineData("Creature — Beast — Craterhoof Behemoth", "", true)]         // Craterhoof in type line
    [InlineData("", "whenever this creature deals combat damage to a player, draw a card.", true)]
    [InlineData("Creature", "whenever this creature attacks, it gets +X/+X.", true)]
    public void IsClosingPowerCard_TrueCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsClosingPowerCard(typeLine, oracleText));
    }

    [Theory]
    [InlineData("Creature — Elf", "Tap: add G.", false)]
    [InlineData("Sorcery", "search your library for a basic land card.", false)]
    public void IsClosingPowerCard_FalseCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsClosingPowerCard(typeLine, oracleText));
    }

    // -----------------------------------------------------------------------
    // IsTutorCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Search your library for a card, then shuffle.", true)]            // generic tutor
    [InlineData("Search your library for a creature card, reveal it.", true)]      // typed non-land tutor
    [InlineData("Search your library for a nonland card, exile it, then shuffle.", true)]  // nonland tutor must not match the "land card" land-fetch exclusion
    public void IsTutorCard_TrueCases(string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsTutorCard(oracleText));
    }

    [Theory]
    [InlineData("Search your library for a basic land card and put it onto the battlefield.", false)]  // Rampant Growth
    [InlineData("Search your library for up to two basic land cards, then shuffle.", false)]           // Cultivate
    [InlineData("Search your library for a Mountain, then put that land onto the battlefield.", false)]  // land onto battlefield
    [InlineData("Draw a card.", false)]                                                                  // not a tutor
    public void IsTutorCard_FalseCases(string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsTutorCard(oracleText));
    }

    // -----------------------------------------------------------------------
    // IsFastManaCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Artifact", "{T}: Add {C}{C}.", "", true)]            // Mana Crypt: MV 0, artifact, produces mana
    [InlineData("Artifact", "Add {C}{C}{C}.", "", true)]              // Jeweled-Lotus-style: MV 0, "Add {"
    public void IsFastManaCard_TrueCases(string typeLine, string oracleText, string manaCost, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsFastManaCard(typeLine, oracleText, manaCost));
    }

    [Theory]
    [InlineData("Artifact", "{T}: Add {C}.", "{1}", false)]           // Sol Ring: MV 1, not fast mana
    [InlineData("Creature — Elf", "{T}: Add {G}.", "{G}", false)]     // mana dork, not artifact
    [InlineData("Artifact", "{T}: Tap target creature.", "", false)]  // MV 0 artifact but produces no mana
    public void IsFastManaCard_FalseCases(string typeLine, string oracleText, string manaCost, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsFastManaCard(typeLine, oracleText, manaCost));
    }

    // -----------------------------------------------------------------------
    // IsRampOrDrawUnderThreeMv
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Sorcery", "Draw two cards.", "{1}{U}", true)]                       // MV 2, draw
    [InlineData("Artifact", "{T}: Add one mana of any color.", "{2}", true)]         // MV 2, ramp
    public void IsRampOrDrawUnderThreeMv_TrueCases(string typeLine, string oracleText, string manaCost, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsRampOrDrawUnderThreeMv(typeLine, oracleText, manaCost));
    }

    [Theory]
    [InlineData("Sorcery", "Draw four cards.", "{4}{U}", false)]                     // MV 5, over threshold
    [InlineData("Instant", "Counter target spell.", "{U}", false)]                   // MV 1 but neither ramp nor draw
    public void IsRampOrDrawUnderThreeMv_FalseCases(string typeLine, string oracleText, string manaCost, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsRampOrDrawUnderThreeMv(typeLine, oracleText, manaCost));
    }

    // -----------------------------------------------------------------------
    // IsCounterspellCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Counter target spell.", true)]
    [InlineData("Counter target spell unless its controller pays {3}.", true)]
    public void IsCounterspellCard_TrueCases(string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsCounterspellCard(oracleText));
    }

    [Theory]
    [InlineData("Counter target activated or triggered ability.", false)]  // ability counter, not spell
    [InlineData("Destroy target creature.", false)]                        // not a counter
    public void IsCounterspellCard_FalseCases(string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsCounterspellCard(oracleText));
    }

    // -----------------------------------------------------------------------
    // IsTargetedRemovalCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Instant", "Destroy target creature.", true)]
    [InlineData("Sorcery", "Exile target creature or planeswalker.", true)]
    [InlineData("Instant", "Target creature gets -3/-3 until end of turn.", true)]
    public void IsTargetedRemovalCard_TrueCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsTargetedRemovalCard(typeLine, oracleText));
    }

    [Theory]
    [InlineData("Sorcery", "Destroy all creatures.", false)]
    [InlineData("Instant", "Return target creature you control to its owner's hand.", false)]
    public void IsTargetedRemovalCard_FalseCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsTargetedRemovalCard(typeLine, oracleText));
    }

    // -----------------------------------------------------------------------
    // IsSelfTargetedInteraction
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Instant", "Target creature you control gains hexproof until end of turn.", true)]
    public void IsSelfTargetedInteraction_TrueCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsSelfTargetedInteraction(typeLine, oracleText));
    }

    [Theory]
    [InlineData("Instant", "Destroy target creature.", false)]
    public void IsSelfTargetedInteraction_FalseCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsSelfTargetedInteraction(typeLine, oracleText));
    }

    // -----------------------------------------------------------------------
    // IsPseudoRemovalCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Instant", "Return target creature to its owner's hand.", true)]
    [InlineData("Sorcery", "Put target creature into its owner's library third from the top.", true)]
    [InlineData("Instant", "Exile target creature. Return it to the battlefield under its owner's control at the beginning of the next end step.", true)]
    public void IsPseudoRemovalCard_TrueCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsPseudoRemovalCard(typeLine, oracleText));
    }

    [Theory]
    [InlineData("Instant", "Destroy target creature.", false)]
    [InlineData("Instant", "Return target creature you control to its owner's hand.", false)]
    public void IsPseudoRemovalCard_FalseCases(string typeLine, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsPseudoRemovalCard(typeLine, oracleText));
    }

    // -----------------------------------------------------------------------
    // IsProtectionCard
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Heroic Intervention", "Permanents you control gain indestructible until end of turn.", true)]
    [InlineData("Teferi's Protection", "Your life total can't change. You gain protection from everything. All permanents you control phase out.", true)]
    // Mother of Runes: singular "gains protection from" — the ROADMAP-named D-06 defect. This is
    // the plan's real Scryfall oracle text, quoted verbatim from 09.1-RESEARCH.md.
    [InlineData("Mother of Runes", "{T}: Target creature you control gains protection from the color of your choice until end of turn.", true)]
    public void IsProtectionCard_TrueCases(string name, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsProtectionCard(name, oracleText));
    }

    [Theory]
    [InlineData("Lightning Bolt", "Deal 3 damage to any target.", false)]
    public void IsProtectionCard_FalseCases(string name, string oracleText, bool expected)
    {
        Assert.Equal(expected, DeckStatClassifier.IsProtectionCard(name, oracleText));
    }

    // Why: a needle that is listed in ProtectionOracleNeedles but unreachable through
    // IsProtectionCard is a defect — this asserts every row of the table actually classifies as
    // protection when supplied on its own as the whole oracle text.
    [Fact]
    public void IsProtectionCard_EveryProtectionOracleNeedle_ClassifiesAsProtection()
    {
        foreach (ProtectionNeedle needle in DeckStatClassifier.ProtectionOracleNeedles)
        {
            Assert.True(
                DeckStatClassifier.IsProtectionCard("Needle Test Card", needle.Text),
                $"Needle '{needle.Text}' (effect={needle.Effect}, subjectForm={needle.SubjectForm}) did not classify as protection.");
        }
    }

    // -----------------------------------------------------------------------
    // ParseManaToken
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("3", 3)]
    [InlineData("10", 10)]
    public void ParseManaToken_NumericReturnsValue(string token, int expected)
    {
        Assert.Equal(expected, DeckStatClassifier.ParseManaToken(token));
    }

    [Theory]
    [InlineData("X", 0)]
    [InlineData("x", 0)]
    public void ParseManaToken_X_ReturnsZero(string token, int expected)
    {
        Assert.Equal(expected, DeckStatClassifier.ParseManaToken(token));
    }

    [Theory]
    [InlineData("W/U", 1)]
    [InlineData("2/W", 1)]
    [InlineData("B/G", 1)]
    public void ParseManaToken_Hybrid_ReturnsOne(string token, int expected)
    {
        Assert.Equal(expected, DeckStatClassifier.ParseManaToken(token));
    }

    [Theory]
    [InlineData("W", 1)]
    [InlineData("U", 1)]
    [InlineData("B", 1)]
    [InlineData("R", 1)]
    [InlineData("G", 1)]
    [InlineData("C", 1)]
    public void ParseManaToken_ColoredSymbol_ReturnsOne(string token, int expected)
    {
        Assert.Equal(expected, DeckStatClassifier.ParseManaToken(token));
    }
}
