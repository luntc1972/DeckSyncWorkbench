using DeckFlow.Core.Knowledge.StatedRulesExtraction;
using Microsoft.Extensions.Caching.Memory;

namespace DeckFlow.Web.Services.Scryfall;

/// <summary>
/// Grounds candidate card names through the shared, throttled Scryfall resolver path.
/// </summary>
public sealed class ScryfallCardNameGrounder(IScryfallCardResolver resolver, IMemoryCache cache) : ICardNameGrounder
{
    private const string CacheKeyPrefix = "card-grounder:";

    /// <inheritdoc />
    public async Task<CardGroundingResult> TryGroundAsync(string candidateName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(candidateName))
        {
            return new CardGroundingResult(false, candidateName);
        }

        return await CachedNameResolution.GetOrAddAsync(
            cache,
            CacheKeyPrefix,
            candidateName,
            async ct =>
            {
                ScryfallCard? card = await resolver.SearchPrintingFallbackCardAsync(candidateName, ct).ConfigureAwait(false);
                return card is not null
                    ? new CardGroundingResult(true, card.Name)
                    : new CardGroundingResult(false, candidateName);
            },
            _ => new CardGroundingResult(false, candidateName),
            static result => result.Resolved
                ? CachedNameResolution.PositiveCacheTtl
                : CachedNameResolution.NegativeCacheTtl,
            cancellationToken).ConfigureAwait(false);
    }
}
