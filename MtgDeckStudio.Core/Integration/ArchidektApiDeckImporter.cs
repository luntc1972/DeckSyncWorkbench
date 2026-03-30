using System.Net;
using System.Text.Json;
using Polly;
using Polly.Retry;
using RestSharp;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Normalization;

namespace MtgDeckStudio.Core.Integration;

public sealed class ArchidektApiDeckImporter : IArchidektDeckImporter
{
    private readonly RestClient _restClient;
    private static readonly AsyncRetryPolicy<RestResponse> RetryPolicy = Policy<RestResponse>
        .HandleResult(response => response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        .WaitAndRetryAsync(
            retryCount: 6,
            sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)),
            onRetry: (outcome, timespan, retryAttempt, context) => { });

    /// <summary>
    /// Initializes the Archidekt importer with an optional RestClient instance.
    /// </summary>
    /// <param name="restClient">Optional REST client for test injection.</param>
    public ArchidektApiDeckImporter(RestClient? restClient = null)
    {
        _restClient = restClient ?? new RestClient(new RestClientOptions
        {
            BaseUrl = new Uri("https://archidekt.com"),
            ThrowOnAnyError = false,
        });
    }

    /// <summary>
    /// Imports deck entries from an Archidekt deck, preserving categories and boards.
    /// </summary>
    /// <param name="urlOrDeckId">Deck URL or ID.</param>
    /// <param name="cancellationToken">Cancellation token for the request.</param>
    public async Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
    {
        if (!ArchidektApiUrl.TryGetDeckId(urlOrDeckId, out var deckId))
        {
            throw new InvalidOperationException($"Unable to determine Archidekt deck id from: {urlOrDeckId}");
        }

        var response = await RetryPolicy.ExecuteAsync(ct => _restClient.ExecuteAsync(CreateDeckRequest(deckId), ct), cancellationToken);
        var body = response.Content ?? string.Empty;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Archidekt API deck {deckId} returned {(int)response.StatusCode} {response.StatusDescription}: {body[..Math.Min(body.Length, 500)]}");
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

    /// <summary>
    /// Builds the project REST request for fetching the deck payload.
    /// </summary>
    /// <param name="deckId">Target deck identifier.</param>
    private static RestRequest CreateDeckRequest(string deckId)
    {
        var request = new RestRequest($"api/decks/{deckId}/", Method.Get);
        request.AddHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36");
        request.AddHeader("Accept", "application/json, text/plain, */*");
        request.AddHeader("Referer", $"https://archidekt.com/decks/{deckId}");
        request.AddHeader("Accept-Language", "en-US,en;q=0.9");
        return request;
    }

    /// <summary>
    /// Determines which board a card belongs to based on its category list.
    /// </summary>
    /// <param name="categories">List of Archidekt categories attached to the card.</param>
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

    /// <summary>
    /// Checks whether the provided category maps to a board designation.
    /// </summary>
    /// <param name="category">Category string to evaluate.</param>
    private static bool IsBoardCategory(string category)
    {
        return string.Equals(category, "Commander", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "Maybeboard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "Sideboard", StringComparison.OrdinalIgnoreCase);
    }
}
