namespace MtgDeckStudio.Core.Reporting;

public static class CategoryInferenceReporter
{
    /// <summary>
    /// Infers categories for a card from the harvested knowledge text.
    /// </summary>
    /// <param name="knowledgeText">Harvested knowledge text chunk.</param>
    /// <param name="cardName">Card name to locate.</param>
    public static IReadOnlyList<string> InferCategoriesFromKnowledge(string knowledgeText, string cardName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeText);
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);

        var matches = new List<string>();
        var currentCategory = string.Empty;

        // Split lines robustly for both \n and \r\n
        var lines = knowledgeText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                currentCategory = line[1..^1].Trim();
                continue;
            }

            if (currentCategory.Length == 0)
            {
                continue;
            }

            var splitIndex = line.IndexOf(' ');
            if (splitIndex < 0 || splitIndex == line.Length - 1)
            {
                continue;
            }

            var candidateName = line[(splitIndex + 1)..].Trim();
            if (string.Equals(candidateName, cardName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(currentCategory);
            }
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
