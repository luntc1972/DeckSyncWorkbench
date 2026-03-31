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

public interface IChatGptDeckPacketService
{
    Task<ChatGptDeckPacketResult> BuildAsync(ChatGptDeckRequest request, CancellationToken cancellationToken = default);
}

public sealed record ChatGptDeckPacketResult(
    string InputSummary,
    string ProbePromptText,
    string ProbeResponseSchemaJson,
    string DeckProfileSchemaJson,
    string? ReferenceText,
    string? AnalysisPromptText,
    string? SetUpgradePromptText,
    string? SavedArtifactsDirectory);

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
    private readonly string _chatGptArtifactsPath;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> _executeCollectionAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>> _executeSearchAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCard>>> _executeNamedAsync;
    private readonly ILogger<ChatGptDeckPacketService> _logger;

    public ChatGptDeckPacketService(
        IMoxfieldDeckImporter moxfieldDeckImporter,
        IArchidektDeckImporter archidektDeckImporter,
        MoxfieldParser moxfieldParser,
        ArchidektParser archidektParser,
        IMechanicLookupService mechanicLookupService,
        ICommanderBanListService commanderBanListService,
        IScryfallSetService scryfallSetService,
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

    public async Task<ChatGptDeckPacketResult> BuildAsync(ChatGptDeckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var overallStopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.DeckSource))
        {
            throw new InvalidOperationException("A deck URL or pasted deck export is required.");
        }

        var loadDeckStopwatch = Stopwatch.StartNew();
        var entries = await LoadDeckEntriesAsync(request.DeckSource, cancellationToken).ConfigureAwait(false);
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
        var bannedCardsStopwatch = Stopwatch.StartNew();
        var bannedCards = await _commanderBanListService.GetBannedCardsAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ChatGPT packet banned-list fetch completed in {ElapsedMs}ms.", bannedCardsStopwatch.ElapsedMilliseconds);

        var probeResponse = ParseProbeResponse(request.ProbeResponseJson);
        var deckProfileText = string.IsNullOrWhiteSpace(request.DeckProfileJson)
            ? deckProfileSchemaJson
            : ExtractJsonObject(request.DeckProfileJson);
        var setPacketStopwatch = Stopwatch.StartNew();
        var generatedSetPacket = await BuildGeneratedSetPacketAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ChatGPT packet set-packet generation completed in {ElapsedMs}ms.", setPacketStopwatch.ElapsedMilliseconds);
        var selectedQuestions = AnalysisQuestionCatalog.NormalizeSelections(request.SelectedAnalysisQuestions);

        if (probeResponse is not null && CommanderBracketCatalog.Find(request.TargetCommanderBracket) is null)
        {
            throw new InvalidOperationException("Choose a target Commander bracket before generating the analysis packet.");
        }

        if (probeResponse is not null && selectedQuestions.Count == 0)
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
            && selectedQuestions.Contains("budget-upgrades", StringComparer.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.BudgetUpgradeAmount))
        {
            throw new InvalidOperationException("Enter a budget amount for the selected budget upgrade question.");
        }

        if (probeResponse is not null)
        {
            var unknownCardNames = MergeUnknownCards(probeResponse.UnknownCards, commanderName);
            var cardReferenceStopwatch = Stopwatch.StartNew();
            var cardReferenceBundle = await LookupCardReferencesAsync(unknownCardNames, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "ChatGPT packet card reference lookup completed in {ElapsedMs}ms for {CardCount} cards and {MechanicCount} mechanics.",
                cardReferenceStopwatch.ElapsedMilliseconds,
                cardReferenceBundle.CardReferences.Count,
                cardReferenceBundle.MechanicNames.Count);
            var mechanicReferenceStopwatch = Stopwatch.StartNew();
            var mechanicReferences = await LookupMechanicReferencesAsync(cardReferenceBundle.MechanicNames, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "ChatGPT packet mechanic lookup completed in {ElapsedMs}ms for {MechanicCount} mechanics.",
                mechanicReferenceStopwatch.ElapsedMilliseconds,
                mechanicReferences.Count);

            referenceText = BuildReferenceText(request, mechanicReferences, cardReferenceBundle.CardReferences, bannedCards);
            analysisPromptText = BuildAnalysisPrompt(request, decklistText, referenceText, deckProfileSchemaJson, commanderName, selectedQuestions, bannedCards);
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
            _logger.LogInformation("ChatGPT packet artifact save completed in {ElapsedMs}ms.", saveArtifactsStopwatch.ElapsedMilliseconds);
        }

        _logger.LogInformation(
            "ChatGPT packet build completed in {ElapsedMs}ms. ProbeProvided={ProbeProvided} AnalysisGenerated={AnalysisGenerated} SetPacketGenerated={SetPacketGenerated}.",
            overallStopwatch.ElapsedMilliseconds,
            probeResponse is not null,
            !string.IsNullOrWhiteSpace(analysisPromptText),
            !string.IsNullOrWhiteSpace(setUpgradePromptText));

        return new ChatGptDeckPacketResult(
            inputSummary,
            probePromptText,
            probeSchemaJson,
            deckProfileSchemaJson,
            referenceText,
            analysisPromptText,
            setUpgradePromptText,
            savedArtifactsDirectory);
    }

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

    private static string BuildDecklistText(IReadOnlyList<DeckEntry> entries, IReadOnlyList<DeckEntry> possibleIncludeEntries)
    {
        var commanderLines = entries
            .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Quantity} {entry.Name}")
            .ToList();
        var mainboardLines = entries
            .Where(entry => !string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Quantity} {entry.Name}")
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

    private static string BuildProbeDecklistText(IReadOnlyList<DeckEntry> entries)
    {
        var commanderLines = entries
            .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Quantity} {entry.Name}")
            .ToList();
        var deckLines = entries
            .Where(entry => string.Equals(entry.Board, "mainboard", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Quantity} {entry.Name}")
            .ToList();
        var sideboardLines = entries
            .Where(entry => string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Quantity} {entry.Name}")
            .ToList();
        var maybeboardLines = entries
            .Where(entry => string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Quantity} {entry.Name}")
            .ToList();

        var builder = new StringBuilder();
        AppendSection(builder, "Commander", commanderLines);
        AppendSection(builder, "Decklist", deckLines);
        AppendSection(builder, "Sideboard", sideboardLines);
        AppendSection(builder, "Maybeboard", maybeboardLines);
        return builder.ToString().TrimEnd();
    }

    private static string BuildProbePrompt(ChatGptDeckRequest request, string decklistText, string? commanderName)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Task: Before analyzing this Magic: The Gathering deck, identify any card names you are uncertain about.");
        builder.AppendLine("Only list card names you are not confident you can explain accurately from memory.");
        builder.AppendLine();
        builder.AppendLine("Rules:");
        builder.AppendLine("- Do not guess card text.");
        builder.AppendLine("- Do not analyze the deck yet.");
        builder.AppendLine("- If you are unsure whether you know a card exactly, include it.");
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

    private static string BuildAnalysisPrompt(ChatGptDeckRequest request, string decklistText, string referenceText, string deckProfileSchemaJson, string? commanderName, IReadOnlyList<string> selectedQuestionIds, IReadOnlyList<string> bannedCards)
    {
        var bracket = CommanderBracketCatalog.Find(request.TargetCommanderBracket);
        var selectedQuestions = AnalysisQuestionCatalog.ResolveTexts(selectedQuestionIds, request.CardSpecificQuestionCardName, request.BudgetUpgradeAmount);
        var builder = new StringBuilder();
        builder.AppendLine("Analyze this Magic: The Gathering deck.");
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
        builder.AppendLine();
        builder.AppendLine("After answering the questions, include these recommendation sections in the prose analysis:");
        builder.AppendLine("1. top adds");
        builder.AppendLine("2. top cuts");
        builder.AppendLine("For every recommended add and cut, explain the reasoning briefly and tie it back to the deck's plan, bracket target, or weaknesses.");
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

    private static string BuildSetUpgradePrompt(ChatGptDeckRequest request, string decklistText, string deckProfileJson, string? commanderName, string? generatedSetPacket, IReadOnlyList<string> bannedCards)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Given this deck and this set packet, analyze each selected set for possible additions to the deck, suggested removals for those additions, and any traps from that set.");
        builder.AppendLine();
        builder.AppendLine("Use the deck profile as authoritative for the deck's plan, strengths, weaknesses, and replaceable slots.");
        builder.AppendLine("Use the set mechanics and card reference as authoritative for the set.");
        builder.AppendLine("Do not invent card text or rules.");
        builder.AppendLine("Cards listed under Possible Includes are not part of the current deck. Treat them only as candidate additions.");
        builder.AppendLine("Do not recommend cards from the official Commander banned list.");
        builder.AppendLine($"Official Commander banned cards: {FormatBannedCardsLine(bannedCards)}");
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

    private static string BuildProbeResponseSchemaJson()
    {
        return """
{
  "unknown_cards": [
    "Card Name"
  ]
}
""";
    }

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

    [GeneratedRegex(@"^(?<term>[A-Za-z][A-Za-z' -]{1,40})\s+—\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AbilityWordPattern();

    private sealed record ProbeResponse([property: JsonPropertyName("unknown_cards")] IReadOnlyList<string> UnknownCards);

    private sealed record CardReference(string Name, string ManaCost, string TypeLine, string OracleText);

    private sealed record CardReferenceBundle(IReadOnlyList<CardReference> CardReferences, IReadOnlyList<string> MechanicNames);

    private sealed record MechanicReference(string Name, string Description, string? RuleReference);
}
