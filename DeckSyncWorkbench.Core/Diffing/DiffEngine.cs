using DeckSyncWorkbench.Core.Models;

namespace DeckSyncWorkbench.Core.Diffing;

public sealed class DiffEngine
{
    private readonly MatchMode _matchMode;

    public DiffEngine(MatchMode matchMode)
    {
        _matchMode = matchMode;
    }

    public DeckDiff Compare(List<DeckEntry> moxfield, List<DeckEntry> archidekt)
    {
        ArgumentNullException.ThrowIfNull(moxfield);
        ArgumentNullException.ThrowIfNull(archidekt);

        var toAdd = new List<DeckEntry>();
        var countMismatch = new List<DeckEntry>();
        var onlyInArchidekt = new List<DeckEntry>();
        var printingConflicts = new List<PrintingConflict>();

        var moxLoose = BuildLookup(moxfield, MatchMode.Loose);
        var archLoose = BuildLookup(archidekt, MatchMode.Loose);
        var moxStrict = BuildLookup(moxfield, MatchMode.Strict);
        var archStrict = BuildLookup(archidekt, MatchMode.Strict);

        foreach (var pair in moxfield)
        {
            var looseKey = BuildKey(pair, MatchMode.Loose);
            var strictKey = BuildKey(pair, MatchMode.Strict);

            if (_matchMode == MatchMode.Strict)
            {
                if (!archStrict.TryGetValue(strictKey, out var strictArchMatch))
                {
                    if (TryGetLooseMatch(archLoose, pair, out var looseArchMatch))
                    {
                        printingConflicts.Add(CreateConflict(pair, looseArchMatch.Representative));
                    }
                    else
                    {
                        toAdd.Add(pair);
                    }

                    continue;
                }

                CompareQuantities(pair, strictArchMatch, toAdd, countMismatch);
                continue;
            }

            if (!TryGetLooseMatch(archLoose, pair, out var looseMatch))
            {
                toAdd.Add(pair);
                continue;
            }

            if (HasPrintingConflict(pair, looseMatch.Representative))
            {
                printingConflicts.Add(CreateConflict(pair, looseMatch.Representative));
                continue;
            }

            CompareQuantities(pair, looseMatch, toAdd, countMismatch);
        }

        foreach (var entry in archidekt)
        {
            var looseKey = BuildKey(entry, MatchMode.Loose);
            var strictKey = BuildKey(entry, MatchMode.Strict);

            var existsInMoxfield = _matchMode == MatchMode.Strict
                ? moxStrict.ContainsKey(strictKey) || TryHasLooseMatch(moxLoose, entry)
                : TryHasLooseMatch(moxLoose, entry);

            if (!existsInMoxfield)
            {
                onlyInArchidekt.Add(entry);
            }
        }

        return new DeckDiff(
            Consolidate(toAdd),
            Consolidate(countMismatch),
            Consolidate(onlyInArchidekt),
            DistinctConflicts(printingConflicts));
    }

    private static void CompareQuantities(DeckEntry moxEntry, AggregatedEntry archMatch, List<DeckEntry> toAdd, List<DeckEntry> countMismatch)
    {
        var delta = moxEntry.Quantity - archMatch.Quantity;
        if (delta > 0)
        {
            toAdd.Add(moxEntry with { Quantity = delta });
        }
        else if (delta < 0)
        {
            countMismatch.Add(archMatch.Representative with { Quantity = archMatch.Quantity - moxEntry.Quantity });
        }
    }

    private static Dictionary<string, AggregatedEntry> BuildLookup(IEnumerable<DeckEntry> entries, MatchMode matchMode)
    {
        var lookup = new Dictionary<string, AggregatedEntry>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var key = BuildKey(entry, matchMode);
            if (lookup.TryGetValue(key, out var existing))
            {
                lookup[key] = existing with { Quantity = existing.Quantity + entry.Quantity };
                continue;
            }

            lookup[key] = new AggregatedEntry(entry, entry.Quantity);
        }

        return lookup;
    }

    private static bool TryGetLooseMatch(Dictionary<string, AggregatedEntry> lookup, DeckEntry entry, out AggregatedEntry match)
    {
        if (lookup.TryGetValue(BuildKey(entry, MatchMode.Loose), out var directMatch))
        {
            match = directMatch;
            return true;
        }

        var fallbackKey = BuildCommanderFallbackKey(entry);
        if (fallbackKey is not null && lookup.TryGetValue(fallbackKey, out var fallbackMatch))
        {
            match = fallbackMatch;
            return true;
        }

        match = default!;
        return false;
    }

    private static bool TryHasLooseMatch(Dictionary<string, AggregatedEntry> lookup, DeckEntry entry)
        => TryGetLooseMatch(lookup, entry, out _);

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

    private static string BuildKey(DeckEntry entry, MatchMode matchMode)
    {
        if (matchMode == MatchMode.Strict)
        {
            return $"{entry.NormalizedName}|{entry.Board}|{entry.SetCode?.ToLowerInvariant()}|{entry.CollectorNumber?.ToLowerInvariant()}";
        }

        return $"{entry.NormalizedName}|{entry.Board}";
    }

    private static bool HasPrintingConflict(DeckEntry moxfieldEntry, DeckEntry archidektEntry)
    {
        if (string.IsNullOrWhiteSpace(moxfieldEntry.SetCode) || string.IsNullOrWhiteSpace(archidektEntry.SetCode))
        {
            return false;
        }

        return !string.Equals(moxfieldEntry.SetCode, archidektEntry.SetCode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(moxfieldEntry.CollectorNumber, archidektEntry.CollectorNumber, StringComparison.OrdinalIgnoreCase);
    }

    private static PrintingConflict CreateConflict(DeckEntry moxfieldEntry, DeckEntry archidektEntry)
    {
        return new PrintingConflict
        {
            CardName = moxfieldEntry.Name,
            MoxfieldVersion = moxfieldEntry,
            ArchidektVersion = archidektEntry,
        };
    }

    private static IReadOnlyList<DeckEntry> Consolidate(IEnumerable<DeckEntry> entries)
    {
        return entries
            .GroupBy(entry => $"{entry.NormalizedName}|{entry.Board}|{entry.SetCode}|{entry.CollectorNumber}|{entry.Category}", StringComparer.Ordinal)
            .Select(group => group.First() with { Quantity = group.Sum(item => item.Quantity) })
            .OrderBy(entry => BoardOrder(entry.Board))
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<PrintingConflict> DistinctConflicts(IEnumerable<PrintingConflict> conflicts)
    {
        return conflicts
            .GroupBy(conflict => $"{conflict.CardName}|{conflict.ArchidektVersion.Board}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(conflict => conflict.CardName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int BoardOrder(string board) => board switch
    {
        "commander" => 0,
        "mainboard" => 1,
        "sideboard" => 2,
        "maybeboard" => 3,
        _ => 4,
    };

    private sealed record AggregatedEntry(DeckEntry Representative, int Quantity);
}
