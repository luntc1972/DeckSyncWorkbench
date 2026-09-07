using DeckFlow.Core.Models;

namespace DeckFlow.Web.Models.DeckModules;

/// <summary>
/// Table profile a Deck Modules alternative declares itself for. Restricted to the three
/// milestone-supported play contexts; no free-text power score is accepted.
/// </summary>
public enum DeckModulesProfile
{
    /// <summary>Casual Commander tables.</summary>
    Casual,

    /// <summary>Bracket 4 / high-power Commander tables.</summary>
    Bracket4HighPower,

    /// <summary>Competitive EDH (cEDH) tables.</summary>
    Cedh,
}

/// <summary>
/// One manually named, manually assigned strategy alternative and its optional linked mana support.
/// </summary>
public sealed record DeckModulesAlternativeInput
{
    /// <summary>Gets the maximum accepted length of <see cref="Name"/> and <see cref="ManaSupportName"/>.</summary>
    public const int MaxNameLength = 80;

    /// <summary>Gets the maximum accepted length of <see cref="PlayPlan"/>.</summary>
    public const int MaxPlayPlanLength = 400;

    /// <summary>Gets the maximum accepted entries in <see cref="MainboardEntries"/> or <see cref="ManaSupportEntries"/>.</summary>
    public const int MaxEntriesPerList = 200;

    /// <summary>Gets the browser-assigned, request-scoped identifier for this alternative. Not a persistence key.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the user-entered display name for this strategy alternative.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the declared table profile for this alternative.</summary>
    public required DeckModulesProfile Profile { get; init; }

    /// <summary>Gets the required, nonblank one-sentence play-plan disclosure for this alternative.</summary>
    public required string PlayPlan { get; init; }

    /// <summary>Gets the manually assigned mainboard entries for this strategy.</summary>
    public required IReadOnlyList<DeckEntry> MainboardEntries { get; init; }

    /// <summary>Gets the optional linked mana-support module's display name, or <see langword="null"/> when this alternative links no mana support.</summary>
    public string? ManaSupportName { get; init; }

    /// <summary>Gets the manually assigned mana-support mainboard entries, or empty when this alternative links no mana support.</summary>
    public IReadOnlyList<DeckEntry> ManaSupportEntries { get; init; } = Array.Empty<DeckEntry>();
}

/// <summary>
/// Request to compile the current browser-session Deck Modules project through the Phase 1 compiler.
/// Carries only the current session's manually curated project and active selection: no project ID,
/// owner, share token, persistence key, or collaboration field is accepted.
/// </summary>
public sealed record DeckModulesCompilationRequest
{
    /// <summary>Gets the minimum accepted number of <see cref="Alternatives"/>.</summary>
    public const int MinAlternativeCount = 2;

    /// <summary>Gets the maximum accepted number of <see cref="Alternatives"/>.</summary>
    public const int MaxAlternativeCount = 4;

    /// <summary>Gets the maximum accepted entries in <see cref="CommandZone"/>, <see cref="BaselineMainboardEntries"/>, or <see cref="CoreEntries"/>.</summary>
    public const int MaxEntriesPerList = 200;

    /// <summary>Gets the server-issued baseline token echoed unmodified from import.</summary>
    public required string BaselineToken { get; init; }

    /// <summary>Gets the command zone submitted for this compilation. Must match the protected import baseline exactly.</summary>
    public required IReadOnlyList<DeckEntry> CommandZone { get; init; }

    /// <summary>Gets the imported baseline mainboard entries used for swap-plan comparison.</summary>
    public required IReadOnlyList<DeckEntry> BaselineMainboardEntries { get; init; }

    /// <summary>Gets the shared mainboard entries included in every configuration.</summary>
    public required IReadOnlyList<DeckEntry> CoreEntries { get; init; }

    /// <summary>Gets the 2-4 manually curated strategy alternatives available to select.</summary>
    public required IReadOnlyList<DeckModulesAlternativeInput> Alternatives { get; init; }

    /// <summary>Gets the identifier of the currently selected alternative.</summary>
    public required string SelectedAlternativeId { get; init; }
}
