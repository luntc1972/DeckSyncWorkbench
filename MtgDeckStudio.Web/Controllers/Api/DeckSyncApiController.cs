using System.Linq;
using MtgDeckStudio.Core.Exporting;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Core.Reporting;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Models.Api;
using MtgDeckStudio.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace MtgDeckStudio.Web.Controllers.Api;

[ApiController]
[Route("api/deck")]
public sealed class DeckSyncApiController : ControllerBase
{
    private readonly IDeckSyncService _deckSyncService;
    private readonly ILogger<DeckSyncApiController> _logger;

    public DeckSyncApiController(IDeckSyncService deckSyncService, ILogger<DeckSyncApiController> logger)
    {
        _deckSyncService = deckSyncService;
        _logger = logger;
    }

    /// <summary>
    /// Compares the two decks and returns the report, delta import text, full export text, and printing conflicts.
    /// </summary>
    /// <param name="request">Deck sync request payload.</param>
    /// <param name="cancellationToken">Cancellation token for the sync.</param>
    /// <returns>A structured deck sync response used by the web UI and external callers.</returns>
    [HttpPost("diff")]
    [ProducesResponseType(typeof(DeckSyncApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DeckSyncApiResponse>> PostDiffAsync([FromBody] DeckSyncApiRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { Message = "Request body is required." });
        }

        if (!HasMoxfieldInput(request))
        {
            return BadRequest(new { Message = request.MoxfieldInputSource == MtgDeckStudio.Web.Models.DeckInputSource.PublicUrl ? "A Moxfield deck URL is required." : "Moxfield text is required." });
        }

        if (!HasArchidektInput(request))
        {
            return BadRequest(new { Message = request.ArchidektInputSource == MtgDeckStudio.Web.Models.DeckInputSource.PublicUrl ? "An Archidekt deck URL is required." : "Archidekt text is required." });
        }

        try
        {
            var deckRequest = request.ToDeckDiffRequest();
            var syncResult = await _deckSyncService.CompareDecksAsync(deckRequest, cancellationToken).ConfigureAwait(false);
            var sourceSystem = DeckSyncSupport.GetSourceSystem(deckRequest.Direction);
            var targetSystem = DeckSyncSupport.GetTargetSystem(deckRequest.Direction);
            var diff = syncResult.Diff;
            var sourceEntries = DeckSyncSupport.GetSourceEntries(deckRequest.Direction, syncResult.LoadedDecks);
            var targetEntries = DeckSyncSupport.GetTargetEntries(deckRequest.Direction, syncResult.LoadedDecks);

            var response = new DeckSyncApiResponse(
                ReconciliationReporter.ToText(diff, sourceSystem, targetSystem),
                DeltaExporter.ToText(diff.ToAdd.ToList(), targetSystem),
                FullImportExporter.ToText(sourceEntries, targetEntries, deckRequest.Mode, targetSystem, diff.PrintingConflicts, deckRequest.CategorySyncMode),
                ReconciliationReporter.GetInstructions(targetSystem),
                sourceSystem,
                targetSystem,
                new DeckSyncApiDiffSummary(
                    diff.ToAdd.Count,
                    diff.CountMismatch.Count,
                    diff.OnlyInArchidekt.Count,
                    diff.PrintingConflicts.Count),
                diff.PrintingConflicts.Select(conflict => new PrintingConflictDto(
                    conflict.CardName,
                    conflict.ArchidektVersion.SetCode ?? string.Empty,
                    conflict.ArchidektVersion.CollectorNumber ?? string.Empty,
                    conflict.ArchidektVersion.Category,
                    conflict.MoxfieldVersion.SetCode,
                    conflict.MoxfieldVersion.CollectorNumber,
                    conflict.Resolution.ToString())).ToList());

            return Ok(response);
        }
        catch (Exception exception) when (exception is DeckParseException or InvalidOperationException or HttpRequestException)
        {
            _logger.LogWarning(exception, "Deck sync API request failed.");
            return BadRequest(new { Message = BuildUserFacingErrorMessage(request, exception) });
        }
    }

    private static bool HasMoxfieldInput(DeckSyncApiRequest request)
        => request.MoxfieldInputSource == MtgDeckStudio.Web.Models.DeckInputSource.PublicUrl
            ? !string.IsNullOrWhiteSpace(request.MoxfieldUrl)
            : !string.IsNullOrWhiteSpace(request.MoxfieldText);

    private static bool HasArchidektInput(DeckSyncApiRequest request)
        => request.ArchidektInputSource == MtgDeckStudio.Web.Models.DeckInputSource.PublicUrl
            ? !string.IsNullOrWhiteSpace(request.ArchidektUrl)
            : !string.IsNullOrWhiteSpace(request.ArchidektText);

    private static string BuildUserFacingErrorMessage(DeckSyncApiRequest request, Exception exception)
    {
        if (request.MoxfieldInputSource == MtgDeckStudio.Web.Models.DeckInputSource.PublicUrl
            && !string.IsNullOrWhiteSpace(request.MoxfieldUrl)
            && exception is HttpRequestException httpException
            && httpException.StatusCode == HttpStatusCode.Forbidden)
        {
            return "Moxfield blocked the deck URL request from this local web app with HTTP 403. Paste the Moxfield export text into the form instead, or run the compare from the CLI/WSL environment where URL fetches succeed.";
        }

        return UpstreamErrorMessageBuilder.BuildDeckSyncMessage(request.ToDeckDiffRequest(), exception);
    }
}
