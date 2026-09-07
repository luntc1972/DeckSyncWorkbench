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

public sealed class DeckModulesCompareEndpointTests
{
    [Fact]
    public void Compare_ReturnsOk_ForCachedSides_WithoutAnalysis()
    {
        var cache = new PacketSessionCache();
        cache.Set("key-a", CreateAnalysis("alt-a"), PacketSizeEstimator.EstimateSizeBytes(CreateAnalysis("alt-a")));
        cache.Set("key-b", CreateAnalysis("alt-b"), PacketSizeEstimator.EstimateSizeBytes(CreateAnalysis("alt-b")));
        var controller = CreateController(cache);

        var result = controller.Compare(CreateRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void Compare_ReturnsConflict_WithMissingConfigurationIds_WhenCacheMissHasNoPayload()
    {
        var controller = CreateController(new PacketSessionCache());

        var result = controller.Compare(CreateRequest(), CancellationToken.None);

        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var missingConfigurationIds = conflict.Value!.GetType().GetProperty("missingConfigurationIds");
        Assert.NotNull(missingConfigurationIds);
        Assert.Contains("alt-a", Assert.IsAssignableFrom<IEnumerable<string>>(missingConfigurationIds!.GetValue(conflict.Value)));
    }

    [Fact]
    public void Compare_ReseatsInlineAnalysis_WhenCacheMisses()
    {
        var cache = new PacketSessionCache();
        var controller = CreateController(cache);
        var request = CreateRequest(CreateAnalysis("alt-a"), CreateAnalysis("alt-b"));

        var result = controller.Compare(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(cache.TryGet<ConfigurationAnalysisResult>("key-a", out var cached));
        Assert.Equal("alt-a", cached!.ConfigurationId);
    }

    [Fact]
    public void Compare_ReturnsBadRequest_ForInvalidSideCountOrReference()
    {
        var controller = CreateController(new PacketSessionCache());
        var tooFewSides = new ConfigurationComparisonRequest
        {
            Sides = new[] { new ConfigurationComparisonSide { ConfigurationId = "alt-a", AnalysisKey = "key-a" } },
            ReferenceConfigurationId = "alt-a",
        };
        var unknownReference = CreateRequest(CreateAnalysis("alt-a"), CreateAnalysis("alt-b")) with { ReferenceConfigurationId = "missing" };

        Assert.IsType<BadRequestObjectResult>(controller.Compare(tooFewSides, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(controller.Compare(unknownReference, CancellationToken.None));
    }

    [Fact]
    public void Compare_ReturnsBadRequestForbiddenAndHasFeatureGate_ForInvalidRequestContext()
    {
        var controller = CreateController(new PacketSessionCache());
        Assert.IsType<BadRequestObjectResult>(controller.Compare(null, CancellationToken.None));

        controller.Request.Host = new HostString("deckflow.test");
        controller.Request.Headers.Origin = "https://evil.test";
        var forbidden = Assert.IsType<ObjectResult>(controller.Compare(CreateRequest(), CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        var method = typeof(DeckModulesController).GetMethod(nameof(DeckModulesController.Compare));
        var gate = method!.GetCustomAttributes(typeof(FeatureFlagGateAttribute), inherit: true)
            .Cast<FeatureFlagGateAttribute>()
            .Single();
        Assert.Equal("tool.deck-modules.enabled", gate.Key);
    }

    [Fact]
    public void Compare_ResponseContainsNoPromptSchemaOrComboNarrative()
    {
        var cache = new PacketSessionCache();
        cache.Set("key-a", CreateAnalysis("alt-a"), PacketSizeEstimator.EstimateSizeBytes(CreateAnalysis("alt-a")));
        cache.Set("key-b", CreateAnalysis("alt-b"), PacketSizeEstimator.EstimateSizeBytes(CreateAnalysis("alt-b")));
        var response = Assert.IsType<OkObjectResult>(CreateController(cache).Compare(CreateRequest(), CancellationToken.None));

        var names = response.Value!.GetType().GetProperties().Select(property => property.Name);
        Assert.DoesNotContain(names, name => name.Contains("prompt", StringComparison.OrdinalIgnoreCase) || name.Contains("schema", StringComparison.OrdinalIgnoreCase) || name.Contains("combo", StringComparison.OrdinalIgnoreCase));
    }

    private static DeckModulesController CreateController(PacketSessionCache cache) =>
        new(new FakeDeckModulesPageService(), NullLogger<DeckModulesController>.Instance, new ThrowingManabaseAnalysisService(), cache, new ConfigurationDeltaService())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static ConfigurationComparisonRequest CreateRequest(ConfigurationAnalysisResult? first = null, ConfigurationAnalysisResult? second = null) => new()
    {
        Sides = new[]
        {
            new ConfigurationComparisonSide { ConfigurationId = "alt-a", AnalysisKey = "key-a", Analysis = first },
            new ConfigurationComparisonSide { ConfigurationId = "alt-b", AnalysisKey = "key-b", Analysis = second },
        },
        ReferenceConfigurationId = "alt-a",
    };

    private static ConfigurationAnalysisResult CreateAnalysis(string id) => new()
    {
        ConfigurationId = id,
        ConfigurationName = id,
        AnalyzedCardCount = 100,
        LandCount = 35,
        TargetLandCount = 36,
        LandDelta = -1,
        Health = "Healthy",
        RampSourceCount = 10,
        HardToCastCount = 0,
        IsCoreOnly = false,
    };

    private sealed class ThrowingManabaseAnalysisService : IConfigurationAnalysisService
    {
        public Task<DeckModulesServiceResult<ConfigurationAnalysisResult>> AnalyzeAsync(ConfigurationAnalysisRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Comparison must not invoke mana-base analysis.");
    }

    private sealed class FakeDeckModulesPageService : IDeckModulesPageService
    {
        public Task<DeckModulesServiceResult<DeckModulesViewModel>> ImportAsync(DeckModulesImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public DeckModulesServiceResult<DeckModulesCompilationViewModel> Compile(DeckModulesCompilationRequest request) => throw new NotSupportedException();
    }
}
