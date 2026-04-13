using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using MtgDeckStudio.Core.Diffing;
using MtgDeckStudio.Core.Exporting;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Core.Reporting;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Services;

namespace MtgDeckStudio.Web.Controllers;

/// <summary>
/// Serves the MVC pages for deck compare, category suggestion, lookup, and ChatGPT-assisted workflows.
/// </summary>
public sealed class DeckController : Controller
{
    private static readonly TimeSpan SuggestionTimeout = TimeSpan.FromSeconds(20);
    private readonly IDeckSyncService _deckSyncService;
    private readonly IDeckConvertService _deckConvertService;
    private readonly ICardSearchService _cardSearchService;
    private readonly ICardLookupService _cardLookupService;
    private readonly IMechanicLookupService _mechanicLookupService;
    private readonly ICategorySuggestionService _categorySuggestionService;
    private readonly IChatGptDeckPacketService _chatGptDeckPacketService;
    private readonly IChatGptDeckComparisonService _chatGptDeckComparisonService;
    private readonly IScryfallSetService _scryfallSetService;
    private readonly ILogger<DeckController> _logger;

    /// <summary>
    /// Creates the main deck-tools controller.
    /// </summary>
    public DeckController(
        IDeckSyncService deckSyncService,
        IDeckConvertService deckConvertService,
        ICardSearchService cardSearchService,
        ICardLookupService cardLookupService,
        IMechanicLookupService mechanicLookupService,
        ICategorySuggestionService categorySuggestionService,
        IChatGptDeckPacketService chatGptDeckPacketService,
        IChatGptDeckComparisonService chatGptDeckComparisonService,
        IScryfallSetService scryfallSetService,
        ILogger<DeckController> logger)
    {
        _deckSyncService = deckSyncService;
        _deckConvertService = deckConvertService;
        _cardSearchService = cardSearchService;
        _cardLookupService = cardLookupService;
        _mechanicLookupService = mechanicLookupService;
        _categorySuggestionService = categorySuggestionService;
        _chatGptDeckPacketService = chatGptDeckPacketService;
        _chatGptDeckComparisonService = chatGptDeckComparisonService;
        _scryfallSetService = scryfallSetService;
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

    [HttpGet("/card-lookup")]
    /// <summary>
    /// Renders the card lookup page.
    /// </summary>
    public IActionResult CardLookup()
    {
        return View("CardLookup", new CardLookupViewModel
        {
            ActiveTab = DeckPageTab.CardLookup,
        });
    }

    [HttpGet("/mechanic-lookup")]
    /// <summary>
    /// Renders the mechanic rules lookup page.
    /// </summary>
    public IActionResult MechanicLookup()
    {
        return View("MechanicLookup", new MechanicLookupViewModel
        {
            ActiveTab = DeckPageTab.MechanicLookup,
        });
    }

    [HttpGet("/chatgpt-packets")]
    /// <summary>
    /// Renders the staged ChatGPT packet workflow. Set options load asynchronously on the client.
    /// </summary>
    public IActionResult ChatGptPackets()
    {
        return View("ChatGptPackets", new ChatGptDeckViewModel
        {
            ActiveTab = DeckPageTab.ChatGptPackets,
            Request = new ChatGptDeckRequest(),
        });
    }

    [HttpGet("/chatgpt-deck-comparison")]
    /// <summary>
    /// Renders the staged ChatGPT deck-comparison workflow.
    /// </summary>
    public IActionResult ChatGptDeckComparison()
    {
        return View("ChatGptDeckComparison", new ChatGptDeckComparisonViewModel
        {
            ActiveTab = DeckPageTab.ChatGptDeckComparison,
            Request = new ChatGptDeckComparisonRequest(),
        });
    }

    [HttpGet("/api/set-options")]
    /// <summary>
    /// Returns the Scryfall set catalog as JSON for client-side async loading.
    /// </summary>
    public async Task<IActionResult> GetSetOptions()
    {
        var sets = await TryGetSetOptionsAsync();
        return Json(sets.Select(s => new { s.Code, s.DisplayLabel }));
    }

    [HttpGet("/convert")]
    /// <summary>
    /// Renders the deck format conversion page.
    /// </summary>
    public IActionResult Convert()
    {
        return View("DeckConvert", new DeckConvertViewModel());
    }

    [HttpPost("/convert")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Converts a single deck from one platform format to another.
    /// </summary>
    /// <param name="request">Deck convert request.</param>
    public async Task<IActionResult> Convert(DeckConvertRequest request)
    {
        request ??= new DeckConvertRequest();
        var hasInput = request.InputSource == DeckInputSource.PublicUrl
            ? !string.IsNullOrWhiteSpace(request.DeckUrl)
            : !string.IsNullOrWhiteSpace(request.DeckText);

        if (!hasInput)
        {
            return View("DeckConvert", new DeckConvertViewModel
            {
                Request = request,
                ErrorMessage = "Paste a deck export or enter a public URL before converting.",
            });
        }

        try
        {
            var result = await _deckConvertService.ConvertAsync(request, HttpContext.RequestAborted);
            return View("DeckConvert", new DeckConvertViewModel
            {
                Request = request,
                ConvertedText = result.ConvertedText,
                MissingCommander = result.CommanderMissing,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            _logger.LogInformation(exception, "Deck conversion failed.");
            return View("DeckConvert", new DeckConvertViewModel
            {
                Request = request,
                ErrorMessage = exception.Message,
            });
        }
    }
    [HttpGet("/convert/commander-search")]
    /// <summary>
    /// Returns commander-eligible card name suggestions for the deck convert form typeahead.
    /// </summary>
    /// <param name="q">Partial commander name.</param>
    public async Task<IActionResult> ConvertCommanderSearch(string q)
    {
        try
        {
            var names = await _cardSearchService.SearchCommandersAsync(q ?? string.Empty, HttpContext.RequestAborted);
            return Json(names);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Commander search autocomplete failed for query {Query}.", q);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                Message = UpstreamErrorMessageBuilder.BuildScryfallMessage(exception)
            });
        }
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

    [HttpPost("/card-lookup")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Looks up a pasted card list against Scryfall and renders returned text.
    /// </summary>
    /// <param name="request">Card verification request.</param>
    public async Task<IActionResult> CardLookup(CardLookupRequest request)
    {
        return await RenderCardLookupAsync(request, downloadFile: false);
    }

    [HttpPost("/card-lookup/download")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Verifies a pasted card list and returns the output as a downloadable text file.
    /// </summary>
    /// <param name="request">Card verification request.</param>
    public async Task<IActionResult> DownloadCardLookup(CardLookupRequest request)
    {
        return await RenderCardLookupAsync(request, downloadFile: true);
    }

    [HttpPost("/mechanic-lookup")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Looks up official rules text for a mechanic or rules term.
    /// </summary>
    /// <param name="request">Mechanic lookup request.</param>
    public async Task<IActionResult> MechanicLookup(MechanicLookupRequest request)
    {
        request ??= new MechanicLookupRequest();
        if (string.IsNullOrWhiteSpace(request.MechanicName))
        {
            return View("MechanicLookup", new MechanicLookupViewModel
            {
                ActiveTab = DeckPageTab.MechanicLookup,
                Request = request,
                ErrorMessage = "A mechanic name is required.",
            });
        }

        try
        {
            var result = await _mechanicLookupService.LookupAsync(request.MechanicName, HttpContext.RequestAborted);
            return View("MechanicLookup", new MechanicLookupViewModel
            {
                ActiveTab = DeckPageTab.MechanicLookup,
                Request = request,
                MechanicName = result.MechanicName,
                RuleReference = result.RuleReference,
                MatchType = result.MatchType,
                RulesText = result.RulesText,
                SummaryText = result.SummaryText,
                RulesTextUrl = result.RulesTextUrl,
                NotFoundMessage = result.Found
                    ? null
                    : $"No official rules entry was found for {request.MechanicName.Trim()} in the current Wizards Comprehensive Rules text.",
            });
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogInformation(exception, "Mechanic lookup request failed validation.");
            return View("MechanicLookup", new MechanicLookupViewModel
            {
                ActiveTab = DeckPageTab.MechanicLookup,
                Request = request,
                ErrorMessage = exception.Message,
            });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Mechanic lookup failed.");
            return View("MechanicLookup", new MechanicLookupViewModel
            {
                ActiveTab = DeckPageTab.MechanicLookup,
                Request = request,
                ErrorMessage = "Wizards of the Coast rules lookup is currently unavailable. Try again shortly.",
            });
        }
    }

    [HttpPost("/chatgpt-packets")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Processes a ChatGPT workflow postback and regenerates the next packet outputs.
    /// </summary>
    /// <param name="request">Current workflow request.</param>
    public async Task<IActionResult> ChatGptPackets(ChatGptDeckRequest request)
    {
        request ??= new ChatGptDeckRequest();

        try
        {
            var result = await _chatGptDeckPacketService.BuildAsync(request, HttpContext.RequestAborted);
            return View("ChatGptPackets", new ChatGptDeckViewModel
            {
                ActiveTab = DeckPageTab.ChatGptPackets,
                Request = request,
                InputSummary = result.InputSummary,
                SuggestedChatTitle = result.SuggestedChatTitle,
                ReferenceText = result.ReferenceText,
                AnalysisPromptText = result.AnalysisPromptText,
                DeckProfileSchemaJson = result.DeckProfileSchemaJson,
                SetUpgradePromptText = result.SetUpgradePromptText,
                SavedArtifactsDirectory = result.SavedArtifactsDirectory,
                TimingSummary = result.TimingSummary,
                AnalysisResponse = result.AnalysisResponse,
            });
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogInformation(exception, "ChatGPT packet generation failed validation.");
            return View("ChatGptPackets", new ChatGptDeckViewModel
            {
                ActiveTab = DeckPageTab.ChatGptPackets,
                Request = request,
                ErrorMessage = exception.Message,
            });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "ChatGPT packet generation hit an upstream dependency.");
            return View("ChatGptPackets", new ChatGptDeckViewModel
            {
                ActiveTab = DeckPageTab.ChatGptPackets,
                Request = request,
                ErrorMessage = UpstreamErrorMessageBuilder.BuildScryfallMessage(exception),
            });
        }
    }

    [HttpPost("/chatgpt-deck-comparison")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Processes the ChatGPT deck comparison workflow.
    /// </summary>
    /// <param name="request">Current comparison workflow request.</param>
    public async Task<IActionResult> ChatGptDeckComparison(ChatGptDeckComparisonRequest request)
    {
        request ??= new ChatGptDeckComparisonRequest();
        if (!ModelState.IsValid)
        {
            return View("ChatGptDeckComparison", new ChatGptDeckComparisonViewModel
            {
                ActiveTab = DeckPageTab.ChatGptDeckComparison,
                Request = request,
                ErrorMessage = "The comparison form contains invalid values. Review the highlighted fields and try again."
            });
        }

        try
        {
            var result = await _chatGptDeckComparisonService.BuildAsync(request, HttpContext.RequestAborted);
            return View("ChatGptDeckComparison", new ChatGptDeckComparisonViewModel
            {
                ActiveTab = DeckPageTab.ChatGptDeckComparison,
                Request = request,
                InputSummary = result.InputSummary,
                DeckAListText = result.DeckAListText,
                DeckBListText = result.DeckBListText,
                DeckAComboText = result.DeckAComboText,
                DeckBComboText = result.DeckBComboText,
                ComparisonContextText = result.ComparisonContextText,
                ComparisonPromptText = result.ComparisonPromptText,
                FollowUpPromptText = result.FollowUpPromptText,
                ComparisonSchemaJson = result.ComparisonSchemaJson,
                ComparisonResponse = result.ComparisonResponse,
                SavedArtifactsDirectory = result.SavedArtifactsDirectory,
                TimingSummary = result.TimingSummary
            });
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogInformation(exception, "ChatGPT deck comparison failed validation.");
            return View("ChatGptDeckComparison", new ChatGptDeckComparisonViewModel
            {
                ActiveTab = DeckPageTab.ChatGptDeckComparison,
                Request = request,
                ErrorMessage = exception.Message
            });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "ChatGPT deck comparison hit an upstream dependency.");
            var errorMessage = exception.Message.Contains("Deck A", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("Deck B", StringComparison.OrdinalIgnoreCase)
                    ? exception.Message
                    : UpstreamErrorMessageBuilder.BuildScryfallMessage(exception);

            return View("ChatGptDeckComparison", new ChatGptDeckComparisonViewModel
            {
                ActiveTab = DeckPageTab.ChatGptDeckComparison,
                Request = request,
                ErrorMessage = errorMessage
            });
        }
    }

    /// <summary>
    /// Attempts to load set options without surfacing catalog failures as page-breaking errors.
    /// </summary>
    private async Task<IReadOnlyList<ScryfallSetOption>> TryGetSetOptionsAsync()
    {
        try
        {
            return await _scryfallSetService.GetSetsAsync(HttpContext.RequestAborted);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Set catalog lookup failed.");
            return Array.Empty<ScryfallSetOption>();
        }
    }

    /// <summary>
    /// Verifies a pasted card list and either renders the page or returns a text download.
    /// </summary>
    /// <param name="request">Card verification request.</param>
    /// <param name="downloadFile">Whether the result should be returned as a file.</param>
    private async Task<IActionResult> RenderCardLookupAsync(CardLookupRequest request, bool downloadFile)
    {
        request ??= new CardLookupRequest();
        if (string.IsNullOrWhiteSpace(request.CardList))
        {
            return View("CardLookup", new CardLookupViewModel
            {
                ActiveTab = DeckPageTab.CardLookup,
                Request = request,
                ErrorMessage = "A card list is required.",
            });
        }

        try
        {
            var result = await _cardLookupService.LookupAsync(request.CardList, HttpContext.RequestAborted);
            if (downloadFile)
            {
                var output = BuildVerificationFile(result);
                var fileName = $"verified-cards-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
                return File(System.Text.Encoding.UTF8.GetBytes(output), "text/plain; charset=utf-8", fileName);
            }

            return View("CardLookup", new CardLookupViewModel
            {
                ActiveTab = DeckPageTab.CardLookup,
                Request = request,
                VerifiedText = string.Join(System.Environment.NewLine + System.Environment.NewLine, result.VerifiedOutputs),
                MissingText = string.Join(Environment.NewLine, result.MissingLines),
                FoundCount = result.VerifiedOutputs.Count,
                MissingCount = result.MissingLines.Count,
            });
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogInformation(exception, "Bulk card verification request failed validation.");
            return View("CardLookup", new CardLookupViewModel
            {
                ActiveTab = DeckPageTab.CardLookup,
                Request = request,
                ErrorMessage = exception.Message,
            });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Bulk card verification failed.");
            return View("CardLookup", new CardLookupViewModel
            {
                ActiveTab = DeckPageTab.CardLookup,
                Request = request,
                ErrorMessage = UpstreamErrorMessageBuilder.BuildScryfallMessage(exception),
            });
        }
    }

    /// <summary>
    /// Builds a downloadable text payload for verified and missing cards.
    /// </summary>
    /// <param name="result">Verification result.</param>
    private static string BuildVerificationFile(CardLookupResult result)
    {
        var lines = new List<string>
        {
            "Verified Cards"
        };

        lines.AddRange(result.VerifiedOutputs.Count == 0 ? ["(none)"] : result.VerifiedOutputs);
        lines.Add(string.Empty);
        lines.Add("Cards With Errors");
        lines.AddRange(result.MissingLines.Count == 0 ? ["(none)"] : result.MissingLines);
        return string.Join(Environment.NewLine, lines);
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
            var leftSystem = DeckSyncSupport.GetLeftPanelSystem(request.Direction);
            return View("DeckSync", new DeckDiffViewModel
            {
                ActiveTab = DeckPageTab.Sync,
                Request = request,
                ErrorMessage = request.MoxfieldInputSource == DeckInputSource.PublicUrl
                    ? $"A {leftSystem} deck URL is required."
                    : $"{leftSystem} text is required.",
            });
        }

        if (!HasArchidektInput(request))
        {
            var rightSystem = DeckSyncSupport.GetRightPanelSystem(request.Direction);
            return View("DeckSync", new DeckDiffViewModel
            {
                ActiveTab = DeckPageTab.Sync,
                Request = request,
                ErrorMessage = request.ArchidektInputSource == DeckInputSource.PublicUrl
                    ? $"A {rightSystem} deck URL is required."
                    : $"{rightSystem} text is required.",
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
