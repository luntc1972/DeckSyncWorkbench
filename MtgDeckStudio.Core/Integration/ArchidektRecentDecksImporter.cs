using System.Net;
using System.Text.RegularExpressions;
using Polly;
using Polly.Retry;
using RestSharp;

namespace MtgDeckStudio.Core.Integration;

public interface IArchidektRecentDecksImporter
{
    Task<IReadOnlyList<string>> ImportRecentDeckIdsAsync(int count, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ImportRecentDeckIdsAsync(int count, int startPage, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ImportRecentDeckIdsPageAsync(int page, CancellationToken cancellationToken = default);
}

public sealed partial class ArchidektRecentDecksImporter : IArchidektRecentDecksImporter
{
    private readonly RestClient _restClient;
    private static readonly AsyncRetryPolicy<RestResponse> RetryPolicy = Policy<RestResponse>
        .HandleResult(response => response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        .WaitAndRetryAsync(
            retryCount: 4,
            sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)));

    /// <summary>
    /// Initializes the importer optionally using a provided RestClient.
    /// </summary>
    /// <param name="restClient">Optional REST client used for requests.</param>
    public ArchidektRecentDecksImporter(RestClient? restClient = null)
    {
        _restClient = restClient ?? new RestClient(new RestClientOptions
        {
            BaseUrl = new Uri("https://websockets.archidekt.com"),
            ThrowOnAnyError = false,
        });
    }

    /// <summary>
    /// Imports the requested number of recent public Archidekt deck IDs.
    /// </summary>
    /// <param name="count">Number of decks to collect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<string>> ImportRecentDeckIdsAsync(int count, CancellationToken cancellationToken = default)
    {
        return await ImportRecentDeckIdsAsync(count, 1, cancellationToken);
    }

    /// <summary>
    /// Imports the requested number of recent public Archidekt deck IDs starting from the supplied page.
    /// </summary>
    /// <param name="count">Number of decks to collect.</param>
    /// <param name="startPage">Page number to start crawling from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<string>> ImportRecentDeckIdsAsync(int count, int startPage, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var deckIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var page = Math.Max(1, startPage);

        while (deckIds.Count < count)
        {
            var pageIds = await ImportRecentDeckIdsPageAsync(page, cancellationToken);
            if (pageIds.Count == 0)
            {
                break;
            }

            foreach (var deckId in pageIds)
            {
                if (seen.Add(deckId))
                {
                    deckIds.Add(deckId);
                    if (deckIds.Count == count)
                    {
                        break;
                    }
                }
            }

            page += 1;
        }

        return deckIds;
    }

    /// <summary>
    /// Fetches a single page of recent public Archidekt deck IDs.
    /// </summary>
    /// <param name="page">Page index to request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<string>> ImportRecentDeckIdsPageAsync(int page, CancellationToken cancellationToken = default)
        => ImportRecentDeckIdsPageCoreAsync(page, cancellationToken);

    /// <summary>
    /// Fetches a page of recent deck IDs from Archidekt.
    /// </summary>
    /// <param name="page">Page index to request.</param>
    /// <param name="cancellationToken">Cancellation token for the HTTP call.</param>
    private async Task<IReadOnlyList<string>> ImportRecentDeckIdsPageCoreAsync(int page, CancellationToken cancellationToken)
    {
        var response = await RetryPolicy.ExecuteAsync(ct => _restClient.ExecuteAsync(CreatePageRequest(page), ct), cancellationToken);
        var body = response.Content ?? string.Empty;
        if (!response.IsSuccessful)
        {
            throw new HttpRequestException($"Archidekt recent decks page {page} returned {(int)response.StatusCode} {response.StatusDescription}");
        }

        return DeckLinkRegex()
            .Matches(body)
            .Select(match => match.Groups["deckId"].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Regular expression that matches Archidekt deck links in HTML responses.
    /// </summary>
    [GeneratedRegex("href=\"/decks/(?<deckId>\\d+)(?:/[^\"#?]*)?\"", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DeckLinkRegex();

    /// <summary>
    /// Builds the request used to scrape a page of public deck listings.
    /// </summary>
    /// <param name="page">Page index to request.</param>
    private static RestRequest CreatePageRequest(int page)
    {
        var request = new RestRequest($"/search/decks?name=&orderBy=-updatedAt&page={page}", Method.Get);
        request.AddHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36");
        request.AddHeader("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.AddHeader("Referer", "https://archidekt.com/");
        request.AddHeader("Accept-Language", "en-US,en;q=0.9");
        return request;
    }
}
