using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using MtgDeckStudio.Core.Diffing;
using MtgDeckStudio.Core.Exporting;
using MtgDeckStudio.Core.Filtering;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Knowledge;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Core.Reporting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RestSharp;
using Serilog;

namespace MtgDeckStudio.CLI;

internal static class CommandRunners
{
    public static async Task<int> RunCompareAsync(FileInfo? moxfield, string? moxfieldUrl, FileInfo? archidekt, string? archidektUrl, FileInfo output, MatchMode mode, SyncDirection direction, bool dryRun, bool resolveConflicts)
    {
        try
        {
            var moxfieldEntries = DeckEntryFilter.ExcludeMaybeboard(await LoadMoxfieldEntriesAsync(moxfield, moxfieldUrl));
            var archidektEntries = await LoadArchidektEntriesAsync(archidekt, archidektUrl);
            ValidateDeckSize("Moxfield", moxfieldEntries);
            ValidateDeckSize("Archidekt", archidektEntries);
            var sourceEntries = direction == SyncDirection.MoxfieldToArchidekt ? moxfieldEntries : archidektEntries;
            var targetEntries = direction == SyncDirection.MoxfieldToArchidekt ? archidektEntries : moxfieldEntries;
            var sourceSystem = direction == SyncDirection.MoxfieldToArchidekt ? "Moxfield" : "Archidekt";
            var targetSystem = direction == SyncDirection.MoxfieldToArchidekt ? "Archidekt" : "Moxfield";
            var diff = new DiffEngine(mode).Compare(sourceEntries, targetEntries);

            Console.WriteLine(ReconciliationReporter.ToText(diff, sourceSystem, targetSystem));
            Console.WriteLine();

            if (resolveConflicts)
            {
                diff = diff with
                {
                    PrintingConflicts = ResolveConflicts(diff.PrintingConflicts, sourceSystem, targetSystem).ToList(),
                };

                var checklist = ReconciliationReporter.GenerateSwapChecklist(diff.PrintingConflicts.ToList(), targetSystem);
                if (!string.IsNullOrWhiteSpace(checklist))
                {
                    Console.WriteLine(checklist);
                    Console.WriteLine();
                }
            }

            if (dryRun)
            {
                Console.WriteLine("(Dry run - no output file written. Run without --dry-run to generate file.)");
                Console.WriteLine();
                Console.WriteLine(DeltaExporter.ToText(diff.ToAdd.ToList(), targetSystem));
                Console.WriteLine();
                Console.WriteLine("--- Full Import ---");
                Console.WriteLine(FullImportExporter.ToText(sourceEntries, targetEntries, mode, targetSystem, diff.PrintingConflicts));
                return 0;
            }

            DeltaExporter.WriteFile(diff.ToAdd.ToList(), output.FullName, targetSystem);
            var fullImportPath = Path.Combine(
                output.DirectoryName ?? Directory.GetCurrentDirectory(),
                $"{Path.GetFileNameWithoutExtension(output.Name)}.full{output.Extension}");
            FullImportExporter.WriteFile(sourceEntries, targetEntries, mode, fullImportPath, targetSystem, diff.PrintingConflicts);
            Console.WriteLine($"Wrote delta file: {output.FullName}");
            Console.WriteLine($"Wrote full import file: {fullImportPath}");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DeckParseException or InvalidOperationException or HttpRequestException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    public static async Task<List<DeckEntry>> LoadMoxfieldEntriesAsync(FileInfo? file, string? url)
    {
        if (file is not null && !string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("Specify either --moxfield or --moxfield-url, not both.");
        }

        if (file is not null)
        {
            return new MoxfieldParser().ParseFile(file.FullName);
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            return await new MoxfieldApiDeckImporter().ImportAsync(url);
        }

        throw new InvalidOperationException("Either --moxfield or --moxfield-url is required.");
    }

    public static async Task<List<DeckEntry>> LoadArchidektEntriesAsync(FileInfo? file, string? url)
    {
        if (file is not null && !string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("Specify either --archidekt or --archidekt-url, not both.");
        }

        if (file is not null)
        {
            return new ArchidektParser().ParseFile(file.FullName);
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            return await new ArchidektApiDeckImporter().ImportAsync(url);
        }

        throw new InvalidOperationException("Either --archidekt or --archidekt-url is required.");
    }

    public static void ValidateDeckSize(string systemName, IReadOnlyList<DeckEntry> entries)
    {
        const int requiredDeckSize = 100;
        var count = entries
            .Where(entry => !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase))
            .Sum(entry => entry.Quantity);

        if (count != requiredDeckSize)
        {
            throw new InvalidOperationException($"{systemName} deck must contain exactly {requiredDeckSize} cards across commander and mainboard. Found {count}.");
        }
    }

    public static IEnumerable<PrintingConflict> ResolveConflicts(IReadOnlyList<PrintingConflict> conflicts, string sourceSystem, string targetSystem)
    {
        foreach (var conflict in conflicts)
        {
            Console.WriteLine($"Printing conflict: {conflict.CardName}");
            Console.WriteLine($"  [A] Keep {targetSystem} version: ({conflict.ArchidektVersion.SetCode}) {conflict.ArchidektVersion.CollectorNumber}  [category: {conflict.ArchidektVersion.Category ?? "none"}]");
            Console.WriteLine($"  [M] Use {sourceSystem} version:   ({conflict.MoxfieldVersion.SetCode}) {conflict.MoxfieldVersion.CollectorNumber}");
            Console.Write("Choice (A/M, default A): ");

            var input = Console.ReadLine()?.Trim();
            var resolution = string.Equals(input, "M", StringComparison.OrdinalIgnoreCase)
                ? PrintingChoice.UseMoxfield
                : PrintingChoice.KeepArchidekt;

            yield return conflict with { Resolution = resolution };
            Console.WriteLine();
        }
    }

    public static async Task<int> RunProbeAsync(string url, FileInfo? output)
    {
        if (!MoxfieldApiUrl.TryGetDeckId(url, out var deckId))
        {
            Console.Error.WriteLine($"Unable to determine Moxfield deck id from: {url}");
            return 1;
        }

        var apiUri = MoxfieldApiUrl.BuildDeckApiUri(deckId);
        Console.WriteLine($"Deck id: {deckId}");
        Console.WriteLine($"API URL: {apiUri}");

        var client = new RestClient(new RestClientOptions
        {
            ThrowOnAnyError = false,
        });

        var request = new RestRequest(apiUri, Method.Get);
        request.AddHeader("User-Agent", "Mozilla/5.0 MtgDeckStudio/1.0");
        request.AddHeader("Accept", "application/json, text/plain, */*");
        request.AddHeader("Referer", "https://moxfield.com/");

        var response = await client.ExecuteAsync(request);
        var body = response.Content ?? string.Empty;

        Console.WriteLine($"HTTP {(int)response.StatusCode} {response.StatusDescription}");
        Console.WriteLine($"Content-Type: {response.ContentType}");

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine();
            Console.WriteLine(body[..Math.Min(body.Length, 400)]);
            return 1;
        }

        if (output is not null)
        {
            Directory.CreateDirectory(output.DirectoryName ?? Directory.GetCurrentDirectory());
            await File.WriteAllTextAsync(output.FullName, body);
            Console.WriteLine($"Saved raw JSON to: {output.FullName}");
        }

        using var json = System.Text.Json.JsonDocument.Parse(body);
        var root = json.RootElement;

        Console.WriteLine();
        Console.WriteLine("Top-level properties:");
        foreach (var property in root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {property}");
        }

        Console.WriteLine();
        Console.WriteLine("Tag-like paths:");
        var paths = new List<string>();
        CollectInterestingPaths(root, "$", paths);
        if (paths.Count == 0)
        {
            Console.WriteLine("  none found");
        }
        else
        {
            foreach (var path in paths.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {path}");
            }
        }

        return 0;
    }

    public static async Task<int> RunExportMoxfieldAsync(string url, FileInfo output)
    {
        try
        {
            var entries = await new MoxfieldApiDeckImporter().ImportAsync(url);
            Directory.CreateDirectory(output.DirectoryName ?? Directory.GetCurrentDirectory());
            MoxfieldTextExporter.WriteFile(entries, output.FullName);
            Console.WriteLine($"Wrote Moxfield deck file: {output.FullName}");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    public static async Task<int> RunArchidektCategoriesAsync(string url, FileInfo? output)
    {
        try
        {
            var entries = await new ArchidektApiDeckImporter().ImportAsync(url);
            var text = CategoryCountReporter.ToText(entries);

            Console.WriteLine(text);
            if (output is not null)
            {
                Directory.CreateDirectory(output.DirectoryName ?? Directory.GetCurrentDirectory());
                await File.WriteAllTextAsync(output.FullName, text);
                Console.WriteLine();
                Console.WriteLine($"Wrote category counts: {output.FullName}");
            }

            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    public static async Task<int> RunArchidektCategoryCardsAsync(string url, string category, FileInfo? output)
    {
        try
        {
            var entries = await new ArchidektApiDeckImporter().ImportAsync(url);
            var text = CategoryCardReporter.ToText(entries, category);

            Console.WriteLine(text);
            if (output is not null)
            {
                Directory.CreateDirectory(output.DirectoryName ?? Directory.GetCurrentDirectory());
                await File.WriteAllTextAsync(output.FullName, text);
                Console.WriteLine();
                Console.WriteLine($"Wrote category card list: {output.FullName}");
            }

            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    public static async Task<int> RunArchidektHarvestRecentAsync(int count, FileInfo output)
    {
        try
        {
            var recentDeckIds = await new ArchidektRecentDecksImporter().ImportRecentDeckIdsAsync(count);
            var importer = new ArchidektApiDeckImporter();
            var entries = new List<DeckEntry>();

            foreach (var deckId in recentDeckIds)
            {
                var deckEntries = await importer.ImportAsync(deckId);
                entries.AddRange(deckEntries);
            }

            var text = CategoryKnowledgeReporter.ToText(entries, recentDeckIds.Count);
            Directory.CreateDirectory(output.DirectoryName ?? Directory.GetCurrentDirectory());
            await File.WriteAllTextAsync(output.FullName, text);

            Console.WriteLine($"Harvested {recentDeckIds.Count} decks.");
            Console.WriteLine($"Wrote category knowledge file: {output.FullName}");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    public static async Task<int> RunArchidektCacheAsync(int seconds, Serilog.ILogger? logger = null)
    {
        logger ??= Log.Logger;
        try
        {
            var durationSeconds = Math.Max(5, seconds);
            var artifactsPath = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
            var repository = new CategoryKnowledgeRepository(Path.Combine(artifactsPath, "category-knowledge.db"));
            var duration = TimeSpan.FromSeconds(durationSeconds);

            logger.Information("Starting Archidekt cache sweep for {DurationSeconds}s (artifacts: {ArtifactsPath}).", durationSeconds, artifactsPath);

            var session = new ArchidektDeckCacheSession(
                repository,
                new ArchidektApiDeckImporter(),
                new ArchidektRecentDecksImporter(),
                NullLogger<ArchidektDeckCacheSession>.Instance);

            var result = await session.RunAsync(duration, queueBatchSize: 5, fetchBatchSize: 10);

            logger.Information(
                "Cache run stopped after {ElapsedSeconds:F1}s. New decks: {DecksAdded}; updated decks: {DecksUpdated}; skipped: {DecksSkipped}.",
                result.Duration.TotalSeconds,
                result.DecksAdded,
                result.DecksUpdated,
                result.DecksSkipped);
            Console.WriteLine($"Cache run stopped after {result.Duration.TotalSeconds:F1} seconds.");
            Console.WriteLine($"New decks cached: {result.DecksAdded}");
            Console.WriteLine($"Existing decks updated: {result.DecksUpdated}");
            Console.WriteLine($"Decks skipped: {result.DecksSkipped}");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            logger.Error(exception, "Archidekt cache sweep failed.");
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    public static async Task<int> RunCategoryFindAsync(string cardName, int cacheSeconds, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(cardName))
        {
            Console.Error.WriteLine("Card name is required.");
            return 1;
        }

        var artifactsPath = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
        var repository = new CategoryKnowledgeRepository(Path.Combine(artifactsPath, "category-knowledge.db"));
        var perRun = GetCacheDurationSeconds(0, cacheSeconds);
        var totalTimeout = TimeSpan.FromSeconds(Math.Max(perRun, timeoutSeconds));
        using var cancellation = new CancellationTokenSource(totalTimeout);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < totalTimeout)
        {
            await repository.EnsureSchemaAsync(cancellation.Token);
            var categories = await repository.GetCategoriesAsync(cardName, cancellation.Token);
            if (categories.Count > 0)
            {
                Console.WriteLine($"Found {cardName} with categories: {string.Join(", ", categories)}");
                return 0;
            }

            var remainingSeconds = Math.Min(perRun, (int)(totalTimeout - stopwatch.Elapsed).TotalSeconds);
            if (remainingSeconds <= 0)
            {
                break;
            }

            await RunArchidektCacheAsync(remainingSeconds, Log.Logger);
        }

        Console.WriteLine($"Timed out after {totalTimeout.TotalSeconds:F0}s without finding {cardName}.");
        return 2;
    }

    public static void CollectInterestingPaths(System.Text.Json.JsonElement element, string path, List<string> paths)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = $"{path}.{property.Name}";
                    if (property.Name.Contains("tag", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("category", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("board", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("annotation", StringComparison.OrdinalIgnoreCase))
                    {
                        paths.Add(propertyPath);
                    }

                    CollectInterestingPaths(property.Value, propertyPath, paths);
                }

                break;
            case System.Text.Json.JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index >= 3)
                    {
                        paths.Add($"{path}[...]");
                        break;
                    }

                    CollectInterestingPaths(item, $"{path}[{index}]", paths);
                    index++;
                }

                break;
        }
    }

    public static int GetCacheDurationSeconds(int minutes, int seconds)
        => Math.Max(5, minutes > 0 ? minutes * 60 : seconds);

    public static async Task<int> RunCardLookupAsync(string cardName)
    {
        try
        {
            var client = new RestClient(new RestClientOptions
            {
                BaseUrl = new Uri("https://api.scryfall.com"),
                ThrowOnAnyError = false,
            });

            client.AddDefaultHeader("User-Agent", "MtgDeckStudio.CLI/1.0 (+https://github.com/luntc1972/MtgDeckStudio)");
            client.AddDefaultHeader("Accept", "application/json;q=0.9,*/*;q=0.8");

            var request = new RestRequest("cards/named", Method.Get);
            request.AddQueryParameter("exact", cardName);

            var response = await client.ExecuteAsync<ScryfallCardDto>(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"Card not found: {cardName}");
                return 2;
            }

            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
            {
                Console.Error.WriteLine($"Scryfall returned HTTP {(int)response.StatusCode}.");
                return 1;
            }

            PrintCard(response.Data);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void PrintCard(ScryfallCardDto card)
    {
        Console.WriteLine(card.Name);
        if (!string.IsNullOrWhiteSpace(card.ManaCost))
        {
            Console.WriteLine(card.ManaCost);
        }

        Console.WriteLine(card.TypeLine);
        if (!string.IsNullOrWhiteSpace(card.OracleText))
        {
            Console.WriteLine();
            Console.WriteLine(card.OracleText);
        }

        if (!string.IsNullOrWhiteSpace(card.Power) && !string.IsNullOrWhiteSpace(card.Toughness))
        {
            Console.WriteLine();
            Console.WriteLine($"{card.Power}/{card.Toughness}");
        }
    }

    private record ScryfallCardDto(
        string Name,
        [property: JsonPropertyName("mana_cost")] string? ManaCost,
        [property: JsonPropertyName("type_line")] string TypeLine,
        [property: JsonPropertyName("oracle_text")] string? OracleText,
        [property: JsonPropertyName("power")] string? Power,
        [property: JsonPropertyName("toughness")] string? Toughness);
}
