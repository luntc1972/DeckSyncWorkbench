using DeckFlow.Core.Manabase;
using DeckFlow.Web.Models.DeckModules;
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
    private readonly IDeckModulesPageService _pageService;
    private readonly IManabaseAnalysisService _manabaseAnalysisService;
    private readonly ILogger<ConfigurationAnalysisService> _logger;

    /// <summary>Creates the configuration analysis service.</summary>
    /// <param name="pageService">Deck Modules compilation service.</param>
    /// <param name="manabaseAnalysisService">Existing manabase analysis service.</param>
    /// <param name="logger">Logger.</param>
    public ConfigurationAnalysisService(
        IDeckModulesPageService pageService,
        IManabaseAnalysisService manabaseAnalysisService,
        ILogger<ConfigurationAnalysisService> logger)
    {
        ArgumentNullException.ThrowIfNull(pageService);
        ArgumentNullException.ThrowIfNull(manabaseAnalysisService);
        ArgumentNullException.ThrowIfNull(logger);
        _pageService = pageService;
        _manabaseAnalysisService = manabaseAnalysisService;
        _logger = logger;
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

        var selectedAlternative = request.Configuration.Alternatives.First(
            alternative => alternative.Id == request.Configuration.SelectedAlternativeId);
        var isCoreOnly = selectedAlternative.MainboardEntries.Count == 0
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
                ? $"This analysis covers {compilation.TotalCardCount} cards and is not a legality verdict."
                : null,
        };

        return DeckModulesServiceResult<ConfigurationAnalysisResult>.Success(result);
    }
}
