using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Core.Reporting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RestSharp;
using MtgDeckStudio.Web.Models;

namespace MtgDeckStudio.Web.Services;

public interface IChatGptDeckComparisonService
{
    Task<ChatGptDeckComparisonResult> BuildAsync(ChatGptDeckComparisonRequest request, CancellationToken cancellationToken = default);
}

public sealed record ChatGptDeckComparisonResult(
    string InputSummary,
    string DeckAListText,
    string DeckBListText,
    string DeckAComboText,
    string DeckBComboText,
    string ComparisonContextText,
    string ComparisonPromptText,
    string FollowUpPromptText,
    string ComparisonSchemaJson,
    ChatGptDeckComparisonResponse? ComparisonResponse,
    string? SavedArtifactsDirectory,
    string? TimingSummary);

public sealed class ChatGptDeckComparisonService : IChatGptDeckComparisonService
{
    private const int ScryfallBatchSize = 75;

    private readonly IMoxfieldDeckImporter _moxfieldDeckImporter;
    private readonly IArchidektDeckImporter _archidektDeckImporter;
    private readonly MoxfieldParser _moxfieldParser;
    private readonly ArchidektParser _archidektParser;
    private readonly ICommanderSpellbookService _commanderSpellbookService;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> _executeCollectionAsync;
    private readonly string _artifactsPath;
    private readonly ILogger<ChatGptDeckComparisonService> _logger;

    public ChatGptDeckComparisonService(
        IMoxfieldDeckImporter moxfieldDeckImporter,
        IArchidektDeckImporter archidektDeckImporter,
        MoxfieldParser moxfieldParser,
        ArchidektParser archidektParser,
        ICommanderSpellbookService commanderSpellbookService,
        IWebHostEnvironment environment,
        ILogger<ChatGptDeckComparisonService>? logger = null,
        RestClient? scryfallRestClient = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsync = null,
        string? artifactsPath = null)
    {
        _moxfieldDeckImporter = moxfieldDeckImporter;
        _archidektDeckImporter = archidektDeckImporter;
        _moxfieldParser = moxfieldParser;
        _archidektParser = archidektParser;
        _commanderSpellbookService = commanderSpellbookService;
        _logger = logger ?? NullLogger<ChatGptDeckComparisonService>.Instance;
        _artifactsPath = string.IsNullOrWhiteSpace(artifactsPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MTG Deck Studio",
                "ChatGPT Deck Comparison")
            : Path.GetFullPath(artifactsPath);

        var client = scryfallRestClient ?? ScryfallRestClientFactory.Create();
        _executeCollectionAsync = executeCollectionAsync ?? ((request, cancellationToken) => client.ExecuteAsync<ScryfallCollectionResponse>(request, cancellationToken));
    }

    public async Task<ChatGptDeckComparisonResult> BuildAsync(ChatGptDeckComparisonRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var overallStopwatch = Stopwatch.StartNew();
        var timings = new List<(string Label, long Ms, string? Detail)>();

        if (string.IsNullOrWhiteSpace(request.DeckASource))
        {
            throw new InvalidOperationException("Deck A URL or deck text is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DeckBSource))
        {
            throw new InvalidOperationException("Deck B URL or deck text is required.");
        }

        var deckABracket = CommanderBracketCatalog.Find(request.DeckABracket)
            ?? throw new InvalidOperationException("Choose a Commander bracket for Deck A before generating the comparison packet.");
        var deckBBracket = CommanderBracketCatalog.Find(request.DeckBBracket)
            ?? throw new InvalidOperationException("Choose a Commander bracket for Deck B before generating the comparison packet.");

        var deckALoadStopwatch = Stopwatch.StartNew();
        var deckA = await LoadDeckAsync("Deck A", request.DeckASource, cancellationToken).ConfigureAwait(false);
        timings.Add(("Deck A load", deckALoadStopwatch.ElapsedMilliseconds, $"{deckA.PlayableEntries.Sum(entry => entry.Quantity)} cards"));

        var deckBLoadStopwatch = Stopwatch.StartNew();
        var deckB = await LoadDeckAsync("Deck B", request.DeckBSource, cancellationToken).ConfigureAwait(false);
        timings.Add(("Deck B load", deckBLoadStopwatch.ElapsedMilliseconds, $"{deckB.PlayableEntries.Sum(entry => entry.Quantity)} cards"));

        var deckAName = ResolveDeckName(request.DeckAName, deckA.CommanderName, "Deck A");
        var deckBName = ResolveDeckName(request.DeckBName, deckB.CommanderName, "Deck B");

        var deckAListText = BuildDecklistText(deckA.PlayableEntries, deckA.OptionalEntries);
        var deckBListText = BuildDecklistText(deckB.PlayableEntries, deckB.OptionalEntries);

        var lookupStopwatch = Stopwatch.StartNew();
        var deckACards = await LookupCardDetailsAsync("Deck A", deckA.PlayableEntries, cancellationToken).ConfigureAwait(false);
        var deckBCards = await LookupCardDetailsAsync("Deck B", deckB.PlayableEntries, cancellationToken).ConfigureAwait(false);
        timings.Add(("Scryfall card lookup", lookupStopwatch.ElapsedMilliseconds, $"Deck A {deckACards.Count} cards | Deck B {deckBCards.Count} cards"));

        var comboLookupStopwatch = Stopwatch.StartNew();
        var deckACombos = await _commanderSpellbookService.FindCombosAsync(deckA.PlayableEntries, cancellationToken).ConfigureAwait(false);
        var deckBCombos = await _commanderSpellbookService.FindCombosAsync(deckB.PlayableEntries, cancellationToken).ConfigureAwait(false);
        timings.Add((
            "Commander Spellbook",
            comboLookupStopwatch.ElapsedMilliseconds,
            $"Deck A {deckACombos?.IncludedCombos.Count ?? 0} combos | Deck B {deckBCombos?.IncludedCombos.Count ?? 0} combos"));

        var deckASummary = BuildDeckSummary(deckAName, deckA.CommanderName, deckABracket, deckA.PlayableEntries, deckACards, deckACombos);
        var deckBSummary = BuildDeckSummary(deckBName, deckB.CommanderName, deckBBracket, deckB.PlayableEntries, deckBCards, deckBCombos);
        var deckAComboText = BuildComboArtifactText(deckASummary);
        var deckBComboText = BuildComboArtifactText(deckBSummary);
        var comparisonContextText = BuildComparisonContextText(deckASummary, deckBSummary);
        var comparisonSchemaJson = BuildComparisonSchemaJson(deckAName, deckBName, deckA.CommanderName, deckB.CommanderName, deckABracket.Label, deckBBracket.Label);
        var inputSummary = BuildInputSummary(deckASummary, deckBSummary);
        var comparisonPromptText = BuildComparisonPrompt(
            deckASummary,
            deckBSummary,
            deckAListText,
            deckBListText,
            deckAComboText,
            deckBComboText,
            comparisonContextText,
            comparisonSchemaJson);
        var followUpPromptText = BuildFollowUpPrompt(comparisonSchemaJson);

        ChatGptDeckComparisonResponse? comparisonResponse = null;
        if (request.WorkflowStep >= 3 && !string.IsNullOrWhiteSpace(request.ComparisonResponseJson))
        {
            comparisonResponse = ParseComparisonResponse(request.ComparisonResponseJson);
        }

        string? savedArtifactsDirectory = null;
        if (request.SaveArtifactsToDisk)
        {
            var artifactStopwatch = Stopwatch.StartNew();
            savedArtifactsDirectory = await SaveArtifactsAsync(
                request,
                deckASummary,
                deckBSummary,
                inputSummary,
                deckAListText,
                deckBListText,
                comparisonContextText,
                comparisonPromptText,
                followUpPromptText,
                comparisonSchemaJson,
                cancellationToken).ConfigureAwait(false);
            timings.Add(("Artifact save", artifactStopwatch.ElapsedMilliseconds, null));
        }

        var timingSummary = BuildTimingSummary(timings, overallStopwatch.ElapsedMilliseconds);

        return new ChatGptDeckComparisonResult(
            inputSummary,
            deckAListText,
            deckBListText,
            deckAComboText,
            deckBComboText,
            comparisonContextText,
            comparisonPromptText,
            followUpPromptText,
            comparisonSchemaJson,
            comparisonResponse,
            savedArtifactsDirectory,
            timingSummary);
    }

    private async Task<LoadedDeck> LoadDeckAsync(string deckLabel, string deckSource, CancellationToken cancellationToken)
    {
        List<DeckEntry> entries;
        try
        {
            entries = await LoadDeckEntriesAsync(deckSource, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DeckParseException or HttpRequestException)
        {
            throw new InvalidOperationException($"{deckLabel} parse failed: {exception.Message}", exception);
        }

        var playableEntries = entries
            .Where(entry =>
                !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var optionalEntries = entries
            .Where(entry =>
                string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (playableEntries.Count == 0)
        {
            throw new InvalidOperationException($"{deckLabel} parse failed: the submitted deck did not contain any commander or mainboard cards.");
        }

        var hasExplicitCommander = playableEntries.Any(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase));
        var commanderName = playableEntries
            .FirstOrDefault(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            ?.Name;

        if (string.IsNullOrWhiteSpace(commanderName))
        {
            if (!hasExplicitCommander && playableEntries.Count < 2)
            {
                throw new InvalidOperationException($"{deckLabel} parse failed: could not determine a commander from the submitted deck.");
            }

            commanderName = playableEntries
                .Where(entry => entry.Quantity == 1)
                .Select(entry => entry.Name)
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(commanderName))
        {
            throw new InvalidOperationException($"{deckLabel} parse failed: could not determine a commander from the submitted deck.");
        }

        return new LoadedDeck(entries, playableEntries, optionalEntries, commanderName ?? string.Empty);
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

    private async Task<IReadOnlyList<ScryfallCard>> LookupCardDetailsAsync(string deckLabel, IReadOnlyList<DeckEntry> entries, CancellationToken cancellationToken)
    {
        var uniqueNames = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resolvedCards = new List<ScryfallCard>();
        foreach (var chunk in Chunk(uniqueNames, ScryfallBatchSize))
        {
            var request = new RestRequest("cards/collection", Method.Post)
                .AddJsonBody(new
                {
                    identifiers = chunk.Select(name => new { name }).ToArray()
                });

            var response = await _executeCollectionAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
            {
                throw new HttpRequestException(
                    $"{deckLabel} Scryfall card reference lookup failed while building the comparison packet with HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            resolvedCards.AddRange(response.Data.Data);
        }

        return resolvedCards;
    }

    private static DeckComparisonDeckSummary BuildDeckSummary(
        string deckName,
        string commanderName,
        CommanderBracketOption bracket,
        IReadOnlyList<DeckEntry> entries,
        IReadOnlyList<ScryfallCard> cards,
        CommanderSpellbookResult? comboResult)
    {
        var cardLookup = cards.ToDictionary(card => card.Name, StringComparer.OrdinalIgnoreCase);
        var mainboardEntries = entries
            .Where(entry => !string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var commanderEntries = entries
            .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var totalMainboardCards = mainboardEntries.Sum(entry => entry.Quantity);
        var categories = CategoryCountReporter.CountByQuantity(mainboardEntries)
            .Take(8)
            .Select(item => $"{item.Category}: {item.Count}")
            .ToList();

        var curveBuckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["0-1"] = 0,
            ["2"] = 0,
            ["3"] = 0,
            ["4"] = 0,
            ["5+"] = 0
        };

        var nonlandCardCount = 0;
        var manaValueTotal = 0m;
        var colorIdentity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lands = 0;
        var creatures = 0;
        var ramp = 0;
        var draw = 0;
        var interaction = 0;
        var wipes = 0;
        var recursion = 0;
        var closingPower = 0;
        var includedCombos = comboResult?.IncludedCombos ?? Array.Empty<SpellbookCombo>();
        var almostIncludedCombos = comboResult?.AlmostIncludedCombos ?? Array.Empty<SpellbookAlmostCombo>();

        foreach (var commanderEntry in commanderEntries)
        {
            if (cardLookup.TryGetValue(commanderEntry.Name, out var commanderCard))
            {
                foreach (var color in commanderCard.ColorIdentity ?? Array.Empty<string>())
                {
                    colorIdentity.Add(color);
                }
            }
        }

        foreach (var entry in mainboardEntries)
        {
            if (!cardLookup.TryGetValue(entry.Name, out var card))
            {
                continue;
            }

            var typeLine = card.TypeLine ?? string.Empty;
            var oracleText = NormalizeOracleText(card);
            var quantity = entry.Quantity;
            var manaValue = EstimateManaValue(card.ManaCost);

            foreach (var color in card.ColorIdentity ?? Array.Empty<string>())
            {
                colorIdentity.Add(color);
            }

            if (typeLine.Contains("Land", StringComparison.OrdinalIgnoreCase))
            {
                lands += quantity;
                curveBuckets["0-1"] += quantity;
                continue;
            }

            nonlandCardCount += quantity;
            manaValueTotal += manaValue * quantity;

            if (manaValue <= 1)
            {
                curveBuckets["0-1"] += quantity;
            }
            else if (manaValue == 2)
            {
                curveBuckets["2"] += quantity;
            }
            else if (manaValue == 3)
            {
                curveBuckets["3"] += quantity;
            }
            else if (manaValue == 4)
            {
                curveBuckets["4"] += quantity;
            }
            else
            {
                curveBuckets["5+"] += quantity;
            }

            if (typeLine.Contains("Creature", StringComparison.OrdinalIgnoreCase))
            {
                creatures += quantity;
            }

            if (IsRampCard(typeLine, oracleText))
            {
                ramp += quantity;
            }

            if (IsDrawCard(oracleText))
            {
                draw += quantity;
            }

            if (IsInteractionCard(typeLine, oracleText))
            {
                interaction += quantity;
            }

            if (IsBoardWipeCard(oracleText))
            {
                wipes += quantity;
            }

            if (IsRecursionCard(oracleText))
            {
                recursion += quantity;
            }

            if (IsClosingPowerCard(typeLine, oracleText))
            {
                closingPower += quantity;
            }
        }

        closingPower += includedCombos.Count * 2 + almostIncludedCombos.Count;
        IReadOnlyList<string> sharedThemes = categories.Count == 0 ? Array.Empty<string>() : categories;
        var averageManaValue = nonlandCardCount == 0 ? 0m : Math.Round(manaValueTotal / nonlandCardCount, 2);
        var comboSummaries = includedCombos
            .Select(combo => $"{string.Join(" + ", combo.CardNames)} -> {string.Join(", ", combo.Results)}")
            .Take(5)
            .ToList();
        var almostComboSummaries = almostIncludedCombos
            .Select(combo => $"{combo.MissingCard} missing from {string.Join(" + ", combo.CardsInDeck)}")
            .Take(5)
            .ToList();

        return new DeckComparisonDeckSummary(
            deckName,
            commanderName,
            bracket,
            totalMainboardCards,
            lands,
            creatures,
            averageManaValue,
            curveBuckets,
            colorIdentity.OrderBy(color => color, StringComparer.OrdinalIgnoreCase).ToList(),
            categories,
            ramp,
            draw,
            interaction,
            wipes,
            recursion,
            closingPower,
            sharedThemes,
            comboSummaries,
            almostComboSummaries,
            includedCombos.Count,
            almostIncludedCombos.Count);
    }

    private static string BuildInputSummary(DeckComparisonDeckSummary deckA, DeckComparisonDeckSummary deckB)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{deckA.Name}: commander {FallbackText(deckA.CommanderName, "Unknown")} | bracket {deckA.Bracket.Label} | mainboard {deckA.MainboardCount} | lands {deckA.Lands} | ramp {deckA.Ramp} | draw {deckA.Draw} | interaction {deckA.Interaction} | combos {deckA.IncludedComboCount}");
        builder.AppendLine($"{deckB.Name}: commander {FallbackText(deckB.CommanderName, "Unknown")} | bracket {deckB.Bracket.Label} | mainboard {deckB.MainboardCount} | lands {deckB.Lands} | ramp {deckB.Ramp} | draw {deckB.Draw} | interaction {deckB.Interaction} | combos {deckB.IncludedComboCount}");
        return builder.ToString().TrimEnd();
    }

    private static string BuildComparisonContextText(DeckComparisonDeckSummary deckA, DeckComparisonDeckSummary deckB)
    {
        var sharedThemeNames = deckA.CategorySummaries
            .Select(item => item.Split(':', 2)[0].Trim())
            .Intersect(deckB.CategorySummaries.Select(item => item.Split(':', 2)[0].Trim()), StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("comparison_context:");
        builder.AppendLine($"generated_at_utc: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        builder.AppendLine();
        builder.AppendLine("commander_bracket_definitions:");
        foreach (var option in CommanderBracketCatalog.Options)
        {
            builder.AppendLine($"- {option.Label}: {option.Summary} {option.TurnsExpectation}");
        }
        builder.AppendLine();
        AppendDeckContext(builder, "deck_a", deckA);
        builder.AppendLine();
        AppendDeckContext(builder, "deck_b", deckB);
        builder.AppendLine();
        builder.AppendLine("comparison_signals:");
        builder.AppendLine($"shared_categories: {(sharedThemeNames.Count == 0 ? "(none)" : string.Join(", ", sharedThemeNames))}");
        builder.AppendLine($"ramp_gap: {deckA.Name} {deckA.Ramp} vs {deckB.Name} {deckB.Ramp}");
        builder.AppendLine($"draw_gap: {deckA.Name} {deckA.Draw} vs {deckB.Name} {deckB.Draw}");
        builder.AppendLine($"interaction_gap: {deckA.Name} {deckA.Interaction} vs {deckB.Name} {deckB.Interaction}");
        builder.AppendLine($"wipe_gap: {deckA.Name} {deckA.Wipes} vs {deckB.Name} {deckB.Wipes}");
        builder.AppendLine($"recursion_gap: {deckA.Name} {deckA.Recursion} vs {deckB.Name} {deckB.Recursion}");
        builder.AppendLine($"closing_power_gap: {deckA.Name} {deckA.ClosingPower} vs {deckB.Name} {deckB.ClosingPower}");
        builder.AppendLine($"combo_gap: {deckA.Name} {deckA.IncludedComboCount} complete combos vs {deckB.Name} {deckB.IncludedComboCount} complete combos");
        builder.AppendLine($"average_mana_value_gap: {deckA.Name} {deckA.AverageManaValue:0.00} vs {deckB.Name} {deckB.AverageManaValue:0.00}");
        return builder.ToString().TrimEnd();
    }

    private static void AppendDeckContext(StringBuilder builder, string label, DeckComparisonDeckSummary deck)
    {
        builder.AppendLine($"{label}:");
        builder.AppendLine($"  name: {deck.Name}");
        builder.AppendLine($"  commander: {FallbackText(deck.CommanderName, "Unknown")}");
        builder.AppendLine($"  bracket: {deck.Bracket.Label}");
        builder.AppendLine($"  bracket_summary: {deck.Bracket.Summary}");
        builder.AppendLine($"  bracket_turn_expectation: {deck.Bracket.TurnsExpectation}");
        builder.AppendLine($"  mainboard_cards: {deck.MainboardCount}");
        builder.AppendLine($"  lands: {deck.Lands}");
        builder.AppendLine($"  creatures: {deck.Creatures}");
        builder.AppendLine($"  average_mana_value: {deck.AverageManaValue:0.00}");
        builder.AppendLine($"  mana_curve: {string.Join(", ", deck.ManaCurve.Select(item => $"{item.Key}={item.Value}"))}");
        builder.AppendLine($"  color_identity: {(deck.ColorIdentity.Count == 0 ? "(unknown)" : string.Join(", ", deck.ColorIdentity))}");
        builder.AppendLine($"  categories: {(deck.CategorySummaries.Count == 0 ? "(none detected)" : string.Join(" | ", deck.CategorySummaries))}");
        builder.AppendLine($"  role_counts: ramp={deck.Ramp}, draw={deck.Draw}, interaction={deck.Interaction}, wipes={deck.Wipes}, recursion={deck.Recursion}, closing_power={deck.ClosingPower}");
        builder.AppendLine($"  combos_included: {deck.IncludedComboCount}");
        builder.AppendLine($"  combos_almost_included: {deck.AlmostIncludedComboCount}");
        builder.AppendLine($"  key_combos: {(deck.ComboSummaries.Count == 0 ? "(none found)" : string.Join(" | ", deck.ComboSummaries))}");
        builder.AppendLine($"  almost_combos: {(deck.AlmostComboSummaries.Count == 0 ? "(none found)" : string.Join(" | ", deck.AlmostComboSummaries))}");
    }

    private static string BuildComparisonPrompt(
        DeckComparisonDeckSummary deckA,
        DeckComparisonDeckSummary deckB,
        string deckAListText,
        string deckBListText,
        string deckAComboText,
        string deckBComboText,
        string comparisonContextText,
        string comparisonSchemaJson)
    {
        var builder = new StringBuilder();
        builder.AppendLine("### Task");
        builder.AppendLine("Based only on the provided deck contents and supplied context, compare the decks in a typical multiplayer Commander environment.");
        builder.AppendLine("Provide a grounded, evidence-based comparison instead of a speculative matchup prediction.");
        builder.AppendLine();
        builder.AppendLine("### Rules");
        builder.AppendLine("- Treat the supplied decklists, commander names, bracket selections, combo findings, and derived comparison context as the source of truth.");
        builder.AppendLine("- Do not invent cards, colors, commander identities, or card text not supported by the provided context.");
        builder.AppendLine("- Do not assume a card's role unless it is supported by the deck contents or provided context.");
        builder.AppendLine("- Do not claim exact card text unless it is included in the packet.");
        builder.AppendLine("- If a conclusion is not well-supported by the provided deck contents, say that explicitly instead of guessing.");
        builder.AppendLine("- When uncertain, mark the statement as low-confidence and add the reason to confidence_notes.");
        builder.AppendLine("- For each major conclusion, reference the deck patterns, card packages, or commander incentives that support it.");
        builder.AppendLine("- Base conclusions on observable deck construction rather than vague impressions.");
        builder.AppendLine("- Do not make claims about exact metagames unless explicitly provided.");
        builder.AppendLine();
        builder.AppendLine("### Comparison Axes");
        builder.AppendLine($"- Commander role and game plan for {deckA.Name}");
        builder.AppendLine($"- Commander role and game plan for {deckB.Name}");
        builder.AppendLine("- Speed and setup tempo");
        builder.AppendLine("- Ramp");
        builder.AppendLine("- Draw");
        builder.AppendLine("- Spot interaction");
        builder.AppendLine("- Sweepers");
        builder.AppendLine("- Recursion");
        builder.AppendLine("- Closing power, including complete combos and near-combos as part of the win-condition comparison");
        builder.AppendLine("- Resilience");
        builder.AppendLine("- Consistency");
        builder.AppendLine("- Mana stability");
        builder.AppendLine("- Dependence on commander");
        builder.AppendLine("- Likely table fit");
        builder.AppendLine("- Major overlap and major differences");
        builder.AppendLine("- Five concrete cards or packages that best explain the gap");
        builder.AppendLine();
        builder.AppendLine("### Output Format");
        builder.AppendLine("- First return a readable comparison section for humans.");
        builder.AppendLine("- Include a concise summary, a side-by-side comparison structure, and a final verdict.");
        builder.AppendLine("- Then return a fenced ```json block whose top-level object is named deck_comparison.");
        builder.AppendLine("- Return valid JSON only inside the fenced block.");
        builder.AppendLine("- Do not include comments in the JSON.");
        builder.AppendLine("- Do not omit required fields.");
        builder.AppendLine("- Use arrays instead of prose where appropriate.");
        builder.AppendLine("- The JSON must match this schema exactly:");
        builder.AppendLine();
        builder.AppendLine("```json");
        builder.AppendLine("{");
        builder.AppendLine("  \"deck_comparison\": " + IndentJson(comparisonSchemaJson, 2));
        builder.AppendLine("}");
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("### Deck A");
        AppendPromptDeckSection(builder, deckA, deckAListText, deckAComboText);
        builder.AppendLine();
        builder.AppendLine("### Deck B");
        AppendPromptDeckSection(builder, deckB, deckBListText, deckBComboText);
        builder.AppendLine();
        builder.AppendLine("### Comparison Context");
        builder.AppendLine(comparisonContextText);
        return builder.ToString().TrimEnd();
    }

    private static void AppendPromptDeckSection(
        StringBuilder builder,
        DeckComparisonDeckSummary deck,
        string deckListText,
        string comboText)
    {
        builder.AppendLine($"Name: {deck.Name}");
        builder.AppendLine($"Commander: {FallbackText(deck.CommanderName, "Unknown")}");
        builder.AppendLine($"Bracket: {deck.Bracket.Label}");
        builder.AppendLine($"Bracket summary: {deck.Bracket.Summary}");
        builder.AppendLine($"Bracket turn expectation: {deck.Bracket.TurnsExpectation}");
        builder.AppendLine("Normalized decklist:");
        builder.AppendLine(deckListText);
        builder.AppendLine();
        builder.AppendLine("Combo summary:");
        builder.AppendLine(comboText);
    }

    private static string BuildFollowUpPrompt(string comparisonSchemaJson)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Use this follow-up prompt after the initial comparison when the user asks extra questions.");
        builder.AppendLine();
        builder.AppendLine("### Task");
        builder.AppendLine("Revise the existing deck comparison using the follow-up questions and answers in this chat.");
        builder.AppendLine();
        builder.AppendLine("### Rules");
        builder.AppendLine("- Preserve the original comparison structure.");
        builder.AppendLine("- Incorporate the new follow-up Q&A without contradicting the supplied deck contents or packet context.");
        builder.AppendLine("- Keep using the decklists and packet context as the source of truth.");
        builder.AppendLine("- If a new conclusion is uncertain, mark it as low-confidence and explain why in confidence_notes.");
        builder.AppendLine();
        builder.AppendLine("### Output Format");
        builder.AppendLine("- Return the updated readable comparison.");
        builder.AppendLine("- Then regenerate the full fenced ```json block with the top-level object named deck_comparison.");
        builder.AppendLine("- Keep the JSON valid and include every required field from this schema:");
        builder.AppendLine();
        builder.AppendLine("```json");
        builder.AppendLine("{");
        builder.AppendLine("  \"deck_comparison\": " + IndentJson(comparisonSchemaJson, 2));
        builder.AppendLine("}");
        builder.AppendLine("```");
        return builder.ToString().TrimEnd();
    }

    private static string BuildComparisonSchemaJson(string deckAName, string deckBName, string deckACommander, string deckBCommander, string deckABracket, string deckBBracket)
    {
        var payload = new
        {
            deck_a_name = deckAName,
            deck_b_name = deckBName,
            deck_a_commander = deckACommander,
            deck_b_commander = deckBCommander,
            deck_a_gameplan = string.Empty,
            deck_b_gameplan = string.Empty,
            deck_a_bracket = deckABracket,
            deck_b_bracket = deckBBracket,
            shared_themes = Array.Empty<string>(),
            major_differences = Array.Empty<string>(),
            deck_a_strengths = Array.Empty<string>(),
            deck_b_strengths = Array.Empty<string>(),
            deck_a_weaknesses = Array.Empty<string>(),
            deck_b_weaknesses = Array.Empty<string>(),
            speed_comparison = string.Empty,
            resilience_comparison = string.Empty,
            interaction_comparison = string.Empty,
            mana_consistency_comparison = string.Empty,
            closing_power_comparison = string.Empty,
            combo_comparison = string.Empty,
            overall_verdict = string.Empty,
            key_gap_cards_or_packages = Array.Empty<string>(),
            deck_a_key_combos = Array.Empty<string>(),
            deck_b_key_combos = Array.Empty<string>(),
            recommended_for = new
            {
                deck_a = Array.Empty<string>(),
                deck_b = Array.Empty<string>()
            },
            confidence_notes = Array.Empty<string>()
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static ChatGptDeckComparisonResponse ParseComparisonResponse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("Paste the deck_comparison JSON returned from ChatGPT into Step 3.");
        }

        var json = ChatGptJsonTextFormatterService.ExtractJsonPayload(input);
        using var document = JsonDocument.Parse(json);

        JsonElement payload = document.RootElement;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("deck_comparison", out var comparisonElement))
        {
            payload = comparisonElement;
        }

        var result = JsonSerializer.Deserialize<ChatGptDeckComparisonResponse>(payload.GetRawText(), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (result is null)
        {
            throw new InvalidOperationException("The submitted ChatGPT response did not contain a valid deck_comparison payload.");
        }

        return result;
    }

    private async Task<string> SaveArtifactsAsync(
        ChatGptDeckComparisonRequest request,
        DeckComparisonDeckSummary deckA,
        DeckComparisonDeckSummary deckB,
        string inputSummary,
        string deckAListText,
        string deckBListText,
        string comparisonContextText,
        string comparisonPromptText,
        string followUpPromptText,
        string comparisonSchemaJson,
        CancellationToken cancellationToken)
    {
        var timestampSegment = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outputDirectory = Path.Combine(
            _artifactsPath,
            CreateSafePathSegment($"{deckA.Name}-vs-{deckB.Name}", "deck-comparison"),
            timestampSegment);
        Directory.CreateDirectory(outputDirectory);

        var files = new (string FileName, string? Content)[]
        {
            ("00-comparison-input-summary.txt", inputSummary),
            ("10-deck-a-list.txt", deckAListText),
            ("11-deck-b-list.txt", deckBListText),
            ("12-deck-a-combos.txt", BuildComboArtifactText(deckA)),
            ("13-deck-b-combos.txt", BuildComboArtifactText(deckB)),
            ("20-comparison-context.txt", comparisonContextText),
            ("30-comparison-prompt.txt", comparisonPromptText),
            ("31-comparison-schema.json", comparisonSchemaJson),
            ("32-comparison-follow-up-prompt.txt", followUpPromptText),
            ("40-deck-comparison-response.json", string.IsNullOrWhiteSpace(request.ComparisonResponseJson) ? null : ChatGptJsonTextFormatterService.ExtractJsonPayload(request.ComparisonResponseJson))
        };

        foreach (var file in files.Where(file => !string.IsNullOrWhiteSpace(file.Content)))
        {
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, file.FileName),
                file.Content!.Trim() + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }

        return outputDirectory;
    }

    private static string BuildTimingSummary(IReadOnlyList<(string Label, long Ms, string? Detail)> timings, long totalMs)
    {
        var builder = new StringBuilder();
        foreach (var timing in timings)
        {
            builder.Append("- ");
            builder.Append(timing.Label);
            builder.Append(": ");
            builder.Append(timing.Ms);
            builder.Append(" ms");
            if (!string.IsNullOrWhiteSpace(timing.Detail))
            {
                builder.Append(" (");
                builder.Append(timing.Detail);
                builder.Append(')');
            }

            builder.AppendLine();
        }

        builder.Append("Total: ");
        builder.Append(totalMs);
        builder.Append(" ms");
        return builder.ToString();
    }

    private static string ResolveDeckName(string requestedName, string commanderName, string fallback)
        => string.IsNullOrWhiteSpace(requestedName)
            ? FallbackText(commanderName, fallback)
            : requestedName.Trim();

    private static string FallbackText(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string BuildDecklistText(IReadOnlyList<DeckEntry> entries, IReadOnlyList<DeckEntry> optionalEntries)
    {
        var builder = new StringBuilder();
        var commanderLines = entries
            .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Quantity} {entry.Name}")
            .ToList();

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
        foreach (var line in entries
                     .Where(entry => !string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => $"{entry.Quantity} {entry.Name}"))
        {
            builder.AppendLine(line);
        }

        if (optionalEntries.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Possible Includes");
            foreach (var line in optionalEntries
                         .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(entry => $"{entry.Quantity} {entry.Name}"))
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString().TrimEnd();
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
            if (!string.IsNullOrWhiteSpace(face.OracleText))
            {
                parts.Add(CollapseWhitespace(face.OracleText));
            }
        }

        return string.Join(" ", parts);
    }

    private static string CollapseWhitespace(string value)
        => string.Join(" ", value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static int EstimateManaValue(string? manaCost)
    {
        if (string.IsNullOrWhiteSpace(manaCost))
        {
            return 0;
        }

        var total = 0;
        var tokenBuilder = new StringBuilder();
        var insideToken = false;

        foreach (var character in manaCost)
        {
            if (character == '{')
            {
                insideToken = true;
                tokenBuilder.Clear();
                continue;
            }

            if (character == '}')
            {
                if (insideToken)
                {
                    total += ParseManaToken(tokenBuilder.ToString());
                }

                insideToken = false;
                continue;
            }

            if (insideToken)
            {
                tokenBuilder.Append(character);
            }
        }

        return total;
    }

    private static int ParseManaToken(string token)
    {
        if (int.TryParse(token, out var numeric))
        {
            return numeric;
        }

        if (token.Contains('/', StringComparison.Ordinal))
        {
            return 1;
        }

        return token.Equals("X", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    private static bool IsRampCard(string typeLine, string oracleText)
        => typeLine.Contains("Land", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("add one mana", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("add two mana", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("search your library for a basic land", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("search your library for up to", StringComparison.OrdinalIgnoreCase) && oracleText.Contains("land", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("Treasure token", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("create a Treasure", StringComparison.OrdinalIgnoreCase);

    private static bool IsDrawCard(string oracleText)
        => oracleText.Contains("draw a card", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("draw two cards", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("draw X cards", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("investigate", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("connive", StringComparison.OrdinalIgnoreCase);

    private static bool IsInteractionCard(string typeLine, string oracleText)
        => typeLine.Contains("Instant", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("destroy target", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("exile target", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("counter target", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("return target spell", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("fight target", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoardWipeCard(string oracleText)
        => oracleText.Contains("destroy all creatures", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("destroy all artifacts", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("destroy all enchantments", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("each creature", StringComparison.OrdinalIgnoreCase) && oracleText.Contains("gets -", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("exile all", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecursionCard(string oracleText)
        => oracleText.Contains("return target card from your graveyard", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("return all land cards from your graveyard", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("return target permanent card from your graveyard", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("reanimate", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("from your graveyard to your hand", StringComparison.OrdinalIgnoreCase);

    private static bool IsClosingPowerCard(string typeLine, string oracleText)
        => oracleText.Contains("each opponent loses", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("you win the game", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("extra turn", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("double strike", StringComparison.OrdinalIgnoreCase)
            || typeLine.Contains("Craterhoof", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("combat damage to a player", StringComparison.OrdinalIgnoreCase) && oracleText.Contains("draw", StringComparison.OrdinalIgnoreCase)
            || oracleText.Contains("whenever this creature attacks", StringComparison.OrdinalIgnoreCase) && oracleText.Contains("+X/+X", StringComparison.OrdinalIgnoreCase);

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

    private static string CreateSafePathSegment(string value, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(candidate.Select(character => invalidChars.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized.Replace(' ', '-').ToLowerInvariant();
    }

    private static string IndentJson(string json, int indentSize)
    {
        var indent = new string(' ', indentSize);
        var lines = json.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => indent + line));
    }

    private sealed record LoadedDeck(
        IReadOnlyList<DeckEntry> AllEntries,
        IReadOnlyList<DeckEntry> PlayableEntries,
        IReadOnlyList<DeckEntry> OptionalEntries,
        string CommanderName);

    private sealed record DeckComparisonDeckSummary(
        string Name,
        string CommanderName,
        CommanderBracketOption Bracket,
        int MainboardCount,
        int Lands,
        int Creatures,
        decimal AverageManaValue,
        IReadOnlyDictionary<string, int> ManaCurve,
        IReadOnlyList<string> ColorIdentity,
        IReadOnlyList<string> CategorySummaries,
        int Ramp,
        int Draw,
        int Interaction,
        int Wipes,
        int Recursion,
        int ClosingPower,
        IReadOnlyList<string> SharedThemes,
        IReadOnlyList<string> ComboSummaries,
        IReadOnlyList<string> AlmostComboSummaries,
        int IncludedComboCount,
        int AlmostIncludedComboCount);

    private static string BuildComboArtifactText(DeckComparisonDeckSummary deck)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{deck.Name} combos");
        builder.AppendLine($"Commander bracket: {deck.Bracket.Label}");
        builder.AppendLine($"Complete combos: {deck.IncludedComboCount}");
        builder.AppendLine($"Near-combos: {deck.AlmostIncludedComboCount}");
        builder.AppendLine();
        builder.AppendLine("Key combos:");
        if (deck.ComboSummaries.Count == 0)
        {
            builder.AppendLine("(none found)");
        }
        else
        {
            foreach (var combo in deck.ComboSummaries)
            {
                builder.AppendLine($"- {combo}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Near-combos:");
        if (deck.AlmostComboSummaries.Count == 0)
        {
            builder.AppendLine("(none found)");
        }
        else
        {
            foreach (var combo in deck.AlmostComboSummaries)
            {
                builder.AppendLine($"- {combo}");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
