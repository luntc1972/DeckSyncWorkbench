using DeckFlow.Web.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Verifies same-origin validation for browser-originated JSON POST endpoints.
/// </summary>
public sealed class SameOriginRequestValidatorTests
{
    /// <summary>
    /// Accepts requests when the Origin header matches the request origin.
    /// </summary>
    [Fact]
    public void IsValid_ReturnsTrue_WhenOriginMatchesRequest()
    {
        var request = BuildRequest("https", "deckflow.test", null, "https://deckflow.test");

        Assert.True(SameOriginRequestValidator.IsValid(request));
    }

    /// <summary>
    /// Accepts requests when the Referer header matches the request origin and Origin is absent.
    /// </summary>
    [Fact]
    public void IsValid_ReturnsTrue_WhenRefererMatchesRequest()
    {
        var request = BuildRequest("https", "deckflow.test", null, null, "https://deckflow.test/tools");

        Assert.True(SameOriginRequestValidator.IsValid(request));
    }

    /// <summary>
    /// Rejects requests when the Origin header points at another host.
    /// </summary>
    [Fact]
    public void IsValid_ReturnsFalse_WhenOriginIsCrossSite()
    {
        var request = BuildRequest("https", "deckflow.test", null, "https://evil.test");

        Assert.False(SameOriginRequestValidator.IsValid(request));
    }

    /// <summary>
    /// Rejects requests when the Referer header points at another host.
    /// </summary>
    [Fact]
    public void IsValid_ReturnsFalse_WhenRefererIsCrossSite()
    {
        var request = BuildRequest("https", "deckflow.test", null, null, "https://evil.test/path");

        Assert.False(SameOriginRequestValidator.IsValid(request));
    }

    /// <summary>
    /// Allows requests that do not include browser origin metadata.
    /// </summary>
    [Fact]
    public void IsValid_ReturnsTrue_WhenOriginHeadersAreMissing()
    {
        var request = BuildRequest("https", "deckflow.test", null, null, null);

        Assert.True(SameOriginRequestValidator.IsValid(request));
    }

    /// <summary>
    /// Treats implicit default ports as matching the request origin.
    /// </summary>
    [Fact]
    public void IsValid_ReturnsTrue_WhenOriginUsesImplicitDefaultPort()
    {
        var request = BuildRequest("https", "deckflow.test", 443, "https://deckflow.test");

        Assert.True(SameOriginRequestValidator.IsValid(request));
    }

    /// <summary>
    /// Builds an HTTP request with the supplied origin metadata for testing.
    /// </summary>
    /// <param name="scheme">Request scheme.</param>
    /// <param name="host">Request host.</param>
    /// <param name="port">Optional request port.</param>
    /// <param name="origin">Optional Origin header value.</param>
    /// <param name="referer">Optional Referer header value.</param>
    /// <returns>Configured HTTP request.</returns>
    private static HttpRequest BuildRequest(string scheme, string host, int? port, string? origin, string? referer = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = port.HasValue ? new HostString(host, port.Value) : new HostString(host);
        if (!string.IsNullOrWhiteSpace(origin))
        {
            context.Request.Headers.Origin = origin;
        }

        if (!string.IsNullOrWhiteSpace(referer))
        {
            context.Request.Headers.Referer = referer;
        }

        return context.Request;
    }
}
