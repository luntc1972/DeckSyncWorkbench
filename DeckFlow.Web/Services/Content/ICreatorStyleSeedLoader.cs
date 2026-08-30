namespace DeckFlow.Web.Services;

/// <summary>
/// Loads the committed creator-style seed files into the local profile and deck-cache stores.
/// </summary>
public interface ICreatorStyleSeedLoader
{
    /// <summary>
    /// Loads the creator-style seed files when present.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total number of rows read from the seed files.</returns>
    Task<int> LoadIfPresentAsync(CancellationToken cancellationToken = default);
}
