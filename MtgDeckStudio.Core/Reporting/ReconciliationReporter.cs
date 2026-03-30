using System.Text;
using MtgDeckStudio.Core.Models;

namespace MtgDeckStudio.Core.Reporting;

public static class ReconciliationReporter
{
    public const string CategoryFixInstructions =
"""
=== How to fix missing or broken categories in Archidekt ===

BEFORE importing the delta file:
  1. Export your current Archidekt deck (the file you just used as input).
     Keep this as a backup in case anything goes wrong.

IMPORTING the delta file:
  2. In Archidekt, open your deck.
  3. Click Import > Add to existing deck (NOT Replace).
  4. Paste the delta import text from this tool.
  5. Confirm. Only the new cards will be added.
     Existing cards and their categories will not be touched.

IF you already imported the full Moxfield list by mistake (categories are gone):
  6. Do NOT re-import anything yet.
  7. Go to Archidekt > Import > Replace deck, and paste your backup export from step 1.
     This restores categories on all existing cards.
  8. Then come back here and import only the delta file from step 4.

AFTER importing the delta:
  9. New cards will appear uncategorized (usually in an "Uncategorized" group).
 10. In Archidekt, filter by category = Uncategorized.
 11. Assign categories to each new card manually.
     The reconciliation report above lists exactly which cards are new -
     use it as your checklist.

FOR printing conflicts (if you chose any Moxfield versions):
 12. In Archidekt, find the card in your deck.
 13. Click the card > Change Printing.
 14. Select the version shown in the swap checklist.
     The category will stay attached - only the printing changes.
""";

    public const string MoxfieldImportInstructions =
"""
=== How to import into Moxfield safely ===

  1. Open the destination deck in Moxfield.
  2. Use the import or bulk edit flow that appends cards instead of replacing the full list unless you intend to overwrite the deck.
  3. Paste the generated text from this tool.
  4. Review commander and maybeboard placement after import.

Notes:
  - Moxfield does not have Archidekt-style per-card categories in the plain text import format.
  - Category-to-tag export in this tool uses a best-effort Bulk Edit style like `#Draw #Ramp`.
    Verify imported tags in Moxfield after paste, especially when a card has multiple tags.
  - Printing conflicts are informational only. If you want the source printing, swap it manually after import.
""";

    public static void WriteReport(DeckDiff diff, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        File.WriteAllText(outputPath, ToText(diff));
    }

    public static string ToText(DeckDiff diff)
        => ToText(diff, "Moxfield", "Archidekt");

    public static string ToText(DeckDiff diff, string sourceSystem, string targetSystem)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSystem);

        var builder = new StringBuilder();
        AppendSection(builder, $"=== Cards to Add ({diff.ToAdd.Count}) ===", diff.ToAdd.Select(entry => $"{entry.Quantity} {entry.Name}"));
        builder.AppendLine();
        AppendSection(
            builder,
            $"=== Count Mismatches ({diff.CountMismatch.Count}) ===",
            diff.CountMismatch.Select(entry => $"{sourceSystem}: lower than {targetSystem} | {targetSystem} has +{entry.Quantity} {entry.Name}"));
        builder.AppendLine();
        AppendSection(
            builder,
            $"=== Only in {targetSystem} ({diff.OnlyInArchidekt.Count}) ===",
            diff.OnlyInArchidekt.Select(entry => $"{entry.Quantity} {entry.Name}{FormatCategory(entry.Category)}"));
        builder.AppendLine();
        AppendPrintingConflicts(builder, diff.PrintingConflicts, sourceSystem, targetSystem);
        builder.AppendLine();
        builder.Append(GetInstructions(targetSystem).TrimEnd());
        return builder.ToString().TrimEnd();
    }

    public static string GenerateSwapChecklist(List<PrintingConflict> resolved)
        => GenerateSwapChecklist(resolved, "Archidekt");

    public static string GenerateSwapChecklist(List<PrintingConflict> resolved, string targetSystem)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSystem);
        var chosen = resolved.Where(conflict => conflict.Resolution == PrintingChoice.UseMoxfield).ToList();
        if (chosen.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Cards to swap printing in {targetSystem}:");
        builder.AppendLine();

        foreach (var conflict in chosen)
        {
            builder.AppendLine(conflict.CardName);
            builder.AppendLine($"  Current:  ({conflict.ArchidektVersion.SetCode}) {conflict.ArchidektVersion.CollectorNumber}");
            builder.AppendLine($"  Swap to:  ({conflict.MoxfieldVersion.SetCode}) {conflict.MoxfieldVersion.CollectorNumber}");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static string GetInstructions(string targetSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSystem);
        return string.Equals(targetSystem, "Archidekt", StringComparison.OrdinalIgnoreCase)
            ? CategoryFixInstructions
            : MoxfieldImportInstructions;
    }

    private static void AppendSection(StringBuilder builder, string header, IEnumerable<string> lines)
    {
        builder.AppendLine(header);
        var materialized = lines.ToList();
        if (materialized.Count == 0)
        {
            builder.AppendLine("  none");
            return;
        }

        foreach (var line in materialized)
        {
            builder.AppendLine($"  {line}");
        }
    }

    private static void AppendPrintingConflicts(StringBuilder builder, IReadOnlyList<PrintingConflict> conflicts, string sourceSystem, string targetSystem)
    {
        builder.AppendLine($"=== Printing Conflicts ({conflicts.Count}) ===");
        if (conflicts.Count == 0)
        {
            builder.AppendLine("  none");
            return;
        }

        foreach (var conflict in conflicts)
        {
            builder.AppendLine($"  {conflict.CardName}");
            builder.AppendLine($"    {targetSystem}: ({conflict.ArchidektVersion.SetCode}) {conflict.ArchidektVersion.CollectorNumber}{FormatCategoryLabel(conflict.ArchidektVersion.Category)}  <- kept by default");
            builder.AppendLine($"    {sourceSystem}:  ({conflict.MoxfieldVersion.SetCode}) {conflict.MoxfieldVersion.CollectorNumber}");
        }
    }

    private static string FormatCategory(string? category) => string.IsNullOrWhiteSpace(category) ? string.Empty : $" [{category}]";

    private static string FormatCategoryLabel(string? category) => string.IsNullOrWhiteSpace(category) ? string.Empty : $" [category: {category}]";
}
