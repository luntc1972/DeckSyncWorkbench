namespace DeckFlow.Web.Services.Scryfall;

/// <summary>
/// Executes typed Scryfall collection protocol requests.
/// </summary>
public interface IScryfallCollectionProtocol
{
    /// <summary>
    /// Executes a collection request through Scryfall safeguards.
    /// </summary>
    Task<ScryfallCollectionProtocolResponse> ResolveAsync(
        ScryfallCollectionProtocolRequest request,
        CancellationToken cancellationToken = default);
}
