using System.Text;
using System.Text.Json;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Core.Parsing;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services.Http;
using Polly;
using Polly.Registry;
using RestSharp;
using System.Net;

namespace DeckFlow.Web.Services;

public interface IChatGptCedhMetaGapService
{
    Task<ChatGptCedhMetaGapResult> BuildAsync(ChatGptCedhMetaGapRequest request, CancellationToken cancellationToken = default);
}

public sealed record ChatGptCedhMetaGapResult(
    string? InputSummary,
    string? ResolvedCommanderName,
    IReadOnlyList<EdhTop16Entry> FetchedEntries,
    string? PromptText,
    string? SchemaJson,
    ChatGptCedhMetaGapResponse? AnalysisResponse,
    string? SavedArtifactsDirectory);

public sealed class ChatGptCedhMetaGapService : IChatGptCedhMetaGapService
{
    private const int FetchCount = 48;
    private const int ScryfallBatchSize = 75;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly string MetaGapSchemaJson = BuildSchemaJson();

    private readonly IMoxfieldDeckImporter _moxfieldDeckImporter;
    private readonly IArchidektDeckImporter _archidektDeckImporter;
    private readonly MoxfieldParser _moxfieldParser;
    private readonly ArchidektParser _archidektParser;
    private readonly IEdhTop16Client _edhTop16Client;
    private readonly ICommanderSpellbookService _commanderSpellbookService;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> _executeCollectionAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>> _executeSearchAsync;
    private readonly string _artifactsPath;

    private ChatGptCedhMetaGapService(
        IScryfallRestClientFactory scryfallRestClientFactory,
        ResiliencePipeline<RestResponse> scryfallPipeline,
        IMoxfieldDeckImporter moxfieldDeckImporter,
        IArchidektDeckImporter archidektDeckImporter,
        MoxfieldParser moxfieldParser,
        ArchidektParser archidektParser,
        IEdhTop16Client edhTop16Client,
        ICommanderSpellbookService commanderSpellbookService,
        RestClient? restClientOverride,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsyncOverride,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeSearchAsyncOverride)
    {
        ArgumentNullException.ThrowIfNull(scryfallRestClientFactory);
        ArgumentNullException.ThrowIfNull(moxfieldDeckImporter);
        ArgumentNullException.ThrowIfNull(archidektDeckImporter);
        ArgumentNullException.ThrowIfNull(moxfieldParser);
        ArgumentNullException.ThrowIfNull(archidektParser);
        ArgumentNullException.ThrowIfNull(edhTop16Client);
        ArgumentNullException.ThrowIfNull(commanderSpellbookService);
        var pipeline = scryfallPipeline ?? ResiliencePipeline<RestResponse>.Empty;
        _moxfieldDeckImporter = moxfieldDeckImporter;
        _archidektDeckImporter = archidektDeckImporter;
        _moxfieldParser = moxfieldParser;
        _archidektParser = archidektParser;
        _edhTop16Client = edhTop16Client;
        _commanderSpellbookService = commanderSpellbookService;
        var client = restClientOverride ?? scryfallRestClientFactory.Create();
        _executeCollectionAsync = executeCollectionAsyncOverride ?? ((request, cancellationToken) =>
            ScryfallThrottle.ExecuteAsync(
                token => pipeline.ExecuteAsync(
                    async pollyCt => await client.ExecuteAsync<ScryfallCollectionResponse>(request, pollyCt).ConfigureAwait(false),
                    token).AsTask(),
                cancellationToken));
        _executeSearchAsync = executeSearchAsyncOverride ?? ((request, cancellationToken) =>
            ScryfallThrottle.ExecuteAsync(
                token => pipeline.ExecuteAsync(
                    async pollyCt => await client.ExecuteAsync<ScryfallSearchResponse>(request, pollyCt).ConfigureAwait(false),
                    token).AsTask(),
                cancellationToken));
        _artifactsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DeckFlow",
            "ChatGPT cEDH Meta Gap");
    }

    public ChatGptCedhMetaGapService(
        IScryfallRestClientFactory scryfallRestClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        IMoxfieldDeckImporter moxfieldDeckImporter,
        IArchidektDeckImporter archidektDeckImporter,
        MoxfieldParser moxfieldParser,
        ArchidektParser archidektParser,
        IEdhTop16Client edhTop16Client,
        ICommanderSpellbookService commanderSpellbookService)
        : this(
            scryfallRestClientFactory,
            pipelineProvider?.GetPipeline<RestResponse>("scryfall") ?? ResiliencePipeline<RestResponse>.Empty,
            moxfieldDeckImporter,
            archidektDeckImporter,
            moxfieldParser,
            archidektParser,
            edhTop16Client,
            commanderSpellbookService,
            null,
            null,
            null)
    {
        ArgumentNullException.ThrowIfNull(pipelineProvider);
    }

    internal ChatGptCedhMetaGapService(
        IMoxfieldDeckImporter moxfieldDeckImporter,
        IArchidektDeckImporter archidektDeckImporter,
        MoxfieldParser moxfieldParser,
        ArchidektParser archidektParser,
        IEdhTop16Client edhTop16Client,
        ICommanderSpellbookService commanderSpellbookService,
        RestClient? scryfallRestClient = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>>? executeCollectionAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeSearchAsync = null)
        : this(
            NullScryfallRestClientFactory.Instance,
            ResiliencePipeline<RestResponse>.Empty,
            moxfieldDeckImporter,
            archidektDeckImporter,
            moxfieldParser,
            archidektParser,
            edhTop16Client,
            commanderSpellbookService,
            scryfallRestClient,
            executeCollectionAsync,
            executeSearchAsync)
    {
    }

    public async Task<ChatGptCedhMetaGapResult> BuildAsync(ChatGptCedhMetaGapRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ChatGptCedhMetaGapResponse? analysisResponse = null;
        if (request.WorkflowStep >= 3 && !string.IsNullOrWhiteSpace(request.MetaGapResponseJson))
        {
            analysisResponse = ParseResponse(request.MetaGapResponseJson);
            if (string.IsNullOrWhiteSpace(request.DeckSource) && string.IsNullOrWhiteSpace(request.CommanderName))
            {
                return new ChatGptCedhMetaGapResult(null, null, Array.Empty<EdhTop16Entry>(), null, MetaGapSchemaJson, analysisResponse, null);
            }
        }

        if (string.IsNullOrWhiteSpace(request.DeckSource))
        {
            throw new InvalidOperationException("Paste your deck URL or deck text before fetching EDH Top 16 reference decks.");
        }

        var loadedDeck = await LoadDeckAsync(request.DeckSource, cancellationToken).ConfigureAwait(false);
        var resolvedCommanderName = string.IsNullOrWhiteSpace(request.CommanderName)
            ? loadedDeck.CommanderName
            : request.CommanderName.Trim();

        if (string.IsNullOrWhiteSpace(resolvedCommanderName))
        {
            throw new InvalidOperationException("Could not determine a commander from the submitted deck. Enter the commander name explicitly and try again.");
        }

        var fetchedEntries = (await _edhTop16Client.SearchCommanderEntriesAsync(
            resolvedCommanderName,
            request.TimePeriod,
            request.SortBy,
            request.MinEventSize,
            request.MaxStanding,
            FetchCount,
            cancellationToken).ConfigureAwait(false))
            .OrderByDescending(entry => entry.TournamentDate ?? DateOnly.MinValue)
            .ThenBy(entry => entry.Standing)
            .ThenBy(entry => entry.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fetchedEntries.Count == 0)
        {
            throw new InvalidOperationException(
                $"No EDH Top 16 decks matched your filters for {resolvedCommanderName}. Try a longer time period, a smaller minimum event size, or a looser finish cutoff.");
        }

        var inputSummary = BuildInputSummary(loadedDeck, resolvedCommanderName, request, fetchedEntries);
        var schemaJson = MetaGapSchemaJson;
        string? promptText = null;
        if (request.WorkflowStep >= 2)
        {
            var selectedEntries = ResolveSelectedEntries(request.SelectedReferenceIndexes, fetchedEntries);
            var oracleNameMap = await ResolveOracleNameMapAsync(loadedDeck.PlayableEntries, selectedEntries, cancellationToken).ConfigureAwait(false);
            var normalizedMyDeckEntries = NormalizeDeckEntriesForPromptAndCombos(loadedDeck.PlayableEntries, oracleNameMap);
            var normalizedReferenceDecks = selectedEntries
                .Select(entry => BuildReferenceDeckEntries(resolvedCommanderName, entry, oracleNameMap))
                .ToList();

            var myDeckComboTask = _commanderSpellbookService.FindCombosAsync(normalizedMyDeckEntries, cancellationToken);
            var referenceComboTasks = selectedEntries
                .Select((entry, index) => _commanderSpellbookService.FindCombosAsync(
                    normalizedReferenceDecks[index],
                    cancellationToken))
                .ToList();

            await Task.WhenAll(referenceComboTasks.Prepend(myDeckComboTask)).ConfigureAwait(false);

            promptText = BuildPrompt(
                resolvedCommanderName,
                normalizedMyDeckEntries,
                myDeckComboTask.Result,
                selectedEntries,
                referenceComboTasks.Select(task => task.Result).ToList(),
                oracleNameMap,
                schemaJson);
        }

        string? savedArtifactsDirectory = null;
        if (request.SaveArtifactsToDisk && (!string.IsNullOrWhiteSpace(promptText) || analysisResponse is not null))
        {
            savedArtifactsDirectory = await SaveArtifactsAsync(
                resolvedCommanderName,
                inputSummary,
                promptText,
                schemaJson,
                request.MetaGapResponseJson,
                cancellationToken).ConfigureAwait(false);
        }

        return new ChatGptCedhMetaGapResult(
            inputSummary,
            resolvedCommanderName,
            fetchedEntries,
            promptText,
            schemaJson,
            analysisResponse,
            savedArtifactsDirectory);
    }

    private static IReadOnlyList<EdhTop16Entry> ResolveSelectedEntries(IReadOnlyList<int> selectedIndexes, IReadOnlyList<EdhTop16Entry> fetchedEntries)
    {
        var distinctIndexes = selectedIndexes
            .Distinct()
            .Where(index => index >= 0 && index < fetchedEntries.Count)
            .ToList();

        if (distinctIndexes.Count == 0)
        {
            throw new InvalidOperationException("Select at least 1 EDH Top 16 reference deck before generating the prompt.");
        }

        if (distinctIndexes.Count > 3)
        {
            throw new InvalidOperationException("Select no more than 3 EDH Top 16 reference decks before generating the prompt.");
        }

        return distinctIndexes.Select(index => fetchedEntries[index]).ToList();
    }

    private async Task<LoadedDeck> LoadDeckAsync(string deckSource, CancellationToken cancellationToken)
    {
        List<DeckEntry> entries;
        try
        {
            entries = await LoadDeckEntriesAsync(deckSource, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DeckParseException or HttpRequestException)
        {
            throw new InvalidOperationException($"Deck parse failed: {exception.Message}", exception);
        }

        var playableEntries = entries
            .Where(entry =>
                !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (playableEntries.Count == 0)
        {
            throw new InvalidOperationException("Deck parse failed: the submitted deck did not contain any commander or mainboard cards.");
        }

        var commanderName = playableEntries
            .FirstOrDefault(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            ?.Name;

        if (string.IsNullOrWhiteSpace(commanderName))
        {
            commanderName = playableEntries
                .Where(entry => entry.Quantity == 1)
                .Select(entry => entry.Name)
                .FirstOrDefault();
        }

        return new LoadedDeck(playableEntries, commanderName ?? string.Empty);
    }

    private async Task<List<DeckEntry>> LoadDeckEntriesAsync(string deckSource, CancellationToken cancellationToken)
    {
        var normalizedDeckSource = deckSource.Trim();
        if (Uri.TryCreate(normalizedDeckSource, UriKind.Absolute, out var uri))
        {
            if (uri.Host.Contains("moxfield.com", StringComparison.OrdinalIgnoreCase))
            {
                return await _moxfieldDeckImporter.ImportAsync(normalizedDeckSource, cancellationToken).ConfigureAwait(false);
            }

            if (uri.Host.Contains("archidekt.com", StringComparison.OrdinalIgnoreCase))
            {
                return await _archidektDeckImporter.ImportAsync(normalizedDeckSource, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            return _moxfieldParser.ParseText(normalizedDeckSource);
        }
        catch (DeckParseException)
        {
            return _archidektParser.ParseText(normalizedDeckSource);
        }
    }

    private static string BuildInputSummary(
        LoadedDeck loadedDeck,
        string resolvedCommanderName,
        ChatGptCedhMetaGapRequest request,
        IReadOnlyList<EdhTop16Entry> fetchedEntries)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Commander: {resolvedCommanderName}");
        builder.AppendLine($"Submitted cards: {loadedDeck.PlayableEntries.Sum(entry => entry.Quantity)}");
        builder.AppendLine($"Time period: {request.TimePeriod}");
        builder.AppendLine($"Sort by: {request.SortBy}");
        builder.AppendLine($"Minimum event size: {(request.MinEventSize > 0 ? request.MinEventSize : "All")}");
        builder.AppendLine($"Maximum standing: {(request.MaxStanding.HasValue ? request.MaxStanding.Value : "All")}");
        builder.AppendLine($"Fetched EDH Top 16 entries: {fetchedEntries.Count}");
        builder.AppendLine();
        builder.AppendLine("Reference decks:");
        for (var index = 0; index < fetchedEntries.Count; index++)
        {
            var entry = fetchedEntries[index];
            builder.Append(index + 1);
            builder.Append(". ");
            builder.Append(string.IsNullOrWhiteSpace(entry.PlayerName) ? "Unknown player" : entry.PlayerName);
            builder.Append(" | ");
            builder.Append(string.IsNullOrWhiteSpace(entry.TournamentName) ? "Unknown tournament" : entry.TournamentName);
            builder.Append(" | Standing ");
            builder.Append(entry.Standing);
            if (entry.TournamentDate.HasValue)
            {
                builder.Append(" | ");
                builder.Append(entry.TournamentDate.Value.ToString("yyyy-MM-dd"));
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildPrompt(
        string commanderName,
        IReadOnlyList<DeckEntry> myDeckEntries,
        CommanderSpellbookResult? myDeckCombos,
        IReadOnlyList<EdhTop16Entry> selectedEntries,
        IReadOnlyList<CommanderSpellbookResult?> referenceDeckCombos,
        IReadOnlyDictionary<string, string> oracleNameMap,
        string schemaJson)
    {
        var refCount = selectedEntries.Count;
        var builder = new StringBuilder();
        builder.AppendLine($"Title this chat: {commanderName} | cEDH Meta Gap");
        builder.AppendLine();
        builder.AppendLine("ROLE:");
        builder.AppendLine("You are a cEDH deck optimization analyst.");
        builder.AppendLine($"Compare MY_DECK against {refCount} REF deck(s).");
        builder.AppendLine();

        builder.AppendLine("EVIDENCE PRIORITY:");
        builder.AppendLine("1. Use the supplied decklists as the primary evidence.");
        builder.AppendLine("2. Use the supplied Commander Spellbook combo sections as verified combo evidence.");
        builder.AppendLine("3. Only infer patterns that are strongly supported by the supplied cards.");
        builder.AppendLine("4. If Commander Spellbook evidence and deck-reading inference conflict, prefer the Commander Spellbook evidence.");
        builder.AppendLine();

        builder.AppendLine("RULES:");
        builder.AppendLine("- Read every supplied decklist before answering.");
        builder.AppendLine("- Base every conclusion ONLY on observable card overlap and deck construction.");
        builder.AppendLine("- Do NOT assume combo lines unless supported by card presence in the lists.");
        builder.AppendLine("- Cite specific card names as evidence.");
        builder.AppendLine("- Clearly label any interpretation as inference.");
        builder.AppendLine("- If evidence is weak or unclear, explicitly say so in the relevant field.");
        builder.AppendLine("- Do NOT invent card text or interactions.");
        builder.AppendLine();

        builder.AppendLine("INPUT DATA:");
        builder.AppendLine($"MY_DECK ({commanderName}):");
        builder.AppendLine(BuildCompactDecklist(myDeckEntries, oracleNameMap));
        builder.AppendLine();
        builder.AppendLine(BuildComboReferenceText("MY_DECK", myDeckCombos));
        builder.AppendLine();

        for (var index = 0; index < refCount; index++)
        {
            var entry = selectedEntries[index];
            builder.Append($"R{index + 1} (");
            builder.Append(string.IsNullOrWhiteSpace(entry.PlayerName) ? "?" : entry.PlayerName);
            builder.Append($", #{entry.Standing}");
            if (!string.IsNullOrWhiteSpace(entry.TournamentName))
            {
                builder.Append($", {entry.TournamentName}");
            }

            if (entry.TournamentDate.HasValue)
            {
                builder.Append($", {entry.TournamentDate.Value:yyyy-MM-dd}");
            }

            builder.AppendLine("):");
            builder.AppendLine(BuildCompactRefDecklist(entry, oracleNameMap));
            builder.AppendLine();

            var comboResult = index < referenceDeckCombos.Count ? referenceDeckCombos[index] : null;
            builder.AppendLine(BuildComboReferenceText($"R{index + 1}", comboResult));
            builder.AppendLine();
        }

        builder.AppendLine("ANALYSIS TASK:");
        builder.AppendLine("Use the input data above and complete every section below.");
        builder.AppendLine();

        builder.AppendLine("1. WIN CONDITIONS");
        builder.AppendLine("- Identify primary and backup win lines in MY_DECK.");
        builder.AppendLine("- Identify primary and backup win lines across REF decks (consensus).");
        builder.AppendLine("- List win lines present in multiple REF decks but missing in MY_DECK.");
        builder.AppendLine();

        builder.AppendLine("2. INTERACTION AUDIT");
        builder.AppendLine("- Count and compare counterspells, removal, free interaction, and stax pieces.");
        builder.AppendLine("- Determine if MY_DECK is under, over, or aligned vs REF decks.");
        builder.AppendLine("- Identify key missing interaction pieces.");
        builder.AppendLine();

        builder.AppendLine("3. SPEED & TEMPO");
        builder.AppendLine("- Classify each deck as turbo (T2-3), fast (T3-4), mid (T4-5), or grind (T5+).");
        builder.AppendLine("- Estimate MY_DECK vs REF average goldfish speed.");
        builder.AppendLine("- Identify cards contributing to faster starts (fast mana, free spells).");
        builder.AppendLine();

        builder.AppendLine("4. MANA EFFICIENCY");
        builder.AppendLine("- Compare fast mana count (0-1 CMC ramp), total ramp density, and land count.");
        builder.AppendLine("- Identify missing high-impact acceleration pieces.");
        builder.AppendLine();

        builder.AppendLine("5. CARD OVERLAP ANALYSIS");
        builder.AppendLine($"- Core convergence: cards in all {refCount} REF decks. Flag whether MY_DECK has them.");
        builder.AppendLine("- High-frequency staples: cards in 2+ REF decks but not in MY_DECK = missing staples.");
        builder.AppendLine("- Cards unique to MY_DECK (in 0 REF decks) = potential cuts.");
        builder.AppendLine("- Categorize each by role: ramp, interaction, draw, wincon, protection, stax, tutor, utility, land.");
        builder.AppendLine();

        builder.AppendLine("6. CONSISTENCY & REDUNDANCY");
        builder.AppendLine("- Compare tutor density, redundant combo pieces, and draw engine count.");
        builder.AppendLine("- Determine whether MY_DECK is more or less consistent than the REF sample.");
        builder.AppendLine();

        builder.AppendLine("7. TOP IMPROVEMENTS");
        builder.AppendLine("- Top 5-10 adds: include what each replaces and justify using overlap evidence.");
        builder.AppendLine("- Top 5-10 cuts: explain why each is low-impact or non-meta.");
        builder.AppendLine();

        builder.AppendLine("8. META POSITIONING");
        builder.AppendLine("- Determine if MY_DECK is faster or slower than the field, more or less interactive.");
        builder.AppendLine("- Identify which archetype it most resembles (turbo, midrange, control, stax).");
        builder.AppendLine("- Assign a 1-10 cEDH readiness score with 2-sentence justification.");
        builder.AppendLine();

        builder.AppendLine("OUTPUT CONTRACT:");
        builder.AppendLine("- First, provide a concise human-readable meta gap summary.");
        builder.AppendLine("- Then return the JSON inside a fenced ```json code block (triple-backtick json) whose top-level object is meta_gap. Do not return raw JSON outside a code block.");
        builder.AppendLine("- The prose summary must come before the JSON block.");
        builder.AppendLine("- Fill every field in meta_gap.");
        builder.AppendLine("- Use empty strings, 0, 0.0, false, or [] when evidence is missing.");
        builder.AppendLine("- Keep all detail and justification concise, specific, and evidence-based.");
        builder.AppendLine("- Put the consistency/redundancy summary and meta-positioning summary into meta_summary and optimization_path.");
        builder.AppendLine("- Do not add any extra sections after the JSON block.");
        builder.AppendLine();
        builder.AppendLine("JSON SHAPE:");
        builder.AppendLine("Use this exact shape:");
        builder.AppendLine(schemaJson);
        return builder.ToString().TrimEnd();
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveOracleNameMapAsync(
        IReadOnlyList<DeckEntry> myDeckEntries,
        IReadOnlyList<EdhTop16Entry> selectedEntries,
        CancellationToken cancellationToken)
    {
        var uniqueNames = myDeckEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry.Name.Trim())
            .Concat(selectedEntries
                .SelectMany(entry => entry.MainDeck)
                .Where(card => !string.IsNullOrWhiteSpace(card.Name))
                .Select(card => card.Name.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var oracleNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    $"Scryfall card reference lookup failed while building the cEDH meta-gap prompt with HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            foreach (var card in response.Data.Data)
            {
                foreach (var submittedName in chunk.Where(name => string.Equals(name, card.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    oracleNameMap[submittedName] = card.Name;
                }
            }

            var unresolvedNames = chunk
                .Where(name => !oracleNameMap.ContainsKey(name))
                .ToList();

            foreach (var unresolvedName in unresolvedNames)
            {
                var fallbackCard = await SearchFallbackCardAsync(unresolvedName, cancellationToken).ConfigureAwait(false);
                if (fallbackCard is null)
                {
                    continue;
                }

                oracleNameMap[unresolvedName] = fallbackCard.Name;
            }
        }

        return oracleNameMap;
    }

    private async Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
    {
        var normalizedName = cardName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        var request = new RestRequest("cards/search", Method.Get);
        request.AddQueryParameter("q", $"!\"{normalizedName}\"");
        request.AddQueryParameter("unique", "cards");
        request.AddQueryParameter("order", "name");

        var response = await _executeSearchAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
        {
            return response.Data?.Data.FirstOrDefault();
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        throw new HttpRequestException(
            $"Scryfall fallback lookup failed while resolving {cardName} with HTTP {(int)response.StatusCode}.",
            null,
            response.StatusCode);
    }

    private static IReadOnlyList<DeckEntry> NormalizeDeckEntriesForPromptAndCombos(
        IReadOnlyList<DeckEntry> deckEntries,
        IReadOnlyDictionary<string, string> oracleNameMap)
    {
        return deckEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry =>
            {
                var resolvedName = ResolvePromptCardName(entry.Name, oracleNameMap);
                return new DeckEntry
                {
                    Name = resolvedName,
                    NormalizedName = CardNormalizer.Normalize(resolvedName),
                    Quantity = entry.Quantity,
                    Board = entry.Board
                };
            })
            .ToList();
    }

    private static IReadOnlyList<DeckEntry> BuildReferenceDeckEntries(string commanderName, EdhTop16Entry entry, IReadOnlyDictionary<string, string> oracleNameMap)
    {
        var entries = new List<DeckEntry>();
        if (!string.IsNullOrWhiteSpace(commanderName))
        {
            var resolvedCommanderName = ResolvePromptCardName(commanderName, oracleNameMap);
            entries.Add(new DeckEntry
            {
                Name = resolvedCommanderName,
                NormalizedName = CardNormalizer.Normalize(resolvedCommanderName),
                Quantity = 1,
                Board = "commander"
            });
        }

        foreach (var card in entry.MainDeck.Where(card => !string.IsNullOrWhiteSpace(card.Name)))
        {
            var resolvedName = ResolvePromptCardName(card.Name, oracleNameMap);
            entries.Add(new DeckEntry
            {
                Name = resolvedName,
                NormalizedName = CardNormalizer.Normalize(resolvedName),
                Quantity = 1,
                Board = "mainboard"
            });
        }

        return entries;
    }

    private static string BuildCompactDecklist(IReadOnlyList<DeckEntry> deckEntries, IReadOnlyDictionary<string, string>? oracleNameMap = null)
    {
        var builder = new StringBuilder();
        var normalizedEntries = deckEntries
            .Where(entry => !string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(
                entry => CardNormalizer.Normalize(entry.Name),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Quantity = group.Sum(entry => entry.Quantity),
                Name = group
                    .Select(entry => ResolvePromptCardName(entry.Name, oracleNameMap))
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty
            })
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in normalizedEntries)
        {
            builder.Append(entry.Quantity);
            builder.Append(' ');
            builder.AppendLine(entry.Name);
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildCompactRefDecklist(EdhTop16Entry entry, IReadOnlyDictionary<string, string>? oracleNameMap = null)
    {
        var builder = new StringBuilder();
        var normalizedCards = entry.MainDeck
            .Where(card => !string.IsNullOrWhiteSpace(card.Name))
            .GroupBy(
                card => CardNormalizer.Normalize(card.Name),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => ResolvePromptCardName(group.First().Name, oracleNameMap))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        foreach (var cardName in normalizedCards)
        {
            builder.AppendLine(cardName);
        }

        return builder.ToString().TrimEnd();
    }

    private static string GetBaseCardDisplayName(string? cardName)
    {
        if (string.IsNullOrWhiteSpace(cardName))
        {
            return string.Empty;
        }

        var trimmed = cardName.Trim();
        var splitSeparators = new[] { " // ", " / " };
        foreach (var separator in splitSeparators)
        {
            var splitIndex = trimmed.IndexOf(separator, StringComparison.Ordinal);
            if (splitIndex >= 0)
            {
                return trimmed[..splitIndex].Trim();
            }
        }

        return trimmed;
    }

    private static string ResolvePromptCardName(string? cardName, IReadOnlyDictionary<string, string>? oracleNameMap)
    {
        if (string.IsNullOrWhiteSpace(cardName))
        {
            return string.Empty;
        }

        var trimmed = cardName.Trim();
        if (oracleNameMap is not null && oracleNameMap.TryGetValue(trimmed, out var resolvedName) && !string.IsNullOrWhiteSpace(resolvedName))
        {
            return GetBaseCardDisplayName(resolvedName);
        }

        return GetBaseCardDisplayName(trimmed);
    }

    private static IEnumerable<List<string>> Chunk(IReadOnlyList<string> source, int size)
    {
        for (var index = 0; index < source.Count; index += size)
        {
            yield return source.Skip(index).Take(size).ToList();
        }
    }

    private static string BuildComboReferenceText(string label, CommanderSpellbookResult? result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Commander Spellbook combos for {label}:");

        if (result is null || (result.IncludedCombos.Count == 0 && result.AlmostIncludedCombos.Count == 0))
        {
            builder.AppendLine("(none found)");
            return builder.ToString().TrimEnd();
        }

        if (result.IncludedCombos.Count > 0)
        {
            builder.AppendLine($"Complete combos: {result.IncludedCombos.Count}");
            for (var i = 0; i < result.IncludedCombos.Count; i++)
            {
                var combo = result.IncludedCombos[i];
                builder.AppendLine($"{i + 1}. Cards: {string.Join(" + ", combo.CardNames)}");
                builder.AppendLine($"   Result: {string.Join(", ", combo.Results)}");
            }
        }
        else
        {
            builder.AppendLine("Complete combos: 0");
        }

        if (result.AlmostIncludedCombos.Count > 0)
        {
            builder.AppendLine($"Near-combos: {result.AlmostIncludedCombos.Count}");
            for (var i = 0; i < result.AlmostIncludedCombos.Count; i++)
            {
                var combo = result.AlmostIncludedCombos[i];
                builder.AppendLine($"{i + 1}. Missing: {combo.MissingCard} | Have: {string.Join(" + ", combo.CardsInDeck)}");
                builder.AppendLine($"   Result: {string.Join(", ", combo.Results)}");
            }
        }
        else
        {
            builder.AppendLine("Near-combos: 0");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildSchemaJson()
    {
        var payload = new
        {
            meta_gap = new
            {
                commander = string.Empty,
                color_id = string.Empty,
                ref_deck_count = 0,
                readiness_score = 0,
                readiness_justification = string.Empty,
                win_lines = new
                {
                    my_deck = new { primary = string.Empty, backup = string.Empty },
                    ref_consensus = new { primary = string.Empty, backup = string.Empty },
                    missing_lines = new[] { string.Empty }
                },
                interaction = new
                {
                    my_count = 0,
                    ref_avg_count = 0.0,
                    verdict = string.Empty,
                    detail = string.Empty
                },
                speed = new
                {
                    my_classification = string.Empty,
                    my_avg_turn = string.Empty,
                    ref_classification = string.Empty,
                    ref_avg_turn = string.Empty,
                    detail = string.Empty
                },
                mana_efficiency = new
                {
                    my_fast_mana = 0,
                    ref_avg_fast_mana = 0.0,
                    my_avg_cmc = 0.0,
                    ref_avg_cmc = 0.0,
                    my_lands = 0,
                    ref_avg_lands = 0.0,
                    detail = string.Empty
                },
                core_convergence = new[]
                {
                    new { card = string.Empty, role = string.Empty, in_my_deck = true }
                },
                missing_staples = new[]
                {
                    new { card = string.Empty, role = string.Empty, ref_count = 0, priority = 1, why = string.Empty }
                },
                potential_cuts = new[]
                {
                    new { card = string.Empty, role = string.Empty, ref_count = 0, priority = 1, why = string.Empty }
                },
                top_10_adds = new[]
                {
                    new { card = string.Empty, replaces = string.Empty, role = string.Empty, why = string.Empty }
                },
                top_10_cuts = new[]
                {
                    new { card = string.Empty, role = string.Empty, why = string.Empty }
                },
                meta_summary = string.Empty,
                optimization_path = string.Empty
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private static ChatGptCedhMetaGapResponse ParseResponse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("Paste the meta_gap JSON returned from ChatGPT into Step 3.");
        }

        var json = ChatGptJsonTextFormatterService.ExtractJsonPayload(input);
        using var document = JsonDocument.Parse(json);

        JsonElement payload = document.RootElement;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("meta_gap", out var metaGapElement))
        {
            payload = JsonSerializer.SerializeToElement(new { meta_gap = metaGapElement });
        }

        var result = JsonSerializer.Deserialize<ChatGptCedhMetaGapResponse>(payload.GetRawText(), JsonOptions);

        if (result is null)
        {
            throw new InvalidOperationException("The submitted ChatGPT response did not contain a valid meta_gap payload.");
        }

        return result;
    }

    private async Task<string> SaveArtifactsAsync(
        string commanderName,
        string inputSummary,
        string? promptText,
        string schemaJson,
        string? responseJson,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(
            _artifactsPath,
            CreateSafePathSegment(commanderName, "cedh-meta-gap"),
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outputDirectory);

        var files = new (string FileName, string? Content)[]
        {
            ("00-input-summary.txt", inputSummary),
            ("30-meta-gap-prompt.txt", promptText),
            ("31-meta-gap-schema.json", schemaJson),
            ("40-meta-gap-response.json", string.IsNullOrWhiteSpace(responseJson) ? null : ChatGptJsonTextFormatterService.ExtractJsonPayload(responseJson))
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

    private static string CreateSafePathSegment(string value, string fallback)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Trim()
            .Select(ch => invalidCharacters.Contains(ch) ? '-' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private sealed record LoadedDeck(IReadOnlyList<DeckEntry> PlayableEntries, string CommanderName);
}
