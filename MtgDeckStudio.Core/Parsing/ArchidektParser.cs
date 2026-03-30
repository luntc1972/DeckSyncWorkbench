using System.Text.RegularExpressions;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Normalization;

namespace MtgDeckStudio.Core.Parsing;

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
        var foundEntries = false;
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (IsStoppingLine(line) && foundEntries)
            {
                break;
            }

            if (IsIgnorableLine(line))
            {
                continue;
            }

            if (!TryParseEntry(line, allowImplicitQuantity: !foundEntries, out var entry))
            {
                if (foundEntries && IsNonDeckTextLine(line))
                {
                    continue;
                }

                throw new DeckParseException($"Unable to parse Archidekt line {i + 1}: \"{line}\"");
            }

            if (entry.Quantity == 0)
            {
                continue;
            }

            entries.Add(entry);
            foundEntries = true;
        }

        if (entries.Count == 0)
        {
            throw new DeckParseException("Archidekt text did not contain any card lines.");
        }

        return entries;
    }

    private static bool TryParseEntry(string line, bool allowImplicitQuantity, out DeckEntry entry)
    {
        entry = default!;

        var quantity = 1;
        var remainder = line;
        var match = QuantityRegex().Match(line);
        if (match.Success)
        {
            quantity = int.Parse(match.Groups["quantity"].Value);
            remainder = match.Groups["rest"].Value.Trim();
        }
        else if (!allowImplicitQuantity)
        {
            return false;
        }

        var hashtagCategories = ExtractHashtagCategories(ref remainder);
        string? categoryText = null;
        var categoryMatch = CategoryRegex().Match(remainder);
        if (categoryMatch.Success)
        {
            categoryText = NullIfWhiteSpace(categoryMatch.Groups["categories"].Value);
            remainder = categoryMatch.Groups["name"].Value.TrimEnd();
        }

        if (string.IsNullOrWhiteSpace(categoryText) && hashtagCategories.Count > 0)
        {
            categoryText = string.Join(",", hashtagCategories);
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
        string cardName;
        string? setCode = null;
        string? collectorNumber = null;
        if (printingMatch.Success)
        {
            cardName = printingMatch.Groups["name"].Value.Trim();
            setCode = NullIfWhiteSpace(printingMatch.Groups["set"].Value);
            collectorNumber = NullIfWhiteSpace(printingMatch.Groups["collector"].Value);
        }
        else
        {
            if (!match.Success && hashtagCategories.Count == 0)
            {
                return false;
            }

            cardName = remainder.Trim();
            if (string.IsNullOrWhiteSpace(cardName))
            {
                return false;
            }
        }

        var board = DetermineBoard(categoryText);
        entry = new DeckEntry
        {
            Name = cardName,
            NormalizedName = CardNormalizer.Normalize(cardName),
            Quantity = quantity,
            Board = board,
            SetCode = setCode,
            CollectorNumber = collectorNumber,
            Category = NormalizeCategory(categoryText),
            IsFoil = isFoil,
        };
        return true;
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

    private static List<string> ExtractHashtagCategories(ref string remainder)
    {
        var categories = HashtagRegex()
            .Matches(remainder)
            .Select(match => match.Groups["tag"].Value.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToList();

        if (categories.Count > 0)
        {
            remainder = HashtagRegex().Replace(remainder, string.Empty).Trim();
        }

        return categories;
    }

    [GeneratedRegex(@"^(?<quantity>\d+)\s+(?<rest>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"^(?<name>.+?)\s+\[(?<categories>.*)\]$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex CategoryRegex();

    [GeneratedRegex(@"^(?<name>.+?)\s+\((?<set>[^)]+)\)\s+(?<collector>\S+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex PrintingRegex();

    [GeneratedRegex(@"\{[^}]+\}", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex BraceTokenRegex();

    [GeneratedRegex(@"\s+#(?<tag>[A-Za-z0-9][A-Za-z0-9_-]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex HashtagRegex();

    private static bool IsIgnorableLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        var normalized = trimmed.TrimEnd(':');
        return string.Equals(normalized, "Deck", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Commander", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Maybeboard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Sideboard", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStoppingLine(string line)
    {
        var normalized = line.Trim().TrimEnd(':');
        return string.Equals(normalized, "Possible names", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Possible name", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Notes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Description", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Primer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonDeckTextLine(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return true;
        }

        if (trimmed.StartsWith("-", StringComparison.Ordinal)
            || trimmed.StartsWith("•", StringComparison.Ordinal)
            || trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.Contains("→", StringComparison.Ordinal)
            || trimmed.Contains("👉", StringComparison.Ordinal)
            || trimmed.Contains("🧩", StringComparison.Ordinal)
            || trimmed.Contains("🎯", StringComparison.Ordinal)
            || trimmed.Contains("💡", StringComparison.Ordinal)
            || trimmed.Contains("🔥", StringComparison.Ordinal)
            || trimmed.Contains("⚡", StringComparison.Ordinal)
            || trimmed.Contains("🧠", StringComparison.Ordinal)
            || trimmed.Contains("✅", StringComparison.Ordinal)
            || trimmed.Contains("🚀", StringComparison.Ordinal)
            || trimmed.Contains("❌", StringComparison.Ordinal))
        {
            return true;
        }

        if (char.IsDigit(trimmed[0]))
        {
            return false;
        }

        if (trimmed.Contains("(", StringComparison.Ordinal) && trimmed.Contains(")", StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}
