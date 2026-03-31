using System.Text;
using MtgDeckStudio.Core.Models;

namespace MtgDeckStudio.Core.Exporting;

public static class FullImportExporter
{
    public static void WriteFile(List<DeckEntry> moxfield, List<DeckEntry> archidekt, MatchMode matchMode, string outputPath, IReadOnlyList<PrintingConflict>? conflicts = null)
        => WriteFile(moxfield, archidekt, matchMode, outputPath, "Archidekt", conflicts);

    public static void WriteFile(List<DeckEntry> sourceEntries, List<DeckEntry> targetEntries, MatchMode matchMode, string outputPath, string targetSystem, IReadOnlyList<PrintingConflict>? conflicts = null, CategorySyncMode categoryMode = CategorySyncMode.TargetCategories)
    {
        ArgumentNullException.ThrowIfNull(sourceEntries);
        ArgumentNullException.ThrowIfNull(targetEntries);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        File.WriteAllText(outputPath, ToText(sourceEntries, targetEntries, matchMode, targetSystem, conflicts, categoryMode));
    }

    public static string ToText(List<DeckEntry> moxfield, List<DeckEntry> archidekt, MatchMode matchMode, IReadOnlyList<PrintingConflict>? conflicts = null)
        => ToText(moxfield, archidekt, matchMode, "Archidekt", conflicts);

    public static string ToText(List<DeckEntry> sourceEntries, List<DeckEntry> targetEntries, MatchMode matchMode, string targetSystem, IReadOnlyList<PrintingConflict>? conflicts = null, CategorySyncMode categoryMode = CategorySyncMode.TargetCategories)
    {
        ArgumentNullException.ThrowIfNull(sourceEntries);
        ArgumentNullException.ThrowIfNull(targetEntries);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSystem);

        var targetLookup = targetEntries
            .GroupBy(entry => BuildLooseKey(entry), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var conflictLookup = (conflicts ?? [])
            .ToDictionary(conflict => BuildLooseKey(conflict.MoxfieldVersion), StringComparer.Ordinal);

        var resolvedEntries = new List<DeckEntry>();
        foreach (var entry in sourceEntries
            .Where(entry => !string.Equals(entry.Board, "sideboard", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => BoardOrder(entry.Board))
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = BuildLooseKey(entry);
            var effectiveEntry = entry;
            if (conflictLookup.TryGetValue(key, out var conflict))
            {
                effectiveEntry = conflict.Resolution == PrintingChoice.UseMoxfield
                    ? entry with
                    {
                        Board = conflict.ArchidektVersion.Board,
                        Category = SelectCategory(entry.Category, conflict.ArchidektVersion.Category, targetSystem, categoryMode),
                    }
                    : entry with
                    {
                        Board = conflict.ArchidektVersion.Board,
                        SetCode = conflict.ArchidektVersion.SetCode,
                        CollectorNumber = conflict.ArchidektVersion.CollectorNumber,
                        Category = SelectCategory(entry.Category, conflict.ArchidektVersion.Category, targetSystem, categoryMode),
                    };
            }
            else if (TryGetTargetMatch(targetLookup, entry, out var targetMatch))
            {
                effectiveEntry = entry with
                {
                    Board = targetMatch.Board,
                    SetCode = string.IsNullOrWhiteSpace(targetMatch.SetCode) ? entry.SetCode : targetMatch.SetCode,
                    CollectorNumber = string.IsNullOrWhiteSpace(targetMatch.CollectorNumber) ? entry.CollectorNumber : targetMatch.CollectorNumber,
                    Category = SelectCategory(entry.Category, targetMatch.Category, targetSystem, categoryMode),
                };
            }

            resolvedEntries.Add(effectiveEntry);
        }

        var finalEntries = DeduplicateCommanderCopies(resolvedEntries);
        var builder = new StringBuilder();
        string? currentBoard = null;

        foreach (var entry in finalEntries.OrderBy(entry => BoardOrder(OutputBoard(entry.Board, targetSystem))).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            var outputBoard = OutputBoard(entry.Board, targetSystem);
            if (!string.Equals(currentBoard, outputBoard, StringComparison.Ordinal))
            {
                currentBoard = outputBoard;
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.AppendLine($"// {TitleCaseBoard(currentBoard)}");
            }

            builder.AppendLine(FormatLine(entry, targetSystem));
        }

        return builder.ToString().TrimEnd();
    }

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

        return entry.Category;
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

    private static string BuildLooseKey(DeckEntry entry) => $"{entry.NormalizedName}|{entry.Board}";

    private static bool TryGetTargetMatch(Dictionary<string, DeckEntry> targetLookup, DeckEntry sourceEntry, out DeckEntry targetMatch)
    {
        if (targetLookup.TryGetValue(BuildLooseKey(sourceEntry), out targetMatch!))
        {
            return true;
        }

        var fallbackKey = BuildCommanderFallbackKey(sourceEntry);
        if (fallbackKey is not null && targetLookup.TryGetValue(fallbackKey, out targetMatch!))
        {
            return true;
        }

        targetMatch = default!;
        return false;
    }

    private static string? BuildCommanderFallbackKey(DeckEntry entry)
    {
        if (string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
        {
            return $"{entry.NormalizedName}|mainboard";
        }

        if (string.Equals(entry.Board, "mainboard", StringComparison.OrdinalIgnoreCase))
        {
            return $"{entry.NormalizedName}|commander";
        }

        return null;
    }

    private static string? SelectCategory(string? sourceCategory, string? targetCategory, string targetSystem, CategorySyncMode categoryMode)
    {
        var normalizedSource = CategoryNormalization.NormalizeSourceCategoriesForTarget(sourceCategory, targetSystem);
        return categoryMode switch
        {
            CategorySyncMode.SourceTags => normalizedSource,
            CategorySyncMode.Combined => CombineCategories(targetCategory, normalizedSource),
            _ => string.Equals(targetSystem, "Moxfield", StringComparison.OrdinalIgnoreCase)
                ? normalizedSource
                : targetCategory,
        };
    }

    private static string? CombineCategories(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collected = new List<string>();

        void AddEntries(string source)
        {
            foreach (var part in source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(part))
                {
                    collected.Add(part);
                }
            }
        }

        AddEntries(first);
        AddEntries(second);

        return collected.Count == 0 ? null : string.Join(",", collected);
    }

    private static IReadOnlyList<DeckEntry> DeduplicateCommanderCopies(IEnumerable<DeckEntry> entries)
    {
        var materialized = entries.ToList();
        var commanderNames = materialized
            .Where(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.NormalizedName)
            .ToHashSet(StringComparer.Ordinal);

        return materialized
            .Where(entry => !string.Equals(entry.Board, "mainboard", StringComparison.OrdinalIgnoreCase)
                || !commanderNames.Contains(entry.NormalizedName))
            .GroupBy(entry => BuildDeduplicationKey(entry), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static string BuildDeduplicationKey(DeckEntry entry)
    {
        if (string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase))
        {
            return $"{entry.NormalizedName}|commander";
        }

        return $"{entry.NormalizedName}|{entry.Board}|{entry.SetCode}|{entry.CollectorNumber}|{entry.Category}";
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

    private static int BoardOrder(string board) => board switch
    {
        "commander" => 0,
        "mainboard" => 1,
        "maybeboard" => 2,
        _ => 3,
    };

    private static string TitleCaseBoard(string board) => board switch
    {
        "commander" => "Commander",
        "maybeboard" => "Maybeboard",
        _ => "Mainboard",
    };
}
