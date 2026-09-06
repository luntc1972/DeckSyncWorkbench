using DeckFlow.Core.Manabase;

namespace DeckFlow.Web.Models.DeckModules;

/// <summary>
/// Request to analyze one compiled Deck Modules configuration's mana base via the existing
/// manabase analysis service. Carries only the compiled configuration and the analysis mode — no
/// deck source string, override box, or options bag.
/// </summary>
public sealed record ConfigurationAnalysisRequest
{
    /// <summary>Gets the Deck Modules configuration to compile and analyze.</summary>
    public required DeckModulesCompilationRequest Configuration { get; init; }

    /// <summary>Gets the manabase analysis mode. Defaults to <see cref="ManabaseMode.Casual"/>.</summary>
    public ManabaseMode Mode { get; init; } = ManabaseMode.Casual;
}
