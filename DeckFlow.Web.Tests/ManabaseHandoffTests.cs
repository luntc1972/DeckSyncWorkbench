using DeckFlow.Core.Manabase;
using DeckFlow.Web.Controllers;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Bracket;
using DeckFlow.Web.Services.FeatureFlags;
using DeckFlow.Web.Services.Manabase;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class ManabaseHandoffTests
{
    [Fact]
    public void GetHandoff_LivePayload_RendersCachedReportWithoutAnalysis()
    {
        var cache = new PacketSessionCache();
        const string key = "live-handoff-key";
        cache.Set(key, new ManabaseHandoffPayload
        {
            Result = AnalysisResult(),
            DecklistText = "1 Sol Ring",
            DeckName = "Handoff deck",
            Mode = ManabaseMode.Casual,
        }, 100);
        var controller = BuildController(new ThrowingManabaseAnalysisService(), cache);

        var result = controller.Manabase(key);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ManabaseViewModel>(view.Model);
        Assert.NotNull(model.Report);
        Assert.Equal("1 Sol Ring", model.Request.DeckText);
        Assert.Equal("Handoff deck", model.Request.DeckName);
    }

    [Fact]
    public void GetHandoff_UnknownPayload_RendersExpiredNotice()
    {
        var controller = BuildController(new ThrowingManabaseAnalysisService(), new PacketSessionCache());

        var result = controller.Manabase("unknown-handoff-key");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ManabaseViewModel>(view.Model);
        Assert.Contains("expired", model.NoticeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public void GetHandoff_MissingValue_RendersEmptyForm()
    {
        var result = BuildController(new ThrowingManabaseAnalysisService(), new PacketSessionCache()).Manabase(handoff: null);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ManabaseViewModel>(view.Model);
        Assert.Null(model.Report);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public void GetHandoff_WhitespaceValue_RendersEmptyForm()
    {
        var result = BuildController(new ThrowingManabaseAnalysisService(), new PacketSessionCache()).Manabase("  ");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ManabaseViewModel>(view.Model);
        Assert.Null(model.Report);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public void GetHandoff_RefreshedLivePayload_RendersReportAgainWithoutAnalysis()
    {
        var cache = new PacketSessionCache();
        const string key = "refreshable-handoff-key";
        cache.Set(key, new ManabaseHandoffPayload
        {
            Result = AnalysisResult(),
            DecklistText = "1 Sol Ring",
            DeckName = "Refreshable deck",
            Mode = ManabaseMode.Casual,
        }, 100);
        var controller = BuildController(new ThrowingManabaseAnalysisService(), cache);

        Assert.IsType<ViewResult>(controller.Manabase(key));
        Assert.IsType<ViewResult>(controller.Manabase(key));
    }

    [Fact]
    public async Task PostManabase_NoHandoff_RendersReportInPlace()
    {
        var controller = BuildController(new ReturningManabaseAnalysisService(AnalysisResult()), new PacketSessionCache());

        var result = await controller.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            DeckName = "In-place deck",
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.NotNull(Assert.IsType<ManabaseViewModel>(view.Model).Report);
    }

    private static ManabaseController BuildController(IManabaseAnalysisService service, PacketSessionCache cache)
        => new(
            service,
            new StubCardSearchService(),
            new FakeFeatureFlagCache(),
            new FakeBracketClassificationService(),
            NullLogger<ManabaseController>.Instance,
            cache)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static ManabaseAnalysisResult AnalysisResult() => new(
        new ManabaseReport
        {
            ActualLands = 36,
            TargetLands = 37,
            ColorFindings = [],
            Mode = ManabaseMode.Casual,
            Summary = "Cached mana base report.",
        },
        "100 cards · 36 lands",
        [],
        null,
        string.Empty,
        [],
        null,
        null,
        false);

    private sealed class ThrowingManabaseAnalysisService : IManabaseAnalysisService
    {
        public Task<ManabaseAnalysisResult> AnalyzeAsync(string deckSource, string? deckName, ManabaseAnalysisOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Cached handoff must not analyze again.");

        public Task<ManabaseLoadResult> LoadAsync(string deckSource, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Cached handoff must not load again.");
    }

    private sealed class ReturningManabaseAnalysisService(ManabaseAnalysisResult result) : IManabaseAnalysisService
    {
        public Task<ManabaseAnalysisResult> AnalyzeAsync(string deckSource, string? deckName, ManabaseAnalysisOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(result);

        public Task<ManabaseLoadResult> LoadAsync(string deckSource, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This test does not load decks.");
    }
}
