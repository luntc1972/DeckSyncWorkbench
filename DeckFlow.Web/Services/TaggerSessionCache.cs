using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace DeckFlow.Web.Services;

/// <summary>
/// Coupled CSRF token + cookie payload captured from a Scryfall Tagger HTML page response.
/// Both fields originate from the same upstream request and expire together (D-07).
/// </summary>
public sealed record TaggerSession(string CsrfToken, string CookieHeader, DateTimeOffset CachedAt);

/// <summary>
/// 270-second IMemoryCache-backed store for the Tagger CSRF token + Set-Cookie payload.
/// TTL is 270s (not 300s = HandlerLifetime) to create a 30-second safety margin at the
/// rotation boundary — prevents a request arriving just before handler rotation from
/// replaying a stale session against a fresh handler (HIGH-2 fix).
/// An age-refresh threshold at 240s triggers a background re-fetch while serving the
/// cached value so the next POST sees a fresh session without blocking the caller.
/// Replay strategy at the call site is request.AddHeader("Cookie", payload) +
/// request.AddHeader("X-CSRF-Token", token) — never via the .NET cookie container API (D-08).
/// </summary>
public interface ITaggerSessionCache
{
    /// <summary>Returns the cached session if present and unexpired; otherwise null.</summary>
    TaggerSession? TryGet();

    /// <summary>Stores the session with a 270-second absolute expiration.</summary>
    void Set(TaggerSession session);

    /// <summary>Drops the cached session — used to force a CSRF re-fetch on 403.</summary>
    void Invalidate();

    /// <summary>
    /// Returns true if the cached session is older than the age-refresh threshold (240s).
    /// Callers may trigger a background refresh when this returns true while still serving
    /// the cached value for the current request.
    /// </summary>
    bool IsApproachingExpiry();
}

/// <summary>IMemoryCache-backed implementation of <see cref="ITaggerSessionCache"/>.</summary>
public sealed class TaggerSessionCache : ITaggerSessionCache
{
    private const string CacheKey = "tagger:session";

    /// <summary>
    /// Cache TTL: 270s. 30s below HandlerLifetime (300s) to avoid the boundary race where
    /// a request near rotation replays a stale cookie+token against a freshly rotated handler.
    /// (HIGH-2 fix — decouples cache expiry from handler lifetime.)
    /// </summary>
    private static readonly TimeSpan SessionCacheTtl = TimeSpan.FromSeconds(270);

    /// <summary>
    /// Age threshold for proactive background refresh. When a cached session is older than
    /// 240s, the next read triggers a background re-fetch while still serving the cached value
    /// so the subsequent POST sees a fresh handler+session pair.
    /// </summary>
    private static readonly TimeSpan SessionRefreshAge = TimeSpan.FromSeconds(240);

    private readonly IMemoryCache _memoryCache;

    /// <summary>Creates a new cache backed by the supplied <see cref="IMemoryCache"/>.</summary>
    public TaggerSessionCache(IMemoryCache memoryCache)
    {
        ArgumentNullException.ThrowIfNull(memoryCache);
        _memoryCache = memoryCache;
    }

    /// <inheritdoc />
    public TaggerSession? TryGet() =>
        _memoryCache.TryGetValue<TaggerSession>(CacheKey, out var session) ? session : null;

    /// <inheritdoc />
    public void Set(TaggerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _memoryCache.Set(CacheKey, session, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = SessionCacheTtl,
        });
    }

    /// <inheritdoc />
    public void Invalidate() => _memoryCache.Remove(CacheKey);

    /// <inheritdoc />
    public bool IsApproachingExpiry()
    {
        var session = TryGet();
        if (session is null) return false;
        return (DateTimeOffset.UtcNow - session.CachedAt) >= SessionRefreshAge;
    }
}
