using System.Globalization;
using System.Net;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Web.Extensions;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Http;
using DeckFlow.Web.Services.Manabase;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.Extensions.DependencyInjection;
using CoreScryfallCollectionIdentifier = DeckFlow.Core.Normalization.ScryfallCollectionIdentifier;

namespace DeckFlow.CLI;

/// <summary>
/// Runs the <c>manabase</c> command: load a public deck, resolve every card through
/// Scryfall's collection endpoint, then run the Karsten §6 mana-base pipeline
/// (<see cref="ScryfallCardFactMapper"/> → <see cref="ManabaseClassifier"/> →
/// <see cref="ManabaseAnalyzer"/>) and print the report.
/// </summary>
internal static class ManabaseCommandRunner
{
    // Scryfall's collection endpoint accepts at most 75 identifiers per request.
    private const int CollectionBatchSize = 75;

    // Only these boards belong in a Commander mana-base analysis; a sideboard / maybeboard
    // is not part of the 100-card deck and would skew the land target.
    private static readonly HashSet<string> AnalyzedBoards =
        new(StringComparer.OrdinalIgnoreCase) { "mainboard", "commander" };

    /// <summary>Resolve a deck and print its mana-base report. Returns a process exit code.</summary>
    /// <param name="archidektUrl">Public Archidekt deck URL, or null.</param>
    /// <param name="moxfieldUrl">Public Moxfield deck URL, or null.</param>
    /// <param name="mode">Analysis profile: "casual" (default), "focused", or "cedh".</param>
    /// <param name="includeSwapPrompt">When true, also print the paste-ready LLM swap prompt.</param>
    public static async Task<int> RunAsync(
        string? archidektUrl,
        string? moxfieldUrl,
        string mode = "casual",
        bool includeSwapPrompt = false)
    {
        bool hasArchidekt = !string.IsNullOrWhiteSpace(archidektUrl);
        bool hasMoxfield = !string.IsNullOrWhiteSpace(moxfieldUrl);
        if (hasArchidekt == hasMoxfield)
        {
            Console.Error.WriteLine("Specify exactly one of --archidekt-url or --moxfield-url.");
            return 1;
        }

        if (!TryParseMode(mode, out ManabaseMode manabaseMode))
        {
            Console.Error.WriteLine("--mode must be 'casual', 'focused', or 'cedh'.");
            return 1;
        }

        try
        {
            List<DeckEntry> entries = hasArchidekt
                ? await DeckCommandRunners.LoadArchidektEntriesAsync(null, archidektUrl)
                : await DeckCommandRunners.LoadMoxfieldEntriesAsync(null, moxfieldUrl);

            // Keep only the boards that make up the deck under analysis.
            var deckCards = entries
                .Where(e => AnalyzedBoards.Contains(e.Board))
                .ToList();

            if (deckCards.Count == 0)
            {
                Console.Error.WriteLine("No mainboard/commander cards found in the deck.");
                return 2;
            }

            // Resolve each distinct card once. Prefer an exact printing (set + collector
            // number) so alternate / flavor / accented card names still resolve; fall back
            // to a plain name identifier when the entry carries no printing.
            ScryfallCollectionProtocolRequest collectionRequest = CreateCollectionRequest(deckCards);

            (var index, var notFound) = await ResolveCardsAsync(collectionRequest);

            var deckEntries = new List<DeckCardEntry>();
            var unresolved = new List<string>();
            foreach (DeckEntry entry in deckCards)
            {
                if (index.TryResolve(entry.Name, entry.SetCode, entry.CollectorNumber, out ScryfallCardData? card))
                {
                    deckEntries.Add(new DeckCardEntry
                    {
                        Card = card!,
                        Quantity = entry.Quantity,
                        IsCommander = string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase),
                    });
                }
                else
                {
                    unresolved.Add(entry.Name);
                }
            }

            if (deckEntries.Count == 0)
            {
                Console.Error.WriteLine("Scryfall resolved none of the deck's cards; cannot analyze.");
                return 2;
            }

            IReadOnlyList<CardFact> facts = ScryfallCardFactMapper.ToCardFacts(deckEntries);

            // Mirror the production web defaults so the CLI verdict matches the live site. These
            // four flags (MQ-02 mana-quantity, MQ-03 ramp-credit-v2, MQ-05 color-aware-mulligan,
            // and 70-03b land-ramp-sim) are seeded ON in prod; the CLI has no flag store, so they
            // are pinned ON here. The health-band flags are seeded OFF in prod, so they are left at
            // their false defaults. ramp-credit-v2 + land-ramp-sim change the classifier's land
            // target/ramp credit (printed), so threading them keeps the CLI numbers aligned.
            ManabaseDeck deck = ManabaseClassifier.Classify(
                facts, isSingleton: true, rampCreditV2: true, landRampSim: true);
            ManabaseReport report = ManabaseAnalyzer.Analyze(
                deck, manabaseMode, CommanderImportance.Standard, costOverrides: null,
                useManaQuantity: true, colorAwareMulligan: true, gateRampOnCastable: true);

            // Plain-language verdict + ramp/draw advisory are Casual-only here (cEDH leaves them
            // null). The web tool computes the same Core surfaces but additionally gates them behind
            // its plain-language-verdict flag; the CLI always shows them in Casual by design.
            ManabaseRampDrawBudget? budget = null;
            ManabaseVerdict? verdict = null;
            if (manabaseMode != ManabaseMode.Cedh)
            {
                budget = ManabaseRampDrawBudgetCalculator.Calculate(deck);
                verdict = ManabaseVerdictSynthesizer.Synthesize(report, manabaseMode, budget);
            }

            PrintReport(report, verdict, budget, unresolved, notFound);

            if (includeSwapPrompt)
            {
                string decklistText = string.Join(
                    "\n",
                    deckCards.Select(e => $"{e.Quantity} {e.Name}"));
                Console.WriteLine();
                Console.WriteLine("--- ChatGPT swap prompt ---");
                Console.WriteLine(ManabaseSwapPromptBuilder.Build(
                    report, deckName: null, decklistText, manabaseMode, verdict, budget));
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    // Batch-resolve identifiers through Scryfall's collection endpoint. Returns a card index
    // (keyed by printing + name) and labels for the identifiers Scryfall could not find.
    internal static ScryfallCollectionProtocolRequest CreateCollectionRequest(IReadOnlyList<DeckEntry> entries) =>
        new(entries
            .Select(entry => !string.IsNullOrWhiteSpace(entry.SetCode) && !string.IsNullOrWhiteSpace(entry.CollectorNumber)
                ? ScryfallCollectionNameIdentifier.ForPrinting(entry.SetCode, entry.CollectorNumber)
                : ScryfallCollectionNameIdentifier.ForName(CoreScryfallCollectionIdentifier.ToFaceIdentifier(entry.Name)))
            .Distinct()
            .ToArray());

    private static async Task<(ScryfallCardNameIndex Index, List<string> NotFound)> ResolveCardsAsync(
        ScryfallCollectionProtocolRequest collectionRequest)
    {
        using ServiceProvider serviceProvider = BuildScryfallServiceProvider();
        await CliFeatureFlagServices.InitializeFeatureFlagsAsync(serviceProvider, CancellationToken.None).ConfigureAwait(false);
        IScryfallCollectionProtocol collectionProtocol = serviceProvider.GetRequiredService<IScryfallCollectionProtocol>();

        var index = new ScryfallCardNameIndex();
        var notFound = new List<string>();

        for (int offset = 0; offset < collectionRequest.Identifiers.Count; offset += CollectionBatchSize)
        {
            var request = new ScryfallCollectionProtocolRequest(
                collectionRequest.Identifiers.Skip(offset).Take(CollectionBatchSize).ToArray());
            ScryfallCollectionProtocolResponse response = await collectionProtocol.ResolveAsync(request).ConfigureAwait(false);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || !response.HasPayload)
            {
                throw new InvalidOperationException(
                    $"Scryfall collection lookup failed with HTTP {(int)response.StatusCode}.");
            }

            foreach (ScryfallCard card in response.Cards)
            {
                index.Add(ScryfallCardDataMapper.ToCardData(card));
            }

            notFound.AddRange(GetNotFoundLabels(response));
        }

        ScryfallCacheStatisticsReporter.Report(serviceProvider.GetRequiredService<ScryfallCollectionCardCache>());
        return (index, notFound);
    }

    internal static IReadOnlyList<string> GetNotFoundLabels(ScryfallCollectionProtocolResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.NotFound.Select(identifier => identifier.Label).ToArray();
    }

    private static void PrintReport(
        ManabaseReport report,
        ManabaseVerdict? verdict,
        ManabaseRampDrawBudget? budget,
        IReadOnlyList<string> unresolved,
        IReadOnlyList<string> notFound)
    {
        Console.WriteLine();
        Console.WriteLine("=== Mana-base analysis (Karsten §6) ===");
        Console.WriteLine();
        WriteInvariant($"Lands: {report.ActualLands}  vs target ~{report.TargetLands:F1}  (delta {report.LandDelta:+0.0;-0.0;0.0})");
        WriteInvariant($"Health: {(report.IsHealthy ? "OK" : "needs work")}");
        Console.WriteLine();
        Console.WriteLine("Color  Sources  Needed  Deficit  Driving spell");
        Console.WriteLine("-----  -------  ------  -------  -------------");
        foreach (ColorSourceFinding f in report.ColorFindings)
        {
            WriteInvariant($"{f.Color,-5}  {f.ActualSources,7:F1}  {f.RequiredSources,6}  {f.Deficit,7:+0.0;-0.0;0.0}  {f.DrivingSpell}");
        }

        Console.WriteLine();
        Console.WriteLine(report.Summary);

        if (verdict is not null)
        {
            Console.WriteLine();
            Console.WriteLine(verdict.Headline);
            if (verdict.HasIssues)
            {
                foreach (string line in verdict.Lines)
                {
                    Console.WriteLine($"- {line}");
                }
            }
            else
            {
                Console.WriteLine(verdict.NoIssueReason);
            }
        }

        if (budget is not null)
        {
            Console.WriteLine();
            WriteInvariant(
                $"Ramp/draw budget: {budget.RampCount:0.#} ramp / {budget.DrawCount:0.#} draw (target ~{budget.TargetRamp}/{budget.TargetDraw}).");
            if (budget.IsRampLight)
            {
                WriteInvariant($"  Ramp looks light — about {budget.RampShort} more ramp piece(s) suggested.");
            }
            else if (budget.IsRampHeavy)
            {
                Console.WriteLine("  Ramp looks heavy for this curve.");
            }
            if (budget.IsDrawLight)
            {
                WriteInvariant($"  Card draw looks light — about {budget.DrawShort} more draw piece(s) suggested.");
            }
        }

        if (notFound.Count > 0)
        {
            Console.WriteLine();
            WriteInvariant($"Scryfall could not find {notFound.Count} name(s): {string.Join(", ", notFound)}");
        }

        if (unresolved.Count > 0)
        {
            Console.WriteLine();
            WriteInvariant($"Skipped {unresolved.Count} unmatched entry/entries: {string.Join(", ", unresolved)}");
        }
    }

    // Console.WriteLine lacks an IFormatProvider overload; format invariantly first so
    // decimals render with a "." regardless of the host culture.
    private static void WriteInvariant(FormattableString line) =>
        Console.WriteLine(line.ToString(CultureInfo.InvariantCulture));

    // Map the --mode option to the Core enum. The "casual"/"cedh" inputs match the enum names
    // case-insensitively; IsDefined rejects out-of-range numeric strings.
    private static bool TryParseMode(string? mode, out ManabaseMode parsed)
        => Enum.TryParse(mode?.Trim(), ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    private static ServiceProvider BuildScryfallServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddDeckFlowHttpClients();
        services.AddDeckFlowResiliencePipelines();
        services.AddCliFeatureFlags();
        services.AddDeckFlowScryfallServices();
        return services.BuildServiceProvider();
    }
}
