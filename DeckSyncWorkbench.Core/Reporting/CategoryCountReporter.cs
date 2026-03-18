using DeckSyncWorkbench.Core.Models;

namespace DeckSyncWorkbench.Core.Reporting;

public static class CategoryCountReporter
{
    public static IReadOnlyList<(string Category, int Count)> CountByQuantity(IEnumerable<DeckEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return entries
            .SelectMany(entry => SplitCategories(entry).Select(category => new { category, entry.Quantity }))
            .GroupBy(item => item.category, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Category: group.First().category, Count: group.Sum(item => item.Quantity)))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ToText(IEnumerable<DeckEntry> entries)
    {
        var counts = CountByQuantity(entries);
        if (counts.Count == 0)
        {
            return "No Archidekt categories found.";
        }

        return string.Join(Environment.NewLine, counts.Select(item => $"{item.Category}: {item.Count}"));
    }

    private static IEnumerable<string> SplitCategories(DeckEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Category))
        {
            yield break;
        }

        foreach (var category in entry.Category.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return category;
        }
    }
}
