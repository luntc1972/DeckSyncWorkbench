using Microsoft.Data.Sqlite;
using MtgDeckStudio.Core.Knowledge;

namespace MtgDeckStudio.Core.Tests;

public sealed class CategoryKnowledgeRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _tempDirectory;

    public CategoryKnowledgeRepositoryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "MtgDeckStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _databasePath = Path.Combine(_tempDirectory, "category-knowledge.db");
    }

    [Fact]
    public async Task AddDeckIdsAsync_DoesNotRequeueRecentlyProcessedDeck()
    {
        var repository = CreateRepository();

        await repository.AddDeckIdsAsync(new[] { "123" });
        await repository.MarkDecksProcessedAsync(new[] { "123" });
        await repository.AddDeckIdsAsync(new[] { "123" });

        var queuedIds = await repository.GetNextUnprocessedDeckIdsAsync(10);

        Assert.Empty(queuedIds);
    }

    [Fact]
    public async Task AddDeckIdsAsync_RequeuesDeckAfterCooldownExpires()
    {
        var repository = CreateRepository();

        await repository.AddDeckIdsAsync(new[] { "123" });
        await repository.MarkDecksProcessedAsync(new[] { "123" });
        await SetLastCheckedUtcAsync("123", DateTimeOffset.UtcNow.AddDays(-2));

        await repository.AddDeckIdsAsync(new[] { "123" });
        var queuedIds = await repository.GetNextUnprocessedDeckIdsAsync(10);

        Assert.Single(queuedIds);
        Assert.Equal("123", queuedIds[0]);
    }

    [Fact]
    public async Task GetRecentDeckCrawlPageAsync_DefaultsToSecondPage()
    {
        var repository = CreateRepository();

        var page = await repository.GetRecentDeckCrawlPageAsync();

        Assert.Equal(2, page);
    }

    [Fact]
    public async Task SetRecentDeckCrawlPageAsync_PersistsPage()
    {
        var repository = CreateRepository();

        await repository.SetRecentDeckCrawlPageAsync(7);

        var page = await repository.GetRecentDeckCrawlPageAsync();
        Assert.Equal(7, page);
    }

    [Fact]
    public async Task HasSourceDataAsync_ReturnsTrue_WhenSourceRowsExist()
    {
        var repository = CreateRepository();

        await repository.PersistObservedCategoriesAsync("archidekt_live:123", "Sol Ring", new[] { "Ramp" });

        var exists = await repository.HasSourceDataAsync("archidekt_live:123");

        Assert.True(exists);
    }

    [Fact]
    public async Task DeleteSourceDataAsync_RemovesExistingSourceRows()
    {
        var repository = CreateRepository();

        await repository.PersistObservedCategoriesAsync("archidekt_live:123", "Sol Ring", new[] { "Ramp" });
        await repository.DeleteSourceDataAsync("archidekt_live:123");

        var exists = await repository.HasSourceDataAsync("archidekt_live:123");

        Assert.False(exists);
    }

    private CategoryKnowledgeRepository CreateRepository() => new(_databasePath);

    private async Task SetLastCheckedUtcAsync(string deckId, DateTimeOffset timestamp)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE deck_queue
            SET last_checked_utc = $timestamp
            WHERE deck_id = $deckId;
            """;
        command.Parameters.AddWithValue("$deckId", deckId);
        command.Parameters.AddWithValue("$timestamp", timestamp.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
