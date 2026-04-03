using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Parsing;
using Microsoft.AspNetCore.Hosting;
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

    public ChatGptDeckPacketService(
        IMoxfieldDeckImporter moxfieldDeckImporter,
        IArchidektDeckImporter archidektDeckImporter,
        MoxfieldParser moxfieldParser,
        ArchidektParser archidektParser,
        IMechanicLookupService mechanicLookupService,
        ICommanderBanListService commanderBanListService,
        IScryfallSetService scryfallSetService,
        IWebHostEnvironment environment,
        RestClient? scryfallRestClient = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeSearchAsync = null)
    {
        _moxfieldDeckImporter = moxfieldDeckImporter;
        _archidektDeckImporter = archidektDeckImporter;
        _moxfieldParser = moxfieldParser;
        _archidektParser = archidektParser;
        _mechanicLookupService = mechanicLookupService;
        _commanderBanListService = commanderBanListService;
        _scryfallSetService = scryfallSetService;
        _chatGptArtifactsPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "artifacts", "chatgpt-packets"));
        var client = scryfallRestClient ?? ScryfallRestClientFactory.Create();
        _executeCollectionAsync = executeCollectionAsync ?? ((request, cancellationToken) => client.ExecuteAsync<ScryfallCollectionResponse>(request, cancellationToken));
        _executeSearchAsync = executeSearchAsync ?? ((request, cancellationToken) => client.ExecuteAsync<ScryfallSearchResponse>(request, cancellationToken));
    }

    public async Task<ChatGptDeckPacketResult> BuildAsync(ChatGptDeckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DeckSource))
        {
            throw new InvalidOperationException("A deck URL or pasted deck export is required.");
        }

        var entries = await LoadDeckEntriesAsync(request.DeckSource, cancellationToken).ConfigureAwait(false);
        var relevantEntries = entries
            .Where(entry => !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (relevantEntries.Count == 0)
        {
            throw new InvalidOperationException("The submitted deck did not contain any commander or mainboard cards.");
        }

        var commanderName = relevantEntries
            .FirstOrDefault(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            ?.Name;

        var inputSummary = BuildInputSummary(request, relevantEntries, commanderName);
        var decklistText = BuildDecklistText(relevantEntries);
        var probePromptText = BuildProbePrompt(request, decklistText, commanderName);
        var probeSchemaJson = BuildProbeResponseSchemaJson();
        var deckProfileSchemaJson = BuildDeckProfileSchemaJson(commanderName, request.Format);

        string? referenceText = null;
        string? analysisPromptText = null;
        string? setUpgradePromptText = null;
        string? savedArtifactsDirectory = null;
        var bannedCards = await _commanderBanListService.GetBannedCardsAsync(cancellationToken).ConfigureAwait(false);

        var probeResponse = ParseProbeResponse(request.ProbeResponseJson);
        var deckProfileText = string.IsNullOrWhiteSpace(request.DeckProfileJson)
            ? deckProfileSchemaJson
            : ExtractJsonObject(request.DeckProfileJson);
        var generatedSetPacket = await BuildGeneratedSetPacketAsync(request, cancellationToken).ConfigureAwait(false);
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

        if (probeResponse is not null)
        {
            var unknownCardNames = MergeUnknownCards(probeResponse.UnknownCards, commanderName);
            var cardReferenceBundle = await LookupCardReferencesAsync(unknownCardNames, cancellationToken).ConfigureAwait(false);
            var mechanicReferences = await LookupMechanicReferencesAsync(cardReferenceBundle.MechanicNames, cancellationToken).ConfigureAwait(false);

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
        }

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

    private static string BuildInputSummary(ChatGptDeckRequest request, IReadOnlyList<DeckEntry> entries, string? commanderName)
    {
        var totalCards = entries.Sum(entry => entry.Quantity);
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

        builder.AppendLine($"Cards included: {totalCards}");
        if (!string.IsNullOrWhiteSpace(request.SetName))
        {
            builder.AppendLine($"Target set: {request.SetName.Trim()}");
        }

        var bracket = CommanderBracketCatalog.Find(request.TargetCommanderBracket);
        if (bracket is not null)
        {
            builder.AppendLine($"Target commander bracket: {bracket.Label}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildDecklistText(IReadOnlyList<DeckEntry> entries)
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
        builder.AppendLine("- Verify whether the commander found in the deck is a legal commander by these established rules only: it must be a legendary creature or a legendary artifact.");
        builder.AppendLine("- If no commander is found in the deck context or decklist, return a missing commander status and a message telling the user to enter one before continuing.");
        builder.AppendLine("- Do not return mechanics. The app will derive mechanics from the returned card text.");
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
        var selectedQuestions = AnalysisQuestionCatalog.ResolveTexts(selectedQuestionIds, request.CardSpecificQuestionCardName);
        var builder = new StringBuilder();
        builder.AppendLine("Analyze this Magic: The Gathering deck.");
        builder.AppendLine();
        builder.AppendLine("Use the mechanic definitions as authoritative.");
        builder.AppendLine("Use the card reference as authoritative for listed cards.");
        builder.AppendLine("Do not invent card text or rules.");
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
        builder.AppendLine("Given this deck and this set packet, which cards from the set are real upgrades, what should they replace, and which cards from the set are traps for this deck?");
        builder.AppendLine();
        builder.AppendLine("Use the deck profile as authoritative for the deck's plan, strengths, weaknesses, and replaceable slots.");
        builder.AppendLine("Use the set mechanics and card reference as authoritative for the set.");
        builder.AppendLine("Do not invent card text or rules.");
        builder.AppendLine("Do not recommend cards from the official Commander banned list.");
        builder.AppendLine($"Official Commander banned cards: {FormatBannedCardsLine(bannedCards)}");
        builder.AppendLine();
        builder.AppendLine("Return sections:");
        builder.AppendLine("1. real upgrades");
        builder.AppendLine("2. likely cuts for each upgrade");
        builder.AppendLine("3. traps");
        builder.AppendLine("4. speculative tests");
        builder.AppendLine("5. final ranked shortlist with must-test, optional, and skip");
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
            builder.AppendLine($"Paste the condensed set packet for {NormalizeSingleLine(request.SetName, "[set name]")} here.");
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

        var generatedPacket = await _scryfallSetService.BuildSetPacketAsync(request.SelectedSetCodes, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(generatedPacket) ? null : generatedPacket;
    }

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

                resolvedCards[unresolvedName] = new CardReference(
                    fallbackCard.Name,
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

        var request = new RestRequest("cards/search", Method.Get);
        request.AddQueryParameter("q", $"(printed:\"{cardName}\" OR name:\"{cardName}\")");
        request.AddQueryParameter("unique", "prints");
        request.AddQueryParameter("include_multilingual", "true");

        var response = await _executeSearchAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
        {
            return null;
        }

        return response.Data.Data.FirstOrDefault();
    }

    private async Task<IReadOnlyList<MechanicReference>> LookupMechanicReferencesAsync(IReadOnlyList<string> mechanicNames, CancellationToken cancellationToken)
    {
        var results = new List<MechanicReference>();
        foreach (var mechanicName in mechanicNames)
        {
            var result = await _mechanicLookupService.LookupAsync(mechanicName, cancellationToken).ConfigureAwait(false);
            var description = result.SummaryText ?? result.RulesText ?? "No official rules text found.";
            results.Add(new MechanicReference(
                mechanicName,
                CollapseWhitespace(description),
                result.RuleReference));
        }

        return results;
    }

    private static string NormalizeOracleText(ScryfallCard card)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(card.OracleText))
        {
            parts.Add(CollapseWhitespace(card.OracleText));
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

        builder.AppendLine();
        builder.AppendLine("strategy_notes:");
        builder.AppendLine(request.StrategyNotes.Trim());
        builder.AppendLine();
        builder.AppendLine("meta_notes:");
        builder.AppendLine(request.MetaNotes.Trim());
        builder.AppendLine();
        builder.AppendLine("set_name:");
        builder.AppendLine(request.SetName.Trim());
        builder.AppendLine();
        builder.AppendLine("deck_source:");
        builder.AppendLine(request.DeckSource.Trim());
        return builder.ToString().TrimEnd() + Environment.NewLine;
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

        if (string.IsNullOrWhiteSpace(card.OracleText))
        {
            yield break;
        }

        foreach (var line in card.OracleText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
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

    [GeneratedRegex(@"^(?<term>[A-Za-z][A-Za-z' -]{1,40})\s+—\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AbilityWordPattern();

    private sealed record ProbeResponse([property: JsonPropertyName("unknown_cards")] IReadOnlyList<string> UnknownCards);

    private sealed record CardReference(string Name, string ManaCost, string TypeLine, string OracleText);

    private sealed record CardReferenceBundle(IReadOnlyList<CardReference> CardReferences, IReadOnlyList<string> MechanicNames);

    private sealed record MechanicReference(string Name, string Description, string? RuleReference);
}
