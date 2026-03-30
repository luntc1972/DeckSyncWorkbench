using System.Linq;

namespace MtgDeckStudio.Core.Exporting;

internal static class CategoryNormalization
{
    public static string? NormalizeSourceCategoriesForTarget(string? sourceCategory, string targetSystem)
    {
        if (string.IsNullOrWhiteSpace(sourceCategory))
        {
            return null;
        }

        if (!string.Equals(targetSystem, "Archidekt", System.StringComparison.OrdinalIgnoreCase))
        {
            return sourceCategory.Trim();
        }

        var normalized = sourceCategory
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .Select(NormalizeArchidektCategory)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        normalized.RemoveAll(value => string.Equals(value, "Commander", System.StringComparison.OrdinalIgnoreCase));

        return normalized.Count == 0 ? null : string.Join(",", normalized);
    }

    private static string NormalizeArchidektCategory(string value)
    {
        var candidate = value.Trim();
        if (candidate.StartsWith("#", System.StringComparison.Ordinal))
        {
            candidate = candidate[1..];
        }

        var braceIndex = candidate.IndexOf('{');
        if (braceIndex >= 0)
        {
            candidate = candidate[..braceIndex];
        }

        return candidate.Trim();
    }
}
