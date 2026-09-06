using System.Text;
using DeckFlow.Core.Models;
using DeckFlow.Core.Modular;
using DeckFlow.Web.Models.DeckModules;

namespace DeckFlow.Web.Services.Modular;

/// <summary>
/// Serializes a compiled Deck Modules configuration into plain decklist text. Extracted from
/// <see cref="DeckFlow.Web.Controllers.DeckModulesController"/> so the export path and the
/// analysis path always read the same compiled configuration through the same code (D-06) —
/// export text and analysis input can never diverge.
/// </summary>
public static class DeckModulesDecklistSerializer
{
    /// <summary>
    /// Builds the full export text for a compiled configuration: command zone, mainboard, and
    /// the IN/OUT/RESET swap plan. Identical output to the historic <c>DeckModulesController.Export</c>
    /// body — this is a pure extraction, not a behavior change.
    /// </summary>
    /// <param name="compilation">The compiled configuration to serialize.</param>
    public static string BuildExportText(DeckModulesCompilationViewModel compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var builder = new StringBuilder();
        AppendEntries(builder, "Command Zone", compilation.CommandZoneEntries);
        AppendEntries(builder, "Mainboard", compilation.MainboardEntries);
        AppendSwapEntries(builder, "IN", compilation.SwapPlan.ToAdd);
        AppendSwapEntries(builder, "OUT", compilation.SwapPlan.ToRemove);
        AppendSwapEntries(builder, "RESET", compilation.SwapPlan.ToReset);
        return builder.ToString();
    }

    /// <summary>
    /// Builds the decklist text handed to <see cref="DeckFlow.Web.Services.Manabase.IManabaseAnalysisService"/>:
    /// the Command Zone and Mainboard sections only. Swap lines (IN/OUT/RESET) describe baseline-relative
    /// deltas, not decklist contents, and must never be fed to the analyzer as deck input.
    /// </summary>
    /// <param name="compilation">The compiled configuration to serialize.</param>
    public static string BuildAnalysisDecklistText(DeckModulesCompilationViewModel compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var builder = new StringBuilder();
        AppendEntries(builder, "Command Zone", compilation.CommandZoneEntries);
        AppendEntries(builder, "Mainboard", compilation.MainboardEntries);
        return builder.ToString();
    }

    private static void AppendEntries(StringBuilder builder, string heading, IReadOnlyList<DeckEntry> entries)
    {
        builder.Append("== ").Append(heading).AppendLine(" ==");
        foreach (var entry in entries)
        {
            builder.Append(entry.Quantity).Append(' ').AppendLine(NormalizeLine(entry.Name));
        }

        builder.AppendLine();
    }

    private static void AppendSwapEntries(StringBuilder builder, string prefix, IReadOnlyList<ModularDeckSwapEntry> entries)
    {
        foreach (var entry in entries)
        {
            var sign = entry.Action == ModularDeckSwapAction.Add ? '+' : '-';
            builder.Append(prefix).Append(" - ").Append(sign).Append(entry.Quantity).Append(' ').AppendLine(NormalizeLine(entry.Name));
        }
    }

    /// <summary>
    /// Normalizes a single decklist line. Deliberately independent of
    /// <see cref="DeckFlow.Web.Controllers.DeckModulesController"/>'s own private <c>NormalizeLine</c>
    /// (Review Dispositions Ledger, Round 1, finding 3) so the two call sites stay independently
    /// maintainable rather than one reaching back into the other.
    /// </summary>
    private static string NormalizeLine(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
