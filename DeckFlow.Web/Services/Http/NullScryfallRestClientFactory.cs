using System;
using RestSharp;
using DeckFlow.Web.Services;

namespace DeckFlow.Web.Services.Http;

/// <summary>
/// Test-only <see cref="IScryfallRestClientFactory"/> that returns a fresh
/// <see cref="RestClient"/> configured with Scryfall's published BaseUrl, UserAgent,
/// and Accept header — the same shape the production
/// <see cref="ScryfallRestClientFactory"/> used to produce via its static
/// <c>Create()</c> method before Phase 1 retired that path. Used by the public
/// test-compat convenience ctor overloads on the eight Scryfall consumer
/// services (D-10) so default Func delegates that fall through to the underlying
/// RestClient still target the real Scryfall API at request time, matching
/// pre-migration test behaviour.
/// </summary>
public sealed class NullScryfallRestClientFactory : IScryfallRestClientFactory
{
    private const string UserAgent = "DeckFlow/1.0 (+https://github.com/luntc1972/DeckFlow)";
    private const string AcceptHeader = "application/json;q=0.9,*/*;q=0.8";

    /// <summary>Singleton instance suitable for use as a test default.</summary>
    public static readonly NullScryfallRestClientFactory Instance = new();

    /// <inheritdoc />
    public RestClient Create()
    {
        var client = new RestClient(new RestClientOptions
        {
            BaseUrl = new Uri("https://api.scryfall.com"),
            ThrowOnAnyError = false,
        });
        client.AddDefaultHeader("User-Agent", UserAgent);
        client.AddDefaultHeader("Accept", AcceptHeader);
        return client;
    }
}
