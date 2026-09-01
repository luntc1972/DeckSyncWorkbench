using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DeckFlow.Web.Services;
using RestSharp;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Tests for <see cref="ScryfallCardLookupService"/> covering quantity preservation, missing-line collection,
/// batch splitting, and fallback named-lookup behaviour.
/// </summary>
public sealed class CardLookupServiceTests
{
    [Fact]
    public async Task LookupAsync_PreservesQuantities_AndCollectsMissingLines()
    {
        var service = TestServiceFactory.CreateScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                new[]
                {
                    new ScryfallCard("Sol Ring", "{T}", "Artifact", "Add {W}", "—", "—", null, null, null, null, null),
                    new ScryfallCard("Arcane Signet", "{1}", "Artifact", "Add {W} or {U}", "—", "—", null, null, null, null, null)
                },
                new[] { new ScryfallCollectionNameIdentifier("Made Up Card") },
                request)),
            executeSearchAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallSearchResponse([])
            }));

        var result = await service.LookupAsync("1 Sol Ring\nArcane Signet\nMade Up Card");

        Assert.Contains("Sol Ring", result.VerifiedOutputs[0]);
        Assert.Contains("{T}", result.VerifiedOutputs[0]);
        Assert.Equal(new[] { "ERROR: Made Up Card" }, result.MissingLines);
    }

    [Fact]
    public async Task LookupAsync_SendsCollectionRequestsInBatches()
    {
        var requestCount = 0;
        var service = TestServiceFactory.CreateScryfallCardLookupService(
            executeAsync: (request, _) =>
            {
                requestCount++;
                return Task.FromResult(CreateCollectionResponse(
                    Array.Empty<ScryfallCard>(),
                    Enumerable.Range(0, 75).Select(index => new ScryfallCollectionNameIdentifier($"Card {index + ((requestCount - 1) * 75)}")).ToArray(),
                    request));
            },
            executeSearchAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallSearchResponse([])
            }));

        var lines = string.Join('\n', Enumerable.Range(0, 100).Select(index => $"Card {index}"));
        await service.LookupAsync(lines);

        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task LookupAsync_ThrowsInvalidOperationException_WhenTooManyCardsSubmitted()
    {
        var service = TestServiceFactory.CreateScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                new[]
                {
                    new ScryfallCard("Sol Ring", "{T}", "Artifact", "Add {W}", "—", "—", null, null, null, null, null)
                },
                Array.Empty<ScryfallCollectionNameIdentifier>(),
                request)));
        var lines = string.Join('\n', Enumerable.Repeat("Sol Ring", 101));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LookupAsync(lines));

        // Why: this string must stay byte-identical to the client-side message in
        // card-lookup.ts:434, so the cap reads the same whether it trips in the
        // browser or on the server.
        Assert.Equal("Card Lookup accepts up to 100 non-empty lines per submission.", exception.Message);
    }

    [Fact]
    public async Task LookupAsync_ThrowsHttpRequestException_WhenScryfallFails()
    {
        var service = TestServiceFactory.CreateScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.ServiceUnavailable
            }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.LookupAsync("Sol Ring"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task LookupAsync_UsesPrintedNameFallback_WhenCollectionDoesNotResolveCard()
    {
        var service = TestServiceFactory.CreateScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                Array.Empty<ScryfallCard>(),
                [new ScryfallCollectionNameIdentifier("Fblthp, Lost on the Range")],
                request)),
            executeSearchAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallSearchResponse(
                    [new ScryfallCard("Fblthp, Lost on the Range", "{1}{U}", "Legendary Creature — Homunculus", "When this enters, draw a card.", "1", "1", null, null, "otp", "Outlaws", "7")])
            }));

        var result = await service.LookupAsync("Fblthp, Lost on the Range");

        Assert.Single(result.VerifiedOutputs);
        Assert.Contains("Fblthp, Lost on the Range", result.VerifiedOutputs[0]);
        Assert.Empty(result.MissingLines);
    }

    [Fact]
    public async Task LookupSingleAsync_ReturnsDetectedMechanics_FromKeywordsAndAbilityWords()
    {
        var service = TestServiceFactory.CreateScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                new[]
                {
                        new ScryfallCard(
                            "Monastery Swiftspear",
                            "{R}",
                            "Creature — Human Monk",
                        "Haste\nProwess\nLandfall — Draw a card.",
                        "1",
                        "2",
                        new[] { "Haste", "Prowess" },
                            null,
                            "ktk",
                            "Khans of Tarkir",
                            "118",
                            Id: "swiftspear-1")
                },
                Array.Empty<ScryfallCollectionNameIdentifier>(),
                request)),
            executeRulingsAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallRulingsResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallRulingsResponse([])
            }));

        var result = await service.LookupSingleAsync("Monastery Swiftspear");

        Assert.NotNull(result);
        Assert.Equal("Monastery Swiftspear", result!.CardName);
        Assert.Contains("Monastery Swiftspear", result.VerifiedText);
        Assert.Equal(new[] { "Haste", "Prowess", "Landfall" }, result.Mechanics);
    }

    [Fact]
    public async Task LookupSingleAsync_ReturnsResolvedCardName_WhenFallbackSearchFindsAlternatePrintedName()
    {
        var service = TestServiceFactory.CreateScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                Array.Empty<ScryfallCard>(),
                [new ScryfallCollectionNameIdentifier("Pastor da Selva")],
                request)),
            executeSearchAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallSearchResponse(
                    [new ScryfallCard("Ancient Greenwarden", "{4}{G}{G}", "Creature — Elemental", "You may play lands from your graveyard.", "5", "7", null, null, "sld", "Secret Lair Drop", "2059")])
            }),
            executeRulingsAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallRulingsResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallRulingsResponse([])
            }));

        var result = await service.LookupSingleAsync("Pastor da Selva");

        Assert.NotNull(result);
        Assert.Equal("Ancient Greenwarden", result!.CardName);
        Assert.Contains("Ancient Greenwarden", result.VerifiedText);
    }

    [Fact]
    public async Task LookupAsync_UsesPlainSearchFallback_ForAlternatePrintedNames()
    {
        var searchQueries = new List<string>();
        var service = TestServiceFactory.CreateScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                Array.Empty<ScryfallCard>(),
                [new ScryfallCollectionNameIdentifier("Pastor da Selva")],
                request)),
            executeSearchAsync: (request, _) =>
            {
                var query = request.Parameters.First(parameter => parameter.Name?.ToString() == "q").Value?.ToString() ?? string.Empty;
                searchQueries.Add(query);

                var cards = query == "Pastor da Selva"
                    ? new[]
                    {
                        new ScryfallCard("Ancient Greenwarden", "{4}{G}{G}", "Creature — Elemental", "You may play lands from your graveyard.", "5", "7", null, null, "sld", "Secret Lair Drop", "2059")
                    }
                    : Array.Empty<ScryfallCard>();

                return Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallSearchResponse(cards.ToList())
                });
            });

        var result = await service.LookupAsync("Pastor da Selva");

        Assert.Single(result.VerifiedOutputs);
        Assert.Contains("Ancient Greenwarden", result.VerifiedOutputs[0]);
        Assert.Empty(result.MissingLines);
        Assert.Equal(
            ["(printed:\"Pastor da Selva\" OR name:\"Pastor da Selva\")", "Pastor da Selva"],
            searchQueries);
    }

    [Fact]
    public async Task LookupAsync_UsesNamedFuzzyFallback_WhenSearchFallbackDoesNotResolveCard()
    {
        var service = TestServiceFactory.CreateScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                Array.Empty<ScryfallCard>(),
                [new ScryfallCollectionNameIdentifier("Pastor da Selva")],
                request)),
            executeSearchAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallSearchResponse([])
            }),
            executeNamedAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallCard>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCard("Ancient Greenwarden", "{4}{G}{G}", "Creature — Elemental", "You may play lands from your graveyard.", "5", "7", null, null, "sld", "Secret Lair Drop", "2059")
            }));

        var result = await service.LookupAsync("Pastor da Selva");

        Assert.Single(result.VerifiedOutputs);
        Assert.Contains("Ancient Greenwarden", result.VerifiedOutputs[0]);
        Assert.Empty(result.MissingLines);
    }

    [Fact]
    public async Task LookupAsync_MatchesCurlyApostrophesAgainstStraightApostropheResults()
    {
        var service = TestServiceFactory.CreateScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                new[]
                {
                    new ScryfallCard("April O'Neil, Hacktivist", "{1}{U}{R}", "Legendary Creature — Human Journalist", "Whenever you cast your second spell each turn, draw a card.", "2", "3", null, null, "who", "Doctor Who", "119")
                },
                Array.Empty<ScryfallCollectionNameIdentifier>(),
                request)));

        var result = await service.LookupAsync("April O’Neil, Hacktivist");

        Assert.Single(result.VerifiedOutputs);
        Assert.Contains("April O'Neil, Hacktivist", result.VerifiedOutputs[0]);
        Assert.Empty(result.MissingLines);
    }

    private static RestResponse<ScryfallCollectionResponse> CreateCollectionResponse(
        IReadOnlyList<ScryfallCard> cards,
        IReadOnlyList<ScryfallCollectionNameIdentifier> notFound,
        RestRequest request)
    {
        return new RestResponse<ScryfallCollectionResponse>(request)
        {
            StatusCode = HttpStatusCode.OK,
            Data = new ScryfallCollectionResponse(cards.ToList(), notFound.ToList())
        };
    }
}
