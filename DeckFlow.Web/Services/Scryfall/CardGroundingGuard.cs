using System.Net;
using System.Text.Json;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;

namespace DeckFlow.Web.Services.Scryfall;

/// <summary>
/// Strict Scryfall-backed guard that validates candidate cards against deck-context safety rules.
/// </summary>
public sealed class CardGroundingGuard(IScryfallCardResolver resolver, IMemoryCache cache) : ICardGroundingGuard
{
    private const string CacheKeyPrefix = "card-grounding-guard:";

    /// <inheritdoc />
    public async Task<CardGroundingVerdict> TryValidateAsync(
        string candidateName,
        CardGroundingDeckContext deckContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deckContext);

        if (string.IsNullOrWhiteSpace(candidateName))
        {
            return CreateRejectedVerdict(candidateName, CardGroundingRejectReason.NotFound);
        }

        CardGroundingBatchResult result = await ValidateAllAsync([candidateName], deckContext, cancellationToken).ConfigureAwait(false);
        return result.Verdicts[0];
    }

    /// <inheritdoc />
    public async Task<CardGroundingBatchResult> ValidateAllAsync(
        IReadOnlyList<string> candidateNames,
        CardGroundingDeckContext deckContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateNames);
        ArgumentNullException.ThrowIfNull(deckContext);

        if (candidateNames.Count == 0)
        {
            return new CardGroundingBatchResult
            {
                Verdicts = [],
                HasUpstreamFailure = false,
            };
        }

        var resolutions = await LoadBatchResolutionsAsync(candidateNames, cancellationToken).ConfigureAwait(false);
        var verdicts = new List<CardGroundingVerdict>(candidateNames.Count);
        foreach (var candidateName in candidateNames)
        {
            if (string.IsNullOrWhiteSpace(candidateName))
            {
                verdicts.Add(CreateRejectedVerdict(candidateName, CardGroundingRejectReason.NotFound));
                continue;
            }

            verdicts.Add(CreateVerdict(candidateName, resolutions[candidateName], deckContext));
        }

        return new CardGroundingBatchResult
        {
            Verdicts = verdicts,
            HasUpstreamFailure = verdicts.Any(verdict => verdict.RejectReason == CardGroundingRejectReason.UpstreamUnavailable),
        };
    }

    private async Task<Dictionary<string, CardResolution>> LoadBatchResolutionsAsync(
        IReadOnlyList<string> candidateNames,
        CancellationToken cancellationToken)
    {
        var resolutions = new Dictionary<string, CardResolution>(candidateNames.Count, StringComparer.Ordinal);
        var uniqueCandidates = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidateName in candidateNames)
        {
            if (string.IsNullOrWhiteSpace(candidateName))
            {
                continue;
            }

            if (resolutions.ContainsKey(candidateName))
            {
                continue;
            }

            string cacheKey = CachedNameResolution.BuildCacheKey(CacheKeyPrefix, candidateName);
            if (cache.TryGetValue<CardResolution>(cacheKey, out var cachedResolution))
            {
                resolutions[candidateName] = cachedResolution!;
                continue;
            }

            if (seenKeys.Add(cacheKey))
            {
                uniqueCandidates.Add(candidateName);
            }
        }

        foreach (var batch in ScryfallBatching.Chunk(uniqueCandidates, ScryfallLimits.CollectionBatchSize))
        {
            await ResolveBatchChunkAsync(batch, resolutions, cancellationToken).ConfigureAwait(false);
        }

        foreach (var candidateName in candidateNames)
        {
            if (string.IsNullOrWhiteSpace(candidateName) || resolutions.ContainsKey(candidateName))
            {
                continue;
            }

            if (TryGetCachedResolution(candidateName, out CardResolution cachedResolution))
            {
                resolutions[candidateName] = cachedResolution;
                continue;
            }

            resolutions[candidateName] = await GetOrFetchResolutionAsync(candidateName, cancellationToken).ConfigureAwait(false);
        }

        return resolutions;
    }

    private async Task ResolveBatchChunkAsync(
        IReadOnlyList<string> candidateNames,
        IDictionary<string, CardResolution> resolutions,
        CancellationToken cancellationToken)
    {
        if (candidateNames.Count == 0)
        {
            return;
        }

        try
        {
            var request = new RestRequest("cards/collection", Method.Post);
            request.AddJsonBody(new
            {
                identifiers = candidateNames.Select(name => new { name }).ToArray(),
            });

            RestResponse<ScryfallCollectionResponse> response =
                await resolver.ExecuteCollectionAsync(request, cancellationToken).ConfigureAwait(false);

            ScryfallThrottle.ThrowIfUpstreamUnavailable(response.StatusCode);
            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices || response.Data is null)
            {
                throw new HttpRequestException(
                    $"Scryfall card lookup (cards/collection) returned HTTP {(int)response.StatusCode} during card grounding.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            var matchedCardsByInput = candidateNames.ToDictionary(
                name => name,
                _ => (ScryfallCard?)null,
                StringComparer.Ordinal);

            foreach (var card in response.Data.Data)
            {
                var matchedInput = candidateNames.FirstOrDefault(candidateName =>
                    string.Equals(CardNormalizer.Normalize(card.Name), CardNormalizer.Normalize(candidateName), StringComparison.Ordinal));
                if (matchedInput is not null)
                {
                    matchedCardsByInput[matchedInput] = card;
                }
            }

            foreach (var candidateName in candidateNames)
            {
                if (matchedCardsByInput[candidateName] is { } exactCard)
                {
                    var exactResolution = CreateResolvedCard(exactCard);
                    CacheResolution(candidateName, exactResolution);
                    resolutions[candidateName] = exactResolution;
                    continue;
                }

                var fuzzyResolution = await ResolveFuzzyOnlyAsync(candidateName, cancellationToken).ConfigureAwait(false);
                if (fuzzyResolution.ResolutionReason != CardGroundingRejectReason.UpstreamUnavailable)
                {
                    CacheResolution(candidateName, fuzzyResolution);
                }

                resolutions[candidateName] = fuzzyResolution;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            foreach (var candidateName in candidateNames)
            {
                resolutions[candidateName] = CreateUnresolvedCard(candidateName, CardGroundingRejectReason.UpstreamUnavailable);
            }
        }
    }

    private bool TryGetCachedResolution(string candidateName, out CardResolution resolution)
    {
        string cacheKey = CachedNameResolution.BuildCacheKey(CacheKeyPrefix, candidateName);
        if (cache.TryGetValue<CardResolution>(cacheKey, out var cachedResolution))
        {
            resolution = cachedResolution!;
            return true;
        }

        resolution = null!;
        return false;
    }

    private Task<CardResolution> GetOrFetchResolutionAsync(string candidateName, CancellationToken cancellationToken)
        => CachedNameResolution.GetOrAddAsync(
            cache,
            CacheKeyPrefix,
            candidateName,
            async ct =>
            {
                var request = new RestRequest("cards/collection", Method.Post);
                request.AddJsonBody(new { identifiers = new object[] { new { name = candidateName } } });

                RestResponse<ScryfallCollectionResponse> response =
                    await resolver.ExecuteCollectionAsync(request, ct).ConfigureAwait(false);

                ScryfallThrottle.ThrowIfUpstreamUnavailable(response.StatusCode);
                if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices || response.Data is null)
                {
                    throw new HttpRequestException(
                        $"Scryfall card lookup (cards/collection) returned HTTP {(int)response.StatusCode} during card grounding.",
                        inner: null,
                        statusCode: response.StatusCode);
                }

                ScryfallCard? exactCard = response.Data.Data.FirstOrDefault(card =>
                    string.Equals(CardNormalizer.Normalize(card.Name), CardNormalizer.Normalize(candidateName), StringComparison.Ordinal));
                if (exactCard is not null)
                {
                    return CreateResolvedCard(exactCard);
                }

                return await ResolveFuzzyOnlyAsync(candidateName, ct).ConfigureAwait(false);
            },
            _ => CreateUnresolvedCard(candidateName, CardGroundingRejectReason.UpstreamUnavailable),
            static resolution => resolution.ResolutionReason switch
            {
                CardGroundingRejectReason.None => CachedNameResolution.PositiveCacheTtl,
                CardGroundingRejectReason.UpstreamUnavailable => null,
                _ => CachedNameResolution.NegativeCacheTtl,
            },
            cancellationToken);

    private async Task<CardResolution> ResolveFuzzyOnlyAsync(string candidateName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await resolver.ExecuteNamedFuzzyAsync(candidateName, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return CreateUnresolvedCard(candidateName, GetNotFoundReason(response.Content));
            }

            if (response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices && response.Data is not null)
            {
                return CreateResolvedCard(response.Data);
            }

            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                throw new HttpRequestException(
                    $"Scryfall named fuzzy lookup returned HTTP {(int)response.StatusCode} during card grounding.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            return CreateUnresolvedCard(candidateName, CardGroundingRejectReason.NotFound);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CreateUnresolvedCard(candidateName, CardGroundingRejectReason.UpstreamUnavailable);
        }
    }

    private static CardGroundingVerdict CreateVerdict(
        string candidateName,
        CardResolution resolution,
        CardGroundingDeckContext deckContext)
    {
        if (resolution.ResolutionReason != CardGroundingRejectReason.None)
        {
            return CreateRejectedVerdict(resolution.CanonicalName, resolution.ResolutionReason);
        }

        var legalities = resolution.CommanderLegalityStatus is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["commander"] = resolution.CommanderLegalityStatus,
            };

        if (!CardGroundingRules.IsLegalForCommander(legalities))
        {
            return CreateRejectedVerdict(resolution.CanonicalName, CardGroundingRejectReason.NotLegal);
        }

        if (!CardGroundingRules.IsWithinColorIdentity(resolution.ColorIdentity, deckContext.CommanderColorIdentity))
        {
            return CreateRejectedVerdict(resolution.CanonicalName, CardGroundingRejectReason.IdentityViolation);
        }

        if (CardGroundingRules.IsSingletonViolation(resolution.CanonicalName, resolution.TypeLine, deckContext.DeckCardNames))
        {
            return CreateRejectedVerdict(resolution.CanonicalName, CardGroundingRejectReason.SingletonDuplicate);
        }

        if (!CardGroundingRules.IsCastable(resolution.ManaCost, deckContext.DeckProducedColors))
        {
            return CreateRejectedVerdict(resolution.CanonicalName, CardGroundingRejectReason.Uncastable);
        }

        return new CardGroundingVerdict
        {
            Accepted = true,
            CanonicalName = resolution.CanonicalName,
            RejectReason = CardGroundingRejectReason.None,
        };
    }

    private static CardGroundingVerdict CreateRejectedVerdict(string canonicalName, CardGroundingRejectReason rejectReason)
        => new()
        {
            Accepted = false,
            CanonicalName = canonicalName,
            RejectReason = rejectReason,
        };

    private static CardResolution CreateResolvedCard(ScryfallCard card)
        => new()
        {
            CanonicalName = card.Name,
            ColorIdentity = card.ColorIdentity,
            CommanderLegalityStatus = GetCommanderLegalityStatus(card.Legalities),
            ManaCost = card.ManaCost,
            TypeLine = card.TypeLine,
            ResolutionReason = CardGroundingRejectReason.None,
        };

    private static CardResolution CreateUnresolvedCard(string candidateName, CardGroundingRejectReason resolutionReason)
        => new()
        {
            CanonicalName = candidateName,
            ColorIdentity = null,
            CommanderLegalityStatus = null,
            ManaCost = null,
            TypeLine = string.Empty,
            ResolutionReason = resolutionReason,
        };

    private static string? GetCommanderLegalityStatus(IReadOnlyDictionary<string, string>? legalities)
    {
        if (legalities is null)
        {
            return null;
        }

        return legalities.TryGetValue("commander", out var commanderStatus)
            ? commanderStatus
            : null;
    }

    private static CardGroundingRejectReason GetNotFoundReason(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return CardGroundingRejectReason.NotFound;
        }

        try
        {
            var error = JsonSerializer.Deserialize<ScryfallErrorResponse>(content);
            return string.Equals(error?.Type, "ambiguous", StringComparison.Ordinal)
                ? CardGroundingRejectReason.Ambiguous
                : CardGroundingRejectReason.NotFound;
        }
        catch (JsonException)
        {
            // Why: a non-JSON 404 body is still a not-found response, not an outage.
            return CardGroundingRejectReason.NotFound;
        }
    }

    private void CacheResolution(string candidateName, CardResolution resolution)
    {
        if (resolution.ResolutionReason == CardGroundingRejectReason.UpstreamUnavailable)
        {
            return;
        }

        cache.Set(
            CachedNameResolution.BuildCacheKey(CacheKeyPrefix, candidateName),
            resolution,
            resolution.ResolutionReason == CardGroundingRejectReason.None
                ? CachedNameResolution.PositiveCacheTtl
                : CachedNameResolution.NegativeCacheTtl);
    }

    private sealed record CardResolution
    {
        public required string CanonicalName { get; init; }

        public required IReadOnlyList<string>? ColorIdentity { get; init; }

        public required string? CommanderLegalityStatus { get; init; }

        public required string? ManaCost { get; init; }

        public required string TypeLine { get; init; }

        public required CardGroundingRejectReason ResolutionReason { get; init; }
    }
}
