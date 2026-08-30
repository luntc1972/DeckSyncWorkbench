using System.Text.Json;
using DeckFlow.Core.Content;
using DeckFlow.Core.Knowledge;

namespace DeckFlow.Web.Services;

/// <summary>
/// Reads the committed creator-style seed JSON files and upserts them into the local stores.
/// </summary>
public sealed class CreatorStyleSeedLoader : ICreatorStyleSeedLoader
{
    private readonly ContentKbArtifactPathResolver _resolver;
    private readonly ICreatorStyleProfileStore _profileStore;
    private readonly ICreatorDeckCacheStore _deckCacheStore;
    private readonly ILogger<CreatorStyleSeedLoader> _logger;

    /// <summary>
    /// Creates a creator-style seed loader.
    /// </summary>
    /// <param name="resolver">Artifact path resolver.</param>
    /// <param name="profileStore">Creator style-profile store.</param>
    /// <param name="deckCacheStore">Creator deck-cache store.</param>
    /// <param name="logger">Logger.</param>
    public CreatorStyleSeedLoader(
        ContentKbArtifactPathResolver resolver,
        ICreatorStyleProfileStore profileStore,
        ICreatorDeckCacheStore deckCacheStore,
        ILogger<CreatorStyleSeedLoader> logger)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(deckCacheStore);
        ArgumentNullException.ThrowIfNull(logger);

        _resolver = resolver;
        _profileStore = profileStore;
        _deckCacheStore = deckCacheStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> LoadIfPresentAsync(CancellationToken cancellationToken = default)
    {
        var profileCount = await LoadProfilesIfPresentAsync(cancellationToken).ConfigureAwait(false);
        var deckCacheCount = await LoadDeckCacheIfPresentAsync(cancellationToken).ConfigureAwait(false);
        var totalCount = profileCount + deckCacheCount;

        _logger.LogInformation(
            "Creator style seed load complete: {ProfileCount} profiles, {DeckCacheCount} deck-cache rows, {TotalCount} total rows.",
            profileCount,
            deckCacheCount,
            totalCount);

        return totalCount;
    }

    private async Task<int> LoadProfilesIfPresentAsync(CancellationToken cancellationToken)
    {
        var seedFilePath = ResolveSeedFilePath(ContentKbPaths.CreatorStyleProfileSeedRelativePath);
        if (!File.Exists(seedFilePath))
        {
            _logger.LogInformation("Creator style profile seed file not found; skipping profile seed load.");
            return 0;
        }

        await using var stream = File.OpenRead(seedFilePath);
        var profiles = await JsonSerializer
            .DeserializeAsync<CreatorStyleProfile[]>(stream, SeedJson.Options, cancellationToken)
            .ConfigureAwait(false)
            ?? Array.Empty<CreatorStyleProfile>();

        foreach (var profile in profiles)
        {
            await _profileStore.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        return profiles.Length;
    }

    private async Task<int> LoadDeckCacheIfPresentAsync(CancellationToken cancellationToken)
    {
        var seedFilePath = ResolveSeedFilePath(ContentKbPaths.CreatorDeckCacheSeedRelativePath);
        if (!File.Exists(seedFilePath))
        {
            _logger.LogInformation("Creator deck-cache seed file not found; skipping deck-cache seed load.");
            return 0;
        }

        await using var stream = File.OpenRead(seedFilePath);
        var entries = await JsonSerializer
            .DeserializeAsync<CreatorDeckCacheEntry[]>(stream, SeedJson.Options, cancellationToken)
            .ConfigureAwait(false)
            ?? Array.Empty<CreatorDeckCacheEntry>();

        foreach (var entry in entries)
        {
            await _deckCacheStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return entries.Length;
    }

    private string ResolveSeedFilePath(string relativePath)
        => Path.GetFullPath(Path.Combine(_resolver.ContentBase, relativePath));
}
