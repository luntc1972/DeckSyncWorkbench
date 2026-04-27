using System.Net.Http;

namespace DeckFlow.Web.Services.Http;

/// <summary>
/// Test-only <see cref="IHttpClientFactory"/> that returns a fresh <see cref="HttpClient"/> per
/// call. Consumed by the public test-compat convenience ctor overloads on migrated services
/// (D-10). Production paths always supply the real <see cref="IHttpClientFactory"/> from DI.
/// </summary>
public sealed class NullHttpClientFactory : IHttpClientFactory
{
    /// <summary>Singleton instance suitable for use as a test default.</summary>
    public static readonly NullHttpClientFactory Instance = new();

    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new HttpClient();
}
