using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeckFlow.Core.Models;
using DeckFlow.Core.Reporting;
using DeckFlow.Web.Controllers.Api;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.Api;
using DeckFlow.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class SuggestionsApiControllerTests
{
    [Fact]
    public async Task PostCardSuggestionAsync_ReturnsBadRequest_WhenCardNameMissing()
    {
        var controller = CreateController(
            new FakeCategorySuggestionService(CategorySuggestionResult.Empty("")),
            new FakeCommanderCategoryService(new CommanderCategoryResult("", Array.Empty<CategoryKnowledgeRow>(), Array.Empty<CommanderCategorySummary>(), 0, CardDeckTotals.Empty, 0, false)),
            new FakeMechanicLookupService(MechanicLookupResult.NotFound("", "https://magic.wizards.com/en/rules", null)),
            NullLogger<SuggestionsApiController>.Instance);

        var response = await controller.PostCardSuggestionAsync(new CategorySuggestionRequest(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task PostCardSuggestionAsync_ReturnsStructuredResponse()
    {
        var result = new CategorySuggestionResult(
            "Guardian Project",
            Array.Empty<string>(),
            new[] { "Draw", "Ramp" },
            Array.Empty<string>(),
            Array.Empty<string>(),
            new CardDeckTotals(3, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["mainboard"] = 3 }),
            new[] { "cached store" },
            false,
            2,
            true);

        var controller = CreateController(
            new FakeCategorySuggestionService(result),
            new FakeCommanderCategoryService(new CommanderCategoryResult("", Array.Empty<CategoryKnowledgeRow>(), Array.Empty<CommanderCategorySummary>(), 0, CardDeckTotals.Empty, 0, false)),
            new FakeMechanicLookupService(MechanicLookupResult.NotFound("", "https://magic.wizards.com/en/rules", null)),
            NullLogger<SuggestionsApiController>.Instance);

        var response = await controller.PostCardSuggestionAsync(new CategorySuggestionRequest
        {
            CardName = "Guardian Project"
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<CategorySuggestionApiResponse>(ok.Value);
        Assert.Equal("Guardian Project", payload.CardName);
        Assert.True(payload.HasInferredCategories);
        Assert.Equal(2, payload.AdditionalDecksFound);
        Assert.True(payload.CacheSweepPerformed);
    }

    [Fact]
    public async Task PostCardSuggestionAsync_ReturnsTaggerFields()
    {
        var result = new CategorySuggestionResult(
            "Esper Sentinel",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            new[] { "Protection", "Value" },
            CardDeckTotals.Empty,
            new[] { "Scryfall Tagger" },
            false,
            0,
            false);

        var controller = CreateController(
            new FakeCategorySuggestionService(result),
            new FakeCommanderCategoryService(new CommanderCategoryResult("", Array.Empty<CategoryKnowledgeRow>(), Array.Empty<CommanderCategorySummary>(), 0, CardDeckTotals.Empty, 0, false)),
            new FakeMechanicLookupService(MechanicLookupResult.NotFound("", "https://magic.wizards.com/en/rules", null)),
            NullLogger<SuggestionsApiController>.Instance);

        var response = await controller.PostCardSuggestionAsync(new CategorySuggestionRequest
        {
            CardName = "Esper Sentinel",
            Mode = CategorySuggestionMode.ScryfallTagger
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<CategorySuggestionApiResponse>(ok.Value);
        Assert.True(payload.HasTaggerCategories);
        Assert.Contains("Protection", payload.TaggerCategoriesText);
        Assert.Equal("These are community-curated functional tags from Scryfall Tagger.", payload.TaggerSuggestionContextText);
        Assert.Equal("Source used: Scryfall Tagger", payload.SuggestionSourceSummary);
        Assert.False(payload.CacheSweepPerformed);
    }

    [Fact]
    public async Task PostCommanderSuggestionAsync_ReturnsBadRequest_WhenCommanderMissing()
    {
        var controller = CreateController(
            new FakeCategorySuggestionService(CategorySuggestionResult.Empty("")),
            new FakeCommanderCategoryService(new CommanderCategoryResult("", Array.Empty<CategoryKnowledgeRow>(), Array.Empty<CommanderCategorySummary>(), 0, CardDeckTotals.Empty, 0, false)),
            new FakeMechanicLookupService(MechanicLookupResult.NotFound("", "https://magic.wizards.com/en/rules", null)),
            NullLogger<SuggestionsApiController>.Instance);

        var response = await controller.PostCommanderSuggestionAsync(new CommanderCategoryRequest(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task PostCommanderSuggestionAsync_ReturnsStructuredResponse()
    {
        var result = new CommanderCategoryResult(
            "Bello",
            new[] { new CategoryKnowledgeRow("Ramp", "Birds of Paradise", 2) },
            new[] { new CommanderCategorySummary("Ramp", 2, 2) },
            8,
            new CardDeckTotals(4, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["commander"] = 4 }),
            3,
            true);

        var controller = CreateController(
            new FakeCategorySuggestionService(CategorySuggestionResult.Empty("")),
            new FakeCommanderCategoryService(result),
            new FakeMechanicLookupService(MechanicLookupResult.NotFound("", "https://magic.wizards.com/en/rules", null)),
            NullLogger<SuggestionsApiController>.Instance);

        var response = await controller.PostCommanderSuggestionAsync(new CommanderCategoryRequest
        {
            CommanderName = "Bello"
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<CommanderCategoryApiResponse>(ok.Value);
        Assert.Equal("Bello", payload.CommanderName);
        Assert.Equal(1, payload.CategoryCount);
        Assert.Equal(3, payload.AdditionalDecksFound);
        Assert.True(payload.CacheSweepPerformed);
    }

    [Fact]
    public async Task PostCardSuggestionAsync_ReturnsSiteSpecificMessage_WhenUpstreamRequestFails()
    {
        var controller = CreateController(
            new ThrowingCategorySuggestionService(new HttpRequestException("EDHREC returned HTTP 503.", null, System.Net.HttpStatusCode.ServiceUnavailable)),
            new FakeCommanderCategoryService(new CommanderCategoryResult("", Array.Empty<CategoryKnowledgeRow>(), Array.Empty<CommanderCategorySummary>(), 0, CardDeckTotals.Empty, 0, false)),
            new FakeMechanicLookupService(MechanicLookupResult.NotFound("", "https://magic.wizards.com/en/rules", null)),
            NullLogger<SuggestionsApiController>.Instance);

        var response = await controller.PostCardSuggestionAsync(new CategorySuggestionRequest
        {
            CardName = "Sol Ring"
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        var message = badRequest.Value?.GetType().GetProperty("Message")?.GetValue(badRequest.Value) as string;
        Assert.Equal("EDHREC returned HTTP 503. Try again shortly.", message);
    }

    [Fact]
    public async Task PostMechanicLookupAsync_ReturnsStructuredResponse()
    {
        var controller = CreateController(
            new FakeCategorySuggestionService(CategorySuggestionResult.Empty("")),
            new FakeCommanderCategoryService(new CommanderCategoryResult("", Array.Empty<CategoryKnowledgeRow>(), Array.Empty<CommanderCategorySummary>(), 0, CardDeckTotals.Empty, 0, false)),
            new FakeMechanicLookupService(new MechanicLookupResult(
                "Prowess",
                true,
                "Prowess",
                "702.108",
                "Exact rules section",
                "702.108. Prowess",
                "A keyword ability that causes a creature to get +1/+1 whenever its controller casts a noncreature spell.",
                "https://magic.wizards.com/en/rules",
                "https://media.wizards.com/2026/downloads/MagicCompRules%2020260227.txt")),
            NullLogger<SuggestionsApiController>.Instance);

        var response = await controller.PostMechanicLookupAsync(new MechanicLookupRequest
        {
            MechanicName = "Prowess"
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<MechanicLookupApiResponse>(ok.Value);
        Assert.True(payload.Found);
        Assert.Equal("Prowess", payload.MechanicName);
        Assert.Equal("702.108", payload.RuleReference);
    }

    [Fact]
    public async Task PostCardSuggestionAsync_ReturnsForbidden_WhenOriginIsCrossSite()
    {
        var controller = new SuggestionsApiController(
            new FakeCategorySuggestionService(CategorySuggestionResult.Empty("")),
            new FakeCommanderCategoryService(new CommanderCategoryResult("", Array.Empty<CategoryKnowledgeRow>(), Array.Empty<CommanderCategorySummary>(), 0, CardDeckTotals.Empty, 0, false)),
            new FakeMechanicLookupService(MechanicLookupResult.NotFound("", "https://magic.wizards.com/en/rules", null)),
            NullLogger<SuggestionsApiController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("deckflow.test");
        controller.Request.Headers.Origin = "https://evil.test";

        var response = await controller.PostCardSuggestionAsync(new CategorySuggestionRequest
        {
            CardName = "Sol Ring"
        }, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    private static SuggestionsApiController CreateController(
        ICategorySuggestionService categorySuggestionService,
        ICommanderCategoryService commanderCategoryService,
        IMechanicLookupService mechanicLookupService,
        ILogger<SuggestionsApiController> logger)
    {
        var controller = new SuggestionsApiController(categorySuggestionService, commanderCategoryService, mechanicLookupService, logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("deckflow.test");
        controller.Request.Headers.Origin = "https://deckflow.test";
        return controller;
    }

    private sealed class FakeCategorySuggestionService : ICategorySuggestionService
    {
        private readonly CategorySuggestionResult _result;

        public FakeCategorySuggestionService(CategorySuggestionResult result)
        {
            _result = result;
        }

        public Task<CategorySuggestionResult> SuggestAsync(CategorySuggestionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class FakeCommanderCategoryService : ICommanderCategoryService
    {
        private readonly CommanderCategoryResult _result;

        public FakeCommanderCategoryService(CommanderCategoryResult result)
        {
            _result = result;
        }

        public Task<CommanderCategoryResult> LookupAsync(string commanderName, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class ThrowingCategorySuggestionService : ICategorySuggestionService
    {
        private readonly Exception _exception;

        public ThrowingCategorySuggestionService(Exception exception)
        {
            _exception = exception;
        }

        public Task<CategorySuggestionResult> SuggestAsync(CategorySuggestionRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<CategorySuggestionResult>(_exception);
    }

    private sealed class FakeMechanicLookupService : IMechanicLookupService
    {
        private readonly MechanicLookupResult _result;

        public FakeMechanicLookupService(MechanicLookupResult result)
        {
            _result = result;
        }

        public Task<MechanicLookupResult> LookupAsync(string mechanicName, CancellationToken cancellationToken = default)
            => Task.FromResult(_result with { Query = mechanicName });
    }
}
