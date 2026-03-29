using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DeckSyncWorkbench.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;
using Xunit;

namespace DeckSyncWorkbench.Web.Tests;

/// <summary>
/// Contains unit tests for commander look-up behavior.
/// </summary>
public sealed class ScryfallCommanderSearchServiceTests
{
    private static readonly IReadOnlyList<ScryfallCard> SampleCards = new[]
    {
        BasicCard("Bello, Bard of the Brambles"),
        BasicCard("Bello, Bard of the Brambles"),
        BasicCard("Bellowjohn")
    };

    private static ScryfallCard BasicCard(string name)
        => new(name, "{1}", "Legendary Creature", "Some text", "2", "2", null, null, null, null);

    [Fact]
    /// <summary>
    /// Ensures the service extracts distinct names and builds the correct query.
    /// </summary>
    public async Task SearchAsync_ReturnsDistinctNamesFromResponse()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var callCount = 0;
        RestRequest? lastRequest = null;
        var service = new ScryfallCommanderSearchService(
            cache,
            executeAsync: (request, _) =>
            {
                callCount++;
                lastRequest = request;
                return Task.FromResult(CreateResponse(SampleCards, request));
            });

        var result = await service.SearchAsync("bel");

        Assert.Equal(new[] { "Bello, Bard of the Brambles", "Bellowjohn" }, result);
        Assert.Equal(1, callCount);
        Assert.Equal("is:commander type:legendary (type:creature or type:vehicle) name:bel", lastRequest?.Parameters.First(p => p.Name == "q").Value);
    }

    [Fact]
    /// <summary>
    /// Verifies the service caches normalized queries so duplicate requests do not call Scryfall twice.
    /// </summary>
    public async Task SearchAsync_UsesCacheOnSubsequentCalls()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var callCount = 0;
        var service = new ScryfallCommanderSearchService(
            cache,
            executeAsync: (request, _) =>
            {
                callCount++;
                return Task.FromResult(CreateResponse(SampleCards, request));
            });

        await service.SearchAsync("bel");
        await service.SearchAsync("  bel  ");

        Assert.Equal(1, callCount);
    }

    [Fact]
    /// <summary>
    /// Throws an HTTP exception when the Scryfall response is not successful.
    /// </summary>
    public async Task SearchAsync_ThrowsWhenResponseFails()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ScryfallCommanderSearchService(
            cache,
            executeAsync: (request, _) =>
            {
                return Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
                {
                    StatusCode = HttpStatusCode.ServiceUnavailable,
                    ResponseStatus = ResponseStatus.Error
                });
            });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("bel"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Contains("Scryfall", exception.Message);
    }

    /// <summary>
    /// Builds a successful REST response containing the provided cards.
    /// </summary>
    private static RestResponse<ScryfallSearchResponse> CreateResponse(IReadOnlyList<ScryfallCard> cards, RestRequest request)
    {
        return new RestResponse<ScryfallSearchResponse>(request)
        {
            StatusCode = HttpStatusCode.OK,
            ResponseStatus = ResponseStatus.Completed,
            Data = new ScryfallSearchResponse(cards.ToList())
        };
    }
}
