using MtgDeckStudio.Core.Models;

namespace MtgDeckStudio.Core.Reporting;

public static class CategoryKnowledgeReporter
{
    /// <summary>
    /// Builds category knowledge rows from deck entries.
    /// </summary>
    /// <param name="entries">Deck entries to aggregate.</param>
    public static IReadOnlyList<CategoryKnowledgeRow> Build(IEnumerable<DeckEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var counts = new Dictionary<(string Category, string CardName), int>();
        var display = new Dictionary<(string Category, string CardName), (string Category, string CardName)>();

        foreach (var entry in entries)
        {
            foreach (var category in SplitCategories(entry.Category))
            {
                var key = (category.ToUpperInvariant(), entry.Name.ToUpperInvariant());
                counts[key] = counts.TryGetValue(key, out var existing) ? existing + entry.Quantity : entry.Quantity;

                if (!display.ContainsKey(key))
                {
                    display[key] = (category, entry.Name);
                }
            }
        }

        return counts
            .Select(item =>
            {
                var label = display[item.Key];
                return new CategoryKnowledgeRow(label.Category, label.CardName, item.Value);
            })
            .OrderBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(row => row.Count)
            .ThenBy(row => row.CardName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Parses category knowledge file text into rows.
    /// </summary>
    /// <param name="text">Text to parse.</param>
    public static IReadOnlyList<CategoryKnowledgeRow> Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var rows = new List<CategoryKnowledgeRow>();
        var currentCategory = string.Empty;

        foreach (var rawLine in text.Split(Environment.NewLine))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("Harvested ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                currentCategory = line[1..^1];
                continue;
            }

            if (currentCategory.Length == 0)
            {
                continue;
            }

            var splitIndex = line.IndexOf(' ');
            if (splitIndex <= 0 || !int.TryParse(line[..splitIndex], out var count))
            {
                continue;
            }

            var cardName = line[(splitIndex + 1)..].Trim();
            if (cardName.Length == 0)
            {
                continue;
            }

            rows.Add(new CategoryKnowledgeRow(currentCategory, cardName, count));
        }

        return rows;
    }

    /// <summary>
    /// Serializes deck entries into a category knowledge text blob.
    /// </summary>
    /// <param name="entries">Entries to format.</param>
    /// <param name="deckCount">Number of decks harvested.</param>
    public static string ToText(IEnumerable<DeckEntry> entries, int deckCount)
    {
        return ToText(Build(entries), deckCount);
    }

    /// <summary>
    /// Serializes rows into the knowledge text format.
    /// </summary>
    /// <param name="rows">Knowledge rows to format.</param>
    /// <param name="deckCount">Count of decks represented.</param>
    public static string ToText(IEnumerable<CategoryKnowledgeRow> rows, int deckCount)
    {
        var items = rows.ToList();
        if (items.Count == 0)
        {
            return "No category knowledge found.";
        }

        var lines = new List<string>
        {
            $"Harvested {deckCount} Archidekt decks.",
            string.Empty,
        };

        foreach (var group in items.GroupBy(row => row.Category, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"[{group.First().Category}]");
            foreach (var row in group.OrderByDescending(item => item.Count).ThenBy(item => item.CardName, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"{row.Count} {row.CardName}");
            }

            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Splits a comma-delimited category string, filtering out top-level types.
    /// </summary>
    /// <param name="categoryText">Raw category text.</param>
    public static IEnumerable<string> SplitCategories(string? categoryText)
    {
        if (string.IsNullOrWhiteSpace(categoryText))
        {
            yield break;
        }

        foreach (var category in categoryText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (CategoryFilter.IsIncluded(category))
            {
                yield return category;
            }
        }
    }
}

    public sealed record CategoryKnowledgeRow(string Category, string CardName, int Count, int DeckCount = 0);
