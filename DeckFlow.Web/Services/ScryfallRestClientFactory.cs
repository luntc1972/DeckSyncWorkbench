using System;
using System.Net.Http;
using RestSharp;

namespace DeckFlow.Web.Services;

/// <summary>
/// Creates RestSharp <see cref="RestClient"/> instances configured for the public Scryfall REST API.
/// Interface defined in Task 4 for green-at-each-step compilation; this file took its full
/// IHttpClientFactory-backed implementation in Task 10c after Tasks 7-10b migrated every caller
/// off the previous static <c>Create()</c> shim.
/// </summary>
public interface IScryfallRestClientFactory
{
    /// <summary>Returns a RestClient wrapping a factory-sourced HttpClient. Cheap to call repeatedly.</summary>
    RestClient Create();
}

/// <summary>
/// DI-friendly Scryfall REST client factory backed by <see cref="IHttpClientFactory"/>.
/// BaseAddress + UserAgent + Accept headers come from the named "scryfall-rest" registration
/// in Program.cs (D-01). Recreating the RestClient per call is the canonical pattern when
/// IHttpClientFactory owns the underlying handler lifecycle — RestClient is a lightweight
/// wrapper around HttpClient and the factory ensures handler rotation every 5 minutes.
/// </summary>
public sealed class ScryfallRestClientFactory : IScryfallRestClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Creates a new factory bound to the supplied <see cref="IHttpClientFactory"/>.</summary>
    public ScryfallRestClientFactory(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public RestClient Create()
    {
        var httpClient = _httpClientFactory.CreateClient("scryfall-rest");
        return new RestClient(httpClient);
    }
}
