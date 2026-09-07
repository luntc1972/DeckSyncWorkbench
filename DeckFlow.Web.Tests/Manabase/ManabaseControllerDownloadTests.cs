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

/// <summary>
/// Verifies the <c>POST /manabase/download</c> action: a valid deck produces a timestamped
/// text file attachment; invalid enum values are coerced to defaults; service failures re-render
/// the view with a friendly error rather than returning a raw 500.
/// </summary>
public sealed class ManabaseControllerDownloadTests
{
    [Fact]
    public async Task Download_ValidDeck_ReturnsFileResultWithTextContentTypeAndTimestampedName()
    {
        var service = new StubService(CasualReport());
        var controller = BuildController(service);

        var result = await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            DeckName = "Test Deck",
        });

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/plain; charset=utf-8", file.ContentType);

        // Filename must use the sanitized deck-name prefix plus the timestamped suffix.
        Assert.Matches(new Regex(@"^test-deck-manabase-\d{8}-\d{6}\.txt$"), file.FileDownloadName);

        // Content must decode to a string containing the report summary
        string text = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains(CasualReport().Summary, text);
    }

    [Fact]
    public async Task Download_UsesSanitizedDeckNamePrefix_WhenDeckNamePresent()
    {
        var service = new StubService(CasualReport());
        var controller = BuildController(service);

        var result = await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            DeckName = "  T3st Deck!!! With Extra Words Past Forty  ",
        });

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Matches(new Regex(@"^t3st-deck-with-extra-words-past-forty-manabase-\d{8}-\d{6}\.txt$"), file.FileDownloadName);
    }

    [Fact]
    public async Task Download_BlankDeckName_KeepsCurrentDefaultFileName()
    {
        var service = new StubService(CasualReport());
        var controller = BuildController(service);

        var result = await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            DeckName = "   ",
        });

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Matches(new Regex(@"^manabase-analysis-\d{8}-\d{6}\.txt$"), file.FileDownloadName);
    }

    [Fact]
    public async Task Download_IncludesVerdictAndBudgetText()
    {
        var verdict = new ManabaseVerdict
        {
            HasIssues = true,
            Headline = "Reading the deck",
            Lines = new[] { "Issue line from test" },
            NoIssueReason = string.Empty,
        };
        var budget = new ManabaseRampDrawBudget
        {
            RampCount = 7,
            DrawCount = 9,
            OverlapCount = 1,
            Threshold = 4,
            ThresholdSource = ManabaseRampDrawThresholdSource.CommanderManaValue,
            TargetRamp = 12,
            TargetDraw = 12,
            IsBalanced = false,
            IsRampLight = true,
            IsRampHeavy = false,
            RampShort = 5,
            IsDrawLight = true,
            DrawShort = 3,
        };
        var service = new StubService(CasualReport(), verdict, budget, showPlainLanguage: true);
        var controller = BuildController(service);

        var result = await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            DeckName = "Test Deck",
        });

        var file = Assert.IsType<FileContentResult>(result);
        string text = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("Reading the deck", text);
        Assert.Contains("Issue line from test", text);
        Assert.Contains("Ramp/draw:", text);
    }

    [Fact]
    public async Task Download_FlagOff_ArtifactDoesNotContainUntappedSourcesSection()
    {
        // TAP-04 byte-identity: flag OFF → no tap block in the artifact.
        var service = new StubService(ReportWithTapAnalysis(), showTapAnalyzer: false);
        var controller = BuildController(service);

        var result = await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            DeckName = "Test Deck",
        });

        var file = Assert.IsType<FileContentResult>(result);
        string text = Encoding.UTF8.GetString(file.FileContents);
        Assert.DoesNotContain("Untapped Sources:", text);
    }

    [Fact]
    public async Task Download_FlagOn_ArtifactContainsUntappedSourcesAndTurn1Sections()
    {
        // TAP-04: flag ON → tap block present (RED until 75-02 appends the block + 75-03 wires the
        // controller to pass tap).
        var service = new StubService(ReportWithTapAnalysis(), showTapAnalyzer: true);
        var controller = BuildController(service);

        var result = await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            DeckName = "Test Deck",
        });

        var file = Assert.IsType<FileContentResult>(result);
        string text = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("Untapped Sources:", text);
        Assert.Contains("Turn-1 untapped availability:", text);
    }

    [Fact]
    public async Task Download_InvalidEnumValues_CoercedToDefaults()
    {
        // Out-of-range Mode/CommanderImportance must produce a file, not a 500 — mirrors
        // the analyze action's MEDIUM-1 guard.
        var service = new StubService(CasualReport());
        var controller = BuildController(service);

        var result = await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Command Tower",
            Mode = (ManabaseMode)999,
            CommanderImportance = (CommanderImportance)(-7),
        });

        // A file must still come back — not a view with an error
        Assert.IsType<FileContentResult>(result);
        Assert.NotNull(service.LastOptions);
        Assert.Equal(ManabaseMode.Casual, service.LastOptions!.Mode);
        Assert.Equal(CommanderImportance.Standard, service.LastOptions.CommanderImportance);
    }

    [Fact]
    public async Task Download_ServiceThrowsInvalidOperation_RendersViewWithErrorMessage()
    {
        var service = new ThrowingService(new InvalidOperationException("Deck parse failed."));
        var controller = BuildController(service);

        var result = await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "bad input",
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ManabaseViewModel>(view.Model);
        Assert.Equal("Deck parse failed.", model.ErrorMessage);
    }

    [Fact]
    public async Task Download_ServiceThrowsHttpRequestException_RendersUpstreamErrorView()
    {
        var service = new ThrowingService(new HttpRequestException("upstream error"));
        var controller = BuildController(service);

        var result = await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ManabaseViewModel>(view.Model);
        // Error message is non-null and non-empty (upstream error builder produces copy)
        Assert.False(string.IsNullOrWhiteSpace(model.ErrorMessage));
    }

    [Fact]
    public async Task Download_CommanderSelectionRequired_RendersViewInsteadOfNullRef()
    {
        var controller = BuildController(new SelectionRequiredService());

        var result = await controller.Download(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Academy Rector",
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ManabaseViewModel>(view.Model);
        Assert.Null(model.Report);
        // Selection is a routine prompt, not an error — no alert banner; the picker is the message.
        Assert.Null(model.ErrorMessage);
        Assert.True(model.CommanderSelectionRequired);
        Assert.Equal(new[] { "Winota, Joiner of Forces" }, model.CommanderChoices);
    }

    [Fact]
    public async Task Manabase_PostResultView_RendersPromptDownloadMarkerOnDownloadButton()
    {
        var service = new StubService(CasualReport());
        var controller = BuildController(service);

        var result = await controller.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            DeckName = "Test Deck",
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ManabaseViewModel>(view.Model);

        string html = await RenderManabaseViewAsync(model);

        Assert.Contains("manabase-download-button", html, StringComparison.Ordinal);
        Assert.Contains("data-prompt-download-submit", html, StringComparison.Ordinal);
    }

    // --- helpers -------------------------------------------------------------

    private static ManabaseController BuildController(IManabaseAnalysisService service)
    {
        var controller = new ManabaseController(
            service,
            new StubCardSearchService(),
            new FakeFeatureFlagCache(),
            new FakeBracketClassificationService(),
            NullLogger<ManabaseController>.Instance,
            new PacketSessionCache())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    private static ManabaseReport CasualReport() => new()
    {
        ActualLands = 36,
        TargetLands = 37.0,
        ColorFindings = Array.Empty<ColorSourceFinding>(),
        Mode = ManabaseMode.Casual,
        Summary = "Mana base looks fine for this test.",
    };

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
        services.AddSingleton<DeckFlow.Web.Services.FeatureFlags.IFeatureFlagCache>(new FakeFeatureFlagCache());
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

    /// <summary>Fake service that returns a canned report and records the last options used.</summary>
    private sealed class StubService : IManabaseAnalysisService
    {
        private readonly ManabaseReport _report;
        private readonly ManabaseVerdict? _verdict;
        private readonly ManabaseRampDrawBudget? _budget;
        private readonly bool _showPlainLanguage;
        private readonly bool _showTapAnalyzer;

        public StubService(
            ManabaseReport report,
            ManabaseVerdict? verdict = null,
            ManabaseRampDrawBudget? budget = null,
            bool showPlainLanguage = false,
            bool showTapAnalyzer = false)
        {
            _report = report;
            _verdict = verdict;
            _budget = budget;
            _showPlainLanguage = showPlainLanguage;
            _showTapAnalyzer = showTapAnalyzer;
        }

        public ManabaseAnalysisOptions? LastOptions { get; private set; }

        public Task<ManabaseAnalysisResult> AnalyzeAsync(
            string deckSource,
            string? deckName,
            ManabaseAnalysisOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options ?? new ManabaseAnalysisOptions();
            return Task.FromResult(CreateResult(
                _report, "1 cards · 36 lands", "prompt", Array.Empty<CostSuggestion>(),
                _verdict, _budget, _showPlainLanguage, _showTapAnalyzer));
        }

        public Task<ManabaseLoadResult> LoadAsync(
            string deckSource,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ManabaseLoadResult(
                "1 cards · 36 lands", Array.Empty<string>(), null, Array.Empty<CostSuggestion>()));
    }

    /// <summary>Fake service that always throws the given exception from AnalyzeAsync.</summary>
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
                "100 cards · 36 lands", Array.Empty<string>(), null, Array.Empty<CostSuggestion>()));
    }

    private sealed class StubTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = string.Empty;
    }

    private static ManabaseAnalysisResult CreateResult(
        ManabaseReport report,
        string inputSummary,
        string chatGptSwapPrompt,
        IReadOnlyList<CostSuggestion> suggestions,
        ManabaseVerdict? verdict,
        ManabaseRampDrawBudget? budget,
        bool showPlainLanguage,
        bool showTapAnalyzer = false)
    {
        ConstructorInfo constructor = typeof(ManabaseAnalysisResult).GetConstructors().Single();
        object?[] args = constructor.GetParameters().Length == 9
            ? new object?[] { report, inputSummary, Array.Empty<string>(), null, chatGptSwapPrompt, suggestions, verdict, budget, showPlainLanguage }
            : new object?[] { report, inputSummary, Array.Empty<string>(), null, chatGptSwapPrompt, suggestions };
        var result = (ManabaseAnalysisResult)constructor.Invoke(args);
        // ShowTapAnalyzer is an additive init-only property (not a ctor param) — set via `with`.
        return result with { ShowTapAnalyzer = showTapAnalyzer };
    }

    /// <summary>A report carrying populated tap analysis, used by the download flag-gating facts.</summary>
    private static ManabaseReport ReportWithTapAnalysis() => new()
    {
        ActualLands = 36,
        TargetLands = 37.0,
        ColorFindings = new List<ColorSourceFinding>
        {
            new()
            {
                Color = ManaColor.White,
                ActualSources = 20.0,
                RequiredSources = 18,
                DrivingSpell = "Swords to Plowshares",
                UntappedSources = 16.0,
            },
            new()
            {
                Color = ManaColor.Blue,
                ActualSources = 16.0,
                RequiredSources = 14,
                DrivingSpell = "Counterspell",
                UntappedSources = 13.5,
            },
        },
        Mode = ManabaseMode.Casual,
        Summary = "Mana base looks fine for this test.",
        TapAnalysis = new ManabaseTapAnalysis
        {
            OverallUntappedPercent = 82,
            UntappedSources = 29.5,
            TotalSources = 36.0,
            Turn1UntappedPercent = 76,
            ColorTap = new Dictionary<ManaColor, ColorTapFinding>
            {
                [ManaColor.White] = new() { UntappedSources = 16.0, TotalSources = 20.0, UntappedPercent = 80 },
                [ManaColor.Blue] = new() { UntappedSources = 13.5, TotalSources = 16.0, UntappedPercent = 84 },
            },
        },
    };
}
