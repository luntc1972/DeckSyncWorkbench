using System.Net;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// CS-25 regression fixtures for LLM-typical fake names and the Dockside Extortionist commander-ban case
/// (banned 2024-09-23), plus typo-heal, off-identity, duplicate, and ambiguous fuzzy lookup behavior.
/// </summary>
public sealed class CardGroundingHallucinationFixtureTests
{
    public static TheoryData<string, FakeResolver, CardGroundingDeckContext, bool, string, CardGroundingRejectReason> HallucinationFixtures
        => new()
        {
            {
                "Dockside Extortionist",
                new FakeResolver
                {
                    ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(Card(
                        "Dockside Extortionist",
                        manaCost: "{1}{R}",
                        typeLine: "Creature - Goblin Pirate",
                        colorIdentity: ["R"],
                        legalities: Legalities("banned")))),
                },
                CreateContext(commanderIdentity: SetOf("R"), producedColors: CharSetOf('R')),
                false,
                "Dockside Extortionist",
                CardGroundingRejectReason.NotLegal
            },
            {
                "Counterspell",
                new FakeResolver
                {
                    ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(Card(
                        "Counterspell",
                        manaCost: "{U}{U}",
                        typeLine: "Instant",
                        colorIdentity: ["U"],
                        legalities: Legalities("legal")))),
                },
                CreateContext(commanderIdentity: SetOf("R"), producedColors: CharSetOf('R', 'U')),
                false,
                "Counterspell",
                CardGroundingRejectReason.IdentityViolation
            },
            {
                "sol-ring",
                new FakeResolver
                {
                    ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(Card(
                        "Sol Ring",
                        manaCost: "{1}",
                        typeLine: "Artifact",
                        legalities: Legalities("legal")))),
                },
                CreateContext(
                    commanderIdentity: SetOf("R"),
                    producedColors: CharSetOf('R'),
                    deckCardNames: SetOf(CardNormalizer.Normalize("Sol Ring"))),
                false,
                "Sol Ring",
                CardGroundingRejectReason.SingletonDuplicate
            },
            {
                "Dockside Extortonist",
                new FakeResolver
                {
                    ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse()),
                    ExecuteNamedFuzzyAsyncImpl = _ => Task.FromResult(NamedResponse(
                        HttpStatusCode.OK,
                        Card(
                            "Dockside Extortionist",
                            manaCost: "{1}{R}",
                            typeLine: "Creature - Goblin Pirate",
                            colorIdentity: ["R"],
                            legalities: Legalities("legal")))),
                },
                CreateContext(commanderIdentity: SetOf("R"), producedColors: CharSetOf('R')),
                true,
                "Dockside Extortionist",
                CardGroundingRejectReason.None
            },
            {
                "Forest",
                new FakeResolver
                {
                    ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse(Card(
                        "Forest",
                        manaCost: null,
                        typeLine: "Basic Land - Forest",
                        colorIdentity: [],
                        legalities: Legalities("legal")))),
                },
                CreateContext(
                    commanderIdentity: SetOf("G"),
                    producedColors: CharSetOf('G'),
                    deckCardNames: SetOf(CardNormalizer.Normalize("Forest"))),
                true,
                "Forest",
                CardGroundingRejectReason.None
            },
        };

    [Theory]
    [MemberData(nameof(HallucinationFixtures))]
    public async Task TryValidateAsync_KnownHallucinationFixtures_ReturnExpectedVerdict(
        string candidateName,
        FakeResolver resolver,
        CardGroundingDeckContext deckContext,
        bool expectedAccepted,
        string expectedCanonicalName,
        CardGroundingRejectReason expectedRejectReason)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync(candidateName, deckContext, CancellationToken.None);

        Assert.Equal(expectedAccepted, verdict.Accepted);
        Assert.Equal(expectedCanonicalName, verdict.CanonicalName);
        Assert.Equal(expectedRejectReason, verdict.RejectReason);
    }

    [Fact]
    public async Task TryValidateAsync_PlausibleFakeName_ReturnsNotFound()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse()),
            ExecuteNamedFuzzyAsyncImpl = _ => Task.FromResult(NamedResponse(
                HttpStatusCode.NotFound,
                content: """{"object":"error","code":"not_found","status":404,"details":"No cards found matching “Prismatic Lotus Vault”."}""")),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync("Prismatic Lotus Vault", CreateContext(), CancellationToken.None);

        Assert.False(verdict.Accepted);
        Assert.Equal("Prismatic Lotus Vault", verdict.CanonicalName);
        Assert.Equal(CardGroundingRejectReason.NotFound, verdict.RejectReason);
    }

    [Fact]
    public async Task TryValidateAsync_AmbiguousFuzzy404_ReturnsAmbiguous()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FakeResolver
        {
            ExecuteCollectionAsyncImpl = _ => Task.FromResult(CollectionResponse()),
            ExecuteNamedFuzzyAsyncImpl = _ => Task.FromResult(NamedResponse(
                HttpStatusCode.NotFound,
                content: """{"object":"error","code":"not_found","type":"ambiguous","status":404,"details":"Too many cards match ambiguous name “aust com”. Add more words to refine your search."}""")),
        };
        var sut = new CardGroundingGuard(resolver, cache);

        var verdict = await sut.TryValidateAsync("aust com", CreateContext(), CancellationToken.None);

        Assert.False(verdict.Accepted);
        Assert.Equal("aust com", verdict.CanonicalName);
        Assert.Equal(CardGroundingRejectReason.Ambiguous, verdict.RejectReason);
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
        string? manaCost = "{1}",
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
            Cmc: manaCost is null ? 0 : 1,
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

    public sealed class FakeResolver : IScryfallCardResolver
    {
        public Func<RestRequest, Task<RestResponse<ScryfallCollectionResponse>>> ExecuteCollectionAsyncImpl { get; init; }
            = _ => Task.FromResult(CollectionResponse());

        public Func<string, Task<RestResponse<ScryfallCard>>> ExecuteNamedFuzzyAsyncImpl { get; init; }
            = _ => Task.FromResult(NamedResponse(HttpStatusCode.NotFound));

        public Task<RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestRequest request, CancellationToken cancellationToken)
            => ExecuteCollectionAsyncImpl(request);

        public Task<RestResponse<ScryfallCard>> ExecuteNamedFuzzyAsync(string cardName, CancellationToken cancellationToken)
            => ExecuteNamedFuzzyAsyncImpl(cardName);

        public Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);
    }
}
