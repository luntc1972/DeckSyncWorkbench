namespace DeckFlow.Web.Services.Scryfall;

/// <summary>
/// Shared batching helpers for Scryfall request limits.
/// </summary>
internal static class ScryfallBatching
{
    internal static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> values, int size)
    {
        // Why (WR-16): validate eagerly, not inside the iterator block — a C# iterator method
        // only runs its body (including argument checks) on the first MoveNext(), so validation
        // inside the yield-returning method would not fire until enumeration begins. size == 0
        // never advances the loop and hangs the caller; a negative size walks the index backwards
        // into an out-of-range access on values[].
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        return ChunkIterator(values, size);
    }

    private static IEnumerable<List<T>> ChunkIterator<T>(IReadOnlyList<T> values, int size)
    {
        for (var index = 0; index < values.Count; index += size)
        {
            var count = Math.Min(size, values.Count - index);
            var chunk = new List<T>(count);
            for (var itemIndex = 0; itemIndex < count; itemIndex++)
            {
                chunk.Add(values[index + itemIndex]);
            }

            yield return chunk;
        }
    }
}
