using System.CommandLine;
using System.IO;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.CLI;
using Serilog;
using Serilog.Events;

var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
Directory.CreateDirectory(logDirectory);
var logPath = Path.Combine(logDirectory, "cli-.log");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();

var compareCommand = new Command("compare", "Compare Moxfield and Archidekt exports.");
var moxfieldOption = new Option<FileInfo?>("--moxfield");
var moxfieldUrlOption = new Option<string?>("--moxfield-url");
var archidektOption = new Option<FileInfo?>("--archidekt");
var archidektUrlOption = new Option<string?>("--archidekt-url");
var outOption = new Option<FileInfo>("--out") { IsRequired = true };
var modeOption = new Option<MatchMode>("--mode", () => MatchMode.Loose);
var directionOption = new Option<SyncDirection>("--direction", () => SyncDirection.MoxfieldToArchidekt);
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
var categoryFindSecondsOption = new Option<int>("--cache-seconds", () => 20);
var categoryFindTimeoutOption = new Option<int>("--timeout", () => 600);
var cardLookupCommand = new Command("card-lookup", "Lookup a single card via Scryfall and show the printed text.");
var cardLookupNameOption = new Option<string>("--name") { IsRequired = true };

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
cardLookupCommand.AddOption(cardLookupNameOption);

compareCommand.SetHandler(context =>
{
    var parseResult = context.ParseResult;
    Environment.ExitCode = CommandRunners.RunCompareAsync(
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

var rootCommand = new RootCommand("MTG Deck Studio");
var cacheFlagOption = new Option<bool>("--archidekt-cache", "Run an Archidekt category cache sweep for the requested duration.");
var cacheMinutesOption = new Option<int>("--minutes", () => 0) { Description = "Duration in minutes when using --archidekt-cache." };
var cacheSecondsOption = new Option<int>("--seconds", () => 0) { Description = "Duration in seconds when using --archidekt-cache." };
rootCommand.AddOption(cacheFlagOption);
rootCommand.AddOption(cacheMinutesOption);
rootCommand.AddOption(cacheSecondsOption);
rootCommand.SetHandler(async (bool runCache, int minutes, int seconds) =>
{
    if (!runCache)
    {
        Console.WriteLine("MTG Deck Studio CLI. Use --help to see available commands or specify --archidekt-cache.");
        Environment.ExitCode = 0;
        return;
    }

    var totalSeconds = CommandRunners.GetCacheDurationSeconds(minutes, seconds);
    Environment.ExitCode = await CommandRunners.RunArchidektCacheAsync(totalSeconds, Log.Logger);
}, cacheFlagOption, cacheMinutesOption, cacheSecondsOption);

rootCommand.AddCommand(compareCommand);
rootCommand.AddCommand(probeCommand);
rootCommand.AddCommand(exportMoxfieldCommand);
rootCommand.AddCommand(archidektCategoriesCommand);
rootCommand.AddCommand(archidektCategoryCardsCommand);
rootCommand.AddCommand(archidektHarvestRecentCommand);
rootCommand.AddCommand(archidektCacheCommand);
rootCommand.AddCommand(categoryFindCommand);
rootCommand.AddCommand(cardLookupCommand);

probeCommand.SetHandler((string url, FileInfo? output) =>
{
    Environment.ExitCode = CommandRunners.RunProbeAsync(url, output).GetAwaiter().GetResult();
}, probeUrlOption, probeOutOption);

exportMoxfieldCommand.SetHandler((string url, FileInfo output) =>
{
    Environment.ExitCode = CommandRunners.RunExportMoxfieldAsync(url, output).GetAwaiter().GetResult();
}, exportMoxfieldUrlOption, exportMoxfieldOutOption);

archidektCategoriesCommand.SetHandler((string url, FileInfo? output) =>
{
    Environment.ExitCode = CommandRunners.RunArchidektCategoriesAsync(url, output).GetAwaiter().GetResult();
}, archidektCategoriesUrlOption, archidektCategoriesOutOption);

archidektCategoryCardsCommand.SetHandler((string url, string category, FileInfo? output) =>
{
    Environment.ExitCode = CommandRunners.RunArchidektCategoryCardsAsync(url, category, output).GetAwaiter().GetResult();
}, archidektCategoryCardsUrlOption, archidektCategoryCardsCategoryOption, archidektCategoryCardsOutOption);

archidektHarvestRecentCommand.SetHandler((int count, FileInfo output) =>
{
    Environment.ExitCode = CommandRunners.RunArchidektHarvestRecentAsync(count, output).GetAwaiter().GetResult();
}, archidektHarvestRecentCountOption, archidektHarvestRecentOutOption);

archidektCacheCommand.SetHandler((int seconds, int minutes) =>
{
    var totalSeconds = CommandRunners.GetCacheDurationSeconds(minutes, seconds);
    Environment.ExitCode = CommandRunners.RunArchidektCacheAsync(totalSeconds, Log.Logger).GetAwaiter().GetResult();
}, archidektCacheSecondsOption, archidektCacheMinutesOption);

categoryFindCommand.SetHandler((string cardName, int runSeconds, int timeoutSeconds) =>
{
    Environment.ExitCode = CommandRunners.RunCategoryFindAsync(cardName, runSeconds, timeoutSeconds).GetAwaiter().GetResult();
}, categoryFindCardOption, categoryFindSecondsOption, categoryFindTimeoutOption);

cardLookupCommand.SetHandler((string cardName) =>
{
    Environment.ExitCode = CommandRunners.RunCardLookupAsync(cardName).GetAwaiter().GetResult();
}, cardLookupNameOption);

return await rootCommand.InvokeAsync(args);
