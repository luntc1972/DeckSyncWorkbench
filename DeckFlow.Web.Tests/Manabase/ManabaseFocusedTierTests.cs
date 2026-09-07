using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeckFlow.Core.Manabase;
using DeckFlow.Web.Controllers;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.FeatureFlags;
using DeckFlow.Web.Services.Manabase;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class ManabaseFocusedTierTests
{
    [Fact]
    public void ShowCastability_TrueForFocused()
    {
        var vm = new ManabaseViewModel
        {
            Report = ReportFor(ManabaseMode.Focused),
        };

        Assert.True(vm.ShowCastability);
    }

    [Fact]
    public Task Get_FocusedTierFlag_SetsViewModelGate()
    {
        var offController = BuildController(new EchoService(), focusedTierEnabled: false);
        var onController = BuildController(new EchoService(), focusedTierEnabled: true);

        ManabaseViewModel offModel = ModelOf(offController.Manabase());
        ManabaseViewModel onModel = ModelOf(onController.Manabase());

        Assert.False(onModel.Request.Mode == ManabaseMode.Focused);
        Assert.False(GetBoolProperty(offModel, "ShowFocusedTier"));
        Assert.True(GetBoolProperty(onModel, "ShowFocusedTier"));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task View_HidesFocusedRadio_WhenGateOff_AndShowsItWhenOn()
    {
        string offHtml = await RenderManabaseViewAsync(new ManabaseViewModel
        {
            Request = new ManabaseRequest { Mode = ManabaseMode.Casual },
            ShowFocusedTier = false,
        });
        string onHtml = await RenderManabaseViewAsync(new ManabaseViewModel
        {
            Request = new ManabaseRequest { Mode = ManabaseMode.Focused },
            ShowFocusedTier = true,
        });

        Assert.DoesNotContain("value=\"Focused\"", offHtml, StringComparison.Ordinal);
        Assert.Contains("value=\"Focused\"", onHtml, StringComparison.Ordinal);
        Assert.Contains(">Focused<", onHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task View_FocusedMode_WithKeepShapes_RendersCurveCoverage()
    {
        string html = await RenderManabaseViewAsync(new ManabaseViewModel
        {
            Request = new ManabaseRequest { DeckInputSource = DeckInputSource.PasteText, Mode = ManabaseMode.Focused },
            Report = ReportWithCurveCoverage(ManabaseMode.Focused),
            ShowFocusedTier = true,
            ShowMulliganEval = true,
            ShowKeepShapes = true,
        });

        Assert.Contains("Curve coverage", html, StringComparison.Ordinal);
        Assert.Contains("plays a spell on ~4 of first 5 turns", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_FocusedFlagOff_CoercesToCasual_AndHtmlMatchesCasual()
    {
        var offController = BuildController(new EchoService(), focusedTierEnabled: false);

        IActionResult focusedResult = await offController.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            Mode = ManabaseMode.Focused,
        });
        IActionResult casualResult = await offController.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            Mode = ManabaseMode.Casual,
        });

        ManabaseViewModel focusedModel = ModelOf(focusedResult);
        ManabaseViewModel casualModel = ModelOf(casualResult);

        Assert.Equal(ManabaseMode.Casual, focusedModel.Request.Mode);
        Assert.Equal(ManabaseMode.Casual, focusedModel.Report!.Mode);
        Assert.Equal(ManabaseMode.Casual, casualModel.Report!.Mode);

        string focusedHtml = NormalizeAntiForgery(await RenderManabaseViewAsync(focusedModel));
        string casualHtml = NormalizeAntiForgery(await RenderManabaseViewAsync(casualModel));

        Assert.Equal(casualHtml, focusedHtml);
    }

    [Fact]
    public async Task Post_FocusedFlagOn_PreservesFocused()
    {
        var onController = BuildController(new EchoService(), focusedTierEnabled: true);

        IActionResult result = await onController.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            Mode = ManabaseMode.Focused,
        });

        ManabaseViewModel model = ModelOf(result);

        Assert.Equal(ManabaseMode.Focused, model.Request.Mode);
        Assert.Equal(ManabaseMode.Focused, model.Report!.Mode);
        Assert.True(GetBoolProperty(model, "ShowFocusedTier"));
    }

    [Fact]
    public async Task Download_FocusedFlagOff_IsByteIdenticalToCasual()
    {
        var controller = BuildController(new EchoService(), focusedTierEnabled: false);

        var focused = Assert.IsType<FileContentResult>(await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            Mode = ManabaseMode.Focused,
        }));
        var casual = Assert.IsType<FileContentResult>(await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            Mode = ManabaseMode.Casual,
        }));

        Assert.Equal(Encoding.UTF8.GetString(casual.FileContents), Encoding.UTF8.GetString(focused.FileContents));
    }

    [Fact]
    public async Task CommanderSelectionRerender_PreservesFocusedGate()
    {
        var controller = BuildController(new SelectionRequiredService(), focusedTierEnabled: true);

        IActionResult result = await controller.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Academy Rector",
        });

        Assert.True(GetBoolProperty(ModelOf(result), "ShowFocusedTier"));
    }

    [Fact]
    public async Task DownloadErrorRerender_PreservesFocusedGate()
    {
        var controller = BuildController(new ThrowingService(new InvalidOperationException("boom")), focusedTierEnabled: true);

        IActionResult result = await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "bad",
        });

        Assert.True(GetBoolProperty(ModelOf(result), "ShowFocusedTier"));
    }

    private static ManabaseController BuildController(IManabaseAnalysisService service, bool focusedTierEnabled)
    {
        var controller = new ManabaseController(
            service,
            new StubCardSearchService(),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.FocusedTierFlagKey] = focusedTierEnabled,
            }),
            new FakeBracketClassificationService(),
            NullLogger<ManabaseController>.Instance,
            new PacketSessionCache())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    private static ManabaseViewModel ModelOf(IActionResult result)
    {
        var view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<ManabaseViewModel>(view.Model);
    }

    private static bool GetBoolProperty(object target, string name)
    {
        PropertyInfo property = target.GetType().GetProperty(name)
            ?? throw new Xunit.Sdk.XunitException($"{target.GetType().Name}.{name} property missing.");
        return (bool)(property.GetValue(target) ?? false);
    }

    private static ManabaseReport ReportFor(ManabaseMode mode) => new()
    {
        ActualLands = 36,
        TargetLands = mode == ManabaseMode.Cedh ? 28.0 : 37.0,
        ColorFindings = Array.Empty<ColorSourceFinding>(),
        Mode = mode,
        Castability = new[]
        {
            new CardCastability { Name = "Counterspell", ManaValue = 2, OnCurveTurn = 2, CastPercent = 62, LimitingFactor = "color:U" },
        },
        Summary = $"Mode: {mode}",
    };

    private static ManabaseReport ReportWithCurveCoverage(ManabaseMode mode) => new()
    {
        ActualLands = 36,
        TargetLands = 37.0,
        ColorFindings = new[]
        {
            new ColorSourceFinding
            {
                Color = ManaColor.White,
                ActualSources = 20.0,
                RequiredSources = 18,
                DrivingSpell = "Swords to Plowshares",
            },
        },
        Mode = mode,
        Castability = new[]
        {
            new CardCastability
            {
                Name = "Swords to Plowshares",
                ManaValue = 1,
                OnCurveTurn = 1,
                CastPercent = 95,
                LimitingFactor = "color:W",
            },
        },
        MulliganEvaluation = new ManabaseMulliganEvaluation
        {
            KeepableHandPercent = 82,
            KeepableBand = "high",
            Kept7Percent = 55,
            MulliganTo6Percent = 30,
            MulliganTo5Percent = 15,
            CurveCoverageTurns = 3.6,
            RepresentativeOpeners = new[]
            {
                new OpeningHandSample
                {
                    Lands = 3,
                    Colors = 2,
                    RampPieces = 0,
                    OtherCards = 4,
                    KeptCards = 7,
                    Decision = "keep 7",
                    TrackedSpellName = "Swords to Plowshares",
                    TrackedOnCurveTurn = 1,
                    OnCurveCastable = true,
                    HasPlan = true,
                },
            },
        },
        Summary = "Mode: Focused",
    };

    private static string NormalizeAntiForgery(string html) =>
        Regex.Replace(
            html.Replace("\r", string.Empty, StringComparison.Ordinal),
            "(__RequestVerificationToken[^>]*?value=\")[^\"]*(\")",
            "$1NORMALIZED$2");

    private static async Task<string> RenderManabaseViewAsync(ManabaseViewModel model)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
        services.AddSingleton<DiagnosticListener>(_ => new DiagnosticListener("DeckFlow.Web.Tests"));
        services.AddSingleton<DiagnosticSource>(serviceProvider => serviceProvider.GetRequiredService<DiagnosticListener>());
        services.AddSingleton<IWebHostEnvironment>(CreateHostingEnvironment());
        services.AddSingleton<IHostEnvironment>(serviceProvider => serviceProvider.GetRequiredService<IWebHostEnvironment>());
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<DeckFlow.Web.Services.Tools.IToolRegistry, DeckFlow.Web.Services.Tools.ToolRegistry>();
        services.AddSingleton<IFeatureFlagCache>(new FakeFeatureFlagCache());
        services.AddControllersWithViews().AddApplicationPart(typeof(ManabaseController).Assembly);

        using var serviceProvider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider,
        };

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(new RouteValueDictionary(new Dictionary<string, object?> { ["controller"] = "Deck" })),
            new ActionDescriptor());
        var viewEngine = serviceProvider.GetRequiredService<IRazorViewEngine>();
        var viewResult = viewEngine.FindView(actionContext, "Manabase", isMainPage: false);
        Assert.True(viewResult.Success, $"View 'Manabase' was not found. Searched: {string.Join(", ", viewResult.SearchedLocations ?? Array.Empty<string>())}");

        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model,
        };

        await using var writer = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            viewResult.View!,
            viewData,
            new TempDataDictionary(httpContext, new StubTempDataProvider()),
            writer,
            new HtmlHelperOptions());

        await viewResult.View!.RenderAsync(viewContext);
        return writer.ToString();
    }

    private static IWebHostEnvironment CreateHostingEnvironment()
    {
        var contentRoot = AppContext.BaseDirectory;
        var fileProvider = new NullFileProvider();
        return new TestWebHostEnvironment
        {
            ApplicationName = typeof(ManabaseController).Assembly.GetName().Name ?? "DeckFlow.Web",
            ContentRootPath = contentRoot,
            ContentRootFileProvider = fileProvider,
            EnvironmentName = Environments.Development,
            WebRootPath = contentRoot,
            WebRootFileProvider = fileProvider,
        };
    }

    private sealed class EchoService : IManabaseAnalysisService
    {
        public Task<ManabaseAnalysisResult> AnalyzeAsync(
            string deckSource,
            string? deckName,
            ManabaseAnalysisOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new ManabaseAnalysisOptions();
            return Task.FromResult(new ManabaseAnalysisResult(
                ReportFor(options.Mode),
                "1 cards · 36 lands",
                Array.Empty<string>(),
                null,
                string.Empty,
                Array.Empty<CostSuggestion>(),
                null,
                null,
                false));
        }

        public Task<ManabaseLoadResult> LoadAsync(
            string deckSource,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ManabaseLoadResult(
                "1 cards · 36 lands",
                Array.Empty<string>(),
                null,
                Array.Empty<CostSuggestion>()));
    }

    private sealed class SelectionRequiredService : IManabaseAnalysisService
    {
        public Task<ManabaseAnalysisResult> AnalyzeAsync(
            string deckSource,
            string? deckName,
            ManabaseAnalysisOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ManabaseAnalysisResult(
                null,
                "100 cards · 36 lands",
                Array.Empty<string>(),
                null,
                string.Empty,
                Array.Empty<CostSuggestion>(),
                null,
                null,
                false)
            {
                CommanderSelectionRequired = true,
                CommanderChoices = new[] { "Winota, Joiner of Forces" },
            });

        public Task<ManabaseLoadResult> LoadAsync(
            string deckSource,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ManabaseLoadResult(
                "100 cards · 36 lands",
                Array.Empty<string>(),
                null,
                Array.Empty<CostSuggestion>()));
    }

    private sealed class ThrowingService : IManabaseAnalysisService
    {
        private readonly Exception _exception;

        public ThrowingService(Exception exception) => _exception = exception;

        public Task<ManabaseAnalysisResult> AnalyzeAsync(
            string deckSource,
            string? deckName,
            ManabaseAnalysisOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ManabaseAnalysisResult>(_exception);

        public Task<ManabaseLoadResult> LoadAsync(
            string deckSource,
            CancellationToken cancellationToken = default)
            => Task.FromException<ManabaseLoadResult>(_exception);
    }

    private sealed class StubTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
