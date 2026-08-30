using System.Net;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Http;
using Polly;
using Polly.Registry;
using RestSharp;
using CoreScryfallCollectionIdentifier = DeckFlow.Core.Normalization.ScryfallCollectionIdentifier;

namespace DeckFlow.Web.Services.Scryfall;

/// <summary>
/// Resolves Scryfall card references for packet services.
/// </summary>
public interface IScryfallCardResolver
{
    /// <summary>
    /// Executes a single Scryfall collection request and returns the raw response.
    /// </summary>
    Task<RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Performs the shared exact-name fallback search used when collection lookup misses a card.
    /// </summary>
    Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken);

    /// <summary>
    /// Performs the analysis-specific printed-name fallback search used when collection lookup misses a card.
    /// </summary>
    Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken);

    /// <summary>
    /// Batch-capable sibling of <see cref="SearchFallbackCardAsync"/>: resolves many exact-name misses
    /// with as few <c>cards/search</c> requests as possible. Returns <c>null</c> (not an empty list)
    /// only when the batched query itself was rejected (HTTP 400) -- a caller must then degrade to
    /// per-card resolution over the SAME name list rather than treating a 400 as "nothing matched".
    /// Every other outcome (including a 404, meaning every term missed) returns a non-null,
    /// possibly-empty list. The default body preserves today's exact behavior for every implementer
    /// that does not override it: it loops <see cref="SearchFallbackCardAsync"/> once per name, so
    /// existing test doubles keep their current per-name call-count assertions with zero edits.
    /// </summary>
    async Task<IReadOnlyList<ScryfallCard>?> SearchFallbackCardsAsync(IReadOnlyList<string> cardNames, CancellationToken cancellationToken)
    {
        var results = new List<ScryfallCard>();
        foreach (var cardName in cardNames)
        {
            var card = await SearchFallbackCardAsync(cardName, cancellationToken).ConfigureAwait(false);
            if (card is not null)
            {
                results.Add(card);
            }
        }

        return results;
    }

    /// <summary>
    /// Executes a strict fuzzy <c>/cards/named</c> lookup and returns the raw response for downstream 404-body inspection.
    /// </summary>
    Task<RestResponse<ScryfallCard>> ExecuteNamedFuzzyAsync(string cardName, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"{nameof(ExecuteNamedFuzzyAsync)} requires a concrete {nameof(ScryfallCardResolver)} implementation.");

    /// <summary>
    /// Resolves a single card by name: an exact collection lookup with a normalized-name match, falling
    /// back to the exact-name search when the collection misses. Returns null when nothing matches.
    /// </summary>
    Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken);
}

/// <summary>
/// Executes Scryfall collection and fallback-search requests through the shared throttle and resilience pipeline.
/// </summary>
public sealed class ScryfallCardResolver : IScryfallCardResolver
{
    // Why 60, not the collection path's 75: cards/search is a GET, so the batch is bounded by URL
    // length rather than the collection endpoint's JSON body. See docs/decisions/0004 and 06-CONTEXT.md.
    private const int SearchFallbackChunkSize = 60;

    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> _executeCollectionAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>> _executeSearchAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCard>>> _executeNamedAsync;
    private readonly ScryfallCollectionCardCache _collectionCardCache;

    /// <summary>
    /// Creates a resolver using the DI-managed Scryfall client factory and resilience pipeline.
    /// </summary>
    public ScryfallCardResolver(
        IScryfallRestClientFactory scryfallRestClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        ScryfallCollectionCardCache collectionCardCache)
        : this(
            scryfallRestClientFactory,
            pipelineProvider,
            null,
            null,
            null,
            null,
            collectionCardCache)
    {
    }

    internal ScryfallCardResolver(
        IScryfallRestClientFactory scryfallRestClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        RestClient? restClientOverride = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsyncOverride = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeSearchAsyncOverride = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCard>>>? executeNamedAsyncOverride = null,
        ScryfallCollectionCardCache? collectionCardCache = null)
    {
        ArgumentNullException.ThrowIfNull(scryfallRestClientFactory);
        ArgumentNullException.ThrowIfNull(pipelineProvider);
        // Why: this internal constructor is a test seam; production construction must supply the DI-managed singleton.
        _collectionCardCache = collectionCardCache ?? new ScryfallCollectionCardCache();
        var pipeline = pipelineProvider.GetPipeline<RestResponse>("scryfall") ?? ResiliencePipeline<RestResponse>.Empty;
        var client = restClientOverride ?? scryfallRestClientFactory.Create();
        _executeCollectionAsync = executeCollectionAsyncOverride ?? ((request, cancellationToken) =>
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
    }

    /// <inheritdoc/>
    public Task<RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _executeCollectionAsync(request, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<RestResponse<ScryfallCard>> ExecuteNamedFuzzyAsync(string cardName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);

        var request = new RestRequest("cards/named", Method.Get);
        request.AddQueryParameter("fuzzy", NormalizeForScryfall(cardName));

        var response = await _executeNamedAsync(request, cancellationToken).ConfigureAwait(false);
        ScryfallThrottle.ThrowIfUpstreamUnavailable(response.StatusCode);
        return response;
    }

    /// <inheritdoc/>
    public async Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cardName))
        {
            return null;
        }

        string collectionIdentifier = CoreScryfallCollectionIdentifier.ToFaceIdentifier(cardName);
        if (_collectionCardCache.TryGetName(collectionIdentifier, out var cachedCard))
        {
            if (cachedCard is not null
                && string.Equals(CardNormalizer.Normalize(cachedCard.Name), CardNormalizer.Normalize(cardName), StringComparison.Ordinal))
            {
                return cachedCard;
            }

            if (cachedCard is null)
            {
                return await SearchFallbackCardAsync(cardName, cancellationToken).ConfigureAwait(false);
            }
        }

        var request = new RestRequest("cards/collection", Method.Post);
        // Why: Scryfall cards/collection name identifiers match a single face name; combined A // B returns not_found.
        request.AddJsonBody(new { identifiers = new object[] { new { name = collectionIdentifier } } });

        RestResponse<ScryfallCollectionResponse> response =
            await ExecuteCollectionAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices && response.Data?.Data.Count > 0)
        {
            ScryfallCard? hit = response.Data.Data.FirstOrDefault(card =>
                string.Equals(CardNormalizer.Normalize(card.Name), CardNormalizer.Normalize(cardName), StringComparison.Ordinal));
            if (hit is not null)
            {
                _collectionCardCache.SetNamePositive(collectionIdentifier, hit);
                return hit;
            }
        }

        if (response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices
            && response.Data?.NotFound?.Any(identifier => string.Equals(identifier.Name, collectionIdentifier, StringComparison.Ordinal)) == true)
        {
            _collectionCardCache.SetNameCollectionMiss(collectionIdentifier);
        }

        return await SearchFallbackCardAsync(cardName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
    {
        var normalizedName = cardName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        var request = new RestRequest("cards/search", Method.Get);
        request.AddQueryParameter("q", $"!\"{normalizedName}\"");
        request.AddQueryParameter("unique", "cards");
        request.AddQueryParameter("order", "name");

        var response = await _executeSearchAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
        {
            return response.Data?.Data.FirstOrDefault();
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        throw new HttpRequestException(
            $"Scryfall fallback lookup failed while resolving {cardName} with HTTP {(int)response.StatusCode}.",
            null,
            response.StatusCode);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ScryfallCard>?> SearchFallbackCardsAsync(IReadOnlyList<string> cardNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cardNames);

        var trimmedNames = cardNames
            .Select(name => name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();

        if (trimmedNames.Count == 0)
        {
            return Array.Empty<ScryfallCard>();
        }

        var results = new List<ScryfallCard>();
        foreach (var chunk in ChunkFallbackNames(trimmedNames, SearchFallbackChunkSize))
        {
            var query = string.Join(" or ", chunk.Select(name => $"!\"{name}\""));
            var request = new RestRequest("cards/search", Method.Get);
            request.AddQueryParameter("q", query);
            request.AddQueryParameter("unique", "cards");
            request.AddQueryParameter("order", "name");

            var response = await _executeSearchAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
            {
                if (response.Data?.Data is { Count: > 0 } cards)
                {
                    results.AddRange(cards);
                }

                continue;
            }

            // Why: a search resource only 404s when EVERY term in the query missed. That is a
            // legitimate all-miss outcome, not a rejected request, so it contributes zero cards to
            // this chunk rather than degrading the whole batch.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            // Why 400 returns the sentinel rather than throwing: a single malformed term must not
            // cost the other names in this call their resolutions. The 400 sentinel signals the
            // caller (MatchChunkAsync) to degrade the WHOLE still-unresolved set to the per-card
            // path, which is the documented worst case of N+1 requests, never fewer resolutions
            // than the per-card path alone would have produced.
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return null;
            }

            // Why this reuses SearchFallbackCardAsync's exact message template rather than a new
            // "batch" wording: consuming services (e.g. DeckComparisonService, WR-01) rely on this
            // substring to decide that a fallback-SEARCH failure is NOT a cards/collection-call
            // failure, so it must propagate with its ORIGINAL, non-deck-labeled message exactly as
            // the per-name method's failure does today.
            throw new HttpRequestException(
                $"Scryfall fallback lookup failed while resolving {string.Join(", ", chunk)} with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        return results;
    }

    private static IEnumerable<List<string>> ChunkFallbackNames(IReadOnlyList<string> values, int size)
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

    /// <inheritdoc/>
    public async Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cardName))
        {
            return null;
        }

        var normalizedCardName = NormalizeLookupName(cardName);
        foreach (var query in new[]
        {
            $"(printed:\"{NormalizeForScryfall(cardName)}\" OR name:\"{NormalizeForScryfall(cardName)}\")",
            NormalizeForScryfall(cardName)
        })
        {
            var request = new RestRequest("cards/search", Method.Get);
            request.AddQueryParameter("q", query);
            request.AddQueryParameter("unique", "prints");
            request.AddQueryParameter("include_multilingual", "true");

            var response = await _executeSearchAsync(request, cancellationToken).ConfigureAwait(false);
            ScryfallThrottle.ThrowIfUpstreamUnavailable(response.StatusCode);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
            {
                continue;
            }

            var match = response.Data.Data
                .FirstOrDefault(card => NormalizeLookupName(card.Name) == normalizedCardName)
                ?? response.Data.Data.FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        var namedRequest = new RestRequest("cards/named", Method.Get);
        namedRequest.AddQueryParameter("fuzzy", NormalizeForScryfall(cardName));
        var namedResponse = await _executeNamedAsync(namedRequest, cancellationToken).ConfigureAwait(false);
        ScryfallThrottle.ThrowIfUpstreamUnavailable(namedResponse.StatusCode);
        if ((int)namedResponse.StatusCode >= 200 && (int)namedResponse.StatusCode < 300 && namedResponse.Data is not null)
        {
            return namedResponse.Data;
        }

        return null;
    }

    /// <summary>
    /// Normalizes a card name for equality comparisons across quote, apostrophe, and dash variants.
    /// </summary>
    public static string NormalizeLookupName(string cardName)
        => cardName
            .Trim()
            .Replace('\u2019', '\'')
            .Replace('\u2018', '\'')
            .Replace('\u02BC', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .ToLowerInvariant();

    /// <summary>
    /// Normalizes a card name for use in Scryfall API payloads.
    /// Converts the single-slash DFC separator used by Archidekt exports (" / ") to the
    /// double-slash form required by Scryfall <c>cards/search</c> and <c>cards/named</c>.
    /// This changes only the identifier SUBMITTED to Scryfall and never the key a batch resolver
    /// matches returned cards back on.
    /// Verified live on 2026-07-28: <c>cards/collection</c> rejects the combined form and must
    /// instead use <see cref="CoreScryfallCollectionIdentifier.ToFaceIdentifier(string)"/>.
    /// DeckEntry.Name is NOT modified — normalization happens only at the call site.
    /// </summary>
    /// <remarks>
    /// A live probe (<c>111.1-REVIEWS.md</c> §0, 2026-07-31) showed <c>/cards/collection</c>
    /// resolves NEITHER slash form of a combined multiface name — so no submission spelling makes
    /// a DFC name resolve there; it reaches its card through the search fallback either way. See
    /// <c>docs/decisions/0004-scryfall-batch-match-key-asymmetry.md</c>.
    /// </remarks>
    public static string NormalizeForScryfall(string cardName)
        => cardName.Replace(" / ", " // ");
}
