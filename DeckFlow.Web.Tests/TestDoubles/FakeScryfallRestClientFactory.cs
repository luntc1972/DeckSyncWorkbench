using DeckFlow.Web.Services;
using RestSharp;

namespace DeckFlow.Web.Tests;

internal sealed class FakeScryfallRestClientFactory : IScryfallRestClientFactory
{
    private readonly HttpClient _httpClient;

    public FakeScryfallRestClientFactory(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (httpClient.BaseAddress is null)
            throw new ArgumentException(
                "FakeScryfallRestClientFactory: HttpClient.BaseAddress must be set before construction.",
                nameof(httpClient));
        _httpClient = httpClient;
    }

    public RestClient Create() => new RestClient(_httpClient);
}
