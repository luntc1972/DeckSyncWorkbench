using System.Net;
using System.Text.Json;
using Polly;
using Polly.Retry;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Normalization;

namespace DeckSyncWorkbench.Core.Integration;

public sealed class ArchidektApiDeckImporter : IArchidektDeckImporter
{
    private readonly HttpClient _httpClient;
    private static readonly AsyncRetryPolicy<HttpResponseMessage> RetryPolicy = Policy<HttpResponseMessage>
        .HandleResult(response => response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        .WaitAndRetryAsync(
            retryCount: 6,
            sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)),
            onRetry: (outcome, timespan, retryAttempt, context) => { });

    public ArchidektApiDeckImporter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
    {
        if (!ArchidektApiUrl.TryGetDeckId(urlOrDeckId, out var deckId))
        {
            throw new InvalidOperationException($"Unable to determine Archidekt deck id from: {urlOrDeckId}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, ArchidektApiUrl.BuildDeckApiUri(deckId));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        request.Headers.Referrer = new Uri($"https://archidekt.com/decks/{deckId}");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        using var response = await RetryPolicy.ExecuteAsync(() => _httpClient.SendAsync(request, cancellationToken));
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Archidekt API deck {deckId} returned {response.StatusCode}: {body[..Math.Min(body.Length, 500)]}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var entries = new List<DeckEntry>();

        if (!root.TryGetProperty("cards", out var cardsElement) || cardsElement.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }

        foreach (var item in cardsElement.EnumerateArray())
        {
            var quantity = item.GetProperty("quantity").GetInt32();
            if (quantity == 0)
            {
                continue;
            }

            var categories = item.TryGetProperty("categories", out var categoriesElement) && categoriesElement.ValueKind == JsonValueKind.Array
                ? categoriesElement.EnumerateArray().Where(cat => cat.ValueKind == JsonValueKind.String).Select(cat => cat.GetString()!).ToList()
                : [];

            var board = DetermineBoard(categories);
            var userCategories = categories
                .Where(category => !IsBoardCategory(category))
                .ToList();

            var card = item.GetProperty("card");
            var name = card.GetProperty("oracleCard").GetProperty("name").GetString()
                ?? card.GetProperty("displayName").GetString()
                ?? "Unknown";

            entries.Add(new DeckEntry
            {
                Name = name,
                NormalizedName = CardNormalizer.Normalize(name),
                Quantity = quantity,
                Board = board,
                SetCode = card.TryGetProperty("edition", out var editionElement) && editionElement.TryGetProperty("editioncode", out var editionCode)
                    ? editionCode.GetString()
                    : null,
                CollectorNumber = card.TryGetProperty("collectorNumber", out var collectorNumberElement)
                    ? collectorNumberElement.GetString()?.Replace("★", string.Empty, StringComparison.Ordinal)
                    : null,
                Category = userCategories.Count == 0 ? (board == "maybeboard" ? "Maybeboard" : null) : string.Join(",", userCategories),
                IsFoil = item.TryGetProperty("modifier", out var modifierElement)
                    && string.Equals(modifierElement.GetString(), "Foil", StringComparison.OrdinalIgnoreCase),
            });
        }

        return entries;
    }

    private static string DetermineBoard(List<string> categories)
    {
        if (categories.Any(category => string.Equals(category, "Commander", StringComparison.OrdinalIgnoreCase)))
        {
            return "commander";
        }

        if (categories.Any(category => string.Equals(category, "Maybeboard", StringComparison.OrdinalIgnoreCase)))
        {
            return "maybeboard";
        }

        if (categories.Any(category => string.Equals(category, "Sideboard", StringComparison.OrdinalIgnoreCase)))
        {
            return "maybeboard";
        }

        return "mainboard";
    }

    private static bool IsBoardCategory(string category)
    {
        return string.Equals(category, "Commander", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "Maybeboard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "Sideboard", StringComparison.OrdinalIgnoreCase);
    }
}
