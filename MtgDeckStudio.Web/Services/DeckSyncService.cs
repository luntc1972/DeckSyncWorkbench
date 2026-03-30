using MtgDeckStudio.Core.Diffing;
using MtgDeckStudio.Core.Filtering;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Web.Models;

namespace MtgDeckStudio.Web.Services;

/// <summary>
/// Defines the deck synchronization service used by the web UI.
/// </summary>
public interface IDeckSyncService
{
    /// <summary>
    /// Compares two decks according to the provided request.
    /// </summary>
    /// <param name="request">Deck diff request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DeckSyncResult> CompareDecksAsync(DeckDiffRequest request, CancellationToken cancellationToken);
}

public sealed record DeckSyncResult(DeckDiff Diff, LoadedDecks LoadedDecks);

public sealed class DeckSyncService : IDeckSyncService
{
    private const int RequiredDeckSize = 100;
    private readonly IMoxfieldDeckImporter _moxfieldDeckImporter;
    private readonly IArchidektDeckImporter _archidektDeckImporter;
    private readonly MoxfieldParser _moxfieldParser;
    private readonly ArchidektParser _archidektParser;

    /// <summary>
    /// Creates a new instance that relies on the provided importers and parsers.
    /// </summary>
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

    /// <summary>
    /// Compares the two decks based on the supplied request.
    /// </summary>
    /// <param name="request">Deck diff request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DeckSyncResult> CompareDecksAsync(DeckDiffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loadedDecks = new LoadedDecks(
            await LoadMoxfieldEntriesAsync(request, cancellationToken).ConfigureAwait(false),
            await LoadArchidektEntriesAsync(request, cancellationToken).ConfigureAwait(false));

        ValidateDeckSize("Moxfield", loadedDecks.MoxfieldEntries);
        ValidateDeckSize("Archidekt", loadedDecks.ArchidektEntries);

        var diff = new DiffEngine(request.Mode).Compare(
            DeckSyncSupport.GetSourceEntries(request.Direction, loadedDecks),
            DeckSyncSupport.GetTargetEntries(request.Direction, loadedDecks));

        return new DeckSyncResult(diff, loadedDecks);
    }

    /// <summary>
    /// Loads Moxfield entries by parsing text or calling the API.
    /// </summary>
    /// <param name="request">Request containing inputs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<List<DeckEntry>> LoadMoxfieldEntriesAsync(DeckDiffRequest request, CancellationToken cancellationToken)
    {
        var entries = request.MoxfieldInputSource == DeckInputSource.PublicUrl
            ? await _moxfieldDeckImporter.ImportAsync(request.MoxfieldUrl ?? string.Empty, cancellationToken).ConfigureAwait(false)
            : _moxfieldParser.ParseText(request.MoxfieldText ?? string.Empty);

        return DeckEntryFilter.ExcludeMaybeboard(entries);
    }

    /// <summary>
    /// Loads Archidekt entries either via API or text parsing.
    /// </summary>
    /// <param name="request">Request containing inputs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<List<DeckEntry>> LoadArchidektEntriesAsync(DeckDiffRequest request, CancellationToken cancellationToken)
    {
        var entries = request.ArchidektInputSource == DeckInputSource.PublicUrl
            ? await _archidektDeckImporter.ImportAsync(request.ArchidektUrl ?? string.Empty, cancellationToken).ConfigureAwait(false)
            : _archidektParser.ParseText(request.ArchidektText ?? string.Empty);

        return entries;
    }

    /// <summary>
    /// Ensures the submitted deck has exactly 100 cards across commander and mainboard sections.
    /// </summary>
    /// <param name="systemName">Display name for the deck source.</param>
    /// <param name="entries">Parsed deck entries.</param>
    private static void ValidateDeckSize(string systemName, IReadOnlyList<DeckEntry> entries)
    {
        var count = entries
            .Where(entry => !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase))
            .Sum(entry => entry.Quantity);

        if (count != RequiredDeckSize)
        {
            throw new InvalidOperationException($"{systemName} deck must contain exactly {RequiredDeckSize} cards across commander and mainboard. Found {count}.");
        }
    }
}
