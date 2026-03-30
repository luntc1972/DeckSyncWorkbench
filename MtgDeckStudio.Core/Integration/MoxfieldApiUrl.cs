namespace MtgDeckStudio.Core.Integration;

public static class MoxfieldApiUrl
{
    public static bool TryGetDeckId(string input, out string deckId)
    {
        deckId = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            deckId = input.Trim();
            return deckId.Length > 0;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && string.Equals(segments[0], "decks", StringComparison.OrdinalIgnoreCase))
        {
            deckId = segments[1];
            return deckId.Length > 0;
        }

        return false;
    }

    public static Uri BuildDeckApiUri(string deckId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deckId);
        return new Uri($"https://api.moxfield.com/v2/decks/all/{deckId}", UriKind.Absolute);
    }
}
