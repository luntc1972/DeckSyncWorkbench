using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace MtgDeckStudio.Web.Services;

/// <summary>
/// Provides the official Commander banned-card list from mtgcommander.net.
/// </summary>
public interface ICommanderBanListService
{
    /// <summary>
    /// Returns the current official Commander banned-card names.
    /// </summary>
    Task<IReadOnlyList<string>> GetBannedCardsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches and caches the official Commander banned-card list.
/// </summary>
public sealed partial class CommanderBanListService : ICommanderBanListService
{
    private const string BannedListUrl = "https://mtgcommander.net/index.php/banned-list/";
    private const string CacheKey = "commander-banned-cards";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
    private static readonly Regex SummaryRegex = SummaryPattern();
    private readonly IMemoryCache _memoryCache;
    private readonly Func<CancellationToken, Task<string>> _fetchPageAsync;

    /// <summary>
    /// Creates a banned-list service using the official Commander site.
    /// </summary>
    public CommanderBanListService(
        IMemoryCache memoryCache,
        Func<CancellationToken, Task<string>>? fetchPageAsync = null)
    {
        _memoryCache = memoryCache;
        _fetchPageAsync = fetchPageAsync ?? FetchPageAsync;
    }

    /// <summary>
    /// Returns the official banned-card names, newest fetch cached in memory.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetBannedCardsAsync(CancellationToken cancellationToken = default)
    {
        if (_memoryCache.TryGetValue<IReadOnlyList<string>>(CacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var html = await _fetchPageAsync(cancellationToken).ConfigureAwait(false);
        var cards = ParseBannedCards(html);
        _memoryCache.Set(CacheKey, cards, CacheDuration);
        return cards;
    }

    internal static IReadOnlyList<string> ParseBannedCards(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return Array.Empty<string>();
        }

        return SummaryRegex.Matches(html)
            .Select(match => WebUtility.HtmlDecode(match.Groups["name"].Value).Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<string> FetchPageAsync(CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 MtgDeckStudio");
        using var response = await httpClient.GetAsync(BannedListUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(@"<summary>\s*(?<name>[^<]+?)\s*</summary>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryPattern();
}
