using System.Text.RegularExpressions;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Normalization;

namespace MtgDeckStudio.Core.Parsing;

public sealed partial class MoxfieldParser : IParser
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
            throw new DeckParseException("Moxfield text is empty.");
        }

        var entries = new List<DeckEntry>();
        var board = "mainboard";
        var commanderSectionActive = false;
        var foundEntries = false;
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (commanderSectionActive)
                {
                    board = "mainboard";
                    commanderSectionActive = false;
                }

                continue;
            }

            if (IsStoppingLine(line) && foundEntries)
            {
                break;
            }

            if (TryGetBoardHeader(line, out var headerBoard))
            {
                board = headerBoard;
                commanderSectionActive = headerBoard == "commander";
                continue;
            }

            if (IsIgnorableLine(line))
            {
                continue;
            }

            if (!TryParseEntry(line, board, allowImplicitQuantity: true, out var entry))
            {
                if (foundEntries && IsNonDeckTextLine(line))
                {
                    continue;
                }

                throw new DeckParseException($"Unable to parse Moxfield line {i + 1}: \"{line}\"");
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
            throw new DeckParseException("Moxfield text did not contain any card lines.");
        }

        return entries;
    }

    private static bool TryParseEntry(string line, string board, bool allowImplicitQuantity, out DeckEntry entry)
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

        var setMatch = PrintingRegex().Match(remainder);
        var rawName = remainder;
        string? setCode = null;
        string? collectorNumber = null;
        if (setMatch.Success)
        {
            rawName = setMatch.Groups["name"].Value.Trim();
            setCode = NullIfWhiteSpace(setMatch.Groups["set"].Value);
            collectorNumber = NullIfWhiteSpace(setMatch.Groups["collector"].Value);
        }
        else if (!match.Success && string.IsNullOrWhiteSpace(remainder))
        {
            return false;
        }

        var cleanName = CleanName(rawName);
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            return false;
        }

        entry = new DeckEntry
        {
            Name = cleanName,
            NormalizedName = CardNormalizer.Normalize(cleanName),
            Quantity = quantity,
            Board = board,
            SetCode = setCode,
            CollectorNumber = collectorNumber,
            IsFoil = isFoil,
            Category = board switch
            {
                "maybeboard" => "Maybeboard",
                "sideboard" => "Sideboard",
                _ => null
            },
        };
        return true;
    }

    private static bool TryGetBoardHeader(string line, out string board)
    {
        if (IsSectionHeader(line, "Commander"))
        {
            board = "commander";
            return true;
        }

        if (IsSectionHeader(line, "Maybeboard"))
        {
            board = "maybeboard";
            return true;
        }

        if (IsSectionHeader(line, "Sideboard"))
        {
            board = "sideboard";
            return true;
        }

        board = string.Empty;
        return false;
    }

    private static string CleanName(string rawName)
    {
        return rawName
            .Replace("★", string.Empty, StringComparison.Ordinal)
            .Replace("*F*", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static bool IsSectionHeader(string line, string header)
        => string.Equals(line.TrimEnd(':'), header, StringComparison.OrdinalIgnoreCase);

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

        return true;
    }

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^(?<quantity>\d+)\s+(?<rest>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"^(?<name>.+?)\s+\((?<set>[^)]+)\)\s+(?<collector>\S+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex PrintingRegex();
}
