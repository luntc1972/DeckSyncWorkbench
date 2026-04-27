using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using RestSharp;

namespace DeckFlow.Web.Services;

/// <summary>
/// Fetches community-curated oracle tags from Scryfall Tagger for a given card.
/// </summary>
public interface IScryfallTaggerService
{
    /// <summary>
    /// Looks up oracle/functional tags for the supplied card name via the Scryfall Tagger GraphQL endpoint.
    /// </summary>
    Task<IReadOnlyList<string>> LookupOracleTagsAsync(string cardName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IScryfallTaggerService"/>.
/// Resolves the card via the Scryfall REST API, then queries the Tagger GraphQL endpoint for oracle tags.
/// </summary>
public sealed class ScryfallTaggerService : IScryfallTaggerService
{
    private static readonly string TaggerQuery =
        "query($set:String!,$number:String!){card:cardBySet(set:$set,number:$number){taggings{tag{name type slug}weight status}}}";

    private readonly IScryfallRestClientFactory _scryfallRestClientFactory;
    private readonly IScryfallTaggerHttpClient _taggerHttpClient;
    private readonly ITaggerSessionCache _taggerSessionCache;
    private readonly ResiliencePipeline<RestResponse> _scryfallPipeline;
    private readonly ResiliencePipeline<RestResponse> _taggerPipeline;
    private readonly ResiliencePipeline<RestResponse> _taggerPostPipeline;
    private readonly ILogger<ScryfallTaggerService> _logger;

    /// <summary>
    /// HIGH-1 loop guard — flows correctly across async/await boundaries.
    /// Replaces the plan's [ThreadStatic] suggestion which would not survive thread hops on continuations.
    /// </summary>
    private static readonly AsyncLocal<bool> _attemptedRefresh = new();

    /// <summary>
    /// Creates a Tagger service backed by the typed Tagger HttpClient (D-06), the
    /// IScryfallRestClientFactory for Scryfall card lookups, the named Polly v8 pipelines
    /// (scryfall, tagger, tagger-post — D-04/D-05), and the 270s session cache (HIGH-2).
    /// </summary>
    public ScryfallTaggerService(
        IScryfallRestClientFactory scryfallRestClientFactory,
        IScryfallTaggerHttpClient taggerHttpClient,
        ITaggerSessionCache taggerSessionCache,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<ScryfallTaggerService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scryfallRestClientFactory);
        ArgumentNullException.ThrowIfNull(taggerHttpClient);
        ArgumentNullException.ThrowIfNull(taggerSessionCache);
        ArgumentNullException.ThrowIfNull(pipelineProvider);
        _scryfallRestClientFactory = scryfallRestClientFactory;
        _taggerHttpClient = taggerHttpClient;
        _taggerSessionCache = taggerSessionCache;
        _scryfallPipeline = pipelineProvider.GetPipeline<RestResponse>("scryfall");
        _taggerPipeline = pipelineProvider.GetPipeline<RestResponse>("tagger");
        _taggerPostPipeline = pipelineProvider.GetPipeline<RestResponse>("tagger-post");
        _logger = logger ?? NullLogger<ScryfallTaggerService>.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> LookupOracleTagsAsync(string cardName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);

        var (set, collectorNumber) = await ResolveCardPrintingAsync(cardName.Trim(), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(set) || string.IsNullOrEmpty(collectorNumber))
        {
            return [];
        }

        // Cache-first: serve a fresh cached session, fetch on miss.
        var session = _taggerSessionCache.TryGet();
        if (session is null)
        {
            session = await FetchTaggerSessionAsync(set, collectorNumber, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                _logger.LogWarning("Unable to obtain Tagger session for {CardName}.", cardName);
                return [];
            }
            _taggerSessionCache.Set(session);
        }
        else if (_taggerSessionCache.IsApproachingExpiry())
        {
            // HIGH-2: cached session age >= 240s but TTL not yet hit. Trigger background refresh
            // so the next request gets a fresh cookie+token while the current request still uses
            // the cached value. Decouples session expiry from the 5-min HandlerLifetime rotation.
            var bgSet = set;
            var bgNumber = collectorNumber;
            _ = Task.Run(async () =>
            {
                try
                {
                    var refreshed = await FetchTaggerSessionAsync(bgSet, bgNumber, CancellationToken.None).ConfigureAwait(false);
                    if (refreshed is not null) _taggerSessionCache.Set(refreshed);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Background Tagger session refresh failed; cached value remains.");
                }
            });
        }

        return await QueryTaggerGraphQlAsync(set, collectorNumber, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Calls the Scryfall REST API to resolve a card name into its default set code and collector number.
    /// </summary>
    private async Task<(string Set, string CollectorNumber)> ResolveCardPrintingAsync(string cardName, CancellationToken cancellationToken)
    {
        var scryfallClient = _scryfallRestClientFactory.Create();
        var request = new RestRequest("cards/named", Method.Get);
        request.AddQueryParameter("exact", cardName);

        var response = await ScryfallThrottle.ExecuteAsync(
            ct => _scryfallPipeline.ExecuteAsync(
                async pollyCt => await scryfallClient.ExecuteAsync(request, pollyCt).ConfigureAwait(false),
                ct).AsTask(),
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
        {
            _logger.LogWarning("Scryfall card lookup failed for {CardName}: {Status}", cardName, response.StatusCode);
            return (string.Empty, string.Empty);
        }

        using var document = JsonDocument.Parse(response.Content);
        var root = document.RootElement;

        var set = root.TryGetProperty("set", out var setProp) ? setProp.GetString() ?? string.Empty : string.Empty;
        var number = root.TryGetProperty("collector_number", out var numProp) ? numProp.GetString() ?? string.Empty : string.Empty;

        return (set, number);
    }

    /// <summary>
    /// Fetches a Tagger card page via the typed cookie-disabled HttpClient and extracts the
    /// CSRF token + Set-Cookie payload. Returns a TaggerSession with CachedAt = UTC now so the
    /// HIGH-2 age-threshold logic can decide when to background-refresh.
    /// </summary>
    private async Task<TaggerSession?> FetchTaggerSessionAsync(string set, string collectorNumber, CancellationToken cancellationToken)
    {
        var taggerRestClient = new RestClient(_taggerHttpClient.Inner);
        var pageRequest = new RestRequest($"card/{set}/{collectorNumber}", Method.Get);

        var pageResponse = await _taggerPipeline.ExecuteAsync(
            async ct => await taggerRestClient.ExecuteAsync(pageRequest, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (!pageResponse.IsSuccessful || string.IsNullOrEmpty(pageResponse.Content))
        {
            _logger.LogWarning("Tagger page fetch failed: HTTP {Status}", (int)pageResponse.StatusCode);
            return null;
        }

        var token = ScryfallTaggerParsers.TryExtractCsrfToken(pageResponse.Content);
        if (string.IsNullOrEmpty(token)) return null;

        var cookieHeader = BuildCookieHeader(pageResponse);
        if (string.IsNullOrEmpty(cookieHeader)) return null;

        return new TaggerSession(token, cookieHeader, DateTimeOffset.UtcNow);
    }

    private static string BuildCookieHeader(RestResponse response)
    {
        var setCookies = response.Headers?
            .Where(h => h.Name is not null && h.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h => StripCookieAttributes(h.Value?.ToString() ?? string.Empty))
            .Where(v => !string.IsNullOrEmpty(v))
            .ToArray();
        return setCookies is { Length: > 0 } ? string.Join("; ", setCookies) : string.Empty;
    }

    private static string StripCookieAttributes(string setCookieValue)
    {
        var semicolon = setCookieValue.IndexOf(';');
        return semicolon < 0 ? setCookieValue : setCookieValue[..semicolon];
    }

    /// <summary>
    /// Posts the GraphQL query to the Tagger endpoint via the tagger-post pipeline (retry=0 because
    /// GraphQL POST is non-idempotent — W6/Pitfall 2). On 403 invokes
    /// <see cref="RefreshSessionAndRetryAsync"/> to satisfy SC-2.
    /// </summary>
    private async Task<IReadOnlyList<string>> QueryTaggerGraphQlAsync(
        string set,
        string collectorNumber,
        TaggerSession session,
        CancellationToken cancellationToken)
    {
        var response = await ExecuteTaggerPostAsync(set, collectorNumber, session, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // HIGH-1: 403 received - invalidate stale session, force-refresh, retry POST once.
            return await RefreshSessionAndRetryAsync(set, collectorNumber, cancellationToken).ConfigureAwait(false);
        }

        if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
        {
            _logger.LogWarning("Tagger GraphQL request failed: {Status}", response.StatusCode);
            return Array.Empty<string>();
        }
        return ScryfallTaggerParsers.ParseOracleTagsFromJson(response.Content);
    }

    /// <summary>
    /// Executes a single Tagger GraphQL POST with the supplied session credentials.
    /// Extracted so <see cref="RefreshSessionAndRetryAsync"/> can replay the same request shape
    /// against fresh credentials without duplicating logic.
    /// </summary>
    private async Task<RestResponse> ExecuteTaggerPostAsync(
        string set,
        string collectorNumber,
        TaggerSession session,
        CancellationToken cancellationToken)
    {
        var taggerRestClient = new RestClient(_taggerHttpClient.Inner);
        var graphqlRequest = new RestRequest("graphql", Method.Post);
        graphqlRequest.AddHeader("Cookie", session.CookieHeader);
        graphqlRequest.AddHeader("X-CSRF-Token", session.CsrfToken);

        var payload = JsonSerializer.Serialize(new
        {
            query = TaggerQuery,
            variables = new { set, number = collectorNumber }
        });
        graphqlRequest.AddStringBody(payload, ContentType.Json);

        return await _taggerPostPipeline.ExecuteAsync(
            async ct => await taggerRestClient.ExecuteAsync(graphqlRequest, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// HIGH-1 fix: On 403 from the tagger-post pipeline, invalidates the cached session,
    /// fetches a fresh session (new CSRF token + cookies), and retries the POST exactly once.
    /// A max-1-retry guard (_attemptedRefresh AsyncLocal) prevents infinite loops.
    /// Degrades to empty result if the retry also fails or returns 403.
    /// </summary>
    private async Task<IReadOnlyList<string>> RefreshSessionAndRetryAsync(
        string set,
        string collectorNumber,
        CancellationToken cancellationToken)
    {
        if (_attemptedRefresh.Value)
        {
            // Already retried once - prevent infinite loop, degrade gracefully.
            _logger.LogWarning("Tagger GraphQL 403 persisted after session refresh for {Set}/{Number}; degrading to empty", set, collectorNumber);
            _taggerSessionCache.Invalidate();
            return Array.Empty<string>();
        }

        _attemptedRefresh.Value = true;
        try
        {
            _taggerSessionCache.Invalidate();
            var freshSession = await FetchTaggerSessionAsync(set, collectorNumber, cancellationToken).ConfigureAwait(false);
            if (freshSession is null)
            {
                _logger.LogWarning("Tagger session refresh failed for {Set}/{Number}; degrading to empty", set, collectorNumber);
                return Array.Empty<string>();
            }

            _taggerSessionCache.Set(freshSession);

            var retryResponse = await ExecuteTaggerPostAsync(set, collectorNumber, freshSession, cancellationToken).ConfigureAwait(false);
            if (!retryResponse.IsSuccessful || string.IsNullOrEmpty(retryResponse.Content))
            {
                _logger.LogWarning("Tagger GraphQL retry failed: {Status}", retryResponse.StatusCode);
                _taggerSessionCache.Invalidate();
                return Array.Empty<string>();
            }

            return ScryfallTaggerParsers.ParseOracleTagsFromJson(retryResponse.Content);
        }
        finally
        {
            _attemptedRefresh.Value = false;
        }
    }
}
