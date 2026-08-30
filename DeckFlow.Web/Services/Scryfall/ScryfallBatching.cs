namespace DeckFlow.Web.Services.Scryfall;

/// <summary>
/// Shared batching helpers for Scryfall request limits.
/// </summary>
internal static class ScryfallBatching
{
    internal static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> values, int size)
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
