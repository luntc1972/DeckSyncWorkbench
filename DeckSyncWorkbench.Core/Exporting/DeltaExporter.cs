using System.Text;
using DeckSyncWorkbench.Core.Models;

namespace DeckSyncWorkbench.Core.Exporting;

public static class DeltaExporter
{
    public static void WriteFile(List<DeckEntry> toAdd, string outputPath)
        => WriteFile(toAdd, outputPath, "Archidekt");

    public static void WriteFile(List<DeckEntry> toAdd, string outputPath, string targetSystem)
    {
        ArgumentNullException.ThrowIfNull(toAdd);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        File.WriteAllText(outputPath, ToText(toAdd, targetSystem));
    }

    public static string ToText(List<DeckEntry> toAdd)
        => ToText(toAdd, "Archidekt");

    public static string ToText(List<DeckEntry> toAdd, string targetSystem)
    {
        ArgumentNullException.ThrowIfNull(toAdd);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSystem);
        if (toAdd.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        string? currentBoard = null;

        foreach (var entry in toAdd.OrderBy(entry => BoardOrder(OutputBoard(entry.Board, targetSystem))).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            var outputBoard = OutputBoard(entry.Board, targetSystem);
            if (!string.Equals(currentBoard, outputBoard, StringComparison.Ordinal))
            {
                currentBoard = outputBoard;
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                if (!string.Equals(currentBoard, "mainboard", StringComparison.Ordinal))
                {
                    builder.AppendLine($"// {TitleCaseBoard(currentBoard)}");
                }
            }

            builder.AppendLine(FormatLine(entry, targetSystem));
        }

        return builder.ToString().TrimEnd();
    }

    private static int BoardOrder(string board) => board switch
    {
        "commander" => 0,
        "mainboard" => 1,
        "sideboard" => 2,
        "maybeboard" => 3,
        _ => 4,
    };

    private static string TitleCaseBoard(string board) => board switch
    {
        "commander" => "Commander",
        "sideboard" => "Sideboard",
        "maybeboard" => "Maybeboard",
        _ => "Mainboard",
    };

    private static string FormatLine(DeckEntry entry, string targetSystem)
    {
        var line = new StringBuilder();
        line.Append($"{entry.Quantity} {FormatCardName(entry.Name, targetSystem)}");

        if (!string.IsNullOrWhiteSpace(entry.SetCode) && !string.IsNullOrWhiteSpace(entry.CollectorNumber))
        {
            line.Append($" ({entry.SetCode}) {entry.CollectorNumber}");
        }

        var categorySuffix = BuildArchidektCategorySuffix(entry, targetSystem);
        if (!string.IsNullOrWhiteSpace(categorySuffix))
        {
            line.Append($" [{categorySuffix}]");
        }

        var moxfieldTags = BuildMoxfieldTags(entry, targetSystem);
        if (!string.IsNullOrWhiteSpace(moxfieldTags))
        {
            line.Append($" {moxfieldTags}");
        }

        return line.ToString();
    }

    private static string FormatCardName(string name, string targetSystem)
    {
        if (!string.Equals(targetSystem, "Archidekt", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return name.Replace(" / ", " // ", StringComparison.Ordinal);
    }

    private static string? BuildArchidektCategorySuffix(DeckEntry entry, string targetSystem)
    {
        if (!string.Equals(targetSystem, "Archidekt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(entry.Category)
                ? "Commander"
                : $"Commander,{entry.Category}";
        }

        return null;
    }

    private static string? BuildMoxfieldTags(DeckEntry entry, string targetSystem)
    {
        if (!string.Equals(targetSystem, "Moxfield", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(entry.Category))
        {
            return null;
        }

        var tags = entry.Category
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tag => $"#{tag}")
            .ToList();

        return tags.Count == 0 ? null : string.Join(" ", tags);
    }

    private static string OutputBoard(string board, string targetSystem)
    {
        if (string.Equals(targetSystem, "Moxfield", StringComparison.OrdinalIgnoreCase)
            && string.Equals(board, "commander", StringComparison.OrdinalIgnoreCase))
        {
            return "mainboard";
        }

        return board;
    }
}
