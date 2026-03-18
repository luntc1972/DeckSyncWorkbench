using System;
using System.Collections.Generic;

namespace DeckSyncWorkbench.Core.Knowledge;

public sealed class CardCategoryComparer : IEqualityComparer<(string CardName, string Category)>
{
    public static CardCategoryComparer Instance { get; } = new();

    private CardCategoryComparer()
    {
    }

    public bool Equals((string CardName, string Category) x, (string CardName, string Category) y)
    {
        return string.Equals(x.CardName, y.CardName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Category, y.Category, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode((string CardName, string Category) obj)
    {
        var nameHash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.CardName ?? string.Empty);
        var categoryHash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Category ?? string.Empty);
        return HashCode.Combine(nameHash, categoryHash);
    }
}
