using DeckFlow.Web.Models.DeckModules;

namespace DeckFlow.Web.Services.Modular;

/// <summary>Builds numeric comparisons between analyzed Deck Modules configurations.</summary>
public interface IConfigurationDeltaService
{
    /// <summary>Computes raw arithmetic deltas because fractional source counts are report values, not noisy measurements; two configurations are the current surface, while three or four require no signature change.</summary>
    ConfigurationComparisonDeltaModel ComputeDelta(IReadOnlyList<ConfigurationAnalysisResult?> analyses, int referenceIndex);
}
