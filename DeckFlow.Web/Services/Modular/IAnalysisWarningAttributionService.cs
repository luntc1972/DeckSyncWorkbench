using DeckFlow.Core.Manabase;
using DeckFlow.Core.Modular;
using DeckFlow.Web.Models.DeckModules;

namespace DeckFlow.Web.Services.Modular;

/// <summary>Attributes configuration analysis warnings to swapped cards or contributing modules.</summary>
public interface IAnalysisWarningAttributionService
{
    /// <summary>Attributes findings without changing their supplied order.</summary>
    IReadOnlyList<ConfigurationAttributedFinding> AttributeFindings(IReadOnlyList<ColorSourceFinding> findings, ModularDeckSwapPlan swapPlan, ConfigurationModuleMap moduleMap);
}
