namespace DeckSyncWorkbench.Web.Models;

public sealed class CategorySuggestionRequest
{
    public CategorySuggestionMode Mode { get; set; } = CategorySuggestionMode.CachedData;

    public DeckInputSource ArchidektInputSource { get; set; } = DeckInputSource.PublicUrl;

    public string ArchidektUrl { get; set; } = string.Empty;

    public string ArchidektText { get; set; } = string.Empty;

    public string CardName { get; set; } = string.Empty;
}
