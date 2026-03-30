namespace MtgDeckStudio.Web.Models;

/// <summary>
/// Represents a pasted list of card names for Scryfall lookup.
/// </summary>
public sealed class CardLookupRequest
{
    /// <summary>
    /// Gets or sets the pasted card list. One card per line; optional leading quantities are allowed.
    /// </summary>
    public string CardList { get; init; } = string.Empty;
}
