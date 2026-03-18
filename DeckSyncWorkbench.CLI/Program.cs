using System.CommandLine;
using System.Diagnostics;
using DeckSyncWorkbench.Core.Diffing;
using DeckSyncWorkbench.Core.Exporting;
using DeckSyncWorkbench.Core.Filtering;
using DeckSyncWorkbench.Core.Integration;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Parsing;
using DeckSyncWorkbench.Core.Reporting;
using DeckSyncWorkbench.Core.Knowledge;

var compareCommand = new Command("compare", "Compare Moxfield and Archidekt exports.");
var moxfieldOption = new Option<FileInfo?>("--moxfield");
var moxfieldUrlOption = new Option<string?>("--moxfield-url");
var archidektOption = new Option<FileInfo?>("--archidekt");
var archidektUrlOption = new Option<string?>("--archidekt-url");
var outOption = new Option<FileInfo>("--out") { IsRequired = true };
var modeOption = new Option<MatchMode>("--mode", () => MatchMode.Loose);
var directionOption = new Option<SyncDirection>("--direction", () => SyncDirection.DeckSyncWorkbench);
var dryRunOption = new Option<bool>("--dry-run");
var resolveConflictsOption = new Option<bool>("--resolve-conflicts");
var probeCommand = new Command("probe-moxfield", "Fetch a public Moxfield deck JSON payload and inspect tag-like fields.");
var probeUrlOption = new Option<string>("--url") { IsRequired = true };
var probeOutOption = new Option<FileInfo?>("--out");
var exportMoxfieldCommand = new Command("export-moxfield", "Fetch a public Moxfield deck through the API and save it as deck text.");
var exportMoxfieldUrlOption = new Option<string>("--url") { IsRequired = true };
var exportMoxfieldOutOption = new Option<FileInfo>("--out") { IsRequired = true };
var archidektCategoriesCommand = new Command("archidekt-categories", "Fetch a public Archidekt deck and print category counts by card quantity.");
var archidektCategoriesUrlOption = new Option<string>("--url") { IsRequired = true };
var archidektCategoriesOutOption = new Option<FileInfo?>("--out");
var archidektCategoryCardsCommand = new Command("archidekt-category-cards", "Fetch a public Archidekt deck and list cards in a specific category.");
var archidektCategoryCardsUrlOption = new Option<string>("--url") { IsRequired = true };
var archidektCategoryCardsCategoryOption = new Option<string>("--category") { IsRequired = true };
var archidektCategoryCardsOutOption = new Option<FileInfo?>("--out");
var archidektHarvestRecentCommand = new Command("archidekt-harvest-recent", "Fetch recent public Archidekt decks and aggregate cards by category.");
var archidektHarvestRecentCountOption = new Option<int>("--count", () => 20);
var archidektHarvestRecentOutOption = new Option<FileInfo>("--out") { IsRequired = true };
var archidektCacheCommand = new Command("archidekt-cache", "Run an incremental Archidekt category cache job for the requested duration.");
var archidektCacheSecondsOption = new Option<int>("--seconds", () => 20);
var archidektCacheMinutesOption = new Option<int>("--minutes", () => 0);
var categoryFindCommand = new Command("category-find", "Keep running the cache job until a card is observed in the knowledge DB.");
var categoryFindCardOption = new Option<string>("--card") { IsRequired = true };
var categoryFindSecondsOption = new Option<int>("--cache-seconds", () => 30);
var categoryFindTimeoutOption = new Option<int>("--timeout", () => 600);

compareCommand.AddOption(moxfieldOption);
compareCommand.AddOption(moxfieldUrlOption);
compareCommand.AddOption(archidektOption);
compareCommand.AddOption(archidektUrlOption);
compareCommand.AddOption(outOption);
compareCommand.AddOption(modeOption);
compareCommand.AddOption(directionOption);
compareCommand.AddOption(dryRunOption);
compareCommand.AddOption(resolveConflictsOption);
probeCommand.AddOption(probeUrlOption);
probeCommand.AddOption(probeOutOption);
exportMoxfieldCommand.AddOption(exportMoxfieldUrlOption);
exportMoxfieldCommand.AddOption(exportMoxfieldOutOption);
archidektCategoriesCommand.AddOption(archidektCategoriesUrlOption);
archidektCategoriesCommand.AddOption(archidektCategoriesOutOption);
archidektCategoryCardsCommand.AddOption(archidektCategoryCardsUrlOption);
archidektCategoryCardsCommand.AddOption(archidektCategoryCardsCategoryOption);
archidektCategoryCardsCommand.AddOption(archidektCategoryCardsOutOption);
archidektHarvestRecentCommand.AddOption(archidektHarvestRecentCountOption);
archidektHarvestRecentCommand.AddOption(archidektHarvestRecentOutOption);
archidektCacheCommand.AddOption(archidektCacheSecondsOption);
archidektCacheCommand.AddOption(archidektCacheMinutesOption);
categoryFindCommand.AddOption(categoryFindCardOption);
categoryFindCommand.AddOption(categoryFindSecondsOption);
categoryFindCommand.AddOption(categoryFindTimeoutOption);

compareCommand.SetHandler(context =>
{
    var parseResult = context.ParseResult;
    Environment.ExitCode = RunCompareAsync(
        parseResult.GetValueForOption(moxfieldOption),
        parseResult.GetValueForOption(moxfieldUrlOption),
        parseResult.GetValueForOption(archidektOption),
        parseResult.GetValueForOption(archidektUrlOption),
        parseResult.GetValueForOption(outOption)!,
        parseResult.GetValueForOption(modeOption),
        parseResult.GetValueForOption(directionOption),
        parseResult.GetValueForOption(dryRunOption),
        parseResult.GetValueForOption(resolveConflictsOption)).GetAwaiter().GetResult();
});

var rootCommand = new RootCommand("DeckSyncWorkbench");
rootCommand.AddCommand(compareCommand);
rootCommand.AddCommand(probeCommand);
rootCommand.AddCommand(exportMoxfieldCommand);
rootCommand.AddCommand(archidektCategoriesCommand);
rootCommand.AddCommand(archidektCategoryCardsCommand);
rootCommand.AddCommand(archidektHarvestRecentCommand);
rootCommand.AddCommand(archidektCacheCommand);
rootCommand.AddCommand(categoryFindCommand);
probeCommand.SetHandler((string url, FileInfo? output) =>
{
    Environment.ExitCode = RunProbeAsync(url, output).GetAwaiter().GetResult();
}, probeUrlOption, probeOutOption);
exportMoxfieldCommand.SetHandler((string url, FileInfo output) =>
{
    Environment.ExitCode = RunExportMoxfieldAsync(url, output).GetAwaiter().GetResult();
}, exportMoxfieldUrlOption, exportMoxfieldOutOption);
archidektCategoriesCommand.SetHandler((string url, FileInfo? output) =>
{
    Environment.ExitCode = RunArchidektCategoriesAsync(url, output).GetAwaiter().GetResult();
}, archidektCategoriesUrlOption, archidektCategoriesOutOption);
archidektCategoryCardsCommand.SetHandler((string url, string category, FileInfo? output) =>
{
    Environment.ExitCode = RunArchidektCategoryCardsAsync(url, category, output).GetAwaiter().GetResult();
}, archidektCategoryCardsUrlOption, archidektCategoryCardsCategoryOption, archidektCategoryCardsOutOption);
archidektHarvestRecentCommand.SetHandler((int count, FileInfo output) =>
{
    Environment.ExitCode = RunArchidektHarvestRecentAsync(count, output).GetAwaiter().GetResult();
}, archidektHarvestRecentCountOption, archidektHarvestRecentOutOption);
archidektCacheCommand.SetHandler((int seconds, int minutes) =>
{
    var totalSeconds = minutes > 0 ? minutes * 60 : seconds;
    Environment.ExitCode = RunArchidektCacheAsync(totalSeconds).GetAwaiter().GetResult();
}, archidektCacheSecondsOption, archidektCacheMinutesOption);
categoryFindCommand.SetHandler((string cardName, int runSeconds, int timeoutSeconds) =>
{
    Environment.ExitCode = RunCategoryFindAsync(cardName, runSeconds, timeoutSeconds).GetAwaiter().GetResult();
}, categoryFindCardOption, categoryFindSecondsOption, categoryFindTimeoutOption);
return await rootCommand.InvokeAsync(args);

static async Task<int> RunCompareAsync(FileInfo? moxfield, string? moxfieldUrl, FileInfo? archidekt, string? archidektUrl, FileInfo output, MatchMode mode, SyncDirection direction, bool dryRun, bool resolveConflicts)
{
    try
    {
        using var httpClient = new HttpClient();
        var moxfieldEntries = DeckEntryFilter.ExcludeMaybeboard(await LoadMoxfieldEntriesAsync(httpClient, moxfield, moxfieldUrl));
        var archidektEntries = await LoadArchidektEntriesAsync(httpClient, archidekt, archidektUrl);
        var sourceEntries = direction == SyncDirection.DeckSyncWorkbench ? moxfieldEntries : archidektEntries;
        var targetEntries = direction == SyncDirection.DeckSyncWorkbench ? archidektEntries : moxfieldEntries;
        var sourceSystem = direction == SyncDirection.DeckSyncWorkbench ? "Moxfield" : "Archidekt";
        var targetSystem = direction == SyncDirection.DeckSyncWorkbench ? "Archidekt" : "Moxfield";
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

static async Task<List<DeckEntry>> LoadMoxfieldEntriesAsync(HttpClient httpClient, FileInfo? file, string? url)
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
        return await new MoxfieldApiDeckImporter(httpClient).ImportAsync(url);
    }

    throw new InvalidOperationException("Either --moxfield or --moxfield-url is required.");
}

static async Task<List<DeckEntry>> LoadArchidektEntriesAsync(HttpClient httpClient, FileInfo? file, string? url)
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
        return await new ArchidektApiDeckImporter(httpClient).ImportAsync(url);
    }

    throw new InvalidOperationException("Either --archidekt or --archidekt-url is required.");
}

static IEnumerable<PrintingConflict> ResolveConflicts(IReadOnlyList<PrintingConflict> conflicts, string sourceSystem, string targetSystem)
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

static async Task<int> RunProbeAsync(string url, FileInfo? output)
{
    if (!MoxfieldApiUrl.TryGetDeckId(url, out var deckId))
    {
        Console.Error.WriteLine($"Unable to determine Moxfield deck id from: {url}");
        return 1;
    }

    var apiUri = MoxfieldApiUrl.BuildDeckApiUri(deckId);
    Console.WriteLine($"Deck id: {deckId}");
    Console.WriteLine($"API URL: {apiUri}");

    using var httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 DeckSyncWorkbench/1.0");
    httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
    httpClient.DefaultRequestHeaders.Referrer = new Uri("https://moxfield.com/");

    using var response = await httpClient.GetAsync(apiUri);
    var body = await response.Content.ReadAsStringAsync();

    Console.WriteLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    Console.WriteLine($"Content-Type: {response.Content.Headers.ContentType}");

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

static async Task<int> RunExportMoxfieldAsync(string url, FileInfo output)
{
    try
    {
        using var httpClient = new HttpClient();
        var entries = await new MoxfieldApiDeckImporter(httpClient).ImportAsync(url);
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

static async Task<int> RunArchidektCategoriesAsync(string url, FileInfo? output)
{
    try
    {
        using var httpClient = new HttpClient();
        var entries = await new ArchidektApiDeckImporter(httpClient).ImportAsync(url);
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

static async Task<int> RunArchidektCategoryCardsAsync(string url, string category, FileInfo? output)
{
    try
    {
        using var httpClient = new HttpClient();
        var entries = await new ArchidektApiDeckImporter(httpClient).ImportAsync(url);
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

static async Task<int> RunArchidektHarvestRecentAsync(int count, FileInfo output)
{
    try
    {
        using var httpClient = new HttpClient();
        var recentDeckIds = await new ArchidektRecentDecksImporter(httpClient).ImportRecentDeckIdsAsync(count);
        var importer = new ArchidektApiDeckImporter(httpClient);
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

static async Task<int> RunArchidektCacheAsync(int seconds)
{
    try
    {
        var duration = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 60));
        var artifactsPath = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
        var repository = new CategoryKnowledgeRepository(Path.Combine(artifactsPath, "category-knowledge.db"));
        await repository.EnsureSchemaAsync();

        using var httpClient = new HttpClient();
        var recentImporter = new ArchidektRecentDecksImporter(httpClient);
        var importer = new ArchidektApiDeckImporter(httpClient);
        var stopwatch = Stopwatch.StartNew();
        var processed = 0;

        while (stopwatch.Elapsed < duration)
        {
            var deckIds = await repository.GetNextUnprocessedDeckIdsAsync(5);
            if (deckIds.Count == 0)
            {
                var newIds = await recentImporter.ImportRecentDeckIdsAsync(10);
                if (newIds.Count == 0)
                {
                    break;
                }

                await repository.AddDeckIdsAsync(newIds);
                deckIds = await repository.GetNextUnprocessedDeckIdsAsync(5);
                if (deckIds.Count == 0)
                {
                    continue;
                }
            }

            foreach (var deckId in deckIds)
            {
                try
                {
                    var entries = await importer.ImportAsync(deckId);
                    await PersistDeckEntriesAsync(repository, entries);
                    await repository.MarkDecksProcessedAsync(new[] { deckId });
                    processed++;
                    Console.WriteLine($"Cached categories from deck {deckId}.");
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                {
                    await repository.MarkDecksProcessedAsync(new[] { deckId }, skip: true);
                    Console.WriteLine($"Skipped deck {deckId}: {ex.Message}");
                }

                if (stopwatch.Elapsed >= duration)
                {
                    break;
                }
            }
        }

        Console.WriteLine($"Cache run stopped after {stopwatch.Elapsed.TotalSeconds:F1} seconds. Total decks cached: {processed}");
        return 0;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<int> RunCategoryFindAsync(string cardName, int cacheSeconds, int timeoutSeconds)
{
    if (string.IsNullOrWhiteSpace(cardName))
    {
        Console.Error.WriteLine("Card name is required.");
        return 1;
    }

    var artifactsPath = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
    var repository = new CategoryKnowledgeRepository(Path.Combine(artifactsPath, "category-knowledge.db"));
    var perRun = Math.Max(5, cacheSeconds);
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

        await RunArchidektCacheAsync(remainingSeconds);
    }

    Console.WriteLine($"Timed out after {totalTimeout.TotalSeconds:F0}s without finding {cardName}.");
    return 2;
}

static async Task PersistDeckEntriesAsync(CategoryKnowledgeRepository repository, IEnumerable<DeckEntry> entries)
{
    var counts = new Dictionary<(string CardName, string Category), int>(CardCategoryComparer.Instance);

    foreach (var entry in entries)
    {
        foreach (var category in CategoryKnowledgeReporter.SplitCategories(entry.Category))
        {
            var key = (entry.Name, category);
            counts[key] = counts.TryGetValue(key, out var existing) ? existing + entry.Quantity : entry.Quantity;
        }
    }

    foreach (var group in counts)
    {
        await repository.PersistObservedCategoriesAsync("archidekt_live", group.Key.CardName, new[] { group.Key.Category }, group.Value);
    }
}

static void CollectInterestingPaths(System.Text.Json.JsonElement element, string path, List<string> paths)
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
