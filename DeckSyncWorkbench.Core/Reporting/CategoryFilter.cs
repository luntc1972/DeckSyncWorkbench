namespace DeckSyncWorkbench.Core.Reporting;

public static class CategoryFilter
{
    private static readonly HashSet<string> ExcludedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Artifact",
        "Artifacts",
        "Battle",
        "Battles",
        "Creature",
        "Creatures",
        "Enchantment",
        "Enchantments",
        "Instant",
        "Instants",
        "Planeswalker",
        "Planeswalkers",
        "Sorcery",
        "Sorceries",
    };

    public static bool IsIncluded(string? category)
    {
        return !string.IsNullOrWhiteSpace(category) && !ExcludedCategories.Contains(category);
    }
}
