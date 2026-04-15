using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Services;
using RestSharp;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class EdhTop16ClientTests
{
    [Fact]
    public async Task SearchCommanderEntriesAsync_MapsGraphQlPayload()
    {
        var client = new EdhTop16Client(executeAsync: (_, _) => Task.FromResult(new RestResponse
        {
            StatusCode = HttpStatusCode.OK,
            ResponseStatus = ResponseStatus.Completed,
            Content = """
                {
                  "data": {
                    "commander": {
                      "entries": {
                        "edges": [
                          {
                            "node": {
                              "standing": 1,
                              "wins": 4,
                              "losses": 1,
                              "draws": 1,
                              "decklist": "https://edhtop16.com/deck/1",
                              "player": { "name": "Alice" },
                              "tournament": {
                                "name": "Rocky Mountain Showdown",
                                "tournamentDate": "2026-04-10",
                                "size": 84,
                                "TID": "abc123"
                              },
                              "maindeck": [
                                { "name": "Mana Crypt", "type": "Artifact" },
                                { "name": "", "type": "Artifact" }
                              ]
                            }
                          }
                        ]
                      }
                    }
                  },
                  "errors": []
                }
                """
        }));

        var result = await client.SearchCommanderEntriesAsync(
            " Tymna the Weaver ",
            CedhMetaTimePeriod.ONE_YEAR,
            CedhMetaSortBy.TOP,
            50,
            16,
            10,
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal(1, entry.Standing);
        Assert.Equal("Alice", entry.PlayerName);
        Assert.Equal("Rocky Mountain Showdown", entry.TournamentName);
        Assert.Equal("abc123", entry.TournamentId);
        Assert.Equal(new DateOnly(2026, 4, 10), entry.TournamentDate);
        Assert.Equal(84, entry.TournamentSize);
        Assert.Equal(4d / 6d + (1d / 12d), entry.WinRate, 6);
        var card = Assert.Single(entry.MainDeck);
        Assert.Equal("Mana Crypt", card.Name);
        Assert.Equal("Artifact", card.Type);
    }

    [Fact]
    public async Task SearchCommanderEntriesAsync_ThrowsWhenGraphQlReturnsErrors()
    {
        var client = new EdhTop16Client(executeAsync: (_, _) => Task.FromResult(new RestResponse
        {
            StatusCode = HttpStatusCode.OK,
            ResponseStatus = ResponseStatus.Completed,
            Content = """
                {
                  "data": null,
                  "errors": [
                    { "message": "Commander not found." }
                  ]
                }
                """
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchCommanderEntriesAsync(
            "Rograkh",
            CedhMetaTimePeriod.ONE_YEAR,
            CedhMetaSortBy.TOP,
            50,
            null,
            10,
            CancellationToken.None));

        Assert.Equal("Commander not found.", exception.Message);
    }

    [Fact]
    public async Task SearchCommanderEntriesAsync_RejectsNonPositiveCount()
    {
        var client = new EdhTop16Client(executeAsync: (_, _) => throw new InvalidOperationException("Should not run"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchCommanderEntriesAsync(
            "Kinnan",
            CedhMetaTimePeriod.ONE_YEAR,
            CedhMetaSortBy.TOP,
            50,
            null,
            0,
            CancellationToken.None));

        Assert.Equal("At least one EDH Top 16 entry must be requested.", exception.Message);
    }
}
