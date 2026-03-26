using System.Linq;
using System.Net.Http;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Parsing;
using DeckSyncWorkbench.Core.Reporting;
using DeckSyncWorkbench.Web.Models;
using DeckSyncWorkbench.Web.Models.Api;
using DeckSyncWorkbench.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeckSyncWorkbench.Web.Controllers.Api;

[ApiController]
[Route("api/suggestions")]
public sealed class SuggestionsApiController : ControllerBase
{
    private readonly ICategorySuggestionService _categorySuggestionService;
    private readonly ICommanderCategoryService _commanderCategoryService;
    private readonly ILogger<SuggestionsApiController> _logger;

    public SuggestionsApiController(
        ICategorySuggestionService categorySuggestionService,
        ICommanderCategoryService commanderCategoryService,
        ILogger<SuggestionsApiController> logger)
    {
        _categorySuggestionService = categorySuggestionService;
        _commanderCategoryService = commanderCategoryService;
        _logger = logger;
    }

    /// <summary>
    /// Suggests Archidekt-style categories for a single card using the local cache, optional reference deck data, and EDHREC fallback data.
    /// </summary>
    /// <param name="request">Card suggestion lookup request.</param>
    /// <param name="cancellationToken">Cancellation token for the lookup.</param>
    /// <returns>A structured suggestion response used by the web UI and external callers.</returns>
    [HttpPost("card")]
    [ProducesResponseType(typeof(CategorySuggestionApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    public async Task<ActionResult<CategorySuggestionApiResponse>> PostCardSuggestionAsync([FromBody] CategorySuggestionRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { Message = "Request body is required." });
        }

        if (request.Mode == CategorySuggestionMode.ReferenceDeck && !HasSuggestionInput(request))
        {
            return BadRequest(new { Message = request.ArchidektInputSource == DeckInputSource.PublicUrl ? "An Archidekt deck URL is required." : "Archidekt text is required." });
        }

        if (string.IsNullOrWhiteSpace(request.CardName))
        {
            return BadRequest(new { Message = "A card name is required." });
        }

        try
        {
            var result = await _categorySuggestionService.SuggestAsync(request, cancellationToken);
            var response = new CategorySuggestionApiResponse
            {
                CardName = result.CardName,
                ExactCategoriesText = CategorySuggestionReporter.ToText(result.ExactCategories, result.CardName),
                ExactSuggestionContextText = "These are exact card-name matches found in the Archidekt reference deck you provided.",
                InferredCategoriesText = CategorySuggestionReporter.ToText(result.InferredCategories, result.CardName),
                InferredSuggestionContextText = "These come from the local cached store built from recent Archidekt decks.",
                EdhrecCategoriesText = CategorySuggestionReporter.ToText(result.EdhrecCategories, result.CardName),
                EdhrecSuggestionContextText = "These themes/tags are inferred from EDHREC’s deck data that include the card.",
                HasExactCategories = result.ExactCategories.Count > 0,
                HasInferredCategories = result.InferredCategories.Count > 0,
                HasEdhrecCategories = result.EdhrecCategories.Count > 0,
                SuggestionSourceSummary = result.UsedSources.Count == 0 ? null : $"Source used: {string.Join(" + ", result.UsedSources)}",
                NoSuggestionsFound = result.NothingFound,
                NoSuggestionsMessage = result.NothingFound ? CategorySuggestionMessageBuilder.BuildNoSuggestionsMessage(result.CardName, result.CardDeckTotals) : null,
                CardDeckTotals = result.CardDeckTotals,
                AdditionalDecksFound = result.AdditionalDecksFound,
                CacheSweepPerformed = result.CacheHarvestTriggered
            };
            return Ok(response);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(408, new { Message = "Category lookup timed out after 20 seconds. Try again, or use a direct Archidekt deck with the card already categorized." });
        }
        catch (Exception exception) when (exception is DeckParseException or InvalidOperationException or HttpRequestException)
        {
            _logger.LogWarning(exception, "Card suggestion lookup failed.");
            return BadRequest(new { Message = UpstreamErrorMessageBuilder.BuildSuggestionMessage(exception) });
        }
    }

    /// <summary>
    /// Returns the most common Archidekt categories seen on decks where the supplied card appears as the commander.
    /// </summary>
    /// <param name="request">Commander category lookup request.</param>
    /// <param name="cancellationToken">Cancellation token for the lookup.</param>
    /// <returns>A commander category summary response used by the web UI and external callers.</returns>
    [HttpPost("commander")]
    [ProducesResponseType(typeof(CommanderCategoryApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    public async Task<ActionResult<CommanderCategoryApiResponse>> PostCommanderSuggestionAsync([FromBody] CommanderCategoryRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CommanderName))
        {
            return BadRequest(new { Message = "Commander name is required." });
        }

        try
        {
            var result = await _commanderCategoryService.LookupAsync(request.CommanderName.Trim(), cancellationToken);
            var response = new CommanderCategoryApiResponse
            {
                CommanderName = result.CommanderName,
                CardRowCount = result.Rows.Count,
                CategoryCount = result.Summaries.Count,
                HarvestedDeckCount = result.HarvestedDeckCount,
                AdditionalDecksFound = result.AdditionalDecksFound,
                CardDeckTotals = result.CardDeckTotals,
                CacheSweepPerformed = result.CacheSweepPerformed,
                Summaries = result.Summaries
                    .Select(summary => new CommanderCategorySummaryDto
                    {
                        Category = summary.Category,
                        Count = summary.Count,
                        DeckCount = summary.DeckCount
                    })
                    .ToList(),
                NoResultsMessage = result.Summaries.Count == 0
                    ? $"No commander categories for {result.CommanderName} have been observed in the cached data yet. Run Show Categories again to refresh the cache."
                    : null
            };
            return Ok(response);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(408, new { Message = "Category lookup timed out after 20 seconds. Try again in a moment." });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to load commander categories for {Commander}.", request.CommanderName);
            return BadRequest(new { Message = UpstreamErrorMessageBuilder.BuildCommanderMessage(exception) });
        }
    }

    private static bool HasSuggestionInput(CategorySuggestionRequest request)
        => request.ArchidektInputSource == DeckInputSource.PublicUrl
            ? !string.IsNullOrWhiteSpace(request.ArchidektUrl)
            : !string.IsNullOrWhiteSpace(request.ArchidektText);
}
