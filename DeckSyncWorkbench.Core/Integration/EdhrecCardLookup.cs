using System.Text.Json;
using System.Text.RegularExpressions;
using DeckSyncWorkbench.Core.Reporting;

namespace DeckSyncWorkbench.Core.Integration;

public sealed partial class EdhrecCardLookup
{
    private readonly HttpClient _httpClient;

    public EdhrecCardLookup(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<string>> LookupCategoriesAsync(string cardName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildCardUri(cardName));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        request.Headers.Referrer = new Uri("https://edhrec.com/");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("panels", out var panelsElement) || panelsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var panel in panelsElement.EnumerateArray())
        {
            if (panel.TryGetProperty("tag", out var tagElement) && tagElement.ValueKind == JsonValueKind.String)
            {
                var tag = NormalizeCategory(tagElement.GetString());
                if (CategoryFilter.IsIncluded(tag))
                {
                    categories.Add(tag!);
                }
            }
        }

        return categories.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Uri BuildCardUri(string cardName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);
        return new Uri($"https://json.edhrec.com/pages/cards/{Slugify(cardName)}.json", UriKind.Absolute);
    }

    public static string Slugify(string cardName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);

        var compact = cardName.Trim().ToLowerInvariant();
        compact = compact.Replace("//", " ");
        compact = compact.Replace('/', ' ');
        compact = NonSlugCharactersRegex().Replace(compact, string.Empty);
        compact = WhitespaceRegex().Replace(compact, "-");
        return compact.Trim('-');
    }

    private static string? NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        var text = category.Replace('-', ' ').Trim();
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    [GeneratedRegex("[^a-z0-9\\s-]", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex NonSlugCharactersRegex();

    [GeneratedRegex("\\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
