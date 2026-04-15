using System.Diagnostics;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Knowledge;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Normalization;

namespace MtgDeckStudio.Core.Tests;

public sealed class ArchidektDeckCacheSessionTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _tempDirectory;

    public ArchidektDeckCacheSessionTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "MtgDeckStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _databasePath = Path.Combine(_tempDirectory, "category-knowledge.db");
    }

    [Fact]
    public async Task RunAsync_WaitsForFullDurationWhenQueueRunsDry()
    {
        var repository = new CategoryKnowledgeRepository(_databasePath);
        await repository.EnsureSchemaAsync();

        var session = new ArchidektDeckCacheSession(
            repository,
            new FakeDeckImporter(),
            new FakeRecentDecksImporter(),
            idlePollDelay: TimeSpan.FromMilliseconds(20));

        var stopwatch = Stopwatch.StartNew();
        await session.RunAsync(TimeSpan.FromMilliseconds(70), cancellationToken: CancellationToken.None);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds >= 60, $"Expected the session to stay alive near the requested duration, but it completed in {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task RunAsync_WaitsForFullDurationWhenRecentDeckFetchFails()
    {
        var repository = new CategoryKnowledgeRepository(_databasePath);
        await repository.EnsureSchemaAsync();

        var session = new ArchidektDeckCacheSession(
            repository,
            new FakeDeckImporter(),
            new FailingRecentDecksImporter(),
            idlePollDelay: TimeSpan.FromMilliseconds(20));

        var stopwatch = Stopwatch.StartNew();
        await session.RunAsync(TimeSpan.FromMilliseconds(70), cancellationToken: CancellationToken.None);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds >= 60, $"Expected the session to keep retrying near the requested duration after recent-deck fetch errors, but it completed in {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task RunAsync_UsesFetchBatchSizeForDeckProcessing()
    {
        var repository = new CategoryKnowledgeRepository(_databasePath);
        await repository.EnsureSchemaAsync();
        await repository.AddDeckIdsAsync(new[] { "100", "101", "102" });

        var importer = new FakeDeckImporter();
        var session = new ArchidektDeckCacheSession(
            repository,
            importer,
            new FakeRecentDecksImporter(),
            idlePollDelay: TimeSpan.FromMilliseconds(5));

        var result = await session.RunAsync(
            TimeSpan.FromMilliseconds(300),
            queueBatchSize: 1,
            fetchBatchSize: 3,
            cancellationToken: CancellationToken.None);

        Assert.Equal(3, result.DecksProcessed);
        Assert.Equal(3, importer.ImportCalls);
    }

    private sealed class FakeDeckImporter : IArchidektDeckImporter
    {
        public int ImportCalls { get; private set; }

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
        {
            ImportCalls++;
            return Task.FromResult(new List<DeckEntry>
            {
                new()
                {
                    Name = $"Card {urlOrDeckId}",
                    NormalizedName = CardNormalizer.Normalize($"Card {urlOrDeckId}"),
                    Quantity = 1,
                    Board = "mainboard",
                    Category = "Ramp"
                }
            });
        }
    }

    private sealed class FakeRecentDecksImporter : IArchidektRecentDecksImporter
    {
        public Task<IReadOnlyList<string>> ImportRecentDeckIdsAsync(int count, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<string>> ImportRecentDeckIdsAsync(int count, int startPage, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<string>> ImportRecentDeckIdsPageAsync(int page, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class FailingRecentDecksImporter : IArchidektRecentDecksImporter
    {
        public Task<IReadOnlyList<string>> ImportRecentDeckIdsAsync(int count, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("Simulated Archidekt recent deck failure.");

        public Task<IReadOnlyList<string>> ImportRecentDeckIdsAsync(int count, int startPage, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("Simulated Archidekt recent deck failure.");

        public Task<IReadOnlyList<string>> ImportRecentDeckIdsPageAsync(int page, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("Simulated Archidekt recent deck failure.");
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
            // ignored
        }
    }
}
