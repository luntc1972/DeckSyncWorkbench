using System.Net;
using System.Text.Json;
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
    private const string TaggerGraphQlUrl = "https://tagger.scryfall.com/graphql";
    private const string TaggerBaseUrl = "https://tagger.scryfall.com";

    private static readonly string TaggerQuery =
        "query($set:String!,$number:String!){card:cardBySet(set:$set,number:$number){taggings{tag{name type slug}weight status}}}";

    private readonly RestClient _scryfallClient;
    private readonly ILogger<ScryfallTaggerService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ScryfallTaggerService"/>.
    /// </summary>
    /// <param name="logger">Logger used for upstream warnings.</param>
    public ScryfallTaggerService(ILogger<ScryfallTaggerService> logger)
    {
        _scryfallClient = ScryfallRestClientFactory.Create();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> LookupOracleTagsAsync(string cardName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);

        var (set, collectorNumber) = await ResolveCardPrintingAsync(cardName.Trim(), cancellationToken);
        if (string.IsNullOrEmpty(set) || string.IsNullOrEmpty(collectorNumber))
        {
            return [];
        }

        var (csrfToken, cookies) = await FetchCsrfTokenAsync(set, collectorNumber, cancellationToken);
        if (string.IsNullOrEmpty(csrfToken))
        {
            _logger.LogWarning("Unable to obtain CSRF token from Scryfall Tagger for {CardName}.", cardName);
            return [];
        }

        return await QueryTaggerGraphQlAsync(set, collectorNumber, csrfToken, cookies, cancellationToken);
    }

    /// <summary>
    /// Calls the Scryfall REST API to resolve a card name into its default set code and collector number.
    /// </summary>
    private async Task<(string Set, string CollectorNumber)> ResolveCardPrintingAsync(string cardName, CancellationToken cancellationToken)
    {
        var request = new RestRequest("cards/named", Method.Get);
        request.AddQueryParameter("exact", cardName);

        var response = await ScryfallThrottle.ExecuteAsync(
            token => _scryfallClient.ExecuteAsync(request, token),
            cancellationToken);
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
    /// Fetches a Tagger card page to obtain the CSRF token and session cookies required for the GraphQL endpoint.
    /// </summary>
    /// <param name="set">Card set code used by the Tagger card page.</param>
    /// <param name="collectorNumber">Collector number used by the Tagger card page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The CSRF token and cookies required for the GraphQL request.</returns>
    private static async Task<(string CsrfToken, CookieCollection? Cookies)> FetchCsrfTokenAsync(string set, string collectorNumber, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { UseCookies = true };
        using var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.Add("User-Agent", "DeckFlow/1.0");

        var pageUrl = $"{TaggerBaseUrl}/card/{set}/{collectorNumber}";
        var pageResponse = await httpClient.GetAsync(pageUrl, cancellationToken);
        if (!pageResponse.IsSuccessStatusCode)
        {
            return (string.Empty, null);
        }

        var html = await pageResponse.Content.ReadAsStringAsync(cancellationToken);
        var token = ScryfallTaggerParsers.TryExtractCsrfToken(html);
        if (string.IsNullOrEmpty(token))
        {
            return (string.Empty, null);
        }

        var cookies = handler.CookieContainer.GetCookies(new Uri(TaggerBaseUrl));
        return (token, cookies);
    }

    /// <summary>
    /// Posts the GraphQL query to the Tagger endpoint and extracts oracle tags from the response.
    /// </summary>
    /// <param name="set">Set code of the resolved printing.</param>
    /// <param name="collectorNumber">Collector number of the resolved printing.</param>
    /// <param name="csrfToken">CSRF token required by the Tagger endpoint.</param>
    /// <param name="cookies">Cookies captured from the Tagger card page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The distinct oracle tags returned by Scryfall Tagger.</returns>
    private async Task<IReadOnlyList<string>> QueryTaggerGraphQlAsync(
        string set,
        string collectorNumber,
        string csrfToken,
        CookieCollection? cookies,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { UseCookies = true };
        if (cookies is not null)
        {
            foreach (System.Net.Cookie cookie in cookies)
            {
                handler.CookieContainer.Add(cookie);
            }
        }

        using var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.Add("User-Agent", "DeckFlow/1.0");
        httpClient.DefaultRequestHeaders.Add("X-CSRF-Token", csrfToken);

        var payload = JsonSerializer.Serialize(new
        {
            query = TaggerQuery,
            variables = new { set, number = collectorNumber }
        });

        var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync(TaggerGraphQlUrl, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Tagger GraphQL request failed: {Status}", response.StatusCode);
            return [];
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ScryfallTaggerParsers.ParseOracleTagsFromJson(body);
    }
}
