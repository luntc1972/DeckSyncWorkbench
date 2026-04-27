using System.Net.Http;

namespace DeckFlow.Web.Services;

/// <summary>Exposes the cookie-disabled <see cref="HttpClient"/> for tagger.scryfall.com.</summary>
public interface IScryfallTaggerHttpClient
{
    /// <summary>
    /// The underlying <see cref="HttpClient"/>, intended to be wrapped by RestSharp via
    /// <c>new RestClient(taggerHttpClient.Inner)</c> at the call site (D-02). The primary
    /// handler is a <c>SocketsHttpHandler</c> with cookies disabled and a 5-minute pooled
    /// connection lifetime per D-06. HandlerLifetime is also 5 minutes so the cookie+token+
    /// handler triple expires together (D-07 — see TaggerSessionCache for the 270s safety margin).
    /// </summary>
    HttpClient Inner { get; }
}

/// <summary>
/// Typed <see cref="HttpClient"/> wrapper for the Scryfall Tagger flow. Registered via
/// <c>builder.Services.AddHttpClient&lt;ScryfallTaggerHttpClient&gt;(...)</c> in Program.cs
/// with a primary <see cref="System.Net.Http.SocketsHttpHandler"/> configured per D-06.
/// </summary>
public sealed class ScryfallTaggerHttpClient : IScryfallTaggerHttpClient
{
    /// <summary>Creates a new typed client wrapping the supplied <paramref name="httpClient"/>.</summary>
    public ScryfallTaggerHttpClient(HttpClient httpClient)
    {
        Inner = httpClient;
    }

    /// <inheritdoc />
    public HttpClient Inner { get; }
}
