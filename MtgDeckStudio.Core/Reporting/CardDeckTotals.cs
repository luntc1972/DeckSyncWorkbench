using System;
using System.Collections.Generic;

namespace MtgDeckStudio.Core.Reporting;

public sealed record CardDeckTotals(int TotalDeckCount, IReadOnlyDictionary<string, int> BoardDeckCounts)
{
    /// <summary>
    /// Represents an empty set of deck totals.
    /// </summary>
    public static CardDeckTotals Empty { get; } = new(0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
}
