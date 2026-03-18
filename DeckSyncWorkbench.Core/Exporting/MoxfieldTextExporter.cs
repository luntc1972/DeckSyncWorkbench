using System.Text;
using DeckSyncWorkbench.Core.Models;

namespace DeckSyncWorkbench.Core.Exporting;

public static class MoxfieldTextExporter
{
    public static void WriteFile(List<DeckEntry> entries, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        File.WriteAllText(outputPath, ToText(entries));
    }

    public static string ToText(List<DeckEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        AppendSection(builder, null, entries.Where(entry => string.Equals(NormalizeBoard(entry.Board), "mainboard", StringComparison.OrdinalIgnoreCase)).ToList());
        AppendSection(builder, "Maybeboard", entries.Where(entry => string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase)).ToList());
        return builder.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder builder, string? header, List<DeckEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(header))
        {
            builder.AppendLine($"{header}:");
        }

        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(FormatLine(entry));
        }
    }

    private static string FormatLine(DeckEntry entry)
    {
        var line = new StringBuilder();
        line.Append($"{entry.Quantity} {entry.Name}");

        if (!string.IsNullOrWhiteSpace(entry.SetCode) && !string.IsNullOrWhiteSpace(entry.CollectorNumber))
        {
            line.Append($" ({entry.SetCode}) {entry.CollectorNumber}");
        }

        if (entry.IsFoil)
        {
            line.Append(" *F*");
        }

        if (!string.IsNullOrWhiteSpace(entry.Category))
        {
            foreach (var tag in entry.Category.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                line.Append($" #{tag}");
            }
        }

        return line.ToString();
    }

    private static string NormalizeBoard(string board)
    {
        return string.Equals(board, "commander", StringComparison.OrdinalIgnoreCase)
            ? "mainboard"
            : board;
    }
}
