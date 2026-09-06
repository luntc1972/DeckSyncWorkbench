using DeckFlow.Web.Models.DeckModules;

namespace DeckFlow.Web.Services.Modular;

/// <summary>
/// Analyzes one compiled Deck Modules configuration's mana base via the existing manabase
/// analysis service. Never presents, phrases, or wires its output as a legality or validity
/// verdict — only the Phase 1 compiler diagnostics may say whether a build is legal.
/// </summary>
public interface IConfigurationAnalysisService
{
    /// <summary>
    /// Compiles <paramref name="request"/>'s configuration and, on success, analyzes its mana base.
    /// </summary>
    /// <param name="request">The configuration to analyze and the analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DeckModulesServiceResult<ConfigurationAnalysisResult>> AnalyzeAsync(
        ConfigurationAnalysisRequest request,
        CancellationToken cancellationToken = default);
}
