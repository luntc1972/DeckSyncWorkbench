using System.Text.Json;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Normalization;

namespace DeckSyncWorkbench.Core.Integration;

public sealed class MoxfieldApiDeckImporter : IMoxfieldDeckImporter
{
    private readonly HttpClient _httpClient;

    public MoxfieldApiDeckImporter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
    {
        if (!MoxfieldApiUrl.TryGetDeckId(urlOrDeckId, out var deckId))
        {
            throw new InvalidOperationException($"Unable to determine Moxfield deck id from: {urlOrDeckId}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, MoxfieldApiUrl.BuildDeckApiUri(deckId));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        request.Headers.Referrer = new Uri("https://moxfield.com/");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var authorTags = ReadAuthorTags(root);
        var entries = new List<DeckEntry>();

        AddBoardEntries(root, "commanders", "commander", authorTags, entries);
        AddBoardEntries(root, "mainboard", "mainboard", authorTags, entries);
        AddBoardEntries(root, "maybeboard", "maybeboard", authorTags, entries);
        AddBoardEntries(root, "sideboard", "maybeboard", authorTags, entries);

        return entries;
    }

    private static Dictionary<string, string?> ReadAuthorTags(JsonElement root)
    {
        var tags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("authorTags", out var authorTagsElement) || authorTagsElement.ValueKind != JsonValueKind.Object)
        {
            return tags;
        }

        foreach (var property in authorTagsElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var values = property.Value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            tags[property.Name] = values.Count == 0 ? null : string.Join(",", values);
        }

        return tags;
    }

    private static void AddBoardEntries(JsonElement root, string propertyName, string board, Dictionary<string, string?> authorTags, List<DeckEntry> entries)
    {
        if (!root.TryGetProperty(propertyName, out var boardElement) || boardElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in boardElement.EnumerateObject())
        {
            var entry = property.Value;
            var quantity = entry.GetProperty("quantity").GetInt32();
            if (quantity == 0)
            {
                continue;
            }

            var card = entry.GetProperty("card");
            var name = card.GetProperty("name").GetString() ?? property.Name;
            authorTags.TryGetValue(name, out var category);

            entries.Add(new DeckEntry
            {
                Name = name,
                NormalizedName = CardNormalizer.Normalize(name),
                Quantity = quantity,
                Board = board,
                SetCode = card.TryGetProperty("set", out var setElement) ? setElement.GetString() : null,
                CollectorNumber = card.TryGetProperty("cn", out var cnElement) ? cnElement.GetString()?.Replace("★", string.Empty, StringComparison.Ordinal) : null,
                Category = string.IsNullOrWhiteSpace(category) ? (board == "maybeboard" ? "Maybeboard" : null) : category,
                IsFoil = entry.TryGetProperty("isFoil", out var foilElement) && foilElement.ValueKind == JsonValueKind.True,
            });
        }
    }
}
