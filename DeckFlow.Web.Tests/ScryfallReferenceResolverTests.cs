using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Scryfall;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Fixture-driven tests for <see cref="ScryfallReferenceResolver"/> -- no live HTTP. Each test
/// constructs a REAL <see cref="ScryfallCardResolver"/> with deterministic
/// <c>executeCollectionAsyncOverride</c>/<c>executeSearchAsyncOverride</c> fixtures (mirroring
/// <c>DeckAnalysisPacketServiceTests.CreateService</c>'s test seam), so the resolver under test
/// wraps the real production collaborator, not a hand-rolled substitute.
/// </summary>
public sealed class ScryfallReferenceResolverTests
{
    [Fact]
    public void ScryfallCollectionProtocolRequest_PrintingIdentifier_SerializesSetAndCollectorNumberOnly()
    {
        var request = new ScryfallCollectionProtocolRequest(
            [ScryfallCollectionNameIdentifier.ForPrinting("mh3", "123")]);

        string json = JsonSerializer.Serialize(request);

        Assert.Contains("\"set\":\"mh3\"", json, StringComparison.Ordinal);
        Assert.Contains("\"collector_number\":\"123\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveSingleAsync_DoubleFacedName_SubmitsFaceIdentifierToCollectionRequest()
    {
        string requestBody = string.Empty;
        var resolver = new ScryfallCardResolver(
            new FakeScryfallRestClientFactory(new HttpClient { BaseAddress = new Uri("https://api.scryfall.com/") }),
            new FakeResiliencePipelineProvider(),
            executeCollectionAsyncOverride: (request, _) =>
            {
                requestBody = ExtractRequestBody(request);
                return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>
                {
                    CreateCard("Delver of Secrets // Insectile Aberration")
                }));
            },
            executeSearchAsyncOverride: (request, _) =>
                Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
                {
                    StatusCode = HttpStatusCode.NotFound,
                    Data = new ScryfallSearchResponse(new List<ScryfallCard>()),
                }));

        var card = await resolver.ResolveSingleAsync("Delver of Secrets // Insectile Aberration", CancellationToken.None);

        Assert.NotNull(card);
        Assert.Equal(new[] { "Delver of Secrets" }, ExtractNames(requestBody));
        Assert.DoesNotContain("Delver of Secrets // Insectile Aberration", requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveBatchAsync_MixedNamesAndDfc_PreservesRequestOrderAndDeduplicatesSubmittedIdentifiers()
    {
        IReadOnlyList<string>? submittedIdentifiers = null;
        var resolver = CreateResolver((request, _) =>
        {
            submittedIdentifiers = ExtractNames(ExtractRequestBody(request));
            return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>
            {
                CreateCard("Sol Ring"),
                CreateCard("Delver of Secrets // Insectile Aberration"),
            }));
        });

        var result = await resolver.ResolveBatchAsync(
            new[] { "Sol Ring", "Delver of Secrets // Insectile Aberration", "Sol Ring" },
            static (_, _) => Task.FromResult<ScryfallCard?>(null),
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(new[] { "Sol Ring", "Delver of Secrets" }, submittedIdentifiers);
        Assert.Equal(
            new[] { "Sol Ring", "Delver of Secrets // Insectile Aberration", "Sol Ring" },
            result.Resolutions.Select(resolution => resolution.RequestName));
        Assert.Equal(
            new[] { "Sol Ring", "Delver of Secrets // Insectile Aberration", "Sol Ring" },
            result.Resolutions.Select(resolution => resolution.Card.Name));
    }

    [Fact]
    public async Task ResolveBatchAsync_MixedRequests_ReportsIdentifierAndExactNameProtocolBands()
    {
        var resolver = CreateResolver((_, _) => Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>
        {
            CreateCard("Sol Ring"),
            CreateCard("Delver of Secrets // Insectile Aberration"),
        })));

        var result = await resolver.ResolveBatchAsync(
            new[] { "Sol Ring", "Delver of Secrets // Insectile Aberration" },
            static (_, _) => Task.FromResult<ScryfallCard?>(null),
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(ScryfallCollectionProtocolBand.Identifier, result.Resolutions[0].Band);
        Assert.Equal(ScryfallCollectionProtocolBand.ExactName, result.Resolutions[1].Band);
    }

    [Fact]
    public async Task ResolveBatchAsync_CollectionMiss_ReportsFallbackProtocolBand()
    {
        var resolver = CreateResolver((_, _) => Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>())));

        var result = await resolver.ResolveBatchAsync(
            new[] { "Missing Card" },
            static (_, _) => Task.FromResult<ScryfallCard?>(CreateCard("Recovered Card")),
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(ScryfallCollectionProtocolBand.Fallback, Assert.Single(result.Resolutions).Band);
    }

    /// <summary>
    /// T1: a warm name-space entry must be omitted from the next collection POST while a newly
    /// requested identifier remains in the POST. This catches a resolver that reads cached cards
    /// only after it has already made the upstream request.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_WarmNameCache_OmitsOnlyCachedIdentifiersFromCollectionPost()
    {
        var cache = new ScryfallCollectionCardCache();
        var postedIdentifiers = new List<IReadOnlyList<string>>();
        var resolver = CreateResolver(
            (request, _) =>
            {
                var identifiers = ExtractNames(ExtractRequestBody(request));
                postedIdentifiers.Add(identifiers);
                return Task.FromResult(CreateCollectionResponse(identifiers.Select(CreateCard).ToList()));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException($"Fallback must not be invoked for collection hit {name}.");

        await resolver.ResolveBatchAsync(new[] { "Sol Ring" }, Fallback, normalizeForScryfall: false, CancellationToken.None);
        var result = await resolver.ResolveBatchAsync(
            new[] { "Sol Ring", "Arcane Signet" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(2, postedIdentifiers.Count);
        Assert.Equal(new[] { "Sol Ring" }, postedIdentifiers[0]);
        Assert.Equal(new[] { "Arcane Signet" }, postedIdentifiers[1]);
        Assert.Equal(new[] { "Sol Ring", "Arcane Signet" }, result.Resolutions.Select(resolution => resolution.RequestName));
    }

    /// <summary>
    /// T2: a cold lookup is the positive control (one POST); repeating the all-warm batch must
    /// issue no further collection POST and still return the cached card.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_AllWarmNameCache_SkipsCollectionPostAndReturnsCachedCards()
    {
        var cache = new ScryfallCollectionCardCache();
        var collectionCallCount = 0;
        var resolver = CreateResolver(
            (request, _) =>
            {
                collectionCallCount++;
                var identifiers = ExtractNames(ExtractRequestBody(request));
                return Task.FromResult(CreateCollectionResponse(identifiers.Select(CreateCard).ToList()));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException($"Fallback must not be invoked for collection hit {name}.");

        await resolver.ResolveBatchAsync(new[] { "Sol Ring" }, Fallback, normalizeForScryfall: false, CancellationToken.None);
        var callsAfterColdLookup = collectionCallCount;
        var warmResult = await resolver.ResolveBatchAsync(
            new[] { "Sol Ring" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(1, callsAfterColdLookup);
        Assert.Equal(callsAfterColdLookup, collectionCallCount);
        var resolution = Assert.Single(warmResult.Resolutions);
        Assert.Equal("Sol Ring", resolution.RequestName);
        Assert.Equal("Sol Ring", resolution.Card.Name);
        Assert.False(resolution.FromFallback);
    }

    /// <summary>
    /// T3: a card found only by ADR-0004's punctuation-tolerant second pass warms the submitted
    /// identifier, so the repeat is resolution-equivalent without another collection POST.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_WarmSecondPassCard_PreservesAdr0004MatchWithoutFallback()
    {
        var cache = new ScryfallCollectionCardCache();
        var collectionCallCount = 0;
        var fallbackCallCount = 0;
        var smugglersCopter = CreateCard("Smuggler's Copter");
        var resolver = CreateResolver(
            (request, _) =>
            {
                collectionCallCount++;
                Assert.Equal(new[] { "Smugglers Copter" }, ExtractNames(ExtractRequestBody(request)));
                return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { smugglersCopter }));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
        {
            fallbackCallCount++;
            return Task.FromResult<ScryfallCard?>(null);
        }

        var coldResult = await resolver.ResolveBatchAsync(
            new[] { "Smugglers Copter" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);
        Assert.True(cache.TryGetName("Smugglers Copter", out var cachedCard));
        Assert.Equal("Smuggler's Copter", cachedCard?.Name);
        var warmResult = await resolver.ResolveBatchAsync(
            new[] { "Smugglers Copter" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(1, collectionCallCount);
        Assert.Equal(0, fallbackCallCount);
        AssertEquivalentBatchResolution(coldResult, warmResult);
        var resolution = Assert.Single(warmResult.Resolutions);
        Assert.Equal("Smuggler's Copter", resolution.Card.Name);
        Assert.False(resolution.FromFallback);
    }

    /// <summary>
    /// T5: partition-then-chunk -- warmth spread across chunk boundaries must collapse the cold
    /// remainder into the fewest chunks, not leave one POST per original chunk.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_WarmthSpreadAcrossChunks_IssuesOnePostForTheColdRemainder()
    {
        var cache = new ScryfallCollectionCardCache();
        var names = Enumerable.Range(0, 100).Select(i => $"Card {i}").ToArray();

        // Why: warm 60 of 100, deliberately INTERLEAVED so every original chunk keeps a cold member.
        foreach (var index in Enumerable.Range(0, 100).Where(i => i % 10 < 6))
        {
            cache.SetNamePositive($"Card {index}", CreateCard($"Card {index}"));
        }

        var collectionCallCount = 0;
        var submittedBatchSizes = new List<int>();
        var resolver = CreateResolver(
            (request, _) =>
            {
                collectionCallCount++;
                var identifiers = ExtractNames(ExtractRequestBody(request));
                submittedBatchSizes.Add(identifiers.Count);
                return Task.FromResult(CreateCollectionResponse(identifiers.Select(CreateCard).ToList()));
            },
            collectionCardCache: cache);

        var fallbackCallCount = 0;
        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
        {
            fallbackCallCount++;
            return Task.FromResult<ScryfallCard?>(null);
        }

        var result = await resolver.ResolveBatchAsync(
            names,
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(1, collectionCallCount);
        Assert.Equal(new[] { 40 }, submittedBatchSizes);
        Assert.Equal(0, fallbackCallCount);
        Assert.Equal(100, result.Resolutions.Count);
        Assert.All(result.Resolutions, resolution => Assert.False(resolution.FromFallback));
    }

    /// <summary>
    /// T6: warm names keep their ORIGINAL chunk boundaries. Two cached cards that share an ADR-0004
    /// match key but never shared a response must not be pooled into one ambiguous group, which
    /// would decline both matches and buy two fallback searches.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_WarmKeyCollisionAcrossChunks_KeepsBothMatchesWithoutFallback()
    {
        var cache = new ScryfallCollectionCardCache();
        var names = Enumerable.Range(0, 100).Select(i => $"Card {i}").ToArray();

        // Why: index 0 and index 80 land in DIFFERENT 75-name chunks, and their cached cards key
        // identically under BatchMatchKey while matching neither request name raw.
        names[0] = "Smugglers Copter";
        names[80] = "Smuggler's Copter";
        foreach (var index in Enumerable.Range(0, 100))
        {
            var card = index switch
            {
                0 => CreateCard("Smugglers, Copter"),
                80 => CreateCard("Smugglers' Copter"),
                _ => CreateCard(names[index]),
            };
            cache.SetNamePositive(names[index], card);
        }

        var collectionCallCount = 0;
        var resolver = CreateResolver(
            (request, _) =>
            {
                collectionCallCount++;
                var identifiers = ExtractNames(ExtractRequestBody(request));
                return Task.FromResult(CreateCollectionResponse(identifiers.Select(CreateCard).ToList()));
            },
            collectionCardCache: cache);

        var fallbackCallCount = 0;
        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
        {
            fallbackCallCount++;
            return Task.FromResult<ScryfallCard?>(null);
        }

        var result = await resolver.ResolveBatchAsync(
            names,
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(0, collectionCallCount);
        Assert.Equal(0, fallbackCallCount);
        Assert.Equal(100, result.Resolutions.Count);
        Assert.Equal(
            "Smugglers, Copter",
            result.Resolutions.Single(resolution => resolution.RequestName == "Smugglers Copter").Card.Name);
        Assert.Equal(
            "Smugglers' Copter",
            result.Resolutions.Single(resolution => resolution.RequestName == "Smuggler's Copter").Card.Name);
    }

    /// <summary>
    /// T7: ACCEPTED divergence, ADR-0004 addendum. Two punctuation-colliding names in one original
    /// chunk, one warm and one cold, no longer share a matching scope, so each resolves where both
    /// previously declined as mutually ambiguous. Resolves more names, never fewer.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_WarmAndColdPunctuationCollisionInOneChunk_ResolvesBoth()
    {
        var cache = new ScryfallCollectionCardCache();
        cache.SetNamePositive("Smugglers Copter", CreateCard("Smugglers, Copter"));

        var collectionCallCount = 0;
        var submittedIdentifiers = new List<string>();
        var resolver = CreateResolver(
            (request, _) =>
            {
                collectionCallCount++;
                submittedIdentifiers.AddRange(ExtractNames(ExtractRequestBody(request)));
                return Task.FromResult(CreateCollectionResponse(
                    new List<ScryfallCard> { CreateCard("Smugglers' Copter") }));
            },
            collectionCardCache: cache);

        var fallbackCallCount = 0;
        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
        {
            fallbackCallCount++;
            return Task.FromResult<ScryfallCard?>(null);
        }

        var result = await resolver.ResolveBatchAsync(
            new[] { "Smugglers Copter", "Smuggler's Copter" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(1, collectionCallCount);
        Assert.Equal(new[] { "Smuggler's Copter" }, submittedIdentifiers);
        Assert.Equal(0, fallbackCallCount);
        Assert.Equal(
            "Smugglers, Copter",
            result.Resolutions.Single(resolution => resolution.RequestName == "Smugglers Copter").Card.Name);
        Assert.Equal(
            "Smugglers' Copter",
            result.Resolutions.Single(resolution => resolution.RequestName == "Smuggler's Copter").Card.Name);
        Assert.All(result.Resolutions, resolution => Assert.False(resolution.FromFallback));
    }

    /// <summary>
    /// T4: a cached collection miss suppresses only the collection POST. It must still invoke the
    /// caller's fallback every time, because collection not_found is not an absent-card result.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_WarmCollectionMiss_SkipsPostButStillRunsFallbackPerLookup()
    {
        var cache = new ScryfallCollectionCardCache();
        var collectionCallCount = 0;
        var fallbackCallCount = 0;
        var resolver = CreateResolver(
            (request, _) =>
            {
                collectionCallCount++;
                Assert.Equal(new[] { "Printed Name" }, ExtractNames(ExtractRequestBody(request)));
                return Task.FromResult(CreateCollectionResponse(
                    new List<ScryfallCard>(),
                    new List<ScryfallCollectionNameIdentifier> { new("Printed Name") }));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
        {
            fallbackCallCount++;
            return Task.FromResult<ScryfallCard?>(CreateCard($"Fallback Result {fallbackCallCount}"));
        }

        var coldResult = await resolver.ResolveBatchAsync(
            new[] { "Printed Name" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);
        var warmResult = await resolver.ResolveBatchAsync(
            new[] { "Printed Name" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(1, collectionCallCount);
        Assert.Equal(2, fallbackCallCount);
        Assert.True(cache.TryGetName("Printed Name", out var cachedMiss));
        Assert.Null(cachedMiss);
        Assert.Equal("Fallback Result 1", Assert.Single(coldResult.Resolutions).Card.Name);
        Assert.Equal("Fallback Result 2", Assert.Single(warmResult.Resolutions).Card.Name);
        Assert.All(warmResult.Resolutions, resolution => Assert.True(resolution.FromFallback));
    }

    [Fact]
    public async Task ResolveBatchAsync_NullCollectionPayload_DoesNotCacheSubmittedIdentifier()
    {
        var cache = new ScryfallCollectionCardCache();
        var postedIdentifiers = new List<IReadOnlyList<string>>();
        var requestCount = 0;
        var resolver = CreateResolver(
            (request, _) =>
            {
                var identifiers = ExtractNames(ExtractRequestBody(request));
                postedIdentifiers.Add(identifiers);
                requestCount++;
                if (requestCount == 1)
                {
                    return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { CreateCard("Cached") }));
                }

                if (requestCount == 2)
                {
                    return Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
                    {
                        StatusCode = HttpStatusCode.OK,
                        Data = null,
                    });
                }

                return Task.FromResult(CreateCollectionResponse(identifiers.Select(CreateCard).ToList()));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _) => Task.FromResult<ScryfallCard?>(CreateCard(name));

        await resolver.ResolveBatchAsync(new[] { "Cached" }, Fallback, false, CancellationToken.None);
        await Assert.ThrowsAsync<ScryfallReferenceCollectionException>(() =>
            resolver.ResolveBatchAsync(new[] { "Cached", "Null Payload" }, Fallback, false, CancellationToken.None));

        Assert.True(cache.TryGetName("Cached", out var cachedCard));
        Assert.Equal("Cached", cachedCard?.Name);
        Assert.False(cache.TryGetName("Null Payload", out _));

        await resolver.ResolveBatchAsync(new[] { "Cached", "Null Payload" }, Fallback, false, CancellationToken.None);

        Assert.Equal(new[] { "Cached" }, postedIdentifiers[0]);
        Assert.Equal(new[] { "Null Payload" }, postedIdentifiers[1]);
        Assert.Equal(new[] { "Null Payload" }, postedIdentifiers[2]);
    }

    [Fact]
    public async Task ResolveBatchAsync_CollectionException_DoesNotCacheSubmittedIdentifier()
    {
        var cache = new ScryfallCollectionCardCache();
        var postedIdentifiers = new List<IReadOnlyList<string>>();
        var requestCount = 0;
        var expectedException = new InvalidOperationException("collection exploded");
        var resolver = CreateResolver(
            (request, _) =>
            {
                var identifiers = ExtractNames(ExtractRequestBody(request));
                postedIdentifiers.Add(identifiers);
                requestCount++;
                if (requestCount == 1)
                {
                    return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { CreateCard("Cached") }));
                }

                if (requestCount == 2)
                {
                    return Task.FromException<RestResponse<ScryfallCollectionResponse>>(expectedException);
                }

                return Task.FromResult(CreateCollectionResponse(identifiers.Select(CreateCard).ToList()));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _) => Task.FromResult<ScryfallCard?>(CreateCard(name));

        await resolver.ResolveBatchAsync(new[] { "Cached" }, Fallback, false, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveBatchAsync(new[] { "Cached", "Thrown" }, Fallback, false, CancellationToken.None));

        Assert.Same(expectedException, exception);
        Assert.True(cache.TryGetName("Cached", out var cachedCard));
        Assert.Equal("Cached", cachedCard?.Name);
        Assert.False(cache.TryGetName("Thrown", out _));

        await resolver.ResolveBatchAsync(new[] { "Cached", "Thrown" }, Fallback, false, CancellationToken.None);

        Assert.Equal(new[] { "Cached" }, postedIdentifiers[0]);
        Assert.Equal(new[] { "Thrown" }, postedIdentifiers[1]);
        Assert.Equal(new[] { "Thrown" }, postedIdentifiers[2]);
    }

    [Fact]
    public async Task ResolveBatchAsync_UnreturnedIdentifier_IsNotCachedWhileReturnedIdentifierIsCached()
    {
        var cache = new ScryfallCollectionCardCache();
        var postedIdentifiers = new List<IReadOnlyList<string>>();
        var resolver = CreateResolver(
            (request, _) =>
            {
                var identifiers = ExtractNames(ExtractRequestBody(request));
                postedIdentifiers.Add(identifiers);
                return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { CreateCard(identifiers[0]) }));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _) => Task.FromResult<ScryfallCard?>(CreateCard(name));

        await resolver.ResolveBatchAsync(new[] { "Returned", "Unreturned" }, Fallback, false, CancellationToken.None);

        Assert.True(cache.TryGetName("Returned", out var returnedCard));
        Assert.Equal("Returned", returnedCard?.Name);
        Assert.False(cache.TryGetName("Unreturned", out _));

        await resolver.ResolveBatchAsync(new[] { "Returned", "Unreturned" }, Fallback, false, CancellationToken.None);

        Assert.Equal(new[] { "Returned", "Unreturned" }, postedIdentifiers[0]);
        Assert.Equal(new[] { "Unreturned" }, postedIdentifiers[1]);
    }

    /// <summary>
    /// T5: if a later collection chunk is non-2xx, entries already validated in earlier chunks
    /// stay warm while the failed chunk writes nothing. A retry posts only the failed chunk.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_NonSuccessLaterChunk_KeepsEarlierChunkEntriesAndDropsFailedChunk()
    {
        var cache = new ScryfallCollectionCardCache();
        var names = Enumerable.Range(1, 76).Select(index => $"Card {index:D3}").ToArray();
        var postedBatches = new List<IReadOnlyList<string>>();
        var collectionCallCount = 0;
        var resolver = CreateResolver(
            (request, _) =>
            {
                collectionCallCount++;
                var identifiers = ExtractNames(ExtractRequestBody(request));
                postedBatches.Add(identifiers);
                if (collectionCallCount == 2)
                {
                    return Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
                    {
                        StatusCode = HttpStatusCode.TooManyRequests,
                        Data = null,
                    });
                }

                return Task.FromResult(CreateCollectionResponse(identifiers.Select(CreateCard).ToList()));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException($"Fallback must not be invoked for collection hit {name}.");

        var exception = await Assert.ThrowsAsync<ScryfallReferenceCollectionException>(() =>
            resolver.ResolveBatchAsync(names, Fallback, normalizeForScryfall: false, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.True(cache.TryGetName(names[0], out var firstChunkCard));
        Assert.Equal(names[0], firstChunkCard?.Name);
        Assert.False(cache.TryGetName(names[75], out _));
        var retry = await resolver.ResolveBatchAsync(names, Fallback, normalizeForScryfall: false, CancellationToken.None);
        var warmAfterRetry = await resolver.ResolveBatchAsync(names, Fallback, normalizeForScryfall: false, CancellationToken.None);

        Assert.Equal(3, collectionCallCount);
        Assert.Equal(names.Take(75), postedBatches[0]);
        Assert.Equal(new[] { names[75] }, postedBatches[1]);
        Assert.Equal(new[] { names[75] }, postedBatches[2]);
        Assert.Equal(76, retry.Resolutions.Count);
        Assert.Equal(76, warmAfterRetry.Resolutions.Count);
    }

    /// <summary>
    /// T6: a successful collection response writes direct and ADR-0004 second-pass positives
    /// under submitted identifiers, while an explicitly named not_found writes a miss marker.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_SuccessfulCollectionResponse_CachesUnambiguousCardsAndExplicitMisses()
    {
        var cache = new ScryfallCollectionCardCache();
        var collectionCallCount = 0;
        var fallbackCallCount = 0;
        var resolver = CreateResolver(
            (request, _) =>
            {
                collectionCallCount++;
                var identifiers = ExtractNames(ExtractRequestBody(request));
                Assert.Equal(new[] { "Sol Ring", "Smugglers Copter", "Missing Card" }, identifiers);
                return Task.FromResult(CreateCollectionResponse(
                    new List<ScryfallCard>
                    {
                        CreateCard("Sol Ring"),
                        CreateCard("Smuggler's Copter"),
                    },
                    new List<ScryfallCollectionNameIdentifier> { new("Missing Card") }));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
        {
            fallbackCallCount++;
            return Task.FromResult<ScryfallCard?>(CreateCard("Fallback Card"));
        }

        await resolver.ResolveBatchAsync(
            new[] { "Sol Ring", "Smugglers Copter", "Missing Card" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.True(cache.TryGetName("Sol Ring", out var exactCard));
        Assert.Equal("Sol Ring", exactCard?.Name);
        Assert.True(cache.TryGetName("Smugglers Copter", out var secondPassCard));
        Assert.Equal("Smuggler's Copter", secondPassCard?.Name);
        Assert.True(cache.TryGetName("Missing Card", out var missMarker));
        Assert.Null(missMarker);

        var warmResult = await resolver.ResolveBatchAsync(
            new[] { "Sol Ring", "Smugglers Copter", "Missing Card" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(1, collectionCallCount);
        Assert.Equal(2, fallbackCallCount);
        Assert.Equal(new[] { "Sol Ring", "Smugglers Copter", "Missing Card" }, warmResult.Resolutions.Select(resolution => resolution.RequestName));
    }

    /// <summary>
    /// T7: two returned cards mapping to one submitted face identifier are ambiguous. Neither may
    /// seed the cache, even if one also happens to be an exact match for the original request.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_AmbiguousReturnedFaceIdentifier_DoesNotCacheEitherCard()
    {
        var cache = new ScryfallCollectionCardCache();
        var collectionCallCount = 0;
        var resolver = CreateResolver(
            (request, _) =>
            {
                collectionCallCount++;
                var identifier = Assert.Single(ExtractNames(ExtractRequestBody(request)));
                if (identifier == "Alpha")
                {
                    return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>
                    {
                        CreateCard("Alpha // One"),
                        CreateCard("Alpha // Two"),
                    }));
                }

                Assert.Equal("Beta", identifier);
                return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { CreateCard("Beta // One") }));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException($"Fallback must not be invoked for exact collection hit {name}.");

        await resolver.ResolveBatchAsync(new[] { "Alpha // One" }, Fallback, normalizeForScryfall: false, CancellationToken.None);

        Assert.False(cache.TryGetName("Alpha", out _));

        var retry = await resolver.ResolveBatchAsync(
            new[] { "Alpha // One" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(2, collectionCallCount);
        Assert.Equal("Alpha // One", Assert.Single(retry.Resolutions).Card.Name);

        await resolver.ResolveBatchAsync(new[] { "Beta // One" }, Fallback, normalizeForScryfall: false, CancellationToken.None);
        Assert.True(cache.TryGetName("Beta", out var cachedBeta));
        Assert.Equal("Beta // One", cachedBeta?.Name);

        await resolver.ResolveBatchAsync(new[] { "Beta // One" }, Fallback, normalizeForScryfall: false, CancellationToken.None);
        Assert.Equal(3, collectionCallCount);
    }

    /// <summary>
    /// The collection cache is a required collaborator: no constructor may yield a cache-less
    /// resolver, because a silent cache-less instance re-posts every identifier to Scryfall.
    /// </summary>
    [Fact]
    public void Constructors_AllRequireCollectionCardCache()
    {
        var constructors = typeof(ScryfallReferenceResolver)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotEmpty(constructors);
        Assert.All(constructors, constructor =>
            Assert.Contains(constructor.GetParameters(), parameter =>
                parameter.ParameterType == typeof(ScryfallCollectionCardCache) && !parameter.IsOptional));
    }

    /// <summary>
    /// T9: a mixed batch covering raw match, ADR-0004 second-pass match, and fallback produces
    /// identical resolutions and oracle map cold and warm. The cached miss still reaches fallback.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_WarmMixedBatch_MatchesColdResolutionsAndOracleNameMap()
    {
        var cache = new ScryfallCollectionCardCache();
        var collectionCallCount = 0;
        var fallbackCallCount = 0;
        var resolver = CreateResolver(
            (request, _) =>
            {
                collectionCallCount++;
                Assert.Equal(
                    new[] { "Sol Ring", "Smugglers Copter", "Missing Card" },
                    ExtractNames(ExtractRequestBody(request)));
                return Task.FromResult(CreateCollectionResponse(
                    new List<ScryfallCard>
                    {
                        CreateCard("Sol Ring"),
                        CreateCard("Smuggler's Copter"),
                    },
                    new List<ScryfallCollectionNameIdentifier> { new("Missing Card") }));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
        {
            fallbackCallCount++;
            return Task.FromResult<ScryfallCard?>(CreateCard("Fallback Result"));
        }

        var coldResult = await resolver.ResolveBatchAsync(
            new[] { "Sol Ring", "Smugglers Copter", "Missing Card" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);
        Assert.True(cache.TryGetName("Smugglers Copter", out var secondPassCard));
        Assert.Equal("Smuggler's Copter", secondPassCard?.Name);
        var warmResult = await resolver.ResolveBatchAsync(
            new[] { "Sol Ring", "Smugglers Copter", "Missing Card" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(1, collectionCallCount);
        Assert.Equal(2, fallbackCallCount);
        AssertEquivalentBatchResolution(coldResult, warmResult);
        Assert.True(warmResult.Resolutions[0].FromFallback is false);
        Assert.True(warmResult.Resolutions[1].FromFallback is false);
        Assert.True(warmResult.Resolutions[2].FromFallback);
    }

    /// <summary>
    /// T10: an ADR-0004 second-pass card is cached under the submitted identifier during the cold
    /// lookup, making a warm repeat POST-free and resolution-equivalent.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_SecondPassMatch_CachesSubmittedIdentifierForWarmRepeat()
    {
        const string submittedIdentifier = "Nissas Triumph";
        var returnedCard = CreateCard("Nissa's Triumph");
        var cache = new ScryfallCollectionCardCache();
        var collectionCallCount = 0;
        var resolver = CreateResolver(
            (request, _) =>
            {
                collectionCallCount++;
                Assert.Equal(new[] { submittedIdentifier }, ExtractNames(ExtractRequestBody(request)));
                return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { returnedCard }));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException($"Fallback must not be invoked for collection hit {name}.");

        var coldResult = await resolver.ResolveBatchAsync(
            new[] { submittedIdentifier },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.True(cache.TryGetName(submittedIdentifier, out var cachedCard));
        Assert.Equal(returnedCard.Name, cachedCard?.Name);
        var callsAfterColdLookup = collectionCallCount;
        Assert.Equal(1, callsAfterColdLookup);

        var warmResult = await resolver.ResolveBatchAsync(
            new[] { submittedIdentifier },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(callsAfterColdLookup, collectionCallCount);
        AssertEquivalentBatchResolution(coldResult, warmResult);
    }

    /// <summary>
    /// T11: second-pass cache pairing skips keys ambiguous on either side, while an unambiguous
    /// leftover in the same response proves that the pairing path still writes a positive entry.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_AmbiguousSecondPassCachePairs_SkipCollisionsAndCacheUnambiguousPair()
    {
        var cache = new ScryfallCollectionCardCache();
        var resolver = CreateResolver(
            (request, _) =>
            {
                Assert.Equal(
                    new[] { "O'Neil", "ONeil", "Urzas Saga", "Nissas Triumph" },
                    ExtractNames(ExtractRequestBody(request)));
                return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>
                {
                    CreateCard("O-Neil"),
                    CreateCard("Urza's Saga"),
                    CreateCard("Urza-s Saga"),
                    CreateCard("Nissa's Triumph"),
                }));
            },
            collectionCardCache: cache);

        Task<ScryfallCard?> Fallback(string _, CancellationToken __)
            => Task.FromResult<ScryfallCard?>(null);

        var result = await resolver.ResolveBatchAsync(
            new[] { "O'Neil", "ONeil", "Urzas Saga", "Nissas Triumph" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.False(cache.TryGetName("O'Neil", out _));
        Assert.False(cache.TryGetName("ONeil", out _));
        Assert.False(cache.TryGetName("Urzas Saga", out _));
        Assert.True(cache.TryGetName("Nissas Triumph", out var cachedCard));
        Assert.Equal("Nissa's Triumph", cachedCard?.Name);
        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal("Nissas Triumph", resolution.RequestName);
        Assert.Equal("Nissa's Triumph", resolution.Card.Name);
        Assert.False(resolution.FromFallback);
    }

    /// <summary>
    /// H2 lock: a single-slash Archidekt-style name ("A / B") normalized on submission to the
    /// double-slash Scryfall form ("A // B") must NOT match its own original request in the
    /// collection match-back step (original "A / B" != returned "A // B"), so it falls through to
    /// the supplied fallback strategy (Analysis's SearchPrintingFallback-style delegate) and is
    /// keyed by the ORIGINAL name, not the normalized submission or the returned card name.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_SingleSlashNameWithNormalizeOn_FallsThroughToFallbackKeyedByOriginalName()
    {
        var doubleSlashCard = CreateCard("A // B");
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
            Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { doubleSlashCard })));

        var fallbackInvocations = new List<string>();
        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
        {
            fallbackInvocations.Add(name);
            return Task.FromResult<ScryfallCard?>(doubleSlashCard);
        }

        var result = await resolver.ResolveBatchAsync(
            new[] { "A / B" },
            Fallback,
            normalizeForScryfall: true,
            CancellationToken.None);

        Assert.Equal(new[] { "A / B" }, fallbackInvocations);
        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal("A / B", resolution.RequestName);
        Assert.Equal("A // B", resolution.Card.Name);
        Assert.True(resolution.FromFallback);
        Assert.Equal("A // B", result.OracleNameMap["A / B"]);
    }

    [Fact]
    public async Task ResolveBatchAsync_DoubleFacedName_SubmitsFaceIdentifierToCollectionRequest()
    {
        string requestBody = string.Empty;
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
        {
            requestBody = ExtractRequestBody(request);
            return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>
            {
                CreateCard("Delver of Secrets // Insectile Aberration")
            }));
        });

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException($"Fallback must not be invoked for collection hit {name}.");

        var result = await resolver.ResolveBatchAsync(
            new[] { "Delver of Secrets // Insectile Aberration" },
            Fallback,
            normalizeForScryfall: true,
            CancellationToken.None);

        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal("Delver of Secrets // Insectile Aberration", resolution.RequestName);
        Assert.False(resolution.FromFallback);
        Assert.Equal(new[] { "Delver of Secrets" }, ExtractNames(requestBody));
        Assert.DoesNotContain("Delver of Secrets // Insectile Aberration", requestBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// A printed-name request that misses the collection lookup recovers via the supplied fallback
    /// delegate (Comparison/MetaGap's SearchFallback-style strategy) with normalize OFF (default);
    /// the resolution is keyed by the original request name and flagged FromFallback.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_CollectionMissWithNormalizeOff_RecoversViaFallback()
    {
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
            Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>())));

        var oracleCard = CreateCard("Oracle Name");
        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => Task.FromResult<ScryfallCard?>(oracleCard);

        var result = await resolver.ResolveBatchAsync(
            new[] { "Printed Name" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal("Printed Name", resolution.RequestName);
        Assert.Equal("Oracle Name", resolution.Card.Name);
        Assert.True(resolution.FromFallback);
        Assert.Equal("Oracle Name", result.OracleNameMap["Printed Name"]);
    }

    /// <summary>
    /// A clean collection hit for two names is keyed by original name, FromFallback=false, and the
    /// resolutions preserve ORIGINAL REQUEST ORDER regardless of the order Scryfall returned them in.
    /// The fallback delegate must never be invoked for a full collection hit.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_CleanCollectionHit_PreservesOriginalOrderAndNeverCallsFallback()
    {
        var solRing = CreateCard("Sol Ring");
        var arcaneSignet = CreateCard("Arcane Signet");
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
            // Response order deliberately reversed relative to the request order below.
            Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { arcaneSignet, solRing })));

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException($"Fallback must not be invoked for a full collection hit (got: {name}).");

        var result = await resolver.ResolveBatchAsync(
            new[] { "Sol Ring", "Arcane Signet" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(2, result.Resolutions.Count);
        Assert.Equal("Sol Ring", result.Resolutions[0].RequestName);
        Assert.False(result.Resolutions[0].FromFallback);
        Assert.Equal("Arcane Signet", result.Resolutions[1].RequestName);
        Assert.False(result.Resolutions[1].FromFallback);
    }

    /// <summary>
    /// OracleNameMap[originalName] == the RETURNED card's Name for both a collection hit and a
    /// fallback-recovered miss within the same batch call.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_MixedHitAndFallback_OracleNameMapKeyedByOriginalNameForBoth()
    {
        var solRing = CreateCard("Sol Ring");
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
            Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { solRing })));

        var resolvedMiss = CreateCard("Resolved Miss");
        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => Task.FromResult<ScryfallCard?>(resolvedMiss);

        var result = await resolver.ResolveBatchAsync(
            new[] { "Sol Ring", "Miss Card" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal("Sol Ring", result.OracleNameMap["Sol Ring"]);
        Assert.Equal("Resolved Miss", result.OracleNameMap["Miss Card"]);
    }

    /// <summary>Empty input yields an empty resolution with no HTTP calls (collection endpoint never invoked).</summary>
    [Fact]
    public async Task ResolveBatchAsync_EmptyInput_ReturnsEmptyResolutionWithNoHttpCalls()
    {
        var collectionCallCount = 0;
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
        {
            collectionCallCount++;
            return Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>()));
        });

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException("Fallback must not be invoked for empty input.");

        var result = await resolver.ResolveBatchAsync(
            Array.Empty<string>(),
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Empty(result.Resolutions);
        Assert.Empty(result.OracleNameMap);
        Assert.Equal(0, collectionCallCount);
    }

    /// <summary>A non-2xx / null-Data collection response throws an HttpRequestException (the
    /// ScryfallReferenceCollectionException subclass) with the upstream status preserved — the broad
    /// catch the controllers rely on still matches.</summary>
    [Fact]
    public async Task ResolveBatchAsync_NonSuccessCollectionResponse_ThrowsWithUpstreamStatus()
    {
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
            Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                Data = null,
            }));

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException("Fallback must not be invoked when the collection call itself fails.");

        var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            resolver.ResolveBatchAsync(new[] { "Sol Ring" }, Fallback, normalizeForScryfall: false, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    /// <summary>
    /// The collection-call failure surfaces as the DISTINCT <see cref="ScryfallReferenceCollectionException"/>
    /// (not a plain <see cref="HttpRequestException"/>) so consuming services can re-label ONLY it (WR-01).
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_NonSuccessCollectionResponse_ThrowsCollectionExceptionType()
    {
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
            Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                Data = null,
            }));

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException("Fallback must not be invoked when the collection call itself fails.");

        var exception = await Assert.ThrowsAsync<ScryfallReferenceCollectionException>(() =>
            resolver.ResolveBatchAsync(new[] { "Sol Ring" }, Fallback, normalizeForScryfall: false, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    /// <summary>
    /// A failure raised INSIDE the caller's fallback delegate propagates unwrapped — it is NOT converted
    /// into a <see cref="ScryfallReferenceCollectionException"/> — so the caller's original error message
    /// (and its downstream routing) is preserved (WR-01).
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_FallbackDelegateThrows_PropagatesOriginalExceptionUnwrapped()
    {
        // Collection succeeds (200) but returns no card for the request -> the miss dispatches the
        // fallback delegate, which here fails upstream.
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
            Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>())));

        var fallbackFailure = new HttpRequestException(
            "Scryfall fallback lookup failed while resolving Sol Ring with HTTP 503.",
            null,
            HttpStatusCode.ServiceUnavailable);

        Task<ScryfallCard?> Fallback(string name, CancellationToken _) => throw fallbackFailure;

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() =>
            resolver.ResolveBatchAsync(new[] { "Sol Ring" }, Fallback, normalizeForScryfall: false, CancellationToken.None));

        Assert.Same(fallbackFailure, thrown);
        Assert.IsNotType<ScryfallReferenceCollectionException>(thrown);
    }

    /// <summary>
    /// SC-4 lock: a punctuation-drifted request name ("Smugglers Copter") that the collection call
    /// already resolved and returned under different punctuation ("Smuggler's Copter") is matched
    /// from the SAME batch response by the additive second pass -- the fallback delegate must NEVER
    /// be dispatched, because the card is already in hand.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_PunctuationDriftedCollectionHit_MatchesFromTheBatchResponseWithoutFallback()
    {
        var smugglersCopter = CreateCard("Smuggler's Copter");
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
            Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { smugglersCopter })));

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException($"Fallback must not be invoked for a punctuation-drifted collection hit (got: {name}).");

        var result = await resolver.ResolveBatchAsync(
            new[] { "Smugglers Copter" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal("Smugglers Copter", resolution.RequestName);
        Assert.Equal("Smuggler's Copter", resolution.Card.Name);
        Assert.False(resolution.FromFallback);
        Assert.Equal("Smuggler's Copter", result.OracleNameMap["Smugglers Copter"]);
    }

    /// <summary>
    /// Regression lock for the symbol-suffix class raised by external review (2026-07-31).
    ///
    /// The concern: swapping the per-miss fallback from ResolveSingleAsync (collection + NORMALIZED
    /// compare) to SearchFallbackCardAsync (exact-name search) could silently drop names carrying a
    /// decorative symbol. Live probes confirmed the asymmetry is real -- cards/collection resolves
    /// "Sol Ring*" to "Sol Ring", while exact-name search on the same string 404s -- so under the
    /// swap alone that name would have become a silent miss.
    ///
    /// It does not, because the second pass closes it: BatchMatchKey's [^\p{L}\p{N}\s/] deletes any
    /// symbol (Unicode category So), so request and returned card collapse to the same key and the
    /// card is matched from the response already in hand. The fallback is never dispatched, so the
    /// search endpoint's stricter behavior never gets the chance to lose the card.
    ///
    /// This test fails if BatchMatchKey is ever narrowed to stop deleting symbols -- which would
    /// reopen exactly the regression class the review identified.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_SymbolSuffixedCollectionHit_MatchesWithoutReachingTheStricterSearchFallback()
    {
        var solRing = CreateCard("Sol Ring");
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
            Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { solRing })));

        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
            => throw new InvalidOperationException(
                $"Fallback must not be invoked for a symbol-suffixed collection hit; exact-name search 404s on it (got: {name}).");

        var result = await resolver.ResolveBatchAsync(
            new[] { "Sol Ring★" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal("Sol Ring★", resolution.RequestName);
        Assert.Equal("Sol Ring", resolution.Card.Name);
        Assert.False(resolution.FromFallback);
    }

    /// <summary>
    /// Permanent guard (ADR 0004): the second pass's match key deletes punctuation but PRESERVES
    /// "/", so a single-slash Archidekt name ("A / B") and its double-slash card ("A // B") must
    /// still NOT collide even under the second pass alone (normalizeForScryfall deliberately OFF
    /// here, isolating this from the H2 submission-normalization lock). This is what keeps a future
    /// "simplify the key to CardNormalizer.Normalize" change (which collapses both to "a") from
    /// landing silently -- see docs/decisions/0004-scryfall-batch-match-key-asymmetry.md.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_SlashFormsDoNotCollideUnderTheSecondPass()
    {
        var doubleSlashCard = CreateCard("A // B");
        var resolver = CreateResolver(executeCollectionAsync: (request, _) =>
            Task.FromResult(CreateCollectionResponse(new List<ScryfallCard> { doubleSlashCard })));

        var fallbackInvocations = new List<string>();
        Task<ScryfallCard?> Fallback(string name, CancellationToken _)
        {
            fallbackInvocations.Add(name);
            return Task.FromResult<ScryfallCard?>(null);
        }

        var result = await resolver.ResolveBatchAsync(
            new[] { "A / B" },
            Fallback,
            normalizeForScryfall: false,
            CancellationToken.None);

        Assert.Equal(new[] { "A / B" }, fallbackInvocations);
        Assert.Empty(result.Resolutions);
    }

    /// <summary>
    /// SCRY-03 (Phase 6 wave 2, Task 1): three unresolved names in one chunk cost exactly ONE
    /// batched <c>cards/search</c> call, not three, when a <c>batchFallbackStrategy</c> is supplied.
    /// Each request name receives the card whose name keys to it; the per-name
    /// <c>fallbackStrategy</c> must never be invoked once the batch resolves everything.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_BatchFallback_IssuesOneSearchForManyMisses()
    {
        var resolver = CreateResolver((_, _) => Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>())));
        var batchCallCount = 0;

        Task<IReadOnlyList<ScryfallCard>?> BatchFallback(IReadOnlyList<string> names, CancellationToken _)
        {
            batchCallCount++;
            Assert.Equal(3, names.Count);
            return Task.FromResult<IReadOnlyList<ScryfallCard>?>(names.Select(CreateCard).ToList());
        }

        Task<ScryfallCard?> PerNameFallback(string name, CancellationToken _)
            => throw new InvalidOperationException(
                $"Per-name fallback must not be invoked for {name}; the batch pass resolves every name here.");

        var result = await resolver.ResolveBatchAsync(
            new[] { "Alpha Card", "Beta Card", "Gamma Card" },
            PerNameFallback,
            normalizeForScryfall: false,
            CancellationToken.None,
            batchFallbackStrategy: BatchFallback);

        Assert.Equal(1, batchCallCount);
        Assert.Equal(3, result.Resolutions.Count);
        Assert.All(result.Resolutions, resolution => Assert.True(resolution.FromFallback));
        Assert.Equal(
            new[] { "Alpha Card", "Beta Card", "Gamma Card" },
            result.Resolutions.Select(resolution => resolution.RequestName));
        Assert.Equal(
            new[] { "Alpha Card", "Beta Card", "Gamma Card" },
            result.Resolutions.Select(resolution => resolution.Card.Name));
    }

    /// <summary>
    /// SCRY-03: a chunk with ZERO misses (every name hits the collection response) issues no
    /// <c>cards/search</c> request at all -- batched or otherwise. The batch delegate throws if
    /// invoked, so this test fails loudly rather than silently passing on an unreachable assertion.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_BatchFallback_IssuesNoSearchWhenChunkHasNoMisses()
    {
        var resolver = CreateResolver((_, _) => Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>
        {
            CreateCard("Sol Ring"),
            CreateCard("Arcane Signet"),
        })));

        Task<IReadOnlyList<ScryfallCard>?> BatchFallback(IReadOnlyList<string> names, CancellationToken _)
            => throw new InvalidOperationException("Batch fallback must not be invoked when the chunk has no misses.");

        var result = await resolver.ResolveBatchAsync(
            new[] { "Sol Ring", "Arcane Signet" },
            static (_, _) => Task.FromResult<ScryfallCard?>(null),
            normalizeForScryfall: false,
            CancellationToken.None,
            batchFallbackStrategy: BatchFallback);

        Assert.Equal(2, result.Resolutions.Count);
        Assert.All(result.Resolutions, resolution => Assert.False(resolution.FromFallback));
    }

    /// <summary>
    /// SCRY-03/SCRY-04 divergence vector (BLOCK finding, 2026-09-05 blind verification): the
    /// existing per-name loop performs NO name comparison, so a foreign printed name
    /// ("Ya viene el coco") legitimately resolves to a differently-named canonical card
    /// ("Perfect Defense // Denting Blows") -- <c>BatchMatchKey</c> cannot bridge two different card
    /// names, so that hit is left unattributed by the batch match-back. This is exactly the signal
    /// that must open the residual guard: the divergent name resolves through exactly ONE residual
    /// per-name call, tagged Fallback, with its oracle-name-map entry intact. The two key-equal
    /// names must NOT trigger any residual call of their own -- only the divergent name does.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_BatchFallback_ResolvesNameDivergentHitThroughResidualPerNamePass()
    {
        var resolver = CreateResolver((_, _) => Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>())));
        var perfectDefense = CreateCard("Perfect Defense // Denting Blows");
        var perNameFallbackCalls = new List<string>();

        Task<IReadOnlyList<ScryfallCard>?> BatchFallback(IReadOnlyList<string> names, CancellationToken _)
            => Task.FromResult<IReadOnlyList<ScryfallCard>?>(new List<ScryfallCard>
            {
                CreateCard("Card B"),
                CreateCard("Card C"),
                perfectDefense,
            });

        Task<ScryfallCard?> PerNameFallback(string name, CancellationToken _)
        {
            perNameFallbackCalls.Add(name);
            return Task.FromResult<ScryfallCard?>(perfectDefense);
        }

        var result = await resolver.ResolveBatchAsync(
            new[] { "Ya viene el coco", "Card B", "Card C" },
            PerNameFallback,
            normalizeForScryfall: false,
            CancellationToken.None,
            batchFallbackStrategy: BatchFallback);

        Assert.Equal(new[] { "Ya viene el coco" }, perNameFallbackCalls);
        Assert.Equal(3, result.Resolutions.Count);
        var divergent = result.Resolutions.Single(resolution => resolution.RequestName == "Ya viene el coco");
        Assert.Equal("Perfect Defense // Denting Blows", divergent.Card.Name);
        Assert.True(divergent.FromFallback);
        Assert.True(result.OracleNameMap.TryGetValue("Ya viene el coco", out var oracleName));
        Assert.Equal("Perfect Defense // Denting Blows", oracleName);
    }

    /// <summary>
    /// SCRY-04, SC-5: a double-faced request name and a contrived single-slash spelling of the same
    /// title are submitted in the SAME batch. <c>BatchMatchKey</c> preserves the slash exactly so
    /// these stay distinct keys -- neither may cross-match the other's card.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_BatchFallback_MatchesDoubleFacedNameBackToItsRequestName()
    {
        var resolver = CreateResolver((_, _) => Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>())));
        var doubleFaced = CreateCard("Delver of Secrets // Insectile Aberration");
        var singleSlash = CreateCard("Delver of Secrets / Insectile Aberration");

        Task<IReadOnlyList<ScryfallCard>?> BatchFallback(IReadOnlyList<string> names, CancellationToken _)
            => Task.FromResult<IReadOnlyList<ScryfallCard>?>(new List<ScryfallCard> { doubleFaced, singleSlash });

        Task<ScryfallCard?> PerNameFallback(string name, CancellationToken _)
            => throw new InvalidOperationException(
                $"Per-name fallback must not be invoked for {name}; both names should match through the batch pass.");

        var result = await resolver.ResolveBatchAsync(
            new[] { "Delver of Secrets // Insectile Aberration", "Delver of Secrets / Insectile Aberration" },
            PerNameFallback,
            normalizeForScryfall: false,
            CancellationToken.None,
            batchFallbackStrategy: BatchFallback);

        Assert.Equal(2, result.Resolutions.Count);
        var doubleFacedResolution = result.Resolutions.Single(
            resolution => resolution.RequestName == "Delver of Secrets // Insectile Aberration");
        var singleSlashResolution = result.Resolutions.Single(
            resolution => resolution.RequestName == "Delver of Secrets / Insectile Aberration");
        Assert.Equal("Delver of Secrets // Insectile Aberration", doubleFacedResolution.Card.Name);
        Assert.Equal("Delver of Secrets / Insectile Aberration", singleSlashResolution.Card.Name);
    }

    /// <summary>
    /// SCRY-04, SC-5: <c>BatchMatchKey</c> deletes all punctuation, so a curly-apostrophe request
    /// name and a straight-apostrophe returned card collapse to the same key and must match.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_BatchFallback_MatchesCurlyApostropheNameBackToItsRequestName()
    {
        var resolver = CreateResolver((_, _) => Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>())));
        var card = CreateCard("Smuggler's Copter");

        Task<IReadOnlyList<ScryfallCard>?> BatchFallback(IReadOnlyList<string> names, CancellationToken _)
            => Task.FromResult<IReadOnlyList<ScryfallCard>?>(new List<ScryfallCard> { card });

        Task<ScryfallCard?> PerNameFallback(string name, CancellationToken _)
            => throw new InvalidOperationException(
                $"Per-name fallback must not be invoked for {name}; the batch pass should match this name.");

        var result = await resolver.ResolveBatchAsync(
            new[] { "Smuggler’s Copter" },
            PerNameFallback,
            normalizeForScryfall: false,
            CancellationToken.None,
            batchFallbackStrategy: BatchFallback);

        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal("Smuggler's Copter", resolution.Card.Name);
        Assert.True(resolution.FromFallback);
    }

    /// <summary>
    /// SCRY-04, SC-4: a rejected batch query (HTTP 400) must not lose a card the per-card path would
    /// have resolved. Built against a REAL <see cref="ScryfallCardResolver"/> so both
    /// <see cref="IScryfallCardResolver.SearchFallbackCardsAsync"/> (the batch member, which returns
    /// the 400 sentinel) and <see cref="IScryfallCardResolver.SearchFallbackCardAsync"/> (the
    /// per-card residual retry) share the same search override, proving the degrade is a REAL
    /// per-card retry over the whole remaining set, not an empty result or a partial subset.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_BatchFallback_DegradesToPerCardOnBadRequest()
    {
        var searchCallCount = 0;
        var cardResolver = new ScryfallCardResolver(
            new FakeScryfallRestClientFactory(new HttpClient { BaseAddress = new Uri("https://api.scryfall.com/") }),
            new FakeResiliencePipelineProvider(),
            executeCollectionAsyncOverride: (_, _) => Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>())),
            executeSearchAsyncOverride: (request, _) =>
            {
                searchCallCount++;
                if (searchCallCount == 1)
                {
                    // Why: the FIRST call is always the batch attempt (all 3 names joined with "or"),
                    // which this test rejects to force the degrade.
                    return Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
                    {
                        StatusCode = HttpStatusCode.BadRequest,
                        Data = null,
                    });
                }

                var name = ExtractSingleSearchName(request);
                return Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallSearchResponse(new List<ScryfallCard> { CreateCard(name) }),
                });
            });
        var resolver = new ScryfallReferenceResolver(cardResolver, new ScryfallCollectionCardCache());

        var result = await resolver.ResolveBatchAsync(
            new[] { "Alpha Card", "Beta Card", "Gamma Card" },
            (name, ct) => cardResolver.SearchFallbackCardAsync(name, ct),
            normalizeForScryfall: false,
            CancellationToken.None,
            batchFallbackStrategy: (names, ct) => cardResolver.SearchFallbackCardsAsync(names, ct));

        Assert.Equal(4, searchCallCount);
        Assert.Equal(3, result.Resolutions.Count);
        Assert.All(result.Resolutions, resolution => Assert.True(resolution.FromFallback));
        Assert.Equal(
            new[] { "Alpha Card", "Beta Card", "Gamma Card" },
            result.Resolutions.Select(resolution => resolution.RequestName));
    }

    /// <summary>
    /// SCRY-04, SC-4: a search resource 404s ONLY when every term in the query missed. This is the
    /// all-missed signal, not a rejection: zero resolutions, no exception, and the search override
    /// called exactly ONCE -- the residual guard must NOT fire, because a 404 leaves nothing
    /// unattributed (there are no cards in the response to attribute at all). Without the exact
    /// call-count assertion, the all-miss case can silently regress to one request plus N.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_BatchFallback_TreatsNotFoundAsEveryTermMissed()
    {
        var searchCallCount = 0;
        var cardResolver = new ScryfallCardResolver(
            new FakeScryfallRestClientFactory(new HttpClient { BaseAddress = new Uri("https://api.scryfall.com/") }),
            new FakeResiliencePipelineProvider(),
            executeCollectionAsyncOverride: (_, _) => Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>())),
            executeSearchAsyncOverride: (request, _) =>
            {
                searchCallCount++;
                return Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
                {
                    StatusCode = HttpStatusCode.NotFound,
                    Data = null,
                });
            });
        var resolver = new ScryfallReferenceResolver(cardResolver, new ScryfallCollectionCardCache());

        Task<ScryfallCard?> PerNameFallback(string name, CancellationToken _)
            => throw new InvalidOperationException(
                $"Per-name fallback must not be invoked for {name}; a 404 batch response is an all-missed result, not a rejection.");

        var result = await resolver.ResolveBatchAsync(
            new[] { "Alpha Card", "Beta Card", "Gamma Card" },
            PerNameFallback,
            normalizeForScryfall: false,
            CancellationToken.None,
            batchFallbackStrategy: (names, ct) => cardResolver.SearchFallbackCardsAsync(names, ct));

        Assert.Equal(1, searchCallCount);
        Assert.Empty(result.Resolutions);
    }

    /// <summary>
    /// T-06-06 / SC-5: two distinct request names ("Smuggler's Copter", "Smugglers Copter") collapse
    /// to the SAME <c>BatchMatchKey</c>. The batch pass must NEVER cross-match either of them --
    /// a key ambiguous on the name side matches nothing, so the single returned card is left
    /// unattributed and BOTH names fall through to the residual per-name pass, exactly as they do
    /// today (resolved independently, never consulting a key at all). The outcome is deliberately
    /// "resolved correctly", not "left unresolved" -- leaving them unresolved would be a regression
    /// against today's per-name loop.
    /// </summary>
    [Fact]
    public async Task ResolveBatchAsync_BatchFallback_AmbiguousMatchKeyResolvesBothNamesThroughResidualPass()
    {
        var resolver = CreateResolver((_, _) => Task.FromResult(CreateCollectionResponse(new List<ScryfallCard>())));
        var curlyCard = CreateCard("Smuggler's Copter");
        var straightCard = CreateCard("Smugglers Copter");

        Task<IReadOnlyList<ScryfallCard>?> BatchFallback(IReadOnlyList<string> names, CancellationToken _)
            => Task.FromResult<IReadOnlyList<ScryfallCard>?>(new List<ScryfallCard> { curlyCard });

        var perNameCalls = new List<string>();
        Task<ScryfallCard?> PerNameFallback(string name, CancellationToken _)
        {
            perNameCalls.Add(name);
            return Task.FromResult<ScryfallCard?>(name == "Smuggler's Copter" ? curlyCard : straightCard);
        }

        var result = await resolver.ResolveBatchAsync(
            new[] { "Smuggler's Copter", "Smugglers Copter" },
            PerNameFallback,
            normalizeForScryfall: false,
            CancellationToken.None,
            batchFallbackStrategy: BatchFallback);

        Assert.Equal(2, perNameCalls.Count);
        Assert.Contains("Smuggler's Copter", perNameCalls);
        Assert.Contains("Smugglers Copter", perNameCalls);
        Assert.Equal(2, result.Resolutions.Count);
        var curlyResolution = result.Resolutions.Single(resolution => resolution.RequestName == "Smuggler's Copter");
        var straightResolution = result.Resolutions.Single(resolution => resolution.RequestName == "Smugglers Copter");
        Assert.Equal("Smuggler's Copter", curlyResolution.Card.Name);
        Assert.Equal("Smugglers Copter", straightResolution.Card.Name);
        Assert.True(curlyResolution.FromFallback);
        Assert.True(straightResolution.FromFallback);
    }

    private static ScryfallReferenceResolver CreateResolver(
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> executeCollectionAsync,
        ScryfallCollectionCardCache? collectionCardCache = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeSearchAsync = null)
    {
        var cardResolver = new ScryfallCardResolver(
            new FakeScryfallRestClientFactory(new HttpClient { BaseAddress = new Uri("https://api.scryfall.com/") }),
            new FakeResiliencePipelineProvider(),
            executeCollectionAsyncOverride: executeCollectionAsync,
            executeSearchAsyncOverride: executeSearchAsync ?? ((request, _) =>
                Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallSearchResponse(new List<ScryfallCard>()),
                })),
            executeNamedAsyncOverride: (request, _) =>
                Task.FromResult(new RestResponse<ScryfallCard>(request)
                {
                    StatusCode = HttpStatusCode.NotFound,
                    Data = null,
                }));

        return new ScryfallReferenceResolver(cardResolver, collectionCardCache ?? new ScryfallCollectionCardCache());
    }

    private static RestResponse<ScryfallCollectionResponse> CreateCollectionResponse(
        List<ScryfallCard> cards,
        List<ScryfallCollectionNameIdentifier>? notFound = null)
        => new(new RestRequest("cards/collection", Method.Post))
        {
            StatusCode = HttpStatusCode.OK,
            Data = new ScryfallCollectionResponse(cards, notFound ?? []),
        };

    /// <summary>
    /// Extracts the single card name from a per-name <c>SearchFallbackCardAsync</c> request's
    /// <c>q=!"Name"</c> query parameter -- used only by tests that share one search override across
    /// both the batched call and the residual per-card retries.
    /// </summary>
    private static string ExtractSingleSearchName(RestRequest request)
    {
        var query = request.Parameters.FirstOrDefault(parameter => parameter.Name == "q")?.Value?.ToString() ?? string.Empty;
        return query.TrimStart('!').Trim('"');
    }

    private static void AssertEquivalentBatchResolution(
        ScryfallBatchResolution expected,
        ScryfallBatchResolution actual)
    {
        Assert.Equal(expected.Resolutions.Count, actual.Resolutions.Count);
        for (var index = 0; index < expected.Resolutions.Count; index++)
        {
            var expectedResolution = expected.Resolutions[index];
            var actualResolution = actual.Resolutions[index];
            Assert.Equal(expectedResolution.RequestName, actualResolution.RequestName);
            Assert.Equal(expectedResolution.Card, actualResolution.Card);
            Assert.Equal(expectedResolution.FromFallback, actualResolution.FromFallback);
        }

        Assert.Equal(expected.OracleNameMap.Count, actual.OracleNameMap.Count);
        foreach (var (name, oracleName) in expected.OracleNameMap)
        {
            Assert.True(actual.OracleNameMap.TryGetValue(name, out var actualOracleName));
            Assert.Equal(oracleName, actualOracleName);
        }
    }

    private static string ExtractRequestBody(RestRequest request)
    {
        var bodyParameter = request.Parameters.Single(parameter => parameter.Type == ParameterType.RequestBody);
        return bodyParameter.Value switch
        {
            string body => body,
            null => string.Empty,
            _ => JsonSerializer.Serialize(bodyParameter.Value),
        };
    }

    private static IReadOnlyList<string> ExtractNames(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("identifiers", out var identifiers) || identifiers.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return identifiers.EnumerateArray()
            .Select(element => element.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
    }

    private static ScryfallCard CreateCard(string name)
        => new(
            Name: name,
            ManaCost: null,
            TypeLine: "Artifact",
            OracleText: null,
            Power: null,
            Toughness: null,
            Keywords: null,
            ColorIdentity: null,
            SetCode: null,
            SetName: null,
            CollectorNumber: null);
}
