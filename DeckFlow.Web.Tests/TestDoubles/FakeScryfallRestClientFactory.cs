using DeckFlow.Web.Services;
using RestSharp;

namespace DeckFlow.Web.Tests;

internal sealed class FakeScryfallRestClientFactory : IScryfallRestClientFactory
{
    private readonly HttpClient _httpClient;

    public FakeScryfallRestClientFactory(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public RestClient Create() => new RestClient(_httpClient);
}
