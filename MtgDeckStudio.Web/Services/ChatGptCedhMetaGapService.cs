using System.Text;
using System.Text.Json;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Web.Models;

namespace MtgDeckStudio.Web.Services;

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
    private readonly string _artifactsPath;

    public ChatGptCedhMetaGapService(
        IMoxfieldDeckImporter moxfieldDeckImporter,
        IArchidektDeckImporter archidektDeckImporter,
        MoxfieldParser moxfieldParser,
        ArchidektParser archidektParser,
        IEdhTop16Client edhTop16Client)
    {
        _moxfieldDeckImporter = moxfieldDeckImporter;
        _archidektDeckImporter = archidektDeckImporter;
        _moxfieldParser = moxfieldParser;
        _archidektParser = archidektParser;
        _edhTop16Client = edhTop16Client;
        _artifactsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MTG Deck Studio",
            "ChatGPT cEDH Meta Gap");
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
            promptText = BuildPrompt(resolvedCommanderName, loadedDeck.PlayableEntries, selectedEntries, schemaJson);
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

        if (distinctIndexes.Count > 5)
        {
            throw new InvalidOperationException("Select no more than 5 EDH Top 16 reference decks before generating the prompt.");
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
        IReadOnlyList<EdhTop16Entry> selectedEntries,
        string schemaJson)
    {
        var refCount = selectedEntries.Count;
        var builder = new StringBuilder();
        builder.AppendLine($"Title this chat: {commanderName} | cEDH Meta Gap Analysis");
        builder.AppendLine();
        builder.AppendLine($"You are a cEDH deck optimization analyst. Compare MY_DECK against {refCount} REF deck(s). Read every supplied decklist before answering.");
        builder.AppendLine();

        // Strict rules
        builder.AppendLine("STRICT RULES:");
        builder.AppendLine("- Base every conclusion ONLY on observable card overlap and deck construction.");
        builder.AppendLine("- Do NOT assume combo lines unless supported by card presence in the lists.");
        builder.AppendLine("- Cite specific card names as evidence.");
        builder.AppendLine("- Clearly label any interpretation as inference.");
        builder.AppendLine("- If evidence is weak or unclear, explicitly say so in the relevant field.");
        builder.AppendLine("- Do NOT invent card text or interactions.");
        builder.AppendLine();

        // Decklists
        builder.AppendLine($"MY_DECK ({commanderName}):");
        builder.AppendLine(BuildCompactDecklist(myDeckEntries));
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
            builder.AppendLine(BuildCompactRefDecklist(entry));
            builder.AppendLine();
        }

        // 8 analysis sections
        builder.AppendLine("ANALYSIS TASK:");
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

        // Output format
        builder.AppendLine("OUTPUT:");
        builder.AppendLine("Return a single fenced ```json block only. No prose before or after.");
        builder.AppendLine("Top-level object must be meta_gap. Fill every field.");
        builder.AppendLine("Use empty strings, 0, 0.0, false, or [] when evidence is missing.");
        builder.AppendLine("Keep all detail/why text concise and evidence-based.");
        builder.AppendLine("Populate consistency and meta_positioning as free-text summaries in meta_summary and optimization_path.");
        builder.AppendLine("Use this exact shape:");
        builder.AppendLine(schemaJson);
        return builder.ToString().TrimEnd();
    }

    private static string BuildCompactDecklist(IReadOnlyList<DeckEntry> deckEntries)
    {
        var builder = new StringBuilder();
        foreach (var entry in deckEntries
                     .Where(entry => !string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(entry.Quantity);
            builder.Append(' ');
            builder.AppendLine(entry.Name);
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildCompactRefDecklist(EdhTop16Entry entry)
    {
        var builder = new StringBuilder();
        foreach (var card in entry.MainDeck.OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(card.Name);
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
