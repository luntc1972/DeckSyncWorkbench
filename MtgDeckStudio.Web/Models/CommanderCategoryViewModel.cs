using MtgDeckStudio.Core.Reporting;

namespace MtgDeckStudio.Web.Models;

public sealed class CommanderCategoryViewModel
{
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.CommanderCategories;
    public CommanderCategoryRequest Request { get; init; } = new();
    public IReadOnlyList<CategoryKnowledgeRow> CategoryRows { get; init; } = Array.Empty<CategoryKnowledgeRow>();
    public IReadOnlyList<CommanderCategorySummary> CategorySummaries { get; init; } = Array.Empty<CommanderCategorySummary>();
    public string? ErrorMessage { get; init; }
    public int HarvestedDeckCount { get; init; }
    public int AdditionalDecksFound { get; init; }
    public bool ExtendedHarvestTriggered { get; init; }
    public bool HasResults => CategorySummaries.Count > 0;
    public CardDeckTotals CardDeckTotals { get; init; } = CardDeckTotals.Empty;
}
