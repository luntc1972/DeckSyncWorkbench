using Microsoft.AspNetCore.Http;

namespace DeckFlow.Web.Security;

/// <summary>
/// Validates that browser-originated unsafe requests target DeckFlow from the same origin.
/// </summary>
public static class SameOriginRequestValidator
{
    private const string ForbiddenMessage = "This endpoint only accepts same-origin browser requests.";

    /// <summary>
    /// Determines whether the current request should be accepted based on its Origin or Referer headers.
    /// </summary>
    /// <param name="request">Incoming HTTP request.</param>
    /// <returns><see langword="true"/> when the request is same-origin or lacks browser origin metadata; otherwise, <see langword="false"/>.</returns>
    public static bool IsValid(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (TryParseOrigin(request.Headers.Origin, out var origin))
        {
            return UriMatchesRequestOrigin(origin, request);
        }

        if (TryParseOrigin(request.Headers.Referer, out var referer))
        {
            return UriMatchesRequestOrigin(referer, request);
        }

        // Allow non-browser callers and same-origin requests where the browser omitted both headers.
        return true;
    }

    /// <summary>
    /// Returns the standard forbidden message used when same-origin validation fails.
    /// </summary>
    /// <returns>User-facing validation message.</returns>
    public static string GetForbiddenMessage()
        => ForbiddenMessage;

    /// <summary>
    /// Parses an Origin or Referer header into an absolute URI.
    /// </summary>
    /// <param name="headerValue">Header value to parse.</param>
    /// <param name="uri">Parsed absolute URI when successful.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    private static bool TryParseOrigin(string? headerValue, out Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(headerValue)
            && Uri.TryCreate(headerValue, UriKind.Absolute, out uri!))
        {
            return true;
        }

        uri = null!;
        return false;
    }

    /// <summary>
    /// Compares an absolute Origin/Referer URI to the active request's scheme, host, and port.
    /// </summary>
    /// <param name="origin">Origin or Referer URI.</param>
    /// <param name="request">Incoming HTTP request.</param>
    /// <returns><see langword="true"/> when the URI matches the request origin.</returns>
    private static bool UriMatchesRequestOrigin(Uri origin, HttpRequest request)
    {
        var requestHost = request.Host.Host ?? string.Empty;
        var requestPort = request.Host.Port
            ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        var originPort = origin.IsDefaultPort
            ? (string.Equals(origin.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
            : origin.Port;

        return string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Host, requestHost, StringComparison.OrdinalIgnoreCase)
            && originPort == requestPort;
    }
}
