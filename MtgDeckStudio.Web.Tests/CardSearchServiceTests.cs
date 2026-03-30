using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MtgDeckStudio.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class CardSearchServiceTests
{
    private static readonly IReadOnlyList<ScryfallCard> SampleCards = new[]
    {
        BasicCard("Guardian Project"),
        BasicCard("Guardian Project"),
        BasicCard("Guard Gomazoa")
    };

    private static ScryfallCard BasicCard(string name)
        => new(name, string.Empty, "Creature", name == "Guard Gomazoa" ? "Sample text" : "Sample text", "1", "1", null, null, null, null);

    [Fact]
    public async Task SearchAsync_ReturnsDistinctNamesFromResponse()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var callCount = 0;
        RestRequest? lastRequest = null;
        var service = new ScryfallCardSearchService(
            cache,
            executeAsync: (request, _) =>
            {
                callCount++;
                lastRequest = request;
                return Task.FromResult(CreateResponse(SampleCards, request));
            });

        var result = await service.SearchAsync("guard");

        Assert.Equal(new[] { "Guardian Project", "Guard Gomazoa" }, result);
        Assert.Equal(1, callCount);
        Assert.Equal("name:guard", lastRequest?.Parameters.First(p => p.Name == "q").Value);
    }

    [Fact]
    public async Task SearchAsync_UsesCacheOnSubsequentCalls()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var callCount = 0;
        var service = new ScryfallCardSearchService(
            cache,
            executeAsync: (request, _) =>
            {
                callCount++;
                return Task.FromResult(CreateResponse(SampleCards, request));
            });

        await service.SearchAsync("guard");
        await service.SearchAsync("  guard  ");

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task SearchAsync_ThrowsWhenResponseFails()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ScryfallCardSearchService(
            cache,
            executeAsync: (request, _) =>
            {
                return Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
                {
                    StatusCode = HttpStatusCode.ServiceUnavailable,
                    ResponseStatus = ResponseStatus.Error
                });
            });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("guard"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Contains("Scryfall", exception.Message);
    }

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
