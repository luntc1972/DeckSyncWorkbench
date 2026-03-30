using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Services;

namespace MtgDeckStudio.Web.Controllers;

public sealed class CommanderController : Controller
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(20);
    private readonly ICommanderSearchService _searchService;
    private readonly ICommanderCategoryService _commanderCategoryService;
    private readonly ILogger<CommanderController> _logger;

    public CommanderController(ICommanderSearchService searchService, ICommanderCategoryService commanderCategoryService, ILogger<CommanderController> logger)
    {
        _searchService = searchService;
        _commanderCategoryService = commanderCategoryService;
        _logger = logger;
    }

    [HttpGet("/commander-categories")]
    /// <summary>
    /// Renders the commander categories form.
    /// </summary>
    /// <param name="commander">Optional commander name to pre-populate.</param>
    public IActionResult Index(string? commander)
    {
        var request = new CommanderCategoryRequest { CommanderName = commander ?? string.Empty };
        return View("CommanderCategories", new CommanderCategoryViewModel { Request = request });
    }

    [HttpPost("/commander-categories")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Looks up commander categories using the knowledge store.
    /// </summary>
    /// <param name="request">Commander category request.</param>
    public async Task<IActionResult> Index(CommanderCategoryRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CommanderName))
        {
            return View("CommanderCategories", new CommanderCategoryViewModel
            {
                Request = request ?? new CommanderCategoryRequest(),
                ErrorMessage = "Enter a commander name to see its most common Archidekt categories."
            });
        }

        var trimmed = request.CommanderName.Trim();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext?.RequestAborted ?? CancellationToken.None);
            timeoutCts.CancelAfter(LookupTimeout);
            var cancellationToken = timeoutCts.Token;
            var result = await _commanderCategoryService.LookupAsync(trimmed, cancellationToken);
            var viewModel = new CommanderCategoryViewModel
            {
                Request = new CommanderCategoryRequest { CommanderName = trimmed },
                CategoryRows = result.Rows,
                CategorySummaries = result.Summaries,
                HarvestedDeckCount = result.HarvestedDeckCount,
                AdditionalDecksFound = result.AdditionalDecksFound,
                ExtendedHarvestTriggered = result.CacheSweepPerformed,
                CardDeckTotals = result.CardDeckTotals
            };
            return View("CommanderCategories", viewModel);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Commander category lookup for {Commander} timed out.", trimmed);
            return View("CommanderCategories", new CommanderCategoryViewModel
            {
                Request = new CommanderCategoryRequest { CommanderName = trimmed },
                ErrorMessage = "Category lookup timed out after 20 seconds. Try again in a moment."
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to load commander categories for {Commander}.", trimmed);
            return View("CommanderCategories", new CommanderCategoryViewModel
            {
                Request = new CommanderCategoryRequest { CommanderName = trimmed },
                ErrorMessage = "Archidekt could not be reached right now. Try again shortly."
            });
        }
    }

    [HttpGet("/commander-categories/search")]
    /// <summary>
    /// Provides a look-ahead list of commander names.
    /// </summary>
    /// <param name="query">Partial commander name.</param>
    public async Task<IActionResult> Search(string query)
    {
        try
        {
            var names = await _searchService.SearchAsync(query ?? string.Empty, HttpContext.RequestAborted);
            return Json(names);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Commander search autocomplete failed for query {Query}.", query);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                Message = UpstreamErrorMessageBuilder.BuildScryfallMessage(exception)
            });
        }
    }
}
