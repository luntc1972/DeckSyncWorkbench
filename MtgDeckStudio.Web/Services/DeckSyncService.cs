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

/// <summary>
/// Carries the loaded deck entries alongside the generated diff.
/// </summary>
public sealed record DeckSyncResult(DeckDiff Diff, LoadedDecks LoadedDecks);

/// <summary>
/// Loads deck inputs from either site, validates Commander deck size, and produces compare results.
/// </summary>
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
            await LoadLeftEntriesAsync(request, cancellationToken).ConfigureAwait(false),
            await LoadRightEntriesAsync(request, cancellationToken).ConfigureAwait(false));

        ValidateDeckSize(DeckSyncSupport.GetLeftPanelSystem(request.Direction), loadedDecks.MoxfieldEntries);
        ValidateDeckSize(DeckSyncSupport.GetRightPanelSystem(request.Direction), loadedDecks.ArchidektEntries);

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
    private Task<List<DeckEntry>> LoadLeftEntriesAsync(DeckDiffRequest request, CancellationToken cancellationToken)
    {
        return LoadEntriesAsync(
            DeckSyncSupport.GetLeftPanelSystem(request.Direction),
            request.MoxfieldInputSource,
            request.MoxfieldUrl ?? string.Empty,
            request.MoxfieldText ?? string.Empty,
            cancellationToken);
    }

    /// <summary>
    /// Loads Archidekt entries either via API or text parsing.
    /// </summary>
    /// <param name="request">Request containing inputs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private Task<List<DeckEntry>> LoadRightEntriesAsync(DeckDiffRequest request, CancellationToken cancellationToken)
    {
        return LoadEntriesAsync(
            DeckSyncSupport.GetRightPanelSystem(request.Direction),
            request.ArchidektInputSource,
            request.ArchidektUrl ?? string.Empty,
            request.ArchidektText ?? string.Empty,
            cancellationToken);
    }

    /// <summary>
    /// Loads entries from either supported deck system by URL import or pasted-text parsing.
    /// </summary>
    /// <param name="systemName">Deck system being loaded.</param>
    /// <param name="inputSource">Whether the input is a public URL or pasted text.</param>
    /// <param name="url">URL to import when the input source is public URL.</param>
    /// <param name="text">Pasted deck text when the input source is text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<List<DeckEntry>> LoadEntriesAsync(string systemName, DeckInputSource inputSource, string url, string text, CancellationToken cancellationToken)
    {
        var isMoxfield = string.Equals(systemName, "Moxfield", StringComparison.OrdinalIgnoreCase);
        var entries = inputSource == DeckInputSource.PublicUrl
            ? isMoxfield
                ? await _moxfieldDeckImporter.ImportAsync(url, cancellationToken).ConfigureAwait(false)
                : await _archidektDeckImporter.ImportAsync(url, cancellationToken).ConfigureAwait(false)
            : isMoxfield
                ? _moxfieldParser.ParseText(text)
                : _archidektParser.ParseText(text);

        return isMoxfield
            ? DeckEntryFilter.ExcludeMaybeboard(entries)
            : entries;
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
