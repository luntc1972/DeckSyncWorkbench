using DeckSyncWorkbench.Core.Models;

namespace DeckSyncWorkbench.Core.Integration;

public interface IMoxfieldDeckImporter
{
    Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default);
}

public interface IArchidektDeckImporter
{
    Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default);
}
