using Microsoft.Extensions.Caching.Memory;

namespace DeckFlow.Web.Services.Scryfall;

/// <summary>
/// Shared cache mechanics for single-name Scryfall resolution helpers.
/// </summary>
internal static class CachedNameResolution
{
    internal static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromHours(24);
    internal static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromHours(1);

    internal static string BuildCacheKey(string prefix, string candidateName)
        => prefix + candidateName.Trim().ToLowerInvariant();

    internal static async Task<T> GetOrAddAsync<T>(
        IMemoryCache cache,
        string prefix,
        string candidateName,
        Func<CancellationToken, Task<T>> fetchAsync,
        Func<Exception, T> onFailure,
        Func<T, TimeSpan?> selectTtl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(candidateName);
        ArgumentNullException.ThrowIfNull(fetchAsync);
        ArgumentNullException.ThrowIfNull(onFailure);
        ArgumentNullException.ThrowIfNull(selectTtl);

        string cacheKey = BuildCacheKey(prefix, candidateName);
        if (cache.TryGetValue<T>(cacheKey, out T? cachedValue))
        {
            return cachedValue!;
        }

        T result;
        try
        {
            result = await fetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = onFailure(exception);
        }

        TimeSpan? ttl = selectTtl(result);
        if (ttl.HasValue)
        {
            cache.Set(cacheKey, result, ttl.Value);
        }

        return result;
    }
}
