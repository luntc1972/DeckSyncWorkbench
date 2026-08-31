using System.Net;
using System.Net.Http;
using System.Text;
using DeckFlow.Web.Services.CreatorStyle;
using Polly;
using Polly.Registry;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests.Services.CreatorStyle;

/// <summary>
/// Tests for <see cref="ArchidektOwnerClient"/> and <see cref="ArchidektOwnerUrl"/>.
/// </summary>
public sealed class ArchidektOwnerClientTests
{
    [Theory]
    [InlineData("snail", "snail")]
    [InlineData("Snail_123", "Snail_123")]
    [InlineData("https://archidekt.com/u/snail/", "snail")]
    [InlineData("https://ARCHIDEKT.COM/u/snail/", "snail")]
    [InlineData("https://sub.archidekt.com/u/snail/", "snail")]
    public void TryGetUsername_AcceptsExpectedInputs(string input, string expectedUsername)
    {
        var ok = ArchidektOwnerUrl.TryGetUsername(input, out var username);

        Assert.True(ok);
        Assert.Equal(expectedUsername, username);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://archidekt.com.evil.test/u/snail/")]
    [InlineData("https://archidekt.com@evil.test/u/snail/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://archidekt.com/u/snail/")]
    [InlineData("https://archidekt.com/u/")]
    [InlineData("bad/slash")]
    public void TryGetUsername_RejectsUnsafeOrInvalidInputs(string input)
    {
        var ok = ArchidektOwnerUrl.TryGetUsername(input, out var username);

        Assert.False(ok);
        Assert.Equal(string.Empty, username);
    }

    [Fact]
    public async Task ResolveUsernameAsync_ParsesResolvedUsername()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(JsonResponse("""
            {
              "results": [
                {
                  "username": "snail"
                }
              ]
            }
            """));

        var sut = CreateClient(stub);

        var username = await sut.ResolveUsernameAsync("snail");

        Assert.Equal("snail", username);
        Assert.Single(stub.RecordedRequests);
        Assert.Equal("/api/users/", stub.RecordedRequests[0].RequestUri?.AbsolutePath);
        Assert.Equal("username=snail", stub.RecordedRequests[0].RequestUri?.Query.TrimStart('?'));
    }

    [Fact]
    public async Task ResolveUsernameAsync_NonMatchingFirstSearchResult_ReturnsNull()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(JsonResponse("""
            {
              "results": [
                {
                  "username": "snailfish"
                }
              ]
            }
            """));

        var sut = CreateClient(stub);

        var username = await sut.ResolveUsernameAsync("snail");

        Assert.Null(username);
    }

    [Fact]
    public async Task ResolveUsernameAsync_UsesArchidektNamedHttpClientAndPipeline()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(JsonResponse("""{ "results": [] }"""));
        var httpClient = new HttpClient(stub, disposeHandler: false)
        {
            BaseAddress = new Uri("https://archidekt.com/")
        };
        var httpClientFactory = new RecordingHttpClientFactory(httpClient);
        var pipelineProvider = new RecordingPipelineProvider();
        var sut = new ArchidektOwnerClient(httpClientFactory, pipelineProvider);

        await sut.ResolveUsernameAsync("snail");

        Assert.Equal("archidekt-owner", httpClientFactory.LastClientName);
        Assert.Equal("archidekt", pipelineProvider.LastPipelineName);
        Assert.Single(stub.RecordedRequests);
        Assert.Equal("https://archidekt.com/api/users/?username=snail", stub.RecordedRequests[0].RequestUri?.ToString());
    }

    [Fact]
    public async Task ResolveUsernameAsync_BuildsUsernameQueryParameter()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(JsonResponse("""{ "results": [] }"""));

        var sut = CreateClient(stub);

        await sut.ResolveUsernameAsync("Snail_123");

        Assert.Equal("username=Snail_123", stub.RecordedRequests[0].RequestUri?.Query.TrimStart('?'));
    }

    [Fact]
    public async Task ResolveUsernameAsync_ReturnsNullOnMalformedJson()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(JsonResponse("{"));

        var sut = CreateClient(stub);

        var username = await sut.ResolveUsernameAsync("snail");

        Assert.Null(username);
    }

    [Fact]
    public async Task ResolveUsernameAsync_ReturnsNullOnOversizedPayload()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(JsonResponse(CreateOversizedJsonPayload()));

        var sut = CreateClient(stub);

        var username = await sut.ResolveUsernameAsync("snail");

        Assert.Null(username);
    }

    [Fact]
    public async Task ListDeckSummariesAsync_FollowsPaginationAndTruncatesAtMaxDecks()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(JsonResponse(CreateDeckPageJson(page: 1, hasNext: true, startId: 1, count: 50)));
        stub.Enqueue(JsonResponse(CreateDeckPageJson(page: 2, hasNext: true, startId: 51, count: 50)));
        stub.Enqueue(JsonResponse(CreateDeckPageJson(page: 3, hasNext: true, startId: 101, count: 50)));
        stub.Enqueue(JsonResponse(CreateDeckPageJson(page: 4, hasNext: true, startId: 151, count: 50)));
        stub.Enqueue(JsonResponse(CreateDeckPageJson(page: 5, hasNext: true, startId: 201, count: 50)));
        stub.Enqueue(JsonResponse(CreateDeckPageJson(page: 6, hasNext: true, startId: 251, count: 50)));
        stub.Enqueue(JsonResponse(CreateDeckPageJson(page: 7, hasNext: true, startId: 301, count: 50)));
        stub.Enqueue(JsonResponse(CreateDeckPageJson(page: 8, hasNext: true, startId: 351, count: 50)));
        stub.Enqueue(JsonResponse(CreateDeckPageJson(page: 9, hasNext: true, startId: 401, count: 50)));
        stub.Enqueue(JsonResponse(CreateDeckPageJson(page: 10, hasNext: true, startId: 451, count: 50)));

        var sut = CreateClient(stub);

        var decks = await sut.ListDeckSummariesAsync("snail");

        Assert.False(decks.HasUpstreamFailure);
        Assert.Equal(500, decks.Decks.Count);
        Assert.Equal("1", decks.Decks[0].Id);
        Assert.Equal("500", decks.Decks[^1].Id);
        Assert.Null(decks.Decks[0].ParentFolderName);
        Assert.Equal(10, stub.CallCount);
        Assert.All(stub.RecordedRequests, request => Assert.Equal("/api/decks/v3/", request.RequestUri?.AbsolutePath));
        Assert.Contains("ownerUsername=snail", stub.RecordedRequests[0].RequestUri?.Query);
        Assert.Contains("pageSize=50", stub.RecordedRequests[0].RequestUri?.Query);
        Assert.Contains("page=1", stub.RecordedRequests[0].RequestUri?.Query);
        Assert.Contains("page=10", stub.RecordedRequests[^1].RequestUri?.Query);
    }

    [Fact]
    public async Task ListDeckSummariesAsync_ReturnsEmptyOnMalformedJson()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(JsonResponse("{"));

        var sut = CreateClient(stub);

        var decks = await sut.ListDeckSummariesAsync("snail");

        Assert.True(decks.HasUpstreamFailure);
        Assert.Empty(decks.Decks);
    }

    [Fact]
    public async Task ListDeckSummariesAsync_ReturnsEmptyOnOversizedPayload()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(JsonResponse(CreateOversizedJsonPayload()));

        var sut = CreateClient(stub);

        var decks = await sut.ListDeckSummariesAsync("snail");

        Assert.True(decks.HasUpstreamFailure);
        Assert.Empty(decks.Decks);
    }

    private static ArchidektOwnerClient CreateClient(StubHttpMessageHandler stub)
    {
        var httpClient = new HttpClient(stub, disposeHandler: false)
        {
            BaseAddress = new Uri("https://archidekt.com/")
        };
        var restClient = new RestClient(httpClient);
        return new ArchidektOwnerClient(new FakeResiliencePipelineProvider(), restClient);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string CreateOversizedJsonPayload()
    {
        return "{\"results\":\"" + new string('a', 5_500_000) + "\"}";
    }

    private static string CreateDeckPageJson(int page, bool hasNext, int startId, int count)
    {
        var builder = new StringBuilder();
        builder.Append("{\"count\":999,\"next\":");
        if (hasNext)
        {
            builder.Append('"');
            builder.Append("https://archidekt.com/api/decks/v3/?page=");
            builder.Append(page + 1);
            builder.Append('"');
        }
        else
        {
            builder.Append("null");
        }

        builder.Append(",\"results\":[");
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            var deckId = startId + index;
            builder.Append("{\"id\":");
            builder.Append(deckId);
            builder.Append(",\"name\":\"Deck ");
            builder.Append(deckId);
            builder.Append("\",\"size\":100,\"parentFolderId\":");
            builder.Append(deckId % 2 == 0 ? "42" : "null");
            builder.Append(",\"parentFolderName\":");
            builder.Append(deckId == 1 ? "null" : "\"Folder\"");
            builder.Append('}');
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public RecordingHttpClientFactory(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public string? LastClientName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastClientName = name;
            return _httpClient;
        }
    }

    private sealed class RecordingPipelineProvider : ResiliencePipelineProvider<string>
    {
        public string? LastPipelineName { get; private set; }

        public override ResiliencePipeline<T> GetPipeline<T>(string key)
        {
            LastPipelineName = key;
            return ResiliencePipeline<T>.Empty;
        }

        public override bool TryGetPipeline<T>(string key, out ResiliencePipeline<T> pipeline)
        {
            LastPipelineName = key;
            pipeline = ResiliencePipeline<T>.Empty;
            return true;
        }

        public override bool TryGetPipeline(string key, out ResiliencePipeline pipeline)
        {
            LastPipelineName = key;
            pipeline = ResiliencePipeline.Empty;
            return true;
        }
    }
}
