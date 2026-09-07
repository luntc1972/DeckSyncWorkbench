using System.Text;
using DeckFlow.Core.Models;
using DeckFlow.Core.Modular;
using DeckFlow.Web.Infrastructure;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Security;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Modular;
using Microsoft.AspNetCore.Mvc;

namespace DeckFlow.Web.Controllers;

/// <summary>
/// Serves the Deck Modules workflow.
/// </summary>
public sealed class DeckModulesController : DeckToolControllerBase
{
    private const string FlagKey = "tool.deck-modules.enabled";
    private readonly IDeckModulesPageService _service;
    private readonly IConfigurationAnalysisService _analysisService;
    private readonly IConfigurationDeltaService _configurationDeltaService;
    private readonly PacketSessionCache _packetSessionCache;
    private readonly ILogger<DeckModulesController> _logger;

    /// <summary>
    /// Creates the Deck Modules controller.
    /// </summary>
    /// <param name="service">Deck Modules page service.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="analysisService">Configuration analysis service.</param>
    /// <param name="configurationDeltaService">Configuration comparison service.</param>
    /// <param name="packetSessionCache">Packet session cache.</param>
    public DeckModulesController(
        IDeckModulesPageService service,
        ILogger<DeckModulesController> logger,
        IConfigurationAnalysisService analysisService,
        PacketSessionCache packetSessionCache,
        IConfigurationDeltaService? configurationDeltaService = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(analysisService);
        ArgumentNullException.ThrowIfNull(packetSessionCache);
        ArgumentNullException.ThrowIfNull(logger);
        _service = service;
        _logger = logger;
        _analysisService = analysisService;
        _configurationDeltaService = configurationDeltaService ?? new ConfigurationDeltaService();
        _packetSessionCache = packetSessionCache;
    }

    /// <summary>Renders the Deck Modules page.</summary>
    [HttpGet("/deck-modules")]
    [FeatureFlagGate(FlagKey)]
    public IActionResult Index() => View("DeckModules", new DeckModulesIndexViewModel());

    /// <summary>Imports a baseline deck.</summary>
    [HttpPost("/deck-modules/import")]
    [FeatureFlagGate(FlagKey)]
    public async Task<IActionResult> Import(
        [FromBody] DeckModulesImportRequest? request,
        CancellationToken cancellationToken)
    {
        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return Forbidden();
        }

        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        var result = await _service.ImportAsync(request, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : Failure(result.ErrorMessage);
    }

    /// <summary>Compiles a submitted Deck Modules configuration.</summary>
    [HttpPost("/deck-modules/compile")]
    [FeatureFlagGate(FlagKey)]
    public IActionResult Compile([FromBody] DeckModulesCompilationRequest? request)
    {
        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return Forbidden();
        }

        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        var result = _service.Compile(request);
        return result.Succeeded ? Ok(result.Value) : Failure(result.ErrorMessage);
    }

    /// <summary>Exports a compiled Deck Modules configuration as text.</summary>
    [HttpPost("/deck-modules/export")]
    [FeatureFlagGate(FlagKey)]
    public IActionResult Export([FromBody] DeckModulesCompilationRequest? request)
    {
        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return Forbidden();
        }

        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        var result = _service.Compile(request);
        if (!result.Succeeded)
        {
            return Failure(result.ErrorMessage);
        }

        var compilation = result.Value!;
        if (!compilation.IsStructurallyValid)
        {
            return Failure("Resolve the compilation diagnostics before exporting this configuration.");
        }

        var fileName = $"deck-modules-{SanitizeFileName(compilation.SelectedStrategyName)}.txt";
        return File(Encoding.UTF8.GetBytes(DeckModulesDecklistSerializer.BuildExportText(compilation)), "text/plain; charset=utf-8", fileName);
    }

    /// <summary>Analyzes a compiled Deck Modules configuration.</summary>
    [HttpPost("/deck-modules/analyze")]
    [FeatureFlagGate(FlagKey)]
    public async Task<IActionResult> Analyze(
        [FromBody] ConfigurationAnalysisRequest? request,
        CancellationToken cancellationToken)
    {
        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return Forbidden();
        }

        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        var key = PacketSessionCache.ComputeKey(new { request.Configuration, request.Mode });
        if (_packetSessionCache.TryGet<ConfigurationAnalysisResult>(key, out var cached))
        {
            return Ok(new { analysisKey = key, analysis = cached });
        }

        var result = await _analysisService.AnalyzeAsync(request, cancellationToken);
        if (!result.Succeeded)
        {
            return Failure(result.ErrorMessage);
        }

        _packetSessionCache.Set(key, result.Value!, PacketSizeEstimator.EstimateSizeBytes(result.Value!));
        return Ok(new { analysisKey = key, analysis = result.Value });
    }

    /// <summary>Compares cached analyses for Deck Modules configurations.</summary>
    [HttpPost("/deck-modules/compare")]
    [FeatureFlagGate(FlagKey)]
    public IActionResult Compare([FromBody] ConfigurationComparisonRequest? request, CancellationToken cancellationToken)
    {
        if (!SameOriginRequestValidator.IsValid(Request))
        {
            return Forbidden();
        }

        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        if (request.Sides is null || request.Sides.Count < ConfigurationComparisonRequest.MinSideCount || request.Sides.Count > ConfigurationComparisonRequest.MaxSideCount)
        {
            return Failure($"Submit between {ConfigurationComparisonRequest.MinSideCount} and {ConfigurationComparisonRequest.MaxSideCount} configuration analyses.");
        }

        var referenceIndex = request.Sides.ToList().FindIndex(side => side.ConfigurationId == request.ReferenceConfigurationId);
        if (referenceIndex < 0)
        {
            return Failure("Reference configuration must be one of the submitted configurations.");
        }

        var resolvedAnalyses = new List<ConfigurationAnalysisResult?>(request.Sides.Count);
        var missingConfigurationIds = new List<string>();
        foreach (var side in request.Sides)
        {
            if (_packetSessionCache.TryGet<ConfigurationAnalysisResult>(side.AnalysisKey, out var cached))
            {
                resolvedAnalyses.Add(cached);
                continue;
            }

            _logger.LogInformation("Configuration analysis cache miss for {KeyPrefix}", PacketSessionCache.GetKeyPrefix(side.AnalysisKey));
            if (side.Analysis is null)
            {
                missingConfigurationIds.Add(side.ConfigurationId);
                resolvedAnalyses.Add(null);
                continue;
            }

            _packetSessionCache.Set(side.AnalysisKey, side.Analysis, PacketSizeEstimator.EstimateSizeBytes(side.Analysis));
            resolvedAnalyses.Add(side.Analysis);
        }

        if (missingConfigurationIds.Count > 0)
        {
            return StatusCode(StatusCodes.Status409Conflict, new { message = "One or more configuration analyses are unavailable.", missingConfigurationIds });
        }

        return Ok(_configurationDeltaService.ComputeDelta(resolvedAnalyses, referenceIndex));
    }

    private IActionResult Forbidden()
    {
        _logger.LogWarning("Rejected cross-origin Deck Modules request.");
        return StatusCode(StatusCodes.Status403Forbidden, new { message = SameOriginRequestValidator.GetForbiddenMessage() });
    }

    private IActionResult Failure(string? message) => BadRequest(new { message = message ?? "Request failed." });

    private static string NormalizeLine(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    private static string SanitizeFileName(string value)
    {
        var normalized = NormalizeLine(value);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        return builder.ToString().Trim('-') is { Length: > 0 } sanitized ? sanitized : "export";
    }
}
