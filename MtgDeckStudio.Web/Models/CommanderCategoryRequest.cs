namespace MtgDeckStudio.Web.Models;

/// <summary>
/// Request payload for commander-category aggregation.
/// </summary>
public sealed class CommanderCategoryRequest
{
    /// <summary>
    /// Commander name to search for in cached Archidekt commander decks.
    /// </summary>
    public string CommanderName { get; set; } = string.Empty;
}
