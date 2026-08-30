using System.Net;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using RestSharp;

namespace DeckFlow.Web.Services.Scryfall;

/// <summary>
/// Shared Scryfall <c>/cards/collection</c> batch resolver for deck-entry inputs.
/// </summary>
internal static class ScryfallCollectionResolver
{
    internal static async Task<IReadOnlyList<ScryfallCard>> ResolveCardsAsync(
        IReadOnlyList<DeckEntry> deckCards,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> executeCollectionAsync,
        string errorMessageSuffix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deckCards);
        ArgumentNullException.ThrowIfNull(executeCollectionAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessageSuffix);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identifiers = new List<object>();
        foreach (DeckEntry entry in deckCards)
        {
            string? printing = ScryfallCardNameIndex.PrintingKey(entry.SetCode, entry.CollectorNumber);
            string key = printing ?? $"name:{entry.Name}";
            if (!seen.Add(key))
            {
                continue;
            }

            identifiers.Add(printing is not null
                ? new { set = entry.SetCode, collector_number = entry.CollectorNumber }
                : (object)new { name = entry.Name });
        }

        var resolvedCards = new List<ScryfallCard>();
        foreach (List<object> batch in ScryfallBatching.Chunk(identifiers, ScryfallLimits.CollectionBatchSize))
        {
            var request = new RestRequest("cards/collection", Method.Post);
            request.AddJsonBody(new { identifiers = batch.ToArray() });

            RestResponse<ScryfallCollectionResponse> response = await executeCollectionAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices || response.Data is null)
            {
                throw new HttpRequestException(
                    $"Scryfall card lookup (cards/collection) returned HTTP {(int)response.StatusCode} during {errorMessageSuffix}",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            resolvedCards.AddRange(response.Data.Data);
        }

        return resolvedCards;
    }
}
