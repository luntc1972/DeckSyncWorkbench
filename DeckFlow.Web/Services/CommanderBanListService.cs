using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Polly;
using Polly.Registry;
using RestSharp;
using DeckFlow.Web.Services.Http;

namespace DeckFlow.Web.Services;

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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResiliencePipeline<RestResponse> _resiliencePipeline;
    private readonly IMemoryCache _memoryCache;
    private readonly Func<CancellationToken, Task<string>> _fetchPageAsync;

    /// <summary>
    /// Private ctor accepting a pre-resolved Polly pipeline. Used by both the production
    /// ctor (which resolves the pipeline from <see cref="ResiliencePipelineProvider{TKey}"/>)
    /// and the test-compat overload (which passes <see cref="ResiliencePipeline{T}.Empty"/>
    /// because tests always supply <paramref name="fetchPageAsync"/> and never exercise the
    /// HTTP/pipeline path).
    /// </summary>
    private CommanderBanListService(
        IHttpClientFactory httpClientFactory,
        ResiliencePipeline<RestResponse> pipeline,
        IMemoryCache memoryCache,
        Func<CancellationToken, Task<string>>? fetchPageAsync)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(memoryCache);
        _httpClientFactory = httpClientFactory;
        _resiliencePipeline = pipeline ?? ResiliencePipeline<RestResponse>.Empty;
        _memoryCache = memoryCache;
        _fetchPageAsync = fetchPageAsync ?? FetchPageAsync;
    }

    /// <summary>
    /// Production ctor - DI resolves the named "banlist" pipeline from
    /// <see cref="ResiliencePipelineProvider{TKey}"/> per D-05/B2 (no keyed-services attribute).
    /// </summary>
    public CommanderBanListService(
        IHttpClientFactory httpClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        IMemoryCache memoryCache,
        Func<CancellationToken, Task<string>>? fetchPageAsync = null)
        : this(
            httpClientFactory,
            pipelineProvider?.GetPipeline<RestResponse>("banlist") ?? ResiliencePipeline<RestResponse>.Empty,
            memoryCache,
            fetchPageAsync)
    {
        ArgumentNullException.ThrowIfNull(pipelineProvider);
    }

    /// <summary>
    /// Internal test-compat ctor preserving the legacy (memoryCache, fetchOverride) signature
    /// used by CommanderBanListServiceTests.GetBannedCardsAsync_CachesResults. Existing tests
    /// always supply <paramref name="fetchPageAsync"/>, so the production HTTP/pipeline path
    /// is short-circuited. Internal per pre-existing AssemblyInfo InternalsVisibleTo for
    /// DeckFlow.Web.Tests - public would create a DI ctor-resolution ambiguity at startup.
    /// Uses <see cref="ResiliencePipeline{T}.Empty"/> directly per MEDIUM-4 - no custom provider.
    /// </summary>
    internal CommanderBanListService(
        IMemoryCache memoryCache,
        Func<CancellationToken, Task<string>>? fetchPageAsync)
        : this(
            NullHttpClientFactory.Instance,
            ResiliencePipeline<RestResponse>.Empty,
            memoryCache,
            fetchPageAsync)
    {
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

    private async Task<string> FetchPageAsync(CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient("commander-banlist");
        var restClient = new RestClient(httpClient);
        var request = new RestRequest(BannedListUrl, Method.Get);

        var response = await _resiliencePipeline.ExecuteAsync(
            async ct => await restClient.ExecuteAsync(request, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessful)
        {
            throw new HttpRequestException(
                $"BanList fetch failed: HTTP {(int)response.StatusCode}",
                inner: null,
                statusCode: response.StatusCode);
        }

        return response.Content ?? string.Empty;
    }

    [GeneratedRegex(@"<summary>\s*(?<name>[^<]+?)\s*</summary>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryPattern();
}
