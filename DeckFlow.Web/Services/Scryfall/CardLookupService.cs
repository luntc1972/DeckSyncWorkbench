using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Polly;
using Polly.Registry;
using RestSharp;
using DeckFlow.Web.Services.Http;
using DeckFlow.Web.Services.Scryfall;

namespace DeckFlow.Web.Services;

/// <summary>
/// Looks up pasted card names against Scryfall and returns formatted outputs plus missing lines.
/// </summary>
public interface ICardLookupService
{
    /// <summary>
    /// Looks up the provided card list using Scryfall.
    /// </summary>
    Task<CardLookupResult> LookupAsync(string cardList, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a single card and returns its formatted text plus detected mechanics.
    /// </summary>
    Task<SingleCardLookupResult?> LookupSingleAsync(string cardName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Returns the results of a card lookup.
/// </summary>
public sealed record CardLookupResult(IReadOnlyList<string> VerifiedOutputs, IReadOnlyList<string> MissingLines);

/// <summary>
/// Returns the result of a single-card lookup, including the resolved card name and detected mechanics.
/// </summary>
public sealed record SingleCardLookupResult(string CardName, string VerifiedText, IReadOnlyList<string> Mechanics);

/// <summary>
/// Looks up card lists via Scryfall's collection endpoint.
/// </summary>
public sealed class ScryfallCardLookupService : ICardLookupService
{
    private const int MaxCardsPerSubmission = 100;
    private static readonly Regex QuantityPrefixRegex = new(@"^(?<quantity>\d+)\s+(?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex AbilityWordRegex = new(@"^(?<term>[A-Za-z][A-Za-z' -]{1,40})\s+—\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingFoilMarkerRegex = new(@"\s+\*[A-Za-z]\*\s*$", RegexOptions.Compiled);
    private static readonly Regex TrailingSetCollectorRegex = new(@"\s+\([A-Za-z0-9]+\)\s+\S+\s*$", RegexOptions.Compiled);
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> _executeAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>> _executeSearchAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCard>>> _executeNamedAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallRulingsResponse>>> _executeRulingsAsync;
    private readonly CardLookupCache _cache;

    internal ScryfallCardLookupService(
        IScryfallRestClientFactory scryfallRestClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        CardLookupCache? cache = null,
        RestClient? restClientOverride = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeAsyncOverride = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeSearchAsyncOverride = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCard>>>? executeNamedAsyncOverride = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallRulingsResponse>>>? executeRulingsAsyncOverride = null)
    {
        ArgumentNullException.ThrowIfNull(scryfallRestClientFactory);
        ArgumentNullException.ThrowIfNull(pipelineProvider);
        _cache = cache ?? new CardLookupCache();
        var pipeline = pipelineProvider.GetPipeline<RestResponse>("scryfall") ?? ResiliencePipeline<RestResponse>.Empty;
        var client = restClientOverride ?? scryfallRestClientFactory.Create();
        _executeAsync = executeAsyncOverride ?? ((request, cancellationToken) =>
            ScryfallThrottle.ExecuteAsync(
                ScryfallEndpoint.Collection,
                token => pipeline.ExecuteAsync(
                    async pollyCt => await client.ExecuteAsync<ScryfallCollectionResponse>(request, pollyCt).ConfigureAwait(false),
                    token).AsTask(),
                cancellationToken));
        _executeSearchAsync = executeSearchAsyncOverride ?? ((request, cancellationToken) =>
            ScryfallThrottle.ExecuteAsync(
                ScryfallEndpoint.Search,
                token => pipeline.ExecuteAsync(
                    async pollyCt => await client.ExecuteAsync<ScryfallSearchResponse>(request, pollyCt).ConfigureAwait(false),
                    token).AsTask(),
                cancellationToken));
        _executeNamedAsync = executeNamedAsyncOverride ?? ((request, cancellationToken) =>
            ScryfallThrottle.ExecuteAsync(
                ScryfallEndpoint.Named,
                token => pipeline.ExecuteAsync(
                    async pollyCt => await client.ExecuteAsync<ScryfallCard>(request, pollyCt).ConfigureAwait(false),
                    token).AsTask(),
                cancellationToken));
        _executeRulingsAsync = executeRulingsAsyncOverride ?? ((request, cancellationToken) =>
            ScryfallThrottle.ExecuteAsync(
                ScryfallEndpoint.Rulings,
                token => pipeline.ExecuteAsync(
                    async pollyCt => await client.ExecuteAsync<ScryfallRulingsResponse>(request, pollyCt).ConfigureAwait(false),
                    token).AsTask(),
                cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<CardLookupResult> LookupAsync(string cardList, CancellationToken cancellationToken = default)
    {
        var parsedLines = ParseLines(cardList);
        if (parsedLines.Count > MaxCardsPerSubmission)
        {
            throw new InvalidOperationException($"Card Lookup accepts up to {MaxCardsPerSubmission} non-empty lines per submission.");
        }

        var resolvedCards = new Dictionary<string, ScryfallCard>(StringComparer.OrdinalIgnoreCase);
        var fallbackCards = new Dictionary<string, ScryfallCard>(StringComparer.OrdinalIgnoreCase);
        var missingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueNames = parsedLines
            .Select(line => line.CardName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var chunk in Chunk(uniqueNames, ScryfallLimits.CollectionBatchSize))
        {
            var request = new RestRequest("cards/collection", Method.Post);
            request.AddJsonBody(new
            {
                identifiers = chunk.Select(name => new { name }).ToArray()
            });

            var response = await _executeAsync(request, cancellationToken);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
            {
                throw new HttpRequestException(
                    $"Scryfall search returned HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            foreach (var card in response.Data.Data)
            {
                if (!string.IsNullOrWhiteSpace(card.Name))
                {
                    resolvedCards[NormalizeName(card.Name)] = card;
                }
            }

            foreach (var identifier in response.Data.NotFound ?? [])
            {
                if (!string.IsNullOrWhiteSpace(identifier.Name))
                {
                    missingNames.Add(NormalizeName(identifier.Name));
                }
            }
        }

        var verifiedOutputs = new List<string>(parsedLines.Count);
        var missingLines = new List<string>();
        var rulingsByCardId = new Dictionary<string, IReadOnlyList<ScryfallRuling>>(StringComparer.OrdinalIgnoreCase);

        foreach (var parsedLine in parsedLines)
        {
            var normalizedInput = NormalizeName(parsedLine.CardName);
            if (resolvedCards.TryGetValue(normalizedInput, out var card))
            {
                var rulings = await GetCachedRulingsAsync(card, rulingsByCardId, cancellationToken).ConfigureAwait(false);
                verifiedOutputs.Add(FormatCard(card, parsedLine.Quantity, rulings));
                continue;
            }

            if (missingNames.Contains(normalizedInput))
            {
                if (!fallbackCards.TryGetValue(normalizedInput, out var fallbackCard))
                {
                    fallbackCard = await SearchFallbackCardAsync(parsedLine.CardName, cancellationToken).ConfigureAwait(false);
                    if (fallbackCard is not null)
                    {
                        fallbackCards[normalizedInput] = fallbackCard;
                    }
                }

                if (fallbackCard is not null)
                {
                    var rulings = await GetCachedRulingsAsync(fallbackCard, rulingsByCardId, cancellationToken).ConfigureAwait(false);
                    verifiedOutputs.Add(FormatCard(fallbackCard, parsedLine.Quantity, rulings));
                    continue;
                }
            }

            missingLines.Add($"ERROR: {parsedLine.OriginalLine}");
        }

        return new CardLookupResult(verifiedOutputs, missingLines);
    }

    /// <inheritdoc/>
    public async Task<SingleCardLookupResult?> LookupSingleAsync(string cardName, CancellationToken cancellationToken = default)
    {
        var trimmedName = cardName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new InvalidOperationException("A card name is required.");
        }

        var request = new RestRequest("cards/collection", Method.Post);
        request.AddJsonBody(new
        {
            identifiers = new[] { new { name = trimmedName } }
        });

        var response = await _executeAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
        {
            throw new HttpRequestException(
                $"Scryfall search returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var resolvedCard = response.Data.Data.FirstOrDefault(card => !string.IsNullOrWhiteSpace(card.Name));
        if (resolvedCard is null)
        {
            var notFound = response.Data.NotFound?.Any(identifier =>
                string.Equals(NormalizeName(identifier.Name ?? string.Empty), NormalizeName(trimmedName), StringComparison.OrdinalIgnoreCase))
                ?? true;

            if (notFound)
            {
                resolvedCard = await SearchFallbackCardAsync(trimmedName, cancellationToken).ConfigureAwait(false);
            }
        }

        if (resolvedCard is null)
        {
            return null;
        }

        var rulings = await GetCachedRulingsAsync(resolvedCard, new Dictionary<string, IReadOnlyList<ScryfallRuling>>(StringComparer.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
        var mechanics = ExtractMechanicNames(resolvedCard)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SingleCardLookupResult(resolvedCard.Name, FormatCard(resolvedCard, null, rulings), mechanics);
    }

    private async Task<IReadOnlyList<ScryfallRuling>> GetCachedRulingsAsync(
        ScryfallCard card,
        Dictionary<string, IReadOnlyList<ScryfallRuling>> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(card.Id))
        {
            return Array.Empty<ScryfallRuling>();
        }

        if (cache.TryGetValue(card.Id, out var cached))
        {
            return cached;
        }

        var fetched = await FetchRulingsAsync(card.Id, cancellationToken).ConfigureAwait(false);
        cache[card.Id] = fetched;
        return fetched;
    }

    private async Task<IReadOnlyList<ScryfallRuling>> FetchRulingsAsync(string cardId, CancellationToken cancellationToken)
    {
        var request = new RestRequest($"cards/{cardId}/rulings", Method.Get);
        var response = await _executeRulingsAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
        {
            return Array.Empty<ScryfallRuling>();
        }

        return response.Data.Data ?? (IReadOnlyList<ScryfallRuling>)Array.Empty<ScryfallRuling>();
    }

    private static IEnumerable<List<string>> Chunk(IReadOnlyList<string> values, int size)
    {
        for (var index = 0; index < values.Count; index += size)
        {
            var count = Math.Min(size, values.Count - index);
            var chunk = new List<string>(count);
            for (var itemIndex = 0; itemIndex < count; itemIndex++)
            {
                chunk.Add(values[index + itemIndex]);
            }

            yield return chunk;
        }
    }

    private static string NormalizeName(string cardName)
        => CardNameNormalizer.Normalize(cardName);

    private async Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
    {
        if (_cache.TryGetCard(cardName, out var cached))
        {
            return cached;
        }

        foreach (var query in new[]
        {
            $"(printed:\"{cardName}\" OR name:\"{cardName}\")",
            cardName
        })
        {
            var request = new RestRequest("cards/search", Method.Get);
            request.AddQueryParameter("q", query);
            request.AddQueryParameter("unique", "prints");
            request.AddQueryParameter("include_multilingual", "true");

            var response = await _executeSearchAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
            {
                continue;
            }

            var match = response.Data.Data.FirstOrDefault();
            if (match is not null)
            {
                _cache.SetPositive(cardName, match);
                return match;
            }
        }

        var namedRequest = new RestRequest("cards/named", Method.Get);
        namedRequest.AddQueryParameter("fuzzy", cardName);
        var namedResponse = await _executeNamedAsync(namedRequest, cancellationToken).ConfigureAwait(false);
        if ((int)namedResponse.StatusCode >= 200 && (int)namedResponse.StatusCode < 300 && namedResponse.Data is not null)
        {
            _cache.SetPositive(cardName, namedResponse.Data);
            return namedResponse.Data;
        }

        _cache.SetNegative(cardName);
        return null;
    }

    private static string FormatCard(ScryfallCard card, int? quantity, IReadOnlyList<ScryfallRuling> rulings)
    {
        var sections = new List<string>();
        sections.Add(quantity.HasValue ? $"{quantity.Value} {card.Name}" : card.Name);
        if (!string.IsNullOrWhiteSpace(card.ManaCost))
        {
            sections.Add(card.ManaCost);
        }

        sections.Add(card.TypeLine);

        if (!string.IsNullOrWhiteSpace(card.OracleText))
        {
            sections.Add(string.Empty);
            sections.Add(card.OracleText);
        }

        if (!string.IsNullOrWhiteSpace(card.Power) && !string.IsNullOrWhiteSpace(card.Toughness))
        {
            sections.Add($"{card.Power}/{card.Toughness}");
        }

        sections.Add(string.Empty);
        sections.Add("Rulings:");
        var printedRuling = false;
        foreach (var ruling in rulings)
        {
            if (string.IsNullOrWhiteSpace(ruling.Comment))
            {
                continue;
            }

            var datePrefix = string.IsNullOrWhiteSpace(ruling.PublishedAt) ? string.Empty : $"({ruling.PublishedAt}) ";
            sections.Add($"- {datePrefix}{ruling.Comment}");
            printedRuling = true;
        }

        if (!printedRuling)
        {
            sections.Add("No rulings on record for this card.");
        }

        return string.Join(Environment.NewLine, sections);
    }

    private static IEnumerable<string> ExtractMechanicNames(ScryfallCard card)
    {
        foreach (var keyword in card.Keywords ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                yield return keyword.Trim();
            }
        }

        foreach (var oracleText in EnumerateOracleText(card))
        {
            foreach (var line in oracleText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine))
                {
                    continue;
                }

                var abilityWordMatch = AbilityWordRegex.Match(trimmedLine);
                if (abilityWordMatch.Success)
                {
                    yield return abilityWordMatch.Groups["term"].Value.Trim();
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateOracleText(ScryfallCard card)
    {
        if (!string.IsNullOrWhiteSpace(card.OracleText))
        {
            yield return card.OracleText;
        }

        foreach (var face in card.CardFaces ?? Array.Empty<ScryfallCardFace>())
        {
            if (!string.IsNullOrWhiteSpace(face.OracleText))
            {
                yield return face.OracleText;
            }
        }
    }

    private static List<ParsedCardLine> ParseLines(string cardList)
    {
        var parsedLines = new List<ParsedCardLine>();
        using var reader = new StringReader(cardList ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var match = QuantityPrefixRegex.Match(trimmed);
            if (match.Success)
            {
                parsedLines.Add(new ParsedCardLine(
                    trimmed,
                    SanitizeCardName(match.Groups["name"].Value.Trim()),
                    int.Parse(match.Groups["quantity"].Value)));
                continue;
            }

            parsedLines.Add(new ParsedCardLine(trimmed, SanitizeCardName(trimmed), null));
        }

        return parsedLines;
    }

    /// <summary>
    /// Strips Archidekt-export decorations from a raw card name so it matches Scryfall's
    /// /cards/collection identifier exactly.
    /// Order: foil/etched markers first (outermost), then (SET) COLLECTOR_NUMBER,
    /// then DFC slash normalisation, then trim.
    /// </summary>
    private static string SanitizeCardName(string raw)
    {
        var name = raw;

        // 1. Strip trailing *F* / *E* / other single-letter export markers (repeat in case of stacked suffixes).
        while (TrailingFoilMarkerRegex.IsMatch(name))
        {
            name = TrailingFoilMarkerRegex.Replace(name, string.Empty);
        }

        // 2. Strip trailing (SET) COLLECTOR_NUMBER  e.g. "(CMR) 84", "(ZNR) 174", "(MKM-174) 289s".
        name = TrailingSetCollectorRegex.Replace(name, string.Empty);

        // 3. Normalise DFC face separator: " / " → " // " so Scryfall recognises double-faced card names.
        name = name.Replace(" / ", " // ");

        return name.Trim();
    }

    private sealed record ParsedCardLine(string OriginalLine, string CardName, int? Quantity);
}

/// <summary>
/// Shared normalisation helper for Scryfall card names: lowercases and collapses
/// smart-quotes, curly apostrophes, and em/en-dashes to their ASCII equivalents.
/// Used by both <see cref="ScryfallCardLookupService"/> and <see cref="CardLookupCache"/>.
/// </summary>
internal static class CardNameNormalizer
{
    /// <summary>
    /// Returns a normalised form of <paramref name="cardName"/> suitable for use as a cache key.
    /// </summary>
    /// <param name="cardName">The raw card name.</param>
    /// <returns>Lowercased card name with Unicode punctuation collapsed to ASCII equivalents.</returns>
    internal static string Normalize(string cardName)
        => cardName
            .Trim()
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('ʼ', '\'')
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('–', '-')
            .Replace('—', '-')
            .ToLowerInvariant();
}
