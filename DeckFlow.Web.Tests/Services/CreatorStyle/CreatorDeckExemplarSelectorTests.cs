using DeckFlow.Core.Content;
using DeckFlow.Core.Models;
using DeckFlow.Web.Services.CreatorStyle;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class CreatorDeckExemplarSelectorTests
{
    [Fact]
    public void SelectExemplars_FiveDecks_ReturnsDefaultMaximumOfThreeWholeEntries()
    {
        CreatorDeckCacheEntry[] creatorDecks =
        [
            Deck("deck-c", "near-precon", 100),
            Deck("deck-a", "unknown-marker", 99),
            Deck("deck-e", "near-precon", 101),
            Deck("deck-b", "ok", 100),
            Deck("deck-d", "ok", 98),
        ];

        IReadOnlyList<CreatorDeckCacheEntry> result = CreatorDeckExemplarSelector.SelectExemplars(creatorDecks, submittedDeckSize: 100);

        Assert.Equal(3, result.Count);
        Assert.Equal(["deck-b", "deck-d", "deck-c"], result.Select(static deck => deck.DeckId).ToArray());
    }

    [Fact]
    public void SelectExemplars_EquivalentInputPermutations_ReturnSameDeterministicOrdering()
    {
        CreatorDeckCacheEntry[] firstOrdering =
        [
            Deck("deck-3", "ok", 100),
            Deck("deck-2", "near-precon", 102),
            Deck("deck-1", "unknown-marker", 100),
            Deck("deck-4", "ok", 98),
        ];
        CreatorDeckCacheEntry[] secondOrdering =
        [
            Deck("deck-4", "ok", 98),
            Deck("deck-1", "unknown-marker", 100),
            Deck("deck-2", "near-precon", 102),
            Deck("deck-3", "ok", 100),
        ];

        IReadOnlyList<CreatorDeckCacheEntry> first = CreatorDeckExemplarSelector.SelectExemplars(firstOrdering, submittedDeckSize: 100);
        IReadOnlyList<CreatorDeckCacheEntry> second = CreatorDeckExemplarSelector.SelectExemplars(secondOrdering, submittedDeckSize: 100);

        string[] expectedOrder = ["deck-3", "deck-4", "deck-2"];
        Assert.Equal(expectedOrder, first.Select(static deck => deck.DeckId).ToArray());
        Assert.Equal(expectedOrder, second.Select(static deck => deck.DeckId).ToArray());
    }

    [Fact]
    public void SelectExemplars_FewerThanMaximum_ReturnsAllAvailableDecks()
    {
        CreatorDeckCacheEntry[] creatorDecks =
        [
            Deck("deck-2", "near-precon", 101),
            Deck("deck-1", "ok", 100),
        ];

        IReadOnlyList<CreatorDeckCacheEntry> result = CreatorDeckExemplarSelector.SelectExemplars(creatorDecks, submittedDeckSize: 100);

        Assert.Equal(2, result.Count);
        Assert.Equal(["deck-1", "deck-2"], result.Select(static deck => deck.DeckId).ToArray());
    }

    [Fact]
    public void SelectExemplars_EmptyCorpus_ReturnsEmptyList()
    {
        IReadOnlyList<CreatorDeckCacheEntry> result = CreatorDeckExemplarSelector.SelectExemplars([], submittedDeckSize: 100);

        Assert.Empty(result);
    }

    [Fact]
    public void SelectExemplars_NullDeckList_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => CreatorDeckExemplarSelector.SelectExemplars(null!, submittedDeckSize: 100));
    }

    private static CreatorDeckCacheEntry Deck(string deckId, string confidenceMarker, int size)
        => new()
        {
            CreatorSlug = "snail",
            DeckId = deckId,
            ContentHash = $"{deckId}-hash",
            Size = size,
            ConfidenceMarker = confidenceMarker,
            Entries = Array.Empty<DeckEntry>(),
            CachedUtc = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero),
        };
}
