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

        try
        {
            CreatorStyleProfile[] profiles;
            await using (var stream = File.OpenRead(seedFilePath))
            {
                profiles = await JsonSerializer
                    .DeserializeAsync<CreatorStyleProfile[]>(stream, SeedJson.Options, cancellationToken)
                    .ConfigureAwait(false)
                    ?? Array.Empty<CreatorStyleProfile>();
            }

            var loaded = 0;
            foreach (var profile in profiles)
            {
                try
                {
                    await _profileStore.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
                    loaded++;
                }
                catch (Exception rowException) when (rowException is not OperationCanceledException)
                {
                    // Why (CR-05): one bad seed row must not cost the deployment its startup (D-10).
                    _logger.LogError(rowException, "Skipping malformed creator style profile seed row {Slug}.", profile.Slug);
                }
            }

            return loaded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Why: creator-style is admin-only and optional (D-10); a corrupt seed file — or an
            // unreadable file, or a store failure below deserialization — must degrade this one
            // feature, not abort startup for the whole deployment.
            _logger.LogError(exception, "Creator style profile seed load failed; skipping profile seed load.");
            return 0;
        }
    }

    private async Task<int> LoadDeckCacheIfPresentAsync(CancellationToken cancellationToken)
    {
        var seedFilePath = ResolveSeedFilePath(ContentKbPaths.CreatorDeckCacheSeedRelativePath);
        if (!File.Exists(seedFilePath))
        {
            _logger.LogInformation("Creator deck-cache seed file not found; skipping deck-cache seed load.");
            return 0;
        }

        try
        {
            CreatorDeckCacheEntry[] entries;
            await using (var stream = File.OpenRead(seedFilePath))
            {
                entries = await JsonSerializer
                    .DeserializeAsync<CreatorDeckCacheEntry[]>(stream, SeedJson.Options, cancellationToken)
                    .ConfigureAwait(false)
                    ?? Array.Empty<CreatorDeckCacheEntry>();
            }

            var loaded = 0;
            foreach (var entry in entries)
            {
                try
                {
                    await _deckCacheStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
                    loaded++;
                }
                catch (Exception rowException) when (rowException is not OperationCanceledException)
                {
                    // Why (CR-05): one bad seed row must not cost the deployment its startup (D-10).
                    _logger.LogError(
                        rowException,
                        "Skipping malformed creator deck-cache seed row {CreatorSlug}/{DeckId}.",
                        entry.CreatorSlug,
                        entry.DeckId);
                }
            }

            return loaded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Why: creator-style is admin-only and optional (D-10); a corrupt seed file — or an
            // unreadable file, or a store failure below deserialization — must degrade this one
            // feature, not abort startup for the whole deployment.
            _logger.LogError(exception, "Creator deck-cache seed load failed; skipping deck-cache seed load.");
            return 0;
        }
    }

    private string ResolveSeedFilePath(string relativePath)
        => Path.GetFullPath(Path.Combine(_resolver.ContentBase, relativePath));
}
