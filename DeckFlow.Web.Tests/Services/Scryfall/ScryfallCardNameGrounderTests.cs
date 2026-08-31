using System.Net;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class ScryfallCardNameGrounderTests
{
    [Fact]
    public async Task TryGroundAsync_TypoRewrite_ReturnsCanonicalMatch()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver(_ => Task.FromResult<ScryfallCard?>(Card("Dockside Extortionist")));
        var sut = new ScryfallCardNameGrounder(resolver, cache);

        var result = await sut.TryGroundAsync("Dockside Extortonist", CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal("Dockside Extortionist", result.CanonicalName);
    }

    [Fact]
    public async Task TryGroundAsync_ResolverReturnsNull_KeepsOriginalName()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver(_ => Task.FromResult<ScryfallCard?>(null));
        var sut = new ScryfallCardNameGrounder(resolver, cache);

        var result = await sut.TryGroundAsync("Made Up Card", CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal("Made Up Card", result.CanonicalName);
    }

    [Fact]
    public async Task TryGroundAsync_ResolverThrows_DegradesToUnresolved()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver(_ => throw new HttpRequestException("upstream"));
        var sut = new ScryfallCardNameGrounder(resolver, cache);

        var result = await sut.TryGroundAsync("Explosive Vegetaion", CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal("Explosive Vegetaion", result.CanonicalName);
    }

    [Fact]
    public async Task TryGroundAsync_ResolverThrows_DoesNotCacheFailure()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver(_ => throw new HttpRequestException("upstream"));
        var sut = new ScryfallCardNameGrounder(resolver, cache);

        await sut.TryGroundAsync("Explosive Vegetaion", CancellationToken.None);
        await sut.TryGroundAsync("Explosive Vegetaion", CancellationToken.None);

        Assert.Equal(2, resolver.SearchPrintingFallbackCallCount);
    }

    [Fact]
    public async Task TryGroundAsync_SameNormalizedName_HitsResolverOnce()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver(_ => Task.FromResult<ScryfallCard?>(Card("Dockside Extortionist")));
        var sut = new ScryfallCardNameGrounder(resolver, cache);

        var first = await sut.TryGroundAsync("  Dockside Extortonist  ", CancellationToken.None);
        var second = await sut.TryGroundAsync("dockside extortonist", CancellationToken.None);

        Assert.True(first.Resolved);
        Assert.True(second.Resolved);
        Assert.Equal("Dockside Extortionist", second.CanonicalName);
        Assert.Equal(1, resolver.SearchPrintingFallbackCallCount);
    }

    private static ScryfallCard Card(string name)
        => new(
            Name: name,
            ManaCost: "{1}",
            TypeLine: "Creature",
            OracleText: null,
            Power: null,
            Toughness: null,
            Keywords: null,
            ColorIdentity: null,
            SetCode: null,
            SetName: null,
            CollectorNumber: null,
            CardFaces: null,
            Id: null,
            Layout: "normal",
            Cmc: 2,
            ProducedMana: null,
            Rarity: "rare");

    private sealed class FakeResolver(Func<string, Task<ScryfallCard?>> searchPrintingFallbackAsync) : IScryfallCardResolver
    {
        public int SearchPrintingFallbackCallCount { get; private set; }

        public Task<RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.NotImplemented,
                Data = null,
            });

        public Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken)
        {
            SearchPrintingFallbackCallCount++;
            return searchPrintingFallbackAsync(cardName);
        }
    }
}
