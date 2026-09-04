using System.Net;
using DeckFlow.Core.Content;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.MeasuredStyleExtraction;
using DeckFlow.Core.Knowledge.ProfileFusion;
using DeckFlow.Core.Knowledge.StatedRulesExtraction;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Manabase;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RestSharp;

namespace DeckFlow.Web.Services.CreatorStyle;

/// <summary>
/// Orchestrates creator measured-style extraction and persistence.
/// </summary>
public sealed class MeasuredStyleProfileBuilder
{
    private const int MaxLiftMetrics = 25;

    // Why: bounds per-creator combo/manabase fan-out (up to ArchidektOwnerClient.MaxDecks = 500
    // decks) so a large creator cannot launch hundreds of concurrent Scryfall-backed analyses on
    // the 512MB Render tier; each caller still funnels through the process-wide ScryfallThrottle,
    // this just caps how many are queued behind it at once.
    private const int MaxConcurrentDeckAnalyses = 4;
    private static readonly HashSet<string> AnalyzedBoards = new(StringComparer.OrdinalIgnoreCase)
    {
        "mainboard",
        "commander"
    };

    private readonly CreatorProfileDeckCrawler _deckCrawler;
    private readonly CreatorDeckCategoryResolver _categoryResolver;
    private readonly CategoryKnowledgeRepository _categoryKnowledgeRepository;
    private readonly ICommanderSpellbookService _commanderSpellbookService;
    private readonly IScryfallCardResolver _scryfallCardResolver;
    private readonly ICreatorStyleProfileStore _profileStore;
    private readonly ILogger<MeasuredStyleProfileBuilder> _logger;
    private readonly Func<DateTimeOffset> _nowUtc;

    /// <summary>
    /// Creates a measured-style profile builder.
    /// </summary>
    public MeasuredStyleProfileBuilder(
        CreatorProfileDeckCrawler deckCrawler,
        CreatorDeckCategoryResolver categoryResolver,
        CategoryKnowledgeRepository categoryKnowledgeRepository,
        ICommanderSpellbookService commanderSpellbookService,
        IScryfallCardResolver scryfallCardResolver,
        ICreatorStyleProfileStore profileStore,
        ILogger<MeasuredStyleProfileBuilder>? logger = null)
        : this(
            deckCrawler,
            categoryResolver,
            categoryKnowledgeRepository,
            commanderSpellbookService,
            scryfallCardResolver,
            profileStore,
            logger,
            null)
    {
    }

    internal MeasuredStyleProfileBuilder(
        CreatorProfileDeckCrawler deckCrawler,
        CreatorDeckCategoryResolver categoryResolver,
        CategoryKnowledgeRepository categoryKnowledgeRepository,
        ICommanderSpellbookService commanderSpellbookService,
        IScryfallCardResolver scryfallCardResolver,
        ICreatorStyleProfileStore profileStore,
        ILogger<MeasuredStyleProfileBuilder>? logger,
        Func<DateTimeOffset>? nowUtc)
    {
        ArgumentNullException.ThrowIfNull(deckCrawler);
        ArgumentNullException.ThrowIfNull(categoryResolver);
        ArgumentNullException.ThrowIfNull(categoryKnowledgeRepository);
        ArgumentNullException.ThrowIfNull(commanderSpellbookService);
        ArgumentNullException.ThrowIfNull(scryfallCardResolver);
        ArgumentNullException.ThrowIfNull(profileStore);
        _deckCrawler = deckCrawler;
        _categoryResolver = categoryResolver;
        _categoryKnowledgeRepository = categoryKnowledgeRepository;
        _commanderSpellbookService = commanderSpellbookService;
        _scryfallCardResolver = scryfallCardResolver;
        _profileStore = profileStore;
        _logger = logger ?? NullLogger<MeasuredStyleProfileBuilder>.Instance;
        _nowUtc = nowUtc ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Builds and persists a measured creator style profile for the supplied creator slug.
    /// </summary>
    /// <param name="creatorSlug">Creator slug.</param>
    /// <param name="platform">Creator platform identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted measured creator style profile.</returns>
    public async Task<CreatorStyleProfile> BuildAsync(
        string creatorSlug,
        string platform,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(creatorSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        IReadOnlyList<CreatorDeckSample> crawledSamples = await _deckCrawler
            .CrawlAsync(creatorSlug, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<CreatorDeckSample> filteredSamples = StapleStripper.FilterOversized(crawledSamples);
        IReadOnlyList<CreatorDeckSample> flaggedSamples = StapleStripper.FlagNearPrecons(filteredSamples);
        IReadOnlyDictionary<string, IReadOnlyList<string>> cardCategories = await _categoryResolver
            .ResolveAsync(flaggedSamples, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlySet<string> personalStaples = StapleStripper.ComputePersonalStaples(flaggedSamples);
        IReadOnlyList<CreatorDeckSample> strippedSamples = StapleStripper.StripStaples(flaggedSamples, personalStaples);
        IReadOnlyList<CreatorDeckSample> weightedSamples = FolderWeighting.ApplyWeights(
            strippedSamples,
            flaggedSamples
                .Where(sample => sample.FolderId.HasValue)
                .GroupBy(sample => sample.FolderId!.Value)
                .ToDictionary(group => group.Key, group => group.First().FolderWeight),
            weightsUncurated: flaggedSamples.All(sample => Math.Abs(sample.FolderWeight - 1.0) < 0.0001));

        int rawDeckCount = FolderWeighting.RawDeckCount(weightedSamples);
        double effectiveSampleSize = FolderWeighting.EffectiveSampleSize(weightedSamples);
        GlobalCategoryBaseline baseline = await _categoryKnowledgeRepository
            .GetGlobalCategoryBaselineAsync(cancellationToken)
            .ConfigureAwait(false);

        List<MeasuredMetric> metrics = BuildCategoryMetrics(weightedSamples, cardCategories, rawDeckCount, effectiveSampleSize);
        metrics.AddRange(BuildLiftMetrics(weightedSamples, cardCategories, baseline, rawDeckCount, effectiveSampleSize));
        // Why (WR-03): each of these already fans out per-deck work at MaxConcurrentDeckAnalyses;
        // starting them concurrently doubles peak concurrency to 8 on the 512MB Render tier the
        // cap exists to protect, so they run sequentially instead.
        MeasuredMetric comboDensity = await BuildComboDensityMetricAsync(weightedSamples, rawDeckCount, effectiveSampleSize, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MeasuredMetric> karstenMetrics = await BuildKarstenMetricsAsync(weightedSamples, rawDeckCount, effectiveSampleSize, cancellationToken).ConfigureAwait(false);
        metrics.Add(comboDensity);
        metrics.AddRange(karstenMetrics);
        metrics = metrics
            .OrderBy(metric => metric.Metric, StringComparer.Ordinal)
            .ToList();

        CreatorStyleProfile? existingProfile = await _profileStore
            .GetBySlugAsync(creatorSlug, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<StatedRule> statedRules = existingProfile?.StatedRules ?? Array.Empty<StatedRule>();
        IReadOnlyList<FusedTarget> fusedTargets = ProfileFusionEngine.Fuse(
            metrics,
            ToCandidates(statedRules));

        var profile = new CreatorStyleProfile
        {
            Slug = creatorSlug,
            Platform = platform,
            MinDecks = rawDeckCount,
            InsufficientSample = rawDeckCount < CreatorStyleProfile.MinDeckFloor,
            MeasuredMetrics = metrics,
            StatedRules = statedRules,
            FusedTargets = fusedTargets,
            UpdatedUtc = _nowUtc()
        };

        await _profileStore.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    private static IReadOnlyList<StatedRuleCandidate> ToCandidates(IReadOnlyList<StatedRule> statedRules)
    {
        return statedRules
            .Select(rule => new StatedRuleCandidate
            {
                Category = rule.Category,
                Metric = rule.TargetMetric,
                Value = rule.TargetValue,
                ValueMin = rule.TargetValueMin,
                ValueMax = rule.TargetValueMax,
                Comparator = rule.Comparator,
                Condition = rule.Condition,
                SourceClip = rule.SourceClip,
                Confidence = rule.Confidence,
                VideoDateUtc = rule.VideoDateUtc
            })
            .ToArray();
    }

    private static List<MeasuredMetric> BuildCategoryMetrics(
        IReadOnlyList<CreatorDeckSample> samples,
        IReadOnlyDictionary<string, IReadOnlyList<string>> cardCategories,
        int rawDeckCount,
        double effectiveSampleSize)
    {
        var metrics = new List<MeasuredMetric>();
        List<IReadOnlyDictionary<string, int>> perDeckCounts = samples
            .Select(sample => CategoryCounter.CountPerDeck(sample, cardCategories))
            .ToList();
        IEnumerable<string> categories = perDeckCounts
            .SelectMany(counts => counts.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase);

        foreach (string category in categories)
        {
            IReadOnlyList<double> perDeckValues = perDeckCounts
                .Select(counts => counts.TryGetValue(category, out var count) ? (double)count : 0d)
                .ToArray();

            metrics.Add(new MeasuredMetric
            {
                Metric = $"category_ratio:{category}",
                Value = perDeckValues.Count == 0 ? 0 : perDeckValues.Average(),
                NumDecks = rawDeckCount,
                Distribution = BuildDistribution(perDeckValues, effectiveSampleSize)
            });
        }

        return metrics;
    }

    private static IEnumerable<MeasuredMetric> BuildLiftMetrics(
        IReadOnlyList<CreatorDeckSample> samples,
        IReadOnlyDictionary<string, IReadOnlyList<string>> cardCategories,
        GlobalCategoryBaseline baseline,
        int rawDeckCount,
        double effectiveSampleSize)
    {
        return LiftCalculator.ComputeLift(samples, cardCategories, baseline)
            .Take(MaxLiftMetrics)
            .Select(item => new MeasuredMetric
            {
                Metric = $"lift:{item.CategoryA}|{item.CategoryB}",
                Value = item.Lift,
                NumDecks = rawDeckCount,
                Distribution = BuildDistribution([item.Lift], effectiveSampleSize)
            })
            .ToList();
    }

    private async Task<MeasuredMetric> BuildComboDensityMetricAsync(
        IReadOnlyList<CreatorDeckSample> samples,
        int rawDeckCount,
        double effectiveSampleSize,
        CancellationToken cancellationToken)
    {
        var comboCounts = new double[samples.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, samples.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentDeckAnalyses, CancellationToken = cancellationToken },
            async (index, ct) =>
            {
                comboCounts[index] = await ResolveComboCountAsync(samples[index].Entries, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return AverageMetric("combo_density:included_per_deck", comboCounts, rawDeckCount, effectiveSampleSize);
    }

    private async Task<IReadOnlyList<MeasuredMetric>> BuildKarstenMetricsAsync(
        IReadOnlyList<CreatorDeckSample> samples,
        int rawDeckCount,
        double effectiveSampleSize,
        CancellationToken cancellationToken)
    {
        var reports = new ManabaseReport[samples.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, samples.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentDeckAnalyses, CancellationToken = cancellationToken },
            async (index, ct) =>
            {
                reports[index] = await AnalyzeDeckAsync(samples[index].Entries, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        IReadOnlyList<double> landDelta = reports.Select(report => report.LandDelta).ToArray();
        IReadOnlyList<double> targetLands = reports.Select(report => report.TargetLands).ToArray();
        IReadOnlyList<double> healthScores = reports.Select(report => CreatorStyleDeckAnalysis.ToHealthScore(report.Health)).ToArray();

        return
        [
            AverageMetric("karsten:land_delta", landDelta, rawDeckCount, effectiveSampleSize),
            AverageMetric("karsten:target_lands", targetLands, rawDeckCount, effectiveSampleSize),
            AverageMetric("karsten:health_score", healthScores, rawDeckCount, effectiveSampleSize)
        ];
    }

    private static MeasuredMetric AverageMetric(
        string metricName,
        IReadOnlyList<double> values,
        int rawDeckCount,
        double effectiveSampleSize)
    {
        return new MeasuredMetric
        {
            Metric = metricName,
            Value = values.Count == 0 ? 0 : values.Average(),
            NumDecks = rawDeckCount,
            Distribution = BuildDistribution(values, effectiveSampleSize)
        };
    }

    private async Task<double> ResolveComboCountAsync(
        IReadOnlyList<DeckEntry> entries,
        CancellationToken cancellationToken)
    {
        CommanderSpellbookResult? result = await _commanderSpellbookService
            .FindCombosAsync(entries, cancellationToken)
            .ConfigureAwait(false);

        return result?.IncludedCombos.Count ?? 0;
    }

    private async Task<ManabaseReport> AnalyzeDeckAsync(
        IReadOnlyList<DeckEntry> entries,
        CancellationToken cancellationToken)
    {
        // Why (WR-12): delegate to the shared Core helper instead of a private copy so this
        // commander-inference rule cannot drift from SubmittedDeckStatsBuilder's, since the two
        // sides' karsten:*/category_ratio:* metrics are compared head-to-head by the rubric scorer.
        List<DeckEntry> deckCards = CommanderInference.ReflagInferredCommanders(entries.ToList())
            .Where(entry => AnalyzedBoards.Contains(entry.Board ?? string.Empty))
            .ToList();

        if (deckCards.Count == 0)
        {
            return CreatorStyleDeckAnalysis.EmptyReport();
        }

        // Shared with SubmittedDeckStatsBuilder; keep creator-style manabase resolution behavior
        // aligned in CreatorStyleDeckAnalysis.
        return await CreatorStyleDeckAnalysis.AnalyzeDeckAsync(
            deckCards,
            _scryfallCardResolver.ExecuteCollectionAsync,
            _scryfallCardResolver.SearchFallbackCardAsync,
            cardName => _logger.LogDebug("Skipping unresolved creator-style manabase card {CardName}.", cardName),
            "creator-style manabase analysis.",
            cancellationToken).ConfigureAwait(false);
    }

    private static MetricDistribution BuildDistribution(IReadOnlyList<double> values, double effectiveSampleSize)
    {
        if (values.Count == 0)
        {
            return new MetricDistribution
            {
                Mean = 0,
                Min = 0,
                Max = 0,
                StdDev = 0,
                EffectiveSampleSize = effectiveSampleSize
            };
        }

        double mean = values.Average();
        double variance = values.Sum(value => Math.Pow(value - mean, 2)) / values.Count;

        return new MetricDistribution
        {
            Mean = mean,
            Min = values.Min(),
            Max = values.Max(),
            StdDev = Math.Sqrt(variance),
            EffectiveSampleSize = effectiveSampleSize
        };
    }

}
