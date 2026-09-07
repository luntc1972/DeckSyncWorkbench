using DeckFlow.Core.Bracket;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Services.Bracket;
using DeckFlow.Web.Services.Manabase;

namespace DeckFlow.Web.Services.Modular;

/// <summary>
/// Compiles a Deck Modules configuration and hands the compiled decklist to the existing
/// manabase analysis service, projecting the result onto <see cref="ConfigurationAnalysisResult"/>.
/// Structurally separated from compilation (<see cref="IDeckModulesPageService.Compile"/>) so
/// editing a card assignment can never trigger an analysis.
/// </summary>
public sealed class ConfigurationAnalysisService : IConfigurationAnalysisService
{
    /// <summary>Maps each declared profile to its display label and implied inclusive bracket range.</summary>
    private static readonly IReadOnlyDictionary<DeckModulesProfile, DeclaredProfileRange> DeclaredProfileRanges = new Dictionary<DeckModulesProfile, DeclaredProfileRange>
    {
        [DeckModulesProfile.Casual] = new("Casual", 1, 3),
        [DeckModulesProfile.Bracket4HighPower] = new("Bracket 4 High Power", 4, 4),
        [DeckModulesProfile.Cedh] = new("cEDH", 5, 5),
    };

    private readonly IDeckModulesPageService _pageService;
    private readonly IManabaseAnalysisService _manabaseAnalysisService;
    private readonly IGameChangerCatalogService _gameChangerCatalogService;
    private readonly ILogger<ConfigurationAnalysisService> _logger;

    /// <summary>Creates the configuration analysis service.</summary>
    /// <param name="pageService">Deck Modules compilation service.</param>
    /// <param name="manabaseAnalysisService">Existing manabase analysis service.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="gameChangerCatalogService">Warm-cached Game Changer catalog service.</param>
    public ConfigurationAnalysisService(
        IDeckModulesPageService pageService,
        IManabaseAnalysisService manabaseAnalysisService,
        ILogger<ConfigurationAnalysisService> logger,
        IGameChangerCatalogService gameChangerCatalogService)
    {
        ArgumentNullException.ThrowIfNull(pageService);
        ArgumentNullException.ThrowIfNull(manabaseAnalysisService);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(gameChangerCatalogService);
        _pageService = pageService;
        _manabaseAnalysisService = manabaseAnalysisService;
        _logger = logger;
        _gameChangerCatalogService = gameChangerCatalogService;
    }

    /// <inheritdoc />
    public async Task<DeckModulesServiceResult<ConfigurationAnalysisResult>> AnalyzeAsync(
        ConfigurationAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var compileResult = _pageService.Compile(request.Configuration);
        if (!compileResult.Succeeded)
        {
            return DeckModulesServiceResult<ConfigurationAnalysisResult>.Failure(compileResult.ErrorMessage!);
        }

        var compilation = compileResult.Value!;
        var boardedEntries = compilation.CommandZoneEntries
            .Select(static entry => new DeckEntry { Name = entry.Name, NormalizedName = entry.NormalizedName, Quantity = entry.Quantity, Board = "commander" })
            .Concat(compilation.MainboardEntries.Select(static entry => new DeckEntry { Name = entry.Name, NormalizedName = entry.NormalizedName, Quantity = entry.Quantity, Board = "mainboard" }))
            .ToList();
        // No combo detection service runs on this path; null means unavailable, while an empty list would falsely claim detection ran.
        var classification = BracketClassifier.Classify(boardedEntries, _gameChangerCatalogService.GetCatalog(), twoCardCombos: null);
        var signals = new ConfigurationSignalSummary
        {
            BracketNumber = classification.BracketNumber,
            GameChangers = classification.DetectedGameChangers,
            MassLandDenialCards = classification.DetectedMassLandDenial,
            ExtraTurnCards = classification.DetectedExtraTurnCards,
            ComboDetectionAvailable = classification.ComboDetectionAvailable,
            CatalogEffectiveDate = classification.EffectiveDate,
        };
        var declaredAlternative = request.Configuration.Alternatives.FirstOrDefault(
            alternative => alternative.Id == request.Configuration.SelectedAlternativeId);
        if (declaredAlternative is not null)
        {
            var profileRange = DeclaredProfileRanges[declaredAlternative.Profile];
            var profileDisagreementNote = signals.BracketNumber < profileRange.MinimumBracket
                || signals.BracketNumber > profileRange.MaximumBracket
                ? $"You declared {profileRange.DisplayLabel}; the bracket rubric reads this list as bracket {signals.BracketNumber}."
                : null;
            signals = signals with
            {
                Declared = new ConfigurationDeclaredDisclosure
                {
                    Profile = profileRange.DisplayLabel,
                    PlayPlan = declaredAlternative.PlayPlan,
                    IsDeclared = true,
                    ProfileDisagreementNote = profileDisagreementNote,
                },
            };
        }
        var decklistText = DeckModulesDecklistSerializer.BuildAnalysisDecklistText(compilation);

        var analysisResult = await _manabaseAnalysisService.AnalyzeAsync(
            decklistText,
            compilation.SelectedStrategyName,
            new ManabaseAnalysisOptions { Mode = request.Mode },
            cancellationToken);

        if (analysisResult.Report is null)
        {
            _logger.LogInformation(
                "Deck Modules analysis requires commander selection: {ChoiceCount} choices.",
                analysisResult.CommanderChoices.Count);
            return DeckModulesServiceResult<ConfigurationAnalysisResult>.Failure(
                $"Commander selection is required before this configuration can be analysed ({analysisResult.CommanderChoices.Count} eligible commanders found).");
        }

        var report = analysisResult.Report;
        var hardToCastCount = report.Castability.Count(row => !row.IsCommander && row.CastPercent < ConfigurationAnalysisResult.HardToCastCastPercentThreshold);
        var colorSources = report.ColorFindings
            .Select(finding => new ConfigurationColorSourceRow
            {
                Color = finding.Color.ToString(),
                DisplayColor = finding.IsSpecialCategory ? finding.DisplayColor : finding.Color.ToString(),
                ActualSources = finding.ActualSources,
                RequiredSources = finding.RequiredSources,
                Deficit = finding.Deficit,
                DrivingSpell = finding.DrivingSpell,
            })
            .ToArray();

        var selectedAlternative = request.Configuration.Alternatives.FirstOrDefault(
            alternative => alternative.Id == request.Configuration.SelectedAlternativeId);
        var isCoreOnly = selectedAlternative is not null
            && selectedAlternative.MainboardEntries.Count == 0
            && selectedAlternative.ManaSupportEntries.Count == 0;
        var result = new ConfigurationAnalysisResult
        {
            ConfigurationId = compilation.SelectedStrategyId,
            ConfigurationName = compilation.SelectedStrategyName,
            AnalyzedCardCount = compilation.TotalCardCount,
            LandCount = report.ActualLands,
            TargetLandCount = report.TargetLands,
            LandDelta = report.LandDelta,
            Health = report.Health.ToString(),
            RampSourceCount = report.RampSourceCount,
            HardToCastCount = hardToCastCount,
            ColorSources = colorSources,
            UnresolvedCardNames = analysisResult.Unresolved,
            IsCoreOnly = isCoreOnly,
            AnalysisNotice = isCoreOnly
                ? $"Analysed {compilation.TotalCardCount} cards — this configuration is missing its strategy module, so these numbers describe an incomplete deck and are not a legality verdict."
                : null,
            Signals = signals,
        };

        return DeckModulesServiceResult<ConfigurationAnalysisResult>.Success(result);
    }

    private sealed record DeclaredProfileRange(string DisplayLabel, int MinimumBracket, int MaximumBracket);
}
