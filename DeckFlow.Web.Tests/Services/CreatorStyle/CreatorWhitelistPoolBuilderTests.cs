using DeckFlow.Core.Content;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Models;
using DeckFlow.Core.Storage;
using DeckFlow.Web.Services.CreatorStyle;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeckFlow.Web.Tests.Services.CreatorStyle;

/// <summary>
/// Automated coverage for creator-whitelist pool assembly and DI composition.
/// </summary>
public sealed class CreatorWhitelistPoolBuilderTests
{
    [Fact]
    public async Task BuildAsync_FrequencyRanksByDistinctDeckCount()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new FakeCreatorDeckCacheStore(
            Deck("ranker", "deck-1", Mainboard("Arcane Signet", "Lightning Greaves")),
            Deck("ranker", "deck-2", Mainboard("Arcane Signet", "Swords to Plowshares")),
            Deck("ranker", "deck-3", Mainboard("Arcane Signet", "Boros Signet")));
        var guard = new FakeCardGroundingGuard();
        var sut = new CreatorWhitelistPoolBuilder(store, guard, cache);

        IReadOnlyList<string> whitelist = (await sut.BuildWithDiagnosticsAsync("ranker", EmptyDeckContext())).AcceptedNames;

        Assert.Equal(
            ["Arcane Signet", "Boros Signet", "Lightning Greaves", "Swords to Plowshares"],
            whitelist);
        Assert.Equal(
            ["Arcane Signet", "Boros Signet", "Lightning Greaves", "Swords to Plowshares"],
            Assert.Single(guard.ValidatedBatches));
    }

    [Fact]
    public async Task BuildAsync_CapsRawPoolBeforeGuardValidation()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var entries = Enumerable.Range(1, 30)
            .Select(index => $"Card {index:00}")
            .ToArray();
        var store = new FakeCreatorDeckCacheStore(Deck("capped", "deck-1", Mainboard(entries)));
        var guard = new FakeCardGroundingGuard();
        var sut = new CreatorWhitelistPoolBuilder(store, guard, cache);

        IReadOnlyList<string> whitelist = (await sut.BuildWithDiagnosticsAsync("capped", EmptyDeckContext())).AcceptedNames;

        IReadOnlyList<string> validated = Assert.Single(guard.ValidatedBatches);
        Assert.Equal(25, validated.Count);
        Assert.Equal(validated, whitelist);
    }

    [Fact]
    public async Task BuildAsync_RejectedCandidatesAreDroppedAndAcceptedCanonicalNamesReturned()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new FakeCreatorDeckCacheStore(
            Deck("guarded", "deck-1", Mainboard("Arcane Signet", "Hullbreacher", "Mystic Remora")));
        var guard = new FakeCardGroundingGuard(new Dictionary<string, CardGroundingVerdict>(StringComparer.Ordinal)
        {
            ["Arcane Signet"] = Accepted("Arcane Signet"),
            ["Hullbreacher"] = Rejected("Hullbreacher", CardGroundingRejectReason.NotLegal),
            ["Mystic Remora"] = Accepted("Mystic Remora Prime"),
        });
        var sut = new CreatorWhitelistPoolBuilder(store, guard, cache);

        IReadOnlyList<string> whitelist = (await sut.BuildWithDiagnosticsAsync("guarded", EmptyDeckContext())).AcceptedNames;

        Assert.Equal(["Arcane Signet", "Mystic Remora Prime"], whitelist);
        Assert.DoesNotContain("Hullbreacher", whitelist);
    }

    [Fact]
    public async Task BuildAsync_EmptyCorpusReturnsEmptyListWithoutCallingGuard()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new FakeCreatorDeckCacheStore();
        var guard = new FakeCardGroundingGuard();
        var sut = new CreatorWhitelistPoolBuilder(store, guard, cache);

        IReadOnlyList<string> whitelist = (await sut.BuildWithDiagnosticsAsync("empty", EmptyDeckContext())).AcceptedNames;

        Assert.Empty(whitelist);
        Assert.Empty(guard.ValidatedBatches);
    }

    [Fact]
    public async Task BuildAsync_SecondCallForSameCreatorReusesCachedRawPoolButRevalidatesDeckContext()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new FakeCreatorDeckCacheStore(
            Deck("cache-me", "deck-1", Mainboard("Arcane Signet", "Lightning Greaves")),
            Deck("cache-me", "deck-2", Mainboard("Arcane Signet", "Swiftfoot Boots")));
        var guard = new FakeCardGroundingGuard();
        var sut = new CreatorWhitelistPoolBuilder(store, guard, cache);

        IReadOnlyList<string> first = (await sut.BuildWithDiagnosticsAsync("cache-me", EmptyDeckContext(["arcane signet"]))).AcceptedNames;
        IReadOnlyList<string> second = (await sut.BuildWithDiagnosticsAsync(" cache-me ", EmptyDeckContext(["swiftfoot boots"]))).AcceptedNames;

        Assert.Equal(1, store.GetByCreatorCallCount);
        Assert.Equal(2, guard.ValidatedBatches.Count);
        Assert.Equal(2, guard.DeckContexts.Count);
        Assert.Contains("arcane signet", guard.DeckContexts[0].DeckCardNames);
        Assert.Contains("swiftfoot boots", guard.DeckContexts[1].DeckCardNames);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task BuildWithDiagnosticsAsync_UpstreamFailure_SurfacesDiagnosticFlag()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new FakeCreatorDeckCacheStore(Deck("guarded", "deck-1", Mainboard("Arcane Signet", "Jeska's Will")));
        var guard = new FakeCardGroundingGuard(new Dictionary<string, CardGroundingVerdict>(StringComparer.Ordinal)
        {
            ["Arcane Signet"] = Accepted("Arcane Signet"),
            ["Jeska's Will"] = Rejected("Jeska's Will", CardGroundingRejectReason.UpstreamUnavailable),
        });
        var sut = new CreatorWhitelistPoolBuilder(store, guard, cache);

        CreatorWhitelistPoolBuildResult result = await sut.BuildWithDiagnosticsAsync("guarded", EmptyDeckContext());

        Assert.True(result.HasUpstreamFailure);
        Assert.Equal(["Arcane Signet"], result.AcceptedNames);
    }

    [Fact]
    public async Task BuildAsync_BlankNormalizedName_UsesCanonicalCardNormalizerFallback()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new FakeCreatorDeckCacheStore(
            Deck("normalized", "deck-1",
                new DeckEntry
                {
                    Name = "Mystic Remora ★",
                    NormalizedName = string.Empty,
                    Quantity = 1,
                    Board = "mainboard",
                }),
            Deck("normalized", "deck-2", Mainboard("Mystic Remora")));
        var guard = new FakeCardGroundingGuard();
        var sut = new CreatorWhitelistPoolBuilder(store, guard, cache);

        IReadOnlyList<string> whitelist = (await sut.BuildWithDiagnosticsAsync("normalized", EmptyDeckContext())).AcceptedNames;

        Assert.Equal(["Mystic Remora"], whitelist);
        Assert.Equal(["Mystic Remora"], Assert.Single(guard.ValidatedBatches));
    }

    [Fact]
    public async Task BuildWithDiagnosticsAsync_CallerCancelsMidBuild_FactoryStillPopulatesCache()
    {
        // Why (WR-15): IMemoryCache.GetOrCreateAsync gives no stampede protection, so the
        // caller that happens to run the raw-pool factory must not fault (or leave the cache
        // unpopulated) just because that particular caller's own request was cancelled - other
        // callers for the same creator are relying on the populated entry.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new GatedCreatorDeckCacheStore(Deck("stampede", "deck-1", Mainboard("Sol Ring")));
        var guard = new FakeCardGroundingGuard();
        var sut = new CreatorWhitelistPoolBuilder(store, guard, cache);
        using var cts = new CancellationTokenSource();

        Task<CreatorWhitelistPoolBuildResult> firstCall = sut.BuildWithDiagnosticsAsync("stampede", EmptyDeckContext(), cts.Token);
        await store.EnteredGetByCreator.Task;
        cts.Cancel();
        store.Release();

        CreatorWhitelistPoolBuildResult result = await firstCall;

        Assert.Equal(["Sol Ring"], result.AcceptedNames);
        Assert.False(
            store.ReceivedToken.IsCancellationRequested,
            "The raw-pool factory must run under CancellationToken.None, not the calling request's token.");

        IReadOnlyList<string> second = (await sut.BuildWithDiagnosticsAsync("stampede", EmptyDeckContext())).AcceptedNames;

        Assert.Equal(["Sol Ring"], second);
        Assert.Equal(1, store.GetByCreatorCallCount);
    }

    [Fact]
    public void ServiceCollection_ValidateOnBuild_ResolvesCreatorWhitelistPoolBuilder()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "deckflow-98-03-di", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, "creator-deck-cache.db");

        try
        {
            var services = new ServiceCollection();
            services.AddMemoryCache();
            services.AddSingleton<ICreatorDeckCacheStore>(_ =>
                new CreatorDeckCacheStore(RelationalDatabaseConnection.FromSqlitePath(databasePath)));
            services.AddSingleton<ICardGroundingGuard, FakeCardGroundingGuard>();
            services.AddSingleton<CreatorWhitelistPoolBuilder>();

            using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            Assert.NotNull(provider.GetRequiredService<CreatorWhitelistPoolBuilder>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static CreatorDeckCacheEntry Deck(string creatorSlug, string deckId, params DeckEntry[] entries)
        => new()
        {
            CreatorSlug = creatorSlug,
            DeckId = deckId,
            ContentHash = $"{deckId}-hash",
            Size = entries.Sum(entry => entry.Quantity),
            ConfidenceMarker = "fixture",
            Entries = entries,
            CachedUtc = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero),
        };

    private static DeckEntry[] Mainboard(params string[] cardNames)
        => cardNames
            .Select(cardName => new DeckEntry
            {
                Name = cardName,
                NormalizedName = cardName.Trim().ToLowerInvariant(),
                Quantity = 1,
                Board = "mainboard",
            })
            .ToArray();

    private static CardGroundingDeckContext EmptyDeckContext(params string[] deckCardNames)
        => new()
        {
            CommanderColorIdentity = new HashSet<string>(StringComparer.Ordinal) { "W", "U", "B", "R", "G" },
            DeckProducedColors = new HashSet<char> { 'W', 'U', 'B', 'R', 'G' },
            DeckCardNames = new HashSet<string>(deckCardNames, StringComparer.Ordinal),
        };

    private static CardGroundingVerdict Accepted(string canonicalName)
        => new()
        {
            Accepted = true,
            CanonicalName = canonicalName,
            RejectReason = CardGroundingRejectReason.None,
        };

    private static CardGroundingVerdict Rejected(string canonicalName, CardGroundingRejectReason reason)
        => new()
        {
            Accepted = false,
            CanonicalName = canonicalName,
            RejectReason = reason,
        };

    /// <summary>
    /// A <see cref="ICreatorDeckCacheStore"/> double whose <see cref="GetByCreatorAsync"/> blocks
    /// until <see cref="Release"/> is called, recording the token it was invoked with, so a test
    /// can cancel the calling request's token mid-flight and assert the factory does not observe it.
    /// </summary>
    private sealed class GatedCreatorDeckCacheStore(params CreatorDeckCacheEntry[] entries) : ICreatorDeckCacheStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource EnteredGetByCreator { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int GetByCreatorCallCount { get; private set; }

        public CancellationToken ReceivedToken { get; private set; }

        public void Release() => _release.TrySetResult();

        public Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> GetContentHashAsync(string creatorSlug, string deckId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public async Task<IReadOnlyList<CreatorDeckCacheEntry>> GetByCreatorAsync(string creatorSlug, CancellationToken cancellationToken = default)
        {
            GetByCreatorCallCount++;
            ReceivedToken = cancellationToken;
            EnteredGetByCreator.TrySetResult();
            await _release.Task.ConfigureAwait(false);

            return entries
                .Where(entry => string.Equals(entry.CreatorSlug, creatorSlug, StringComparison.Ordinal))
                .ToArray();
        }

        public Task UpsertAsync(CreatorDeckCacheEntry entry, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Test store does not support writes.");
    }

    private sealed class FakeCreatorDeckCacheStore(params CreatorDeckCacheEntry[] entries) : ICreatorDeckCacheStore
    {
        public int GetByCreatorCallCount { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> GetContentHashAsync(string creatorSlug, string deckId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<CreatorDeckCacheEntry>> GetByCreatorAsync(string creatorSlug, CancellationToken cancellationToken = default)
        {
            GetByCreatorCallCount++;
            IReadOnlyList<CreatorDeckCacheEntry> matches = entries
                .Where(entry => string.Equals(entry.CreatorSlug, creatorSlug, StringComparison.Ordinal))
                .ToArray();
            return Task.FromResult(matches);
        }

        public Task UpsertAsync(CreatorDeckCacheEntry entry, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Test store does not support writes.");
    }

    private sealed class FakeCardGroundingGuard : ICardGroundingGuard
    {
        private readonly IReadOnlyDictionary<string, CardGroundingVerdict> _verdicts;

        public FakeCardGroundingGuard()
            : this(new Dictionary<string, CardGroundingVerdict>(StringComparer.Ordinal))
        {
        }

        public FakeCardGroundingGuard(IReadOnlyDictionary<string, CardGroundingVerdict> verdicts)
        {
            _verdicts = verdicts;
        }

        public List<IReadOnlyList<string>> ValidatedBatches { get; } = [];

        public List<CardGroundingDeckContext> DeckContexts { get; } = [];

        public Task<CardGroundingVerdict> TryValidateAsync(
            string candidateName,
            CardGroundingDeckContext deckContext,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_verdicts.TryGetValue(candidateName, out var verdict) ? verdict : Accepted(candidateName));

        public Task<CardGroundingBatchResult> ValidateAllAsync(
            IReadOnlyList<string> candidateNames,
            CardGroundingDeckContext deckContext,
            CancellationToken cancellationToken = default)
        {
            ValidatedBatches.Add(candidateNames.ToArray());
            DeckContexts.Add(deckContext);

            IReadOnlyList<CardGroundingVerdict> verdicts = candidateNames
                .Select(candidateName => _verdicts.TryGetValue(candidateName, out var verdict) ? verdict : Accepted(candidateName))
                .ToArray();

            return Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts = verdicts,
                HasUpstreamFailure = verdicts.Any(verdict => verdict.RejectReason == CardGroundingRejectReason.UpstreamUnavailable),
            });
        }
    }
}
