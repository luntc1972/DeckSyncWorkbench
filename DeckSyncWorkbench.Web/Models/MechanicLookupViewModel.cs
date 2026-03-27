namespace DeckSyncWorkbench.Web.Models;

/// <summary>
/// Represents the mechanic rules lookup page state.
/// </summary>
public sealed class MechanicLookupViewModel
{
    /// <summary>
    /// Gets the active tab for shared deck tool navigation.
    /// </summary>
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.MechanicLookup;

    /// <summary>
    /// Gets the original user request.
    /// </summary>
    public MechanicLookupRequest Request { get; init; } = new();

    /// <summary>
    /// Gets the matched mechanic or rules term name.
    /// </summary>
    public string? MechanicName { get; init; }

    /// <summary>
    /// Gets the primary rules reference when available.
    /// </summary>
    public string? RuleReference { get; init; }

    /// <summary>
    /// Gets the match type explanation.
    /// </summary>
    public string? MatchType { get; init; }

    /// <summary>
    /// Gets the official rules text returned for the lookup.
    /// </summary>
    public string? RulesText { get; init; }

    /// <summary>
    /// Gets a shorter summary when available.
    /// </summary>
    public string? SummaryText { get; init; }

    /// <summary>
    /// Gets the direct Wizards rules text URL.
    /// </summary>
    public string? RulesTextUrl { get; init; }

    /// <summary>
    /// Gets the user-facing error message for invalid requests or upstream failures.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the user-facing not found message.
    /// </summary>
    public string? NotFoundMessage { get; init; }
}
