using System;
using System.Net;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Services;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class UpstreamErrorMessageBuilderTests
{
    [Fact]
    public void BuildDeckSyncMessage_ReturnsMoxfield403Message()
    {
        var request = new DeckDiffRequest
        {
            MoxfieldInputSource = DeckInputSource.PublicUrl,
            MoxfieldUrl = "https://moxfield.com/decks/test"
        };

        var message = UpstreamErrorMessageBuilder.BuildDeckSyncMessage(
            request,
            new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden));

        Assert.Contains("Moxfield blocked", message);
    }

    [Fact]
    public void BuildSuggestionMessage_ReturnsArchidektMessage()
    {
        var message = UpstreamErrorMessageBuilder.BuildSuggestionMessage(
            new InvalidOperationException("Archidekt API deck 123 returned 503 Service Unavailable"));

        Assert.Equal("Archidekt could not be reached right now. Try again shortly.", message);
    }

    [Fact]
    public void BuildSuggestionMessage_ReturnsEdhrecMessage()
    {
        var message = UpstreamErrorMessageBuilder.BuildSuggestionMessage(
            new InvalidOperationException("EDHREC lookup failed."));

        Assert.Equal("EDHREC could not be reached right now. Try again shortly.", message);
    }

    [Fact]
    public void BuildScryfallMessage_ReturnsStatusCodeMessage()
    {
        var message = UpstreamErrorMessageBuilder.BuildScryfallMessage(
            new HttpRequestException("Scryfall search returned HTTP 503.", null, HttpStatusCode.ServiceUnavailable));

        Assert.Equal("Scryfall returned HTTP 503. Try again shortly.", message);
    }

    [Fact]
    public void BuildSuggestionMessage_FallsBackToOriginalMessage()
    {
        var message = UpstreamErrorMessageBuilder.BuildSuggestionMessage(
            new InvalidOperationException("Something else failed."));

        Assert.Equal("Something else failed.", message);
    }
}
