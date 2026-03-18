using System.Text.RegularExpressions;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Normalization;

namespace DeckSyncWorkbench.Core.Parsing;

public sealed partial class ArchidektParser : IParser
{
    public List<DeckEntry> ParseFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return ParseText(File.ReadAllText(filePath));
    }

    public List<DeckEntry> ParseText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new DeckParseException("Archidekt text is empty.");
        }

        var entries = new List<DeckEntry>();
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = QuantityRegex().Match(line);
            if (!match.Success)
            {
                throw new DeckParseException($"Unable to parse Archidekt line {i + 1}: \"{line}\"");
            }

            var quantity = int.Parse(match.Groups["quantity"].Value);
            if (quantity == 0)
            {
                continue;
            }

            var remainder = match.Groups["rest"].Value.Trim();
            string? categoryText = null;
            var categoryMatch = CategoryRegex().Match(remainder);
            if (categoryMatch.Success)
            {
                categoryText = NullIfWhiteSpace(categoryMatch.Groups["categories"].Value);
                remainder = categoryMatch.Groups["name"].Value.TrimEnd();
            }

            var isFoil = false;
            if (remainder.EndsWith("★", StringComparison.Ordinal))
            {
                isFoil = true;
                remainder = remainder[..^1].TrimEnd();
            }

            if (remainder.EndsWith("*F*", StringComparison.OrdinalIgnoreCase))
            {
                isFoil = true;
                remainder = remainder[..^3].TrimEnd();
            }

            var printingMatch = PrintingRegex().Match(remainder);
            if (!printingMatch.Success)
            {
                throw new DeckParseException($"Unable to parse Archidekt line {i + 1}: \"{line}\"");
            }

            var board = DetermineBoard(categoryText);

            entries.Add(new DeckEntry
            {
                Name = printingMatch.Groups["name"].Value.Trim(),
                NormalizedName = CardNormalizer.Normalize(printingMatch.Groups["name"].Value),
                Quantity = quantity,
                Board = board,
                SetCode = NullIfWhiteSpace(printingMatch.Groups["set"].Value),
                CollectorNumber = NullIfWhiteSpace(printingMatch.Groups["collector"].Value),
                Category = NormalizeCategory(categoryText),
                IsFoil = isFoil,
            });
        }

        if (entries.Count == 0)
        {
            throw new DeckParseException("Archidekt text did not contain any card lines.");
        }

        return entries;
    }

    private static string DetermineBoard(string? categories)
    {
        if (string.IsNullOrWhiteSpace(categories))
        {
            return "mainboard";
        }

        if (categories.Contains("Maybeboard", StringComparison.OrdinalIgnoreCase))
        {
            return "maybeboard";
        }

        if (categories.Contains("Commander", StringComparison.OrdinalIgnoreCase))
        {
            return "commander";
        }

        return "mainboard";
    }

    private static string? NormalizeCategory(string? categories)
    {
        if (string.IsNullOrWhiteSpace(categories))
        {
            return null;
        }

        var cleaned = BraceTokenRegex().Replace(categories, string.Empty);
        cleaned = cleaned.Replace("Maybeboard", string.Empty, StringComparison.OrdinalIgnoreCase);
        cleaned = cleaned.Replace("Commander", string.Empty, StringComparison.OrdinalIgnoreCase);
        cleaned = cleaned.Replace(",,", ",", StringComparison.Ordinal);
        cleaned = cleaned.Trim(' ', ',');
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^(?<quantity>\d+)\s+(?<rest>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"^(?<name>.+?)\s+\[(?<categories>.*)\]$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex CategoryRegex();

    [GeneratedRegex(@"^(?<name>.+?)\s+\((?<set>[^)]+)\)\s+(?<collector>\S+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex PrintingRegex();

    [GeneratedRegex(@"\{[^}]+\}", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex BraceTokenRegex();
}
