using DeckFlow.Core.Models;
using DeckFlow.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class CommanderSpellbookServiceTests
{
    // Helper to build a minimal DeckEntry for "mainboard"
    private static DeckEntry MainboardEntry(string name) => new DeckEntry
    {
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        Quantity = 1,
        Board = "mainboard"
    };

    private static DeckEntry CommanderEntry(string name) => new DeckEntry
    {
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        Quantity = 1,
        Board = "commander"
    };

    private static CommanderSpellbookService BuildService(StubHttpMessageHandler stub, IMemoryCache? cache = null)
    {
        var factory = new FakeHttpClientFactory(new Dictionary<string, HttpMessageHandler>
        {
            ["commander-spellbook"] = stub
        });
        return new CommanderSpellbookService(
            factory,
            new FakeResiliencePipelineProvider(),
            cache ?? new MemoryCache(new MemoryCacheOptions()));
    }

    [Fact]
    public async Task FindCombosAsync_SingleCombo_ReturnsCombo()
    {
        const string json = """
{
  "results": {
    "included": [
      {
        "uses": [{"card": {"name": "Thrasios, Triton Hero"}}, {"card": {"name": "Tymna the Weaver"}}],
        "produces": [{"feature": {"name": "Infinite mana"}}],
        "description": "Step 1: tap Thrasios."
      }
    ],
    "almostIncluded": []
  }
}
""";
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

        var sut = BuildService(stub);
        var deck = new List<DeckEntry> { MainboardEntry("Thrasios, Triton Hero"), MainboardEntry("Tymna the Weaver") };

        var result = await sut.FindCombosAsync(deck, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.IncludedCombos);
        Assert.Contains("Infinite mana", result.IncludedCombos[0].Results);
        Assert.Contains("Thrasios, Triton Hero", result.IncludedCombos[0].CardNames);
    }

    [Fact]
    public async Task FindCombosAsync_MultiCombo_ParsesNestedArrays()
    {
        const string json = """
{
  "results": {
    "included": [
      {
        "uses": [{"card": {"name": "Thrasios, Triton Hero"}}],
        "produces": [{"feature": {"name": "Infinite mana"}}],
        "description": ""
      },
      {
        "uses": [{"card": {"name": "Tymna the Weaver"}}],
        "produces": [{"feature": {"name": "Draw engine"}}],
        "description": ""
      }
    ],
    "almostIncluded": []
  }
}
""";
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

        var sut = BuildService(stub);
        var deck = new List<DeckEntry> { MainboardEntry("Thrasios, Triton Hero"), MainboardEntry("Tymna the Weaver") };

        var result = await sut.FindCombosAsync(deck, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.IncludedCombos.Count);
        Assert.Contains("Infinite mana", result.IncludedCombos[0].Results);
        Assert.Contains("Draw engine", result.IncludedCombos[1].Results);
    }

    [Fact]
    public async Task FindCombosAsync_HitsCache_OnSecondCall()
    {
        const string json = """
{
  "results": {
    "included": [
      {
        "uses": [{"card": {"name": "Thrasios, Triton Hero"}}],
        "produces": [{"feature": {"name": "Infinite mana"}}],
        "description": ""
      }
    ],
    "almostIncluded": []
  }
}
""";
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = BuildService(stub, cache);
        var deck = new List<DeckEntry> { MainboardEntry("Thrasios, Triton Hero") };

        var first = await sut.FindCombosAsync(deck, CancellationToken.None);
        var second = await sut.FindCombosAsync(deck, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        // Second call must be served from cache — handler invoked exactly once
        Assert.Single(stub.RecordedRequests);
        Assert.Single(first.IncludedCombos);
        Assert.Single(second.IncludedCombos);
    }

    [Fact]
    public async Task FindCombosAsync_ApiFailure_ReturnsNull()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var sut = BuildService(stub);
        var deck = new List<DeckEntry> { MainboardEntry("Thrasios, Triton Hero") };

        var result = await sut.FindCombosAsync(deck, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindCombosAsync_EmptyDeck_ReturnsNull()
    {
        // Service returns null early when main card list is empty (no mainboard entries)
        var stub = new StubHttpMessageHandler();
        var sut = BuildService(stub);
        var deck = new List<DeckEntry>();

        var result = await sut.FindCombosAsync(deck, CancellationToken.None);

        Assert.Null(result);
        // Handler should not be called — no HTTP request made
        Assert.Empty(stub.RecordedRequests);
    }

    [Fact]
    public async Task FindCombosAsync_CommanderEntry_IncludedInMainboard()
    {
        // Commander board entries are included in the main card list for combo lookup
        const string json = """
{
  "results": {
    "included": [
      {
        "uses": [{"card": {"name": "Thrasios, Triton Hero"}}],
        "produces": [{"feature": {"name": "Infinite mana"}}],
        "description": ""
      }
    ],
    "almostIncluded": []
  }
}
""";
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

        var sut = BuildService(stub);
        var deck = new List<DeckEntry> { CommanderEntry("Thrasios, Triton Hero") };

        var result = await sut.FindCombosAsync(deck, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.IncludedCombos);
    }
}
