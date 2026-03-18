using System.Text.RegularExpressions;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Normalization;

namespace DeckSyncWorkbench.Core.Parsing;

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

            if (IsSectionHeader(line, "Sideboard"))
            {
                break;
            }

            if (TryGetBoardHeader(line, out var headerBoard))
            {
                board = headerBoard;
                commanderSectionActive = headerBoard == "commander";
                continue;
            }

            var match = QuantityRegex().Match(line);
            if (!match.Success)
            {
                throw new DeckParseException($"Unable to parse Moxfield line {i + 1}: \"{line}\"");
            }

            var quantity = int.Parse(match.Groups["quantity"].Value);
            if (quantity == 0)
            {
                continue;
            }

            var remainder = match.Groups["rest"].Value.Trim();
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

            entries.Add(new DeckEntry
            {
                Name = CleanName(rawName),
                NormalizedName = CardNormalizer.Normalize(CleanName(rawName)),
                Quantity = quantity,
                Board = board,
                SetCode = setCode,
                CollectorNumber = collectorNumber,
                IsFoil = isFoil,
                Category = board == "maybeboard" ? "Maybeboard" : null,
            });
        }

        if (entries.Count == 0)
        {
            throw new DeckParseException("Moxfield text did not contain any card lines.");
        }

        return entries;
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

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^(?<quantity>\d+)\s+(?<rest>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"^(?<name>.+?)\s+\((?<set>[^)]+)\)\s+(?<collector>\S+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex PrintingRegex();
}
