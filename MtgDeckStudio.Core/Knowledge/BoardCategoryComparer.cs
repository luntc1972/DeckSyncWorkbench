using System;
using System.Collections.Generic;

namespace MtgDeckStudio.Core.Knowledge;

public sealed class BoardCategoryComparer : IEqualityComparer<(string CardName, string Category, string Board)>
{
    public static BoardCategoryComparer Instance { get; } = new();

    private BoardCategoryComparer()
    {
    }

    public bool Equals((string CardName, string Category, string Board) x, (string CardName, string Category, string Board) y)
    {
        return string.Equals(x.CardName, y.CardName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Category, y.Category, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Board, y.Board, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode((string CardName, string Category, string Board) obj)
    {
        var nameHash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.CardName ?? string.Empty);
        var categoryHash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Category ?? string.Empty);
        var boardHash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Board ?? string.Empty);
        return HashCode.Combine(nameHash, categoryHash, boardHash);
    }
}
