using System.Net;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class CardGroundingGuardTests
{
    [Fact]
    public async Task TryValidateAsync_BlankCandidate_ReturnsNotFoundWithoutResolverCall()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver();
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync("  ", CreateContext(), CancellationToken.None);

        Assert.False(verdict.Accepted);
        Assert.Equal("  ", verdict.CanonicalName);
        Assert.Equal(CardGroundingRejectReason.NotFound, verdict.RejectReason);
        Assert.Equal(0, resolver.ExecuteCollectionCallCount);
        Assert.Equal(0, resolver.ExecuteNamedFuzzyCallCount);
    }

    [Fact]
    public async Task TryValidateAsync_ExactCollectionHit_ReturnsAcceptedCanonicalName()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(Card("Sol Ring", legalities: Legalities("legal")))),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync("  sol ring  ", CreateContext(), CancellationToken.None);

        Assert.True(verdict.Accepted);
        Assert.Equal("Sol Ring", verdict.CanonicalName);
        Assert.Equal(CardGroundingRejectReason.None, verdict.RejectReason);
        Assert.Equal(1, resolver.ExecuteCollectionCallCount);
        Assert.Equal(0, resolver.ExecuteNamedFuzzyCallCount);
    }

    [Fact]
    public async Task TryValidateAsync_Fuzzy404WithoutAmbiguousType_ReturnsNotFound()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse()),
            ExecuteNamedFuzzyAsyncImpl = _ => Task.FromResult(NamedResponse(
                HttpStatusCode.NotFound,
                content: """{"object":"error","code":"not_found","status":404,"details":"No cards found."}""")),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync("Made Up Card", CreateContext(), CancellationToken.None);

        Assert.False(verdict.Accepted);
        Assert.Equal("Made Up Card", verdict.CanonicalName);
        Assert.Equal(CardGroundingRejectReason.NotFound, verdict.RejectReason);
    }

    [Fact]
    public async Task TryValidateAsync_Fuzzy404WithAmbiguousType_ReturnsAmbiguous()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse()),
            ExecuteNamedFuzzyAsyncImpl = _ => Task.FromResult(NamedResponse(
                HttpStatusCode.NotFound,
                content: """{"object":"error","code":"not_found","type":"ambiguous","status":404,"details":"Too many matches."}""")),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync("Nicol", CreateContext(), CancellationToken.None);

        Assert.False(verdict.Accepted);
        Assert.Equal("Nicol", verdict.CanonicalName);
        Assert.Equal(CardGroundingRejectReason.Ambiguous, verdict.RejectReason);
    }

    [Fact]
    public async Task TryValidateAsync_MissingCommanderLegality_FailsClosedAsNotLegal()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(Card("Dockside Extortionist", legalities: null))),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync("Dockside Extortionist", CreateContext(), CancellationToken.None);

        Assert.False(verdict.Accepted);
        Assert.Equal("Dockside Extortionist", verdict.CanonicalName);
        Assert.Equal(CardGroundingRejectReason.NotLegal, verdict.RejectReason);
    }

    [Fact]
    public async Task TryValidateAsync_OffIdentityCard_ReturnsIdentityViolation()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(Card(
                "Counterspell",
                manaCost: "{U}{U}",
                colorIdentity: ["U"],
                legalities: Legalities("legal")))),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync(
            "Counterspell",
            CreateContext(commanderIdentity: SetOf("G"), producedColors: CharSetOf('G', 'U')),
            CancellationToken.None);

        Assert.False(verdict.Accepted);
        Assert.Equal(CardGroundingRejectReason.IdentityViolation, verdict.RejectReason);
    }

    [Fact]
    public async Task TryValidateAsync_DuplicateNonBasic_ReturnsSingletonViolation()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(Card(
                "Sol Ring",
                typeLine: "Artifact",
                legalities: Legalities("legal")))),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync(
            "Sol Ring",
            CreateContext(deckCardNames: SetOf(CardNormalizer.Normalize("Sol Ring"))),
            CancellationToken.None);

        Assert.False(verdict.Accepted);
        Assert.Equal(CardGroundingRejectReason.SingletonDuplicate, verdict.RejectReason);
    }

    [Fact]
    public async Task TryValidateAsync_DuplicateBasicLand_IsAccepted()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(Card(
                "Snow-Covered Forest",
                typeLine: "Basic Land - Forest",
                legalities: Legalities("legal")))),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync(
            "Snow-Covered Forest",
            CreateContext(deckCardNames: SetOf(CardNormalizer.Normalize("Snow-Covered Forest"))),
            CancellationToken.None);

        Assert.True(verdict.Accepted);
        Assert.Equal(CardGroundingRejectReason.None, verdict.RejectReason);
    }

    [Fact]
    public async Task TryValidateAsync_UncastableCard_ReturnsUncastable()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(Card(
                "Counterspell",
                manaCost: "{U}{U}",
                colorIdentity: ["U"],
                legalities: Legalities("legal")))),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync(
            "Counterspell",
            CreateContext(commanderIdentity: SetOf("U"), producedColors: CharSetOf('G')),
            CancellationToken.None);

        Assert.False(verdict.Accepted);
        Assert.Equal(CardGroundingRejectReason.Uncastable, verdict.RejectReason);
    }

    [Fact]
    public async Task TryValidateAsync_ResolverThrows_ReturnsUpstreamUnavailableAndDoesNotCacheFailure()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var attempts = 0;
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ =>
            {
                attempts++;
                throw new HttpRequestException("upstream");
            },
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var first = await sut.TryValidateAsync("Sol Ring", CreateContext(), CancellationToken.None);
        var second = await sut.TryValidateAsync("Sol Ring", CreateContext(), CancellationToken.None);

        Assert.False(first.Accepted);
        Assert.Equal(CardGroundingRejectReason.UpstreamUnavailable, first.RejectReason);
        Assert.False(second.Accepted);
        Assert.Equal(CardGroundingRejectReason.UpstreamUnavailable, second.RejectReason);
        Assert.Equal(2, attempts);
        Assert.Equal(2, resolver.ExecuteCollectionCallCount);
    }

    [Fact]
    public async Task TryValidateAsync_SameNormalizedName_UsesCachedResolutionButReevaluatesDeckRules()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(Card(
                "Counterspell",
                manaCost: "{U}{U}",
                colorIdentity: ["U"],
                legalities: Legalities("legal")))),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var accepted = await sut.TryValidateAsync(
            "  Counterspell  ",
            CreateContext(commanderIdentity: SetOf("U"), producedColors: CharSetOf('U')),
            CancellationToken.None);
        var rejected = await sut.TryValidateAsync(
            "counterspell",
            CreateContext(commanderIdentity: SetOf("G"), producedColors: CharSetOf('U')),
            CancellationToken.None);

        Assert.True(accepted.Accepted);
        Assert.False(rejected.Accepted);
        Assert.Equal(CardGroundingRejectReason.IdentityViolation, rejected.RejectReason);
        Assert.Equal(1, resolver.ExecuteCollectionCallCount);
    }

    [Fact]
    public async Task TryValidateAsync_ExactCollection429_ReturnsUpstreamUnavailableWithoutFuzzyFallback()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(statusCode: HttpStatusCode.TooManyRequests)),
            ExecuteNamedFuzzyAsyncImpl = _ => Task.FromResult(NamedResponse(HttpStatusCode.OK, Card("Sol Ring", legalities: Legalities("legal")))),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var first = await sut.TryValidateAsync("Sol Ring", CreateContext(), CancellationToken.None);
        var second = await sut.TryValidateAsync("Sol Ring", CreateContext(), CancellationToken.None);

        Assert.False(first.Accepted);
        Assert.Equal(CardGroundingRejectReason.UpstreamUnavailable, first.RejectReason);
        Assert.False(second.Accepted);
        Assert.Equal(CardGroundingRejectReason.UpstreamUnavailable, second.RejectReason);
        Assert.Equal(2, resolver.ExecuteCollectionCallCount);
        Assert.Equal(0, resolver.ExecuteNamedFuzzyCallCount);
    }

    [Fact]
    public async Task ValidateAllAsync_ReturnsOrderedVerdictsAndUpstreamAggregateFlag()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(
                HttpStatusCode.OK,
                cards:
                [
                    Card("Sol Ring", legalities: Legalities("legal")),
                ],
                notFound:
                [
                    new ScryfallCollectionIdentifier("Made Up Card"),
                    new ScryfallCollectionIdentifier("Counterspell"),
                ])),
            ExecuteNamedFuzzyAsyncImpl = cardName => cardName == "Counterspell"
                ? throw new HttpRequestException("upstream")
                : Task.FromResult(NamedResponse(
                    HttpStatusCode.NotFound,
                    content: """{"object":"error","code":"not_found","status":404,"details":"No cards found."}""")),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var result = await sut.ValidateAllAsync(
            ["Sol Ring", "Made Up Card", "Counterspell"],
            CreateContext(),
            CancellationToken.None);

        Assert.Equal(3, result.Verdicts.Count);
        Assert.True(result.Verdicts[0].Accepted);
        Assert.Equal(CardGroundingRejectReason.NotFound, result.Verdicts[1].RejectReason);
        Assert.Equal(CardGroundingRejectReason.UpstreamUnavailable, result.Verdicts[2].RejectReason);
        Assert.True(result.HasUpstreamFailure);
    }

    private static IReadOnlySet<string> SetOf(params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);

    private static IReadOnlySet<char> CharSetOf(params char[] values)
        => new HashSet<char>(values);

    private static CardGroundingDeckContext CreateContext(
        IReadOnlySet<string>? commanderIdentity = null,
        IReadOnlySet<char>? producedColors = null,
        IReadOnlySet<string>? deckCardNames = null)
        => new()
        {
            CommanderColorIdentity = commanderIdentity ?? new HashSet<string>(),
            DeckProducedColors = producedColors ?? new HashSet<char>(),
            DeckCardNames = deckCardNames ?? new HashSet<string>(),
        };

    private static IReadOnlyDictionary<string, string> Legalities(string commander)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["commander"] = commander,
        };

    private static ScryfallCard Card(
        string name,
        string manaCost = "{1}",
        string typeLine = "Artifact",
        IReadOnlyList<string>? colorIdentity = null,
        IReadOnlyDictionary<string, string>? legalities = null)
        => new(
            Name: name,
            ManaCost: manaCost,
            TypeLine: typeLine,
            OracleText: null,
            Power: null,
            Toughness: null,
            Keywords: null,
            ColorIdentity: colorIdentity,
            SetCode: null,
            SetName: null,
            CollectorNumber: null,
            CardFaces: null,
            Id: null,
            Layout: "normal",
            ReleasedAt: null,
            Cmc: 1,
            ProducedMana: null,
            Rarity: "rare",
            Legalities: legalities);

    private static RestResponse<ScryfallCollectionResponse> CollectionResponse(
        params ScryfallCard[] cards)
        => CollectionResponse(HttpStatusCode.OK, [.. cards], []);

    private static RestResponse<ScryfallCollectionResponse> CollectionResponse(
        HttpStatusCode statusCode,
        IReadOnlyList<ScryfallCard>? cards = null,
        IReadOnlyList<ScryfallCollectionIdentifier>? notFound = null)
        => new(new RestRequest())
        {
            StatusCode = statusCode,
            Data = cards is null && statusCode == HttpStatusCode.OK
                ? null
                : new ScryfallCollectionResponse([.. (cards ?? [])], notFound is null ? null : [.. notFound]),
        };

    private static RestResponse<ScryfallCard> NamedResponse(HttpStatusCode statusCode, ScryfallCard? card = null, string? content = null)
        => new(new RestRequest())
        {
            StatusCode = statusCode,
            Data = card,
            Content = content,
        };

    private sealed class FakeResolver : IScryfallCardResolver
    {
        public Func<RestRequest, Task<RestResponse<ScryfallCollectionResponse>>> ExecuteCollectionAsyncImpl { get; init; }
            = _ => Task.FromResult(CollectionResponse());

        public Func<string, Task<RestResponse<ScryfallCard>>> ExecuteNamedFuzzyAsyncImpl { get; init; }
            = _ => Task.FromResult(NamedResponse(HttpStatusCode.NotFound));

        public int ExecuteCollectionCallCount { get; private set; }

        public int ExecuteNamedFuzzyCallCount { get; private set; }

        public Task<RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestRequest request, CancellationToken cancellationToken)
        {
            ExecuteCollectionCallCount++;
            return ExecuteCollectionAsyncImpl(request);
        }

        public Task<RestResponse<ScryfallCard>> ExecuteNamedFuzzyAsync(string cardName, CancellationToken cancellationToken)
        {
            ExecuteNamedFuzzyCallCount++;
            return ExecuteNamedFuzzyAsyncImpl(cardName);
        }

        public Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);
    }
}
