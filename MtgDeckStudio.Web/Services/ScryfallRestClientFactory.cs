using RestSharp;

namespace MtgDeckStudio.Web.Services;

/// <summary>
/// Builds RestSharp clients configured for Scryfall's published access requirements.
/// </summary>
public static class ScryfallRestClientFactory
{
    private const string UserAgent = "MtgDeckStudio/1.0 (+https://github.com/luntc1972/MtgDeckStudio)";
    private const string AcceptHeader = "application/json;q=0.9,*/*;q=0.8";

    /// <summary>
    /// Creates a RestSharp client for Scryfall with the expected headers.
    /// </summary>
    public static RestClient Create()
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
