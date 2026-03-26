using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Normalization;
using DeckSyncWorkbench.Core.Reporting;

namespace DeckSyncWorkbench.Core.Knowledge;

public sealed class CategoryKnowledgeRepository
{
    private static readonly Func<string, SqliteConnection> ConnectionFactory = path => new SqliteConnection($"Data Source={path}");
    private static readonly TimeSpan DeckRefreshCooldown = TimeSpan.FromDays(1);
    private readonly string _databasePath;
    private readonly string _directoryPath;

    /// <summary>
    /// Initializes the repository for the provided SQLite database path.
    /// </summary>
    public CategoryKnowledgeRepository(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _directoryPath = Path.GetDirectoryName(_databasePath) ?? Directory.GetCurrentDirectory();
    }

    public string DatabasePath => _databasePath;

    /// <summary>
    /// Ensures the database schema and required tables exist.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directoryPath);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        await CreateCardCategoryObservationsTableAsync(connection, cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS deck_queue (
                deck_id TEXT PRIMARY KEY,
                inserted_utc TEXT NOT NULL,
                processed INTEGER NOT NULL DEFAULT 0,
                skipped INTEGER NOT NULL DEFAULT 0,
                last_checked_utc TEXT
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        var crawlStateCommand = connection.CreateCommand();
        crawlStateCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS crawl_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        await crawlStateCommand.ExecuteNonQueryAsync(cancellationToken);

        await EnsureDeckQueueColumnsAsync(connection, cancellationToken);
        await EnsureCategoryObservationSchemaAsync(connection, cancellationToken);
        await CreateCardDeckTotalsTableAsync(connection, cancellationToken);
    }

    /// <summary>
    /// Verifies the deck queue table includes the latest needed columns.
    /// </summary>
    /// <param name="connection">Open SQLite connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task EnsureDeckQueueColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(deck_queue);";
        var hasSkipped = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(1).Equals("skipped", StringComparison.Ordinal))
            {
                hasSkipped = true;
                break;
            }
        }

        if (!hasSkipped)
        {
            var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE deck_queue ADD COLUMN skipped INTEGER NOT NULL DEFAULT 0;";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureCategoryObservationSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(card_category_observations);";
        var hasBoard = false;
        var hasDeckCount = false;
        var columnCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columnCount++;
            if (reader.GetString(1).Equals("board", StringComparison.OrdinalIgnoreCase))
            {
                hasBoard = true;
            }
            if (reader.GetString(1).Equals("deck_count", StringComparison.OrdinalIgnoreCase))
            {
                hasDeckCount = true;
            }
        }

        if (columnCount == 0)
        {
            return;
        }

        if (!hasBoard || !hasDeckCount)
        {
            await MigrateCategoryObservationsTableAsync(connection, cancellationToken);
        }
    }

    private static async Task CreateCardCategoryObservationsTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS card_category_observations (
                source TEXT NOT NULL,
                card_name TEXT NOT NULL,
                normalized_card_name TEXT NOT NULL,
                category TEXT NOT NULL,
                board TEXT NOT NULL DEFAULT 'mainboard',
                deck_count INTEGER NOT NULL DEFAULT 0,
                count INTEGER NOT NULL,
                last_seen_utc TEXT NOT NULL,
                PRIMARY KEY (source, normalized_card_name, category, board)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CreateCardDeckTotalsTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS card_deck_totals (
                source TEXT NOT NULL,
                card_name TEXT NOT NULL,
                normalized_card_name TEXT NOT NULL,
                board TEXT NOT NULL DEFAULT 'mainboard',
                deck_count INTEGER NOT NULL DEFAULT 0,
                last_seen_utc TEXT NOT NULL,
                PRIMARY KEY (source, normalized_card_name, board)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateCategoryObservationsTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var renameCommand = connection.CreateCommand();
        renameCommand.CommandText = "ALTER TABLE card_category_observations RENAME TO card_category_observations_old;";
        await renameCommand.ExecuteNonQueryAsync(cancellationToken);

        await CreateCardCategoryObservationsTableAsync(connection, cancellationToken);

        var copyCommand = connection.CreateCommand();
        copyCommand.CommandText = """
            INSERT INTO card_category_observations (source, card_name, normalized_card_name, category, board, deck_count, count, last_seen_utc)
            SELECT source, card_name, normalized_card_name, category, 'mainboard', 0, count, last_seen_utc
            FROM card_category_observations_old;
            """;
        await copyCommand.ExecuteNonQueryAsync(cancellationToken);

        var dropCommand = connection.CreateCommand();
        dropCommand.CommandText = "DROP TABLE card_category_observations_old;";
        await dropCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Retrieves previously observed categories for the specified card.
    /// </summary>
    /// <param name="cardName">Card name to look up.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task<IReadOnlyList<string>> GetCategoriesAsync(string cardName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT category
            FROM card_category_observations
            WHERE normalized_card_name = $normalized
            GROUP BY category
            ORDER BY category COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$normalized", CardNormalizer.Normalize(cardName));

        var categories = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            categories.Add(reader.GetString(0));
        }

        return categories;
    }

    /// <summary>
    /// Retrieves detail rows for a card, including display name and count.
    /// </summary>
    /// <param name="cardName">Card name to query.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task<IReadOnlyList<CategoryKnowledgeRow>> GetCategoryRowsForCardAsync(string cardName, string? boardFilter = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        var queryTemplate = """
            SELECT category, card_name, SUM(count) AS total, SUM(deck_count) AS deck_total
            FROM card_category_observations
            WHERE normalized_card_name = $normalized
            {0}
            GROUP BY category, card_name
            ORDER BY total DESC, category COLLATE NOCASE;
            """;
        var filterClause = boardFilter is null
            ? string.Empty
            : "AND board = $board";
        command.CommandText = string.Format(queryTemplate, filterClause);
        command.Parameters.AddWithValue("$normalized", CardNormalizer.Normalize(cardName));
        if (boardFilter is not null)
        {
            command.Parameters.AddWithValue("$board", NormalizeBoard(boardFilter));
        }

        var rows = new List<CategoryKnowledgeRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var category = reader.GetString(0);
            var displayName = reader.GetString(1);
            var total = reader.GetInt32(2);
        var deckTotal = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
        rows.Add(new CategoryKnowledgeRow(category, displayName, total, deckTotal));
        }

        return rows;
    }

    /// <summary>
    /// Replaces all observations for a source with the provided rows.
    /// </summary>
    /// <param name="source">Source label for the data.</param>
    /// <param name="rows">Rows to persist.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task ReplaceSourceRowsAsync(string source, IReadOnlyList<CategoryKnowledgeRow> rows, string board = "mainboard", int deckCount = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (rows is null)
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = (SqliteTransaction)transaction;
        deleteCommand.CommandText = "DELETE FROM card_category_observations WHERE source = $source;";
        deleteCommand.Parameters.AddWithValue("$source", source);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

        foreach (var row in rows)
        {
            var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = (SqliteTransaction)transaction;
            insertCommand.CommandText = """
                INSERT INTO card_category_observations (source, card_name, normalized_card_name, category, board, deck_count, count, last_seen_utc)
                VALUES ($source, $cardName, $normalizedCardName, $category, $board, $deckCount, $count, $lastSeenUtc)
                """;
            insertCommand.Parameters.AddWithValue("$source", source);
            insertCommand.Parameters.AddWithValue("$cardName", row.CardName);
            insertCommand.Parameters.AddWithValue("$normalizedCardName", CardNormalizer.Normalize(row.CardName));
            insertCommand.Parameters.AddWithValue("$category", row.Category);
            insertCommand.Parameters.AddWithValue("$board", NormalizeBoard(board));
            var deckCountValue = row.DeckCount > 0 ? row.DeckCount : deckCount;
            insertCommand.Parameters.AddWithValue("$deckCount", deckCountValue);
            insertCommand.Parameters.AddWithValue("$count", row.Count);
            insertCommand.Parameters.AddWithValue("$lastSeenUtc", DateTimeOffset.UtcNow.ToString("O"));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Removes all cached observation and deck total rows for the provided source.
    /// </summary>
    /// <param name="source">Source label to remove.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task DeleteSourceDataAsync(string source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var deleteObservationsCommand = connection.CreateCommand();
        deleteObservationsCommand.Transaction = (SqliteTransaction)transaction;
        deleteObservationsCommand.CommandText = "DELETE FROM card_category_observations WHERE source = $source;";
        deleteObservationsCommand.Parameters.AddWithValue("$source", source);
        await deleteObservationsCommand.ExecuteNonQueryAsync(cancellationToken);

        var deleteTotalsCommand = connection.CreateCommand();
        deleteTotalsCommand.Transaction = (SqliteTransaction)transaction;
        deleteTotalsCommand.CommandText = "DELETE FROM card_deck_totals WHERE source = $source;";
        deleteTotalsCommand.Parameters.AddWithValue("$source", source);
        await deleteTotalsCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Persists observed categories for a specific card occurrence.
    /// </summary>
    /// <param name="source">Data source label.</param>
    /// <param name="cardName">Card name.</param>
    /// <param name="categories">Categories to record.</param>
    /// <param name="quantity">Quantity observed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PersistObservedCategoriesAsync(string source, string cardName, IReadOnlyList<string> categories, int quantity = 1, string board = "mainboard", int deckCountIncrement = 0, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(cardName) || categories.Count == 0 || quantity <= 0)
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var category in categories)
        {
            if (!CategoryFilter.IsIncluded(category))
            {
                continue;
            }

            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO card_category_observations (source, card_name, normalized_card_name, category, board, deck_count, count, last_seen_utc)
                VALUES ($source, $cardName, $normalizedCardName, $category, $board, $deckCount, $quantity, $lastSeenUtc)
                ON CONFLICT(source, normalized_card_name, category, board)
                DO UPDATE SET
                    count = count + excluded.count,
                    deck_count = deck_count + excluded.deck_count,
                    card_name = excluded.card_name,
                    last_seen_utc = excluded.last_seen_utc
                """;
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$cardName", cardName);
            command.Parameters.AddWithValue("$normalizedCardName", CardNormalizer.Normalize(cardName));
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$board", NormalizeBoard(board));
            command.Parameters.AddWithValue("$deckCount", deckCountIncrement);
            command.Parameters.AddWithValue("$quantity", quantity);
            command.Parameters.AddWithValue("$lastSeenUtc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Persists the number of decks that contain the given card on the specified board.
    /// </summary>
    public async Task PersistCardDeckTotalsAsync(string source, string cardName, string board = "mainboard", int deckCountIncrement = 1, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(cardName) || deckCountIncrement <= 0)
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO card_deck_totals (source, card_name, normalized_card_name, board, deck_count, last_seen_utc)
            VALUES ($source, $cardName, $normalizedCardName, $board, $deckCount, $lastSeenUtc)
            ON CONFLICT(source, normalized_card_name, board)
            DO UPDATE SET
                deck_count = deck_count + excluded.deck_count,
                card_name = excluded.card_name,
                last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$cardName", cardName);
        command.Parameters.AddWithValue("$normalizedCardName", CardNormalizer.Normalize(cardName));
        command.Parameters.AddWithValue("$board", NormalizeBoard(board));
        command.Parameters.AddWithValue("$deckCount", deckCountIncrement);
        command.Parameters.AddWithValue("$lastSeenUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Retrieves deck totals for the card, optionally filtered by board.
    /// </summary>
    public async Task<CardDeckTotals> GetCardDeckTotalsAsync(string cardName, string? boardFilter = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        var filterClause = boardFilter is null ? string.Empty : "AND board = $board";
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT board, SUM(deck_count) AS total
            FROM card_deck_totals
            WHERE normalized_card_name = $normalized
            {filterClause}
            GROUP BY board;
            """;
        command.Parameters.AddWithValue("$normalized", CardNormalizer.Normalize(cardName));
        if (boardFilter is not null)
        {
            command.Parameters.AddWithValue("$board", NormalizeBoard(boardFilter));
        }

        var boardCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var board = reader.GetString(0);
            var total = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            boardCounts[board] = total;
        }

        var totalDecks = boardCounts.Values.Sum();
        return new CardDeckTotals(totalDecks, boardCounts);
    }

    /// <summary>
    /// Checks whether the repository already contains entries for the source.
    /// </summary>
    /// <param name="source">Source label to check.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task<bool> HasSourceDataAsync(string source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM card_category_observations WHERE source = $source);";
        command.Parameters.AddWithValue("$source", source);
        var result = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        return result == 1L;
    }

    /// <summary>
    /// Inserts new deck IDs into the queue for processing.
    /// </summary>
    /// <param name="deckIds">Deck IDs to enqueue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AddDeckIdsAsync(IEnumerable<string> deckIds, CancellationToken cancellationToken = default)
    {
        var unique = deckIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal);
        var insertedUtc = DateTimeOffset.UtcNow;
        var requeueBeforeUtc = insertedUtc.Subtract(DeckRefreshCooldown);

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var deckId in unique)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO deck_queue (deck_id, inserted_utc, processed, skipped, last_checked_utc)
                VALUES ($deckId, $insertedUtc, 0, 0, NULL)
                ON CONFLICT(deck_id)
                DO UPDATE SET
                    inserted_utc = excluded.inserted_utc,
                    processed = CASE
                        WHEN deck_queue.processed = 0 AND deck_queue.skipped = 0 THEN 0
                        WHEN deck_queue.last_checked_utc IS NULL OR deck_queue.last_checked_utc <= $requeueBeforeUtc THEN 0
                        ELSE deck_queue.processed
                    END,
                    skipped = CASE
                        WHEN deck_queue.processed = 0 AND deck_queue.skipped = 0 THEN 0
                        WHEN deck_queue.last_checked_utc IS NULL OR deck_queue.last_checked_utc <= $requeueBeforeUtc THEN 0
                        ELSE deck_queue.skipped
                    END;
                """;
            command.Parameters.AddWithValue("$deckId", deckId);
            command.Parameters.AddWithValue("$insertedUtc", insertedUtc.ToString("O"));
            command.Parameters.AddWithValue("$requeueBeforeUtc", requeueBeforeUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the next batch of deck IDs that have not been processed or skipped.
    /// </summary>
    /// <param name="count">Maximum number of deck IDs to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<string>> GetNextUnprocessedDeckIdsAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            return Array.Empty<string>();
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
            command.CommandText = """
                SELECT deck_id
                FROM deck_queue
                WHERE processed = 0 AND skipped = 0
                ORDER BY inserted_utc
                LIMIT $count;
                """;
        command.Parameters.AddWithValue("$count", count);

        var deckIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            deckIds.Add(reader.GetString(0));
        }

        return deckIds;
    }

    /// <summary>
    /// Retrieves the total number of unprocessed deck IDs.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> GetUnprocessedCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM deck_queue WHERE processed = 0 AND skipped = 0;";
        var result = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        return (int)result;
    }

    /// <summary>
    /// Counts the number of decks that have been processed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> GetProcessedDeckCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM deck_queue WHERE processed = 1;";
        var result = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        return (int)result;
    }

    /// <summary>
    /// Gets the next recent Archidekt search page to crawl after page one.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> GetRecentDeckCrawlPageAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM crawl_state WHERE key = 'archidekt_recent_page';";
        var result = await command.ExecuteScalarAsync(cancellationToken) as string;

        if (int.TryParse(result, out var page) && page >= 2)
        {
            return page;
        }

        return 2;
    }

    /// <summary>
    /// Persists the next recent Archidekt search page to crawl.
    /// </summary>
    /// <param name="page">Page number to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetRecentDeckCrawlPageAsync(int page, CancellationToken cancellationToken = default)
    {
        var normalizedPage = Math.Max(2, page);
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO crawl_state (key, value)
            VALUES ('archidekt_recent_page', $page)
            ON CONFLICT(key)
            DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$page", normalizedPage.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Marks the provided deck IDs as processed, optionally skipping them.
    /// </summary>
    /// <param name="deckIds">Deck IDs to update.</param>
    /// <param name="skip">Whether the decks should be skipped after failure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task MarkDecksProcessedAsync(IEnumerable<string> deckIds, bool skip = false, CancellationToken cancellationToken = default)
    {
        var unique = deckIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unique.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            foreach (var deckId in unique)
            {
                var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    UPDATE deck_queue
                    SET processed = 1,
                        skipped = $skipped,
                        last_checked_utc = $now
                    WHERE deck_id = $deckId;
                    """;
                command.Parameters.AddWithValue("$deckId", deckId);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$skipped", skip ? 1 : 0);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

        await transaction.CommitAsync(cancellationToken);
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
