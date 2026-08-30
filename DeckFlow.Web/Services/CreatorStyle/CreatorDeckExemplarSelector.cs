using DeckFlow.Core.Content;

namespace DeckFlow.Web.Services.CreatorStyle;

/// <summary>
/// Selects deterministic whole-deck exemplars from a creator's cached corpus.
/// </summary>
public static class CreatorDeckExemplarSelector
{
    /// <summary>
    /// Selects up to <paramref name="maxExemplars"/> whole creator decks as exemplars.
    /// </summary>
    /// <param name="creatorDecks">Creator decks available for exemplar selection.</param>
    /// <param name="submittedDeckSize">Submitted deck size used for size-proximity ranking.</param>
    /// <param name="maxExemplars">Maximum number of exemplar decks to return.</param>
    /// <returns>Deterministically ordered exemplar deck entries.</returns>
    internal static IReadOnlyList<CreatorDeckCacheEntry> SelectExemplars(
        IReadOnlyList<CreatorDeckCacheEntry> creatorDecks,
        int submittedDeckSize,
        int maxExemplars = 3)
    {
        ArgumentNullException.ThrowIfNull(creatorDecks);

        // Why: this selector returns whole exemplar decklists, not the flat card-name pool used by CreatorWhitelistPoolBuilder.
        return creatorDecks
            .OrderBy(deck => Rank(deck.ConfidenceMarker))
            .ThenBy(deck => Math.Abs(deck.Size - submittedDeckSize))
            .ThenBy(deck => deck.DeckId, StringComparer.Ordinal)
            .Take(maxExemplars)
            .ToArray();
    }

    private static int Rank(string marker)
    {
        return marker switch
        {
            "ok" => 0,
            "near-precon" => 1,
            _ => 2,
        };
    }
}
