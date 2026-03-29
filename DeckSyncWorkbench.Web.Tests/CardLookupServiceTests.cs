using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DeckSyncWorkbench.Web.Services;
using RestSharp;
using Xunit;

namespace DeckSyncWorkbench.Web.Tests;

public sealed class CardLookupServiceTests
{
    [Fact]
    public async Task LookupAsync_PreservesQuantities_AndCollectsMissingLines()
    {
        var service = new ScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                new[]
                {
                    new ScryfallCard("Sol Ring", "{T}", "Artifact", "Add {W}", "—", "—", null, null, null, null),
                    new ScryfallCard("Arcane Signet", "{1}", "Artifact", "Add {W} or {U}", "—", "—", null, null, null, null)
                },
                new[] { new ScryfallCollectionIdentifier("Made Up Card") },
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
        var service = new ScryfallCardLookupService(
            executeAsync: (request, _) =>
            {
                requestCount++;
                return Task.FromResult(CreateCollectionResponse(
                    Array.Empty<ScryfallCard>(),
                    Enumerable.Range(0, 75).Select(index => new ScryfallCollectionIdentifier($"Card {index + ((requestCount - 1) * 75)}")).ToArray(),
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
        var service = new ScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                new[]
                {
                    new ScryfallCard("Sol Ring", "{T}", "Artifact", "Add {W}", "—", "—", null, null, null, null)
                },
                Array.Empty<ScryfallCollectionIdentifier>(),
                request)));
        var lines = string.Join('\n', Enumerable.Repeat("Sol Ring", 101));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LookupAsync(lines));

        Assert.Equal("Please verify 100 non-empty lines or fewer per submission.", exception.Message);
    }

    [Fact]
    public async Task LookupAsync_ThrowsHttpRequestException_WhenScryfallFails()
    {
        var service = new ScryfallCardLookupService(
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
        var service = new ScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                Array.Empty<ScryfallCard>(),
                [new ScryfallCollectionIdentifier("Fblthp, Lost on the Range")],
                request)),
            executeSearchAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallSearchResponse(
                    [new ScryfallCard("Fblthp, Lost on the Range", "{1}{U}", "Legendary Creature — Homunculus", "When this enters, draw a card.", "1", "1", null, "otp", "Outlaws", "7")])
            }));

        var result = await service.LookupAsync("Fblthp, Lost on the Range");

        Assert.Single(result.VerifiedOutputs);
        Assert.Contains("Fblthp, Lost on the Range", result.VerifiedOutputs[0]);
        Assert.Empty(result.MissingLines);
    }

    [Fact]
    public async Task LookupAsync_UsesPlainSearchFallback_ForAlternatePrintedNames()
    {
        var searchQueries = new List<string>();
        var service = new ScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                Array.Empty<ScryfallCard>(),
                [new ScryfallCollectionIdentifier("Pastor da Selva")],
                request)),
            executeSearchAsync: (request, _) =>
            {
                var query = request.Parameters.First(parameter => parameter.Name?.ToString() == "q").Value?.ToString() ?? string.Empty;
                searchQueries.Add(query);

                var cards = query == "Pastor da Selva"
                    ? new[]
                    {
                        new ScryfallCard("Ancient Greenwarden", "{4}{G}{G}", "Creature — Elemental", "You may play lands from your graveyard.", "5", "7", null, "sld", "Secret Lair Drop", "2059")
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
        var service = new ScryfallCardLookupService(
            executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(
                Array.Empty<ScryfallCard>(),
                [new ScryfallCollectionIdentifier("Pastor da Selva")],
                request)),
            executeSearchAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallSearchResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallSearchResponse([])
            }),
            executeNamedAsync: (request, _) => Task.FromResult(new RestResponse<ScryfallCard>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCard("Ancient Greenwarden", "{4}{G}{G}", "Creature — Elemental", "You may play lands from your graveyard.", "5", "7", null, "sld", "Secret Lair Drop", "2059")
            }));

        var result = await service.LookupAsync("Pastor da Selva");

        Assert.Single(result.VerifiedOutputs);
        Assert.Contains("Ancient Greenwarden", result.VerifiedOutputs[0]);
        Assert.Empty(result.MissingLines);
    }

    private static RestResponse<ScryfallCollectionResponse> CreateCollectionResponse(
        IReadOnlyList<ScryfallCard> cards,
        IReadOnlyList<ScryfallCollectionIdentifier> notFound,
        RestRequest request)
    {
        return new RestResponse<ScryfallCollectionResponse>(request)
        {
            StatusCode = HttpStatusCode.OK,
            Data = new ScryfallCollectionResponse(cards.ToList(), notFound.ToList())
        };
    }
}
