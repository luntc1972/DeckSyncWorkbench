using Microsoft.AspNetCore.Builder;

namespace DeckFlow.Web.Infrastructure;

/// <summary>
/// Adds the response security headers used by the DeckFlow web application.
/// </summary>
public static class SecurityHeadersApplicationBuilderExtensions
{
    private const string ContentSecurityPolicyValue =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    /// <summary>
    /// Applies the standard security headers to each response.
    /// </summary>
    /// <param name="app">Application builder to configure.</param>
    /// <returns>The same application builder so middleware registration can continue.</returns>
    public static IApplicationBuilder UseDeckFlowSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

                var path = context.Request.Path.Value ?? string.Empty;
                if (!path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
                {
                    headers.ContentSecurityPolicy = ContentSecurityPolicyValue;
                }

                return Task.CompletedTask;
            });

            await next();
        });
    }
}
