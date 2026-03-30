namespace MtgDeckStudio.Web.Models;

/// <summary>
/// Represents the results of looking up a pasted card list.
/// </summary>
public sealed class CardLookupViewModel
{
    /// <summary>
    /// Gets the active tab for the shared deck tool navigation.
    /// </summary>
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.CardLookup;

    /// <summary>
    /// Gets the original user request.
    /// </summary>
    public CardLookupRequest Request { get; init; } = new();

    /// <summary>
    /// Gets the verified card output, formatted per card.
    /// </summary>
    public string? VerifiedText { get; init; }

    /// <summary>
    /// Gets the lines that could not be found on Scryfall.
    /// </summary>
    public string? MissingText { get; init; }

    /// <summary>
    /// Gets the user-facing error message for form or upstream failures.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the number of found cards.
    /// </summary>
    public int FoundCount { get; init; }

    /// <summary>
    /// Gets the number of missing cards.
    /// </summary>
    public int MissingCount { get; init; }
}
