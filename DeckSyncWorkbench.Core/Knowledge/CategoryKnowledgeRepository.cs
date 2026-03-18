using Microsoft.Data.Sqlite;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Normalization;
using DeckSyncWorkbench.Core.Reporting;

namespace DeckSyncWorkbench.Core.Knowledge;

public sealed class CategoryKnowledgeRepository
{
    private static readonly Func<string, SqliteConnection> ConnectionFactory = path => new SqliteConnection($"Data Source={path}");
    private readonly string _databasePath;
    private readonly string _directoryPath;

    public CategoryKnowledgeRepository(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _directoryPath = Path.GetDirectoryName(_databasePath) ?? Directory.GetCurrentDirectory();
    }

    public string DatabasePath => _databasePath;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directoryPath);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS card_category_observations (
                source TEXT NOT NULL,
                card_name TEXT NOT NULL,
                normalized_card_name TEXT NOT NULL,
                category TEXT NOT NULL,
                count INTEGER NOT NULL,
                last_seen_utc TEXT NOT NULL,
                PRIMARY KEY (source, normalized_card_name, category)
            );
            CREATE TABLE IF NOT EXISTS deck_queue (
                deck_id TEXT PRIMARY KEY,
                inserted_utc TEXT NOT NULL,
                processed INTEGER NOT NULL DEFAULT 0,
                skipped INTEGER NOT NULL DEFAULT 0,
                last_checked_utc TEXT
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureDeckQueueColumnsAsync(connection, cancellationToken);
    }

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

    public async Task ReplaceSourceRowsAsync(string source, IReadOnlyList<CategoryKnowledgeRow> rows, CancellationToken cancellationToken = default)
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
                INSERT INTO card_category_observations (source, card_name, normalized_card_name, category, count, last_seen_utc)
                VALUES ($source, $cardName, $normalizedCardName, $category, $count, $lastSeenUtc)
                """;
            insertCommand.Parameters.AddWithValue("$source", source);
            insertCommand.Parameters.AddWithValue("$cardName", row.CardName);
            insertCommand.Parameters.AddWithValue("$normalizedCardName", CardNormalizer.Normalize(row.CardName));
            insertCommand.Parameters.AddWithValue("$category", row.Category);
            insertCommand.Parameters.AddWithValue("$count", row.Count);
            insertCommand.Parameters.AddWithValue("$lastSeenUtc", DateTimeOffset.UtcNow.ToString("O"));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task PersistObservedCategoriesAsync(string source, string cardName, IReadOnlyList<string> categories, int quantity = 1, CancellationToken cancellationToken = default)
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
                INSERT INTO card_category_observations (source, card_name, normalized_card_name, category, count, last_seen_utc)
                VALUES ($source, $cardName, $normalizedCardName, $category, $quantity, $lastSeenUtc)
                ON CONFLICT(source, normalized_card_name, category)
                DO UPDATE SET
                    count = count + excluded.count,
                    card_name = excluded.card_name,
                    last_seen_utc = excluded.last_seen_utc
                """;
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$cardName", cardName);
            command.Parameters.AddWithValue("$normalizedCardName", CardNormalizer.Normalize(cardName));
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$quantity", quantity);
            command.Parameters.AddWithValue("$lastSeenUtc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

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

    public async Task AddDeckIdsAsync(IEnumerable<string> deckIds, CancellationToken cancellationToken = default)
    {
        var unique = deckIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal);

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = ConnectionFactory(_databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var deckId in unique)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO deck_queue (deck_id, inserted_utc, processed)
                VALUES ($deckId, $insertedUtc, 0);
                """;
            command.Parameters.AddWithValue("$deckId", deckId);
            command.Parameters.AddWithValue("$insertedUtc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

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
}
