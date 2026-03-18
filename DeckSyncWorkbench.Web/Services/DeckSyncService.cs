using DeckSyncWorkbench.Core.Diffing;
using DeckSyncWorkbench.Core.Filtering;
using DeckSyncWorkbench.Core.Integration;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Parsing;
using DeckSyncWorkbench.Web.Models;

namespace DeckSyncWorkbench.Web.Services;

public interface IDeckSyncService
{
    Task<DeckSyncResult> CompareDecksAsync(DeckDiffRequest request, CancellationToken cancellationToken);
}

public sealed record DeckSyncResult(DeckDiff Diff, LoadedDecks LoadedDecks);

public sealed class DeckSyncService : IDeckSyncService
{
    private readonly IMoxfieldDeckImporter _moxfieldDeckImporter;
    private readonly IArchidektDeckImporter _archidektDeckImporter;
    private readonly MoxfieldParser _moxfieldParser;
    private readonly ArchidektParser _archidektParser;

    public DeckSyncService(
        IMoxfieldDeckImporter moxfieldDeckImporter,
        IArchidektDeckImporter archidektDeckImporter,
        MoxfieldParser moxfieldParser,
        ArchidektParser archidektParser)
    {
        _moxfieldDeckImporter = moxfieldDeckImporter;
        _archidektDeckImporter = archidektDeckImporter;
        _moxfieldParser = moxfieldParser;
        _archidektParser = archidektParser;
    }

    public async Task<DeckSyncResult> CompareDecksAsync(DeckDiffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loadedDecks = new LoadedDecks(
            await LoadMoxfieldEntriesAsync(request, cancellationToken).ConfigureAwait(false),
            await LoadArchidektEntriesAsync(request, cancellationToken).ConfigureAwait(false));

        var diff = new DiffEngine(request.Mode).Compare(
            DeckSyncSupport.GetSourceEntries(request.Direction, loadedDecks),
            DeckSyncSupport.GetTargetEntries(request.Direction, loadedDecks));

        return new DeckSyncResult(diff, loadedDecks);
    }

    private async Task<List<DeckEntry>> LoadMoxfieldEntriesAsync(DeckDiffRequest request, CancellationToken cancellationToken)
    {
        var entries = request.MoxfieldInputSource == DeckInputSource.PublicUrl
            ? await _moxfieldDeckImporter.ImportAsync(request.MoxfieldUrl ?? string.Empty, cancellationToken).ConfigureAwait(false)
            : _moxfieldParser.ParseText(request.MoxfieldText ?? string.Empty);

        return DeckEntryFilter.ExcludeMaybeboard(entries);
    }

    private async Task<List<DeckEntry>> LoadArchidektEntriesAsync(DeckDiffRequest request, CancellationToken cancellationToken)
    {
        var entries = request.ArchidektInputSource == DeckInputSource.PublicUrl
            ? await _archidektDeckImporter.ImportAsync(request.ArchidektUrl ?? string.Empty, cancellationToken).ConfigureAwait(false)
            : _archidektParser.ParseText(request.ArchidektText ?? string.Empty);

        return entries;
    }
}
