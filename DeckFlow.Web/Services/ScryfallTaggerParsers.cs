using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeckFlow.Web.Services;

/// <summary>
/// Pure parsing helpers for <see cref="ScryfallTaggerService"/>.
/// </summary>
internal static partial class ScryfallTaggerParsers
{
    internal static string NormalizeTagName(string tag)
    {
        var text = tag.Replace('-', ' ').Replace('_', ' ').Trim();
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    internal static string? TryExtractCsrfToken(string html)
    {
        var match = CsrfMetaTagRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static IReadOnlyList<string> ParseOracleTagsFromJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("card", out var card)
                || !card.TryGetProperty("taggings", out var taggings)
                || taggings.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tagging in taggings.EnumerateArray())
            {
                if (!tagging.TryGetProperty("tag", out var tag))
                {
                    continue;
                }

                var type = tag.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                if (!string.Equals(type, "ORACLE_CARD_TAG", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = tag.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    tags.Add(NormalizeTagName(name));
                }
            }

            return tags.ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex("<meta\\s+name=\"csrf-token\"\\s+content=\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CsrfMetaTagRegex();
}
