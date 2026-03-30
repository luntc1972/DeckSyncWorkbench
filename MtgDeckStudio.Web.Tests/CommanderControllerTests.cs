using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MtgDeckStudio.Core.Reporting;
using MtgDeckStudio.Web.Controllers;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class CommanderControllerTests
{
    [Fact]
    public async Task Index_ReturnsSummaries_WhenServiceHasData()
    {
        var rows = new[]
        {
            new CategoryKnowledgeRow("Ramp", "Bird of Paradise", 3),
            new CategoryKnowledgeRow("Ramp", "Llanowar Elves", 1),
            new CategoryKnowledgeRow("Draw", "Guardian Project", 2)
        };

        var summaries = new[]
        {
            new CommanderCategorySummary("Ramp", 4, 3),
            new CommanderCategorySummary("Draw", 2, 2)
        };

        var cardTotals = new CardDeckTotals(2, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["commander"] = 2
        });

        var result = new CommanderCategoryResult(
            "Bello",
            rows,
            summaries,
            HarvestedDeckCount: 5,
            CardDeckTotals: cardTotals,
            AdditionalDecksFound: 0,
            CacheSweepPerformed: true);

        var controller = new CommanderController(
            new DummyCommanderSearchService(),
            new FakeCommanderCategoryService(result),
            NullLogger<CommanderController>.Instance);

        var response = await controller.Index(new CommanderCategoryRequest { CommanderName = "Bello" });
        var viewResult = Assert.IsType<ViewResult>(response);
        var model = Assert.IsType<CommanderCategoryViewModel>(viewResult.Model);

        Assert.Equal(rows.Length, model.CategoryRows.Count);
        Assert.Equal(summaries.Length, model.CategorySummaries.Count);
        Assert.Equal(5, model.HarvestedDeckCount);
        Assert.True(model.HasResults);
        Assert.True(model.ExtendedHarvestTriggered);
        Assert.Equal(0, model.AdditionalDecksFound);
        Assert.Equal(cardTotals.TotalDeckCount, model.CardDeckTotals.TotalDeckCount);
    }

    [Fact]
    public async Task Index_ShowsNoResults_WhenServiceReturnsEmpty()
    {
        var result = new CommanderCategoryResult(
            "Bello",
            Array.Empty<CategoryKnowledgeRow>(),
            Array.Empty<CommanderCategorySummary>(),
            HarvestedDeckCount: 0,
            CardDeckTotals: CardDeckTotals.Empty,
            AdditionalDecksFound: 0,
            CacheSweepPerformed: false);

        var controller = new CommanderController(
            new DummyCommanderSearchService(),
            new FakeCommanderCategoryService(result),
            NullLogger<CommanderController>.Instance);

        var response = await controller.Index(new CommanderCategoryRequest { CommanderName = "Bello" });
        var viewResult = Assert.IsType<ViewResult>(response);
        var model = Assert.IsType<CommanderCategoryViewModel>(viewResult.Model);

        Assert.Empty(model.CategorySummaries);
        Assert.False(model.HasResults);
        Assert.False(model.ExtendedHarvestTriggered);
        Assert.Equal(0, model.AdditionalDecksFound);
    }

    [Fact]
    public async Task Search_ReturnsServiceUnavailable_WhenScryfallFails()
    {
        var controller = new CommanderController(
            new ThrowingCommanderSearchService(new HttpRequestException("Scryfall search returned HTTP 503.", null, HttpStatusCode.ServiceUnavailable)),
            new FakeCommanderCategoryService(new CommanderCategoryResult("", Array.Empty<CategoryKnowledgeRow>(), Array.Empty<CommanderCategorySummary>(), 0, CardDeckTotals.Empty, 0, false)),
            NullLogger<CommanderController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.Search("bello");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        var payload = objectResult.Value!;
        var message = payload.GetType().GetProperty("Message")?.GetValue(payload) as string;
        Assert.Equal("Scryfall returned HTTP 503. Try again shortly.", message);
    }

    private sealed class DummyCommanderSearchService : ICommanderSearchService
    {
        public Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class ThrowingCommanderSearchService : ICommanderSearchService
    {
        private readonly Exception _exception;

        public ThrowingCommanderSearchService(Exception exception)
        {
            _exception = exception;
        }

        public Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<string>>(_exception);
    }

    private sealed class FakeCommanderCategoryService : ICommanderCategoryService
    {
        private readonly CommanderCategoryResult _result;

        public FakeCommanderCategoryService(CommanderCategoryResult result)
        {
            _result = result;
        }

        public Task<CommanderCategoryResult> LookupAsync(string commanderName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }
}
