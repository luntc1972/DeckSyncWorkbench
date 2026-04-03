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
    string ProbePromptText,
    string ProbeResponseSchemaJson,
    string DeckProfileSchemaJson,
    string? ReferenceText,
    string? AnalysisPromptText,
    string? SetUpgradePromptText,
    string? SavedArtifactsDirectory,
    string? TimingSummary);

/// <summary>
/// Builds probe, analysis, and set-upgrade prompts plus supporting reference data for ChatGPT.
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
        _chatGptArtifactsPath = string.IsNullOrWhiteSpace(chatGptArtifactsPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MTG Deck Studio",
                "ChatGPT Packets")
            : Path.GetFullPath(chatGptArtifactsPath);
        var client = scryfallRestClient ?? ScryfallRestClientFactory.Create();
        _executeCollectionAsync = executeCollectionAsync ?? ((request, cancellationToken) => client.ExecuteAsync<ScryfallCollectionResponse>(request, cancellationToken));
        _executeSearchAsync = executeSearchAsync ?? ((request, cancellationToken) => client.ExecuteAsync<ScryfallSearchResponse>(request, cancellationToken));
        _executeNamedAsync = executeNamedAsync ?? ((request, cancellationToken) => client.ExecuteAsync<ScryfallCard>(request, cancellationToken));
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
            var validatedCommanderName = await ValidateCommanderAsync(entries, commanderName, cancellationToken).ConfigureAwait(false);
            commanderName = validatedCommanderName;
            entries = entries
                .Select(entry => string.Equals(entry.Name, validatedCommanderName, StringComparison.OrdinalIgnoreCase)
                    ? entry with { Board = "commander" }
                    : string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase)
                        ? entry with { Board = "main" }
                        : entry)
                .ToList();
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
        var probeDecklistText = BuildProbeDecklistText(entries);
        var decklistText = BuildDecklistText(deckEntries, possibleIncludeEntries);
        var probePromptText = BuildProbePrompt(request, probeDecklistText, commanderName);
        var probeSchemaJson = BuildProbeResponseSchemaJson();
        var deckProfileSchemaJson = BuildDeckProfileSchemaJson(commanderName, request.Format);

        string? referenceText = null;
        string? analysisPromptText = null;
        string? setUpgradePromptText = null;
        string? savedArtifactsDirectory = null;

        // Fire banned-list and set-packet fetches in parallel — neither depends on the other.
        var parallelStopwatch = Stopwatch.StartNew();
        var bannedCardsTask = _commanderBanListService.GetBannedCardsAsync(cancellationToken);
        var setPacketTask = BuildGeneratedSetPacketAsync(request, cancellationToken);
        await Task.WhenAll(bannedCardsTask, setPacketTask).ConfigureAwait(false);
        timings.Add(("Ban list + set packet", parallelStopwatch.ElapsedMilliseconds, null));
        _logger.LogInformation("ChatGPT packet banned-list + set-packet fetch completed in {ElapsedMs}ms.", parallelStopwatch.ElapsedMilliseconds);
        var bannedCards = bannedCardsTask.Result;
        var generatedSetPacket = setPacketTask.Result;

        var probeResponse = ParseProbeResponse(request.ProbeResponseJson);
        var deckProfileText = string.IsNullOrWhiteSpace(request.DeckProfileJson)
            ? deckProfileSchemaJson
            : ExtractJsonObject(request.DeckProfileJson);
        var selectedQuestions = AnalysisQuestionCatalog.NormalizeSelections(request.SelectedAnalysisQuestions);

        if (probeResponse is not null && CommanderBracketCatalog.Find(request.TargetCommanderBracket) is null)
        {
            throw new InvalidOperationException("Choose a target Commander bracket before generating the analysis packet.");
        }

        if (probeResponse is not null && selectedQuestions.Count == 0 && string.IsNullOrWhiteSpace(request.FreeformQuestion))
        {
            throw new InvalidOperationException("Select at least one analysis question before generating the analysis packet.");
        }

        if (probeResponse is not null
            && selectedQuestions.Any(questionId => questionId is "card-worth-it" or "better-alternatives")
            && string.IsNullOrWhiteSpace(request.CardSpecificQuestionCardName))
        {
            throw new InvalidOperationException("Enter a card name for the selected card-specific analysis questions.");
        }

        if (probeResponse is not null
            && AnalysisQuestionCatalog.RequiresCategoryOutput(selectedQuestions)
            && string.IsNullOrWhiteSpace(request.DecklistExportFormat))
        {
            throw new InvalidOperationException("Choose Moxfield or Archidekt as the export format when assigning or updating categories — plain text does not support inline category formatting.");
        }

        if (probeResponse is not null
            && selectedQuestions.Contains("budget-upgrades", StringComparer.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.BudgetUpgradeAmount))
        {
            throw new InvalidOperationException("Enter a budget amount for the selected budget upgrade question.");
        }

        if (probeResponse is not null)
        {
            var unknownCardNames = MergeUnknownCards(probeResponse.UnknownCards, commanderName);

            // Start combo lookup immediately — only needs deckEntries, independent of Scryfall lookups.
            var comboStopwatch = Stopwatch.StartNew();
            var comboTask = AnalysisQuestionCatalog.RequiresComboLookup(selectedQuestions)
                ? _commanderSpellbookService.FindCombosAsync(deckEntries, cancellationToken)
                : Task.FromResult<CommanderSpellbookResult?>(null);

            var cardReferenceStopwatch = Stopwatch.StartNew();
            var cardReferenceBundle = await LookupCardReferencesAsync(unknownCardNames, cancellationToken).ConfigureAwait(false);
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

            var includeCardVersions = AnalysisQuestionCatalog.RequiresFullDecklistOutput(selectedQuestions) && request.IncludeCardVersions;
            var analysisDecklistText = includeCardVersions
                ? BuildDecklistText(deckEntries, possibleIncludeEntries, includeVersions: true)
                : decklistText;
            analysisPromptText = BuildAnalysisPrompt(request, analysisDecklistText, referenceText, deckProfileSchemaJson, commanderName, selectedQuestions, bannedCards, comboResult, includeCardVersions);
            setUpgradePromptText = BuildSetUpgradePrompt(request, decklistText, deckProfileText, commanderName, generatedSetPacket, bannedCards);
        }
        else if (!string.IsNullOrWhiteSpace(generatedSetPacket) || !string.IsNullOrWhiteSpace(request.DeckProfileJson) || !string.IsNullOrWhiteSpace(request.SetPacketText))
        {
            setUpgradePromptText = BuildSetUpgradePrompt(request, decklistText, deckProfileText, commanderName, generatedSetPacket, bannedCards);
        }

        if (request.SaveArtifactsToDisk)
        {
            var saveArtifactsStopwatch = Stopwatch.StartNew();
            savedArtifactsDirectory = await SaveArtifactsAsync(
                request,
                commanderName,
                inputSummary,
                probePromptText,
                probeSchemaJson,
                referenceText,
                analysisPromptText,
                deckProfileSchemaJson,
                setUpgradePromptText,
                cancellationToken).ConfigureAwait(false);
            timings.Add(("Artifact save", saveArtifactsStopwatch.ElapsedMilliseconds, null));
            _logger.LogInformation("ChatGPT packet artifact save completed in {ElapsedMs}ms.", saveArtifactsStopwatch.ElapsedMilliseconds);
        }

        _logger.LogInformation(
            "ChatGPT packet build completed in {ElapsedMs}ms. ProbeProvided={ProbeProvided} AnalysisGenerated={AnalysisGenerated} SetPacketGenerated={SetPacketGenerated}.",
            overallStopwatch.ElapsedMilliseconds,
            probeResponse is not null,
            !string.IsNullOrWhiteSpace(analysisPromptText),
            !string.IsNullOrWhiteSpace(setUpgradePromptText));

        var timingSummary = BuildTimingSummary(timings, overallStopwatch.ElapsedMilliseconds);

        return new ChatGptDeckPacketResult(
            inputSummary,
            probePromptText,
            probeSchemaJson,
            deckProfileSchemaJson,
            referenceText,
            analysisPromptText,
            setUpgradePromptText,
            savedArtifactsDirectory,
            timingSummary);
    }

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

    private static string FormatDecklistLine(DeckEntry entry, bool includeVersions)
    {
        var name = entry.Name;
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
        return line;
    }

    /// <summary>
    /// Builds the analysis deck text, keeping possible includes separate from the playable list.
    /// When <paramref name="includeVersions"/> is <see langword="true"/>, each commander and mainboard
    /// line includes the set code and collector number when available.
    /// </summary>
    private static string BuildDecklistText(IReadOnlyList<DeckEntry> entries, IReadOnlyList<DeckEntry> possibleIncludeEntries, bool includeVersions = false)
    {
        var commanderLines = entries
            .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => FormatDecklistLine(entry, includeVersions))
            .ToList();
        var mainboardLines = entries
            .Where(entry => !string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => FormatDecklistLine(entry, includeVersions))
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
                         .Select(entry => $"{entry.Quantity} {entry.Name}"))
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the probe-step deck text with commander, decklist, sideboard, and maybeboard sections.
    /// Preserves set code and collector number from the original input, normalizes double-faced card
    /// names to the front face, and annotates commander lines with [Commander].
    /// </summary>
    private static string BuildProbeDecklistText(IReadOnlyList<DeckEntry> entries)
    {
        var commanderLines = entries
            .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => FormatProbeCardLine(entry, isCommander: true))
            .ToList();
        var deckLines = entries
            .Where(entry => string.Equals(entry.Board, "mainboard", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => FormatProbeCardLine(entry, isCommander: false))
            .ToList();
        var sideboardLines = entries
            .Where(entry => string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => FormatProbeCardLine(entry, isCommander: false))
            .ToList();
        var maybeboardLines = entries
            .Where(entry => string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => FormatProbeCardLine(entry, isCommander: false))
            .ToList();

        var builder = new StringBuilder();
        AppendSection(builder, "Commander", commanderLines);
        AppendSection(builder, "Decklist", deckLines);
        AppendSection(builder, "Sideboard", sideboardLines);
        AppendSection(builder, "Maybeboard", maybeboardLines);
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats a single card line for the probe prompt.
    /// Uses the front-face name for double-faced cards, appends (SET) collectorNumber when present,
    /// and tags commander lines with [Commander].
    /// </summary>
    private static string FormatProbeCardLine(DeckEntry entry, bool isCommander)
    {
        var name = entry.Name;
        var slashIndex = name.IndexOf(" // ", StringComparison.Ordinal);
        if (slashIndex >= 0)
        {
            name = name[..slashIndex].TrimEnd();
        }

        var sb = new StringBuilder();
        sb.Append(entry.Quantity);
        sb.Append(' ');
        sb.Append(name);

        if (!string.IsNullOrWhiteSpace(entry.SetCode))
        {
            sb.Append($" ({entry.SetCode.ToUpperInvariant()})");
            if (!string.IsNullOrWhiteSpace(entry.CollectorNumber))
            {
                sb.Append(' ');
                sb.Append(entry.CollectorNumber);
            }
        }

        if (isCommander)
        {
            sb.Append(" [Commander]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the first prompt that asks ChatGPT to identify unknown cards.
    /// </summary>
    private static string BuildProbePrompt(ChatGptDeckRequest request, string decklistText, string? commanderName)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Task: Before analyzing this Magic: The Gathering deck, identify any card names you are uncertain about.");
        builder.AppendLine("Only list card names you are not confident you can explain accurately from memory.");
        builder.AppendLine();
        builder.AppendLine($"Suggested chat title: {BuildSuggestedChatTitle(request, commanderName)}");
        builder.AppendLine("Use that title for this ChatGPT conversation before you start the workflow.");
        builder.AppendLine();
        builder.AppendLine("Rules:");
        builder.AppendLine("- Do not guess card text.");
        builder.AppendLine("- Do not analyze the deck yet.");
        builder.AppendLine("- If you are unsure whether you know a card exactly, include it.");
        builder.AppendLine("- Verify whether the commander found in the deck is a legal commander by these established rules only: it must be a legendary creature, a legendary Vehicle, or a planeswalker whose text says it can be your commander.");
        builder.AppendLine("- If no commander is found in the deck context or decklist, return a missing commander status and a message telling the user to enter one before continuing.");
        builder.AppendLine("- Do not return mechanics. The app will derive mechanics from the returned card text.");
        builder.AppendLine("- Treat the Commander and Decklist sections as the actual deck being evaluated.");
        builder.AppendLine("- Treat the Sideboard and Maybeboard sections only as candidate additions, not part of the current deck.");
        builder.AppendLine("- Return valid JSON only.");
        builder.AppendLine("- Wrap the JSON in a ```json fenced code block so it can be copied cleanly.");
        builder.AppendLine();
        builder.AppendLine("Return this shape:");
        builder.AppendLine(BuildProbeResponseSchemaJson());
        builder.AppendLine();
        builder.AppendLine("deck_context:");
        builder.AppendLine($"format: {NormalizeSingleLine(request.Format, "Commander")}");
        if (!string.IsNullOrWhiteSpace(commanderName))
        {
            builder.AppendLine($"commander: {commanderName}");
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
        builder.AppendLine("decklist:");
        builder.AppendLine(decklistText);
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
        builder.AppendLine("official_commander_banned_cards:");
        builder.AppendLine(FormatBannedCardsLine(bannedCards));
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
        if (cardReferences.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var cardReference in cardReferences)
            {
                builder.AppendLine($"{cardReference.Name} | {cardReference.ManaCost} | {cardReference.TypeLine} | {cardReference.OracleText}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the main analysis prompt from the deck text, references, bracket guidance, and selected questions.
    /// </summary>
    private static string BuildAnalysisPrompt(ChatGptDeckRequest request, string decklistText, string referenceText, string deckProfileSchemaJson, string? commanderName, IReadOnlyList<string> selectedQuestionIds, IReadOnlyList<string> bannedCards, CommanderSpellbookResult? comboResult = null, bool includeCardVersions = false)
    {
        var bracket = CommanderBracketCatalog.Find(request.TargetCommanderBracket);
        var selectedQuestions = AnalysisQuestionCatalog.ResolveTexts(selectedQuestionIds, request.CardSpecificQuestionCardName, request.BudgetUpgradeAmount);
        var requiresFullDecklists = AnalysisQuestionCatalog.RequiresFullDecklistOutput(selectedQuestionIds);
        var requiresCategoryOutput = AnalysisQuestionCatalog.RequiresCategoryOutput(selectedQuestionIds);
        var builder = new StringBuilder();
        builder.AppendLine("Analyze this Magic: The Gathering deck.");
        builder.AppendLine();
        builder.AppendLine($"Suggested chat title: {BuildSuggestedChatTitle(request, commanderName)}");
        builder.AppendLine("Use that title for this ChatGPT conversation before you start the analysis.");
        builder.AppendLine();
        builder.AppendLine("Use the mechanic definitions as authoritative.");
        builder.AppendLine("Use the card reference as authoritative for listed cards.");
        builder.AppendLine("Before you begin the analysis, read the supplied card reference entries for any cards you did not already know, including any newly supplied keywords or mechanics, and use that supplied text when evaluating the deck.");
        builder.AppendLine("Do not invent card text or rules.");
        builder.AppendLine("Cards listed under Possible Includes are not part of the current deck. Treat them only as candidate additions.");
        builder.AppendLine("Do not recommend cards from the official Commander banned list.");
        builder.AppendLine($"Official Commander banned cards: {FormatBannedCardsLine(bannedCards)}");
        if (bracket is not null)
        {
            builder.AppendLine($"Target the Commander experience of {bracket.Label}.");
            builder.AppendLine($"Bracket summary: {bracket.Summary}");
            builder.AppendLine($"Turn expectation: {bracket.TurnsExpectation}");
            builder.AppendLine("Use that bracket target when evaluating speed, card quality, interaction density, and suggested improvements.");
        }
        builder.AppendLine();
        builder.AppendLine("Answer these requested analysis questions:");
        foreach (var question in selectedQuestions)
        {
            builder.AppendLine($"- {question}");
        }
        if (!string.IsNullOrWhiteSpace(request.FreeformQuestion))
        {
            builder.AppendLine($"- {request.FreeformQuestion.Trim()}");
        }
        builder.AppendLine();
        builder.AppendLine("After answering the questions, include these recommendation sections in the prose analysis:");
        builder.AppendLine("1. top adds");
        builder.AppendLine("2. top cuts");
        builder.AppendLine("For every recommended add and cut, explain the reasoning briefly and tie it back to the deck's plan, bracket target, or weaknesses.");
        if (requiresFullDecklists)
        {
            builder.AppendLine("IMPORTANT: For every requested deck-version or upgrade-path question, you must output the full, complete 100-card Commander decklist.");
            builder.AppendLine("Do NOT summarize, truncate, abbreviate, or say 'and X more cards', 'remaining lands', 'fill with basics', or anything similar.");
            builder.AppendLine("Every single card must be listed explicitly, one per line, with no omissions.");
            builder.AppendLine("Each list must contain exactly 1 commander and 99 other cards — count them before responding to confirm the total reaches 100.");
            builder.AppendLine("When a question asks for 3 upgrade paths, produce 3 separate full decklists — one per path.");
            var exportFormat = request.DecklistExportFormat.Trim();
            if (string.Equals(exportFormat, "moxfield", StringComparison.OrdinalIgnoreCase))
            {
                if (requiresCategoryOutput)
                    builder.AppendLine("Format each decklist for Moxfield bulk edit: one card per line as 'quantity CardName (SET) collectorNumber #Category1 #Category2' (e.g. '1 Sol Ring (CMM) 1 #Ramp #ManaRocks'). Category names must be single words with no spaces — use CamelCase or hyphens for multi-word categories. List all 100 cards together with no section headers. The commander line needs no category tags.");
                else
                    builder.AppendLine("Format each decklist for Moxfield bulk edit: one card per line as 'quantity CardName (SET) collectorNumber' (e.g. '1 Sol Ring (CMM) 1'). List all 100 cards together with no section headers. The commander is just another line.");
            }
            else if (string.Equals(exportFormat, "archidekt", StringComparison.OrdinalIgnoreCase))
            {
                if (requiresCategoryOutput)
                    builder.AppendLine("Format each decklist for Archidekt bulk edit: start with a '// Commander' section header, then the commander line as '1 CommanderName (SET) collectorNumber [Commander]', then a '// Mainboard' section header, then the remaining 99 cards as 'quantity CardName (SET) collectorNumber [Category1,Category2]' (one per line). Categories are comma-delimited inside square brackets — no spaces around commas, no quotes.");
                else
                    builder.AppendLine("Format each decklist for Archidekt bulk edit: start with a '// Commander' section header, then the commander line as '1 CommanderName (SET) collectorNumber [Commander]', then a '// Mainboard' section header, then the remaining 99 cards as 'quantity CardName (SET) collectorNumber' (one per line).");
            }
            else
            {
                builder.AppendLine("Format each decklist as plain text: quantity CardName (SET) collectorNumber (one card per line, e.g. '1 Sol Ring (CMM) 1'). Start with the commander line.");
            }
            if (requiresCategoryOutput)
            {
                builder.AppendLine("Do NOT use basic card types as categories (Creature, Instant, Sorcery, Enchantment, Artifact, Planeswalker, Battle). Use functional role categories instead (e.g. Ramp, Card Draw, Removal, Wipe, Tutor, Win Condition, Protection).");
                builder.AppendLine("Return the categorized decklist only inside a fenced ```text code block. Do not put the final decklist in prose or outside the code block.");
                var preferredCats = ParseCardNameList(request.PreferredCategories);
                if (preferredCats.Count > 0)
                {
                    builder.AppendLine($"Preferred category names: {string.Join(", ", preferredCats)}");
                    builder.AppendLine("Use these category names wherever they fit. You may create additional categories when none of the preferred names apply, but prefer the listed names.");
                }
            }
            if (includeCardVersions)
            {
                builder.AppendLine("For every card retained from the original deck, use the exact set code and collector number from the decklist below.");
                builder.AppendLine("For cards added that were not in the original deck, omit the set code and collector number — the deck builder will pick the default printing.");
            }
            builder.AppendLine("Return each full list in its own clearly labeled ```text fenced code block with the version or path name as the label (e.g. ```text Budget Efficiency).");
            builder.AppendLine("The goal is a list that can be pasted directly into the deck builder's bulk-edit field. It must be complete and machine-readable.");
            builder.AppendLine();
            builder.AppendLine("After each complete decklist, output these three sections:");
            builder.AppendLine("1. Cards Added — a bulleted list of every card in the new deck that was NOT in the original deck.");
            builder.AppendLine("2. Cards Cut — a bulleted list of every card in the original deck that is NOT in the new deck.");
            builder.AppendLine("3. A deck_profile JSON block for this specific deck version, using the same schema as the main deck_profile at the end of the response. Return it inside a ```json fenced code block labeled with the version name (e.g., ```json deck_profile — Budget Efficiency).");
        }
        var protectedCards = ParseCardNameList(request.ProtectedCards);
        if (protectedCards.Count > 0 && requiresFullDecklists)
        {
            builder.AppendLine($"Protected cards: {string.Join(", ", protectedCards)}");
            builder.AppendLine("Keep every protected card in all requested deck versions and upgrade paths.");
            builder.AppendLine("You may still mention them as potential cuts in the general top-cuts analysis if warranted by the deck evaluation.");
        }
        var comboReferenceText = BuildComboReferenceText(comboResult);
        if (!string.IsNullOrWhiteSpace(comboReferenceText))
        {
            builder.AppendLine();
            builder.AppendLine(comboReferenceText);
        }

        builder.AppendLine();
        builder.AppendLine("After the analysis, return a JSON object named deck_profile that matches this schema.");
        builder.AppendLine("Return the deck_profile JSON inside a ```json fenced code block so it can be copied cleanly.");
        builder.AppendLine(deckProfileSchemaJson);
        builder.AppendLine();
        builder.AppendLine("deck_context:");
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
        builder.AppendLine(referenceText);
        builder.AppendLine();
        builder.AppendLine("decklist:");
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

        builder.AppendLine("Given this deck and this set packet, analyze each selected set for possible additions to the deck, suggested removals for those additions, and any traps from that set.");
        builder.AppendLine();
        builder.AppendLine("Use the deck profile as authoritative for the deck's plan, strengths, weaknesses, and replaceable slots.");
        builder.AppendLine("Use the set mechanics and card reference as authoritative for the set.");
        builder.AppendLine("Do not invent card text or rules.");
        builder.AppendLine("Cards listed under Possible Includes are not part of the current deck. Treat them only as candidate additions.");
        builder.AppendLine("Do not recommend cards from the official Commander banned list.");
        builder.AppendLine($"Official Commander banned cards: {FormatBannedCardsLine(bannedCards)}");

        if (isLateralOnly)
        {
            builder.AppendLine();
            builder.AppendLine("Focus: lateral moves only.");
            builder.AppendLine("A lateral move is a card from the set that fills the same role as a card already in the deck but offers a different angle, better synergy fit, or a more interesting effect at roughly the same power level.");
            builder.AppendLine("For every lateral move, identify the current deck card it would replace and explain why the swap is worth considering even though neither card is strictly better.");
            builder.AppendLine("Do not recommend cards that are simply stronger — only flag those as traps if they would create a bracket or power mismatch.");
        }
        else if (isStrictOnly)
        {
            builder.AppendLine();
            builder.AppendLine("Focus: strict upgrades only.");
            builder.AppendLine("A strict upgrade is a card from the set that does the same job as a card already in the deck but is meaningfully more powerful, more efficient, or more synergistic with the deck's strategy.");
            builder.AppendLine("For every strict upgrade, name the card it replaces and explain precisely why it is better in this deck's context.");
            builder.AppendLine("Do not recommend lateral moves or speculative includes that are not clearly better than what the deck already runs.");
        }
        else if (isBoth)
        {
            builder.AppendLine();
            builder.AppendLine("Focus: strict upgrades and lateral moves.");
            builder.AppendLine("For strict upgrades: identify cards from the set that are meaningfully more powerful or efficient than a card already in the deck. Name the card being replaced and explain why it is better.");
            builder.AppendLine("For lateral moves: identify cards from the set that fill the same role as an existing card but offer a different angle, better synergy fit, or more interesting effect at roughly the same power level. Name the card being replaced and explain why the swap is worth considering.");
            builder.AppendLine("Label each recommendation clearly as 'Strict Upgrade' or 'Lateral Move'.");
        }

        builder.AppendLine();
        builder.AppendLine("Return sections:");
        builder.AppendLine("1. per-set analysis");
        builder.AppendLine("For each selected set, include:");
        builder.AppendLine("- top adds from that set");
        builder.AppendLine("- suggested removals for each add from that set");
        builder.AppendLine("- traps from that set");
        builder.AppendLine("- speculative tests from that set");
        builder.AppendLine("2. final cross-set ranked shortlist with must-test, optional, and skip");
        builder.AppendLine("For every recommended add or cut, explain the reasoning briefly and connect it to the deck profile.");
        builder.AppendLine("3. after the prose analysis, return a complete set_upgrade_report JSON object in a fenced ```json code block");
        builder.AppendLine("The JSON should capture the same per-set adds, suggested removals, traps, speculative tests, and final shortlist.");
        builder.AppendLine("Use this shape:");
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
        builder.AppendLine("4. after the JSON block, return a second fenced code block tagged as ```text named discussion_summary.txt");
        builder.AppendLine("That text block should include the per-set analysis in condensed form, the final recommendations, the reasoning behind the key adds and cuts, and direct answers to the questions discussed in this analysis so it can be copied as a standalone notes document.");
        builder.AppendLine();
        builder.AppendLine("deck_context:");
        builder.AppendLine($"format: {NormalizeSingleLine(request.Format, "Commander")}");
        if (!string.IsNullOrWhiteSpace(commanderName))
        {
            builder.AppendLine($"commander: {commanderName}");
        }

        builder.AppendLine();
        builder.AppendLine("deck_profile_json:");
        builder.AppendLine(deckProfileJson);
        builder.AppendLine();
        builder.AppendLine("decklist:");
        builder.AppendLine(decklistText);
        builder.AppendLine();
        builder.AppendLine("set_packet:");
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

        var commanderColorIdentity = await LookupCommanderColorIdentityAsync(request.DeckSource, cancellationToken).ConfigureAwait(false);
        var generatedPacket = await _scryfallSetService
            .BuildSetPacketAsync(request.SelectedSetCodes, commanderColorIdentity, cancellationToken)
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
    /// Returns the strict JSON schema expected from the probe-response step.
    /// </summary>
    private static string BuildProbeResponseSchemaJson()
    {
        return """
{
  "commander_status": "valid",
  "commander_name": "Card Name",
  "commander_reason": "",
  "unknown_cards": [
    "Card Name"
  ]
}
""";
    }

    /// <summary>
    /// Returns the deck-profile schema that ChatGPT should follow during analysis.
    /// </summary>
    private static string BuildDeckProfileSchemaJson(string? commanderName, string format)
    {
        var payload = new
        {
            format = NormalizeSingleLine(format, "Commander"),
            commander = commanderName ?? string.Empty,
            game_plan = string.Empty,
            primary_axes = Array.Empty<string>(),
            speed = string.Empty,
            strengths = Array.Empty<string>(),
            weaknesses = Array.Empty<string>(),
            deck_needs = Array.Empty<string>(),
            weak_slots = new[]
            {
                new
                {
                    card = string.Empty,
                    reason = string.Empty
                }
            },
            synergy_tags = Array.Empty<string>()
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static ProbeResponse? ParseProbeResponse(string probeResponseJson)
    {
        if (string.IsNullOrWhiteSpace(probeResponseJson))
        {
            return null;
        }

        try
        {
            var response = JsonSerializer.Deserialize<ProbeResponse>(ExtractJsonObject(probeResponseJson), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return response is null
                ? null
                : new ProbeResponse(NormalizeDistinct(response.UnknownCards));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Probe response JSON was invalid. Return only the requested JSON object.", exception);
        }
    }

    private static List<string> NormalizeDistinct(IReadOnlyList<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> MergeUnknownCards(IReadOnlyList<string> unknownCards, string? commanderName)
    {
        var merged = NormalizeDistinct(unknownCards);
        if (!string.IsNullOrWhiteSpace(commanderName) && !merged.Contains(commanderName, StringComparer.OrdinalIgnoreCase))
        {
            merged.Insert(0, commanderName);
        }

        return merged;
    }

    private async Task<CardReferenceBundle> LookupCardReferencesAsync(IReadOnlyList<string> cardNames, CancellationToken cancellationToken)
    {
        if (cardNames.Count == 0)
        {
            return new CardReferenceBundle(Array.Empty<CardReference>(), Array.Empty<string>());
        }

        var resolvedCards = new Dictionary<string, CardReference>(StringComparer.OrdinalIgnoreCase);
        var mechanicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in Chunk(cardNames, ScryfallBatchSize))
        {
            var request = new RestRequest("cards/collection", Method.Post);
            request.AddJsonBody(new
            {
                identifiers = chunk.Select(name => new { name }).ToArray()
            });

            var response = await _executeCollectionAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
            {
                throw new HttpRequestException(
                    $"Scryfall search returned HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            foreach (var card in response.Data.Data)
            {
                resolvedCards[card.Name] = new CardReference(
                    card.Name,
                    card.ManaCost ?? string.Empty,
                    card.TypeLine,
                    NormalizeOracleText(card));

                foreach (var mechanicName in ExtractMechanicNames(card))
                {
                    mechanicNames.Add(mechanicName);
                }
            }

            foreach (var unresolvedName in chunk.Where(name => !resolvedCards.ContainsKey(name)))
            {
                var fallbackCard = await SearchFallbackCardAsync(unresolvedName, cancellationToken).ConfigureAwait(false);
                if (fallbackCard is null)
                {
                    continue;
                }

                var displayName = NormalizeLookupName(unresolvedName) == NormalizeLookupName(fallbackCard.Name)
                    ? fallbackCard.Name
                    : $"submitted_name: {unresolvedName} | resolved_card: {fallbackCard.Name}";

                resolvedCards[unresolvedName] = new CardReference(
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

        var cardReferences = cardNames
            .Where(name => resolvedCards.ContainsKey(name))
            .Select(name => resolvedCards[name])
            .ToList();

        return new CardReferenceBundle(
            cardReferences,
            mechanicNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList());
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
        if ((int)namedResponse.StatusCode >= 200 && (int)namedResponse.StatusCode < 300 && namedResponse.Data is not null)
        {
            return namedResponse.Data;
        }

        return null;
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
            throw new InvalidOperationException("The commander isn't in the deck text. Add a legal commander line before generating the probe prompt.");
        }

        var commanderEntry = entries.FirstOrDefault(entry => string.Equals(entry.Name, commanderName, StringComparison.OrdinalIgnoreCase));
        if (commanderEntry is null)
        {
            throw new InvalidOperationException("The commander isn't in the deck text. Add a legal commander line before generating the probe prompt.");
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

    private static IEnumerable<List<string>> Chunk(IReadOnlyList<string> values, int size)
    {
        for (var index = 0; index < values.Count; index += size)
        {
            var count = Math.Min(size, values.Count - index);
            var chunk = new List<string>(count);
            for (var itemIndex = 0; itemIndex < count; itemIndex++)
            {
                chunk.Add(values[index + itemIndex]);
            }

            yield return chunk;
        }
    }

    private static void AppendSection(StringBuilder builder, string header, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.AppendLine(header);
        foreach (var line in lines)
        {
            builder.AppendLine(line);
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
        string probePromptText,
        string probeResponseSchemaJson,
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
            ("10-probe-prompt.txt", "PROBE PROMPT", probePromptText),
            ("11-probe-schema.json", "PROBE RESPONSE JSON SCHEMA", probeResponseSchemaJson),
            ("30-reference.txt", "REFERENCE TEXT", referenceText),
            ("31-analysis-prompt.txt", "ANALYSIS PROMPT", analysisPromptText),
            ("41-deck-profile-schema.json", "DECK PROFILE JSON SCHEMA", deckProfileSchemaJson),
            ("50-set-upgrade-prompt.txt", "SET UPGRADE PROMPT", setUpgradePromptText)
        };

        var responseSections = new List<(string FileName, string Label, string? Content)>
        {
            ("20-probe-response.json", "PROBE RESPONSE JSON", request.ProbeResponseJson),
            ("40-deck-profile.json", "DECK PROFILE JSON", request.DeckProfileJson)
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

    private sealed record ProbeResponse([property: JsonPropertyName("unknown_cards")] IReadOnlyList<string> UnknownCards);

    private sealed record CardReference(string Name, string ManaCost, string TypeLine, string OracleText);

    private sealed record CardReferenceBundle(IReadOnlyList<CardReference> CardReferences, IReadOnlyList<string> MechanicNames);

    private sealed record MechanicReference(string Name, string Description, string? RuleReference);
}
