using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MtgDeckStudio.Core.Models;

namespace MtgDeckStudio.Web.Services;

/// <summary>
/// A single confirmed or almost-confirmed combo from Commander Spellbook.
/// </summary>
public sealed record SpellbookCombo(
    IReadOnlyList<string> CardNames,
    IReadOnlyList<string> Results,
    string Instructions);

/// <summary>
/// A combo that is one card away from being complete in the submitted deck.
/// </summary>
public sealed record SpellbookAlmostCombo(
    string MissingCard,
    IReadOnlyList<string> CardsInDeck,
    IReadOnlyList<string> Results,
    string Instructions);

/// <summary>
/// The combo lookup result for a deck.
/// </summary>
public sealed record CommanderSpellbookResult(
    IReadOnlyList<SpellbookCombo> IncludedCombos,
    IReadOnlyList<SpellbookAlmostCombo> AlmostIncludedCombos);

/// <summary>
/// Looks up combos for a deck using the Commander Spellbook API.
/// </summary>
public interface ICommanderSpellbookService
{
    /// <summary>
    /// Returns combos that are fully in the deck and combos that are one card away,
    /// within the deck's color identity. Returns null if the API call fails.
    /// </summary>
    Task<CommanderSpellbookResult?> FindCombosAsync(
        IReadOnlyList<DeckEntry> entries,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches and caches combo data from the Commander Spellbook backend API.
/// </summary>
public sealed class CommanderSpellbookService : ICommanderSpellbookService
{
    private const string ApiUrl = "https://backend.commanderspellbook.com/find-my-combos";
    private const int MaxIncluded = 20;
    private const int MaxAlmostIncluded = 15;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<CommanderSpellbookService> _logger;
    private readonly Func<string, CancellationToken, Task<string?>> _postJsonAsync;

    /// <summary>
    /// Creates a service that calls the live Commander Spellbook API.
    /// </summary>
    public CommanderSpellbookService(
        IMemoryCache memoryCache,
        ILogger<CommanderSpellbookService>? logger = null,
        Func<string, CancellationToken, Task<string?>>? postJsonAsync = null)
    {
        _memoryCache = memoryCache;
        _logger = logger ?? NullLogger<CommanderSpellbookService>.Instance;
        _postJsonAsync = postJsonAsync ?? PostJsonAsync;
    }

    /// <inheritdoc/>
    public async Task<CommanderSpellbookResult?> FindCombosAsync(
        IReadOnlyList<DeckEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var commanders = entries
            .Where(e => string.Equals(e.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var main = entries
            .Where(e => string.Equals(e.Board, "mainboard", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(e.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (main.Count == 0)
        {
            return null;
        }

        var cacheKey = $"spellbook:{string.Join("|", commanders)}::{string.Join("|", main)}";
        if (_memoryCache.TryGetValue<CommanderSpellbookResult>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var requestBody = BuildRequestJson(commanders, main);
        string? responseJson;
        try
        {
            responseJson = await _postJsonAsync(requestBody, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Commander Spellbook API call failed; continuing without combo data.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        CommanderSpellbookResult? result;
        try
        {
            result = ParseResponse(responseJson, new HashSet<string>(main, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Commander Spellbook response; continuing without combo data.");
            return null;
        }

        if (result is not null)
        {
            _memoryCache.Set(cacheKey, result, CacheDuration);
        }

        return result;
    }

    internal static string BuildRequestJson(IReadOnlyList<string> commanders, IReadOnlyList<string> main)
    {
        var obj = new
        {
            commanders = commanders.Select(n => new { card = n, quantity = 1 }).ToList(),
            main = main.Where(n => !commanders.Contains(n, StringComparer.OrdinalIgnoreCase))
                       .Select(n => new { card = n, quantity = 1 })
                       .ToList()
        };
        return JsonSerializer.Serialize(obj);
    }

    internal static CommanderSpellbookResult? ParseResponse(string json, HashSet<string> deckCardNames)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results))
        {
            return null;
        }

        var included = results.TryGetProperty("included", out var inc)
            ? ParseVariants(inc).Take(MaxIncluded).ToList()
            : [];

        var almostIncluded = results.TryGetProperty("almostIncluded", out var almost)
            ? ParseAlmostVariants(almost, deckCardNames).Take(MaxAlmostIncluded).ToList()
            : [];

        return new CommanderSpellbookResult(included, almostIncluded);
    }

    private static IEnumerable<SpellbookCombo> ParseVariants(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var variant in array.EnumerateArray())
        {
            var cards = ExtractCardNames(variant);
            var results = ExtractResults(variant);
            var instructions = ExtractInstructions(variant);

            if (cards.Count == 0 || results.Count == 0)
            {
                continue;
            }

            yield return new SpellbookCombo(cards, results, instructions);
        }
    }

    private static IEnumerable<SpellbookAlmostCombo> ParseAlmostVariants(JsonElement array, HashSet<string> deckCardNames)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var variant in array.EnumerateArray())
        {
            var allCards = ExtractCardNames(variant);
            var results = ExtractResults(variant);
            var instructions = ExtractInstructions(variant);

            if (allCards.Count == 0 || results.Count == 0)
            {
                continue;
            }

            var missing = allCards.Where(c => !deckCardNames.Contains(c)).ToList();
            var inDeck = allCards.Where(c => deckCardNames.Contains(c)).ToList();

            // Only include if exactly one card is missing (one card away)
            if (missing.Count != 1)
            {
                continue;
            }

            yield return new SpellbookAlmostCombo(missing[0], inDeck, results, instructions);
        }
    }

    private static IReadOnlyList<string> ExtractCardNames(JsonElement variant)
    {
        if (!variant.TryGetProperty("uses", out var uses) || uses.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return uses.EnumerateArray()
            .Select(use => use.TryGetProperty("card", out var card)
                && card.TryGetProperty("name", out var name)
                ? name.GetString() ?? string.Empty
                : string.Empty)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
    }

    private static IReadOnlyList<string> ExtractResults(JsonElement variant)
    {
        if (!variant.TryGetProperty("produces", out var produces) || produces.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return produces.EnumerateArray()
            .Select(p => p.TryGetProperty("feature", out var feature)
                && feature.TryGetProperty("name", out var name)
                ? name.GetString() ?? string.Empty
                : string.Empty)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ExtractInstructions(JsonElement variant)
    {
        if (variant.TryGetProperty("description", out var desc))
        {
            var text = desc.GetString() ?? string.Empty;
            // Trim to first 300 chars to keep prompt size manageable
            return text.Length > 300 ? text[..300].TrimEnd() + "…" : text;
        }

        return string.Empty;
    }

    private static async Task<string?> PostJsonAsync(string requestBody, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(15);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MtgDeckStudio/1.0");

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(ApiUrl, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
