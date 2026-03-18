using System.Text.RegularExpressions;

namespace DeckSyncWorkbench.Core.Integration;

public sealed partial class ArchidektRecentDecksImporter
{
    private readonly HttpClient _httpClient;

    public ArchidektRecentDecksImporter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<string>> ImportRecentDeckIdsAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var deckIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;

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

    private async Task<IReadOnlyList<string>> ImportRecentDeckIdsPageAsync(int page, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://websockets.archidekt.com/search/decks?name=&orderBy=-updatedAt&page={page}");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.Referrer = new Uri("https://archidekt.com/");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        return DeckLinkRegex()
            .Matches(body)
            .Select(match => match.Groups["deckId"].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    [GeneratedRegex("href=\"/decks/(?<deckId>\\d+)(?:/[^\"#?]*)?\"", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DeckLinkRegex();
}
