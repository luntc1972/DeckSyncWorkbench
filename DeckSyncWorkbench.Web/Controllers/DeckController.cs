using Microsoft.AspNetCore.Mvc;
using System.Net;
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
    private static readonly TimeSpan SuggestionTimeout = TimeSpan.FromSeconds(12);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDeckSyncService _deckSyncService;
    private readonly CategoryKnowledgeStore _categoryKnowledgeStore;
    private readonly ILogger<DeckController> _logger;

    public DeckController(IHttpClientFactory httpClientFactory, IDeckSyncService deckSyncService, CategoryKnowledgeStore categoryKnowledgeStore, ILogger<DeckController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _deckSyncService = deckSyncService;
        _categoryKnowledgeStore = categoryKnowledgeStore;
        _logger = logger;
    }

    [HttpGet("/")]
    public IActionResult Index()
    {
        return View("DeckSync", new DeckDiffViewModel
        {
            ActiveTab = DeckPageTab.Sync,
        });
    }

    [HttpGet("/suggest-categories")]
    public IActionResult SuggestCategories()
    {
        return View("SuggestCategories", new DeckDiffViewModel
        {
            ActiveTab = DeckPageTab.SuggestCategories,
        });
    }

    [HttpPost("/")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(DeckDiffRequest request)
    {
        return await RenderDiffAsync(request);
    }

    [HttpPost("/suggest-categories")]
    [ValidateAntiForgeryToken]
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
            var httpClient = _httpClientFactory.CreateClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeoutCts.CancelAfter(SuggestionTimeout);
            var cancellationToken = timeoutCts.Token;

            await _categoryKnowledgeStore.EnsureHarvestFreshAsync(httpClient, _logger, cancellationToken);
            var exactCategories = request.Mode == CategorySuggestionMode.ReferenceDeck
                ? CategorySuggestionReporter.SuggestCategories(await LoadArchidektSuggestionEntriesAsync(request, httpClient, cancellationToken), request.CardName)
                : [];
            var inferredCategories = await _categoryKnowledgeStore.GetCategoriesAsync(request.CardName, cancellationToken);
            if (inferredCategories.Count == 0)
            {
                await _categoryKnowledgeStore.RunCacheSweepAsync(httpClient, _logger, 30, cancellationToken);
                inferredCategories = await _categoryKnowledgeStore.GetCategoriesAsync(request.CardName, cancellationToken);
            }
            await _categoryKnowledgeStore.ProcessNextDecksAsync(httpClient, _logger, cancellationToken);
            var liveSearchCategories = exactCategories.Count == 0 && inferredCategories.Count == 0
                ? await SearchRecentArchidektDecksAsync(request.CardName, httpClient, cancellationToken)
                : [];
            var edhrecCategories = exactCategories.Count == 0 && inferredCategories.Count == 0 && liveSearchCategories.Count == 0
                ? await new EdhrecCardLookup(httpClient).LookupCategoriesAsync(request.CardName, cancellationToken)
                : [];
            var nothingFound = exactCategories.Count == 0
                && inferredCategories.Count == 0
                && liveSearchCategories.Count == 0
                && edhrecCategories.Count == 0;
            var usedSources = new List<string>();
            if (exactCategories.Count > 0)
            {
                usedSources.Add("reference deck");
            }

            if (inferredCategories.Count > 0)
            {
                usedSources.Add("cached store");
            }

            if (liveSearchCategories.Count > 0)
            {
                usedSources.Add("live Archidekt");
            }

            if (edhrecCategories.Count > 0)
            {
                usedSources.Add("EDHREC");
            }

            await _categoryKnowledgeStore.PersistObservedCategoriesAsync("archidekt_live", request.CardName, liveSearchCategories, cancellationToken: cancellationToken);
            await _categoryKnowledgeStore.PersistObservedCategoriesAsync("edhrec", request.CardName, edhrecCategories, cancellationToken: cancellationToken);

            return View("SuggestCategories", new DeckDiffViewModel
            {
                ActiveTab = DeckPageTab.SuggestCategories,
                SuggestionRequest = request,
                ExactSuggestedCategoriesText = CategorySuggestionReporter.ToText(exactCategories, request.CardName),
                ExactSuggestionContextText = "These are exact card-name matches found in the Archidekt reference deck you provided.",
                InferredCategoriesText = CategorySuggestionReporter.ToText(inferredCategories, request.CardName),
                InferredSuggestionContextText = "These come from the local cached store built from recent Archidekt decks and prior fallback lookups.",
                LiveSearchCategoriesText = CategorySuggestionReporter.ToText(liveSearchCategories, request.CardName),
                LiveSearchSuggestionContextText = "These came from scanning recent public Archidekt decks for exact card-name matches because the local corpus had no hit.",
                EdhrecCategoriesText = CategorySuggestionReporter.ToText(edhrecCategories, request.CardName),
                EdhrecSuggestionContextText = "These are lower-confidence EDHREC theme/tag suggestions from the public card JSON endpoint.",
                NoSuggestionsFound = nothingFound,
                NoSuggestionsMessage = nothingFound
                    ? $"No category suggestions were found for {request.CardName}. You can run the lookup again to retry the live Archidekt and EDHREC checks."
                    : null,
                SuggestionSourceSummary = usedSources.Count == 0
                    ? null
                    : $"Source used: {string.Join(" + ", usedSources)}",
            });
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
                SuggestionErrorMessage = "Category lookup timed out after 12 seconds. Try again, or use a direct Archidekt deck with the card already categorized.",
            });
        }
    }

    [HttpPost("/resolve")]
    [ValidateAntiForgeryToken]
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
            FullImportText = FullImportExporter.ToText(sourceEntries, targetEntries, request.Mode, targetSystem, diff.PrintingConflicts),
            ReportText = ReconciliationReporter.ToText(diff, sourceSystem, targetSystem),
            SwapChecklistText = string.IsNullOrWhiteSpace(swapChecklistText) ? null : swapChecklistText,
            InstructionsText = ReconciliationReporter.GetInstructions(targetSystem),
        });
    }

    private static async Task<List<DeckEntry>> LoadArchidektSuggestionEntriesAsync(CategorySuggestionRequest request, HttpClient httpClient, CancellationToken cancellationToken)
    {
        if (request.ArchidektInputSource == DeckInputSource.PublicUrl)
        {
            return await new ArchidektApiDeckImporter(httpClient).ImportAsync(request.ArchidektUrl, cancellationToken);
        }

        return new ArchidektParser().ParseText(request.ArchidektText);
    }

    private static string BuildUserFacingErrorMessage(DeckDiffRequest request, Exception exception)
    {
        if (IsMoxfieldForbidden(request, exception))
        {
            return "Moxfield blocked the deck URL request from this local web app with HTTP 403. Paste the Moxfield export text into the form instead, or run the compare from the CLI/WSL environment where URL fetches succeed.";
        }

        return exception.Message;
    }

    private static bool IsMoxfieldForbidden(DeckDiffRequest request, Exception exception)
    {
        return request.MoxfieldInputSource == DeckInputSource.PublicUrl
            && !string.IsNullOrWhiteSpace(request.MoxfieldUrl)
            && exception is HttpRequestException httpException
            && httpException.StatusCode == HttpStatusCode.Forbidden;
    }

    private static bool HasMoxfieldInput(DeckDiffRequest request)
        => request.MoxfieldInputSource == DeckInputSource.PublicUrl
            ? !string.IsNullOrWhiteSpace(request.MoxfieldUrl)
            : !string.IsNullOrWhiteSpace(request.MoxfieldText);

    private static bool HasArchidektInput(DeckDiffRequest request)
        => request.ArchidektInputSource == DeckInputSource.PublicUrl
            ? !string.IsNullOrWhiteSpace(request.ArchidektUrl)
            : !string.IsNullOrWhiteSpace(request.ArchidektText);

    private static bool HasSuggestionInput(CategorySuggestionRequest request)
        => request.ArchidektInputSource == DeckInputSource.PublicUrl
            ? !string.IsNullOrWhiteSpace(request.ArchidektUrl)
            : !string.IsNullOrWhiteSpace(request.ArchidektText);

    private static async Task<IReadOnlyList<string>> SearchRecentArchidektDecksAsync(string cardName, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var importer = new ArchidektRecentDecksImporter(httpClient);
        var deckIds = await importer.ImportRecentDeckIdsAsync(40, cancellationToken);
        var deckImporter = new ArchidektApiDeckImporter(httpClient);
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var deckId in deckIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = await deckImporter.ImportAsync(deckId, cancellationToken);
            foreach (var category in CategorySuggestionReporter.SuggestCategories(entries, cardName))
            {
                categories.Add(category);
            }

            if (categories.Count >= 5)
            {
                break;
            }
        }

        return categories.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

}
