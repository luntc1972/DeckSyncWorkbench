using System.Collections.Generic;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Reporting;

namespace MtgDeckStudio.Core.Knowledge;

internal static class DeckCategoryCacheWriter
{
    /// <summary>
    /// Replaces the cached category rows for a single deck source.
    /// </summary>
    /// <param name="repository">Repository the categories should be persisted to.</param>
    /// <param name="source">Source label for the deck.</param>
    /// <param name="entries">Deck entries to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task ReplaceDeckEntriesAsync(CategoryKnowledgeRepository repository, string source, IEnumerable<DeckEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrEmpty(source);

        await repository.DeleteSourceDataAsync(source, cancellationToken);
        await PersistDeckEntriesAsync(repository, source, entries, cancellationToken);
    }

    /// <summary>
    /// Persists the categories found in a single deck to the repository.
    /// </summary>
    /// <param name="repository">Repository the categories should be persisted to.</param>
    /// <param name="source">Source label for the deck.</param>
    /// <param name="entries">Stack of deck entries to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task PersistDeckEntriesAsync(CategoryKnowledgeRepository repository, string source, IEnumerable<DeckEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrEmpty(source);
        if (entries is null)
        {
            return;
        }

        var counts = new Dictionary<(string CardName, string Category, string Board), (int Quantity, int DeckIncrement)>(BoardCategoryComparer.Instance);
        var cardBoardHits = new HashSet<(string CardName, string Board)>(CardBoardComparer.Instance);

        foreach (var entry in entries)
        {
            var board = NormalizeBoard(entry.Board);
            cardBoardHits.Add((entry.Name, board));
            foreach (var category in CategoryKnowledgeReporter.SplitCategories(entry.Category))
            {
                var key = (entry.Name, category, board);
                counts[key] = counts.TryGetValue(key, out var existing)
                    ? (existing.Quantity + entry.Quantity, existing.DeckIncrement)
                    : (entry.Quantity, 0);
            }
        }

        foreach (var group in counts)
        {
            await repository.PersistObservedCategoriesAsync(
                source,
                group.Key.CardName,
                new[] { group.Key.Category },
                group.Value.Quantity,
                group.Key.Board,
                deckCountIncrement: 1,
                cancellationToken);
        }

        foreach (var cardBoard in cardBoardHits)
        {
            await repository.PersistCardDeckTotalsAsync(source, cardBoard.CardName, cardBoard.Board, deckCountIncrement: 1, cancellationToken: cancellationToken);
        }
    }

    private static string NormalizeBoard(string? board)
    {
        if (string.IsNullOrWhiteSpace(board))
        {
            return "mainboard";
        }

        return board.Trim().ToLowerInvariant();
    }
}
