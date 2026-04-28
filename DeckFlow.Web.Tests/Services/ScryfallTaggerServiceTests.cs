using DeckFlow.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using RichardSzalay.MockHttp;
using System.Net;
using System.Net.Http;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class ScryfallTaggerServiceTests
{
    // Scryfall REST response for cards/named?exact=Thrasios, Triton Hero
    private const string ScryfallCardJson = """
{"object":"card","id":"abc123","name":"Thrasios, Triton Hero","set":"lea","collector_number":"161"}
""";

    // Tagger CSRF page HTML — must include Set-Cookie header AND meta csrf-token (LANDMINE 6)
    private const string TaggerCsrfHtml = """
<html><head><meta name="csrf-token" content="test-csrf-token"/></head><body></body></html>
""";

    // Tagger GraphQL response — type must be ORACLE_CARD_TAG (not ORACLE_CARD_THEME) per ScryfallTaggerParsers
    private const string TaggerGraphQlJson = """
{"data":{"card":{"taggings":[{"tag":{"name":"ramp","type":"ORACLE_CARD_TAG","slug":"ramp","weight":1,"status":"APPROVED"}}]}}}
""";

    private static ScryfallTaggerService CreateService(
        MockHttpMessageHandler scryfallMock,
        MockHttpMessageHandler taggerMock,
        ITaggerSessionCache? sessionCache = null)
    {
        var scryfallHttpClient = scryfallMock.ToHttpClient();
        scryfallHttpClient.BaseAddress = new Uri("https://api.scryfall.com/");
        var restClientFactory = new FakeScryfallRestClientFactory(scryfallHttpClient);

        var taggerHttpClient = taggerMock.ToHttpClient();
        taggerHttpClient.BaseAddress = new Uri("https://tagger.scryfall.com/");
        var typedTaggerClient = new ScryfallTaggerHttpClient(taggerHttpClient);

        var cache = sessionCache
            ?? new TaggerSessionCache(new MemoryCache(new MemoryCacheOptions()));

        return new ScryfallTaggerService(
            restClientFactory,
            typedTaggerClient,
            cache,
            new FakeResiliencePipelineProvider());
    }

    [Fact]
    public async Task LookupOracleTagsAsync_ColdFlow_ReturnsTagsFromGraphQL()
    {
        using var scryfallMock = new MockHttpMessageHandler();
        using var taggerMock = new MockHttpMessageHandler();

        var scryfallRoute = scryfallMock
            .When(HttpMethod.Get, "https://api.scryfall.com/cards/named*")
            .Respond(HttpStatusCode.OK, "application/json", ScryfallCardJson);

        var csrfRoute = taggerMock
            .When(HttpMethod.Get, "https://tagger.scryfall.com/card/lea/161")
            .Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Content = new StringContent(TaggerCsrfHtml, System.Text.Encoding.UTF8, "text/html");
                r.Headers.Add("Set-Cookie", "_ga=test-cookie; Path=/; HttpOnly");
                return r;
            });

        var graphqlRoute = taggerMock
            .When(HttpMethod.Post, "https://tagger.scryfall.com/graphql")
            .Respond(HttpStatusCode.OK, "application/json", TaggerGraphQlJson);

        var sut = CreateService(scryfallMock, taggerMock);

        var tags = await sut.LookupOracleTagsAsync("Thrasios, Triton Hero", CancellationToken.None);

        Assert.NotNull(tags);
        Assert.NotEmpty(tags);
        Assert.Contains("Ramp", tags);  // NormalizeTagName capitalizes first letter

        // All 3 legs fired exactly once
        Assert.Equal(1, scryfallMock.GetMatchCount(scryfallRoute));
        Assert.Equal(1, taggerMock.GetMatchCount(csrfRoute));
        Assert.Equal(1, taggerMock.GetMatchCount(graphqlRoute));
    }

    [Fact]
    public async Task LookupOracleTagsAsync_WarmCache_SkipsCsrfLeg_RefetchesRestAndGraphQL()
    {
        using var scryfallMock = new MockHttpMessageHandler();
        using var taggerMock = new MockHttpMessageHandler();

        var scryfallRoute = scryfallMock
            .When(HttpMethod.Get, "https://api.scryfall.com/cards/named*")
            .Respond(HttpStatusCode.OK, "application/json", ScryfallCardJson);

        var csrfRoute = taggerMock
            .When(HttpMethod.Get, "https://tagger.scryfall.com/card/lea/161")
            .Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Content = new StringContent(TaggerCsrfHtml, System.Text.Encoding.UTF8, "text/html");
                r.Headers.Add("Set-Cookie", "_ga=test-cookie; Path=/; HttpOnly");
                return r;
            });

        var graphqlRoute = taggerMock
            .When(HttpMethod.Post, "https://tagger.scryfall.com/graphql")
            .Respond(HttpStatusCode.OK, "application/json", TaggerGraphQlJson);

        var sut = CreateService(scryfallMock, taggerMock);

        // Cold call
        var first = await sut.LookupOracleTagsAsync("Thrasios, Triton Hero", CancellationToken.None);
        // Warm call — session cached, CSRF should NOT re-fire; REST+GraphQL re-fire per invocation
        var second = await sut.LookupOracleTagsAsync("Thrasios, Triton Hero", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);

        // Scryfall REST: called once (resolves set+number, which is per-call — not cached in service)
        // CSRF: called once (session cached after cold leg)
        // GraphQL: called twice (one per LookupOracleTagsAsync invocation)
        Assert.Equal(1, taggerMock.GetMatchCount(csrfRoute));
        Assert.Equal(2, taggerMock.GetMatchCount(graphqlRoute));
        // Scryfall REST resolves card on every call (no card-resolution cache in service)
        Assert.Equal(2, scryfallMock.GetMatchCount(scryfallRoute));
    }

    [Fact]
    public async Task LookupOracleTagsAsync_CsrfExpired_RefetchesSession()
    {
        using var scryfallMock = new MockHttpMessageHandler();
        using var taggerMock = new MockHttpMessageHandler();

        scryfallMock
            .When(HttpMethod.Get, "https://api.scryfall.com/cards/named*")
            .Respond(HttpStatusCode.OK, "application/json", ScryfallCardJson);

        var csrfRoute = taggerMock
            .When(HttpMethod.Get, "https://tagger.scryfall.com/card/lea/161")
            .Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Content = new StringContent(TaggerCsrfHtml, System.Text.Encoding.UTF8, "text/html");
                r.Headers.Add("Set-Cookie", "_ga=test-cookie; Path=/; HttpOnly");
                return r;
            });

        taggerMock
            .When(HttpMethod.Post, "https://tagger.scryfall.com/graphql")
            .Respond(HttpStatusCode.OK, "application/json", TaggerGraphQlJson);

        // Use a shared TaggerSessionCache so we can invalidate between calls
        var sessionCache = new TaggerSessionCache(new MemoryCache(new MemoryCacheOptions()));
        var sut = CreateService(scryfallMock, taggerMock, sessionCache);

        // First call — cold, populates session
        await sut.LookupOracleTagsAsync("Thrasios, Triton Hero", CancellationToken.None);

        // Simulate cache eviction (CSRF expired)
        sessionCache.Invalidate();

        // Second call — session gone, must re-fetch CSRF
        await sut.LookupOracleTagsAsync("Thrasios, Triton Hero", CancellationToken.None);

        // CSRF page must have been fetched twice
        Assert.Equal(2, taggerMock.GetMatchCount(csrfRoute));
    }

    [Fact]
    public async Task LookupOracleTagsAsync_GraphQlFails_ReturnsEmptyList()
    {
        using var scryfallMock = new MockHttpMessageHandler();
        using var taggerMock = new MockHttpMessageHandler();

        scryfallMock
            .When(HttpMethod.Get, "https://api.scryfall.com/cards/named*")
            .Respond(HttpStatusCode.OK, "application/json", ScryfallCardJson);

        taggerMock
            .When(HttpMethod.Get, "https://tagger.scryfall.com/card/lea/161")
            .Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Content = new StringContent(TaggerCsrfHtml, System.Text.Encoding.UTF8, "text/html");
                r.Headers.Add("Set-Cookie", "_ga=test-cookie; Path=/; HttpOnly");
                return r;
            });

        taggerMock
            .When(HttpMethod.Post, "https://tagger.scryfall.com/graphql")
            .Respond(HttpStatusCode.InternalServerError);

        var sut = CreateService(scryfallMock, taggerMock);

        var tags = await sut.LookupOracleTagsAsync("Thrasios, Triton Hero", CancellationToken.None);

        // Graceful degrade: returns empty list, no exception
        Assert.NotNull(tags);
        Assert.Empty(tags);
    }
}
