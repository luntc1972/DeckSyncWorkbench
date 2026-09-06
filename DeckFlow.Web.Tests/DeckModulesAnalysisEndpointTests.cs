using DeckFlow.Core.Models;
using DeckFlow.Core.Modular;
using DeckFlow.Web.Controllers;
using DeckFlow.Web.Infrastructure;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Security;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Modular;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class DeckModulesAnalysisEndpointTests
{
    [Fact]
    public async Task Analyze_ReturnsBadRequest_WhenRequestBodyIsNull()
    {
        var controller = CreateController(new FakeConfigurationAnalysisService());

        var result = await controller.Analyze(null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Analyze_ReturnsForbidden_WhenCrossOrigin()
    {
        var service = new FakeConfigurationAnalysisService();
        var controller = CreateController(service);
        controller.Request.Host = new HostString("deckflow.test");
        controller.Request.Headers.Origin = "https://evil.test";

        var result = await controller.Analyze(CreateRequest(), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        Assert.Equal(0, service.InvocationCount);
    }

    [Fact]
    public void Analyze_HasDeckModulesFeatureFlagGate()
    {
        var method = typeof(DeckModulesController).GetMethod(nameof(DeckModulesController.Analyze));
        var gate = method!.GetCustomAttributes(typeof(FeatureFlagGateAttribute), inherit: true)
            .Cast<FeatureFlagGateAttribute>()
            .Single();

        Assert.Equal("tool.deck-modules.enabled", gate.Key);
    }

    [Fact]
    public async Task Analyze_ReturnsOk_WhenAnalysisSucceeds()
    {
        var controller = CreateController(new FakeConfigurationAnalysisService());

        var result = await controller.Analyze(CreateRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Analyze_ReturnsBadRequest_WhenAnalysisFails()
    {
        var controller = CreateController(new ThrowingConfigurationAnalysisService());

        var result = await controller.Analyze(CreateRequest(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Analyze_SecondIdenticalRequest_ReturnsTheSameAnalysisKey()
    {
        var service = new FakeConfigurationAnalysisService();
        var controller = CreateController(service);
        var request = CreateRequest();

        var first = Assert.IsType<OkObjectResult>(await controller.Analyze(request, CancellationToken.None));
        var second = Assert.IsType<OkObjectResult>(await controller.Analyze(request, CancellationToken.None));
        var firstKey = first.Value!.GetType().GetProperty("analysisKey")!.GetValue(first.Value) as string;
        var secondKey = second.Value!.GetType().GetProperty("analysisKey")!.GetValue(second.Value) as string;

        Assert.Equal(firstKey, secondKey, StringComparer.Ordinal);
        Assert.NotNull(firstKey);
        Assert.Equal(64, firstKey.Length);
        Assert.Equal(1, service.InvocationCount);
    }

    private static DeckModulesController CreateController(IConfigurationAnalysisService analysisService) =>
        new(new FakeDeckModulesPageService(), NullLogger<DeckModulesController>.Instance, analysisService, new PacketSessionCache())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

    private static ConfigurationAnalysisRequest CreateRequest() => new()
    {
        Configuration = new DeckModulesCompilationRequest
        {
            BaselineToken = "baseline",
            CommandZone = Array.Empty<DeckEntry>(),
            BaselineMainboardEntries = Array.Empty<DeckEntry>(),
            CoreEntries = Array.Empty<DeckEntry>(),
            Alternatives = new[]
            {
                new DeckModulesAlternativeInput
                {
                    Id = "alt-a",
                    Name = "Alternative A",
                    Profile = DeckModulesProfile.Casual,
                    PlayPlan = "Play a straightforward midrange game plan.",
                    MainboardEntries = Array.Empty<DeckEntry>(),
                },
            },
            SelectedAlternativeId = "alt-a",
        },
    };

    private sealed class FakeDeckModulesPageService : IDeckModulesPageService
    {
        public Task<DeckModulesServiceResult<DeckModulesViewModel>> ImportAsync(DeckModulesImportRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public DeckModulesServiceResult<DeckModulesCompilationViewModel> Compile(DeckModulesCompilationRequest request) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConfigurationAnalysisService : IConfigurationAnalysisService
    {
        public int InvocationCount { get; private set; }

        public Task<DeckModulesServiceResult<ConfigurationAnalysisResult>> AnalyzeAsync(
            ConfigurationAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(DeckModulesServiceResult<ConfigurationAnalysisResult>.Success(new ConfigurationAnalysisResult
            {
                ConfigurationId = "alt-a",
                ConfigurationName = "Alternative A",
                AnalyzedCardCount = 100,
                LandCount = 35,
                TargetLandCount = 36,
                LandDelta = -1,
                Health = "Healthy",
                RampSourceCount = 10,
                HardToCastCount = 0,
                IsCoreOnly = false,
            }));
        }
    }

    private sealed class ThrowingConfigurationAnalysisService : IConfigurationAnalysisService
    {
        public Task<DeckModulesServiceResult<ConfigurationAnalysisResult>> AnalyzeAsync(
            ConfigurationAnalysisRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DeckModulesServiceResult<ConfigurationAnalysisResult>.Failure("Analysis failed."));
    }
}
