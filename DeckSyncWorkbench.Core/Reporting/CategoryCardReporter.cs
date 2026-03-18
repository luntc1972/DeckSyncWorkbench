using DeckSyncWorkbench.Core.Models;

namespace DeckSyncWorkbench.Core.Reporting;

public static class CategoryCardReporter
{
    public static IReadOnlyList<DeckEntry> CardsInCategory(IEnumerable<DeckEntry> entries, string category)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        return entries
            .Where(entry => HasCategory(entry, category))
            .OrderByDescending(entry => entry.Quantity)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ToText(IEnumerable<DeckEntry> entries, string category)
    {
        var matches = CardsInCategory(entries, category);
        if (matches.Count == 0)
        {
            return $"No cards found in category: {category}";
        }

        return string.Join(Environment.NewLine, matches.Select(entry => $"{entry.Quantity} {entry.Name}"));
    }

    private static bool HasCategory(DeckEntry entry, string category)
    {
        if (string.IsNullOrWhiteSpace(entry.Category))
        {
            return false;
        }

        return entry.Category
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(item => string.Equals(item, category, StringComparison.OrdinalIgnoreCase));
    }
}
