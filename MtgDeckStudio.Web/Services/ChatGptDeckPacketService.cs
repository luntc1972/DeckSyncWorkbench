using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Parsing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RestSharp;
using MtgDeckStudio.Web.Models;

namespace MtgDeckStudio.Web.Services;

/// <summary>
/// Defines the staged prompt-building workflow used by the ChatGPT deck-analysis page.
/// </summary>
public interface IChatGptDeckPacketService
{
    /// <summary>
    /// Builds the next packet outputs for the supplied workflow state.
    /// </summary>
    /// <param name="request">Current workflow request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ChatGptDeckPacketResult> BuildAsync(ChatGptDeckRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Contains the generated packet outputs and saved-artifact location for a workflow run.
/// </summary>
public sealed record ChatGptDeckPacketResult(
    string InputSummary,
    string SuggestedChatTitle,
    string DeckProfileSchemaJson,
    string? ReferenceText,
    string? AnalysisPromptText,
    string? SetUpgradePromptText,
    string? SavedArtifactsDirectory,
    string? TimingSummary,
    ChatGptDeckAnalysisResponse? AnalysisResponse = null,
    ChatGptSetUpgradeResponse? SetUpgradeResponse = null);

/// <summary>
/// Builds analysis and set-upgrade prompts plus supporting reference data for ChatGPT.
/// </summary>
public sealed partial class ChatGptDeckPacketService : IChatGptDeckPacketService
{
    private const int ScryfallBatchSize = 75;
    private static readonly Regex AbilityWordRegex = AbilityWordPattern();
    private readonly IMoxfieldDeckImporter _moxfieldDeckImporter;
    private readonly IArchidektDeckImporter _archidektDeckImporter;
    private readonly MoxfieldParser _moxfieldParser;
    private readonly ArchidektParser _archidektParser;
    private readonly IMechanicLookupService _mechanicLookupService;
    private readonly ICommanderBanListService _commanderBanListService;
    private readonly IScryfallSetService _scryfallSetService;
    private readonly ICommanderSpellbookService _commanderSpellbookService;
    private readonly string _chatGptArtifactsPath;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> _executeCollectionAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>> _executeSearchAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCard>>> _executeNamedAsync;
    private readonly ILogger<ChatGptDeckPacketService> _logger;

    /// <summary>
    /// Creates the ChatGPT packet service with the importers, lookup services, and persistence settings it needs.
    /// </summary>
    public ChatGptDeckPacketService(
        IMoxfieldDeckImporter moxfieldDeckImporter,
        IArchidektDeckImporter archidektDeckImporter,
        MoxfieldParser moxfieldParser,
        ArchidektParser archidektParser,
        IMechanicLookupService mechanicLookupService,
        ICommanderBanListService commanderBanListService,
        IScryfallSetService scryfallSetService,
        ICommanderSpellbookService commanderSpellbookService,
        IWebHostEnvironment environment,
        ILogger<ChatGptDeckPacketService>? logger = null,
        RestClient? scryfallRestClient = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeSearchAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCard>>>? executeNamedAsync = null,
        string? chatGptArtifactsPath = null)
    {
        _moxfieldDeckImporter = moxfieldDeckImporter;
        _archidektDeckImporter = archidektDeckImporter;
        _moxfieldParser = moxfieldParser;
        _archidektParser = archidektParser;
        _mechanicLookupService = mechanicLookupService;
        _commanderBanListService = commanderBanListService;
        _scryfallSetService = scryfallSetService;
        _commanderSpellbookService = commanderSpellbookService;
        _logger = logger ?? NullLogger<ChatGptDeckPacketService>.Instance;
        _chatGptArtifactsPath = ResolveChatGptArtifactsPath(chatGptArtifactsPath);
        var client = scryfallRestClient ?? ScryfallRestClientFactory.Create();
        _executeCollectionAsync = executeCollectionAsync
            ?? ((request, cancellationToken) => ExecuteScryfallWithThrottleAsync(token => client.ExecuteAsync<ScryfallCollectionResponse>(request, token), cancellationToken));
        _executeSearchAsync = executeSearchAsync
            ?? ((request, cancellationToken) => ExecuteScryfallWithThrottleAsync(token => client.ExecuteAsync<ScryfallSearchResponse>(request, token), cancellationToken));
        _executeNamedAsync = executeNamedAsync
            ?? ((request, cancellationToken) => ExecuteScryfallWithThrottleAsync(token => client.ExecuteAsync<ScryfallCard>(request, token), cancellationToken));
    }

    private static string ResolveChatGptArtifactsPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var dataDir = Environment.GetEnvironmentVariable("MTG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return Path.Combine(Path.GetFullPath(dataDir), "ChatGPT Analysis");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MTG Deck Studio",
            "ChatGPT Analysis");
    }

    // Keep under Scryfall's 10 req/sec cap with a small safety margin (≈ 9 req/sec).
    private static readonly TimeSpan ScryfallMinInterval = TimeSpan.FromMilliseconds(110);
    // Honor Retry-After up to this cap; longer cooldowns fall through and surface as a rate-limit error.
    private static readonly TimeSpan ScryfallRetryAfterCap = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim ScryfallThrottle = new(1, 1);
    private static DateTime _lastScryfallCallUtc = DateTime.MinValue;

    private static async Task<RestResponse<T>> ExecuteScryfallWithThrottleAsync<T>(
        Func<CancellationToken, Task<RestResponse<T>>> execute,
        CancellationToken cancellationToken)
    {
        var response = await ExecuteScryfallThrottledOnceAsync(execute, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode != 429)
        {
            return response;
        }

        var retryAfter = ReadRetryAfter(response);
        if (retryAfter is null || retryAfter.Value > ScryfallRetryAfterCap)
        {
            return response;
        }

        await Task.Delay(retryAfter.Value, cancellationToken).ConfigureAwait(false);
        return await ExecuteScryfallThrottledOnceAsync(execute, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RestResponse<T>> ExecuteScryfallThrottledOnceAsync<T>(
        Func<CancellationToken, Task<RestResponse<T>>> execute,
        CancellationToken cancellationToken)
    {
        await ScryfallThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var elapsedSinceLast = DateTime.UtcNow - _lastScryfallCallUtc;
            if (elapsedSinceLast < ScryfallMinInterval)
            {
                await Task.Delay(ScryfallMinInterval - elapsedSinceLast, cancellationToken).ConfigureAwait(false);
            }

            var result = await execute(cancellationToken).ConfigureAwait(false);
            _lastScryfallCallUtc = DateTime.UtcNow;
            return result;
        }
        finally
        {
            ScryfallThrottle.Release();
        }
    }

    private static TimeSpan? ReadRetryAfter<T>(RestResponse<T> response)
    {
        var header = response.Headers?.FirstOrDefault(h => string.Equals(h.Name, "Retry-After", StringComparison.OrdinalIgnoreCase));
        if (header?.Value is string raw && int.TryParse(raw, out var seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }
        return null;
    }

    /// <summary>
    /// Builds the requested prompt outputs for the current workflow state.
    /// </summary>
    /// <param name="request">Current workflow request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<ChatGptDeckPacketResult> BuildAsync(ChatGptDeckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var overallStopwatch = Stopwatch.StartNew();
        var timings = new List<(string Label, long Ms, string? Detail)>();

        if (!string.IsNullOrWhiteSpace(request.ImportArtifactsPath))
        {
            ImportSavedArtifacts(request);
            // Do not re-import on downstream branches (e.g., artifact save) that read the request again.
            request.ImportArtifactsPath = string.Empty;
        }

        if (request.WorkflowStep == 3
            && string.IsNullOrWhiteSpace(request.DeckSource)
            && !string.IsNullOrWhiteSpace(request.DeckProfileJson))
        {
            var savedAnalysisResponse = ParseAnalysisResponse(request.DeckProfileJson);
            var savedDeckProfileSchemaJson = BuildDeckProfileSchemaJson(
                string.IsNullOrWhiteSpace(savedAnalysisResponse.Commander) ? null : savedAnalysisResponse.Commander,
                string.IsNullOrWhiteSpace(savedAnalysisResponse.Format) ? request.Format : savedAnalysisResponse.Format,
                savedAnalysisResponse.DeckVersions.Count > 0);
            string? savedArtifactsDirectoryForStep3 = null;
            if (request.SaveArtifactsToDisk)
            {
                savedArtifactsDirectoryForStep3 = await SaveArtifactsAsync(
                    request,
                    string.IsNullOrWhiteSpace(savedAnalysisResponse.Commander) ? null : savedAnalysisResponse.Commander,
                    BuildAnalysisSummaryFromSavedJson(savedAnalysisResponse),
                    referenceText: null,
                    analysisPromptText: null,
                    savedDeckProfileSchemaJson,
                    setUpgradePromptText: null,
                    cancellationToken).ConfigureAwait(false);
            }
            var savedTimingSummary = BuildTimingSummary(timings, overallStopwatch.ElapsedMilliseconds);
            return new ChatGptDeckPacketResult(
                InputSummary: BuildAnalysisSummaryFromSavedJson(savedAnalysisResponse),
                SuggestedChatTitle: BuildSuggestedChatTitle(request, savedAnalysisResponse.Commander),
                DeckProfileSchemaJson: savedDeckProfileSchemaJson,
                ReferenceText: null,
                AnalysisPromptText: null,
                SetUpgradePromptText: null,
                SavedArtifactsDirectory: savedArtifactsDirectoryForStep3,
                TimingSummary: savedTimingSummary,
                AnalysisResponse: savedAnalysisResponse);
        }

        if (request.WorkflowStep == 5
            && string.IsNullOrWhiteSpace(request.DeckSource)
            && !string.IsNullOrWhiteSpace(request.SetUpgradeResponseJson))
        {
            var savedSetUpgradeResponse = ParseSetUpgradeResponse(request.SetUpgradeResponseJson);
            var savedAnalysisResponse = string.IsNullOrWhiteSpace(request.DeckProfileJson)
                ? null
                : ParseAnalysisResponse(request.DeckProfileJson);
            var step5Commander = savedAnalysisResponse is null || string.IsNullOrWhiteSpace(savedAnalysisResponse.Commander)
                ? null
                : savedAnalysisResponse.Commander;
            var step5DeckProfileSchemaJson = BuildDeckProfileSchemaJson(
                step5Commander,
                savedAnalysisResponse is null || string.IsNullOrWhiteSpace(savedAnalysisResponse.Format) ? request.Format : savedAnalysisResponse.Format,
                (savedAnalysisResponse?.DeckVersions.Count ?? 0) > 0);
            var step5InputSummary = savedAnalysisResponse is null
                ? string.Empty
                : BuildAnalysisSummaryFromSavedJson(savedAnalysisResponse);
            string? savedArtifactsDirectoryForStep5 = null;
            if (request.SaveArtifactsToDisk)
            {
                savedArtifactsDirectoryForStep5 = await SaveArtifactsAsync(
                    request,
                    step5Commander,
                    step5InputSummary,
                    referenceText: null,
                    analysisPromptText: null,
                    step5DeckProfileSchemaJson,
                    setUpgradePromptText: null,
                    cancellationToken).ConfigureAwait(false);
            }
            var savedTimingSummary = BuildTimingSummary(timings, overallStopwatch.ElapsedMilliseconds);
            return new ChatGptDeckPacketResult(
                InputSummary: step5InputSummary,
                SuggestedChatTitle: BuildSuggestedChatTitle(request, savedAnalysisResponse?.Commander),
                DeckProfileSchemaJson: step5DeckProfileSchemaJson,
                ReferenceText: null,
                AnalysisPromptText: null,
                SetUpgradePromptText: null,
                SavedArtifactsDirectory: savedArtifactsDirectoryForStep5,
                TimingSummary: savedTimingSummary,
                AnalysisResponse: savedAnalysisResponse,
                SetUpgradeResponse: savedSetUpgradeResponse);
        }

        if (string.IsNullOrWhiteSpace(request.DeckSource))
        {
            throw new InvalidOperationException("A deck URL or pasted deck export is required.");
        }

        var loadDeckStopwatch = Stopwatch.StartNew();
        var entries = await LoadDeckEntriesAsync(request.DeckSource, cancellationToken).ConfigureAwait(false);
        timings.Add(("Deck load", loadDeckStopwatch.ElapsedMilliseconds, null));
        _logger.LogInformation("ChatGPT packet deck load completed in {ElapsedMs}ms.", loadDeckStopwatch.ElapsedMilliseconds);
        var deckEntries = entries
            .Where(entry =>
                !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var possibleIncludeEntries = entries
            .Where(entry =>
                string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (deckEntries.Count == 0)
        {
            throw new InvalidOperationException("The submitted deck did not contain any commander or mainboard cards.");
        }

        var commanderName = deckEntries
            .FirstOrDefault(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            ?.Name;
        var inferredCommanderFromMoxfieldOrdering = false;

        // Fallback for Moxfield exports without a Commander section header.
        // By convention the commander (or partner pair) appears first in the list.
        if (commanderName is null && entries.Count > 0)
        {
            var leadingOneOfs = entries
                .TakeWhile(entry => entry.Quantity == 1
                    && !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();

            // If two candidates were found, confirm the second is a partner commander and not the
            // first card of an A-Z-sorted mainboard. When the second entry sorts alphabetically
            // before the third entry it fits naturally in a sorted mainboard sequence; in that case
            // only the first entry is the commander.
            if (leadingOneOfs.Count == 2 && entries.Count > 2)
            {
                var thirdEntry = entries[2];
                if (string.Compare(leadingOneOfs[1].Name, thirdEntry.Name, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    leadingOneOfs = leadingOneOfs.Take(1).ToList();
                }
            }

            if (leadingOneOfs.Count > 0)
            {
                var commanderNames = leadingOneOfs.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                entries = entries
                    .Select(entry => commanderNames.Contains(entry.Name)
                        ? entry with { Board = "commander" }
                        : entry)
                    .ToList();
                deckEntries = deckEntries
                    .Select(entry => commanderNames.Contains(entry.Name)
                        ? entry with { Board = "commander" }
                        : entry)
                    .ToList();
                commanderName = leadingOneOfs[0].Name;
                inferredCommanderFromMoxfieldOrdering = true;
            }
        }

        if (string.Equals(request.Format, "Commander", StringComparison.OrdinalIgnoreCase) && inferredCommanderFromMoxfieldOrdering)
        {
            var inferredCommanderNames = entries
                .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (inferredCommanderNames.Count <= 1)
            {
                var validatedCommanderName = await ValidateCommanderAsync(entries, commanderName, cancellationToken).ConfigureAwait(false);
                commanderName = validatedCommanderName;
                entries = entries
                    .Select(entry => string.Equals(entry.Name, validatedCommanderName, StringComparison.OrdinalIgnoreCase)
                        ? entry with { Board = "commander" }
                        : string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase)
                            ? entry with { Board = "main" }
                            : entry)
                    .ToList();
            }
            else
            {
                foreach (var inferredCommander in inferredCommanderNames)
                {
                    await ValidateCommanderAsync(entries, inferredCommander, cancellationToken).ConfigureAwait(false);
                }

                commanderName = inferredCommanderNames[0];
            }

            deckEntries = entries
                .Where(entry =>
                    !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
                .ToList();
            possibleIncludeEntries = entries
                .Where(entry =>
                    string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var inputSummary = BuildInputSummary(request, deckEntries, possibleIncludeEntries, commanderName);
        var decklistText = BuildDecklistText(deckEntries, possibleIncludeEntries);
        var requiresFullDecklists = AnalysisQuestionCatalog.RequiresFullDecklistOutput(request.SelectedAnalysisQuestions);
        var deckProfileSchemaJson = BuildDeckProfileSchemaJson(commanderName, request.Format, requiresFullDecklists);

        string? referenceText = null;
        string? analysisPromptText = null;
        string? setUpgradePromptText = null;
        string? savedArtifactsDirectory = null;
        ChatGptDeckAnalysisResponse? analysisResponse = null;
        ChatGptSetUpgradeResponse? setUpgradeResponse = null;

        if (request.WorkflowStep >= 3 && !string.IsNullOrWhiteSpace(request.DeckProfileJson))
        {
            analysisResponse = ParseAnalysisResponse(request.DeckProfileJson);
        }

        if (request.WorkflowStep >= 5 && !string.IsNullOrWhiteSpace(request.SetUpgradeResponseJson))
        {
            setUpgradeResponse = ParseSetUpgradeResponse(request.SetUpgradeResponseJson);
        }

        var deckProfileText = string.IsNullOrWhiteSpace(request.DeckProfileJson)
            ? deckProfileSchemaJson
            : ExtractJsonObject(request.DeckProfileJson);
        var selectedQuestions = AnalysisQuestionCatalog.NormalizeSelections(request.SelectedAnalysisQuestions);
        var wantsAnalysisPacket = request.WorkflowStep == 2;
        var wantsSetUpgradeOnly = request.WorkflowStep < 2
            && (!string.IsNullOrWhiteSpace(request.DeckProfileJson) || !string.IsNullOrWhiteSpace(request.SetPacketText));
        var wantsSetUpgradePacket = request.WorkflowStep == 4 || wantsSetUpgradeOnly;

        if (wantsAnalysisPacket && CommanderBracketCatalog.Find(request.TargetCommanderBracket) is null)
        {
            throw new InvalidOperationException("Choose a target Commander bracket before generating the analysis packet.");
        }

        if (wantsAnalysisPacket && selectedQuestions.Count == 0 && string.IsNullOrWhiteSpace(request.FreeformQuestion))
        {
            throw new InvalidOperationException("Select at least one analysis question before generating the analysis packet.");
        }

        if (wantsAnalysisPacket
            && selectedQuestions.Any(questionId => questionId is "card-worth-it" or "better-alternatives")
            && string.IsNullOrWhiteSpace(request.CardSpecificQuestionCardName))
        {
            throw new InvalidOperationException("Enter a card name for the selected card-specific analysis questions.");
        }

        if (wantsAnalysisPacket
            && AnalysisQuestionCatalog.RequiresCategoryOutput(selectedQuestions)
            && string.IsNullOrWhiteSpace(request.DecklistExportFormat))
        {
            throw new InvalidOperationException("Choose Moxfield or Archidekt as the export format when assigning or updating categories — plain text does not support inline category formatting.");
        }

        if (wantsAnalysisPacket
            && selectedQuestions.Contains("budget-upgrades", StringComparer.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.BudgetUpgradeAmount))
        {
            throw new InvalidOperationException("Enter a budget amount for the selected budget upgrade question.");
        }

        // Only fetch banned list and set packet when the analysis or set-upgrade step actually needs them.
        if (wantsAnalysisPacket || wantsSetUpgradePacket)
        {
            // Fire banned-list and set-packet fetches in parallel — neither depends on the other.
            var parallelStopwatch = Stopwatch.StartNew();
            var bannedCardsTask = _commanderBanListService.GetBannedCardsAsync(cancellationToken);
            var setPacketTask = BuildGeneratedSetPacketAsync(request, cancellationToken);
            await Task.WhenAll(bannedCardsTask, setPacketTask).ConfigureAwait(false);
            timings.Add(("Ban list + set packet", parallelStopwatch.ElapsedMilliseconds, null));
            _logger.LogInformation("ChatGPT packet banned-list + set-packet fetch completed in {ElapsedMs}ms.", parallelStopwatch.ElapsedMilliseconds);
            var bannedCards = bannedCardsTask.Result;
            var generatedSetPacket = setPacketTask.Result;

            if (wantsAnalysisPacket)
            {
                var analysisPossibleIncludeEntries = possibleIncludeEntries
                    .Where(entry =>
                        (request.IncludeSideboardInAnalysis && string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
                        || (request.IncludeMaybeboardInAnalysis && string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                var cardReferenceRequests = BuildAnalysisCardReferenceRequests(deckEntries, analysisPossibleIncludeEntries);

                // Start combo lookup immediately — only needs deckEntries, independent of Scryfall lookups.
                var comboStopwatch = Stopwatch.StartNew();
                var comboTask = AnalysisQuestionCatalog.RequiresComboLookup(selectedQuestions)
                    ? _commanderSpellbookService.FindCombosAsync(deckEntries, cancellationToken)
                    : Task.FromResult<CommanderSpellbookResult?>(null);

                var cardReferenceStopwatch = Stopwatch.StartNew();
                var cardReferenceBundle = await LookupCardReferencesAsync(cardReferenceRequests, cancellationToken).ConfigureAwait(false);
                timings.Add(("Scryfall card lookup", cardReferenceStopwatch.ElapsedMilliseconds, $"{cardReferenceBundle.CardReferences.Count} cards, {cardReferenceBundle.MechanicNames.Count} mechanics found"));
                _logger.LogInformation(
                    "ChatGPT packet card reference lookup completed in {ElapsedMs}ms for {CardCount} cards and {MechanicCount} mechanics.",
                    cardReferenceStopwatch.ElapsedMilliseconds,
                    cardReferenceBundle.CardReferences.Count,
                    cardReferenceBundle.MechanicNames.Count);
                var mechanicReferenceStopwatch = Stopwatch.StartNew();
                var mechanicReferences = await LookupMechanicReferencesAsync(cardReferenceBundle.MechanicNames, cancellationToken).ConfigureAwait(false);
                timings.Add(("Mechanic rules lookup", mechanicReferenceStopwatch.ElapsedMilliseconds, $"{mechanicReferences.Count} mechanics resolved"));
                _logger.LogInformation(
                    "ChatGPT packet mechanic lookup completed in {ElapsedMs}ms for {MechanicCount} mechanics.",
                    mechanicReferenceStopwatch.ElapsedMilliseconds,
                    mechanicReferences.Count);

                referenceText = BuildReferenceText(request, mechanicReferences, cardReferenceBundle.CardReferences, bannedCards);

                var comboResult = await comboTask.ConfigureAwait(false);
                if (AnalysisQuestionCatalog.RequiresComboLookup(selectedQuestions))
                {
                    timings.Add(("Commander Spellbook", comboStopwatch.ElapsedMilliseconds, $"{comboResult?.IncludedCombos.Count ?? 0} combos, {comboResult?.AlmostIncludedCombos.Count ?? 0} near-combos"));
                }
                _logger.LogInformation(
                    "Commander Spellbook lookup completed in {ElapsedMs}ms. Included={Included} AlmostIncluded={AlmostIncluded}.",
                    comboStopwatch.ElapsedMilliseconds,
                    comboResult?.IncludedCombos.Count ?? 0,
                    comboResult?.AlmostIncludedCombos.Count ?? 0);

                // Resolve commander name to oracle name if the deck used a renamed printing.
                if (commanderName is not null && cardReferenceBundle.OracleNameMap.TryGetValue(commanderName, out var oracleCommanderName))
                {
                    commanderName = oracleCommanderName;
                }

                var includeCardVersions = AnalysisQuestionCatalog.RequiresFullDecklistOutput(selectedQuestions) && request.IncludeCardVersions;
                var analysisDecklistText = includeCardVersions
                    ? BuildDecklistText(deckEntries, analysisPossibleIncludeEntries, includeVersions: true, oracleNameMap: cardReferenceBundle.OracleNameMap)
                    : BuildDecklistText(deckEntries, analysisPossibleIncludeEntries, oracleNameMap: cardReferenceBundle.OracleNameMap);
                analysisPromptText = BuildAnalysisPrompt(request, analysisDecklistText, referenceText, deckProfileSchemaJson, commanderName, selectedQuestions, bannedCards, comboResult, includeCardVersions);
                if (wantsSetUpgradePacket)
                {
                    var oracleResolvedDecklistText = BuildDecklistText(deckEntries, possibleIncludeEntries, oracleNameMap: cardReferenceBundle.OracleNameMap);
                    setUpgradePromptText = BuildSetUpgradePrompt(request, oracleResolvedDecklistText, deckProfileText, commanderName, generatedSetPacket, bannedCards);
                }
            }
            else if (wantsSetUpgradePacket)
            {
                setUpgradePromptText = BuildSetUpgradePrompt(request, decklistText, deckProfileText, commanderName, generatedSetPacket, bannedCards);
            }
        }

        if (request.SaveArtifactsToDisk)
        {
            var saveArtifactsStopwatch = Stopwatch.StartNew();
            savedArtifactsDirectory = await SaveArtifactsAsync(
                request,
                commanderName,
                inputSummary,
                referenceText,
                analysisPromptText,
                deckProfileSchemaJson,
                setUpgradePromptText,
                cancellationToken).ConfigureAwait(false);
            timings.Add(("Artifact save", saveArtifactsStopwatch.ElapsedMilliseconds, null));
            _logger.LogInformation("ChatGPT packet artifact save completed in {ElapsedMs}ms.", saveArtifactsStopwatch.ElapsedMilliseconds);
        }

        _logger.LogInformation(
            "ChatGPT packet build completed in {ElapsedMs}ms. AnalysisGenerated={AnalysisGenerated} SetPacketGenerated={SetPacketGenerated}.",
            overallStopwatch.ElapsedMilliseconds,
            !string.IsNullOrWhiteSpace(analysisPromptText),
            !string.IsNullOrWhiteSpace(setUpgradePromptText));

        var timingSummary = BuildTimingSummary(timings, overallStopwatch.ElapsedMilliseconds);

        var suggestedChatTitle = BuildSuggestedChatTitle(request, commanderName);

        return new ChatGptDeckPacketResult(
            inputSummary,
            suggestedChatTitle,
            deckProfileSchemaJson,
            referenceText,
            analysisPromptText,
            setUpgradePromptText,
            savedArtifactsDirectory,
            timingSummary,
            analysisResponse,
            setUpgradeResponse);
    }

    internal static ChatGptDeckAnalysisResponse ParseAnalysisResponse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("Paste the deck_profile JSON returned from ChatGPT into Step 3.");
        }

        var json = ChatGptJsonTextFormatterService.ExtractJsonPayload(input);
        using var document = JsonDocument.Parse(json);

        JsonElement payload = document.RootElement;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("deck_profile", out var profileElement))
        {
            payload = profileElement;
        }

        if (payload.ValueKind != JsonValueKind.Object || !LooksLikeDeckProfile(payload))
        {
            throw new InvalidOperationException("The submitted ChatGPT response did not contain a valid deck_profile payload.");
        }

        var result = JsonSerializer.Deserialize<ChatGptDeckAnalysisResponse>(payload.GetRawText(), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (result is null || !HasMeaningfulDeckProfileContent(result))
        {
            throw new InvalidOperationException("The submitted ChatGPT response did not contain a valid deck_profile payload.");
        }

        return result;
    }

    private static bool LooksLikeDeckProfile(JsonElement payload)
    {
        string[] knownProperties =
        [
            "format",
            "commander",
            "game_plan",
            "primary_axes",
            "speed",
            "strengths",
            "weaknesses",
            "deck_needs",
            "weak_slots",
            "synergy_tags",
            "question_answers",
            "deck_versions"
        ];

        return knownProperties.Any(propertyName => payload.TryGetProperty(propertyName, out _));
    }

    private static bool HasMeaningfulDeckProfileContent(ChatGptDeckAnalysisResponse response)
        => !string.IsNullOrWhiteSpace(response.Format)
            || !string.IsNullOrWhiteSpace(response.Commander)
            || !string.IsNullOrWhiteSpace(response.GamePlan)
            || !string.IsNullOrWhiteSpace(response.Speed)
            || response.PrimaryAxes.Count > 0
            || response.Strengths.Count > 0
            || response.Weaknesses.Count > 0
            || response.DeckNeeds.Count > 0
            || response.WeakSlots.Count > 0
            || response.SynergyTags.Count > 0
            || response.QuestionAnswers.Count > 0
            || response.DeckVersions.Count > 0;

    private void ImportSavedArtifacts(ChatGptDeckRequest request)
    {
        var path = request.ImportArtifactsPath.Trim();
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_chatGptArtifactsPath, path));

        var artifactsRoot = Path.GetFullPath(_chatGptArtifactsPath);
        var isUnderArtifactsRoot = fullPath.StartsWith(
            artifactsRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, artifactsRoot, StringComparison.OrdinalIgnoreCase);
        if (!isUnderArtifactsRoot)
        {
            throw new InvalidOperationException(
                $"Import folder must be under the ChatGPT Analysis artifacts directory ({artifactsRoot}).");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Import folder not found: {fullPath}");
        }

        var deckProfilePath = Path.Combine(fullPath, "40-deck-profile.json");
        var setUpgradeResponsePath = Path.Combine(fullPath, "51-set-upgrade-response.json");

        var loadedDeckProfile = false;
        if (File.Exists(deckProfilePath))
        {
            var deckProfileContent = File.ReadAllText(deckProfilePath);
            if (!string.IsNullOrWhiteSpace(deckProfileContent))
            {
                request.DeckProfileJson = deckProfileContent;
                loadedDeckProfile = true;
            }
        }

        var loadedSetUpgrade = false;
        if (File.Exists(setUpgradeResponsePath))
        {
            var setUpgradeContent = File.ReadAllText(setUpgradeResponsePath);
            if (!string.IsNullOrWhiteSpace(setUpgradeContent))
            {
                request.SetUpgradeResponseJson = setUpgradeContent;
                loadedSetUpgrade = true;
            }
        }

        if (!loadedDeckProfile && !loadedSetUpgrade)
        {
            throw new InvalidOperationException(
                $"Import folder did not contain 40-deck-profile.json or 51-set-upgrade-response.json: {fullPath}");
        }

        // Advance the workflow step so the matching standalone short-circuit fires.
        request.DeckSource = string.Empty;
        request.WorkflowStep = loadedSetUpgrade ? 5 : 3;
    }

    internal static ChatGptSetUpgradeResponse ParseSetUpgradeResponse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("Paste the set_upgrade_report JSON returned from ChatGPT into Step 5.");
        }

        var json = ChatGptJsonTextFormatterService.ExtractJsonPayload(input);
        using var document = JsonDocument.Parse(json);

        JsonElement payload = document.RootElement;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("set_upgrade_report", out var reportElement))
        {
            payload = reportElement;
        }

        if (payload.ValueKind != JsonValueKind.Object || !LooksLikeSetUpgradeReport(payload))
        {
            throw new InvalidOperationException("The submitted ChatGPT response did not contain a valid set_upgrade_report payload.");
        }

        var result = JsonSerializer.Deserialize<ChatGptSetUpgradeResponse>(payload.GetRawText(), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (result is null || !HasMeaningfulSetUpgradeContent(result))
        {
            throw new InvalidOperationException("The submitted ChatGPT response did not contain a valid set_upgrade_report payload.");
        }

        return result;
    }

    private static bool LooksLikeSetUpgradeReport(JsonElement payload)
    {
        string[] knownProperties = ["sets", "final_shortlist"];
        return knownProperties.Any(propertyName => payload.TryGetProperty(propertyName, out _));
    }

    private static bool HasMeaningfulSetUpgradeContent(ChatGptSetUpgradeResponse response)
        => response.Sets.Count > 0
            || (response.FinalShortlist is not null
                && (response.FinalShortlist.MustTest.Count > 0
                    || response.FinalShortlist.Optional.Count > 0
                    || response.FinalShortlist.Skip.Count > 0));

    /// <summary>
    /// Loads deck entries from a public URL or pasted export text.
    /// </summary>
    /// <param name="deckSource">Deck URL or pasted export text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<List<DeckEntry>> LoadDeckEntriesAsync(string deckSource, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(deckSource.Trim(), UriKind.Absolute, out var uri))
        {
            if (uri.Host.Contains("moxfield.com", StringComparison.OrdinalIgnoreCase))
            {
                return await _moxfieldDeckImporter.ImportAsync(deckSource, cancellationToken).ConfigureAwait(false);
            }

            if (uri.Host.Contains("archidekt.com", StringComparison.OrdinalIgnoreCase))
            {
                return await _archidektDeckImporter.ImportAsync(deckSource, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            return _moxfieldParser.ParseText(deckSource);
        }
        catch (DeckParseException)
        {
        }

        try
        {
            return _archidektParser.ParseText(deckSource);
        }
        catch (DeckParseException)
        {
        }

        throw new InvalidOperationException("The submitted deck was not recognized as a Moxfield URL, Archidekt URL, Moxfield export, or Archidekt export.");
    }

    /// <summary>
    /// Builds the short deck summary shown above the generated ChatGPT packets.
    /// </summary>
    private static string BuildInputSummary(ChatGptDeckRequest request, IReadOnlyList<DeckEntry> entries, IReadOnlyList<DeckEntry> possibleIncludeEntries, string? commanderName)
    {
        var mainDeckCards = entries
            .Where(entry => string.Equals(entry.Board, "mainboard", StringComparison.OrdinalIgnoreCase))
            .Sum(entry => entry.Quantity);
        var commanderCards = entries
            .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .Sum(entry => entry.Quantity);
        var sideboardCards = possibleIncludeEntries
            .Where(entry => string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .Sum(entry => entry.Quantity);
        var maybeboardCards = possibleIncludeEntries
            .Where(entry => string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase))
            .Sum(entry => entry.Quantity);
        var builder = new StringBuilder();
        builder.AppendLine($"Format: {NormalizeSingleLine(request.Format, "Commander")}");
        if (!string.IsNullOrWhiteSpace(request.DeckName))
        {
            builder.AppendLine($"Deck name: {request.DeckName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(commanderName))
        {
            builder.AppendLine($"Commander: {commanderName}");
        }

        builder.AppendLine($"Main deck cards: {mainDeckCards}");
        if (!string.IsNullOrWhiteSpace(commanderName) || commanderCards > 0)
        {
            builder.AppendLine($"Commander cards: {commanderCards}");
        }

        if (possibleIncludeEntries.Count > 0)
        {
            builder.AppendLine($"Possible includes: {possibleIncludeEntries.Sum(entry => entry.Quantity)}");
            if (sideboardCards > 0)
            {
                builder.AppendLine($"Sideboard cards: {sideboardCards}");
            }

            if (maybeboardCards > 0)
            {
                builder.AppendLine($"Maybeboard cards: {maybeboardCards}");
            }
        }

        var bracket = CommanderBracketCatalog.Find(request.TargetCommanderBracket);
        if (bracket is not null)
        {
            builder.AppendLine($"Target commander bracket: {bracket.Label}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatDecklistLine(DeckEntry entry, bool includeVersions, IReadOnlyDictionary<string, string>? oracleNameMap = null)
    {
        var name = entry.Name;
        string? printedAs = null;
        if (oracleNameMap is not null && oracleNameMap.TryGetValue(entry.Name, out var oracleName)
            && !string.Equals(oracleName, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            printedAs = entry.Name;
            name = oracleName;
        }
        if (includeVersions)
        {
            var slash = name.IndexOf(" // ", StringComparison.Ordinal);
            if (slash >= 0) name = name[..slash].TrimEnd();
        }
        var line = $"{entry.Quantity} {name}";
        if (includeVersions && !string.IsNullOrWhiteSpace(entry.SetCode))
        {
            line += $" ({entry.SetCode.ToUpperInvariant()})";
            if (!string.IsNullOrWhiteSpace(entry.CollectorNumber))
                line += $" {entry.CollectorNumber}";
        }
        if (printedAs is not null)
        {
            line += $" [printed as: {printedAs}]";
        }
        return line;
    }

    /// <summary>
    /// Builds the analysis deck text, keeping possible includes separate from the playable list.
    /// When <paramref name="includeVersions"/> is <see langword="true"/>, each commander and mainboard
    /// line includes the set code and collector number when available.
    /// </summary>
    private static string BuildDecklistText(IReadOnlyList<DeckEntry> entries, IReadOnlyList<DeckEntry> possibleIncludeEntries, bool includeVersions = false, IReadOnlyDictionary<string, string>? oracleNameMap = null)
    {
        var commanderLines = entries
            .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => FormatDecklistLine(entry, includeVersions, oracleNameMap))
            .ToList();
        var mainboardLines = entries
            .Where(entry => !string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => FormatDecklistLine(entry, includeVersions, oracleNameMap))
            .ToList();

        var builder = new StringBuilder();
        if (commanderLines.Count > 0)
        {
            builder.AppendLine("Commander");
            foreach (var line in commanderLines)
            {
                builder.AppendLine(line);
            }

            builder.AppendLine();
        }

        builder.AppendLine("Mainboard");
        foreach (var line in mainboardLines)
        {
            builder.AppendLine(line);
        }

        if (possibleIncludeEntries.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Possible Includes");
            foreach (var line in possibleIncludeEntries
                         .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(entry =>
                         {
                             if (oracleNameMap is not null && oracleNameMap.TryGetValue(entry.Name, out var oracleName)
                                 && !string.Equals(oracleName, entry.Name, StringComparison.OrdinalIgnoreCase))
                             {
                                 return $"{entry.Quantity} {oracleName} [printed as: {entry.Name}]";
                             }
                             return $"{entry.Quantity} {entry.Name}";
                         }))
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats the Commander Spellbook combo lookup result as a reference block for injection into the analysis prompt.
    /// Returns an empty string when no combo data is available.
    /// </summary>
    private static string BuildComboReferenceText(CommanderSpellbookResult? result)
    {
        if (result is null
            || (result.IncludedCombos.Count == 0 && result.AlmostIncludedCombos.Count == 0))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Commander Spellbook combo reference (verified data — use this when answering combo questions):");

        if (result.IncludedCombos.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"COMPLETE COMBOS IN THIS DECK ({result.IncludedCombos.Count}):");
            for (var i = 0; i < result.IncludedCombos.Count; i++)
            {
                var combo = result.IncludedCombos[i];
                builder.AppendLine($"{i + 1}. Cards: {string.Join(" + ", combo.CardNames)}");
                builder.AppendLine($"   Result: {string.Join(", ", combo.Results)}");
                if (!string.IsNullOrWhiteSpace(combo.Instructions))
                {
                    builder.AppendLine($"   How: {combo.Instructions}");
                }
            }
        }

        if (result.AlmostIncludedCombos.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"COMBOS ONE CARD AWAY (within color identity) ({result.AlmostIncludedCombos.Count}):");
            for (var i = 0; i < result.AlmostIncludedCombos.Count; i++)
            {
                var combo = result.AlmostIncludedCombos[i];
                builder.AppendLine($"{i + 1}. Missing: {combo.MissingCard} | Have: {string.Join(" + ", combo.CardsInDeck)}");
                builder.AppendLine($"   Result: {string.Join(", ", combo.Results)}");
                if (!string.IsNullOrWhiteSpace(combo.Instructions))
                {
                    builder.AppendLine($"   How: {combo.Instructions}");
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Suggests a conversation title derived from the commander or deck name.
    /// </summary>
    private static string BuildSuggestedChatTitle(ChatGptDeckRequest request, string? commanderName)
    {
        var primaryName = !string.IsNullOrWhiteSpace(commanderName)
            ? commanderName.Trim()
            : !string.IsNullOrWhiteSpace(request.DeckName)
                ? request.DeckName.Trim()
                : "Commander Deck";

        return $"{primaryName} | AI Deck Analysis";
    }

    private static string BuildAnalysisSummaryFromSavedJson(ChatGptDeckAnalysisResponse analysisResponse)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Format: {NormalizeSingleLine(analysisResponse.Format, "Commander")}");

        if (!string.IsNullOrWhiteSpace(analysisResponse.Commander))
        {
            builder.AppendLine($"Commander: {analysisResponse.Commander.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(analysisResponse.GamePlan))
        {
            builder.AppendLine($"Game plan: {analysisResponse.GamePlan.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(analysisResponse.Speed))
        {
            builder.AppendLine($"Speed: {analysisResponse.Speed.Trim()}");
        }

        if (analysisResponse.PrimaryAxes.Count > 0)
        {
            builder.AppendLine($"Primary axes: {string.Join(", ", analysisResponse.PrimaryAxes)}");
        }

        if (analysisResponse.SynergyTags.Count > 0)
        {
            builder.AppendLine($"Synergy tags: {string.Join(", ", analysisResponse.SynergyTags)}");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the authoritative card, mechanic, and banned-list reference bundle used during analysis.
    /// </summary>
    private static string BuildReferenceText(
        ChatGptDeckRequest request,
        IReadOnlyList<MechanicReference> mechanicReferences,
        IReadOnlyList<CardReference> cardReferences,
        IReadOnlyList<string> bannedCards)
    {
        var builder = new StringBuilder();
        builder.AppendLine("reference_context:");
        builder.AppendLine("source: Scryfall Oracle and official Wizards Comprehensive Rules");
        builder.AppendLine($"generated_at_utc: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        builder.AppendLine($"format: {NormalizeSingleLine(request.Format, "Commander")}");
        builder.AppendLine();
        builder.AppendLine($"official_commander_banned_cards: {FormatBannedCardsLine(bannedCards)}");
        builder.AppendLine();
        builder.AppendLine("mechanics:");
        if (mechanicReferences.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var mechanicReference in mechanicReferences)
            {
                builder.AppendLine($"{mechanicReference.Name}: {mechanicReference.Description}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("cards:");
        builder.AppendLine("[current_deck] = active deck. [candidate_include:sideboard] and [candidate_include:maybeboard] = optional candidates only.");
        if (cardReferences.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var cardReference in cardReferences)
            {
                builder.AppendLine($"[{cardReference.Scope}] {cardReference.Name} | {cardReference.ManaCost} | {cardReference.TypeLine} | {cardReference.OracleText}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<CardReferenceRequest> BuildAnalysisCardReferenceRequests(
        IReadOnlyList<DeckEntry> deckEntries,
        IReadOnlyList<DeckEntry> analysisPossibleIncludeEntries)
    {
        var requests = new List<CardReferenceRequest>();

        requests.AddRange(deckEntries
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new CardReferenceRequest(entry.Name, "current_deck")));

        requests.AddRange(analysisPossibleIncludeEntries
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new CardReferenceRequest(
                entry.Name,
                string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase)
                    ? "candidate_include:sideboard"
                    : "candidate_include:maybeboard")));

        return requests
            .GroupBy(request => request.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>
    /// Builds the main analysis prompt from the deck text, references, bracket guidance, and selected questions.
    /// </summary>
    private static string BuildAnalysisPrompt(ChatGptDeckRequest request, string decklistText, string referenceText, string deckProfileSchemaJson, string? commanderName, IReadOnlyList<string> selectedQuestionIds, IReadOnlyList<string> bannedCards, CommanderSpellbookResult? comboResult = null, bool includeCardVersions = false)
    {
        var bracket = CommanderBracketCatalog.Find(request.TargetCommanderBracket);
        var selectedQuestions = AnalysisQuestionCatalog.ResolveTexts(selectedQuestionIds, request.CardSpecificQuestionCardName, request.BudgetUpgradeAmount);
        var allRequestedQuestions = selectedQuestions.ToList();
        if (!string.IsNullOrWhiteSpace(request.FreeformQuestion))
        {
            allRequestedQuestions.Add(request.FreeformQuestion.Trim());
        }
        var requiresFullDecklists = AnalysisQuestionCatalog.RequiresFullDecklistOutput(selectedQuestionIds);
        var requiresCategoryOutput = AnalysisQuestionCatalog.RequiresCategoryOutput(selectedQuestionIds);
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(commanderName))
        {
            builder.AppendLine($"Title this chat: {commanderName} | Deck Analysis");
            builder.AppendLine();
        }

        builder.AppendLine("You are an expert Magic: The Gathering deck analyst specializing in Commander.");
        builder.AppendLine();
        builder.AppendLine("Analyze this Magic: The Gathering deck. Read all supplied card reference, bracket guidance, and decklist data before beginning.");
        builder.AppendLine();

        // --- Deck context first — grounds the model before instructions ---
        builder.AppendLine("## DECK CONTEXT");
        builder.AppendLine($"format: {NormalizeSingleLine(request.Format, "Commander")}");
        if (!string.IsNullOrWhiteSpace(commanderName))
        {
            builder.AppendLine($"commander: {commanderName}");
        }
        if (bracket is not null)
        {
            builder.AppendLine($"target_bracket: {bracket.Label}");
        }

        if (!string.IsNullOrWhiteSpace(request.DeckName))
        {
            builder.AppendLine($"deck_name: {NormalizeSingleLine(request.DeckName, string.Empty)}");
        }

        if (!string.IsNullOrWhiteSpace(request.StrategyNotes))
        {
            builder.AppendLine($"strategy_notes: {NormalizeSingleLine(request.StrategyNotes, string.Empty)}");
        }

        if (!string.IsNullOrWhiteSpace(request.MetaNotes))
        {
            builder.AppendLine($"meta_notes: {NormalizeSingleLine(request.MetaNotes, string.Empty)}");
        }

        builder.AppendLine();

        // --- Evidence and authority rules ---
        builder.AppendLine("## EVIDENCE RULES");
        builder.AppendLine("- Use the mechanic definitions and card reference supplied below as authoritative. Read all supplied card entries before beginning the analysis.");
        builder.AppendLine("- Do not invent card text or rules.");
        builder.AppendLine("- When a conclusion is based on supplied card text, rules text, or bracket guidance, say so briefly.");
        builder.AppendLine("- When a conclusion is based on inference from deck construction, curve, redundancy, or play patterns, label it as an inference.");
        builder.AppendLine("- If the supplied data is insufficient to support a claim, say that directly instead of overstating confidence.");
        builder.AppendLine("- If you encounter a card name you do not recognize, look it up at https://scryfall.com/search?q=!\"Card Name\" before assuming what it does. Some cards are alternate-art or Universe Beyond printings with unfamiliar names.");
        builder.AppendLine("- Cards labeled candidate_include in the reference are not part of the current deck. Treat them only as candidate additions.");
        builder.AppendLine("- Do not recommend cards from the official Commander banned list (see banned list in the reference section below).");
        builder.AppendLine();

        // --- Bracket guidance ---
        builder.AppendLine("## BRACKET GUIDANCE");
        builder.AppendLine("Commander bracket definitions:");
        foreach (var bracketOption in CommanderBracketCatalog.Options)
        {
            builder.AppendLine($"- {bracketOption.Label}: {bracketOption.Summary} {bracketOption.TurnsExpectation}");
        }
        if (bracket is not null)
        {
            builder.AppendLine($"Target the Commander experience of {bracket.Label}.");
            builder.AppendLine($"Bracket summary: {bracket.Summary}");
            builder.AppendLine($"Turn expectation: {bracket.TurnsExpectation}");
            builder.AppendLine("Use that bracket target when evaluating speed, card quality, interaction density, and suggested improvements.");
        }
        builder.AppendLine();

        // --- Analysis questions ---
        builder.AppendLine("## ANALYSIS QUESTIONS");
        builder.AppendLine("Answer each question below. Use the same numbering in your response.");
        for (var i = 0; i < allRequestedQuestions.Count; i++)
        {
            builder.AppendLine($"{i + 1}. {allRequestedQuestions[i]}");
        }
        builder.AppendLine();

        // --- Output format ---
        builder.AppendLine("## OUTPUT FORMAT");
        builder.AppendLine("Structure your response as follows:");
        builder.AppendLine();
        builder.AppendLine("A. Start with a section titled Requested Question Answers.");
        builder.AppendLine("   - Answer every question using the same numbering from the ANALYSIS QUESTIONS section.");
        builder.AppendLine("   - For each answer, state the conclusion first, then give 6-12 sentences of detailed reasoning that cites specific card names, interactions, and strategic rationale.");
        builder.AppendLine("   - Do not skip, merge, or partially answer any question.");
        builder.AppendLine("   - After writing the readable analysis, copy every answer into deck_profile.question_answers with the same numbering and the same full answer text expanded to JSON form.");
        builder.AppendLine();
        builder.AppendLine("B. After the question answers, include these recommendation sections:");
        builder.AppendLine("   - Top Adds: 5-10 cards with one sentence of reasoning per card, tied to the deck's plan, bracket target, or weaknesses.");
        builder.AppendLine("   - Top Cuts: 5-10 cards with one sentence of reasoning per card.");
        if (requiresFullDecklists)
        {
            builder.AppendLine();
            builder.AppendLine("C. Full decklist output requirements:");
            builder.AppendLine("   For every requested deck-version or upgrade-path question, output the full 100-card Commander decklist.");
            builder.AppendLine("   List every card on its own line — 1 commander and 99 other cards.");
            builder.AppendLine("   After writing each list, count the total lines. If the count is not exactly 100, add or remove cards until it is. Show the count at the end as `// Total: 100`.");
            builder.AppendLine("   When a question asks for 3 upgrade paths, produce 3 separate full decklists — one per path.");
            var exportFormat = request.DecklistExportFormat.Trim();
            if (string.Equals(exportFormat, "moxfield", StringComparison.OrdinalIgnoreCase))
            {
                if (requiresCategoryOutput)
                    builder.AppendLine("   Format for Moxfield bulk edit: one card per line as 'quantity CardName (SET) collectorNumber #Category1 #Category2' (e.g. '1 Sol Ring (CMM) 1 #Ramp #ManaRocks'). Category names must be single words with no spaces — use CamelCase or hyphens. List all 100 cards together with no section headers. The commander line needs no category tags.");
                else
                    builder.AppendLine("   Format for Moxfield bulk edit: one card per line as 'quantity CardName (SET) collectorNumber' (e.g. '1 Sol Ring (CMM) 1'). List all 100 cards together with no section headers.");
            }
            else if (string.Equals(exportFormat, "archidekt", StringComparison.OrdinalIgnoreCase))
            {
                if (requiresCategoryOutput)
                    builder.AppendLine("   Format for Archidekt bulk edit: start with a '// Commander' section header, then '1 CommanderName (SET) collectorNumber [Commander]', then '// Mainboard', then remaining 99 cards as 'quantity CardName (SET) collectorNumber [Category1,Category2]'. Categories are comma-delimited inside square brackets — no spaces around commas, no quotes.");
                else
                    builder.AppendLine("   Format for Archidekt bulk edit: start with a '// Commander' section header, then '1 CommanderName (SET) collectorNumber [Commander]', then '// Mainboard', then remaining 99 cards as 'quantity CardName (SET) collectorNumber'.");
            }
            else
            {
                builder.AppendLine("   Format as plain text: quantity CardName (SET) collectorNumber (one card per line, e.g. '1 Sol Ring (CMM) 1'). Start with the commander line.");
            }
            if (requiresCategoryOutput)
            {
                builder.AppendLine("   Do NOT use basic card types as categories (Creature, Instant, Sorcery, Enchantment, Artifact, Planeswalker, Battle). Use functional role categories (e.g. Ramp, CardDraw, Removal, Wipe, Tutor, WinCondition, Protection).");
                builder.AppendLine("   Return the categorized decklist only inside a fenced ```text code block.");
                var preferredCats = ParseCardNameList(request.PreferredCategories);
                if (preferredCats.Count > 0)
                {
                    builder.AppendLine($"   Preferred category names: {string.Join(", ", preferredCats)}");
                    builder.AppendLine("   Use these names wherever they fit. Create additional categories only when none of the preferred names apply.");
                }
            }
            if (includeCardVersions)
            {
                builder.AppendLine("   For cards retained from the original deck, use the exact set code and collector number from the decklist below.");
                builder.AppendLine("   For newly added cards, omit the set code and collector number — the deck builder will pick the default printing.");
            }
            builder.AppendLine("   Return each full list in its own clearly labeled ```text fenced code block (e.g. ```text Budget Efficiency).");
            builder.AppendLine("   The goal is a list that can be pasted directly into the deck builder's bulk-edit field.");
            builder.AppendLine();
            builder.AppendLine("   After each complete decklist, output:");
            builder.AppendLine("   - Cards Added — a bulleted list of every card in the new deck that was NOT in the original.");
            builder.AppendLine("   - Cards Cut — a bulleted list of every card in the original deck that is NOT in the new deck.");
            builder.AppendLine("   - A deck_profile JSON block for this version, using the same schema as the main deck_profile. Return it in a ```json fenced code block labeled with the version name (e.g. ```json deck_profile — Budget Efficiency).");
        }
        var protectedCards = ParseCardNameList(request.ProtectedCards);
        if (protectedCards.Count > 0 && requiresFullDecklists)
        {
            builder.AppendLine();
            builder.AppendLine($"   Protected cards: {string.Join(", ", protectedCards)}");
            builder.AppendLine("   Keep every protected card in all requested deck versions and upgrade paths.");
            builder.AppendLine("   You may still mention them as potential cuts in the general top-cuts analysis if warranted.");
        }

        builder.AppendLine();
        builder.AppendLine("D. After the full analysis, return a JSON object named deck_profile matching the schema below.");
        builder.AppendLine("   You MUST return the JSON inside a fenced ```json code block (triple-backtick json). Do not return raw JSON outside a code block.");
        builder.AppendLine("   The question_answers array must contain one entry per question, in the same order as the numbered list above.");
        builder.AppendLine("   Do not omit any question. If there are 8 questions, return exactly 8 question_answers entries numbered 1 through 8.");
        builder.AppendLine("   The JSON question_answers entries must mirror the readable Requested Question Answers section one-for-one.");
        builder.AppendLine("   Each answer field must be a thorough response (6-12 sentences minimum) — not a brief summary. Cite specific card names and interactions.");
        builder.AppendLine("   Do not collapse multiple questions into one JSON entry, and do not replace full answers with shorthand summaries in the JSON.");
        builder.AppendLine("   Before returning the JSON, count the numbered questions above and verify that question_answers has the same count.");
        if (requiresFullDecklists)
        {
            builder.AppendLine("   The deck_versions array must contain one entry per requested deck version or upgrade path.");
            builder.AppendLine("   Each entry's decklist field must contain the complete 100-card list (one card per line, same format as the text code blocks above).");
            builder.AppendLine("   Do not abbreviate or truncate any decklist in the JSON — every card must be present.");
        }
        builder.AppendLine();
        builder.AppendLine("   Field-level detail requirements for the deck_profile JSON:");
        builder.AppendLine("   - game_plan: 2-4 sentences describing the deck's primary win condition, game plan, and how it closes games.");
        builder.AppendLine("   - speed: 2-3 sentences characterizing the deck's speed, threat deployment, and typical turn progression.");
        builder.AppendLine("   - strengths: each item should be 1-2 sentences with a specific card or interaction reference.");
        builder.AppendLine("   - weaknesses: each item should be 1-2 sentences with a specific card or interaction reference.");
        builder.AppendLine("   - deck_needs: each item should be 1-2 sentences identifying a gap and what kind of card fills it.");
        builder.AppendLine("   - weak_slots.reason: 2-3 sentences explaining why this slot is weak and what would improve it.");
        builder.AppendLine();
        builder.AppendLine(deckProfileSchemaJson);

        // --- Combo reference (if available) ---
        var comboReferenceText = BuildComboReferenceText(comboResult);
        if (!string.IsNullOrWhiteSpace(comboReferenceText))
        {
            builder.AppendLine();
            builder.AppendLine(comboReferenceText);
        }

        // --- Reference data ---
        builder.AppendLine();
        builder.AppendLine("## REFERENCE DATA");
        builder.AppendLine(referenceText);

        // --- Decklist ---
        builder.AppendLine();
        builder.AppendLine("## DECKLIST");
        builder.AppendLine(decklistText);
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the optional set-upgrade prompt used after the deck profile has been generated.
    /// </summary>
    private static string BuildSetUpgradePrompt(ChatGptDeckRequest request, string decklistText, string deckProfileJson, string? commanderName, string? generatedSetPacket, IReadOnlyList<string> bannedCards)
    {
        var builder = new StringBuilder();
        var upgradeFocus = request.SetUpgradeFocus.Trim();
        var isLateralOnly = string.Equals(upgradeFocus, "lateral-moves", StringComparison.OrdinalIgnoreCase);
        var isStrictOnly = string.Equals(upgradeFocus, "strict-upgrades", StringComparison.OrdinalIgnoreCase);
        var isBoth = string.Equals(upgradeFocus, "both", StringComparison.OrdinalIgnoreCase);
        var bracket = CommanderBracketCatalog.Find(request.TargetCommanderBracket);

        builder.AppendLine("You are an expert Magic: The Gathering deck analyst specializing in Commander set reviews and upgrade evaluation.");
        builder.AppendLine();
        builder.AppendLine("Analyze each selected set for possible additions to this deck, suggested removals for those additions, and any traps.");
        builder.AppendLine("Read all supplied deck profile, decklist, and set packet data before beginning.");
        builder.AppendLine();

        // --- Deck context first ---
        builder.AppendLine("## DECK CONTEXT");
        builder.AppendLine($"format: {NormalizeSingleLine(request.Format, "Commander")}");
        if (!string.IsNullOrWhiteSpace(commanderName))
        {
            builder.AppendLine($"commander: {commanderName}");
        }
        if (bracket is not null)
        {
            builder.AppendLine($"target_bracket: {bracket.Label}");
        }

        builder.AppendLine();

        // --- Bracket guidance ---
        if (bracket is not null)
        {
            builder.AppendLine("## BRACKET GUIDANCE");
            builder.AppendLine($"Target the Commander experience of {bracket.Label}.");
            builder.AppendLine($"Bracket summary: {bracket.Summary}");
            builder.AppendLine($"Turn expectation: {bracket.TurnsExpectation}");
            builder.AppendLine("Evaluate all recommended additions and cuts against this bracket target. Flag any card that would push the deck above or below the target bracket as a trap.");
            builder.AppendLine();
        }

        // --- Evidence rules ---
        builder.AppendLine("## EVIDENCE RULES");
        builder.AppendLine("- Use the deck profile as authoritative for the deck's plan, strengths, weaknesses, and replaceable slots.");
        builder.AppendLine("- Use the set mechanics and card reference as authoritative for set cards.");
        builder.AppendLine("- Do not invent card text or rules.");
        builder.AppendLine("- When a conclusion is based on the deck profile or set card text, say so briefly.");
        builder.AppendLine("- When a conclusion is based on inference from deck construction or play patterns, label it as an inference.");
        builder.AppendLine("- If the supplied data is insufficient to support a claim, say that directly instead of overstating confidence.");
        builder.AppendLine("- If you encounter a card name you do not recognize, look it up at https://scryfall.com/search?q=!\"Card Name\" before assuming what it does. Some cards are alternate-art or Universe Beyond printings with unfamiliar names.");
        builder.AppendLine("- Cards listed under Possible Includes are not part of the current deck. Treat them only as candidate additions.");
        builder.AppendLine($"- Do not recommend cards from the official Commander banned list: {FormatBannedCardsLine(bannedCards)}");

        // --- Upgrade focus ---
        if (isLateralOnly)
        {
            builder.AppendLine();
            builder.AppendLine("## UPGRADE FOCUS: LATERAL MOVES ONLY");
            builder.AppendLine("A lateral move fills the same role as a card already in the deck but offers a different angle, better synergy fit, or a more interesting effect at roughly the same power level.");
            builder.AppendLine("For every lateral move, identify the current deck card it would replace and explain why the swap is worth considering.");
            builder.AppendLine("Do not recommend cards that are simply stronger — flag those as traps if they would create a bracket or power mismatch.");
        }
        else if (isStrictOnly)
        {
            builder.AppendLine();
            builder.AppendLine("## UPGRADE FOCUS: STRICT UPGRADES ONLY");
            builder.AppendLine("A strict upgrade does the same job as a card already in the deck but is meaningfully more powerful, more efficient, or more synergistic with the deck's strategy.");
            builder.AppendLine("For every strict upgrade, name the card it replaces and explain precisely why it is better in this deck's context.");
            builder.AppendLine("Do not recommend lateral moves or speculative includes that are not clearly better than what the deck already runs.");
        }
        else if (isBoth)
        {
            builder.AppendLine();
            builder.AppendLine("## UPGRADE FOCUS: STRICT UPGRADES AND LATERAL MOVES");
            builder.AppendLine("Strict upgrade: meaningfully more powerful or efficient than a card already in the deck. Name the card being replaced and explain why it is better.");
            builder.AppendLine("Lateral move: fills the same role as an existing card but offers a different angle, better synergy fit, or more interesting effect at roughly the same power level. Name the card being replaced and explain why the swap is worth considering.");
            builder.AppendLine("Label each recommendation clearly as 'Strict Upgrade' or 'Lateral Move'.");
        }

        builder.AppendLine();

        // --- Output format ---
        builder.AppendLine("## OUTPUT FORMAT");
        builder.AppendLine("Structure your response as follows:");
        builder.AppendLine();
        builder.AppendLine("A. Per-set analysis — for each selected set, include:");
        builder.AppendLine("   - Top adds from that set (with one sentence of reasoning each, tied to the deck profile)");
        builder.AppendLine("   - Suggested removals for each add (name the card being cut and why it is the weakest slot)");
        builder.AppendLine("   - Traps from that set (cards that look appealing but would hurt the deck's plan, bracket target, or consistency)");
        builder.AppendLine("   - Speculative tests from that set (cards worth trying that lack enough data to confidently recommend — e.g. novel mechanics, unproven synergies, or meta-dependent value)");
        builder.AppendLine();
        builder.AppendLine("B. Final cross-set ranked shortlist with must-test, optional, and skip.");
        builder.AppendLine("   For every recommended add or cut, explain the reasoning briefly and connect it to the deck profile.");
        builder.AppendLine();
        builder.AppendLine("C. Return a complete set_upgrade_report JSON matching the schema at the end of this prompt. You MUST return the JSON inside a fenced ```json code block (triple-backtick json). Do not return raw JSON outside a code block.");
        builder.AppendLine();
        builder.AppendLine("D. Return a second fenced code block tagged as ```text named discussion_summary.txt.");
        builder.AppendLine("   Include the per-set analysis in condensed form, final recommendations, reasoning behind key adds and cuts, and direct answers to the analysis questions — a standalone notes document.");

        // --- Data sections ---
        builder.AppendLine();
        builder.AppendLine("## DECK PROFILE");
        builder.AppendLine(deckProfileJson);
        builder.AppendLine();
        builder.AppendLine("## DECKLIST");
        builder.AppendLine(decklistText);
        builder.AppendLine();
        builder.AppendLine("## SET PACKET");
        var setPacket = !string.IsNullOrWhiteSpace(request.SetPacketText)
            ? request.SetPacketText
            : generatedSetPacket;
        if (string.IsNullOrWhiteSpace(setPacket))
        {
            builder.AppendLine("Paste the condensed set packet here.");
        }
        else
        {
            builder.AppendLine(setPacket.Trim());
        }

        // --- JSON schema at the end (referenced by step C above) ---
        builder.AppendLine();
        builder.AppendLine("## SET UPGRADE REPORT JSON SCHEMA");
        builder.AppendLine("```json");
        builder.AppendLine("{");
        builder.AppendLine("  \"set_upgrade_report\": {");
        builder.AppendLine("    \"sets\": [");
        builder.AppendLine("      {");
        builder.AppendLine("        \"set_code\": \"\",");
        builder.AppendLine("        \"set_name\": \"\",");
        builder.AppendLine("        \"top_adds\": [");
        builder.AppendLine("          {");
        builder.AppendLine("            \"card\": \"\",");
        builder.AppendLine("            \"reason\": \"\",");
        builder.AppendLine("            \"suggested_cut\": \"\",");
        builder.AppendLine("            \"cut_reason\": \"\"");
        builder.AppendLine("          }");
        builder.AppendLine("        ],");
        builder.AppendLine("        \"traps\": [");
        builder.AppendLine("          {");
        builder.AppendLine("            \"card\": \"\",");
        builder.AppendLine("            \"reason\": \"\"");
        builder.AppendLine("          }");
        builder.AppendLine("        ],");
        builder.AppendLine("        \"speculative_tests\": [");
        builder.AppendLine("          {");
        builder.AppendLine("            \"card\": \"\",");
        builder.AppendLine("            \"reason\": \"\"");
        builder.AppendLine("          }");
        builder.AppendLine("        ]");
        builder.AppendLine("      }");
        builder.AppendLine("    ],");
        builder.AppendLine("    \"final_shortlist\": {");
        builder.AppendLine("      \"must_test\": [\"\"],");
        builder.AppendLine("      \"optional\": [\"\"],");
        builder.AppendLine("      \"skip\": [\"\"]");
        builder.AppendLine("    }");
        builder.AppendLine("  }");
        builder.AppendLine("}");
        builder.AppendLine("```");

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds a condensed set packet from Scryfall for the selected set codes.
    /// </summary>
    private async Task<string?> BuildGeneratedSetPacketAsync(ChatGptDeckRequest request, CancellationToken cancellationToken)
    {
        if (request.SelectedSetCodes.Count == 0)
        {
            return string.IsNullOrWhiteSpace(request.SetPacketText) ? null : request.SetPacketText.Trim();
        }

        if (request.SelectedSetCodes.Count > 1 && string.IsNullOrWhiteSpace(request.SetPacketText))
        {
            throw new InvalidOperationException("Choose only one set or paste a condensed set packet override before generating the set-upgrade packet.");
        }

        var commanderColorIdentity = await LookupCommanderColorIdentityAsync(request.DeckSource, cancellationToken).ConfigureAwait(false);
        var generatedPacket = await _scryfallSetService
            .BuildSetPacketAsync([request.SelectedSetCodes[0]], commanderColorIdentity, cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(generatedPacket) ? null : generatedPacket;
    }

    /// <summary>
    /// Looks up the commander's color identity so generated set packets can filter to legal cards.
    /// </summary>
    private async Task<IReadOnlyList<string>> LookupCommanderColorIdentityAsync(string deckSource, CancellationToken cancellationToken)
    {
        var entries = await LoadDeckEntriesAsync(deckSource, cancellationToken).ConfigureAwait(false);
        var commanderName = entries
            .FirstOrDefault(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            ?.Name;
        if (string.IsNullOrWhiteSpace(commanderName))
        {
            return Array.Empty<string>();
        }

        var request = new RestRequest("cards/collection", Method.Post)
            .AddJsonBody(new
            {
                identifiers = new[]
                {
                    new { name = commanderName.Trim() }
                }
            });
        var response = await _executeCollectionAsync(request, cancellationToken).ConfigureAwait(false);
        var card = response.Data?.Data?.FirstOrDefault();
        if (card?.ColorIdentity is null)
        {
            return Array.Empty<string>();
        }

        return card.ColorIdentity
            .Where(color => !string.IsNullOrWhiteSpace(color))
            .Select(color => color.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the deck-profile schema that ChatGPT should follow during analysis.
    /// </summary>
    private static string BuildDeckProfileSchemaJson(string? commanderName, string format, bool includeFullDecklists = false)
    {
        var payload = new Dictionary<string, object>
        {
            ["format"] = NormalizeSingleLine(format, "Commander"),
            ["commander"] = commanderName ?? string.Empty,
            ["game_plan"] = string.Empty,
            ["primary_axes"] = Array.Empty<string>(),
            ["speed"] = string.Empty,
            ["strengths"] = Array.Empty<string>(),
            ["weaknesses"] = Array.Empty<string>(),
            ["deck_needs"] = Array.Empty<string>(),
            ["weak_slots"] = new[]
            {
                new
                {
                    card = string.Empty,
                    reason = string.Empty
                }
            },
            ["synergy_tags"] = Array.Empty<string>(),
            ["question_answers"] = new[]
            {
                new
                {
                    question_number = 1,
                    question = string.Empty,
                    answer = string.Empty,
                    basis = "authoritative|inference|mixed"
                }
            }
        };

        if (includeFullDecklists)
        {
            payload["deck_versions"] = new[]
            {
                new
                {
                    version_name = string.Empty,
                    decklist = "complete 100-card decklist, one card per line, same format as the text code blocks",
                    cards_added = Array.Empty<string>(),
                    cards_cut = Array.Empty<string>()
                }
            };
        }

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private async Task<CardReferenceBundle> LookupCardReferencesAsync(IReadOnlyList<CardReferenceRequest> cardRequests, CancellationToken cancellationToken)
    {
        if (cardRequests.Count == 0)
        {
            return new CardReferenceBundle(Array.Empty<CardReference>(), Array.Empty<string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var resolvedCards = new Dictionary<string, CardReference>(StringComparer.OrdinalIgnoreCase);
        var oracleNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mechanicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in Chunk(cardRequests, ScryfallBatchSize))
        {
            var request = new RestRequest("cards/collection", Method.Post);
            request.AddJsonBody(new
            {
                identifiers = chunk.Select(card => new { name = card.Name }).ToArray()
            });

            var response = await _executeCollectionAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
            {
                throw new HttpRequestException(
                    $"Scryfall card reference lookup (cards/collection) returned HTTP {(int)response.StatusCode} while building the analysis packet.",
                    null,
                    response.StatusCode);
            }

            foreach (var card in response.Data.Data)
            {
                var matchingRequest = chunk.FirstOrDefault(entry => string.Equals(entry.Name, card.Name, StringComparison.OrdinalIgnoreCase));
                if (matchingRequest is null)
                {
                    continue;
                }

                oracleNameMap[matchingRequest.Name] = card.Name;
                resolvedCards[matchingRequest.Name] = new CardReference(
                    matchingRequest.Scope,
                    card.Name,
                    card.ManaCost ?? string.Empty,
                    card.TypeLine,
                    NormalizeOracleText(card));

                foreach (var mechanicName in ExtractMechanicNames(card))
                {
                    mechanicNames.Add(mechanicName);
                }
            }

            foreach (var unresolvedRequest in chunk.Where(card => !resolvedCards.ContainsKey(card.Name)))
            {
                var fallbackCard = await SearchFallbackCardAsync(unresolvedRequest.Name, cancellationToken).ConfigureAwait(false);
                if (fallbackCard is null)
                {
                    continue;
                }

                oracleNameMap[unresolvedRequest.Name] = fallbackCard.Name;
                var displayName = NormalizeLookupName(unresolvedRequest.Name) == NormalizeLookupName(fallbackCard.Name)
                    ? fallbackCard.Name
                    : $"submitted_name: {unresolvedRequest.Name} | resolved_card: {fallbackCard.Name}";

                resolvedCards[unresolvedRequest.Name] = new CardReference(
                    unresolvedRequest.Scope,
                    displayName,
                    fallbackCard.ManaCost ?? string.Empty,
                    fallbackCard.TypeLine,
                    NormalizeOracleText(fallbackCard));

                foreach (var mechanicName in ExtractMechanicNames(fallbackCard))
                {
                    mechanicNames.Add(mechanicName);
                }
            }
        }

        var cardReferences = cardRequests
            .Where(card => resolvedCards.ContainsKey(card.Name))
            .Select(card => resolvedCards[card.Name])
            .ToList();

        return new CardReferenceBundle(
            cardReferences,
            mechanicNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
            oracleNameMap);
    }

    private async Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cardName))
        {
            return null;
        }

        var normalizedCardName = NormalizeLookupName(cardName);
        foreach (var query in new[]
        {
            $"(printed:\"{cardName}\" OR name:\"{cardName}\")",
            cardName
        })
        {
            var request = new RestRequest("cards/search", Method.Get);
            request.AddQueryParameter("q", query);
            request.AddQueryParameter("unique", "prints");
            request.AddQueryParameter("include_multilingual", "true");

            var response = await _executeSearchAsync(request, cancellationToken).ConfigureAwait(false);
            ThrowIfUpstreamUnavailable(response.StatusCode);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
            {
                continue;
            }

            var match = response.Data.Data
                .FirstOrDefault(card => NormalizeLookupName(card.Name) == normalizedCardName)
                ?? response.Data.Data.FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        var namedRequest = new RestRequest("cards/named", Method.Get);
        namedRequest.AddQueryParameter("fuzzy", cardName);
        var namedResponse = await _executeNamedAsync(namedRequest, cancellationToken).ConfigureAwait(false);
        ThrowIfUpstreamUnavailable(namedResponse.StatusCode);
        if ((int)namedResponse.StatusCode >= 200 && (int)namedResponse.StatusCode < 300 && namedResponse.Data is not null)
        {
            return namedResponse.Data;
        }

        return null;
    }

    private static void ThrowIfUpstreamUnavailable(System.Net.HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        if (code == 429 || code >= 500)
        {
            throw new HttpRequestException(
                $"Scryfall returned HTTP {code}.",
                inner: null,
                statusCode: statusCode);
        }
    }

    private static string NormalizeLookupName(string cardName)
        => cardName
            .Trim()
            .Replace('\u2019', '\'')
            .Replace('\u2018', '\'')
            .Replace('\u02BC', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .ToLowerInvariant();

    private async Task<string> ValidateCommanderAsync(IReadOnlyList<DeckEntry> entries, string? commanderName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commanderName))
        {
            throw new InvalidOperationException("The commander isn't in the deck text. Add a legal commander line before generating the analysis packet.");
        }

        var commanderEntry = entries.FirstOrDefault(entry => string.Equals(entry.Name, commanderName, StringComparison.OrdinalIgnoreCase));
        if (commanderEntry is null)
        {
            throw new InvalidOperationException("The commander isn't in the deck text. Add a legal commander line before generating the analysis packet.");
        }

        var commanderCard = await SearchFallbackCardAsync(commanderName, cancellationToken).ConfigureAwait(false);
        if (commanderCard is null || !IsCommanderEligible(commanderCard))
        {
            throw new InvalidOperationException($"The commander isn't in the deck text. \"{commanderName}\" is not a legal commander by this workflow's rules.");
        }

        return commanderEntry.Name;
    }

    private static bool IsCommanderEligible(ScryfallCard card)
    {
        var typeLine = card.TypeLine ?? string.Empty;
        var oracleText = NormalizeOracleText(card);
        if (IsLegendaryType(typeLine, "Creature"))
        {
            return true;
        }

        if (IsLegendaryType(typeLine, "Vehicle"))
        {
            return true;
        }

        return typeLine.Contains("Planeswalker", StringComparison.OrdinalIgnoreCase)
            && oracleText.Contains("can be your commander", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegendaryType(string typeLine, string requiredType)
        => typeLine.Contains("Legendary", StringComparison.OrdinalIgnoreCase)
            && typeLine.Contains(requiredType, StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<MechanicReference>> LookupMechanicReferencesAsync(IReadOnlyList<string> mechanicNames, CancellationToken cancellationToken)
    {
        var tasks = mechanicNames
            .Select(async mechanicName =>
            {
                var result = await _mechanicLookupService.LookupAsync(mechanicName, cancellationToken).ConfigureAwait(false);
                var description = result.SummaryText ?? result.RulesText ?? "No official rules text found.";
                return new MechanicReference(
                    mechanicName,
                    CollapseWhitespace(description),
                    result.RuleReference);
            })
            .ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string NormalizeOracleText(ScryfallCard card)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(card.OracleText))
        {
            parts.Add(CollapseWhitespace(card.OracleText));
        }

        foreach (var face in card.CardFaces ?? Array.Empty<ScryfallCardFace>())
        {
            var faceParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(face.Name))
            {
                faceParts.Add(face.Name.Trim());
            }

            if (!string.IsNullOrWhiteSpace(face.ManaCost))
            {
                faceParts.Add(face.ManaCost.Trim());
            }

            if (!string.IsNullOrWhiteSpace(face.TypeLine))
            {
                faceParts.Add(CollapseWhitespace(face.TypeLine));
            }

            if (!string.IsNullOrWhiteSpace(face.OracleText))
            {
                faceParts.Add(CollapseWhitespace(face.OracleText));
            }

            if (!string.IsNullOrWhiteSpace(face.Power) && !string.IsNullOrWhiteSpace(face.Toughness))
            {
                faceParts.Add($"{face.Power}/{face.Toughness}");
            }

            if (faceParts.Count > 0)
            {
                parts.Add(string.Join(" | ", faceParts));
            }
        }

        if (!string.IsNullOrWhiteSpace(card.Power) && !string.IsNullOrWhiteSpace(card.Toughness))
        {
            parts.Add($"{card.Power}/{card.Toughness}");
        }

        return string.Join(" ", parts);
    }

    private static string CollapseWhitespace(string value)
    {
        return string.Join(" ", (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> values, int size)
    {
        for (var index = 0; index < values.Count; index += size)
        {
            var count = Math.Min(size, values.Count - index);
            var chunk = new List<T>(count);
            for (var itemIndex = 0; itemIndex < count; itemIndex++)
            {
                chunk.Add(values[index + itemIndex]);
            }

            yield return chunk;
        }
    }

    private static string NormalizeSingleLine(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : CollapseWhitespace(value);

    private static string ExtractJsonObject(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                trimmed = trimmed[..closingFence];
            }
        }

        return trimmed.Trim();
    }

    private async Task<string> SaveArtifactsAsync(
        ChatGptDeckRequest request,
        string? commanderName,
        string inputSummary,
        string? referenceText,
        string? analysisPromptText,
        string deckProfileSchemaJson,
        string? setUpgradePromptText,
        CancellationToken cancellationToken)
    {
        var commanderSegment = CreateSafePathSegment(commanderName, "unknown-commander");
        var timestampSegment = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outputDirectory = Path.Combine(_chatGptArtifactsPath, commanderSegment, timestampSegment);
        Directory.CreateDirectory(outputDirectory);

        var promptSections = new List<(string FileName, string Label, string? Content)>
        {
            ("01-request-context.txt", "REQUEST CONTEXT", BuildRequestContextText(request, commanderName)),
            ("00-input-summary.txt", "INPUT SUMMARY", inputSummary),
            ("30-reference.txt", "REFERENCE TEXT", referenceText),
            ("31-analysis-prompt.txt", "ANALYSIS PROMPT", analysisPromptText),
            ("41-deck-profile-schema.json", "DECK PROFILE JSON SCHEMA", deckProfileSchemaJson),
            ("50-set-upgrade-prompt.txt", "SET UPGRADE PROMPT", setUpgradePromptText)
        };

        var responseSections = new List<(string FileName, string Label, string? Content)>
        {
            ("40-deck-profile.json", "DECK PROFILE JSON", request.DeckProfileJson),
            ("51-set-upgrade-response.json", "SET UPGRADE RESPONSE JSON", request.SetUpgradeResponseJson)
        };

        foreach (var section in promptSections.Where(section => !string.IsNullOrWhiteSpace(section.Content)))
        {
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, section.FileName),
                section.Content!.Trim() + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var section in responseSections.Where(section => !string.IsNullOrWhiteSpace(section.Content)))
        {
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, section.FileName),
                ExtractJsonObject(section.Content!).Trim() + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "all-prompts.txt"),
            BuildCombinedArtifactText(promptSections),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "all-responses.txt"),
            BuildCombinedArtifactText(responseSections),
            cancellationToken).ConfigureAwait(false);

        return outputDirectory;
    }

    private static string BuildCombinedArtifactText(IEnumerable<(string FileName, string Label, string? Content)> sections)
    {
        var builder = new StringBuilder();
        foreach (var section in sections.Where(section => !string.IsNullOrWhiteSpace(section.Content)))
        {
            builder.AppendLine($"===== {section.Label} ({section.FileName}) =====");
            builder.AppendLine(section.Content!.Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string CreateSafePathSegment(string? value, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(candidate.Select(character => invalidChars.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized.Replace(' ', '-').ToLowerInvariant();
    }

    private static string BuildRequestContextText(ChatGptDeckRequest request, string? commanderName)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"workflow_step: {request.WorkflowStep}");
        builder.AppendLine($"save_artifacts_to_disk: {request.SaveArtifactsToDisk}");
        builder.AppendLine($"format: {NormalizeSingleLine(request.Format, "Commander")}");
        builder.AppendLine($"deck_name: {NormalizeSingleLine(request.DeckName, string.Empty)}");
        builder.AppendLine($"commander: {NormalizeSingleLine(commanderName, string.Empty)}");
        builder.AppendLine($"target_commander_bracket: {NormalizeSingleLine(request.TargetCommanderBracket, string.Empty)}");
        builder.AppendLine($"include_sideboard_in_analysis: {request.IncludeSideboardInAnalysis}");
        builder.AppendLine($"include_maybeboard_in_analysis: {request.IncludeMaybeboardInAnalysis}");
        builder.AppendLine($"card_specific_question_card_name: {NormalizeSingleLine(request.CardSpecificQuestionCardName, string.Empty)}");
        builder.AppendLine($"budget_upgrade_amount: {NormalizeSingleLine(request.BudgetUpgradeAmount, string.Empty)}");
        builder.AppendLine("selected_analysis_questions:");
        foreach (var questionId in AnalysisQuestionCatalog.NormalizeSelections(request.SelectedAnalysisQuestions))
        {
            builder.AppendLine($"- {questionId}");
        }

        builder.AppendLine("selected_set_codes:");
        foreach (var setCode in request.SelectedSetCodes.Where(setCode => !string.IsNullOrWhiteSpace(setCode)))
        {
            builder.AppendLine($"- {setCode.Trim()}");
        }

        AppendOptionalContextBlock(builder, "strategy_notes", request.StrategyNotes);
        AppendOptionalContextBlock(builder, "meta_notes", request.MetaNotes);
        AppendOptionalContextBlock(builder, "deck_source", request.DeckSource);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendOptionalContextBlock(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"{label}:");
        builder.AppendLine(value.Trim());
    }

    private static string FormatBannedCardsLine(IReadOnlyList<string> bannedCards)
        => bannedCards.Count == 0 ? "(unavailable)" : string.Join(", ", bannedCards);

    /// <summary>
    /// Parses a newline- or comma-separated list of card names into a deduplicated, trimmed list.
    /// </summary>
    private static IReadOnlyList<string> ParseCardNameList(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        return input
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ExtractMechanicNames(ScryfallCard card)
    {
        foreach (var keyword in card.Keywords ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                yield return keyword.Trim();
            }
        }

        foreach (var oracleText in EnumerateOracleText(card))
        {
            foreach (var line in oracleText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine))
                {
                    continue;
                }

                var abilityWordMatch = AbilityWordRegex.Match(trimmedLine);
                if (abilityWordMatch.Success)
                {
                    yield return abilityWordMatch.Groups["term"].Value.Trim();
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateOracleText(ScryfallCard card)
    {
        if (!string.IsNullOrWhiteSpace(card.OracleText))
        {
            yield return card.OracleText;
        }

        foreach (var face in card.CardFaces ?? Array.Empty<ScryfallCardFace>())
        {
            if (!string.IsNullOrWhiteSpace(face.OracleText))
            {
                yield return face.OracleText;
            }
        }
    }

    private static string BuildTimingSummary(List<(string Label, long Ms, string? Detail)> timings, long totalMs)
    {
        var sb = new StringBuilder();
        foreach (var (label, ms, detail) in timings)
        {
            sb.Append($"{label}: {ms:N0}ms");
            if (!string.IsNullOrWhiteSpace(detail))
            {
                sb.Append($" ({detail})");
            }

            sb.AppendLine();
        }

        sb.Append($"Total: {totalMs:N0}ms");
        return sb.ToString();
    }

    [GeneratedRegex(@"^(?<term>[A-Za-z][A-Za-z' -]{1,40})\s+—\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AbilityWordPattern();


    private sealed record CardReferenceRequest(string Name, string Scope);
    private sealed record CardReference(string Scope, string Name, string ManaCost, string TypeLine, string OracleText);

    private sealed record CardReferenceBundle(IReadOnlyList<CardReference> CardReferences, IReadOnlyList<string> MechanicNames, IReadOnlyDictionary<string, string> OracleNameMap);

    private sealed record MechanicReference(string Name, string Description, string? RuleReference);
}
