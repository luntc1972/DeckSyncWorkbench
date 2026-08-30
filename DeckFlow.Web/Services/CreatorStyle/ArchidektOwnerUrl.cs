using System.Text.RegularExpressions;

namespace DeckFlow.Web.Services.CreatorStyle;

/// <summary>
/// Parses Archidekt creator profile inputs while enforcing an HTTPS host guard.
/// </summary>
public static partial class ArchidektOwnerUrl
{
    private const string ArchidektApex = "archidekt.com";

    /// <summary>
    /// Tries to resolve an Archidekt username from a bare username or trusted profile URL.
    /// </summary>
    /// <param name="input">Bare username or Archidekt profile URL.</param>
    /// <param name="username">Resolved username when parsing succeeds.</param>
    /// <returns><see langword="true"/> when a username was resolved; otherwise <see langword="false"/>.</returns>
    public static bool TryGetUsername(string input, out string username)
    {
        username = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (!IsValidUsername(trimmed))
            {
                return false;
            }

            username = trimmed;
            return true;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsApprovedHost(uri.Host))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "u", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = segments[1];
        if (!IsValidUsername(candidate))
        {
            return false;
        }

        username = candidate;
        return true;
    }

    private static bool IsApprovedHost(string host)
    {
        return string.Equals(host, ArchidektApex, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + ArchidektApex, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidUsername(string candidate)
    {
        return UsernamePattern().IsMatch(candidate);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
