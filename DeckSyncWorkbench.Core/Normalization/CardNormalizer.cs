using System.Text.RegularExpressions;

namespace DeckSyncWorkbench.Core.Normalization;

public static partial class CardNormalizer
{
    public static string Normalize(string cardName)
    {
        ArgumentNullException.ThrowIfNull(cardName);

        var normalized = cardName.Trim().ToLowerInvariant();
        normalized = normalized.Replace("★", string.Empty, StringComparison.Ordinal);
        normalized = normalized.Replace("*f*", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" // ", " / ", StringComparison.Ordinal);

        var splitIndex = normalized.IndexOf(" / ", StringComparison.Ordinal);
        if (splitIndex >= 0)
        {
            normalized = normalized[..splitIndex];
        }

        normalized = PunctuationRegex().Replace(normalized, " ");
        normalized = MultiSpaceRegex().Replace(normalized, " ").Trim();
        return normalized.Trim();
    }

    [GeneratedRegex(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex PunctuationRegex();
}
