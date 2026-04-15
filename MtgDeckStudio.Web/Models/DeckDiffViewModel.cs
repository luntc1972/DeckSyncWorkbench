using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Reporting;

namespace MtgDeckStudio.Web.Models;

public sealed class DeckDiffViewModel
{
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.Sync;

    public DeckDiffRequest Request { get; init; } = new();

    public CategorySuggestionRequest SuggestionRequest { get; init; } = new();

    public DeckDiff? Diff { get; init; }

    public string? DeltaText { get; init; }

    public string? FullImportText { get; init; }

    public string? ReportText { get; init; }

    public string? SwapChecklistText { get; init; }

    public string? InstructionsText { get; init; }

    public string? ErrorMessage { get; init; }

    public string? SuggestionErrorMessage { get; init; }

    public string? ExactSuggestedCategoriesText { get; init; }

    public string? ExactSuggestionContextText { get; init; }

    public string? InferredCategoriesText { get; init; }

    public string? InferredSuggestionContextText { get; init; }

    public string? EdhrecCategoriesText { get; init; }

    public string? EdhrecSuggestionContextText { get; init; }

    public string? TaggerCategoriesText { get; init; }

    public string? TaggerSuggestionContextText { get; init; }

    public bool NoSuggestionsFound { get; init; }

    public string? NoSuggestionsMessage { get; init; }

    public string? SuggestionSourceSummary { get; init; }
    public int AdditionalDecksFound { get; init; }
    public bool ExtendedHarvestTriggered { get; init; }
    public CardDeckTotals CardDeckTotals { get; init; } = CardDeckTotals.Empty;
}
