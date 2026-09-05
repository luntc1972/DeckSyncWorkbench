using System.Text;
using DeckFlow.Core.Models;
using DeckFlow.Core.Modular;
using DeckFlow.Web.Infrastructure;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Security;
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
    private readonly ILogger<DeckModulesController> _logger;

    /// <summary>
    /// Creates the Deck Modules controller.
    /// </summary>
    /// <param name="service">Deck Modules page service.</param>
    /// <param name="logger">Logger.</param>
    public DeckModulesController(IDeckModulesPageService service, ILogger<DeckModulesController> logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(logger);
        _service = service;
        _logger = logger;
    }

    /// <summary>Renders the Deck Modules page.</summary>
    [HttpGet("/deck-modules")]
    [FeatureFlagGate(FlagKey)]
    public IActionResult Index() => View("DeckModules", new DeckModulesIndexViewModel());

    /// <summary>Imports a baseline deck.</summary>
    [HttpPost("/deck-modules/import")]
    [FeatureFlagGate(FlagKey)]
    public async Task<IActionResult> Import(DeckModulesImportRequest? request, CancellationToken cancellationToken)
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
    public IActionResult Compile(DeckModulesCompilationRequest? request)
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
    public IActionResult Export(DeckModulesCompilationRequest? request)
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
        return File(Encoding.UTF8.GetBytes(CreateExport(compilation)), "text/plain; charset=utf-8", fileName);
    }

    private IActionResult Forbidden()
    {
        _logger.LogWarning("Rejected cross-origin Deck Modules request.");
        return StatusCode(StatusCodes.Status403Forbidden, new { message = SameOriginRequestValidator.GetForbiddenMessage() });
    }

    private IActionResult Failure(string? message) => BadRequest(new { message = message ?? "Request failed." });

    private static string CreateExport(DeckModulesCompilationViewModel compilation)
    {
        var builder = new StringBuilder();
        AppendEntries(builder, "Command Zone", compilation.CommandZoneEntries);
        AppendEntries(builder, "Mainboard", compilation.MainboardEntries);
        AppendSwapEntries(builder, "IN", compilation.SwapPlan.ToAdd);
        AppendSwapEntries(builder, "OUT", compilation.SwapPlan.ToRemove);
        AppendSwapEntries(builder, "RESET", compilation.SwapPlan.ToReset);
        return builder.ToString();
    }

    private static void AppendEntries(StringBuilder builder, string heading, IReadOnlyList<DeckEntry> entries)
    {
        builder.Append("== ").Append(heading).AppendLine(" ==");
        foreach (var entry in entries)
        {
            builder.Append(entry.Quantity).Append(' ').AppendLine(NormalizeLine(entry.Name));
        }

        builder.AppendLine();
    }

    private static void AppendSwapEntries(StringBuilder builder, string prefix, IReadOnlyList<ModularDeckSwapEntry> entries)
    {
        foreach (var entry in entries)
        {
            var sign = entry.Action == ModularDeckSwapAction.Add ? '+' : '-';
            builder.Append(prefix).Append(" - ").Append(sign).Append(entry.Quantity).Append(' ').AppendLine(NormalizeLine(entry.Name));
        }
    }

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
