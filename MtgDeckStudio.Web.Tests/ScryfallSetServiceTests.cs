using MtgDeckStudio.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class ScryfallSetServiceTests
{
    [Fact]
    public async Task GetSetsAsync_ReturnsSetsOrderedByReleaseDateDescending()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ScryfallSetService(
            cache,
            new FakeMechanicLookupService(),
            executeSetListAsync: (_, _) => Task.FromResult(
                new RestResponse<ScryfallSetListResponse>(new RestRequest("sets"))
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Data = new ScryfallSetListResponse(
                    [
                        new ScryfallSet("old", "Old Set", "2024-01-01", "expansion", 250),
                        new ScryfallSet("new", "New Set", "2025-01-01", "expansion", 275),
                        new ScryfallSet("mid", "Mid Set", "2024-06-01", "expansion", 260)
                    ])
                }));

        var sets = await service.GetSetsAsync();

        Assert.Collection(
            sets,
            set => Assert.Equal("new", set.Code),
            set => Assert.Equal("mid", set.Code),
            set => Assert.Equal("old", set.Code));
    }

    private sealed class FakeMechanicLookupService : IMechanicLookupService
    {
        public Task<MechanicLookupResult> LookupAsync(string mechanicName, CancellationToken cancellationToken = default)
            => Task.FromResult(new MechanicLookupResult(mechanicName, false, null, null, null, null, null, "https://magic.wizards.com/en/rules", null));
    }
}
