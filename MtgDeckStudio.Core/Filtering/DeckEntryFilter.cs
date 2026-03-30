using MtgDeckStudio.Core.Models;

namespace MtgDeckStudio.Core.Filtering;

public static class DeckEntryFilter
{
    public static List<DeckEntry> ExcludeMaybeboard(IEnumerable<DeckEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .Where(entry => !string.Equals(entry.Board, "maybeboard", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
