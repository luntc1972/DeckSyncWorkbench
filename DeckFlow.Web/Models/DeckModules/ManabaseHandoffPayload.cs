using DeckFlow.Core.Manabase;
using DeckFlow.Web.Services.Manabase;

namespace DeckFlow.Web.Models.DeckModules;

/// <summary>
/// Short-lived render payload for the cache's five-minute absolute TTL. This is not a saved deck,
/// share token, or persistence key; widening it into one is the separate deferred D-04 work.
/// </summary>
public sealed record ManabaseHandoffPayload
{
    /// <summary>The already-computed mana-base analysis result.</summary>
    public required ManabaseAnalysisResult Result { get; init; }

    /// <summary>The compiled decklist text echoed into the mana-base form.</summary>
    public required string DecklistText { get; init; }

    /// <summary>The compiled configuration name echoed into the mana-base form.</summary>
    public required string DeckName { get; init; }

    /// <summary>The mode used for the cached analysis.</summary>
    public required ManabaseMode Mode { get; init; }
}
