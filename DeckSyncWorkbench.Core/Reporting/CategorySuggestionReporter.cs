using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Normalization;

namespace DeckSyncWorkbench.Core.Reporting;

public static class CategorySuggestionReporter
{
    public static IReadOnlyList<string> SuggestCategories(IEnumerable<DeckEntry> entries, string cardName)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);

        var normalizedName = CardNormalizer.Normalize(cardName);
        return entries
            .Where(entry => string.Equals(entry.NormalizedName, normalizedName, StringComparison.Ordinal))
            .SelectMany(entry => SplitCategories(entry.Category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ToText(IEnumerable<string> categories, string cardName)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);

        var items = categories.ToList();
        if (items.Count == 0)
        {
            return $"No deck-local category suggestion found for {cardName}.";
        }

        return string.Join(Environment.NewLine, items.Select(category => $"- {category}"));
    }

    private static IEnumerable<string> SplitCategories(string? categoryText)
    {
        if (string.IsNullOrWhiteSpace(categoryText))
        {
            yield break;
        }

        foreach (var item in categoryText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (CategoryFilter.IsIncluded(item))
            {
                yield return item;
            }
        }
    }
}
