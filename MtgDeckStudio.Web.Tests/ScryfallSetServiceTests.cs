using MtgDeckStudio.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;
using Xunit;
using System.Net;

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
                        new ScryfallSet("old", "Old Set", "2024-01-01", "expansion", 250, Digital: false),
                        new ScryfallSet("new", "New Set", "2025-01-01", "expansion", 275, Digital: false),
                        new ScryfallSet("mid", "Mid Set", "2024-06-01", "expansion", 260, Digital: false)
                    ])
                }));

        var sets = await service.GetSetsAsync();

        Assert.Collection(
            sets,
            set => Assert.Equal("new", set.Code),
            set => Assert.Equal("mid", set.Code),
            set => Assert.Equal("old", set.Code));
    }

    [Fact]
    public async Task GetSetsAsync_ExcludesDigitalSets()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ScryfallSetService(
            cache,
            new FakeMechanicLookupService(),
            executeSetListAsync: (_, _) => Task.FromResult(
                new RestResponse<ScryfallSetListResponse>(new RestRequest("sets"))
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallSetListResponse(
                    [
                        new ScryfallSet("ppr", "Paper Set", "2025-01-01", "expansion", 250, Digital: false),
                        new ScryfallSet("vow", "Digital Only Set", "2025-01-01", "expansion", 100, Digital: true)
                    ])
                }));

        var sets = await service.GetSetsAsync();

        Assert.Single(sets);
        Assert.Equal("ppr", sets[0].Code);
    }

    [Fact]
    public async Task BuildSetPacketAsync_FiltersCardsByCommanderColorIdentity()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ScryfallSetService(
            cache,
            new FakeMechanicLookupService(),
            executeSetListAsync: (_, _) => Task.FromResult(
                new RestResponse<ScryfallSetListResponse>(new RestRequest("sets"))
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallSetListResponse(
                    [
                        new ScryfallSet("tst", "Test Set", "2025-01-01", "expansion", 3, Digital: false)
                    ])
                }),
            executeSearchAsync: (_, _) => Task.FromResult(
                new RestResponse<ScryfallSearchResponse>(new RestRequest("cards/search"))
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallSearchResponse(
                    [
                        new ScryfallCard("Azorius Card", "{W}{U}", "Creature", "Flying", "2", "2", [], ["W", "U"], "tst", "Test Set", "1"),
                        new ScryfallCard("Colorless Card", "{3}", "Artifact", "{T}: Add {C}.", null, null, [], [], "tst", "Test Set", "2"),
                        new ScryfallCard("Rakdos Card", "{B}{R}", "Creature", "Menace", "3", "2", [], ["B", "R"], "tst", "Test Set", "3")
                    ],
                    false,
                    null)
                }));

        var packet = await service.BuildSetPacketAsync(["tst"], ["W", "U"]);

        Assert.Contains("Azorius Card", packet);
        Assert.Contains("Colorless Card", packet);
        Assert.DoesNotContain("Rakdos Card", packet);
    }

    [Fact]
    public async Task BuildSetPacketAsync_ExcludesLowSignalLandsAndAddsSelectionNotes()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ScryfallSetService(
            cache,
            new FakeMechanicLookupService(),
            executeSetListAsync: (_, _) => Task.FromResult(
                new RestResponse<ScryfallSetListResponse>(new RestRequest("sets"))
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallSetListResponse(
                    [
                        new ScryfallSet("tst", "Test Set", "2025-01-01", "expansion", 4, Digital: false)
                    ])
                }),
            executeSearchAsync: (_, _) => Task.FromResult(
                new RestResponse<ScryfallSearchResponse>(new RestRequest("cards/search"))
                {
                    StatusCode = HttpStatusCode.OK,
                    Data = new ScryfallSearchResponse(
                    [
                        new ScryfallCard("Basic Plains", null, "Basic Land — Plains", "({T}: Add {W}.)", null, null, [], ["W"], "tst", "Test Set", "1"),
                        new ScryfallCard("Temple Campus", null, "Land", "This land enters tapped. {T}: Add {W}.", null, null, [], ["W"], "tst", "Test Set", "2"),
                        new ScryfallCard("Grave Lesson", "{1}{B}", "Sorcery", "Mill three cards, then return target creature card from your graveyard to your hand.", null, null, [], ["B"], "tst", "Test Set", "3"),
                        new ScryfallCard("Token Lecture", "{2}{G}", "Creature", "When this creature enters, create two 1/1 green tokens.", "3", "3", [], ["G"], "tst", "Test Set", "4")
                    ],
                    false,
                    null)
                }));

        var packet = await service.BuildSetPacketAsync(["tst"], ["B", "G", "W"]);

        Assert.Contains("selection_notes:", packet);
        Assert.Contains("candidate_cards_included:", packet);
        Assert.Contains("color_legal_cards_scanned: 4", packet);
        Assert.Contains("Grave Lesson", packet);
        Assert.Contains("Token Lecture", packet);
        Assert.DoesNotContain("Basic Plains", packet);
        Assert.DoesNotContain("Temple Campus", packet);
    }

    private sealed class FakeMechanicLookupService : IMechanicLookupService
    {
        public Task<MechanicLookupResult> LookupAsync(string mechanicName, CancellationToken cancellationToken = default)
            => Task.FromResult(new MechanicLookupResult(mechanicName, false, null, null, null, null, null, "https://magic.wizards.com/en/rules", null));
    }
}
