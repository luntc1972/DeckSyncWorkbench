using DeckFlow.Core.Analysis;

namespace DeckFlow.Core.Tests;

/// <summary>
/// Covers <see cref="InteractionAuditAggregator"/> bucket classification, confidence tiers, and gap advisories.
/// </summary>
public sealed class InteractionAuditAggregatorTests
{
    private static InteractionCardInput Card(int qty, string name, string type, string oracle, string mana)
        => new(qty, name, type, oracle, mana);

    [Fact]
    public void Compute_BucketsSixCardFixtureIntoConfidentBuckets()
    {
        var audit = InteractionAuditAggregator.Compute(new[]
        {
            Card(1, "Swords to Plowshares", "Instant", "Exile target creature.", "{W}"),
            Card(1, "Wrath of God", "Sorcery", "Destroy all creatures.", "{2}{W}{W}"),
            Card(1, "Counterspell", "Instant", "Counter target spell.", "{U}{U}"),
            Card(1, "Eternal Witness", "Creature", "Return target card from your graveyard to your hand.", "{1}{G}{G}"),
            Card(1, "Rule of Law", "Enchantment", "Each player can't cast more than one spell each turn.", "{2}{W}"),
            Card(1, "Heroic Intervention", "Instant", "Permanents you control gain hexproof and indestructible until end of turn.", "{1}{G}"),
        });

        Assert.Contains(audit.TargetedRemoval.Confident, card => card.Name == "Swords to Plowshares" && card.Quantity == 1);
        Assert.Contains(audit.BoardWipes.Confident, card => card.Name == "Wrath of God" && card.Quantity == 1);
        Assert.Contains(audit.Counterspells.Confident, card => card.Name == "Counterspell" && card.Quantity == 1);
        Assert.Contains(audit.ProtectionRecursion.Confident, card => card.Name == "Eternal Witness" && card.Quantity == 1);
        Assert.Contains(audit.ProtectionRecursion.Confident, card => card.Name == "Heroic Intervention" && card.Quantity == 1);
        Assert.Contains(audit.StaxTaxation.Confident, card => card.Name == "Rule of Law" && card.Quantity == 1);
    }

    [Fact]
    public void Compute_SelfTargetCardLandsInTargetedRemovalReview()
    {
        var audit = InteractionAuditAggregator.Compute(new[]
        {
            Card(1, "Self Bounce", "Instant", "Return target creature you control to its owner's hand.", "{U}"),
        });

        Assert.Contains(audit.TargetedRemoval.Review, card => card.Name == "Self Bounce");
        Assert.DoesNotContain(audit.TargetedRemoval.Confident, card => card.Name == "Self Bounce");
    }

    [Fact]
    public void Compute_BounceCardLandsInTargetedRemovalReview()
    {
        var audit = InteractionAuditAggregator.Compute(new[]
        {
            Card(1, "Unsummon", "Instant", "Return target creature to its owner's hand.", "{U}"),
        });

        Assert.Contains(audit.TargetedRemoval.Review, card => card.Name == "Unsummon");
        Assert.DoesNotContain(audit.TargetedRemoval.Confident, card => card.Name == "Unsummon");
    }

    [Fact]
    public void Compute_TuckCardLandsInTargetedRemovalReview()
    {
        var audit = InteractionAuditAggregator.Compute(new[]
        {
            Card(1, "Tuck Away", "Sorcery", "Put target creature into its owner's library third from the top.", "{2}{U}"),
        });

        Assert.Contains(audit.TargetedRemoval.Review, card => card.Name == "Tuck Away");
        Assert.DoesNotContain(audit.TargetedRemoval.Confident, card => card.Name == "Tuck Away");
    }

    [Fact]
    public void Compute_TemporaryExileAndReturnCardLandsInTargetedRemovalReview()
    {
        var audit = InteractionAuditAggregator.Compute(new[]
        {
            Card(1, "Temporary Exile", "Instant", "Exile target creature. Return it to the battlefield under its owner's control at the beginning of the next end step.", "{1}{W}"),
        });

        Assert.Contains(audit.TargetedRemoval.Review, card => card.Name == "Temporary Exile");
        Assert.DoesNotContain(audit.TargetedRemoval.Confident, card => card.Name == "Temporary Exile");
    }

    [Fact]
    public void Compute_ModalMdfcLandFaceClassifiesFromOracleText()
    {
        var audit = InteractionAuditAggregator.Compute(new[]
        {
            Card(1, "Removal Land", "Land", "Destroy target creature.", ""),
        });

        Assert.Contains(audit.TargetedRemoval.Confident, card => card.Name == "Removal Land");
    }

    [Fact]
    public void Compute_PreservesQuantityPerCard()
    {
        var audit = InteractionAuditAggregator.Compute(new[]
        {
            Card(2, "Counterspell", "Instant", "Counter target spell.", "{U}{U}"),
        });

        Assert.Contains(audit.Counterspells.Confident, card => card.Name == "Counterspell" && card.Quantity == 2);
    }

    // Why: proves plan 09.1-01's Mother of Runes classification (singular "gains protection from")
    // reaches this second IsProtectionCard consumer, not just CutLabRoleAssigner.
    [Fact]
    public void Compute_MotherOfRunes_LandsInProtectionRecursionConfident()
    {
        var audit = InteractionAuditAggregator.Compute(new[]
        {
            Card(1, "Mother of Runes", "Creature — Human Cleric", "{T}: Target creature you control gains protection from the color of your choice until end of turn.", "{W}"),
        });

        Assert.Contains(audit.ProtectionRecursion.Confident, card => card.Name == "Mother of Runes" && card.Quantity == 1);
    }

    [Fact]
    public void Compute_EmptyInputReturnsAllCoverageGaps()
    {
        var audit = InteractionAuditAggregator.Compute(Array.Empty<InteractionCardInput>());

        Assert.Empty(audit.TargetedRemoval.Confident);
        Assert.Empty(audit.BoardWipes.Confident);
        Assert.Empty(audit.Counterspells.Confident);
        Assert.Empty(audit.ProtectionRecursion.Confident);
        Assert.Empty(audit.StaxTaxation.Confident);
        Assert.Contains("0 counterspells", audit.CoverageGaps);
        Assert.Contains("no board wipes", audit.CoverageGaps);
        Assert.Contains("no targeted removal", audit.CoverageGaps);
        Assert.Contains("no protection or recursion (possible graveyard-hate / protection gap)", audit.CoverageGaps);
        Assert.Contains("no stax or taxation", audit.CoverageGaps);
    }
}
