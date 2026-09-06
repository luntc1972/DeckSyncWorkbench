using DeckFlow.Core.Exporting;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services.Http;
using DeckFlow.Web.Services.Scryfall;
using Polly;
using Polly.Registry;
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
    private readonly IDeckEntryLoader _deckEntryLoader;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> _executeCollectionAsync;

    internal DeckConvertService(
        IScryfallRestClientFactory scryfallRestClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        IDeckEntryLoader deckEntryLoader,
        RestClient? restClientOverride = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsyncOverride = null)
    {
        ArgumentNullException.ThrowIfNull(scryfallRestClientFactory);
        ArgumentNullException.ThrowIfNull(pipelineProvider);
        ArgumentNullException.ThrowIfNull(deckEntryLoader);
        var pipeline = pipelineProvider.GetPipeline<RestResponse>("scryfall") ?? ResiliencePipeline<RestResponse>.Empty;
        _deckEntryLoader = deckEntryLoader;
        var client = restClientOverride ?? scryfallRestClientFactory.Create();
        _executeCollectionAsync = executeCollectionAsyncOverride ?? ((request, cancellationToken) =>
            ScryfallThrottle.ExecuteAsync(
                ScryfallEndpoint.Collection,
                token => pipeline.ExecuteAsync(
                    async pollyCt => await client.ExecuteAsync<ScryfallCollectionResponse>(request, pollyCt).ConfigureAwait(false),
                    token).AsTask(),
                cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<DeckConvertResult> ConvertAsync(DeckConvertRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var isMoxfield = string.Equals(request.SourceFormat, "Moxfield", StringComparison.OrdinalIgnoreCase);
        var isTargetArchidekt = string.Equals(request.TargetFormat, "Archidekt", StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<DeckEntry> entries = await _deckEntryLoader.LoadAsync(
            new DeckLoadRequest(
                isMoxfield ? DeckPlatform.Moxfield : DeckPlatform.Archidekt,
                request.InputSource == DeckInputSource.PublicUrl ? DeckInputKind.PublicUrl : DeckInputKind.PastedText,
                request.InputSource == DeckInputSource.PublicUrl ? request.DeckUrl ?? string.Empty : request.DeckText ?? string.Empty,
                ExcludeMaybeboard: isMoxfield),
            cancellationToken).ConfigureAwait(false);

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

        for (var i = 0; i < distinctKeys.Count; i += ScryfallLimits.CollectionBatchSize)
        {
            var batch = distinctKeys.Skip(i).Take(ScryfallLimits.CollectionBatchSize)
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
