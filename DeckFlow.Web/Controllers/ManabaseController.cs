using System.Text;
using System.Text.RegularExpressions;
using DeckFlow.Core.Manabase;
using DeckFlow.Web.Infrastructure;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Bracket;
using DeckFlow.Web.Services.FeatureFlags;
using DeckFlow.Web.Services.Manabase;
using Microsoft.AspNetCore.Mvc;

namespace DeckFlow.Web.Controllers;

/// <summary>
/// Serves the standalone mana-base analysis page. Loads a deck, resolves its cards via
/// Scryfall, and renders the deterministic Karsten §6 report plus an optional ChatGPT
/// swap-suggestion prompt.
/// </summary>
public sealed class ManabaseController : DeckToolControllerBase
{
    private readonly IManabaseAnalysisService _manabaseAnalysisService;
    private readonly ICardSearchService _cardSearchService;
    private readonly IFeatureFlagCache _featureFlags;
    private readonly IBracketClassificationService _bracketClassification;
    private readonly PacketSessionCache _packetSessionCache;
    private readonly ILogger<ManabaseController> _logger;

    /// <summary>Creates the mana-base controller.</summary>
    public ManabaseController(
        IManabaseAnalysisService manabaseAnalysisService,
        ICardSearchService cardSearchService,
        IFeatureFlagCache featureFlags,
        IBracketClassificationService bracketClassification,
        ILogger<ManabaseController> logger,
        PacketSessionCache? packetSessionCache = null)
    {
        ArgumentNullException.ThrowIfNull(manabaseAnalysisService);
        ArgumentNullException.ThrowIfNull(cardSearchService);
        ArgumentNullException.ThrowIfNull(featureFlags);
        ArgumentNullException.ThrowIfNull(bracketClassification);
        ArgumentNullException.ThrowIfNull(logger);

        _manabaseAnalysisService = manabaseAnalysisService;
        _cardSearchService = cardSearchService;
        _featureFlags = featureFlags;
        _bracketClassification = bracketClassification;
        _logger = logger;
        _packetSessionCache = packetSessionCache ?? new PacketSessionCache();
    }

    /// <summary>Renders the empty mana-base form.</summary>
    [HttpGet("/manabase")]
    [FeatureFlagGate("tool.manabase.enabled")]
    public IActionResult Manabase(string? handoff = null)
    {
        bool focusedTierEnabled = IsFocusedTierEnabled();
        bool baselineEnabled = IsBaselineFlagEnabled();
        if (!string.IsNullOrWhiteSpace(handoff))
        {
            if (_packetSessionCache.TryGet<ManabaseHandoffPayload>(handoff, out var payload))
            {
                _logger.LogInformation("Mana-base handoff cache hit for {KeyPrefix}.", PacketSessionCache.GetKeyPrefix(handoff));
                var request = new ManabaseRequest
                {
                    DeckInputSource = DeckInputSource.PasteText,
                    DeckText = payload.DecklistText,
                    DeckName = payload.DeckName,
                    Mode = payload.Mode,
                };
                NormalizeKnobs(request, focusedTierEnabled);
                return View("Manabase", BuildAnalysisViewModel(request, payload.Result, [], focusedTierEnabled, baselineEnabled));
            }

            _logger.LogInformation("Mana-base handoff cache miss for {KeyPrefix}.", PacketSessionCache.GetKeyPrefix(handoff));
            return View("Manabase", new ManabaseViewModel
            {
                NoticeMessage = "That Deck Modules result expired. Please re-run the analysis.",
                ShowFocusedTier = focusedTierEnabled,
                ShowCommunityBaseline = baselineEnabled,
            });
        }

        return View("Manabase", new ManabaseViewModel
        {
            ShowFocusedTier = focusedTierEnabled,
            ShowCommunityBaseline = baselineEnabled,
        });
    }

    /// <summary>
    /// Loads the submitted deck and detects its reduced/alternative-cost suggestions WITHOUT running
    /// the analysis, so the user can review and edit the overrides before analyzing.
    /// </summary>
    /// <param name="request">The form-bound deck input.</param>
    [HttpPost("/manabase/load")]
    [ValidateAntiForgeryToken]
    [FeatureFlagGate("tool.manabase.enabled")]
    public async Task<IActionResult> Load(ManabaseRequest request)
    {
        request ??= new ManabaseRequest();
        bool focusedTierEnabled = IsFocusedTierEnabled();
        bool baselineEnabled = IsBaselineFlagEnabled();
        NormalizeKnobs(request, focusedTierEnabled);

        return await RunGuardedAsync(request, focusedTierEnabled, baselineEnabled, "load",
            "Something went wrong loading that deck. Please try again.",
            async token =>
            {
                var result = await _manabaseAnalysisService.LoadAsync(request.DeckSource, token);

                return View("Manabase", new ManabaseViewModel
                {
                    Request = request,
                    InputSummary = result.InputSummary,
                    Unresolved = result.Unresolved,
                    ImportWarning = result.ImportWarning,
                    Suggestions = result.Suggestions,
                    Loaded = true,
                    ShowFocusedTier = focusedTierEnabled,
                    ShowCommunityBaseline = baselineEnabled,
                });
            });
    }

    /// <summary>Runs the analysis for the submitted deck and renders the report.</summary>
    /// <param name="request">The form-bound deck input.</param>
    [HttpPost("/manabase")]
    [ValidateAntiForgeryToken]
    [FeatureFlagGate("tool.manabase.enabled")]
    public async Task<IActionResult> Manabase(ManabaseRequest request)
    {
        request ??= new ManabaseRequest();
        bool focusedTierEnabled = IsFocusedTierEnabled();
        bool baselineEnabled = IsBaselineFlagEnabled();
        NormalizeKnobs(request, focusedTierEnabled);

        return await RunGuardedAsync(request, focusedTierEnabled, baselineEnabled, "analysis",
            "Something went wrong analyzing that deck. Please try again.",
            async token =>
            {
                ManabaseCostOverrideParser.OverrideParseResult parsed =
                    ManabaseCostOverrideParser.ParseWithDiagnostics(request.CostOverridesText);
                var result = await RunAnalysisAsync(request, parsed.Overrides, token);
                if (result.CommanderSelectionRequired || result.Report is null)
                {
                    return View("Manabase", BuildCommanderSelectionViewModel(request, result, focusedTierEnabled, baselineEnabled));
                }

                // "Not applied" = lines the parser rejected (bad syntax) plus valid lines whose card
                // name matched no spell in the deck (typo / not in list). Both were previously silent.
                var notApplied = parsed.MalformedLines
                    .Concat(result.UnmatchedOverrideNames)
                    .ToList();

                return View("Manabase", BuildAnalysisViewModel(request, result, notApplied, focusedTierEnabled, baselineEnabled));
            });
    }

    private static ManabaseViewModel BuildAnalysisViewModel(
        ManabaseRequest request,
        ManabaseAnalysisResult result,
        IReadOnlyList<string> notAppliedOverrides,
        bool focusedTierEnabled,
        bool baselineEnabled)
        => new()
        {
            Request = request,
            Report = result.Report,
            InputSummary = result.InputSummary,
            Unresolved = result.Unresolved,
            ImportWarning = result.ImportWarning,
            PromptSwapPrompt = result.PromptSwapPrompt,
            Suggestions = result.Suggestions,
            PlainLanguageVerdict = result.Verdict,
            RampDrawBudget = result.Budget,
            ShowPlainLanguage = result.ShowPlainLanguage,
            ShowCommanderCastability = result.CommanderCastabilityEnabled,
            ShowTapAnalyzer = result.ShowTapAnalyzer,
            ShowMulliganEval = result.ShowMulliganEval,
            ShowPlanPresence = result.ShowPlanPresence,
            ShowKeepShapes = result.ShowKeepShapes,
            ShowFocusedTier = focusedTierEnabled,
            ShowSourceList = result.ShowSourceList,
            ShowCedhInteractionLens = result.ShowCedhInteractionLens,
            CompanionCallout = result.CompanionRow,
            CommunityBaseline = result.CommunityBaseline,
            ShowCommunityBaseline = baselineEnabled,
            NotAppliedOverrides = notAppliedOverrides,
        };

    /// <summary>
    /// Re-runs the mana-base analysis for the submitted deck and returns the full report as a
    /// paste-ready text file attachment (<c>manabase-analysis-{timestamp}.txt</c>). Mirrors the
    /// analyze action body exactly so the download and the on-page verdict are always consistent.
    /// Failures re-render the Manabase view with a friendly error rather than returning a 500.
    /// </summary>
    /// <param name="request">The form-bound deck input (re-posted by the mini download form).</param>
    [HttpPost("/manabase/download")]
    [ValidateAntiForgeryToken]
    [FeatureFlagGate("tool.manabase.enabled")]
    public async Task<IActionResult> Download(ManabaseRequest request)
    {
        request ??= new ManabaseRequest();
        bool focusedTierEnabled = IsFocusedTierEnabled();
        bool baselineEnabled = IsBaselineFlagEnabled();
        NormalizeKnobs(request, focusedTierEnabled);

        return await RunGuardedAsync(request, focusedTierEnabled, baselineEnabled, "download",
            "Something went wrong analyzing that deck. Please try again.",
            async token =>
            {
                var result = await RunAnalysisAsync(
                    request, ManabaseCostOverrideParser.Parse(request.CostOverridesText), token);
                if (result.CommanderSelectionRequired || result.Report is null)
                {
                    return View("Manabase", BuildCommanderSelectionViewModel(request, result, focusedTierEnabled, baselineEnabled));
                }

                var commanderNames = result.Report.Castability
                    .Where(row => row.IsCommander && !string.IsNullOrWhiteSpace(row.Name))
                    .Select(row => row.Name.Trim())
                    .ToList();
                var displayName = !string.IsNullOrWhiteSpace(request.DeckName)
                    ? request.DeckName.Trim()
                    : commanderNames.Count > 0
                        ? string.Join(" & ", commanderNames)
                        : null;
                string text = ManabaseReportTextBuilder.Build(
                    result.Report, displayName, decklistText: null, request.Mode, result.Verdict, result.Budget,
                    tap: result.ShowTapAnalyzer ? result.Report.TapAnalysis : null,
                    interactionLens: result.Report.InteractionLens,
                    mulligan: result.ShowMulliganEval ? result.Report.MulliganEvaluation : null,
                    includeCommandZone: result.CommanderCastabilityEnabled,
                    companionRow: result.CompanionRow,
                    includePlanPresence: result.ShowPlanPresence,
                    includeCedhKeepShapes: result.ShowKeepShapes);
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                string fileName = displayName is null
                    ? $"manabase-analysis-{timestamp}.txt"
                    : $"{SanitizeFileSegment(displayName)}-manabase-{timestamp}.txt";
                Response.Headers["X-DeckFlow-Filename"] = fileName;

                return File(
                    Encoding.UTF8.GetBytes(text),
                    "text/plain; charset=utf-8",
                    fileName);
            });
    }

    private static string SanitizeFileSegment(string value)
    {
        var lower = value.ToLowerInvariant();
        var sanitized = Regex.Replace(lower, "[^a-z0-9]+", "-");
        sanitized = Regex.Replace(sanitized, "-{2,}", "-").Trim('-');
        if (sanitized.Length > 40)
        {
            sanitized = sanitized[..40].TrimEnd('-');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "manabase-analysis" : sanitized;
    }

    /// <summary>
    /// Returns commander-eligible card name suggestions for the mana-base commander picker.
    /// </summary>
    /// <param name="q">Partial commander name.</param>
    [HttpGet("/manabase/commander-search")]
    [FeatureFlagGate("tool.manabase.enabled")]
    public async Task<IActionResult> CommanderSearch(string q)
    {
        try
        {
            var names = await _cardSearchService.SearchCommandersAsync(q ?? string.Empty, HttpContext.RequestAborted);
            return Json(names);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Commander search autocomplete failed for mana-base query {Query}.", q);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                Message = UpstreamErrorMessageBuilder.BuildScryfallMessage(exception)
            });
        }
    }

    // MEDIUM-1: a hand-crafted post can carry an out-of-range enum value (model binding does not
    // reject unknown ints). Coerce both knobs back to their defaults and write the normalized values
    // onto the request so every action runs a valid mode AND the view re-renders the correct radios
    // (an invalid Mode would otherwise drop the castability table and un-check both radios).
    private static void NormalizeKnobs(ManabaseRequest request, bool focusedTierEnabled)
    {
        request.Mode = Enum.IsDefined(typeof(ManabaseMode), request.Mode) ? request.Mode : ManabaseMode.Casual;
        if (request.Mode == ManabaseMode.Focused && !focusedTierEnabled)
        {
            request.Mode = ManabaseMode.Casual;
        }

        request.CommanderImportance = Enum.IsDefined(typeof(CommanderImportance), request.CommanderImportance)
            ? request.CommanderImportance
            : CommanderImportance.Standard;
        request.Bracket = request.Bracket is >= 2 and <= 5 ? request.Bracket : null;
    }

    /// <summary>
    /// Runs the shared analyze pipeline with the request's mode, importance, and the already-parsed
    /// cost overrides. Callers parse the box text themselves (the analyze action keeps the malformed
    /// lines for "not applied" feedback; the download re-parses without diagnostics) so both paths
    /// feed the same analyzer with identical overrides.
    /// </summary>
    private async Task<ManabaseAnalysisResult> RunAnalysisAsync(
        ManabaseRequest request,
        IReadOnlyDictionary<string, string> overrides,
        CancellationToken cancellationToken)
    {
        (int? bracket, ManabaseBracketSource? bracketSource) =
            await ResolveEffectiveBracketAsync(request, cancellationToken);

        return await _manabaseAnalysisService.AnalyzeAsync(
            request.DeckSource,
            request.DeckName,
            new ManabaseAnalysisOptions
            {
                Mode = request.Mode,
                CommanderImportance = request.CommanderImportance,
                CompanionDesignator = request.CompanionName,
                SelectedCommander = request.SelectedCommander,
                CostOverrides = overrides,
                Bracket = bracket,
                BracketSource = bracketSource,
            },
            cancellationToken);
    }

    // Selecting a commander is a routine interactive prompt, not an error, so this leaves
    // ErrorMessage null (no role="alert" banner) — the picker panel is the sole message.
    private static ManabaseViewModel BuildCommanderSelectionViewModel(
        ManabaseRequest request,
        ManabaseAnalysisResult result,
        bool focusedTierEnabled,
        bool baselineEnabled)
        => new()
        {
            Request = request,
            InputSummary = result.InputSummary,
            Unresolved = result.Unresolved,
            ImportWarning = result.ImportWarning,
            Suggestions = result.Suggestions,
            CommanderSelectionRequired = true,
            CommanderChoices = result.CommanderChoices,
            ShowCommanderCastability = result.CommanderCastabilityEnabled,
            ShowTapAnalyzer = result.ShowTapAnalyzer,
            ShowMulliganEval = result.ShowMulliganEval,
            ShowPlanPresence = result.ShowPlanPresence,
            ShowKeepShapes = result.ShowKeepShapes,
            ShowFocusedTier = focusedTierEnabled,
            ShowSourceList = result.ShowSourceList,
            ShowCedhInteractionLens = result.ShowCedhInteractionLens,
            ShowCommunityBaseline = baselineEnabled,
        };

    /// <summary>
    /// Wraps a mana-base action body in the shared request timeout scope and the friendly error
    /// ladder so every entry point (load/analyze/download) renders the same recoverable errors
    /// instead of a raw 500. <paramref name="operation"/> names the action for log messages and
    /// <paramref name="unexpectedMessage"/> is the copy shown for an unhandled fault.
    /// </summary>
    private async Task<IActionResult> RunGuardedAsync(
        ManabaseRequest request,
        bool focusedTierEnabled,
        bool baselineEnabled,
        string operation,
        string unexpectedMessage,
        Func<CancellationToken, Task<IActionResult>> body)
    {
        using var timeoutScope = CreateTimeoutScope(LookupTimeout);

        try
        {
            return await body(timeoutScope.Token);
        }
        catch (OperationCanceledException) when (timeoutScope.IsCancellationRequested)
        {
            _logger.LogInformation("Mana-base {Operation} timed out.", operation);
            return View("Manabase", new ManabaseViewModel
            {
                Request = request,
                ErrorMessage = "The deck took too long to load. Try again in a moment.",
                ShowFocusedTier = focusedTierEnabled,
                ShowCommunityBaseline = baselineEnabled,
            });
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogInformation(exception, "Mana-base {Operation} failed validation.", operation);
            return View("Manabase", new ManabaseViewModel
            {
                Request = request,
                ErrorMessage = exception.Message,
                ShowFocusedTier = focusedTierEnabled,
                ShowCommunityBaseline = baselineEnabled,
            });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Mana-base {Operation} hit an upstream dependency.", operation);
            return View("Manabase", new ManabaseViewModel
            {
                Request = request,
                ErrorMessage = UpstreamErrorMessageBuilder.BuildScryfallMessage(exception),
                ShowFocusedTier = focusedTierEnabled,
                ShowCommunityBaseline = baselineEnabled,
            });
        }
        catch (Exception exception)
        {
            // Last-resort boundary so an unexpected parser/runtime fault renders a friendly
            // error on this public form instead of a raw 500.
            _logger.LogError(exception, "Mana-base {Operation} failed unexpectedly.", operation);
            return View("Manabase", new ManabaseViewModel
            {
                Request = request,
                ErrorMessage = unexpectedMessage,
                ShowFocusedTier = focusedTierEnabled,
                ShowCommunityBaseline = baselineEnabled,
            });
        }
    }

    private bool IsBaselineFlagEnabled()
        => _featureFlags.Snapshot().TryGetValue(ManabaseAnalysisService.BaselineFlagKey, out bool enabled)
            && enabled;

    private async Task<(int? Bracket, ManabaseBracketSource? Source)> ResolveEffectiveBracketAsync(
        ManabaseRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Bracket is int chosen)
        {
            return (chosen, ManabaseBracketSource.Override);
        }

        if (!IsBaselineFlagEnabled())
        {
            return (null, null);
        }

        try
        {
            BracketClassificationResult classification = await _bracketClassification.ClassifyAsync(
                request.DeckSource,
                targetBracketNumber: null,
                platform: "manabase",
                deckName: request.DeckName,
                cancellationToken);
            int bracket = Math.Max(2, classification.Classification.BracketNumber);
            return (bracket, ManabaseBracketSource.Auto);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Manabase bracket auto-classification failed; using mode-derived bracket.");
            return (null, null);
        }
    }

    private bool IsFocusedTierEnabled()
        => _featureFlags.Snapshot().TryGetValue(ManabaseAnalysisService.FocusedTierFlagKey, out bool enabled)
            && enabled;
}
