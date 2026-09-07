using DeckFlow.Core.Analysis;
using DeckFlow.Core.Manabase;
using DeckFlow.Web.Infrastructure;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.Api;
using DeckFlow.Web.Models.CutLab;
using DeckFlow.Web.Security;
using DeckFlow.Web.Services.CutLab;
using DeckFlow.Web.Services.FeatureFlags;
using Microsoft.AspNetCore.Mvc;

namespace DeckFlow.Web.Controllers.Api;

/// <summary>Exposes Cut Lab decision application through the JSON API.</summary>
[ApiController]
[Route("api/cut-lab")]
public sealed class CutLabApiController : ControllerBase
{
    private const string InvalidStateMessage = "Cut Lab state is invalid. Re-import the pool and try again.";

    private readonly ICutLabAnalysisContextBuilder _contextBuilder;
    private readonly ICutLabFloorResolver _floorResolver;
    private readonly ICutLabUiPatchBuilder _patchBuilder;
    private readonly ICutLabWhatifService _whatifService;
    private readonly IFeatureFlagCache _featureFlags;
    private readonly ILogger<CutLabApiController> _logger;
    private readonly ICutLabPlanAffinityFactory _planAffinityFactory;
    private readonly IEdhrecCommanderThemeService _themeService;

    /// <summary>Creates the Cut Lab API controller.</summary>
    /// <param name="contextBuilder">Shared analysis-context builder reused by intake and decision flows.</param>
    /// <param name="floorResolver">Shared floor resolver reused across Cut Lab transports.</param>
    /// <param name="patchBuilder">Shared UI patch builder reused by mutation endpoints.</param>
    /// <param name="whatifService">Shared what-if preview service reused by API and no-JS swap flows.</param>
    /// <param name="featureFlags">Feature-flag cache used to gate the functional-twins detector.</param>
    /// <param name="logger">Logger used for non-fatal API warnings.</param>
    /// <param name="planAffinityFactory">Optional shared plan-affinity factory used to resolve the checked plan profile against the pool.</param>
    /// <param name="themeService">Optional EDHREC commander-theme source used to re-validate the plan panel's checked theme slugs on apply.</param>
    public CutLabApiController(
        ICutLabAnalysisContextBuilder contextBuilder,
        ICutLabFloorResolver floorResolver,
        ICutLabUiPatchBuilder patchBuilder,
        ICutLabWhatifService whatifService,
        IFeatureFlagCache featureFlags,
        ILogger<CutLabApiController> logger,
        ICutLabPlanAffinityFactory? planAffinityFactory = null,
        IEdhrecCommanderThemeService? themeService = null)
    {
        _contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
        _floorResolver = floorResolver ?? throw new ArgumentNullException(nameof(floorResolver));
        _patchBuilder = patchBuilder ?? throw new ArgumentNullException(nameof(patchBuilder));
        _whatifService = whatifService ?? throw new ArgumentNullException(nameof(whatifService));
        _featureFlags = featureFlags ?? throw new ArgumentNullException(nameof(featureFlags));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _planAffinityFactory = planAffinityFactory ?? NullCutLabPlanAffinityFactory.Instance;
        _themeService = themeService ?? NullEdhrecCommanderThemeService.Instance;
    }

    /// <summary>Applies one Cut Lab decision and returns the next proposal payload.</summary>
    /// <param name="request">Decision request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated state plus the next proposal surface.</returns>
    [HttpPost("decide")]
    [FeatureFlagGate("tool.cut-lab.enabled")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [ProducesResponseType(typeof(CutLabDecideApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CutLabDecideApiResponse>> PostDecideAsync([FromBody] CutLabDecideApiRequest request, CancellationToken cancellationToken)
    {
        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Message = SameOriginRequestValidator.GetForbiddenMessage() });
        }

        if (request is null)
        {
            return BadRequest(new { Message = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.CutLabStateJson) || string.IsNullOrWhiteSpace(request.CardName))
        {
            return BadRequest(new { Message = "Cut Lab state and card name are required." });
        }

        try
        {
            CutLabState state = SanitizePlanProfile(CutLabStateSerializer.Deserialize(request.CutLabStateJson));
            if (state.Pool.Count == 0)
            {
                return BadRequest(new { Message = InvalidStateMessage });
            }

            IReadOnlyList<string> commanderNames = CutLabCommanderNames.Resolve(state);
            bool twinsEnabled = IsFlagOn(CutLabStructuralFindings.FunctionalTwinsFlagKey);
            IReadOnlyList<CutLabPoolCard> fullPool = state.Pool;

            IReadOnlyList<CutLabPoolCard> beforeWorkingList = CutLabWorkingList.Derive(state.Pool, state.Decisions, state.QuantityAdjustments);
            string beforePoolKey = CutLabResolvedCardCache.ComputePoolKey(beforeWorkingList);
            CutLabAnalysisContext beforeContext = await _contextBuilder.BuildAsync(
                beforeWorkingList,
                state.Intent.PlayExperience,
                commanderNames,
                poolKey: beforePoolKey,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, int> floorByRole = _floorResolver.Resolve(state, beforeContext.CommanderManaValue, commanderNames)
                .ToDictionary(
                    floor => floor.Role,
                    floor => floor.Floor,
                    StringComparer.OrdinalIgnoreCase);
            IReadOnlyDictionary<string, CutLabPlanAffinity>? planAffinities = await _planAffinityFactory.BuildAsync(
                state.Intent.PlanProfile,
                beforeContext.AnalyzedCards,
                commanderNames,
                cancellationToken).ConfigureAwait(false);
            (_, CutLabRoundPlan beforeRoundPlan) = CutLabCutRoundEngine.BuildFindingsAndRoundPlan(
                beforeWorkingList,
                beforeContext,
                floorByRole,
                state.Decisions,
                twinsEnabled,
                planAffinities: planAffinities);

            string roundKey = DetermineRoundKey(state, request, beforeRoundPlan);
            IReadOnlyList<CutLabDecideFloorWarningDto> floorWarnings = request.Decision == CutLabDecideAction.Accept
                ? CutLabSharedHelpers.BuildFloorWarnings(beforeWorkingList, beforeContext, floorByRole, request.CardName)
                : [];
            state = CutLabDecisionApplier.Apply(state, request.CardName, request.Decision, roundKey);

            IReadOnlyList<CutLabPoolCard> afterWorkingList = CutLabWorkingList.Derive(state.Pool, state.Decisions, state.QuantityAdjustments);
            string afterPoolKey = CutLabResolvedCardCache.ComputePoolKey(afterWorkingList);
            IReadOnlyList<ScryfallCardData>? afterPreResolvedCards = TryBuildAfterPreResolvedCards(
                fullPool,
                afterWorkingList,
                beforeContext.ResolvedCards);
            if (request.Decision == CutLabDecideAction.Restore)
            {
                CutLabAnalysisContext afterContext = await _contextBuilder.BuildAsync(
                    afterWorkingList,
                    state.Intent.PlayExperience,
                    commanderNames,
                    afterPreResolvedCards,
                    afterPoolKey,
                    cancellationToken).ConfigureAwait(false);
                planAffinities = await _planAffinityFactory.BuildAsync(
                    state.Intent.PlanProfile,
                    afterContext.AnalyzedCards,
                    commanderNames,
                    cancellationToken).ConfigureAwait(false);
            }
            CutLabUiPatchDto patch = await _patchBuilder.BuildAsync(
                state,
                state.Intent.PlayExperience,
                commanderNames,
                twinsEnabled,
                afterPreResolvedCards,
                afterPoolKey,
                floorWarnings,
                planAffinities,
                cancellationToken).ConfigureAwait(false);
            return Ok(BuildDecideApiResponse(patch));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(exception, "Cut Lab decide API request failed.");
            return BadRequest(new { Message = CutLabMessages.NoChangeMessage });
        }
    }

    /// <summary>Applies one Cut Lab quantity adjustment and returns the updated state plus card count.</summary>
    /// <param name="request">Quantity-adjustment request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated serialized state plus cards remaining to target.</returns>
    [HttpPost("adjust")]
    [FeatureFlagGate("tool.cut-lab.enabled")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [ProducesResponseType(typeof(CutLabAdjustApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<CutLabAdjustApiResponse>> PostAdjustAsync([FromBody] CutLabAdjustApiRequest request, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return Task.FromResult<ActionResult<CutLabAdjustApiResponse>>(StatusCode(StatusCodes.Status403Forbidden, new { Message = SameOriginRequestValidator.GetForbiddenMessage() }));
        }

        if (request is null)
        {
            return Task.FromResult<ActionResult<CutLabAdjustApiResponse>>(BadRequest(new { Message = "Request body is required." }));
        }

        if (string.IsNullOrWhiteSpace(request.CutLabStateJson) || string.IsNullOrWhiteSpace(request.CardName))
        {
            return Task.FromResult<ActionResult<CutLabAdjustApiResponse>>(BadRequest(new { Message = "Cut Lab state and card name are required." }));
        }

        try
        {
            CutLabState state = SanitizePlanProfile(CutLabStateSerializer.Deserialize(request.CutLabStateJson));
            if (state.Pool.Count == 0)
            {
                return Task.FromResult<ActionResult<CutLabAdjustApiResponse>>(BadRequest(new { Message = InvalidStateMessage }));
            }

            IReadOnlyList<string> commanderNames = CutLabCommanderNames.Resolve(state);
            state = CutLabAdjustmentApplier.Apply(state, request.CardName, request.Delta, request.IsAddedBasic);
            if (_patchBuilder is not CutLabUiPatchBuilder adjustPatchBuilder)
            {
                throw new InvalidOperationException("Cut Lab adjust requests require the default UI patch builder.");
            }

            CutLabUiPatchDto patch = adjustPatchBuilder.BuildAdjustPatch(state, commanderNames);

            return Task.FromResult<ActionResult<CutLabAdjustApiResponse>>(Ok(new CutLabAdjustApiResponse
            {
                Patch = patch,
                CutLabStateJson = patch.CutLabStateJson,
                CardsRemaining = patch.CardsRemaining,
            }));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(exception, "Cut Lab adjust API request failed.");
            return Task.FromResult<ActionResult<CutLabAdjustApiResponse>>(BadRequest(new { Message = CutLabMessages.NoChangeMessage }));
        }
    }

    /// <summary>Re-surfaces prior round 1 and round 2 rejects/defers without undoing accepted cuts.</summary>
    /// <param name="request">Restart request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated state plus the next proposal surface.</returns>
    [HttpPost("restart-rounds")]
    [FeatureFlagGate("tool.cut-lab.enabled")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [ProducesResponseType(typeof(CutLabDecideApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CutLabDecideApiResponse>> PostRestartRoundsAsync([FromBody] CutLabRestartRoundsApiRequest request, CancellationToken cancellationToken)
    {
        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Message = SameOriginRequestValidator.GetForbiddenMessage() });
        }

        if (request is null)
        {
            return BadRequest(new { Message = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.CutLabStateJson))
        {
            return BadRequest(new { Message = "Cut Lab state is required." });
        }

        try
        {
            CutLabState state = SanitizePlanProfile(CutLabStateSerializer.Deserialize(request.CutLabStateJson));
            if (state.Pool.Count == 0)
            {
                return BadRequest(new { Message = InvalidStateMessage });
            }

            IReadOnlyList<string> commanderNames = CutLabCommanderNames.Resolve(state);
            bool twinsEnabled = IsFlagOn(CutLabStructuralFindings.FunctionalTwinsFlagKey);
            state = CutLabDecisionApplier.RestartRounds(state, [CutLabCutRoundEngine.Round1Key, CutLabCutRoundEngine.Round2Key]);

            CutLabUiPatchDto patch = await _patchBuilder.BuildAsync(
                state,
                state.Intent.PlayExperience,
                commanderNames,
                twinsEnabled,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return Ok(BuildDecideApiResponse(patch));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(exception, "Cut Lab restart rounds API request failed.");
            return BadRequest(new { Message = CutLabMessages.NoChangeMessage });
        }
    }

    /// <summary>Builds a non-mutating what-if swap preview and returns the metric deltas.</summary>
    /// <param name="request">What-if swap request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The side-effect-free preview payload.</returns>
    [HttpPost("whatif")]
    [FeatureFlagGate("tool.cut-lab.enabled")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [ProducesResponseType(typeof(CutLabWhatifApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CutLabWhatifApiResponse>> PostWhatifAsync([FromBody] CutLabWhatifApiRequest request, CancellationToken cancellationToken)
    {
        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Message = SameOriginRequestValidator.GetForbiddenMessage() });
        }

        if (request is null)
        {
            return BadRequest(new { Message = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.CutLabStateJson) || string.IsNullOrWhiteSpace(request.CardOut) || string.IsNullOrWhiteSpace(request.CardIn))
        {
            return BadRequest(new { Message = "Cut Lab state, card out, and card in are required." });
        }

        try
        {
            CutLabState state = SanitizePlanProfile(CutLabStateSerializer.Deserialize(request.CutLabStateJson));
            if (state.Pool.Count == 0)
            {
                return BadRequest(new { Message = InvalidStateMessage });
            }

            if (!_whatifService.TryValidateSwap(state, request.CardOut, request.CardIn, out string? validationError))
            {
                return BadRequest(new { Message = validationError });
            }

            CutLabWhatifPreview preview = await _whatifService
                .PreviewSwapAsync(state, request.CardOut, request.CardIn, cancellationToken)
                .ConfigureAwait(false);

            return Ok(new CutLabWhatifApiResponse
            {
                CardOut = preview.CardOut,
                CardIn = preview.CardIn,
                Deltas = BuildMetricDeltas(preview.Deltas),
                ChangedFamilyCount = preview.ChangedFamilyCount,
                CutLabStateJson = null,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(exception, "Cut Lab what-if preview API request failed.");
            return BadRequest(new { Message = CutLabMessages.NoChangeMessage });
        }
    }

    /// <summary>Commits a validated what-if swap by restoring B and accepting A atomically.</summary>
    /// <param name="request">What-if swap request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated serialized Cut Lab state.</returns>
    [HttpPost("whatif/commit")]
    [FeatureFlagGate("tool.cut-lab.enabled")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [ProducesResponseType(typeof(CutLabWhatifApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CutLabWhatifApiResponse>> PostWhatifCommitAsync([FromBody] CutLabWhatifApiRequest request, CancellationToken cancellationToken)
    {
        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Message = SameOriginRequestValidator.GetForbiddenMessage() });
        }

        if (request is null)
        {
            return BadRequest(new { Message = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.CutLabStateJson) || string.IsNullOrWhiteSpace(request.CardOut) || string.IsNullOrWhiteSpace(request.CardIn))
        {
            return BadRequest(new { Message = "Cut Lab state, card out, and card in are required." });
        }

        CutLabWhatifCommitResult result;
        try
        {
            CutLabState state = SanitizePlanProfile(CutLabStateSerializer.Deserialize(request.CutLabStateJson));
            if (state.Pool.Count == 0)
            {
                return BadRequest(new { Message = InvalidStateMessage });
            }

            result = await _whatifService
                .CommitSwapAsync(state, request.CardOut, request.CardIn, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(exception, "Cut Lab what-if commit API request failed.");
            return BadRequest(new { Message = CutLabMessages.NoChangeMessage });
        }

        if (!result.Applied)
        {
            return BadRequest(new { Message = result.ErrorMessage ?? CutLabMessages.NoChangeMessage });
        }

        IReadOnlyList<string> commanderNames = CutLabCommanderNames.Resolve(result.State);
        bool twinsEnabled = IsFlagOn(CutLabStructuralFindings.FunctionalTwinsFlagKey);
        string cardOut = result.CardOut ?? throw new InvalidOperationException("What-if commit must provide CardOut when applied.");
        string cardIn = result.CardIn ?? throw new InvalidOperationException("What-if commit must provide CardIn when applied.");
        CutLabUiPatchDto patch = await _patchBuilder.BuildAsync(
            result.State,
            result.State.Intent.PlayExperience,
            commanderNames,
            twinsEnabled,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Ok(new CutLabWhatifApiResponse
        {
            CardOut = cardOut,
            CardIn = cardIn,
            Patch = patch with { NextProposal = AddProposalGlance(patch.NextProposal, patch.ProposalDeltas) },
            CutLabStateJson = patch.CutLabStateJson,
        });
    }

    /// <summary>Re-validates a posted plan-panel profile against the catalog and the commander's fetched EDHREC themes, and returns the resulting UI patch.</summary>
    /// <param name="request">Plan-apply request payload carrying the client-updated session state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The server-authored live UI patch for the re-validated plan profile.</returns>
    [HttpPost("plan-apply")]
    [FeatureFlagGate("tool.cut-lab.enabled")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [ProducesResponseType(typeof(CutLabPlanApplyApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CutLabPlanApplyApiResponse>> PostPlanApplyAsync([FromBody] CutLabPlanApplyApiRequest request, CancellationToken cancellationToken)
    {
        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Message = SameOriginRequestValidator.GetForbiddenMessage() });
        }

        if (request is null)
        {
            return BadRequest(new { Message = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.CutLabStateJson))
        {
            return BadRequest(new { Message = "Cut Lab state is required." });
        }

        try
        {
            CutLabState state = SanitizePlanProfile(CutLabStateSerializer.Deserialize(request.CutLabStateJson));
            if (state.Pool.Count == 0)
            {
                return BadRequest(new { Message = InvalidStateMessage });
            }

            IReadOnlyList<string> commanderNames = CutLabCommanderNames.Resolve(state);
            bool twinsEnabled = IsFlagOn(CutLabStructuralFindings.FunctionalTwinsFlagKey);
            EdhrecThemeResult planThemeResult = await CutLabSharedHelpers.FetchPlanThemeResultAsync(_themeService, _logger, commanderNames, cancellationToken).ConfigureAwait(false);

            CutLabPlanProfile? postedProfile = state.Intent.PlanProfile;
            CutLabPlanProfile rebuiltProfile = CutLabPageService.BuildPlanProfile(
                postedProfile?.GenericStrategies ?? [],
                postedProfile?.CommanderThemes.Select(theme => theme.Slug).ToArray() ?? [],
                priorProfile: postedProfile,
                planThemeResult: planThemeResult);

            state = state with { Intent = state.Intent with { PlanProfile = rebuiltProfile } };

            CutLabUiPatchDto patch = await _patchBuilder.BuildAsync(
                state,
                state.Intent.PlayExperience,
                commanderNames,
                twinsEnabled,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return Ok(new CutLabPlanApplyApiResponse
            {
                Patch = patch,
                AppliedStrategies = rebuiltProfile.GenericStrategies,
                AppliedThemes = rebuiltProfile.CommanderThemes.Select(theme => theme.Slug).ToArray(),
                CommanderThemesUnavailable = rebuiltProfile.CommanderThemesUnavailable,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(exception, "Cut Lab plan-apply API request failed.");
            return BadRequest(new { Message = CutLabMessages.NoChangeMessage });
        }
    }

    // Why: IsEnabled defaults a missing key ON; dark launch requires missing keys to land OFF.
    private bool IsFlagOn(string key)
        => _featureFlags.Snapshot().TryGetValue(key, out bool enabled) && enabled;

    private IReadOnlyList<ScryfallCardData>? TryBuildAfterPreResolvedCards(
        IReadOnlyList<CutLabPoolCard> fullPool,
        IReadOnlyList<CutLabPoolCard> afterWorkingList,
        IReadOnlyList<ScryfallCardData> beforeResolvedCards)
    {
        if (_contextBuilder.TrySeedDerivedPool(afterWorkingList, beforeResolvedCards, out IReadOnlyList<ScryfallCardData>? seededCards)
            && seededCards is not null)
        {
            return seededCards;
        }

        if (_contextBuilder.TryGetCachedResolvedCards(fullPool, out IReadOnlyList<ScryfallCardData>? fullPoolCards)
            && fullPoolCards is not null
            && _contextBuilder.TrySeedDerivedPool(afterWorkingList, fullPoolCards, out seededCards)
            && seededCards is not null)
        {
            return seededCards;
        }

        return BuildPartialResolvedSubset(afterWorkingList, fullPoolCards ?? beforeResolvedCards);
    }

    private static CutLabDecideApiResponse BuildDecideApiResponse(CutLabUiPatchDto patch)
    {
        CutLabDecideNextProposalDto? nextProposal = AddProposalGlance(patch.NextProposal, patch.ProposalDeltas);
        return new()
        {
            Patch = patch with { NextProposal = nextProposal },
            CutLabStateJson = patch.CutLabStateJson,
            NextProposal = nextProposal ?? throw new InvalidOperationException("A decision response requires a next proposal."),
            ProposalDeltas = patch.ProposalDeltas,
            FloorWarnings = patch.FloorWarnings,
            CardsRemaining = patch.CardsRemaining,
            CutsMade = patch.CutsMade,
            StructuralFindings = patch.StructuralFindings,
            ComboDataAvailable = patch.ComboDataAvailable,
            CategoryDataAvailable = patch.CategoryDataAvailable,
        };
    }

    private static CutLabDecideNextProposalDto? AddProposalGlance(
        CutLabDecideNextProposalDto? proposal,
        CutLabDecideProposalDeltasDto? deltas)
        => proposal is null
            ? null
            : proposal with
            {
                GlanceLine = CutLabViewModel.ComposeProposalGlance(
                deltas is null
                    ? null
                    : new CutLabProposalDeltas
                    {
                        ChangedFamilyCount = deltas.ChangedFamilyCount,
                        Deltas = deltas.Deltas.Select(delta => new CutLabMetricDelta
                        {
                            Label = delta.Label,
                            Delta = delta.Delta,
                            Unit = delta.Unit,
                            IsMeaningful = delta.IsMeaningful,
                        }).ToArray(),
                    },
                CutLabMessages.NoChangeMessage),
            };

    private static CutLabState SanitizePlanProfile(CutLabState state)
    {
        CutLabPlanProfile? postedPlanProfile = state.Intent.PlanProfile;
        if (postedPlanProfile is null)
        {
            return state;
        }

        return state with
        {
            Intent = state.Intent with
            {
                PlanProfile = postedPlanProfile with
                {
                    GenericStrategies = CutLabPageService.ValidateStrategySlugs(postedPlanProfile.GenericStrategies),
                    CommanderThemes = postedPlanProfile.CommanderThemes
                        .Where(theme => theme is not null && !string.IsNullOrWhiteSpace(theme.Slug))
                        .DistinctBy(theme => theme.Slug, StringComparer.OrdinalIgnoreCase)
                        .Select(theme => new CutLabCommanderTheme { Slug = theme.Slug })
                        .ToArray(),
                },
            },
        };
    }

    private static string DetermineRoundKey(CutLabState state, CutLabDecideApiRequest request, CutLabRoundPlan roundPlan)
    {
        if (request.Decision == CutLabDecideAction.Restore)
        {
            return CutLabDecisionApplier.LatestRoundForCard(state, request.CardName);
        }

        if (roundPlan.NextProposal is not null
            && string.Equals(roundPlan.NextProposal.CardName, request.CardName, StringComparison.OrdinalIgnoreCase))
        {
            return roundPlan.NextProposal.RoundKey;
        }

        return roundPlan.Queue
            .FirstOrDefault(item => string.Equals(item.CardName, request.CardName, StringComparison.OrdinalIgnoreCase))
            ?.RoundKey
            ?? CutLabDecisionApplier.LatestRoundForCard(state, request.CardName);
    }

    private static IReadOnlyList<ScryfallCardData>? BuildPartialResolvedSubset(
        IReadOnlyList<CutLabPoolCard> targetPool,
        IReadOnlyList<ScryfallCardData> sourceCards)
    {
        IReadOnlyDictionary<string, ScryfallCardData> sourceByName = CutLabCardNames.ToLastWinsDictionary(
            sourceCards,
            card => card.Name,
            card => card);
        return targetPool
            .Select(card => sourceByName.TryGetValue(CutLabCardNames.Normalize(card.Name), out ScryfallCardData? resolvedCard) ? resolvedCard : null)
            .Where(card => card is not null)
            .Cast<ScryfallCardData>()
            .DistinctBy(card => CutLabCardNames.Normalize(card.Name))
            .ToArray();
    }

    private static IReadOnlyList<CutLabDecideMetricDeltaDto> BuildMetricDeltas(IReadOnlyList<CutLabMetricDelta> deltas)
        => deltas
            .Select(delta => new CutLabDecideMetricDeltaDto
            {
                Kind = delta.Kind,
                Label = delta.Label,
                Before = delta.Before,
                After = delta.After,
                Delta = delta.Delta,
                Unit = delta.Unit,
                Direction = delta.Direction,
                IsMeaningful = delta.IsMeaningful,
            })
            .ToArray();

}
