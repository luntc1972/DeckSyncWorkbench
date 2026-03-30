namespace MtgDeckStudio.Web.Models;

/// <summary>
/// Request payload for a single-card category suggestion lookup.
/// </summary>
public sealed class CategorySuggestionRequest
{
    /// <summary>
    /// Chooses whether the lookup should use only the local cache or also inspect a supplied Archidekt reference deck.
    /// </summary>
    public CategorySuggestionMode Mode { get; set; } = CategorySuggestionMode.CachedData;

    /// <summary>
    /// Describes whether the optional reference deck will be provided as a public URL or pasted export text.
    /// </summary>
    public DeckInputSource ArchidektInputSource { get; set; } = DeckInputSource.PublicUrl;

    /// <summary>
    /// Public Archidekt deck URL used when <see cref="ArchidektInputSource"/> is <see cref="DeckInputSource.PublicUrl"/>.
    /// </summary>
    public string ArchidektUrl { get; set; } = string.Empty;

    /// <summary>
    /// Raw Archidekt export text used when <see cref="ArchidektInputSource"/> is <see cref="DeckInputSource.PasteText"/>.
    /// </summary>
    public string ArchidektText { get; set; } = string.Empty;

    /// <summary>
    /// Card name whose common categories should be suggested.
    /// </summary>
    public string CardName { get; set; } = string.Empty;
}
