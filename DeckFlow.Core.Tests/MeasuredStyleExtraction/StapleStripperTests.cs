using DeckFlow.Core.Knowledge.MeasuredStyleExtraction;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;

namespace DeckFlow.Core.Tests;

/// <summary>
/// Unit tests for the pure staple stripping helper.
/// </summary>
public sealed class StapleStripperTests
{
    /// <summary>
    /// Verifies curated staples are always stripped from returned entries.
    /// </summary>
    [Fact]
    public void StripStaples_RemovesCuratedStaples()
    {
        var samples = new[]
        {
            Sample("deck-1", Entry("Sol Ring"), Entry("Rhystic Study")),
        };

        var stripped = StapleStripper.StripStaples(samples, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Single(stripped);
        Assert.Collection(
            stripped[0].Entries,
            entry => Assert.Equal("Rhystic Study", entry.Name));
    }

    /// <summary>
    /// Verifies a punctuated curated staple is stripped after production-equivalent normalization
    /// (CardNormalizer.Normalize strips the apostrophe, so the comparison must normalize both sides).
    /// </summary>
    [Fact]
    public void StripStaples_RemovesPunctuatedCuratedStapleAfterProductionNormalization()
    {
        var samples = new[]
        {
            Sample("deck-1", Entry("Rogue's Passage"), Entry("Rhystic Study")),
        };

        var stripped = StapleStripper.StripStaples(samples, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Single(stripped);
        Assert.Collection(
            stripped[0].Entries,
            entry => Assert.Equal("Rhystic Study", entry.Name));
    }

    /// <summary>
    /// Verifies cards present in more than sixty percent of decks are treated as personal staples.
    /// </summary>
    [Fact]
    public void ComputePersonalStaples_StripsCardsPresentInMoreThanSixtyPercentOfDecks()
    {
        var filteredSamples = new[]
        {
            Sample("deck-1", Entry("Mystic Remora"), Entry("Card A")),
            Sample("deck-2", Entry("Mystic Remora"), Entry("Card B")),
            Sample("deck-3", Entry("Mystic Remora"), Entry("Card C")),
            Sample("deck-4", Entry("Mystic Remora"), Entry("Card D")),
            Sample("deck-5", Entry("Card E")),
        };

        var personalStaples = StapleStripper.ComputePersonalStaples(filteredSamples);
        var stripped = StapleStripper.StripStaples(filteredSamples, personalStaples);

        Assert.Contains("Mystic Remora", personalStaples);
        Assert.All(stripped, sample => Assert.DoesNotContain(sample.Entries, entry => entry.Name == "Mystic Remora"));
    }

    /// <summary>
    /// Verifies the personal staple threshold is strictly greater than sixty percent.
    /// </summary>
    [Fact]
    public void ComputePersonalStaples_RetainsCardsAtExactlySixtyPercent()
    {
        var filteredSamples = new[]
        {
            Sample("deck-1", Entry("Brainstorm")),
            Sample("deck-2", Entry("Brainstorm")),
            Sample("deck-3", Entry("Brainstorm")),
            Sample("deck-4", Entry("Card D")),
            Sample("deck-5", Entry("Card E")),
        };

        var personalStaples = StapleStripper.ComputePersonalStaples(filteredSamples);
        var stripped = StapleStripper.StripStaples(filteredSamples, personalStaples);

        Assert.DoesNotContain("Brainstorm", personalStaples);
        Assert.Equal(3, stripped.Count(sample => sample.Entries.Any(entry => entry.Name == "Brainstorm")));
    }

    /// <summary>
    /// Verifies oversized decks are removed before downstream frequency calculations.
    /// </summary>
    [Fact]
    public void FilterOversized_RemovesDecksOverOneHundredFiveCards()
    {
        var samples = new[]
        {
            Sample("keep", 100, Entry("Card A")),
            Sample("drop", 106, Entry("Card B")),
        };

        var filtered = StapleStripper.FilterOversized(samples);

        Assert.Single(filtered);
        Assert.Equal("keep", filtered[0].DeckId);
    }

    /// <summary>
    /// Verifies later near-precon duplicates are re-marked when overlap exceeds the documented threshold.
    /// </summary>
    [Fact]
    public void FlagNearPrecons_RemarksLaterDecksAboveSeventyPercentOverlap()
    {
        var samples = new[]
        {
            Sample("deck-1", "trusted", Entry("Card 1"), Entry("Card 2"), Entry("Card 3"), Entry("Card 4"), Entry("Card 5"), Entry("Card 6"), Entry("Card 7"), Entry("Card 8")),
            Sample("deck-2", "trusted", Entry("Card 1"), Entry("Card 2"), Entry("Card 3"), Entry("Card 4"), Entry("Card 5"), Entry("Card 6"), Entry("Card 7"), Entry("Card 9")),
        };

        var flagged = StapleStripper.FlagNearPrecons(samples);

        Assert.Equal("trusted", flagged[0].ConfidenceMarker);
        Assert.Equal("near-precon", flagged[1].ConfidenceMarker);
    }

    private static CreatorDeckSample Sample(string deckId, params DeckEntry[] entries)
    {
        return Sample(deckId, "trusted", entries);
    }

    private static CreatorDeckSample Sample(string deckId, string confidenceMarker, params DeckEntry[] entries)
    {
        return Sample(deckId, entries.Sum(entry => entry.Quantity), confidenceMarker, entries);
    }

    private static CreatorDeckSample Sample(string deckId, int cardCount, params DeckEntry[] entries)
    {
        return Sample(deckId, cardCount, "trusted", entries);
    }

    private static CreatorDeckSample Sample(string deckId, int cardCount, string confidenceMarker, params DeckEntry[] entries)
    {
        return new CreatorDeckSample
        {
            DeckId = deckId,
            Entries = entries,
            CardCount = cardCount,
            ConfidenceMarker = confidenceMarker,
        };
    }

    private static DeckEntry Entry(string name, int quantity = 1)
    {
        return new DeckEntry
        {
            Name = name,
            // Why: production writers of NormalizedName use CardNormalizer.Normalize
            // (see ArchidektApiDeckImporter, MoxfieldApiDeckImporter, ArchidektParser,
            // MoxfieldParser) — the fixture must match or staple stripping bugs go undetected.
            NormalizedName = CardNormalizer.Normalize(name),
            Quantity = quantity,
            Board = "mainboard",
        };
    }
}
