using DeckFlow.Core.Exporting;
using DeckFlow.Core.Filtering;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Core.Parsing;
using DeckFlow.Web.Models;
using RestSharp;

namespace DeckFlow.Web.Services;

/// <summary>
/// Result returned by a deck conversion, including whether a commander was missing.
/// </summary>
public sealed record DeckConvertResult(string ConvertedText, bool CommanderMissing);

/// <summary>
/// Converts a single deck export from one platform's format to another.
/// </summary>
public interface IDeckConvertService
{
    /// <summary>
    /// Loads the source deck and re-formats it for the target platform.
    /// </summary>
    Task<DeckConvertResult> ConvertAsync(DeckConvertRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads a single deck via URL or pasted text and outputs it in the requested target format.
/// </summary>
public sealed class DeckConvertService : IDeckConvertService
{
    private const int CollectionBatchSize = 75;

    private readonly IMoxfieldDeckImporter _moxfieldDeckImporter;
    private readonly IArchidektDeckImporter _archidektDeckImporter;
    private readonly MoxfieldParser _moxfieldParser;
    private readonly ArchidektParser _archidektParser;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> _executeCollectionAsync;

    /// <summary>
    /// Creates the convert service with the importers and parsers it needs.
    /// </summary>
    public DeckConvertService(
        IMoxfieldDeckImporter moxfieldDeckImporter,
        IArchidektDeckImporter archidektDeckImporter,
        MoxfieldParser moxfieldParser,
        ArchidektParser archidektParser,
        RestClient? restClient = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsync = null)
    {
        _moxfieldDeckImporter = moxfieldDeckImporter;
        _archidektDeckImporter = archidektDeckImporter;
        _moxfieldParser = moxfieldParser;
        _archidektParser = archidektParser;
        var client = restClient ?? ScryfallRestClientFactory.Create();
        _executeCollectionAsync = executeCollectionAsync ?? ((request, ct) => ScryfallThrottle.ExecuteAsync(token => client.ExecuteAsync<ScryfallCollectionResponse>(request, token), ct));
    }

    /// <inheritdoc/>
    public async Task<DeckConvertResult> ConvertAsync(DeckConvertRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var isMoxfield = string.Equals(request.SourceFormat, "Moxfield", StringComparison.OrdinalIgnoreCase);
        var isTargetArchidekt = string.Equals(request.TargetFormat, "Archidekt", StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<DeckEntry> entries = request.InputSource == DeckInputSource.PublicUrl
            ? isMoxfield
                ? await _moxfieldDeckImporter.ImportAsync(request.DeckUrl ?? string.Empty, cancellationToken).ConfigureAwait(false)
                : await _archidektDeckImporter.ImportAsync(request.DeckUrl ?? string.Empty, cancellationToken).ConfigureAwait(false)
            : isMoxfield
                ? _moxfieldParser.ParseText(request.DeckText ?? string.Empty)
                : _archidektParser.ParseText(request.DeckText ?? string.Empty);

        if (isMoxfield)
        {
            entries = DeckEntryFilter.ExcludeMaybeboard(entries);
        }

        var commanderMissing = false;
        if (isMoxfield)
        {
            var hasCommander = entries.Any(e => string.Equals(e.Board, "commander", StringComparison.OrdinalIgnoreCase));
            var playableCount = entries.Sum(e => string.Equals(e.Board, "sideboard", StringComparison.OrdinalIgnoreCase) ? 0 : e.Quantity);
            commanderMissing = !hasCommander || playableCount <= 99;

            if (commanderMissing && !string.IsNullOrWhiteSpace(request.CommanderOverride))
            {
                var commanderName = request.CommanderOverride.Trim();
                var commander = new DeckEntry
                {
                    Name = commanderName,
                    NormalizedName = CardNormalizer.Normalize(commanderName),
                    Quantity = 1,
                    Board = "commander",
                };
                var list = new List<DeckEntry> { commander };
                list.AddRange(entries);
                entries = list;
                commanderMissing = false;
            }
        }

        if (isMoxfield && isTargetArchidekt)
        {
            entries = await NormalizeNamesAsync(entries, cancellationToken).ConfigureAwait(false);
        }

        var targetSystem = isTargetArchidekt ? "Archidekt" : "Moxfield";
        var text = FullImportExporter.ToText([.. entries], [], MatchMode.Loose, targetSystem, null, CategorySyncMode.SourceTags);

        return new DeckConvertResult(text, commanderMissing);
    }

    private async Task<IReadOnlyList<DeckEntry>> NormalizeNamesAsync(IReadOnlyList<DeckEntry> entries, CancellationToken cancellationToken)
    {
        var distinctKeys = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.SetCode) && !string.IsNullOrWhiteSpace(e.CollectorNumber))
            .Select(e => (Set: e.SetCode!.ToLowerInvariant(), Collector: e.CollectorNumber!.ToLowerInvariant()))
            .Distinct()
            .ToList();

        if (distinctKeys.Count == 0)
        {
            return entries;
        }

        var canonicalNames = new Dictionary<(string Set, string Collector), string>();

        for (var i = 0; i < distinctKeys.Count; i += CollectionBatchSize)
        {
            var batch = distinctKeys.Skip(i).Take(CollectionBatchSize)
                .Select(k => new ScryfallPrintingIdentifier(k.Set, k.Collector))
                .ToList();

            var restRequest = new RestRequest("cards/collection", Method.Post);
            restRequest.AddJsonBody(new { identifiers = batch });

            var response = await _executeCollectionAsync(restRequest, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
            {
                continue;
            }

            foreach (var card in response.Data.Data)
            {
                if (string.IsNullOrWhiteSpace(card.SetCode) || string.IsNullOrWhiteSpace(card.CollectorNumber))
                {
                    continue;
                }

                var key = (card.SetCode.ToLowerInvariant(), card.CollectorNumber.ToLowerInvariant());
                canonicalNames.TryAdd(key, card.Name);
            }
        }

        if (canonicalNames.Count == 0)
        {
            return entries;
        }

        return entries.Select(entry =>
        {
            if (string.IsNullOrWhiteSpace(entry.SetCode) || string.IsNullOrWhiteSpace(entry.CollectorNumber))
            {
                return entry;
            }

            var key = (entry.SetCode.ToLowerInvariant(), entry.CollectorNumber.ToLowerInvariant());
            if (!canonicalNames.TryGetValue(key, out var canonical) ||
                string.Equals(entry.Name, canonical, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }

            return entry with { Name = canonical, NormalizedName = canonical.ToLowerInvariant() };
        }).ToList();
    }
}
