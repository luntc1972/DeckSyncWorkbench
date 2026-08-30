namespace DeckFlow.Web.Services.Scryfall;

/// <summary>
/// Shared Scryfall API request limits. The <c>/cards/collection</c> endpoint accepts at most 75
/// identifiers per request per the Scryfall API documentation.
/// </summary>
internal static class ScryfallLimits
{
    internal const int CollectionBatchSize = 75;
}
