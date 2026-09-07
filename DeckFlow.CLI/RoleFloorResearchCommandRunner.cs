using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeckFlow.Core.Analysis;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Normalization;
using DeckFlow.Core.Reporting;
using DeckFlow.Core.Research;
using DeckFlow.Core.Storage;
using DeckFlow.Web.Extensions;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.CutLab;
using DeckFlow.Web.Services.FeatureFlags;
using DeckFlow.Web.Services.Http;
using DeckFlow.Web.Services.Manabase;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.Registry;
using RestSharp;
using CoreScryfallCollectionIdentifier = DeckFlow.Core.Normalization.ScryfallCollectionIdentifier;

namespace DeckFlow.CLI;

internal static class RoleFloorResearchCommandRunner
{
    private const double RatioLow = 0.667;
    private const double RatioHigh = 1.5;
    private const double ZThreshold = 2.0;
    // Why: when the corpus 25th percentile is zero, ComputeRatio returns 0.0 and would slide
    // under RatioLow, marking every commander divergent-low; 2.0 cards is the smallest gap worth
    // stating as a floor recommendation, and RFLR-02 requires the whole written bar in one place.
    private const double AbsoluteFloorGap = 2.0;
    private const int BreadthMinimum = 3;
    // Source: .planning/archive/2026-cycles/research/2026-07-16-edhrec-bracket-land-data.md
    // "bracket mean" row. The B1 value carries the archive's own caveat that Teval's 42-land B1
    // cell came from ~50 decks and pushed up a tiny-sample outlier.
    private static readonly IReadOnlyDictionary<int, double> PriorLandBracketMeans = new Dictionary<int, double>
    {
        [1] = 36.3,
        [2] = 35.7,
        [3] = 35.2,
        [4] = 34.5,
        [5] = 30.0,
    };
    // Source: .planning/archive/2026-cycles/research/2026-07-16-edhrec-bracket-land-data.md
    // "bracket SD" row. The B1 value carries the archive's own caveat that Teval's 42-land B1
    // cell came from ~50 decks and pushed up a tiny-sample outlier.
    private static readonly IReadOnlyDictionary<int, double> PriorLandBracketStdDevs = new Dictionary<int, double>
    {
        [1] = 2.24,
        [2] = 1.19,
        [3] = 1.25,
        [4] = 1.20,
        [5] = 2.14,
    };
    // Source: .planning/archive/2026-cycles/research/2026-07-16-edhrec-bracket-land-data.md
    // "bracket mean" / "bracket SD" rows, ALL column.
    private const double PriorLandOverallMean = 34.9;
    private const double PriorLandOverallStdDev = 1.45;
    private const string PriorLandStudyDate = "2026-07-16";
    private const string PriorLandStudyPath = ".planning/archive/2026-cycles/research/2026-07-16-edhrec-bracket-land-data.md";
    private const int LandsCalibrationMinCommanders = 3;
    // Source: DeckFlow.Web/Data/manabase-baseline/latest.json, $.brackets[*].avgLands. This is the
    // live shipped snapshot that resolves Cut Lab's lands floor default today via
    // EdhrecAveragesConverter -> IManabaseBaselineProvider -> CutLabFloorDefaults.ResolveLandsDefault.
    private static readonly IReadOnlyDictionary<int, double> LiveBaselineLandBracketMeans = new Dictionary<int, double>
    {
        [2] = 35.9,
        [3] = 35.5,
        [4] = 34.5,
        [5] = 30.5,
    };
    private const string LiveBaselineSnapshotPath = "DeckFlow.Web/Data/manabase-baseline/latest.json";
    private const string LiveBaselineGeneratedUtc = "2026-07-17T21:38:00Z";
    // Why: the 2026-07-16 prior set the per-cell EDHREC floor at 400 decks backing the cell, and
    // the manifest's min_decks: 8000 is the commander-selection floor for which commanders were
    // fetched, not the per-cell qualifying floor used by this harness.
    private const int EdhrecMinCellDeckCount = 400;
    private const int EdhrecThinBracketThreshold = 50;
    private const int CommanderMembershipMaxConcurrency = 8;
    private const int CommanderMembershipProgressInterval = 200;
    private const int ScryfallRateLimitRetryMaxAttempts = 4;
    private const string NoGoTemplatePath = ".planning/workstreams/cycle21-cut-lab/phases/02-role-floor-divergence-research/NO-GO-TEMPLATE.md";
    private const string CasualBiasArchivePath = ".planning/archive/2026-cycles/quick/260718-nip-investigate-usefulness-of-edhrec-dump-fo/";
    // Why: no line range. The prior value pinned DeckStatClassifier.cs:226-231, which Phase 9.1's
    // widening already invalidated once and which would drift again on the next edit to that file.
    private const string ProtectionClassifierPath = "DeckFlow.Core/Analysis/DeckStatClassifier.cs";
    private const string ProtectionDeltaPath = ".planning/workstreams/cycle21-cut-lab/phases/01.1-plan-role-classifier-heuristic-fixes-fix-the-counters-counte/01.1-02-DELTA.md";
    // Why: the four needles the classifier originally carried, before Phase 9.1 widened
    // DeckStatClassifier.ProtectionOracleNeedles to the corpus-derived 17-needle table. Frozen
    // historical fact describing runs produced before that widening, not a live vocabulary — the
    // shipped needle list is read directly from DeckStatClassifier.ProtectionOracleNeedles below,
    // so this array is not a second copy of it.
    private static readonly string[] ProtectionHistoricalNarrowNeedles =
    [
        "gains hexproof",
        "gains indestructible",
        "gain protection from",
        "phases out",
    ];
    private static readonly ProtectionMissedCardDisclosure[] ProtectionKnownMissedCards =
    [
        new("Swiftfoot Boots", "measured", "Measured through a scratch dotnet harness against the repo code."),
        new("Mother of Runes", "measured", "Measured through the same scratch dotnet harness against the repo code."),
        new("Hexing Squelcher", "measured", "Measured local corpus oracle/type data from _role-floor-research/cards_full.json."),
        new("Goblin Chirurgeon", "measured", "Measured local corpus oracle/type data from _role-floor-research/cards_full.json."),
        new("Lightning Greaves", "inferred", "Inferred, not measured: the delta used the plan-provided oracle text and a reasoned Artifact — Equipment type line because no local facts entry for that card was present in the measured corpus files used here."),
    ];
    private static readonly string[] ProtectionConsumers =
    [
        "InteractionAuditAggregator.cs:58",
        "CutLabRoleAssigner.cs:165",
        "PlanRoleClassifier.cs:236",
    ];
    private static readonly string[] CorpusHygieneUnparsedPayloadFields =
    [
        "deckFormat",
        "theorycrafted",
        "createdAt",
        "updatedAt",
        "viewCount",
        "points",
        "edhBracket",
    ];
    private static readonly TimeSpan HarnessFallbackSearchPacingDelay = TimeSpan.FromMilliseconds(350);

    // Why: the prior five-role list was the pre-Phase-1 taxonomy, including merged
    // "interaction", which CutLabRoleAssigner no longer emits; because the tally loop only
    // increments keys already seeded, that stale key would have silently recorded zero for every
    // deck and every commander. Decision D-C also requires lands and ramp, draw stays in because
    // it is a shipped first-class role, and "other" stays out because its residual-bucket count
    // would measure classifier coverage rather than deck construction.
    private static readonly string[] TargetRoles =
    [
        "lands",
        "ramp",
        "draw",
        "interaction-targeted",
        "interaction-mass",
        "protection",
        "engines",
        "payoffs",
        "wincons",
    ];

    private static readonly int[] DiagnosticThresholds = [15, 20, 25, 30, 40, 50, 75, 100];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(
        string? connectionString,
        int minDeckCount,
        string mode,
        string cardsCachePath,
        string outputPath,
        string outputJsonPath,
        string? edhrecDataPath = null,
        int? commanderLimit = null,
        CancellationToken cancellationToken = default)
    {
        const string connectionStringEnvironmentVariableName = "DECKFLOW_ROLE_FLOOR_CONNECTION_STRING";

        // Why: argv is visible in the process list for the entire multi-hour run, so the
        // environment path avoids forcing the credential into process listings; the flag remains
        // only for backward compatibility and still wins when explicitly supplied.
        string? resolvedConnectionString = RoleFloorProvenance.ResolveConnectionString(
            connectionString,
            Environment.GetEnvironmentVariable(connectionStringEnvironmentVariableName));

        if (string.IsNullOrWhiteSpace(resolvedConnectionString))
        {
            Console.Error.WriteLine(
                FormattableString.Invariant(
                    $"Either --connection-string or the {connectionStringEnvironmentVariableName} environment variable is required."));
            return 1;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.Error.WriteLine("--out is required.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(outputJsonPath))
        {
            Console.Error.WriteLine("--out-json is required.");
            return 1;
        }

        try
        {
            int? activeCommanderLimit = commanderLimit is > 0 ? commanderLimit : null;
            string runTimestampUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            string normalizedConnectionString = PostgresConnectionStringNormalizer.Normalize(resolvedConnectionString);
            string databaseHost = RoleFloorProvenance.DescribeDatabaseHost(normalizedConnectionString);
            string harnessCommitSha = DescribeHarnessCommitSha();
            var connectionInfo = new RelationalDatabaseConnection(
                RelationalDatabaseProvider.Postgres,
                normalizedConnectionString);
            var repository = new CategoryKnowledgeRepository(connectionInfo);
            using var serviceProvider = BuildScryfallServiceProvider();
            await CliFeatureFlagServices.InitializeFeatureFlagsAsync(serviceProvider, cancellationToken).ConfigureAwait(false);
            var resolver = serviceProvider.GetRequiredService<IScryfallCardResolver>();
            ManabaseMode resolvedMode = CutLabRoleAssigner.ResolveMode(mode);
            string? taxonomyError = ValidateTaxonomyAgainstAssigner(resolvedMode);
            if (taxonomyError is not null)
            {
                Console.Error.WriteLine(taxonomyError);
                return 1;
            }

            EdhrecReadResult? edhrecReadResult = null;
            if (!string.IsNullOrWhiteSpace(edhrecDataPath))
            {
                edhrecReadResult = EdhrecCellReader.Read(edhrecDataPath, EdhrecMinCellDeckCount);
                if (edhrecReadResult.Failure is not null)
                {
                    Console.Error.WriteLine(edhrecReadResult.Failure);
                    return 1;
                }
            }

            if (activeCommanderLimit.HasValue)
            {
                Console.WriteLine(FormattableString.Invariant($"Commander limit in effect: {activeCommanderLimit.Value} (--limit {activeCommanderLimit.Value})."));
            }

            List<(string CommanderName, int DeckCount, string? LastProcessedUtc)> commanderRows =
                await LoadCommanderRowsAsync(repository, activeCommanderLimit, cancellationToken).ConfigureAwait(false);

            var commanderDecks = new Dictionary<string, CommanderDeckSet>(StringComparer.OrdinalIgnoreCase);
            var distinctCardNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var commanderDeckSets = new CommanderDeckSet?[commanderRows.Count];
            var distinctCardNameSet = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            int commandersProcessed = 0;
            int commandersWithMembership = 0;
            long rawDecksWithMembership = 0;
            var membershipLoadStopwatch = Stopwatch.StartNew();

            await Parallel.ForEachAsync(
                Enumerable.Range(0, commanderRows.Count),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = CommanderMembershipMaxConcurrency,
                },
                async (index, token) =>
                {
                    string commanderName = commanderRows[index].CommanderName;
                    IReadOnlyList<CategoryDeckMembership> memberships =
                        await repository.GetCategoryDeckMembershipForCommanderAsync(
                            commanderName,
                            boardFilter: "mainboard",
                            cancellationToken: token).ConfigureAwait(false);

                    var rawDecks = memberships
                        .GroupBy(membership => membership.DeckId)
                        .ToDictionary(
                            group => group.Key,
                            group => new HashSet<string>(
                                group.Select(membership => membership.CardName),
                                StringComparer.OrdinalIgnoreCase));

                    foreach (HashSet<string> cardNames in rawDecks.Values)
                    {
                        foreach (string cardName in cardNames)
                        {
                            distinctCardNameSet.TryAdd(cardName, 0);
                        }
                    }

                    if (rawDecks.Count > 0)
                    {
                        Interlocked.Increment(ref commandersWithMembership);
                        Interlocked.Add(ref rawDecksWithMembership, rawDecks.Count);
                    }

                    // RAW N can undercount reality because a processed deck with zero category-tagged
                    // cards is invisible to this reconstruction pipeline, not merely thin.
                    commanderDeckSets[index] = new CommanderDeckSet
                    {
                        CommanderName = commanderName,
                        RawDecks = rawDecks,
                    };

                    int processed = Interlocked.Increment(ref commandersProcessed);
                    if (processed % CommanderMembershipProgressInterval == 0 || processed == commanderRows.Count)
                    {
                        Console.WriteLine(
                            FormattableString.Invariant(
                                $"Loaded commander memberships {processed}/{commanderRows.Count} in {membershipLoadStopwatch.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)} (commandersWithMembership={Volatile.Read(ref commandersWithMembership)}, rawDecks={Volatile.Read(ref rawDecksWithMembership)})."));
                    }
                }).ConfigureAwait(false);

            foreach (CommanderDeckSet commanderDeckSet in commanderDeckSets.Where(set => set is not null).Cast<CommanderDeckSet>())
            {
                commanderDecks[commanderDeckSet.CommanderName] = commanderDeckSet;
            }

            foreach (string cardName in distinctCardNameSet.Keys)
            {
                distinctCardNames.Add(cardName);
            }

            if (edhrecReadResult is not null)
            {
                foreach (EdhrecCell cell in edhrecReadResult.Cells)
                {
                    foreach (EdhrecCard card in cell.Cards)
                    {
                        distinctCardNames.Add(card.Name);
                    }
                }
            }

            IReadOnlyDictionary<long, string?> contentHashes = await repository
                .GetContentHashesByIdsAsync(
                    commanderDecks.Values.SelectMany(set => set.RawDecks.Keys).Distinct().ToList(),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (CommanderDeckSet commander in commanderDecks.Values)
            {
                commander.RepresentativeDecks = DeduplicateDecks(commander.RawDecks, contentHashes);
            }

            CardResolutionResult cardResolution = await ResolveCardsAsync(
                resolver,
                distinctCardNames,
                cardsCachePath,
                cancellationToken).ConfigureAwait(false);

            List<EdhrecRolePointEstimate> edhrecPointEstimates = [];
            List<EdhrecBracketCoverage> edhrecBracketCoverage = [];
            List<EdhrecLandSelfCheck> edhrecLandSelfChecks = [];
            int edhrecParseFailureCount = 0;
            int edhrecCardCountAnomalyCount = 0;
            string? edhrecMinSaveDate = null;
            string? edhrecMaxSaveDate = null;

            foreach (CommanderDeckSet commander in commanderDecks.Values)
            {
                foreach ((long deckId, HashSet<string> cardNames) in commander.RepresentativeDecks)
                {
                    var roleCounts = TargetRoles.ToDictionary(role => role, _ => 0, StringComparer.Ordinal);
                    foreach (string cardName in cardNames)
                    {
                        if (!cardResolution.ResolvedCards.TryGetValue(cardName, out ScryfallCardData? card))
                        {
                            continue;
                        }

                        // Commander is singleton for every nonland card that can plausibly earn one
                        // of the target roles, so the research harness classifies quantity as 1.
                        CardFact fact = ScryfallCardFactMapper.ToCardFact(card, quantity: 1, isCommander: false);
                        // Commander Spellbook combo-piece resolution is out of scope here, so this
                        // can only undercount wincons, never overcount them.
                        IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(fact, [], isComboPiece: false, resolvedMode);
                        foreach (string role in roles)
                        {
                            if (roleCounts.ContainsKey(role))
                            {
                                roleCounts[role]++;
                            }
                        }
                    }

                    commander.RepresentativeRoleCounts[deckId] = roleCounts;
                }
            }

            if (edhrecReadResult is not null)
            {
                edhrecParseFailureCount = edhrecReadResult.Cells.Sum(cell => cell.ParseFailures.Count);
                edhrecCardCountAnomalyCount = edhrecReadResult.CardCountAnomalies.Count;
                edhrecMinSaveDate = edhrecReadResult.Cells.Count == 0
                    ? null
                    : edhrecReadResult.Cells.Min(cell => cell.MinSaveDate);
                edhrecMaxSaveDate = edhrecReadResult.Cells.Count == 0
                    ? null
                    : edhrecReadResult.Cells.Max(cell => cell.MaxSaveDate);

                foreach (EdhrecCell cell in edhrecReadResult.Cells)
                {
                    IReadOnlyDictionary<string, int> roleCounts = EdhrecRoleTally.TallyRoleCounts(
                        TargetRoles,
                        cell.Cards
                            .Where(cardEntry => cardResolution.ResolvedCards.TryGetValue(cardEntry.Name, out _))
                            .Select(cardEntry =>
                            {
                                ScryfallCardData card = cardResolution.ResolvedCards[cardEntry.Name];
                                CardFact fact = ScryfallCardFactMapper.ToCardFact(card, quantity: 1, isCommander: false);
                                IReadOnlyList<string> roles = CutLabRoleAssigner.AssignRoles(fact, [], isComboPiece: false, resolvedMode);
                                return (Roles: roles, Quantity: cardEntry.Quantity);
                            }));

                    int harnessLandCount = roleCounts["lands"];
                    // Why: EDHREC's aggregate land count and the harness's
                    // CutLabLockRules.IsLand(typeLine) || fact.HasLandFace test can legitimately
                    // disagree on modal double-faced cards, so a mismatch is a methodology finding
                    // for the later lands verdict rather than a run failure.
                    edhrecLandSelfChecks.Add(new EdhrecLandSelfCheck
                    {
                        CellId = BuildEdhrecCellId(cell),
                        EdhrecLandCount = cell.EdhrecLandCount,
                        HarnessLandCount = harnessLandCount,
                        Delta = harnessLandCount - cell.EdhrecLandCount,
                    });

                    foreach (string role in TargetRoles)
                    {
                        edhrecPointEstimates.Add(new EdhrecRolePointEstimate
                        {
                            Source = RoleFloorSource.Edhrec,
                            Role = role,
                            CommanderName = cell.Commander,
                            BracketSlug = cell.Bracket,
                            BracketIndex = cell.BracketIndex,
                            Count = roleCounts[role],
                            DeckCount = cell.NDecks,
                            Qualifies = cell.Qualifies,
                        });
                    }
                }

                edhrecBracketCoverage = BuildEdhrecBracketCoverage(edhrecReadResult);
            }

            Dictionary<string, RoleBaseline> corpusBaseline = BuildCorpusBaseline(commanderDecks.Values);
            var qualifyingCommanders = new Dictionary<string, CommanderResearch>(StringComparer.OrdinalIgnoreCase);
            var postgresDistributions = new List<PostgresRoleDistribution>();
            foreach (CommanderDeckSet commander in commanderDecks.Values.Where(set => set.DedupedN >= minDeckCount))
            {
                var commanderResearch = new CommanderResearch
                {
                    CommanderName = commander.CommanderName,
                    RawN = commander.RawN,
                    N = commander.DedupedN,
                };

                foreach (string role in TargetRoles)
                {
                    List<double> perDeckCounts = commander.RepresentativeRoleCounts.Values
                        .Select(counts => (double)counts[role])
                        .ToList();
                    double commanderMean = perDeckCounts.Count == 0 ? 0.0 : perDeckCounts.Average();
                    double commanderP25 = perDeckCounts.Count == 0
                        ? 0.0
                        : RoleFloorDivergenceStats.ComputePercentile(perDeckCounts, 0.25);
                    RoleBaseline baseline = corpusBaseline[role];
                    commanderResearch.Roles[role] = new CommanderRoleStat
                    {
                        Mean = commanderMean,
                        P25 = commanderP25,
                        Ratio = RoleFloorDivergenceStats.ComputeRatio(commanderMean, baseline.Mean),
                        ZScore = RoleFloorDivergenceStats.ComputeZScore(commanderMean, baseline.Mean, baseline.StdDev, commander.DedupedN),
                        CohensD = RoleFloorDivergenceStats.ComputeCohensD(commanderMean, baseline.Mean, baseline.StdDev),
                        ClearsBar = RoleFloorDivergenceStats.ClearsFloorBar(
                            commander.DedupedN,
                            commanderP25,
                            baseline.P25,
                            commanderMean,
                            baseline.Mean,
                            baseline.StdDev,
                            minDeckCount,
                            ratioLow: RatioLow,
                            ratioHigh: RatioHigh,
                            zThreshold: ZThreshold,
                            absoluteFloorGap: AbsoluteFloorGap),
                    };

                    postgresDistributions.Add(new PostgresRoleDistribution
                    {
                        Source = RoleFloorSource.Postgres,
                        Role = role,
                        CommanderName = commander.CommanderName,
                        DeckCount = commander.RawN,
                        Mean = commanderMean,
                        P25 = commanderP25,
                        StdDev = baseline.StdDev,
                        Ratio = commanderResearch.Roles[role].Ratio,
                        ZScore = commanderResearch.Roles[role].ZScore,
                        CohensD = commanderResearch.Roles[role].CohensD,
                        ClearsBar = commanderResearch.Roles[role].ClearsBar,
                    });
                }

                qualifyingCommanders[commander.CommanderName] = commanderResearch;
            }

            Dictionary<int, int> thresholdCounts = DiagnosticThresholds.ToDictionary(
                threshold => threshold,
                threshold => commanderDecks.Values.Count(set => set.DedupedN >= threshold));
            if (RoleFloorGuards.HasNoQualifyingCommanders(qualifyingCommanders.Count))
            {
                // Why: HasNoQualifyingCommanders is unit-tested in Core, but only plan 02-08's
                // --min-decks 999999 smoke run proves this guard still sits before artifact writes
                // rather than after BuildGoNoGo/WriteFindingsFiles.
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"Zero commanders met the minimum deduped deck count of {minDeckCount}."));
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"Commander rows enumerated: {commanderRows.Count}."));
                Console.Error.WriteLine("ThresholdCounts:");
                foreach ((int threshold, int count) in thresholdCounts.OrderBy(pair => pair.Key))
                {
                    Console.Error.WriteLine(FormattableString.Invariant($"  {threshold}: {count}"));
                }

                Console.Error.WriteLine("NO findings artifact was written.");
                return 2;
            }

            var computation = new ResearchComputation
            {
                MinDeckCount = minDeckCount,
                DatabaseHost = databaseHost,
                RunTimestampUtc = runTimestampUtc,
                HarnessCommitSha = harnessCommitSha,
                CommanderLimit = activeCommanderLimit,
                CommandersEnumerated = commanderRows.Count,
                RawDeckCount = commanderDecks.Values.Sum(set => set.RawN),
                DedupedDeckCount = commanderDecks.Values.Sum(set => set.DedupedN),
                UnresolvedNotFoundCount = cardResolution.UnresolvedNotFoundCount,
                UnresolvedRateLimitedAfterRetryCount = cardResolution.UnresolvedRateLimitedAfterRetryCount,
                PostgresCoverage = new PostgresCoverage
                {
                    CommandersEnumerated = commanderRows.Count,
                    CommandersWithMembership = commandersWithMembership,
                    RawDeckCount = commanderDecks.Values.Sum(set => set.RawN),
                    DedupedDeckCount = commanderDecks.Values.Sum(set => set.DedupedN),
                    CommandersQualifying = qualifyingCommanders.Count,
                    UnresolvedNotFoundCount = cardResolution.UnresolvedNotFoundCount,
                    UnresolvedRateLimitedAfterRetryCount = cardResolution.UnresolvedRateLimitedAfterRetryCount,
                },
                CorpusBaseline = corpusBaseline,
                Commanders = qualifyingCommanders,
                PostgresDistributions = postgresDistributions,
                EdhrecPointEstimates = edhrecPointEstimates,
                EdhrecCoverage = new EdhrecCoverage
                {
                    CellsFetched = edhrecReadResult?.Cells.Count ?? 0,
                    CellsQualifying = edhrecReadResult?.Cells.Count(cell => cell.Qualifies) ?? 0,
                    CellsMissing = edhrecReadResult is null
                        ? 0
                        : edhrecReadResult.MissingCells.Count + edhrecReadResult.InvalidCells.Count,
                    InvalidCells = edhrecReadResult?.InvalidCells.Count ?? 0,
                    UnexpectedCells = edhrecReadResult?.UnexpectedCells.Count ?? 0,
                    CommandersReached = edhrecReadResult?.Cells
                        .Select(cell => cell.Slug)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() ?? 0,
                    MinCellDeckCount = EdhrecMinCellDeckCount,
                    MinSaveDate = edhrecMinSaveDate,
                    MaxSaveDate = edhrecMaxSaveDate,
                    Brackets = edhrecBracketCoverage,
                    LandSelfChecks = edhrecLandSelfChecks,
                },
                EdhrecParseFailureCount = edhrecParseFailureCount,
                EdhrecCardCountAnomalyCount = edhrecCardCountAnomalyCount,
                ThresholdCounts = thresholdCounts,
            };

            computation.GoNoGo = BuildGoNoGo(computation.Commanders);
            WriteFindingsFiles(computation, outputPath, outputJsonPath);

            string goRoles = string.Join(", ", computation.GoNoGo
                .Where(pair => string.Equals(pair.Value.JsonStatus, "go", StringComparison.Ordinal))
                .Select(pair => pair.Key));
            string signalRoles = string.Join(", ", computation.GoNoGo
                .Where(pair => string.Equals(pair.Value.JsonStatus, "signal-present", StringComparison.Ordinal))
                .Select(pair => pair.Key));

            Console.WriteLine(
                FormattableString.Invariant(
                    $"RawDecks={computation.RawDeckCount}, DedupedDecks={computation.DedupedDeckCount}, Commanders={computation.CommandersEnumerated}, QualifyingCommanders={computation.Commanders.Count}, GoRoles={(string.IsNullOrWhiteSpace(goRoles) ? "none" : goRoles)}, SignalRoles={(string.IsNullOrWhiteSpace(signalRoles) ? "none" : signalRoles)}"));
            ScryfallCacheStatisticsReporter.Report(serviceProvider.GetRequiredService<ScryfallCollectionCardCache>());
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static ServiceProvider BuildScryfallServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddDeckFlowHttpClients();
        services.AddDeckFlowResiliencePipelines();
        services.AddCliFeatureFlags();
        services.AddSingleton<IScryfallRestClientFactory, ScryfallRestClientFactory>();
        services.AddSingleton(serviceProvider => new ScryfallCollectionCardCache(
            serviceProvider.GetService<IFeatureFlagCache>(),
            serviceProvider.GetService<ILogger<ScryfallCollectionCardCache>>()));
        services.AddSingleton<IScryfallCardResolver>(serviceProvider =>
            new ScryfallCardResolver(
                serviceProvider.GetRequiredService<IScryfallRestClientFactory>(),
                serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>(),
                serviceProvider.GetRequiredService<ScryfallCollectionCardCache>()));
        return services.BuildServiceProvider();
    }

    private static string DescribeHarnessCommitSha()
    {
        try
        {
            (int revParseExitCode, string revParseStdout) = RunGitCommand("rev-parse", "--short", "HEAD");
            int effectiveExitCode = revParseExitCode;
            string? statusPorcelainStdout = null;
            if (revParseExitCode == 0)
            {
                (int statusExitCode, string statusStdout) = RunGitCommand("status", "--porcelain");
                effectiveExitCode = statusExitCode;
                statusPorcelainStdout = statusStdout;
            }

            return RoleFloorProvenance.FormatCommitSha(effectiveExitCode, revParseStdout, statusPorcelainStdout);
        }
        catch
        {
            return RoleFloorProvenance.FormatCommitSha(exitCode: 1, revParseStdout: null, statusPorcelainStdout: null);
        }
    }

    private static (int ExitCode, string Stdout) RunGitCommand(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
        };

        process.Start();
        string stdout = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // Why: the harness may run from a dirty worktree, including its own local edits, and an
        // artifact that claims a clean SHA in that state would misrepresent the code that produced it.
        return (process.ExitCode, stdout);
    }

    private static async Task<List<(string CommanderName, int DeckCount, string? LastProcessedUtc)>> LoadCommanderRowsAsync(
        CategoryKnowledgeRepository repository,
        int? commanderLimit,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string CommanderName, int DeckCount, string? LastProcessedUtc)>();
        for (int page = 1; ; page++)
        {
            IReadOnlyList<(string CommanderName, int DeckCount, string? LastProcessedUtc)> pageRows =
                await repository.GetPagedProcessedCommanderRowsAsync(page, 200, cancellationToken).ConfigureAwait(false);
            if (pageRows.Count == 0)
            {
                break;
            }

            rows.AddRange(pageRows);
            if (RoleFloorGuards.HasReachedCommanderLimit(rows.Count, commanderLimit))
            {
                rows = rows.Take(commanderLimit!.Value).ToList();
                // Why: plan 02-11's smoke-run proof is about the exit-2 guard staying where it is,
                // so paging must stop early to make MIN_DECKS=999999 LIMIT=50 finish in seconds.
                break;
            }

            if (page % 5 == 0)
            {
                Console.WriteLine($"Paged {page} commander batches ({rows.Count} commanders so far).");
            }
        }

        return rows;
    }

    private static Dictionary<long, HashSet<string>> DeduplicateDecks(
        IReadOnlyDictionary<long, HashSet<string>> rawDecks,
        IReadOnlyDictionary<long, string?> contentHashes)
    {
        var representatives = new Dictionary<long, HashSet<string>>();

        foreach (IGrouping<string?, long> hashGroup in rawDecks.Keys.GroupBy(
                     deckId => contentHashes.TryGetValue(deckId, out string? hash) ? hash : null,
                     StringComparer.Ordinal))
        {
            if (hashGroup.Key is null)
            {
                foreach (long deckId in hashGroup)
                {
                    representatives[deckId] = rawDecks[deckId];
                }

                continue;
            }

            long representativeDeckId = hashGroup.Min();
            representatives[representativeDeckId] = rawDecks[representativeDeckId];
        }

        return representatives;
    }

    private static async Task<CardResolutionResult> ResolveCardsAsync(
        IScryfallCardResolver resolver,
        IReadOnlyCollection<string> distinctCardNames,
        string cardsCachePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(distinctCardNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(cardsCachePath);

        string cacheDirectory = Path.GetDirectoryName(cardsCachePath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(cacheDirectory);

        var cache = LoadCardCache(cardsCachePath);
        var unresolvedNotFoundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedRateLimitedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> uncachedNames = distinctCardNames
            .Where(name => !cache.ContainsKey(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int offset = 0; offset < uncachedNames.Count; offset += 75)
        {
            List<string> batchNames = uncachedNames.Skip(offset).Take(75).ToList();
            string[] batchIdentifiers = batchNames
                .Select(CoreScryfallCollectionIdentifier.ToFaceIdentifier)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var request = new RestRequest("cards/collection", Method.Post);
            // Why: Scryfall cards/collection name identifiers match a single face name; combined A // B returns not_found.
            request.AddJsonBody(new { identifiers = batchIdentifiers.Select(cardName => (object)new { name = cardName }).ToArray() });

            RestResponse<ScryfallCollectionResponse>? response = await ExecuteWithScryfall429RetryAsync(
                operationName: $"cards/collection batch {offset / 75 + 1}",
                operation: token => resolver.ExecuteCollectionAsync(request, token),
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                foreach (string cardName in batchNames)
                {
                    unresolvedRateLimitedNames.Add(cardName);
                }

                SnapshotFileWriter.WriteLfFile(cardsCachePath, JsonSerializer.Serialize(cache, JsonOptions));
                continue;
            }

            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices || response.Data is null)
            {
                throw new HttpRequestException(
                    $"Scryfall card lookup (cards/collection) returned HTTP {(int)response.StatusCode} during role-floor research.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            var cardsByNormalizedName = response.Data.Data
                .GroupBy(card => CardNormalizer.Normalize(card.Name), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (string cardName in batchNames)
            {
                string normalizedName = CardNormalizer.Normalize(cardName);
                ScryfallCard? resolvedCard = cardsByNormalizedName.TryGetValue(normalizedName, out ScryfallCard? hit)
                    ? hit
                    : await ExecuteWithScryfall429RetryAsync(
                        operationName: $"fallback search for {cardName}",
                        operation: token => resolver.SearchFallbackCardAsync(cardName, token),
                        cancellationToken).ConfigureAwait(false);

                if (resolvedCard is null)
                {
                    if (cardsByNormalizedName.ContainsKey(normalizedName))
                    {
                        continue;
                    }

                    if (unresolvedRateLimitedNames.Contains(cardName))
                    {
                        continue;
                    }

                    unresolvedNotFoundNames.Add(cardName);
                    continue;
                }

                cache[cardName] = ScryfallCardDataMapper.ToCardData(resolvedCard);

                if (!ReferenceEquals(resolvedCard, hit))
                {
                    await Task.Delay(HarnessFallbackSearchPacingDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            SnapshotFileWriter.WriteLfFile(cardsCachePath, JsonSerializer.Serialize(cache, JsonOptions));
        }

        string unresolvedPath = Path.Combine(cacheDirectory, "unresolved-cards.txt");
        string unresolvedNotFoundPath = Path.Combine(cacheDirectory, "unresolved-not-found-cards.txt");
        string unresolvedRateLimitedPath = Path.Combine(cacheDirectory, "unresolved-rate-limited-after-retry-cards.txt");
        SnapshotFileWriter.WriteLfFile(
            unresolvedPath,
            string.Join(
                '\n',
                unresolvedNotFoundNames
                    .Select(name => $"not_found\t{name}")
                    .Concat(unresolvedRateLimitedNames.Select(name => $"rate_limited_after_retry\t{name}"))
                    .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)));
        SnapshotFileWriter.WriteLfFile(
            unresolvedNotFoundPath,
            string.Join('\n', unresolvedNotFoundNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)));
        SnapshotFileWriter.WriteLfFile(
            unresolvedRateLimitedPath,
            string.Join('\n', unresolvedRateLimitedNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)));
        return new CardResolutionResult(
            cache,
            unresolvedNotFoundNames.Count,
            unresolvedRateLimitedNames.Count);
    }

    private static async Task<T?> ExecuteWithScryfall429RetryAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);

        for (int attempt = 1; attempt <= ScryfallRateLimitRetryMaxAttempts; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt == ScryfallRateLimitRetryMaxAttempts)
                {
                    Console.WriteLine(
                        FormattableString.Invariant(
                            $"Scryfall 429 persisted for {operationName}; excluding from tallies after {attempt} attempts."));
                    return default;
                }

                TimeSpan delay = ComputeScryfall429Backoff(attempt);
                Console.WriteLine(
                    FormattableString.Invariant(
                        $"Scryfall 429 during {operationName}; retrying in {delay.TotalSeconds:0.#}s (attempt {attempt}/{ScryfallRateLimitRetryMaxAttempts})."));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return default;
    }

    private static TimeSpan ComputeScryfall429Backoff(int attempt)
    {
        int[] delaySeconds = [5, 8, 12, 15];
        int safeAttemptIndex = Math.Clamp(attempt - 1, 0, delaySeconds.Length - 1);
        return TimeSpan.FromSeconds(delaySeconds[safeAttemptIndex]);
    }

    private static Dictionary<string, ScryfallCardData> LoadCardCache(string cardsCachePath)
    {
        if (!File.Exists(cardsCachePath))
        {
            return new Dictionary<string, ScryfallCardData>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, ScryfallCardData>? persisted =
            JsonSerializer.Deserialize<Dictionary<string, ScryfallCardData>>(File.ReadAllText(cardsCachePath), JsonOptions);
        return persisted is null
            ? new Dictionary<string, ScryfallCardData>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ScryfallCardData>(persisted, StringComparer.OrdinalIgnoreCase);
    }

    // Why: this seam is internal so DeckFlow.Core.Tests can exercise the real CLI guard through the
    // existing InternalsVisibleTo, matching the project rule that CLI additions carry Core test coverage.
    internal static string? ValidateTaxonomyAgainstAssigner(ManabaseMode mode)
    {
        string? roleKeyReadError = RoleFloorGuards.TryReadShippedRoleKeys(
            typeof(CutLabRoleAssigner),
            "RoleKeys",
            out string[]? shippedRoleKeys);
        if (roleKeyReadError is not null)
        {
            return roleKeyReadError;
        }

        // Why: CutLabRoleAssigner.RoleKeys is private static readonly, so the harness reflects the
        // authoritative shipped list instead of hand-copying it; "other" is a separate const
        // outside RoleKeys and is deliberately excluded per D-01; and this turns silent taxonomy
        // drift from a corpus-wide zero into a startup abort for any of the nine shipped keys.
        CardFact[] probes =
        [
            new()
            {
                Name = "Forest",
                Quantity = 1,
                TypeLine = "Basic Land — Forest",
                // Source: DeckFlow.Web.Tests/CutLabRoleAssignerTests.AssignRoles_Forest_MapsToExactlyLands
            },
            new()
            {
                Name = "Cultivate",
                Quantity = 1,
                TypeLine = "Sorcery",
                OracleText = "Search your library for up to two basic land cards, reveal those cards, put one onto the battlefield tapped and the other into your hand, then shuffle.",
                // Source: DeckFlow.Web.Tests/CutLabRoleAssignerTests.AssignRoles_Cultivate_MapsToRampOnly
            },
            new()
            {
                Name = "Quick Study",
                Quantity = 1,
                TypeLine = "Instant",
                OracleText = "Draw two cards.",
                // Source: DeckFlow.Web.Tests/CutLabRoleAssignerTests.AssignRoles_OneShotDrawSpell_NotEngine
            },
            new()
            {
                Name = "Swords to Plowshares",
                Quantity = 1,
                TypeLine = "Instant",
                OracleText = "Exile target creature. Its controller gains life equal to its power.",
                // Source: DeckFlow.Web.Tests/CutLabRoleAssignerTests.AssignRoles_SwordsToPlowshares_IsTargetedOnlyInCasualViaPreGateSignal
            },
            new()
            {
                Name = "Wrath of God",
                Quantity = 1,
                TypeLine = "Sorcery",
                OracleText = "Destroy all creatures. They can't be regenerated.",
                // Source: DeckFlow.Web.Tests/CutLabRoleAssignerTests.AssignRoles_WipeByOracleHeuristic_IsMassOnly
            },
            new()
            {
                Name = "Protection Wand",
                Quantity = 1,
                TypeLine = "Artifact",
                OracleText = "{T}: Target creature you control gains hexproof until end of turn.",
                // Source: DeckFlow.Web.Tests/Manabase/PlanRoleClassifierTests.Classify_ProtectionPermanent_IsInteractionAndNothingElse
            },
            new()
            {
                Name = "Phyrexian Arena",
                Quantity = 1,
                TypeLine = "Enchantment",
                OracleText = "At the beginning of your upkeep, draw a card and you lose 1 life.",
                // Source: DeckFlow.Web.Tests/CutLabRoleAssignerTests.AssignRoles_PermanentDrawEngine_IsEngine;
                // the guard passes empty categories, and this oracle text alone satisfies the heuristic path.
            },
            new()
            {
                Name = "Avatar Finisher",
                Quantity = 1,
                TypeLine = "Creature — Avatar",
                OracleText = "Whenever this attacks, each opponent loses 3 life.",
                // Source: DeckFlow.Web.Tests/Manabase/PlanRoleClassifierTests.Classify_PermanentPayoff_IsKept;
                // the guard passes empty categories, and this oracle text alone satisfies the heuristic path.
            },
            new()
            {
                Name = "Torment of Hailfire",
                Quantity = 1,
                TypeLine = "Sorcery",
                OracleText = "Repeat the following process X times. Each opponent loses 3 life unless that player sacrifices a nonland permanent or discards a card.",
                // Source: DeckFlow.Web.Tests/CutLabRoleAssignerTests.AssignRoles_TormentOfHailfire_IsWinconDespitePlanRolePermanentGate;
                // the guard passes empty categories, and this oracle text alone satisfies the heuristic path.
            },
        ];

        var emittedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (CardFact probe in probes)
        {
            foreach (string role in CutLabRoleAssigner
                         .AssignRoles(probe, [], isComboPiece: false, mode))
            {
                emittedKeys.Add(role);
            }
        }

        return RoleFloorGuards.FindTaxonomyDrift(shippedRoleKeys!, TargetRoles, emittedKeys, residualRoleKey: "other");
    }

    private static Dictionary<string, RoleBaseline> BuildCorpusBaseline(IEnumerable<CommanderDeckSet> commanderDecks)
    {
        var perRoleCounts = TargetRoles.ToDictionary(
            role => role,
            _ => new List<double>(),
            StringComparer.Ordinal);

        foreach (Dictionary<string, int> deckCounts in commanderDecks.SelectMany(set => set.RepresentativeRoleCounts.Values))
        {
            foreach (string role in TargetRoles)
            {
                perRoleCounts[role].Add(deckCounts[role]);
            }
        }

        var baseline = new Dictionary<string, RoleBaseline>(StringComparer.Ordinal);
        foreach (string role in TargetRoles)
        {
            List<double> counts = perRoleCounts[role];
            if (counts.Count == 0)
            {
                baseline[role] = new RoleBaseline();
                continue;
            }

            double mean = counts.Average();
            baseline[role] = new RoleBaseline
            {
                Mean = mean,
                StdDev = ComputePopulationStdDev(counts, mean),
                P25 = RoleFloorDivergenceStats.ComputePercentile(counts, 0.25),
            };
        }

        return baseline;
    }

    private static List<EdhrecBracketCoverage> BuildEdhrecBracketCoverage(EdhrecReadResult readResult)
    {
        ArgumentNullException.ThrowIfNull(readResult);

        var brackets = new List<EdhrecBracketCoverage>(readResult.Brackets.Count);
        for (int index = 0; index < readResult.Brackets.Count; index++)
        {
            string bracketSlug = readResult.Brackets[index];
            List<EdhrecCell> bracketCells = readResult.Cells
                .Where(cell => string.Equals(cell.Bracket, bracketSlug, StringComparison.Ordinal))
                .ToList();
            int qualifyingCount = bracketCells.Count(cell => cell.Qualifies);

            brackets.Add(new EdhrecBracketCoverage
            {
                BracketSlug = bracketSlug,
                BracketIndex = index + 1,
                CellsFetched = bracketCells.Count,
                CellsQualifying = qualifyingCount,
                MedianBackingDeckCount = bracketCells.Count == 0
                    ? 0.0
                    : RoleFloorDivergenceStats.ComputePercentile(
                        bracketCells.Select(cell => (double)cell.NDecks).ToList(),
                        0.5),
                SupportLabel = BuildEdhrecSupportLabel(qualifyingCount),
            });
        }

        return brackets;
    }

    private static string BuildEdhrecSupportLabel(int qualifyingCount)
    {
        if (qualifyingCount <= 1)
        {
            return "NOT REPORTED — insufficient cells";
        }

        if (qualifyingCount < EdhrecThinBracketThreshold)
        {
            return FormattableString.Invariant($"THIN — {qualifyingCount} qualifying cells");
        }

        // Why: on the 2026-07-27 corpus this yields B1 NOT REPORTED (1 qualifying cell of 305)
        // and B5 THIN (40), so a one-cell bracket figure is treated as a single deck's number
        // wearing the costume of an average rather than presented as supported. That matches the
        // independent B1 omission already present in ManabaseAnalysisService.cs:603-605 and the
        // committed DeckFlow.Web/Data/manabase-baseline/latest.json snapshot.
        return "reported";
    }

    private static string BuildEdhrecCellId(EdhrecCell cell)
        => FormattableString.Invariant($"{cell.Slug}__{cell.Bracket}");

    private static Dictionary<string, RoleOutcome> BuildGoNoGo(IReadOnlyDictionary<string, CommanderResearch> qualifyingCommanders)
    {
        var outcomes = new Dictionary<string, RoleOutcome>(StringComparer.Ordinal);
        foreach (string role in TargetRoles)
        {
            List<string> citingCommanders = qualifyingCommanders.Values
                .Where(commander => commander.Roles[role].ClearsBar)
                .OrderByDescending(commander => commander.N)
                .ThenBy(commander => commander.CommanderName, StringComparer.Ordinal)
                .Select(commander => commander.CommanderName)
                .ToList();

            if (citingCommanders.Count >= BreadthMinimum)
            {
                outcomes[role] = new RoleOutcome
                {
                    MarkdownStatus = "go",
                    JsonStatus = "go",
                    CitingCommanders = citingCommanders,
                    ClearingCommanderCount = citingCommanders.Count,
                };
                continue;
            }

            if (citingCommanders.Count > 0)
            {
                outcomes[role] = new RoleOutcome
                {
                    MarkdownStatus = "signal present but insufficient breadth",
                    JsonStatus = "signal-present",
                    CitingCommanders = citingCommanders,
                    ClearingCommanderCount = citingCommanders.Count,
                };
                continue;
            }

            outcomes[role] = new RoleOutcome
            {
                MarkdownStatus = "no-go",
                JsonStatus = "no-go",
                CitingCommanders = [],
                ClearingCommanderCount = 0,
            };
        }

        return outcomes;
    }

    private static string ClassifyLandsCalibration(ResearchComputation computation)
    {
        ArgumentNullException.ThrowIfNull(computation);

        RoleBaseline landsBaseline = computation.CorpusBaseline["lands"];
        if (computation.Commanders.Count < LandsCalibrationMinCommanders || landsBaseline.Mean <= 0.0)
        {
            return "insufficient data";
        }

        string landsStatus = computation.GoNoGo["lands"].JsonStatus;
        return landsStatus switch
        {
            // Why: the 2026-07-16 prior concluded commander identity barely moves land count, so a
            // no-go on lands is agreement with that prior and a go is disagreement with it.
            "no-go" => "reproduces",
            // Why: signal-present means the harness itself said the breadth was insufficient, so
            // routing it to contradicts would assert that a ~50-commander study was wrong on the
            // strength of one or two commanders, on the very role chosen to tell us whether this
            // harness can be trusted. contradicts requires the SAME breadth bar (BreadthMinimum)
            // that earns a role a Phase 3 go.
            "signal-present" => "insufficient data",
            "go" when computation.GoNoGo["lands"].ClearingCommanderCount >= BreadthMinimum => "contradicts",
            "go" => "insufficient data",
            _ => throw new InvalidOperationException(
                FormattableString.Invariant($"Unexpected lands go/no-go status '{landsStatus}'."))
        };
    }

    private static void WriteFindingsFiles(ResearchComputation computation, string outputPath, string outputJsonPath)
    {
        string? markdownDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(markdownDirectory))
        {
            Directory.CreateDirectory(markdownDirectory);
        }

        string? jsonDirectory = Path.GetDirectoryName(outputJsonPath);
        if (!string.IsNullOrWhiteSpace(jsonDirectory))
        {
            Directory.CreateDirectory(jsonDirectory);
        }

        SnapshotFileWriter.WriteLfFile(outputPath, BuildMarkdownReport(computation));
        SnapshotFileWriter.WriteLfFile(outputJsonPath, JsonSerializer.Serialize(BuildJsonPayload(computation), CreateResearchJsonOptions()));
    }

    private static string BuildMarkdownReport(ResearchComputation computation)
    {
        var builder = new StringBuilder();
        IReadOnlyList<string> provenanceWarnings = BuildArtifactProvenanceWarnings(computation);

        builder.AppendLine("# Role-Floor Divergence Research");
        builder.AppendLine();
        builder.AppendLine("## Run Provenance");
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("|-------|-------|");
        builder.AppendLine($"| Database Host | {EscapePipe(computation.DatabaseHost)} |");
        builder.AppendLine($"| Run Timestamp (UTC) | {EscapePipe(computation.RunTimestampUtc)} |");
        builder.AppendLine($"| Harness Commit SHA | {EscapePipe(computation.HarnessCommitSha)} |");
        builder.AppendLine(FormattableString.Invariant($"| Commanders Enumerated | {computation.CommandersEnumerated} |"));
        builder.AppendLine(FormattableString.Invariant($"| Raw Deck Count | {computation.RawDeckCount} |"));
        builder.AppendLine(FormattableString.Invariant($"| Deduped Deck Count | {computation.DedupedDeckCount} |"));
        builder.AppendLine(FormattableString.Invariant($"| Minimum Deck Count | {computation.MinDeckCount} |"));
        if (computation.CommanderLimit.HasValue)
        {
            builder.AppendLine(FormattableString.Invariant($"| Commander Limit | {FormatCommanderLimit(computation.CommanderLimit)} |"));
        }
        foreach (string warning in provenanceWarnings)
        {
            if (warning.StartsWith("limited run:", StringComparison.Ordinal))
            {
                builder.AppendLine($"> **WARNING — limited run:** {warning["limited run:".Length..].TrimStart()}");
                continue;
            }

            builder.AppendLine($"> **WARNING — provenance degraded:** {warning}");
        }

        builder.AppendLine();
        builder.AppendLine("## Corpus Coverage");
        builder.AppendLine("### Postgres (within-commander distributions)");
        builder.AppendLine("| Metric | Value |");
        builder.AppendLine("|--------|------:|");
        builder.AppendLine(FormattableString.Invariant($"| Commanders enumerated | {computation.PostgresCoverage.CommandersEnumerated} |"));
        builder.AppendLine(FormattableString.Invariant($"| Commanders with membership | {computation.PostgresCoverage.CommandersWithMembership} |"));
        builder.AppendLine(FormattableString.Invariant($"| Raw deck count | {computation.PostgresCoverage.RawDeckCount} |"));
        builder.AppendLine(FormattableString.Invariant($"| Deduped deck count | {computation.PostgresCoverage.DedupedDeckCount} |"));
        builder.AppendLine(FormattableString.Invariant($"| Commanders qualifying at DEDUPED N >= {computation.MinDeckCount} | {computation.PostgresCoverage.CommandersQualifying} |"));
        builder.AppendLine(FormattableString.Invariant($"| Unresolved cards (not_found) | {computation.PostgresCoverage.UnresolvedNotFoundCount} |"));
        builder.AppendLine(FormattableString.Invariant($"| Unresolved cards (rate_limited_after_retry) | {computation.PostgresCoverage.UnresolvedRateLimitedAfterRetryCount} |"));
        builder.AppendLine(FormattableString.Invariant($"| Unresolved cards (total) | {computation.PostgresCoverage.UnresolvedCardCount} |"));
        builder.AppendLine();
        builder.AppendLine("### EDHREC (commander x bracket grid)");
        builder.AppendLine("| Metric | Value |");
        builder.AppendLine("|--------|------:|");
        builder.AppendLine(FormattableString.Invariant($"| Cells fetched | {computation.EdhrecCoverage.CellsFetched} |"));
        builder.AppendLine(FormattableString.Invariant($"| Cells qualifying at >= {computation.EdhrecCoverage.MinCellDeckCount} decks backing cell | {computation.EdhrecCoverage.CellsQualifying} |"));
        builder.AppendLine(FormattableString.Invariant($"| Cells missing or invalid | {computation.EdhrecCoverage.CellsMissing} |"));
        builder.AppendLine(FormattableString.Invariant($"| Invalid cells | {computation.EdhrecCoverage.InvalidCells} |"));
        builder.AppendLine(FormattableString.Invariant($"| Unexpected cells | {computation.EdhrecCoverage.UnexpectedCells} |"));
        builder.AppendLine(FormattableString.Invariant($"| Commanders reached | {computation.EdhrecCoverage.CommandersReached} |"));
        builder.AppendLine(FormattableString.Invariant($"| Per-cell minimum | {computation.EdhrecCoverage.MinCellDeckCount} |"));
        builder.AppendLine(FormattableString.Invariant($"| Corpus save-date range | {FormatEdhrecDateRange(computation.EdhrecCoverage.MinSaveDate, computation.EdhrecCoverage.MaxSaveDate)} |"));
        if (computation.EdhrecCoverage.Brackets.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("| Bracket | Index | Cells fetched | Cells qualifying | Median backing deck count | Support |");
            builder.AppendLine("|---------|------:|--------------:|-----------------:|--------------------------:|---------|");
            foreach (EdhrecBracketCoverage bracket in computation.EdhrecCoverage.Brackets.OrderBy(row => row.BracketIndex))
            {
                builder.AppendLine(FormattableString.Invariant(
                    $"| {EscapePipe(bracket.BracketSlug)} | {bracket.BracketIndex} | {bracket.CellsFetched} | {bracket.CellsQualifying} | {FormatMetric(bracket.MedianBackingDeckCount)} | {EscapePipe(bracket.SupportLabel)} |"));
            }
        }

        builder.AppendLine();
        builder.AppendLine("### EDHREC land self-check");
        if (computation.EdhrecCoverage.LandSelfChecks.Count == 0)
        {
            builder.AppendLine("_No EDHREC cells were supplied for this run (--edhrec-data not provided)._");
        }
        else
        {
            builder.AppendLine("This comparison is only meaningful after a full Scryfall resolution pass; a run against a partially populated card cache will undercount unresolved names and therefore report artificially low harness land counts.");
            EdhrecLandSelfCheckSummary selfCheckSummary = SummarizeEdhrecLandSelfChecks(computation.EdhrecCoverage.LandSelfChecks);
            builder.AppendLine(FormattableString.Invariant($"- Exact match: {selfCheckSummary.ExactMatchCount}"));
            builder.AppendLine(FormattableString.Invariant($"- Within one: {selfCheckSummary.WithinOneCount}"));
            builder.AppendLine(FormattableString.Invariant($"- Diverged by more than one: {selfCheckSummary.DivergedByMoreThanOneCount}"));
            builder.AppendLine();
            builder.AppendLine("| CellId | EDHREC lands | Harness lands | Delta |");
            builder.AppendLine("|--------|-------------:|--------------:|------:|");
            foreach (EdhrecLandSelfCheck selfCheck in computation.EdhrecCoverage.LandSelfChecks
                         .OrderByDescending(check => Math.Abs(check.Delta))
                         .ThenBy(check => check.CellId, StringComparer.Ordinal)
                         .Take(5))
            {
                builder.AppendLine(FormattableString.Invariant(
                    $"| {EscapePipe(selfCheck.CellId)} | {selfCheck.EdhrecLandCount} | {selfCheck.HarnessLandCount} | {selfCheck.Delta:+#;-#;0} |"));
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Methodology");
        builder.AppendLine(FormattableString.Invariant(
            $"A commander-role row clears the written statistical bar only when DEDUPED N >= {computation.MinDeckCount}, the commander's P25 is >= {RatioHigh:0.###}x or <= {RatioLow:0.###}x the corpus P25 (or differs by at least {AbsoluteFloorGap:0.0} cards when the corpus P25 is zero), and |z| >= {ZThreshold:0.0}; z is computed as (commanderMean - corpusMean) / (corpusStdDev / sqrt(n))."));
        builder.AppendLine("RAW N is the count of distinct reconstructed mainboard deck_queue ids for a commander before content-hash collapse; DEDUPED N collapses same-content_hash near-duplicates to one representative deck per non-null hash, so DEDUPED N is the only N compared against the threshold or passed into ClearsFloorBar.");
        builder.AppendLine("Every card is classified oracle-only with the production role assigner using empty categories and `isComboPiece: false`: categories are intentionally always `[]` because `PlanRoleClassifier.Classify` is categories-first and first-hit-wins, so using live Archidekt tags would partially measure each commander playerbase's tagging habits rather than the card's mechanics. This matches the already-shipped `CutLabSimulationService.cs:517` call shape, but it means these findings do not reproduce what CutLabRoleAssigner outputs today for an actually-tagged production decklist.");
        builder.AppendLine("The verdict is computed from the commander's 25th percentile against the corpus 25th percentile; the mean z-score is retained only as a significance gate because a sample percentile has no closed-form standard error.");
        builder.AppendLine("When the corpus P25 is zero, the multiplicative ratio is undefined and `ComputeRatio` returns 0.0, so the bar falls back to an absolute gap of 2.0 cards; that is the smallest floor difference worth stating as a recommendation.");
        builder.AppendLine("EDHREC cells never enter the go/no-go, because ClearsFloorBar requires a standard deviation and a sample size that a single synthesized average deck cannot supply.");
        builder.AppendLine("Cohen's d is reported alongside ratio and z as a scale-uniform effect size, because a fixed 1.5x / 0.667x ratio gate is not scale-fair across roles with very different corpus-wide means.");
        builder.AppendLine(FormattableString.Invariant(
            $"A role is only a Phase-2 \"go\" when at least {BreadthMinimum} distinct qualifying commanders clear the bar in that role; one or two clearing commanders is recorded as signal present but insufficient breadth."));
        builder.AppendLine("At these defaults the z-gate is largely redundant once N and ratio are both satisfied: for example, N=40, sd=3, mean=6, and a 1.5x ratio implies z~6.3, far above the 2.0 cutoff.");
        builder.AppendLine(FormattableString.Invariant(
            $"This run reconstructed {computation.RawDeckCount} raw mainboard decks, {computation.DedupedDeckCount} deduped representative decks, enumerated {computation.CommandersEnumerated} commanders, and retained {computation.Commanders.Count} qualifying commanders at the primary DEDUPED-N threshold of {computation.MinDeckCount}."));
        builder.AppendLine(FormattableString.Invariant(
            $"Unresolved Scryfall card names excluded from tallies: {computation.UnresolvedCardCount} total ({computation.UnresolvedNotFoundCount} not_found, {computation.UnresolvedRateLimitedAfterRetryCount} rate_limited_after_retry)."));
        builder.AppendLine();
        builder.AppendLine("Known gaps:");
        builder.AppendLine("- Card-level category-tag coverage gap: a tagged deck can still leave individual cards uncategorized, so reconstructed decklists may miss cards.");
        builder.AppendLine("- Deck-level invisibility gap: a processed deck with zero category-tagged cards contributes no membership rows at all and is invisible to this pipeline.");
        builder.AppendLine("- content_hash NULL dedup limitation: dedup is conservative, not perfect, because older deck_queue rows may still have NULL content_hash values.");
        builder.AppendLine("- `isComboPiece` is fixed to `false`, so combo-only win conditions can be undercounted.");
        builder.AppendLine("- ManabaseMode is fixed per run, so this pass does not compare role floors across multiple play-experience modes.");
        builder.AppendLine("- The `other` residual role is deliberately excluded because it measures fallback classifier coverage rather than deck-construction structure.");
        builder.AppendLine("- Postgres decks are classified as singleton card sets because Commander is singleton for the target nonland roles there, while EDHREC cells preserve real decklist quantities so basics and other repeated entries are counted at their actual quantity.");
        builder.AppendLine("- Oracle-only classification means these findings do not reproduce today's category-aware production role output for a tagged decklist.");
        builder.AppendLine("- Cards that still fail after harness-side HTTP 429 retry are tracked separately as `rate_limited_after_retry`; like true `not_found` names, they are excluded from classification tallies, but this run distinguishes the two unresolved reasons.");
        builder.AppendLine(FormattableString.Invariant($"- EDHREC quantity-parse failures excluded rather than dropped silently: {computation.EdhrecParseFailureCount} raw deck entries across all ingested cells failed the quantity-prefix parse and were left out of classification."));
        builder.AppendLine(FormattableString.Invariant($"- EDHREC parsed-card-count anomalies: {computation.EdhrecCardCountAnomalyCount} ingested cells did not sum to 100 parsed cards after quantity parsing."));
        builder.AppendLine("- `boardFilter: \"mainboard\"` commander-membership exclusion of `sideboard` and `maybeboard` rows is verified by `CategoryCacheSchemaParityTests.GetCategoryDeckMembershipForCommanderAsync_BoardFilterMainboard_ExcludesSideboardAndMaybeboardRows`.");
        builder.AppendLine(FormattableString.Invariant($"- Protection vocabulary disclosure (unconditional): {BuildProtectionUnderDetectionPointer()}"));
        AppendBlock(builder, BuildCorpusHygieneNotice(computation));
        builder.AppendLine();
        builder.AppendLine("## Qualifying Commanders By DEDUPED-N Threshold");
        builder.AppendLine("| Threshold | Qualifying Commanders |");
        builder.AppendLine("|----------:|----------------------:|");
        foreach ((int threshold, int count) in computation.ThresholdCounts.OrderBy(pair => pair.Key))
        {
            builder.AppendLine(FormattableString.Invariant($"| {threshold} | {count} |"));
        }

        builder.AppendLine();
        builder.AppendLine("## Corpus Baseline");
        builder.AppendLine("| Role | Mean | SD | P25 |");
        builder.AppendLine("|------|-----:|---:|----:|");
        foreach (string role in TargetRoles)
        {
            RoleBaseline baseline = computation.CorpusBaseline[role];
            builder.AppendLine(FormattableString.Invariant(
                $"| {role} | {FormatMetric(baseline.Mean)} | {FormatMetric(baseline.StdDev)} | {FormatMetric(baseline.P25)} |"));
        }

        foreach (string role in TargetRoles)
        {
            builder.AppendLine();
            builder.AppendLine($"## {role}");
            if (computation.Commanders.Count == 0)
            {
                builder.AppendLine("No commanders reached the deduped threshold.");
                continue;
            }

            builder.AppendLine("### Postgres — within-commander distribution (n decks per commander)");
            AppendMarkdownTableHeader(builder, RoleFloorFigureTable.PostgresColumns);
            foreach (PostgresRoleDistribution distribution in computation.PostgresDistributions
                         .Where(figure => string.Equals(figure.Role, role, StringComparison.Ordinal))
                         .OrderByDescending(figure => ResolveCommanderDedupedN(computation.Commanders, figure.CommanderName))
                         .ThenBy(figure => figure.CommanderName, StringComparer.Ordinal))
            {
                builder.AppendLine(BuildPostgresFigureRow(computation.Commanders, distribution));
            }

            builder.AppendLine();
            builder.AppendLine("### EDHREC — commander x bracket point estimates");
            if (computation.EdhrecPointEstimates.Count == 0)
            {
                builder.AppendLine("_No EDHREC cells were supplied for this run (--edhrec-data not provided)._");
            }
            else
            {
                AppendMarkdownTableHeader(builder, RoleFloorFigureTable.EdhrecColumns);
                foreach (EdhrecRolePointEstimate pointEstimate in computation.EdhrecPointEstimates
                             .Where(figure => string.Equals(figure.Role, role, StringComparison.Ordinal))
                             .OrderBy(figure => figure.CommanderName, StringComparer.Ordinal)
                             .ThenBy(figure => figure.BracketIndex))
                {
                    builder.AppendLine(BuildEdhrecFigureRow(pointEstimate));
                }

                builder.AppendLine();
                builder.AppendLine("*Each figure above is a point estimate from a single synthesized average deck. It is not a percentile and has no within-cell variance. EDHREC figures do not enter the go/no-go.*");
            }
        }

        builder.AppendLine();
        AppendLandsCalibrationControl(builder, computation);
        builder.AppendLine();
        builder.AppendLine("## Go/No-Go");
        IReadOnlyList<string> rolesInScopeForPhase3 = GetRolesByStatus(computation.GoNoGo, "go");
        IReadOnlyList<string> signalPresentRoles = GetRolesByStatus(computation.GoNoGo, "signal-present");
        builder.AppendLine(FormattableString.Invariant(
            $"**Roles in scope for Phase 3:** {(rolesInScopeForPhase3.Count == 0 ? "NONE." : string.Join(", ", rolesInScopeForPhase3))}"));
        builder.AppendLine(FormattableString.Invariant(
            $"**Signal present but insufficient breadth (NOT in scope):** {(signalPresentRoles.Count == 0 ? "NONE." : string.Join(", ", signalPresentRoles))}"));
        AppendBlock(builder, BuildCasualBiasEngagement(computation));
        // Why: criterion 10 requires a null result to read as a valid deliverable; without this
        // dedicated block, nine "no-go" bullets read like a broken run rather than an answer.
        if (rolesInScopeForPhase3.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("### Null result — Phase 3 is a documented no-op");
            builder.AppendLine("No role's per-commander 25th percentile diverged from the corpus 25th percentile by enough, at enough breadth, to justify a commander-specific floor.");
            builder.AppendLine("This is a valid result for this phase, not a failed run. Phase 3 becomes a documented no-op closeout, and the cycle ends at Phases 4 and 5 exactly as the ROADMAP's Phase 3 dependency line anticipates.");
            builder.AppendLine("The measurement itself still stands: the corpus baselines, the per-commander distributions, and the lands calibration verdict are all usable results.");
            builder.AppendLine(FormattableString.Invariant($"Use `{NoGoTemplatePath}` for the write-up shape."));
        }

        foreach (string role in TargetRoles)
        {
            RoleOutcome outcome = computation.GoNoGo[role];
            string commanderCitation = outcome.CitingCommanders.Count == 0
                ? string.Empty
                : $" ({FormatCommanderCitation(outcome)})";
            builder.AppendLine($"- {role}: {outcome.MarkdownStatus}{commanderCitation}");
            if (string.Equals(role, "protection", StringComparison.Ordinal))
            {
                builder.AppendLine();
                AppendBlock(builder, BuildProtectionUnderDetectionNotice());
            }
        }

        return builder.ToString();
    }

    private static object BuildJsonPayload(ResearchComputation computation)
    {
        // Why: fallback provenance values must never block a run or leak a credential, so the
        // warning array is emitted from the same resolved values to prevent "unavailable"/"unknown"
        // from silently masquerading as complete provenance.
        IReadOnlyList<string> provenanceWarnings = BuildArtifactProvenanceWarnings(computation);
        EdhrecLandSelfCheckSummary edhrecLandSelfCheckSummary = SummarizeEdhrecLandSelfChecks(computation.EdhrecCoverage.LandSelfChecks);
        var methodology = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["minDeckCount"] = computation.MinDeckCount,
            ["ratioLow"] = RatioLow,
            ["ratioHigh"] = RatioHigh,
            ["zThreshold"] = ZThreshold,
            ["absoluteFloorGap"] = AbsoluteFloorGap,
            ["breadthMinimum"] = BreadthMinimum,
            ["databaseHost"] = computation.DatabaseHost,
            ["runTimestampUtc"] = computation.RunTimestampUtc,
            ["harnessCommitSha"] = computation.HarnessCommitSha,
            ["rawDeckCount"] = computation.RawDeckCount,
            ["dedupedDeckCount"] = computation.DedupedDeckCount,
            ["commandersEnumerated"] = computation.CommandersEnumerated,
            ["unresolvedCardCount"] = computation.UnresolvedCardCount,
            ["unresolvedNotFoundCount"] = computation.UnresolvedNotFoundCount,
            ["unresolvedRateLimitedAfterRetryCount"] = computation.UnresolvedRateLimitedAfterRetryCount,
            ["provenanceWarnings"] = provenanceWarnings,
        };

        if (computation.CommanderLimit.HasValue)
        {
            methodology["commanderLimit"] = computation.CommanderLimit.Value;
        }

        return new
        {
            methodology,
            corpusBaseline = TargetRoles.ToDictionary(
                role => role,
                role => new
                {
                    mean = computation.CorpusBaseline[role].Mean,
                    stdDev = computation.CorpusBaseline[role].StdDev,
                    p25 = computation.CorpusBaseline[role].P25,
                },
                StringComparer.Ordinal),
            commanders = computation.Commanders.Values
                .OrderByDescending(commander => commander.N)
                .ThenBy(commander => commander.CommanderName, StringComparer.Ordinal)
                .ToDictionary(
                    commander => commander.CommanderName,
                    commander => new
                    {
                        rawN = commander.RawN,
                        n = commander.N,
                        roles = TargetRoles.ToDictionary(
                            role => role,
                            role => new
                            {
                                source = RoleFloorSource.Postgres,
                                mean = commander.Roles[role].Mean,
                                p25 = commander.Roles[role].P25,
                                ratio = commander.Roles[role].Ratio,
                                z = commander.Roles[role].ZScore,
                                cohensD = commander.Roles[role].CohensD,
                                clearsBar = commander.Roles[role].ClearsBar,
                            },
                            StringComparer.Ordinal),
                    },
                    StringComparer.Ordinal),
            edhrec = new
            {
                cells = computation.EdhrecPointEstimates
                    .OrderBy(cell => cell.CommanderName, StringComparer.Ordinal)
                    .ThenBy(cell => cell.BracketIndex)
                    .Select(cell => new
                    {
                        source = cell.Source,
                        role = cell.Role,
                        commander = cell.CommanderName,
                        bracket = cell.BracketSlug,
                        bracketIndex = cell.BracketIndex,
                        count = cell.Count,
                        deckCount = cell.DeckCount,
                        qualifies = cell.Qualifies,
                    })
                    .ToArray(),
                coverage = new
                {
                    cellsFetched = computation.EdhrecCoverage.CellsFetched,
                    cellsQualifying = computation.EdhrecCoverage.CellsQualifying,
                    cellsMissing = computation.EdhrecCoverage.CellsMissing,
                    invalidCells = computation.EdhrecCoverage.InvalidCells,
                    unexpectedCells = computation.EdhrecCoverage.UnexpectedCells,
                    commandersReached = computation.EdhrecCoverage.CommandersReached,
                    minCellDeckCount = computation.EdhrecCoverage.MinCellDeckCount,
                    minSaveDate = computation.EdhrecCoverage.MinSaveDate,
                    maxSaveDate = computation.EdhrecCoverage.MaxSaveDate,
                    brackets = computation.EdhrecCoverage.Brackets
                        .OrderBy(bracket => bracket.BracketIndex)
                        .Select(bracket => new
                        {
                            bracket = bracket.BracketSlug,
                            bracketIndex = bracket.BracketIndex,
                            cellsFetched = bracket.CellsFetched,
                            cellsQualifying = bracket.CellsQualifying,
                            medianBackingDeckCount = bracket.MedianBackingDeckCount,
                            supportLabel = bracket.SupportLabel,
                        })
                        .ToArray(),
                    landSelfCheck = new
                    {
                        summary = new
                        {
                            exactMatch = edhrecLandSelfCheckSummary.ExactMatchCount,
                            withinOne = edhrecLandSelfCheckSummary.WithinOneCount,
                            divergedByMoreThanOne = edhrecLandSelfCheckSummary.DivergedByMoreThanOneCount,
                        },
                        worstFive = computation.EdhrecCoverage.LandSelfChecks
                            .OrderByDescending(check => Math.Abs(check.Delta))
                            .ThenBy(check => check.CellId, StringComparer.Ordinal)
                            .Take(5)
                            .Select(check => new
                            {
                                cellId = check.CellId,
                                edhrecLandCount = check.EdhrecLandCount,
                                harnessLandCount = check.HarnessLandCount,
                                delta = check.Delta,
                            })
                            .ToArray(),
                    },
                },
            },
            goNoGo = TargetRoles.ToDictionary(
                role => role,
                role => new
                {
                    status = computation.GoNoGo[role].JsonStatus,
                    citingCommanders = computation.GoNoGo[role].CitingCommanders,
                },
                StringComparer.Ordinal),
            rolesInScopeForPhase3 = GetRolesByStatus(computation.GoNoGo, "go"),
            signalPresentRoles = GetRolesByStatus(computation.GoNoGo, "signal-present"),
            protectionUnderDetection = new
            {
                affectedRole = "protection, interaction-targeted",
                needles = DeckStatClassifier.ProtectionOracleNeedles.Select(needle => new
                {
                    text = needle.Text,
                    effect = needle.Effect,
                    subjectForm = needle.SubjectForm,
                }).ToArray(),
                historicalNarrowNeedles = ProtectionHistoricalNarrowNeedles,
                knownMissedCards = ProtectionKnownMissedCards.Select(card => new
                {
                    name = card.Name,
                    evidenceGrade = card.EvidenceGrade,
                    evidenceNote = card.EvidenceNote,
                }).ToArray(),
                consequence = "The vocabulary was widened in Phase 9.1 (docs/research/protection-vocabulary-corpus-2026-09.md) from historicalNarrowNeedles to the corpus-derived table above. Runs produced before that widening used only historicalNarrowNeedles, so their reported protection and interaction-targeted counts are lower bounds, not true counts; this run uses the corpus-derived table.",
                consumers = ProtectionConsumers,
            },
            corpusHygiene = new
            {
                corpusDecks = 397063,
                processedDecks = 151202,
                commandersWithDecks = 4003,
                depth = new
                {
                    atLeast40 = 847,
                    atLeast100 = 346,
                    atLeast250 = 88,
                    atLeast500 = 17,
                    deepest = 917,
                },
                sample = new
                {
                    sampleSize = 300,
                    liveDecks = 287,
                    commanderFormatShare = "286/287 live decks are deckFormat 3 (Commander), 99.65%; one was format 7.",
                    deadIdShare = "13/300 (4.3%) dead deck ids: 404, private, or deleted.",
                    theorycraftedShare = "7/287 (2.4%) live decks are theorycrafted.",
                    createdYearHistogram = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["2026"] = 213,
                        ["2025"] = 62,
                        ["2024"] = 10,
                        ["2023"] = 1,
                        ["2021"] = 1,
                    },
                },
                recencyWindow = (string?)null,
                unparsedPayloadFields = CorpusHygieneUnparsedPayloadFields,
                commanderInferredFrom = "card-category",
                phase5NonGatingRationale = "Phase 5 remains non-gating because edhBracket is only ~25% filled on the payload; even the deepest commander has 917 decks, so 917 x 25% / 5 brackets is roughly 46 decks per cell against EDHREC's 400-deck floor. That arithmetic strengthens decision D-A rather than weakening it.",
            },
            casualBiasObjection = new
            {
                sourcePath = CasualBiasArchivePath,
                codebaseResponses = new[]
                {
                    "ManabaseAnalysisService.cs:603-605",
                    "CutLabFloorDefaults.ResolveLandsDefault",
                },
                argument = "A stax deck and a swarm deck under the same commander are both correct with wildly different mixes, so distance-from-average is noise for the serious-builder audience.",
                thisRunSays = BuildCasualBiasThisRunSays(computation),
            },
            landsCalibration = BuildLandsCalibrationPayload(computation),
            rampCalibration = new
            {
                verdict = "no-prior",
                note = "Per-commander ramp variation was not measured in the 2026-07-16 study; that study rejected commander-ability-driven land adjustment, which is a different question.",
            },
        };
    }

    private static IReadOnlyList<string> BuildArtifactProvenanceWarnings(ResearchComputation computation)
    {
        ArgumentNullException.ThrowIfNull(computation);

        var warnings = RoleFloorProvenance.BuildProvenanceWarnings(
            computation.DatabaseHost,
            computation.HarnessCommitSha,
            computation.RawDeckCount,
            computation.DedupedDeckCount).ToList();

        if (computation.CommanderLimit.HasValue)
        {
            warnings.Add(FormattableString.Invariant(
                $"limited run: only {computation.CommandersEnumerated} commanders were loaded (--limit {computation.CommanderLimit.Value}). These findings are a diagnostic, not evidence."));
        }

        return warnings;
    }

    private static string FormatCommanderLimit(int? commanderLimit)
        => commanderLimit.HasValue
            ? FormattableString.Invariant($"{commanderLimit.Value} (--limit {commanderLimit.Value})")
            : "full corpus";

    private static void AppendLandsCalibrationControl(StringBuilder builder, ResearchComputation computation)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(computation);

        string verdict = ClassifyLandsCalibration(computation);
        RoleBaseline landsBaseline = computation.CorpusBaseline["lands"];
        IReadOnlyDictionary<int, FreshEdhrecLandBracketFigure?> freshFigures = BuildFreshEdhrecLandBracketFigures(computation);
        ThreeReferenceAgreement agreement = BuildThreeReferenceAgreement(computation, freshFigures);

        builder.AppendLine("## Lands Calibration Control");
        builder.AppendLine(FormattableString.Invariant($"**Verdict vs the {PriorLandStudyDate} prior: {verdict}**"));
        builder.AppendLine(FormattableString.Invariant(
            $"The prior in `{PriorLandStudyPath}` concluded that bracket drove land count, commander identity barely moved it, and every commander-ability-driven land adjustment was rejected; on this control role that means a lands `no-go` reproduces the prior while a lands `go` contradicts it."));
        builder.AppendLine();
        builder.AppendLine("| Bracket | Prior mean / SD | Live shipped avgLands | Fresh measured avgLands |");
        builder.AppendLine("|---------|-----------------:|----------------------:|------------------------:|");
        foreach (int bracketIndex in Enumerable.Range(1, 5))
        {
            builder.AppendLine(FormattableString.Invariant(
                $"| B{bracketIndex} | {FormatPriorLandCell(bracketIndex)} | {FormatLiveBaselineLandCell(bracketIndex)} | {FormatFreshMeasuredLandCell(computation, bracketIndex, freshFigures)} |"));
        }

        builder.AppendLine(FormattableString.Invariant(
            $"Postgres corpus-wide only (not bracket-resolved; the Postgres corpus has no bracket dimension): mean / SD / P25 = {FormatMetric(landsBaseline.Mean)} / {FormatMetric(landsBaseline.StdDev)} / {FormatMetric(landsBaseline.P25)}."));
        builder.AppendLine(BuildThreeReferenceAgreementSentence(agreement, landsBaseline.Mean));

        if (string.Equals(verdict, "contradicts", StringComparison.Ordinal))
        {
            builder.AppendLine();
            builder.AppendLine("This contradicts the prior, so per the ROADMAP this is a finding about the methodology before it is a finding about decks. First suspects: the P25-versus-point-estimate difference, the EDHREC land self-check deltas from plan 02-06, and the Postgres corpus category-tag coverage gaps already listed in Known gaps.");
        }
        else if (string.Equals(verdict, "insufficient data", StringComparison.Ordinal))
        {
            builder.AppendLine();
            builder.AppendLine(BuildLandsInsufficientDataExplanation(computation));
        }

        builder.AppendLine();
        builder.AppendLine("### Ramp — no prior to compare against");
        builder.AppendLine("Per-commander ramp variation was never measured this way in the 2026-07-16 work. That study addressed commander-ability-driven land adjustment, which is a different question, so this run's ramp verdict stands on its own with no reproduce/contradict comparison available.");
    }

    private static object BuildLandsCalibrationPayload(ResearchComputation computation)
    {
        ArgumentNullException.ThrowIfNull(computation);

        IReadOnlyDictionary<int, FreshEdhrecLandBracketFigure?> freshFigures = BuildFreshEdhrecLandBracketFigures(computation);
        return new
        {
            verdict = ClassifyLandsCalibration(computation),
            priorStudyPath = PriorLandStudyPath,
            priorStudyDate = PriorLandStudyDate,
            priorBracketMeans = PriorLandBracketMeans.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value),
            priorOverallMean = PriorLandOverallMean,
            runLandsMeanPostgres = computation.CorpusBaseline["lands"].Mean,
            runLandsP25Postgres = computation.CorpusBaseline["lands"].P25,
            runLandsPerBracketEdhrec = freshFigures
                .OrderBy(pair => pair.Key)
                .ToDictionary(
                    pair => pair.Key,
                    pair => (object)(pair.Value is null
                        ? new
                        {
                            mean = (double?)null,
                            supportLabel = computation.EdhrecCoverage.CellsFetched == 0
                                ? "n/a (no EDHREC corpus supplied)"
                                : ResolveBracketSupportLabel(computation, pair.Key),
                            qualifyingCellCount = ResolveQualifyingCellCount(computation, pair.Key),
                        }
                        : new
                        {
                            mean = (double?)pair.Value.Mean,
                            supportLabel = pair.Value.SupportLabel,
                            qualifyingCellCount = pair.Value.QualifyingCellCount,
                        })),
            qualifyingCommanderCount = computation.Commanders.Count,
        };
    }

    private static string BuildLandsInsufficientDataExplanation(ResearchComputation computation)
    {
        ArgumentNullException.ThrowIfNull(computation);

        if (computation.Commanders.Count < LandsCalibrationMinCommanders)
        {
            return FormattableString.Invariant(
                $"Insufficient data because only {computation.Commanders.Count} qualifying commanders were available for lands and LandsCalibrationMinCommanders requires {LandsCalibrationMinCommanders}; this does not challenge the prior in either direction.");
        }

        RoleBaseline landsBaseline = computation.CorpusBaseline["lands"];
        if (landsBaseline.Mean <= 0.0)
        {
            return "Insufficient data because the corpus lands baseline is zero; this does not challenge the prior in either direction.";
        }

        RoleOutcome landsOutcome = computation.GoNoGo["lands"];
        if (string.Equals(landsOutcome.JsonStatus, "signal-present", StringComparison.Ordinal))
        {
            return FormattableString.Invariant(
                $"Insufficient data because only {landsOutcome.ClearingCommanderCount} distinct qualifying commanders cleared the lands bar and BreadthMinimum = {BreadthMinimum} distinct qualifying commanders clearing the bar. This is not a contradiction of the prior: the harness itself reported the breadth as insufficient, so the result supports no conclusion about the prior in either direction. What would settle it is more qualifying commanders at the stated minimum deck count.");
        }

        return FormattableString.Invariant(
            $"Insufficient data because only {computation.Commanders.Count} qualifying commanders were available for lands and LandsCalibrationMinCommanders requires {LandsCalibrationMinCommanders}, or because the corpus lands baseline was zero; this does not challenge the prior in either direction.");
    }

    private static IReadOnlyDictionary<int, FreshEdhrecLandBracketFigure?> BuildFreshEdhrecLandBracketFigures(ResearchComputation computation)
    {
        ArgumentNullException.ThrowIfNull(computation);

        var results = new Dictionary<int, FreshEdhrecLandBracketFigure?>();
        foreach (int bracketIndex in Enumerable.Range(1, 5))
        {
            if (computation.EdhrecCoverage.CellsFetched == 0)
            {
                results[bracketIndex] = null;
                continue;
            }

            string supportLabel = ResolveBracketSupportLabel(computation, bracketIndex);
            if (supportLabel.StartsWith("NOT REPORTED", StringComparison.Ordinal))
            {
                results[bracketIndex] = null;
                continue;
            }

            List<EdhrecRolePointEstimate> cells = computation.EdhrecPointEstimates
                .Where(estimate =>
                    string.Equals(estimate.Role, "lands", StringComparison.Ordinal) &&
                    estimate.BracketIndex == bracketIndex &&
                    estimate.Qualifies)
                .ToList();

            if (cells.Count == 0)
            {
                results[bracketIndex] = null;
                continue;
            }

            results[bracketIndex] = new FreshEdhrecLandBracketFigure
            {
                BracketIndex = bracketIndex,
                Mean = cells.Average(cell => (double)cell.Count),
                QualifyingCellCount = cells.Count,
                SupportLabel = supportLabel,
            };
        }

        return results;
    }

    private static string ResolveBracketSupportLabel(ResearchComputation computation, int bracketIndex)
        => computation.EdhrecCoverage.Brackets
            .FirstOrDefault(bracket => bracket.BracketIndex == bracketIndex)?.SupportLabel
            ?? "n/a (no bracket coverage row)";

    private static int ResolveQualifyingCellCount(ResearchComputation computation, int bracketIndex)
        => computation.EdhrecCoverage.Brackets
            .FirstOrDefault(bracket => bracket.BracketIndex == bracketIndex)?.CellsQualifying
            ?? 0;

    private static string FormatPriorLandCell(int bracketIndex)
        => FormattableString.Invariant(
            $"{FormatMetric(PriorLandBracketMeans[bracketIndex])} / {FormatMetric(PriorLandBracketStdDevs[bracketIndex])}");

    private static string FormatLiveBaselineLandCell(int bracketIndex)
        => LiveBaselineLandBracketMeans.TryGetValue(bracketIndex, out double mean)
            ? FormatMetric(mean)
            : "n/a (no B1 row in the snapshot)";

    private static string FormatFreshMeasuredLandCell(
        ResearchComputation computation,
        int bracketIndex,
        IReadOnlyDictionary<int, FreshEdhrecLandBracketFigure?> freshFigures)
    {
        ArgumentNullException.ThrowIfNull(computation);
        ArgumentNullException.ThrowIfNull(freshFigures);

        if (computation.EdhrecCoverage.CellsFetched == 0)
        {
            return "n/a (no EDHREC corpus supplied)";
        }

        string supportLabel = ResolveBracketSupportLabel(computation, bracketIndex);
        if (supportLabel.StartsWith("NOT REPORTED", StringComparison.Ordinal))
        {
            return "n/a (insufficient cells)";
        }

        if (!freshFigures.TryGetValue(bracketIndex, out FreshEdhrecLandBracketFigure? figure) || figure is null)
        {
            return "n/a (insufficient cells)";
        }

        if (supportLabel.StartsWith("THIN", StringComparison.Ordinal))
        {
            return FormattableString.Invariant($"{FormatMetric(figure.Mean)} ({figure.QualifyingCellCount} qualifying cells)");
        }

        return FormatMetric(figure.Mean);
    }

    private static ThreeReferenceAgreement BuildThreeReferenceAgreement(
        ResearchComputation computation,
        IReadOnlyDictionary<int, FreshEdhrecLandBracketFigure?> freshFigures)
    {
        ArgumentNullException.ThrowIfNull(computation);
        ArgumentNullException.ThrowIfNull(freshFigures);

        var spreads = new List<(int BracketIndex, double Spread)>();
        foreach (int bracketIndex in Enumerable.Range(1, 5))
        {
            var values = new List<double> { PriorLandBracketMeans[bracketIndex] };
            if (LiveBaselineLandBracketMeans.TryGetValue(bracketIndex, out double liveMean))
            {
                values.Add(liveMean);
            }

            if (freshFigures.TryGetValue(bracketIndex, out FreshEdhrecLandBracketFigure? freshFigure) && freshFigure is not null)
            {
                values.Add(freshFigure.Mean);
            }

            if (values.Count < 2)
            {
                continue;
            }

            spreads.Add((bracketIndex, values.Max() - values.Min()));
        }

        double postgresMean = computation.CorpusBaseline["lands"].Mean;
        var sharedBracketMeans = Enumerable.Range(1, 5)
            .Where(bracketIndex =>
                LiveBaselineLandBracketMeans.ContainsKey(bracketIndex) &&
                freshFigures.TryGetValue(bracketIndex, out FreshEdhrecLandBracketFigure? freshFigure) &&
                freshFigure is not null)
            .Select(bracketIndex => new
            {
                BracketIndex = bracketIndex,
                PriorMean = PriorLandBracketMeans[bracketIndex],
                LiveMean = LiveBaselineLandBracketMeans[bracketIndex],
                FreshMean = freshFigures[bracketIndex]!.Mean,
            })
            .ToList();

        if (sharedBracketMeans.Count == 0)
        {
            return new ThreeReferenceAgreement
            {
                MaximumSpread = spreads.Count == 0 ? 0.0 : spreads.Max(spread => spread.Spread),
                DivergentBrackets = spreads
                    .Where(spread => spread.Spread > 1.0)
                    .Select(spread => $"B{spread.BracketIndex}")
                    .ToArray(),
                ClosestReferenceSet = null,
                ComparisonBrackets = Array.Empty<string>(),
            };
        }

        // Why: comparing average distances over different bracket sets biases the result toward
        // whichever set omits the outlier bracket; here B1 = 36.3 is the farthest-out value and
        // the live snapshot structurally omits it, so all three averages must use the same brackets.
        double priorAverageDistance = sharedBracketMeans.Average(value => Math.Abs(postgresMean - value.PriorMean));
        double liveAverageDistance = sharedBracketMeans.Average(value => Math.Abs(postgresMean - value.LiveMean));
        double freshAverageDistance = sharedBracketMeans.Average(value => Math.Abs(postgresMean - value.FreshMean));

        string closestReferenceSet = new[]
            {
                (Name: "the prior", Distance: priorAverageDistance),
                (Name: "the live shipped baseline", Distance: liveAverageDistance),
                (Name: "the fresh measured EDHREC baseline", Distance: freshAverageDistance),
            }
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Name, StringComparer.Ordinal)
            .First().Name;

        return new ThreeReferenceAgreement
        {
            MaximumSpread = spreads.Count == 0 ? 0.0 : spreads.Max(spread => spread.Spread),
            DivergentBrackets = spreads
                .Where(spread => spread.Spread > 1.0)
                .Select(spread => $"B{spread.BracketIndex}")
                .ToArray(),
            ClosestReferenceSet = closestReferenceSet,
            ComparisonBrackets = sharedBracketMeans.Select(value => $"B{value.BracketIndex}").ToArray(),
        };
    }

    private static string BuildThreeReferenceAgreementSentence(ThreeReferenceAgreement agreement, double postgresMean)
    {
        ArgumentNullException.ThrowIfNull(agreement);

        string spreadClause = FormattableString.Invariant(
            $"**Do the three reference sets agree?** Maximum absolute spread across prior / live / fresh = {agreement.MaximumSpread:0.###} lands{FormatDivergentBracketClause(agreement)}.");
        if (agreement.ComparisonBrackets.Count == 0 || string.IsNullOrWhiteSpace(agreement.ClosestReferenceSet))
        {
            return FormattableString.Invariant(
                $"{spreadClause} No bracket carries all three reference sets, so no closest-set statement can be made. Agreement among the three EDHREC-derived baselines says nothing about this run's Postgres P25 result, which measures a quantity none of them can measure.");
        }

        return FormattableString.Invariant(
            $"{spreadClause} Using this run's Postgres mean ({FormatMetric(postgresMean)}) as the only comparable scalar from the within-commander corpus, it sits closest on average to {agreement.ClosestReferenceSet}, computed over {FormatBracketSet(agreement.ComparisonBrackets)} (brackets where all three sets carry a value). Agreement among the three EDHREC-derived baselines says nothing about this run's Postgres P25 result, which measures a quantity none of them can measure.");
    }

    private static string FormatBracketSet(IReadOnlyList<string> brackets)
    {
        ArgumentNullException.ThrowIfNull(brackets);

        if (brackets.Count == 0)
        {
            return "no brackets";
        }

        if (brackets.Count == 1)
        {
            return brackets[0];
        }

        return FormattableString.Invariant($"{brackets[0]}-{brackets[^1]}");
    }

    private static string FormatDivergentBracketClause(ThreeReferenceAgreement agreement)
        => agreement.DivergentBrackets.Count == 0
            ? "; no bracket exceeds a 1.0-land spread"
            : FormattableString.Invariant($"; brackets exceeding a 1.0-land spread: {string.Join(", ", agreement.DivergentBrackets)}");

    private static IReadOnlyList<string> GetRolesByStatus(
        IReadOnlyDictionary<string, RoleOutcome> goNoGo,
        string status)
        => TargetRoles
            .Where(role => string.Equals(goNoGo[role].JsonStatus, status, StringComparison.Ordinal))
            .ToArray();

    // Why: this seam is internal so DeckFlow.Core.Tests can exercise the real CLI disclosure through the existing InternalsVisibleTo, matching the project rule that CLI additions carry Core test coverage.
    internal static string BuildProtectionUnderDetectionNotice(
        bool includeHeading = true,
        bool includeHistoryPointer = true)
    {
        var builder = new StringBuilder();
        if (includeHeading)
        {
            builder.AppendLine("### Protection vocabulary disclosure");
        }

        builder.AppendLine(FormattableString.Invariant(
            $"`DeckStatClassifier.IsProtectionCard` (`{ProtectionClassifierPath}`) is `StaxProtectionCatalog.IsProtection(name)` OR-ed with a corpus-derived needle table, `DeckStatClassifier.ProtectionOracleNeedles` ({DeckStatClassifier.ProtectionOracleNeedles.Count} needles):"));
        foreach (ProtectionNeedle needle in DeckStatClassifier.ProtectionOracleNeedles)
        {
            builder.AppendLine(FormattableString.Invariant(
                $"- `{needle.Text}` ({needle.Effect}, {needle.SubjectForm} subject form)."));
        }

        builder.AppendLine(FormattableString.Invariant(
            $"The vocabulary was widened in Phase 9.1 from the four needles the classifier originally carried ({string.Join(", ", ProtectionHistoricalNarrowNeedles.Select(needle => $"`{needle}`"))}). Runs produced before that widening used only those four needles, so their reported `protection` and `interaction-targeted` counts are lower bounds, not true counts; runs produced after it use the corpus-derived table above."));
        builder.AppendLine(
            "`interaction-targeted` is affected because CutLabRoleAssigner grants it on PlanRoleClassifier's pre-gate interaction signal, which IsProtectionCard feeds. On the nine-fixture reference sample the widening moved it from 68 to 77 cards.");
        builder.AppendLine("Cards previously missed under the narrow four-needle set, now detected, with evidence grade stated rather than flattened:");
        foreach (ProtectionMissedCardDisclosure card in ProtectionKnownMissedCards)
        {
            builder.AppendLine(FormattableString.Invariant(
                $"- `{card.Name}` — {card.EvidenceGrade}. {card.EvidenceNote}"));
        }

        if (includeHistoryPointer)
        {
            builder.AppendLine(FormattableString.Invariant(
                $"Full corpus derivation and the measured blast radius across all three consumers are recorded at `docs/research/protection-vocabulary-corpus-2026-09.md` and `docs/research/protection-vocabulary-blast-radius-2026-09.md` (`{ProtectionDeltaPath}` records the original Phase 01.1 measurement notes this disclosure traces back to)."));
        }

        builder.AppendLine(FormattableString.Invariant(
            $"The predicate is shared by three consumers: {string.Join(", ", ProtectionConsumers)}."));
        return builder.ToString().TrimEnd();
    }

    private static string BuildProtectionUnderDetectionPointer()
        => FormattableString.Invariant(
            $"see `## Go/No-Go` below for the full disclosure; the protection vocabulary was widened in Phase 9.1 and this run's counts use the corpus-derived needle table. {BuildProtectionUnderDetectionNotice(includeHeading: false, includeHistoryPointer: false).ReplaceLineEndings("\n").Split('\n')[0]}");

    private static string BuildCorpusHygieneNotice(ResearchComputation computation)
    {
        ArgumentNullException.ThrowIfNull(computation);

        string observedEdhrecRange = FormatEdhrecDateRange(computation.EdhrecCoverage.MinSaveDate, computation.EdhrecCoverage.MaxSaveDate);
        var builder = new StringBuilder();
        builder.AppendLine("### Corpus hygiene disclosure");
        builder.AppendLine("Corpus scale: 397,063 decks, 151,202 processed, 4,003 commanders with processed decks. Depth: 847 commanders at >=40 decks, 346 at >=100, 88 at >=250, 17 at >=500, deepest 917.");
        builder.AppendLine("From a random sample of n=300 processed decks drawn across commanders clearing the >=40 gate: 286/287 live decks are `deckFormat` 3 (Commander), 99.65% (one was format 7); 13/300 (4.3%) deck ids are dead — 404, private, or deleted; 7/287 (2.4%) live decks are `theorycrafted`. Created-year spread: 213x 2026, 62x 2025, 10x 2024, 1x 2023, 1x 2021.");
        builder.AppendLine("There is no recency window. Decks are counted regardless of age, and the corpus carries no `createdAt` or `updatedAt` at all — the year figures above come from the sample's live API responses, not from stored data.");
        builder.AppendLine("The 4.3% dead-id rate inflates every deck count in this document, including the deduped counts the go/no-go breadth bar is applied against.");
        builder.AppendLine(FormattableString.Invariant(
            $"`ArchidektApiDeckImporter` parses `cards[]` only. {string.Join(", ", CorpusHygieneUnparsedPayloadFields.Select(field => $"`{field}`"))} are unparsed and unstored, so commander-ness is inferred from a card categorized `Commander`, never from the deck's declared format."));
        builder.AppendLine("This is a stated limitation, not a blocker. Phase 5 remains independent and non-gating: `edhBracket` is present on the payload at roughly 25% fill and is free to capture, but the deepest commander has 917 decks, so 917 x 25% / 5 brackets is roughly 46 decks per cell against EDHREC's 400 floor. Archidekt bracket capture therefore cannot fill a bracket cell for any commander, now or after a full backfill. That arithmetic strengthens decision D-A rather than weakening it.");
        builder.AppendLine(FormattableString.Invariant(
            $"By contrast, the EDHREC side does carry a recency window through each cell's `savedate_summary`; this run observed an overall range of {observedEdhrecRange}."));
        return builder.ToString().TrimEnd();
    }

    private static string BuildCasualBiasEngagement(ResearchComputation computation)
    {
        ArgumentNullException.ThrowIfNull(computation);

        // Why: D-B moved the statistic from distance-from-average to a P25 over real per-deck
        // distributions; that makes the question worth re-asking, but it is not itself an answer.
        string thisRunSays = BuildCasualBiasThisRunSays(computation);
        var builder = new StringBuilder();
        builder.AppendLine("### The casual-bias objection");
        builder.AppendLine(FormattableString.Invariant(
            $"Archived conclusion: EDHREC averages are casual-dominated (`{CasualBiasArchivePath}`)."));
        builder.AppendLine("The codebase already acted on that conclusion in two places: `ManabaseAnalysisService.cs:603-605` restricts the EDHREC commander cell to brackets 2-3, and `CutLabFloorDefaults.ResolveLandsDefault` routes bracket 5 to the cEDH tournament corpus instead.");
        builder.AppendLine("The objection aimed at this phase's premise is that a stax deck and a swarm deck under the same commander are both correct with wildly different mixes, so distance-from-average is noise for the serious-builder audience.");
        builder.AppendLine(thisRunSays);
        builder.AppendLine("What this run cannot say is whether the full within-commander archetype spread is multimodal in a way the mean/P25 summary hides; it only speaks to lower-tail spread visible in the Postgres per-deck distributions.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildCasualBiasThisRunSays(ResearchComputation computation)
    {
        ArgumentNullException.ThrowIfNull(computation);

        string expression = "Avg_role(Avg_commander(max(0, commanderMean - commanderP25)) / PopStdDev_commander(commanderMean))";
        var perRoleRatios = new List<(string Role, double? Ratio)>();
        foreach (string role in TargetRoles)
        {
            List<CommanderResearch> commanders = computation.Commanders.Values
                .Where(commander => commander.Roles.ContainsKey(role))
                .ToList();
            if (commanders.Count < 2)
            {
                continue;
            }

            double withinCommanderLowerTailSpread = commanders.Average(commander => Math.Max(0.0, commander.Roles[role].Mean - commander.Roles[role].P25));
            double betweenCommanderSpread = ComputePopulationStdDev(
                commanders.Select(commander => commander.Roles[role].Mean).ToArray(),
                commanders.Average(commander => commander.Roles[role].Mean));
            perRoleRatios.Add((
                Role: role,
                Ratio: betweenCommanderSpread <= 0.0
                    ? null
                    : withinCommanderLowerTailSpread / betweenCommanderSpread));
        }

        List<string> formattedPerRoleRatios = perRoleRatios
            .Select(value => $"{value.Role}={(value.Ratio is null ? "n/a" : FormatMetric(value.Ratio.Value))}")
            .ToList();

        List<double> comparableRatios = perRoleRatios
            .Where(value => value.Ratio is not null)
            .Select(value => value.Ratio!.Value)
            .ToList();

        if (comparableRatios.Count == 0)
        {
            return FormattableString.Invariant(
                $"This run cannot yet quantify the objection from its own numbers because no role had enough qualifying commanders to compare within-commander lower-tail spread against between-commander spread. Expression reserved for that comparison: `{expression}`.");
        }

        return FormattableString.Invariant(
            $"This run says, using `{expression}`, that the lower-tail within-commander spread relative to the between-commander spread is {FormatMetric(comparableRatios.Average())} on average across roles ({string.Join(", ", formattedPerRoleRatios)}). That is evidence about lower-tail spread in the measured Postgres corpus, not a rebuttal of the broader claim.");
    }

    private static void AppendBlock(StringBuilder builder, string block)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(block);

        string[] lines = block.ReplaceLineEndings("\n").Split('\n');
        int lineCount = lines.Length;
        if (lines[^1].Length == 0)
        {
            lineCount--;
        }

        for (var index = 0; index < lineCount; index++)
        {
            builder.AppendLine(lines[index]);
        }
    }

    private static string FormatCommanderCitation(RoleOutcome outcome)
    {
        if (outcome.CitingCommanders.Count <= 5)
        {
            return string.Join(", ", outcome.CitingCommanders);
        }

        string topFive = string.Join(", ", outcome.CitingCommanders.Take(5));
        return FormattableString.Invariant($"{topFive}; top 5 of {outcome.ClearingCommanderCount} clearing commanders");
    }

    private static string EscapePipe(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string BuildPostgresFigureRow(
        IReadOnlyDictionary<string, CommanderResearch> commanders,
        PostgresRoleDistribution distribution)
    {
        if (!commanders.TryGetValue(distribution.CommanderName, out CommanderResearch? commander))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Missing commander research row for {distribution.CommanderName}."));
        }

        // Why: criterion 8 requires every reported figure to state which source it came from, and
        // a heading-based source tag silently stops covering any column a future contributor adds.
        return FormattableString.Invariant(
            $"| {FormatRoleFloorSource(distribution.Source)} | {EscapePipe(distribution.CommanderName)} | {distribution.DeckCount} | {commander.N} | {FormatMetric(distribution.Mean)} | {FormatMetric(distribution.P25)} | {FormatMetric(distribution.Ratio)} | {FormatMetric(distribution.ZScore)} | {FormatMetric(distribution.CohensD)} | {FormatBoolean(distribution.ClearsBar)} |");
    }

    private static string BuildEdhrecFigureRow(EdhrecRolePointEstimate pointEstimate)
        => FormattableString.Invariant(
            $"| {FormatRoleFloorSource(pointEstimate.Source)} | {EscapePipe(pointEstimate.CommanderName)} | {EscapePipe(pointEstimate.BracketSlug)} | {FormatMetric(pointEstimate.Count)} | {pointEstimate.DeckCount} | {FormatBoolean(pointEstimate.Qualifies)} |");

    private static void AppendMarkdownTableHeader(StringBuilder builder, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(columns);

        builder.AppendLine(FormattableString.Invariant($"| {string.Join(" | ", columns)} |"));
        builder.AppendLine(BuildMarkdownAlignmentRow(columns));
    }

    private static string BuildMarkdownAlignmentRow(IReadOnlyList<string> columns)
        => FormattableString.Invariant(
            $"| {string.Join(" | ", columns.Select(GetMarkdownAlignmentCell))} |");

    private static string GetMarkdownAlignmentCell(string column)
        => column switch
        {
            "Source" => "------",
            "Commander" => "-----------",
            "RAW N" => "------:",
            "DEDUPED N" => "----------:",
            "Mean" => "-----:",
            "P25" => "----:",
            "Ratio" => "------:",
            "Z" => "--:",
            "Cohen's d" => "----------:",
            "ClearsBar" => "----------:",
            "Bracket" => "---------",
            "Count" => "-----:",
            "Decks backing cell" => "------------------:",
            "Qualifies" => "----------:",
            _ => throw new InvalidOperationException(FormattableString.Invariant($"Unsupported markdown column '{column}'.")),
        };

    private static string FormatRoleFloorSource(RoleFloorSource source)
        => source.ToString().ToLowerInvariant();

    private static string FormatBoolean(bool value)
        => value ? "true" : "false";

    private static string FormatEdhrecDateRange(string? minSaveDate, string? maxSaveDate)
        => string.IsNullOrWhiteSpace(minSaveDate) || string.IsNullOrWhiteSpace(maxSaveDate)
            ? "n/a"
            : FormattableString.Invariant($"{minSaveDate} to {maxSaveDate}");

    private static EdhrecLandSelfCheckSummary SummarizeEdhrecLandSelfChecks(IReadOnlyList<EdhrecLandSelfCheck> selfChecks)
    {
        ArgumentNullException.ThrowIfNull(selfChecks);

        return new EdhrecLandSelfCheckSummary
        {
            ExactMatchCount = selfChecks.Count(check => check.Delta == 0),
            WithinOneCount = selfChecks.Count(check => Math.Abs(check.Delta) == 1),
            DivergedByMoreThanOneCount = selfChecks.Count(check => Math.Abs(check.Delta) > 1),
        };
    }

    private static int ResolveCommanderDedupedN(
        IReadOnlyDictionary<string, CommanderResearch> commanders,
        string commanderName)
        => commanders.TryGetValue(commanderName, out CommanderResearch? commander)
            ? commander.N
            : 0;

    private static JsonSerializerOptions CreateResearchJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonOptions);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static string FormatMetric(double value)
    {
        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static double ComputePopulationStdDev(IReadOnlyList<double> values, double mean)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        double variance = values.Sum(value => Math.Pow(value - mean, 2)) / values.Count;
        return Math.Sqrt(variance);
    }

    private sealed class CommanderDeckSet
    {
        public required string CommanderName { get; init; }
        public required Dictionary<long, HashSet<string>> RawDecks { get; init; }
        public Dictionary<long, HashSet<string>> RepresentativeDecks { get; set; } = [];
        public Dictionary<long, Dictionary<string, int>> RepresentativeRoleCounts { get; } = [];
        public int RawN => RawDecks.Count;
        public int DedupedN => RepresentativeDecks.Count;
    }

    private sealed class ResearchComputation
    {
        public int MinDeckCount { get; init; }
        public required string DatabaseHost { get; init; }
        public required string RunTimestampUtc { get; init; }
        public required string HarnessCommitSha { get; init; }
        public int? CommanderLimit { get; init; }
        public int CommandersEnumerated { get; init; }
        public int RawDeckCount { get; init; }
        public int DedupedDeckCount { get; init; }
        public int UnresolvedNotFoundCount { get; init; }
        public int UnresolvedRateLimitedAfterRetryCount { get; init; }
        public int UnresolvedCardCount => UnresolvedNotFoundCount + UnresolvedRateLimitedAfterRetryCount;
        public required PostgresCoverage PostgresCoverage { get; init; }
        public required Dictionary<string, RoleBaseline> CorpusBaseline { get; init; }
        public required Dictionary<string, CommanderResearch> Commanders { get; init; }
        public IReadOnlyList<PostgresRoleDistribution> PostgresDistributions { get; init; } = [];
        public IReadOnlyList<EdhrecRolePointEstimate> EdhrecPointEstimates { get; init; } = [];
        public required EdhrecCoverage EdhrecCoverage { get; init; }
        public int EdhrecParseFailureCount { get; init; }
        public int EdhrecCardCountAnomalyCount { get; init; }
        public required Dictionary<int, int> ThresholdCounts { get; init; }
        public Dictionary<string, RoleOutcome> GoNoGo { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CommanderResearch
    {
        public required string CommanderName { get; init; }
        public int RawN { get; init; }
        public int N { get; init; }
        public Dictionary<string, CommanderRoleStat> Roles { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class CommanderRoleStat
    {
        public double Mean { get; init; }
        public double P25 { get; init; }
        public double Ratio { get; init; }
        public double ZScore { get; init; }
        public double CohensD { get; init; }
        public bool ClearsBar { get; init; }
    }

    private sealed class RoleBaseline
    {
        public double Mean { get; init; }
        public double StdDev { get; init; }
        public double P25 { get; init; }
    }

    private sealed class PostgresCoverage
    {
        public int CommandersEnumerated { get; init; }
        public int CommandersWithMembership { get; init; }
        public int RawDeckCount { get; init; }
        public int DedupedDeckCount { get; init; }
        public int CommandersQualifying { get; init; }
        public int UnresolvedNotFoundCount { get; init; }
        public int UnresolvedRateLimitedAfterRetryCount { get; init; }
        public int UnresolvedCardCount => UnresolvedNotFoundCount + UnresolvedRateLimitedAfterRetryCount;
    }

    private sealed class EdhrecCoverage
    {
        public int CellsFetched { get; init; }
        public int CellsQualifying { get; init; }
        public int CellsMissing { get; init; }
        public int InvalidCells { get; init; }
        public int UnexpectedCells { get; init; }
        public int CommandersReached { get; init; }
        public int MinCellDeckCount { get; init; }
        public string? MinSaveDate { get; init; }
        public string? MaxSaveDate { get; init; }
        public IReadOnlyList<EdhrecBracketCoverage> Brackets { get; init; } = [];
        public IReadOnlyList<EdhrecLandSelfCheck> LandSelfChecks { get; init; } = [];
    }

    private sealed class EdhrecBracketCoverage
    {
        public required string BracketSlug { get; init; }
        public int BracketIndex { get; init; }
        public int CellsFetched { get; init; }
        public int CellsQualifying { get; init; }
        public double MedianBackingDeckCount { get; init; }
        public required string SupportLabel { get; init; }
    }

    private sealed class EdhrecLandSelfCheck
    {
        public required string CellId { get; init; }
        public int EdhrecLandCount { get; init; }
        public int HarnessLandCount { get; init; }
        public int Delta { get; init; }
    }

    private sealed class EdhrecLandSelfCheckSummary
    {
        public int ExactMatchCount { get; init; }
        public int WithinOneCount { get; init; }
        public int DivergedByMoreThanOneCount { get; init; }
    }

    private sealed class FreshEdhrecLandBracketFigure
    {
        public int BracketIndex { get; init; }
        public double Mean { get; init; }
        public int QualifyingCellCount { get; init; }
        public required string SupportLabel { get; init; }
    }

    private sealed class ThreeReferenceAgreement
    {
        public double MaximumSpread { get; init; }
        public required IReadOnlyList<string> DivergentBrackets { get; init; }
        public required string? ClosestReferenceSet { get; init; }
        public required IReadOnlyList<string> ComparisonBrackets { get; init; }
    }

    private sealed class ProtectionMissedCardDisclosure
    {
        public ProtectionMissedCardDisclosure(string name, string evidenceGrade, string evidenceNote)
        {
            Name = name;
            EvidenceGrade = evidenceGrade;
            EvidenceNote = evidenceNote;
        }

        public string Name { get; }
        public string EvidenceGrade { get; }
        public string EvidenceNote { get; }
    }

    private sealed class RoleOutcome
    {
        public required string MarkdownStatus { get; init; }
        public required string JsonStatus { get; init; }
        public required IReadOnlyList<string> CitingCommanders { get; init; }
        public int ClearingCommanderCount { get; init; }
    }

    private sealed class CardResolutionResult
    {
        public CardResolutionResult(
            IReadOnlyDictionary<string, ScryfallCardData> resolvedCards,
            int unresolvedNotFoundCount,
            int unresolvedRateLimitedAfterRetryCount)
        {
            ResolvedCards = resolvedCards;
            UnresolvedNotFoundCount = unresolvedNotFoundCount;
            UnresolvedRateLimitedAfterRetryCount = unresolvedRateLimitedAfterRetryCount;
        }

        public IReadOnlyDictionary<string, ScryfallCardData> ResolvedCards { get; }
        public int UnresolvedNotFoundCount { get; }
        public int UnresolvedRateLimitedAfterRetryCount { get; }
        public int UnresolvedCount => UnresolvedNotFoundCount + UnresolvedRateLimitedAfterRetryCount;
    }
}
