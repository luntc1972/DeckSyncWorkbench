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
    private readonly PacketSessionCache _packetSessionCache;
    private readonly ILogger<DeckModulesController> _logger;

    /// <summary>
    /// Creates the Deck Modules controller.
    /// </summary>
    /// <param name="service">Deck Modules page service.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="analysisService">Configuration analysis service.</param>
    /// <param name="packetSessionCache">Packet session cache.</param>
    public DeckModulesController(
        IDeckModulesPageService service,
        ILogger<DeckModulesController> logger,
        IConfigurationAnalysisService analysisService,
        PacketSessionCache packetSessionCache)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(analysisService);
        ArgumentNullException.ThrowIfNull(packetSessionCache);
        ArgumentNullException.ThrowIfNull(logger);
        _service = service;
        _logger = logger;
        _analysisService = analysisService;
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
