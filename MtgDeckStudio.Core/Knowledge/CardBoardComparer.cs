using System;
using System.Collections.Generic;

namespace MtgDeckStudio.Core.Knowledge;

internal sealed class CardBoardComparer : IEqualityComparer<(string CardName, string Board)>
{
    public static CardBoardComparer Instance { get; } = new();

    private CardBoardComparer()
    {
    }

    public bool Equals((string CardName, string Board) x, (string CardName, string Board) y)
    {
        return string.Equals(x.CardName, y.CardName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Board, y.Board, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode((string CardName, string Board) obj)
    {
        var nameHash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.CardName ?? string.Empty);
        var boardHash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Board ?? string.Empty);
        return HashCode.Combine(nameHash, boardHash);
    }
}
