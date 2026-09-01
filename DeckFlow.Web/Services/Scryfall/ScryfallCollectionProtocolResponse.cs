using System.Net;

namespace DeckFlow.Web.Services.Scryfall;

/// <summary>
/// Typed cards/collection response retaining its HTTP status and payload.
/// </summary>
public sealed record ScryfallCollectionProtocolResponse(
    HttpStatusCode StatusCode,
    IReadOnlyList<ScryfallCard> Cards,
    IReadOnlyList<ScryfallCollectionNameIdentifier> NotFound,
    bool HasPayload);
