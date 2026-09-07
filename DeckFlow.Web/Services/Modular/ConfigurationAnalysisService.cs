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
    private readonly IAnalysisWarningAttributionService _warningAttributionService;
    private readonly IGameChangerCatalogService _gameChangerCatalogService;
    private readonly ILogger<ConfigurationAnalysisService> _logger;

    /// <summary>Creates the configuration analysis service for existing callers.</summary>
    public ConfigurationAnalysisService(
        IDeckModulesPageService pageService,
        IManabaseAnalysisService manabaseAnalysisService,
        ILogger<ConfigurationAnalysisService> logger,
        IGameChangerCatalogService gameChangerCatalogService)
        : this(pageService, manabaseAnalysisService, new AnalysisWarningAttributionService(), logger, gameChangerCatalogService)
    {
    }

    /// <summary>Creates the configuration analysis service.</summary>
    /// <param name="pageService">Deck Modules compilation service.</param>
    /// <param name="manabaseAnalysisService">Existing manabase analysis service.</param>
    /// <param name="warningAttributionService">Warning attribution service.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="gameChangerCatalogService">Warm-cached Game Changer catalog service.</param>
    public ConfigurationAnalysisService(
        IDeckModulesPageService pageService,
        IManabaseAnalysisService manabaseAnalysisService,
        IAnalysisWarningAttributionService warningAttributionService,
        ILogger<ConfigurationAnalysisService> logger,
        IGameChangerCatalogService gameChangerCatalogService)
    {
        ArgumentNullException.ThrowIfNull(pageService);
        ArgumentNullException.ThrowIfNull(manabaseAnalysisService);
        ArgumentNullException.ThrowIfNull(warningAttributionService);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(gameChangerCatalogService);
        _pageService = pageService;
        _manabaseAnalysisService = manabaseAnalysisService;
        _warningAttributionService = warningAttributionService;
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
            // WR-05: DeclaredProfileRanges is a hand-maintained dictionary; the indexer was safe
            // only because DeckModulesPageService.ValidateAlternative runs Enum.IsDefined first --
            // an implicit, undocumented coupling across two files. Adding a profile to the enum
            // without a matching range entry would compile clean and 500 at runtime.
            if (!DeclaredProfileRanges.TryGetValue(declaredAlternative.Profile, out var profileRange))
            {
                _logger.LogWarning(
                    "No declared bracket range for profile {Profile}; skipping the declared-profile disclosure.",
                    declaredAlternative.Profile);
            }
            else
            {
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
        }
        var decklistText = DeckModulesDecklistSerializer.BuildAnalysisDecklistText(compilation);

        var analysisResult = await _manabaseAnalysisService.AnalyzeAsync(
            decklistText,
            compilation.SelectedStrategyName,
            new ManabaseAnalysisOptions
            {
                Mode = request.Mode,
                // ANL-01: this explicit, user-triggered analysis runs once per Analyze press, never on a keystroke, so plan-role lookups are safe here.
                ClassifyPlanRoles = true,
            },
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
        var moduleMap = ConfigurationModuleMap.Build(request.Configuration);
        var attributedFindings = _warningAttributionService.AttributeFindings(
            report.ColorFindings,
            compilation.SwapPlan,
            moduleMap);
        signals = signals with
        {
            InteractionAttributionAvailable = analysisResult.AnalyzedSpells.Count > 0,
            InteractionsByModule = BuildInteractionRows(analysisResult.AnalyzedSpells, moduleMap),
        };

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
            AttributedFindings = attributedFindings,
            UnresolvedCardNames = analysisResult.Unresolved,
            IsCoreOnly = isCoreOnly,
            AnalysisNotice = BuildAnalysisNotice(compilation, isCoreOnly),
            Signals = signals,
            ManabaseHandoffPayload = new ManabaseHandoffPayload
            {
                Result = analysisResult,
                DecklistText = decklistText,
                DeckName = compilation.SelectedStrategyName,
                Mode = request.Mode,
            },
        };

        return DeckModulesServiceResult<ConfigurationAnalysisResult>.Success(result);
    }

    /// <summary>
    /// WR-06: Export refuses to ship a structurally invalid compilation
    /// (<c>!compilation.IsStructurallyValid</c>), but Analyze previously proceeded regardless --
    /// a build carrying Overlap/UnknownStrategy/MissingSelection/TotalCardCount diagnostics still
    /// produced a confident Health, TargetLandCount, and bracket number with no caveat at all
    /// (the only notice was the core-only one). Analyze stays advisory-only by design (D-22:
    /// this record never carries an IsValid/IsStructurallyValid verdict), so it still returns the
    /// numbers -- it now discloses the same caveat Export enforces instead of staying silent.
    /// </summary>
    private static string? BuildAnalysisNotice(DeckModulesCompilationViewModel compilation, bool isCoreOnly)
    {
        if (isCoreOnly)
        {
            return $"Analysed {compilation.TotalCardCount} cards — this configuration is missing its strategy module, so these numbers describe an incomplete deck and are not a legality verdict.";
        }

        if (!compilation.IsStructurallyValid)
        {
            return $"Analysed {compilation.TotalCardCount} cards — this compiled configuration has unresolved compilation diagnostics, so these numbers describe a build that isn't legal yet and are not a legality verdict.";
        }

        return null;
    }

    private static IReadOnlyList<ConfigurationModuleInteractionCount> BuildInteractionRows(
        IReadOnlyList<SpellRequirement> analyzedSpells,
        ConfigurationModuleMap moduleMap)
    {
        if (analyzedSpells.Count == 0)
        {
            return [];
        }

        var counts = new Dictionary<ConfigurationModuleKind, int>();
        foreach (var spell in analyzedSpells)
        {
            if ((spell.PlanRoles & PlanRole.Interaction) == 0 && !spell.IsInteractionSpell)
            {
                continue;
            }

            if (moduleMap.TryResolve(spell.Name, out var kind, out _))
            {
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
            }
        }

        return
        [
            InteractionRow(ConfigurationModuleKind.CommandZone, "Command Zone", counts),
            InteractionRow(ConfigurationModuleKind.Core, "Core", counts),
            InteractionRow(ConfigurationModuleKind.Strategy, "Strategy", counts),
            InteractionRow(ConfigurationModuleKind.ManaSupport, "Mana Support", counts),
            InteractionRow(ConfigurationModuleKind.Multiple, "More than one module", counts),
        ];
    }

    private static ConfigurationModuleInteractionCount InteractionRow(
        ConfigurationModuleKind moduleKind,
        string moduleName,
        IReadOnlyDictionary<ConfigurationModuleKind, int> counts) => new()
        {
            ModuleKind = moduleKind,
            ModuleName = moduleName,
            InteractionCount = counts.GetValueOrDefault(moduleKind),
        };

    private sealed record DeclaredProfileRange(string DisplayLabel, int MinimumBracket, int MaximumBracket);
}
