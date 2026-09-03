using System.Text.Json;
using DeckFlow.Core.Content;
using DeckFlow.Core.Knowledge;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.FeatureFlags;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Tests for <see cref="CreatorStyleSeedLoader"/> covering the two-file creator-style seed startup hydration flow.
/// </summary>
public sealed class CreatorStyleSeedLoaderTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public async Task LoadIfPresentAsync_ReturnsZero_WhenBothSeedFilesAbsent()
    {
        var baseDir = CreateContentKbBase();
        var profileStore = new FakeCreatorStyleProfileStore();
        var deckCacheStore = new FakeCreatorDeckCacheStore();
        var loader = BuildLoader(baseDir, profileStore, deckCacheStore);

        var count = await loader.LoadIfPresentAsync();

        Assert.Equal(0, count);
        Assert.Empty(profileStore.Upserts);
        Assert.Empty(deckCacheStore.Upserts);
    }

    [Fact]
    public async Task LoadIfPresentAsync_UpsertsProfilesAndDeckCacheRows_WhenBothSeedFilesPresent()
    {
        var baseDir = CreateContentKbBase();
        WriteSeed(
            baseDir,
            ContentKbPaths.CreatorStyleProfileSeedRelativePath,
            JsonSerializer.Serialize(new[]
            {
                CreateProfile("alpha"),
                CreateProfile("beta")
            }));
        WriteSeed(
            baseDir,
            ContentKbPaths.CreatorDeckCacheSeedRelativePath,
            JsonSerializer.Serialize(new[]
            {
                CreateDeckCacheEntry("alpha", "deck-a"),
                CreateDeckCacheEntry("alpha", "deck-b"),
                CreateDeckCacheEntry("beta", "deck-c")
            }));
        var profileStore = new FakeCreatorStyleProfileStore();
        var deckCacheStore = new FakeCreatorDeckCacheStore();
        var loader = BuildLoader(baseDir, profileStore, deckCacheStore);

        var count = await loader.LoadIfPresentAsync();

        Assert.Equal(5, count);
        Assert.Collection(
            profileStore.Upserts,
            profile => Assert.Equal("alpha", profile.Slug),
            profile => Assert.Equal("beta", profile.Slug));
        Assert.Collection(
            deckCacheStore.Upserts,
            entry => Assert.Equal(("alpha", "deck-a"), (entry.CreatorSlug, entry.DeckId)),
            entry => Assert.Equal(("alpha", "deck-b"), (entry.CreatorSlug, entry.DeckId)),
            entry => Assert.Equal(("beta", "deck-c"), (entry.CreatorSlug, entry.DeckId)));
    }

    [Fact]
    public async Task LoadIfPresentAsync_LoadsPresentProfilesFile_WhenDeckCacheSeedFileAbsent()
    {
        var baseDir = CreateContentKbBase();
        WriteSeed(
            baseDir,
            ContentKbPaths.CreatorStyleProfileSeedRelativePath,
            JsonSerializer.Serialize(new[]
            {
                CreateProfile("alpha")
            }));
        var profileStore = new FakeCreatorStyleProfileStore();
        var deckCacheStore = new FakeCreatorDeckCacheStore();
        var loader = BuildLoader(baseDir, profileStore, deckCacheStore);

        var count = await loader.LoadIfPresentAsync();

        Assert.Equal(1, count);
        Assert.Single(profileStore.Upserts);
        Assert.Empty(deckCacheStore.Upserts);
    }

    [Fact]
    public async Task LoadIfPresentAsync_MalformedProfileSeedFile_LogsAndReturnsZeroWithoutThrowing()
    {
        // Why (WR-10): a corrupt seed file must degrade this admin-only, optional feature, not
        // abort startup for the whole deployment.
        var baseDir = CreateContentKbBase();
        WriteSeed(baseDir, ContentKbPaths.CreatorStyleProfileSeedRelativePath, "{");
        var profileStore = new FakeCreatorStyleProfileStore();
        var deckCacheStore = new FakeCreatorDeckCacheStore();
        var loader = BuildLoader(baseDir, profileStore, deckCacheStore);

        var count = await loader.LoadIfPresentAsync();

        Assert.Equal(0, count);
        Assert.Empty(profileStore.Upserts);
        Assert.Empty(deckCacheStore.Upserts);
    }

    [Fact]
    public async Task LoadIfPresentAsync_MalformedProfileSeedFile_StillLoadsUnaffectedDeckCacheSeedFile()
    {
        // Why (WR-10): the two seed files must fail independently — a corrupt profile seed must
        // not also block a healthy deck-cache seed from loading.
        var baseDir = CreateContentKbBase();
        WriteSeed(baseDir, ContentKbPaths.CreatorStyleProfileSeedRelativePath, "{");
        WriteSeed(
            baseDir,
            ContentKbPaths.CreatorDeckCacheSeedRelativePath,
            JsonSerializer.Serialize(new[] { CreateDeckCacheEntry("alpha", "deck-a") }));
        var profileStore = new FakeCreatorStyleProfileStore();
        var deckCacheStore = new FakeCreatorDeckCacheStore();
        var loader = BuildLoader(baseDir, profileStore, deckCacheStore);

        var count = await loader.LoadIfPresentAsync();

        Assert.Equal(1, count);
        Assert.Empty(profileStore.Upserts);
        Assert.Single(deckCacheStore.Upserts);
    }

    [Fact]
    public async Task LoadIfPresentAsync_MalformedDeckCacheSeedFile_LogsAndReturnsZeroWithoutThrowing()
    {
        var baseDir = CreateContentKbBase();
        WriteSeed(baseDir, ContentKbPaths.CreatorDeckCacheSeedRelativePath, "not json");
        var profileStore = new FakeCreatorStyleProfileStore();
        var deckCacheStore = new FakeCreatorDeckCacheStore();
        var loader = BuildLoader(baseDir, profileStore, deckCacheStore);

        var count = await loader.LoadIfPresentAsync();

        Assert.Equal(0, count);
        Assert.Empty(profileStore.Upserts);
        Assert.Empty(deckCacheStore.Upserts);
    }

    [Fact]
    public async Task LoadIfPresentAsync_ProfileSeedContainsBlankSlugRow_SkipsBadRowAndLoadsTheRest()
    {
        // Why (CR-05): WR-10 only guarded deserialization. A seed file that is VALID JSON but has
        // one row that fails store-side validation (e.g. blank slug, mirroring
        // CreatorStyleProfileStore.UpsertAsync's ArgumentException.ThrowIfNullOrWhiteSpace guard)
        // must not crash startup — the bad row is skipped and the rest still load.
        var baseDir = CreateContentKbBase();
        WriteSeed(
            baseDir,
            ContentKbPaths.CreatorStyleProfileSeedRelativePath,
            JsonSerializer.Serialize(new[]
            {
                CreateProfile("alpha"),
                CreateProfile(string.Empty),
                CreateProfile("beta")
            }));
        var profileStore = new ThrowingOnBlankSlugCreatorStyleProfileStore();
        var deckCacheStore = new FakeCreatorDeckCacheStore();
        var loader = BuildLoader(baseDir, profileStore, deckCacheStore);

        var count = await loader.LoadIfPresentAsync();

        Assert.Equal(2, count);
        Assert.Collection(
            profileStore.Upserts,
            profile => Assert.Equal("alpha", profile.Slug),
            profile => Assert.Equal("beta", profile.Slug));
    }

    [Fact]
    public async Task LoadIfPresentAsync_DeckCacheSeedContainsBlankCreatorSlugRow_SkipsBadRowAndLoadsTheRest()
    {
        // Why (CR-05): same shape for the deck-cache loader — a row that fails store-side
        // validation (blank CreatorSlug, mirroring CreatorDeckCacheStore.UpsertAsync's guard)
        // must not abort the whole seed load.
        var baseDir = CreateContentKbBase();
        WriteSeed(
            baseDir,
            ContentKbPaths.CreatorDeckCacheSeedRelativePath,
            JsonSerializer.Serialize(new[]
            {
                CreateDeckCacheEntry("alpha", "deck-a"),
                CreateDeckCacheEntry(string.Empty, "deck-bad"),
                CreateDeckCacheEntry("beta", "deck-c")
            }));
        var profileStore = new FakeCreatorStyleProfileStore();
        var deckCacheStore = new ThrowingOnBlankCreatorSlugDeckCacheStore();
        var loader = BuildLoader(baseDir, profileStore, deckCacheStore);

        var count = await loader.LoadIfPresentAsync();

        Assert.Equal(2, count);
        Assert.Collection(
            deckCacheStore.Upserts,
            entry => Assert.Equal("deck-a", entry.DeckId),
            entry => Assert.Equal("deck-c", entry.DeckId));
    }

    private CreatorStyleSeedLoader BuildLoader(
        string baseDir,
        ICreatorStyleProfileStore profileStore,
        ICreatorDeckCacheStore deckCacheStore)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ContentKb:ContentBase"] = baseDir })
            .Build();
        var resolver = new ContentKbArtifactPathResolver(
            new StubWebHostEnvironment(baseDir),
            configuration,
            new FakeFeatureFlagCache(),
            NullLogger<ContentKbArtifactPathResolver>.Instance);
        return new CreatorStyleSeedLoader(
            resolver,
            profileStore,
            deckCacheStore,
            NullLogger<CreatorStyleSeedLoader>.Instance);
    }

    private string CreateContentKbBase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "creator-style-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "content-kb"));
        _tempDirs.Add(dir);
        return dir;
    }

    private static void WriteSeed(string baseDir, string relativePath, string json)
    {
        var fullPath = Path.Combine(baseDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, json);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static CreatorStyleProfile CreateProfile(string slug)
        => new()
        {
            Slug = slug,
            Platform = "youtube",
            MinDecks = 7,
            InsufficientSample = false,
            StatedRules = Array.Empty<StatedRule>(),
            MeasuredMetrics = Array.Empty<MeasuredMetric>(),
            FusedTargets = Array.Empty<FusedTarget>(),
            UpdatedUtc = DateTimeOffset.Parse("2026-07-18T00:00:00Z")
        };

    private static CreatorDeckCacheEntry CreateDeckCacheEntry(string creatorSlug, string deckId)
        => new()
        {
            CreatorSlug = creatorSlug,
            DeckId = deckId,
            ContentHash = $"hash-{deckId}",
            FolderId = 42,
            FolderName = "Folder",
            Size = 100,
            ConfidenceMarker = "exact",
            Entries =
            [
                new DeckFlow.Core.Models.DeckEntry
                {
                    Name = "Sol Ring",
                    NormalizedName = "sol ring",
                    Quantity = 1,
                    Board = "mainboard"
                }
            ],
            CachedUtc = DateTimeOffset.Parse("2026-07-18T00:00:00Z")
        };

    private sealed class FakeCreatorStyleProfileStore : ICreatorStyleProfileStore
    {
        public List<CreatorStyleProfile> Upserts { get; } = new();

        public Task EnsureSchemaAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CreatorStyleProfile?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
            => Task.FromResult<CreatorStyleProfile?>(null);

        public Task UpsertAsync(CreatorStyleProfile profile, CancellationToken cancellationToken = default)
        {
            Upserts.Add(profile);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCreatorDeckCacheStore : ICreatorDeckCacheStore
    {
        public List<CreatorDeckCacheEntry> Upserts { get; } = new();

        public Task EnsureSchemaAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> GetContentHashAsync(string creatorSlug, string deckId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<CreatorDeckCacheEntry>> GetByCreatorAsync(string creatorSlug, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CreatorDeckCacheEntry>>(Array.Empty<CreatorDeckCacheEntry>());

        public Task UpsertAsync(CreatorDeckCacheEntry entry, CancellationToken cancellationToken = default)
        {
            Upserts.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingOnBlankSlugCreatorStyleProfileStore : ICreatorStyleProfileStore
    {
        public List<CreatorStyleProfile> Upserts { get; } = new();

        public Task EnsureSchemaAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CreatorStyleProfile?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
            => Task.FromResult<CreatorStyleProfile?>(null);

        public Task UpsertAsync(CreatorStyleProfile profile, CancellationToken cancellationToken = default)
        {
            // Mirrors CreatorStyleProfileStore.UpsertAsync's real guard.
            ArgumentException.ThrowIfNullOrWhiteSpace(profile.Slug);
            Upserts.Add(profile);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingOnBlankCreatorSlugDeckCacheStore : ICreatorDeckCacheStore
    {
        public List<CreatorDeckCacheEntry> Upserts { get; } = new();

        public Task EnsureSchemaAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> GetContentHashAsync(string creatorSlug, string deckId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<CreatorDeckCacheEntry>> GetByCreatorAsync(string creatorSlug, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CreatorDeckCacheEntry>>(Array.Empty<CreatorDeckCacheEntry>());

        public Task UpsertAsync(CreatorDeckCacheEntry entry, CancellationToken cancellationToken = default)
        {
            // Mirrors CreatorDeckCacheStore.UpsertAsync's real guard.
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.CreatorSlug);
            Upserts.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFeatureFlagCache : IFeatureFlagCache
    {
        public bool IsEnabled(string flagKey) => false;

        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyDictionary<string, bool> Snapshot() => new Dictionary<string, bool>();
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new NullFileProvider();
            WebRootPath = contentRootPath;
            WebRootFileProvider = new NullFileProvider();
        }

        public string WebRootPath { get; set; }

        public IFileProvider WebRootFileProvider { get; set; }

        public string ApplicationName { get; set; } = "DeckFlow.Web.Tests";

        public IFileProvider ContentRootFileProvider { get; set; }

        public string ContentRootPath { get; set; }

        public string EnvironmentName { get; set; } = Environments.Development;
    }
}
