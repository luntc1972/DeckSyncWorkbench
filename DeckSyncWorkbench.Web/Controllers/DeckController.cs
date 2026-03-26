using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Threading.Tasks;
using DeckSyncWorkbench.Core.Diffing;
using DeckSyncWorkbench.Core.Exporting;
using DeckSyncWorkbench.Core.Integration;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Parsing;
using DeckSyncWorkbench.Core.Reporting;
using DeckSyncWorkbench.Web.Models;
using DeckSyncWorkbench.Web.Services;

namespace DeckSyncWorkbench.Web.Controllers;

public sealed class DeckController : Controller
{
    private static readonly TimeSpan SuggestionTimeout = TimeSpan.FromSeconds(20);
    private readonly IDeckSyncService _deckSyncService;
    private readonly ICardSearchService _cardSearchService;
    private readonly ICategorySuggestionService _categorySuggestionService;
    private readonly ILogger<DeckController> _logger;

    public DeckController(
        IDeckSyncService deckSyncService,
        ICardSearchService cardSearchService,
        ICategorySuggestionService categorySuggestionService,
        ILogger<DeckController> logger)
    {
        _deckSyncService = deckSyncService;
        _cardSearchService = cardSearchService;
        _categorySuggestionService = categorySuggestionService;
        _logger = logger;
    }

    [HttpGet("/")]
    /// <summary>
    /// Renders the deck sync view with default tab state.
    /// </summary>
    public IActionResult Index()
    {
        return View("DeckSync", new DeckDiffViewModel
        {
            ActiveTab = DeckPageTab.Sync,
        });
    }

    [HttpGet("/suggest-categories")]
    /// <summary>
    /// Renders the suggest categories tab with fresh state.
    /// </summary>
    public IActionResult SuggestCategories()
    {
        return View("SuggestCategories", new DeckDiffViewModel
        {
            ActiveTab = DeckPageTab.SuggestCategories,
            SuggestionRequest = new CategorySuggestionRequest(),
        });
    }

    [HttpGet("/suggest-categories/card-search")]
    /// <summary>
    /// Provides card name suggestions for the suggest categories form.
    /// </summary>
    /// <param name="query">Partial card name.</param>
    public async Task<IActionResult> CardSearch(string query)
    {
        try
        {
            var names = await _cardSearchService.SearchAsync(query ?? string.Empty, HttpContext.RequestAborted);
            return Json(names);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Card search autocomplete failed for query {Query}.", query);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                Message = UpstreamErrorMessageBuilder.BuildScryfallMessage(exception)
            });
        }
    }

    [HttpPost("/")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Handles the deck sync POST to generate a diff report.
    /// </summary>
    /// <param name="request">Deck diff request data.</param>
    public async Task<IActionResult> Index(DeckDiffRequest request)
    {
        return await RenderDiffAsync(request);
    }

    [HttpPost("/suggest-categories")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Suggests categories based on cached data and optional reference deck.
    /// </summary>
    /// <param name="request">Category suggestion request.</param>
    public async Task<IActionResult> SuggestCategories(CategorySuggestionRequest request)
    {
        request ??= new CategorySuggestionRequest();
        if (request.Mode == CategorySuggestionMode.ReferenceDeck && !HasSuggestionInput(request))
        {
            return View("SuggestCategories", new DeckDiffViewModel
            {
                ActiveTab = DeckPageTab.SuggestCategories,
                SuggestionRequest = request,
                SuggestionErrorMessage = request.ArchidektInputSource == DeckInputSource.PublicUrl
                    ? "An Archidekt deck URL is required."
                    : "Archidekt text is required.",
            });
        }

        if (string.IsNullOrWhiteSpace(request.CardName))
        {
            return View("SuggestCategories", new DeckDiffViewModel
            {
                ActiveTab = DeckPageTab.SuggestCategories,
                SuggestionRequest = request,
                SuggestionErrorMessage = "A card name is required.",
            });
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeoutCts.CancelAfter(SuggestionTimeout);
            var cancellationToken = timeoutCts.Token;
            var result = await _categorySuggestionService.SuggestAsync(request, cancellationToken);
            var lookupMessage = result.NothingFound
                ? CategorySuggestionMessageBuilder.BuildNoSuggestionsMessage(result.CardName, result.CardDeckTotals)
                : null;
            var viewModel = new DeckDiffViewModel
            {
                ActiveTab = DeckPageTab.SuggestCategories,
                SuggestionRequest = request,
                ExactSuggestedCategoriesText = CategorySuggestionReporter.ToText(result.ExactCategories, result.CardName),
                ExactSuggestionContextText = "These are exact card-name matches found in the Archidekt reference deck you provided.",
                InferredCategoriesText = CategorySuggestionReporter.ToText(result.InferredCategories, result.CardName),
                InferredSuggestionContextText = "These come from the local cached store built from recent Archidekt decks.",
                EdhrecCategoriesText = CategorySuggestionReporter.ToText(result.EdhrecCategories, result.CardName),
                EdhrecSuggestionContextText = "These themes/tags are inferred from EDHREC’s deck data that include the card.",
                NoSuggestionsFound = result.NothingFound,
                NoSuggestionsMessage = lookupMessage,
                SuggestionSourceSummary = result.UsedSources.Count == 0
                    ? null
                    : $"Source used: {string.Join(" + ", result.UsedSources)}",
                ExtendedHarvestTriggered = result.CacheHarvestTriggered,
                AdditionalDecksFound = result.AdditionalDecksFound,
                CardDeckTotals = result.CardDeckTotals
            };
            return View("SuggestCategories", viewModel);
        }
        catch (Exception exception) when (exception is DeckParseException or InvalidOperationException or HttpRequestException)
        {
            _logger.LogError(exception, "Failed to suggest categories for {CardName}.", request.CardName);
            return View("SuggestCategories", new DeckDiffViewModel
            {
                ActiveTab = DeckPageTab.SuggestCategories,
                SuggestionRequest = request,
                SuggestionErrorMessage = exception.Message,
            });
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            return View("SuggestCategories", new DeckDiffViewModel
            {
                ActiveTab = DeckPageTab.SuggestCategories,
                SuggestionRequest = request,
                SuggestionErrorMessage = "Category lookup timed out after 20 seconds. Try again, or use a direct Archidekt deck with the card already categorized.",
            });
        }
    }

    [HttpPost("/resolve")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Persists user resolutions for printing conflicts and rebuilds the view.
    /// </summary>
    /// <param name="request">Deck diff request with resolutions.</param>
    public async Task<IActionResult> Resolve(DeckDiffRequest request)
    {
        try
        {
            var syncResult = await _deckSyncService.CompareDecksAsync(request, HttpContext.RequestAborted);
            var diff = syncResult.Diff;
            var updatedConflicts = diff.PrintingConflicts
                .Select(conflict => conflict with
                {
                    Resolution = request.Resolutions.TryGetValue(conflict.CardName, out var resolution)
                        ? resolution
                        : PrintingChoice.KeepArchidekt,
                })
                .ToList();

            var resolvedDiff = diff with { PrintingConflicts = updatedConflicts };
            return BuildViewModel(request, syncResult.LoadedDecks, resolvedDiff, ReconciliationReporter.GenerateSwapChecklist(updatedConflicts, DeckSyncSupport.GetTargetSystem(request.Direction)));
        }
        catch (Exception exception) when (exception is DeckParseException or InvalidOperationException or HttpRequestException)
        {
            _logger.LogError(exception, "Failed to resolve printing conflicts for {Direction}.", request.Direction);
            return View("DeckSync", new DeckDiffViewModel
            {
                Request = request,
                ErrorMessage = BuildUserFacingErrorMessage(request, exception),
            });
        }
    }

    /// <summary>
    /// Validates inputs and renders the diff view or error message.
    /// </summary>
    /// <param name="request">Deck diff request data.</param>
    private async Task<IActionResult> RenderDiffAsync(DeckDiffRequest request)
    {
        request ??= new DeckDiffRequest();
        if (!HasMoxfieldInput(request))
        {
            return View("DeckSync", new DeckDiffViewModel
            {
                ActiveTab = DeckPageTab.Sync,
                Request = request,
                ErrorMessage = request.MoxfieldInputSource == DeckInputSource.PublicUrl
                    ? "A Moxfield deck URL is required."
                    : "Moxfield text is required.",
            });
        }

        if (!HasArchidektInput(request))
        {
            return View("DeckSync", new DeckDiffViewModel
            {
                ActiveTab = DeckPageTab.Sync,
                Request = request,
                ErrorMessage = request.ArchidektInputSource == DeckInputSource.PublicUrl
                    ? "An Archidekt deck URL is required."
                    : "Archidekt text is required.",
            });
        }

        try
        {
            var syncResult = await _deckSyncService.CompareDecksAsync(request, HttpContext.RequestAborted);
            _logger.LogInformation(
                "Running deck sync for {Direction}. MoxfieldUrlProvided={HasMoxfieldUrl} ArchidektUrlProvided={HasArchidektUrl}",
                request.Direction,
                !string.IsNullOrWhiteSpace(request.MoxfieldUrl),
                !string.IsNullOrWhiteSpace(request.ArchidektUrl));
            return BuildViewModel(request, syncResult.LoadedDecks, syncResult.Diff, null);
        }
        catch (Exception exception) when (exception is DeckParseException or InvalidOperationException or HttpRequestException)
        {
            _logger.LogError(
                exception,
                "Failed to render deck sync for {Direction}. MoxfieldUrl={MoxfieldUrl} ArchidektUrl={ArchidektUrl}",
                request.Direction,
                request.MoxfieldUrl,
                request.ArchidektUrl);
                return View("DeckSync", new DeckDiffViewModel
            {
                ActiveTab = DeckPageTab.Sync,
                Request = request,
                ErrorMessage = BuildUserFacingErrorMessage(request, exception),
            });
        }
    }

    /// <summary>
    /// Creates the DeckDiffViewModel for rendering after a comparison.
    /// </summary>
    /// <param name="request">Incoming request.</param>
    /// <param name="loadedDecks">Loaded deck entries.</param>
    /// <param name="diff">Diff result.</param>
    /// <param name="swapChecklistText">Optional swap checklist text.</param>
    private ViewResult BuildViewModel(DeckDiffRequest request, LoadedDecks loadedDecks, DeckDiff diff, string? swapChecklistText)
    {
        var sourceEntries = DeckSyncSupport.GetSourceEntries(request.Direction, loadedDecks);
        var targetEntries = DeckSyncSupport.GetTargetEntries(request.Direction, loadedDecks);
        var sourceSystem = DeckSyncSupport.GetSourceSystem(request.Direction);
        var targetSystem = DeckSyncSupport.GetTargetSystem(request.Direction);

        return View("DeckSync", new DeckDiffViewModel
        {
            ActiveTab = DeckPageTab.Sync,
            Request = request,
            Diff = diff,
            DeltaText = DeltaExporter.ToText(diff.ToAdd.ToList(), targetSystem),
            FullImportText = FullImportExporter.ToText(sourceEntries, targetEntries, request.Mode, targetSystem, diff.PrintingConflicts, request.CategorySyncMode),
            ReportText = ReconciliationReporter.ToText(diff, sourceSystem, targetSystem),
            SwapChecklistText = string.IsNullOrWhiteSpace(swapChecklistText) ? null : swapChecklistText,
            InstructionsText = ReconciliationReporter.GetInstructions(targetSystem),
        });
    }

    /// <summary>
    /// Builds a user-friendly error message for controller failures.
    /// </summary>
    /// <param name="request">Original request data.</param>
    /// <param name="exception">Exception that occurred.</param>
    private static string BuildUserFacingErrorMessage(DeckDiffRequest request, Exception exception)
    {
        if (IsMoxfieldForbidden(request, exception))
        {
            return "Moxfield blocked the deck URL request from this local web app with HTTP 403. Paste the Moxfield export text into the form instead, or run the compare from the CLI/WSL environment where URL fetches succeed.";
        }

        return exception.Message;
    }

    /// <summary>
    /// Determines whether a 403 from Moxfield should be surfaced with a tip.
    /// </summary>
    /// <param name="request">Deck diff request.</param>
    /// <param name="exception">Exception thrown by the request.</param>
    private static bool IsMoxfieldForbidden(DeckDiffRequest request, Exception exception)
    {
        return request.MoxfieldInputSource == DeckInputSource.PublicUrl
            && !string.IsNullOrWhiteSpace(request.MoxfieldUrl)
            && exception is HttpRequestException httpException
            && httpException.StatusCode == HttpStatusCode.Forbidden;
    }

    /// <summary>
    /// Checks if the request includes Moxfield input (text or URL).
    /// </summary>
    /// <param name="request">Deck diff request.</param>
    private static bool HasMoxfieldInput(DeckDiffRequest request)
        => request.MoxfieldInputSource == DeckInputSource.PublicUrl
            ? !string.IsNullOrWhiteSpace(request.MoxfieldUrl)
            : !string.IsNullOrWhiteSpace(request.MoxfieldText);

    /// <summary>
    /// Checks if the request includes Archidekt input (text or URL).
    /// </summary>
    /// <param name="request">Deck diff request.</param>
    private static bool HasArchidektInput(DeckDiffRequest request)
        => request.ArchidektInputSource == DeckInputSource.PublicUrl
            ? !string.IsNullOrWhiteSpace(request.ArchidektUrl)
            : !string.IsNullOrWhiteSpace(request.ArchidektText);

    /// <summary>
    /// Validates the suggestion request contains enough Archidekt input.
    /// </summary>
    /// <param name="request">Category suggestion request.</param>
    private static bool HasSuggestionInput(CategorySuggestionRequest request)
        => request.ArchidektInputSource == DeckInputSource.PublicUrl
            ? !string.IsNullOrWhiteSpace(request.ArchidektUrl)
            : !string.IsNullOrWhiteSpace(request.ArchidektText);

    /// <summary>
    /// Searches recent Archidekt decks live for potential categories.
    /// </summary>
    /// <param name="cardName">Card name to search for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
}
