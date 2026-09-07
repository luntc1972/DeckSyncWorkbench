using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeckFlow.Core.Models;
using DeckFlow.Core.Modular;
using DeckFlow.Web.Controllers;
using DeckFlow.Web.Infrastructure;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Modular;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Tests for <see cref="DeckModulesController"/> covering the empty landing page, the
/// import/compile/export JSON contracts against a fake page service, same-origin enforcement,
/// and the feature-flag gate wiring for every action.
/// </summary>
public sealed class DeckModulesControllerTests
{
    [Fact]
    public void Index_ReturnsViewWithDeckModulesTabActive()
    {
        var controller = CreateController(new FakeDeckModulesPageService());

        var result = controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("DeckModules", view.ViewName);
        var model = Assert.IsType<DeckModulesIndexViewModel>(view.Model);
        Assert.Equal(DeckPageTab.DeckModules, model.ActiveTab);
    }

    [Fact]
    public async Task Import_ReturnsBadRequest_WhenRequestBodyIsNull()
    {
        var controller = CreateController(new FakeDeckModulesPageService());

        var result = await controller.Import(null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Import_ReturnsForbidden_WhenCrossOrigin()
    {
        var service = new FakeDeckModulesPageService();
        var controller = CreateController(service);
        controller.Request.Host = new HostString("deckflow.test");
        controller.Request.Headers.Origin = "https://evil.test";

        var result = await controller.Import(new DeckModulesImportRequest { ActiveSource = DeckInputSource.PasteText, PasteText = "1 Sol Ring" }, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        Assert.Null(service.LastImportRequest);
    }

    [Fact]
    public async Task Import_ReturnsOkWithViewModel_OnSuccess()
    {
        var viewModel = new DeckModulesViewModel
        {
            BaselineToken = "token",
            CommandZone = new[] { CreateEntry("Commander Card", "commander") },
            BaselineMainboardEntries = new[] { CreateEntry("Sol Ring", "mainboard") },
        };
        var service = new FakeDeckModulesPageService
        {
            ImportResult = DeckModulesServiceResult<DeckModulesViewModel>.Success(viewModel),
        };
        var controller = CreateController(service);
        var request = new DeckModulesImportRequest { ActiveSource = DeckInputSource.PasteText, PasteText = "1 Sol Ring" };

        var result = await controller.Import(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(viewModel, ok.Value);
        Assert.Same(request, service.LastImportRequest);
    }

    [Fact]
    public async Task Import_ReturnsBadRequestWithMessage_OnFailure()
    {
        var service = new FakeDeckModulesPageService
        {
            ImportResult = DeckModulesServiceResult<DeckModulesViewModel>.Failure("Pasted decklist text is required."),
        };
        var controller = CreateController(service);

        var result = await controller.Import(new DeckModulesImportRequest { ActiveSource = DeckInputSource.PasteText, PasteText = string.Empty }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Pasted decklist text is required.", GetMessage(badRequest.Value));
    }

    [Fact]
    public void Compile_ReturnsBadRequest_WhenRequestBodyIsNull()
    {
        var controller = CreateController(new FakeDeckModulesPageService());

        var result = controller.Compile(null);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void Compile_ReturnsForbidden_WhenCrossOrigin()
    {
        var service = new FakeDeckModulesPageService();
        var controller = CreateController(service);
        controller.Request.Host = new HostString("deckflow.test");
        controller.Request.Headers.Origin = "https://evil.test";

        var result = controller.Compile(CreateCompilationRequest());

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        Assert.Equal(0, service.CompileCallCount);
    }

    [Fact]
    public void Compile_ReturnsOkWithDiagnostics_OnSuccess()
    {
        var compilation = CreateCompilationViewModel(isStructurallyValid: true);
        var service = new FakeDeckModulesPageService
        {
            CompileResult = DeckModulesServiceResult<DeckModulesCompilationViewModel>.Success(compilation),
        };
        var controller = CreateController(service);
        var request = CreateCompilationRequest();

        var result = controller.Compile(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(compilation, ok.Value);
        Assert.Same(request, service.LastCompileRequest);
        Assert.Equal(1, service.CompileCallCount);
    }

    [Fact]
    public void Compile_ReturnsBadRequestWithMessage_OnFailure()
    {
        var service = new FakeDeckModulesPageService
        {
            CompileResult = DeckModulesServiceResult<DeckModulesCompilationViewModel>.Failure("The imported baseline no longer matches the submitted deck."),
        };
        var controller = CreateController(service);

        var result = controller.Compile(CreateCompilationRequest());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("The imported baseline no longer matches the submitted deck.", GetMessage(badRequest.Value));
    }

    [Fact]
    public void Export_ReturnsBadRequest_WhenRequestBodyIsNull()
    {
        var controller = CreateController(new FakeDeckModulesPageService());

        var result = controller.Export(null);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void Export_ReturnsForbidden_WhenCrossOrigin()
    {
        var service = new FakeDeckModulesPageService();
        var controller = CreateController(service);
        controller.Request.Host = new HostString("deckflow.test");
        controller.Request.Headers.Origin = "https://evil.test";

        var result = controller.Export(CreateCompilationRequest());

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        Assert.Equal(0, service.CompileCallCount);
    }

    [Fact]
    public void Export_ReturnsBadRequest_OnCompileFailure()
    {
        var service = new FakeDeckModulesPageService
        {
            CompileResult = DeckModulesServiceResult<DeckModulesCompilationViewModel>.Failure("Your Deck Modules session has expired or is invalid. Re-import the deck to continue."),
        };
        var controller = CreateController(service);

        var result = controller.Export(CreateCompilationRequest());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Your Deck Modules session has expired or is invalid. Re-import the deck to continue.", GetMessage(badRequest.Value));
    }

    [Fact]
    public void Export_ReturnsBadRequest_WhenNotStructurallyValid()
    {
        var compilation = CreateCompilationViewModel(isStructurallyValid: false);
        var service = new FakeDeckModulesPageService
        {
            CompileResult = DeckModulesServiceResult<DeckModulesCompilationViewModel>.Success(compilation),
        };
        var controller = CreateController(service);

        var result = controller.Export(CreateCompilationRequest());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Resolve the compilation diagnostics before exporting this configuration.", GetMessage(badRequest.Value));
    }

    [Fact]
    public void Export_ReturnsUtf8TextFile_WithCompleteListAndInOutResetSections_OnSuccess()
    {
        var compilation = CreateCompilationViewModel(isStructurallyValid: true);
        var service = new FakeDeckModulesPageService
        {
            CompileResult = DeckModulesServiceResult<DeckModulesCompilationViewModel>.Success(compilation),
        };
        var controller = CreateController(service);

        var result = controller.Export(CreateCompilationRequest());

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/plain; charset=utf-8", file.ContentType);
        Assert.EndsWith(".txt", file.FileDownloadName);

        var text = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("Commander Card", text, System.StringComparison.Ordinal);
        Assert.Contains("Sol Ring", text, System.StringComparison.Ordinal);
        Assert.Contains("== Command Zone ==", text, System.StringComparison.Ordinal);
        Assert.Contains("== Mainboard ==", text, System.StringComparison.Ordinal);
        Assert.Contains("IN -", text, System.StringComparison.Ordinal);
        Assert.Contains("OUT -", text, System.StringComparison.Ordinal);
        Assert.Contains("RESET -", text, System.StringComparison.Ordinal);
        Assert.Contains("+1 New Card", text, System.StringComparison.Ordinal);
        Assert.Contains("-1 Old Card", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Every_deck_modules_action_is_gated_by_the_deck_modules_flag()
    {
        var actions = GetDeckModulesActions();

        Assert.Equal(new[] { "Analyze", "Compare", "Compile", "Export", "Import", "Index" }, actions.Select(static action => action.Name).OrderBy(static name => name).ToArray());

        foreach (var method in actions)
        {
            var gate = method.GetCustomAttribute<FeatureFlagGateAttribute>();

            Assert.NotNull(gate);
            Assert.Equal("tool.deck-modules.enabled", gate!.Key);
        }
    }

    private static MethodInfo[] GetDeckModulesActions() =>
        typeof(DeckModulesController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
            .ToArray();

    private static DeckModulesController CreateController(IDeckModulesPageService service) =>
        CreateController(service, new FakeConfigurationAnalysisService());

    private static DeckModulesController CreateController(
        IDeckModulesPageService service,
        IConfigurationAnalysisService analysisService) =>
        new(service, NullLogger<DeckModulesController>.Instance, analysisService, new PacketSessionCache(), new ConfigurationDeltaService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

    private static DeckEntry CreateEntry(string name, string board, int quantity = 1) => new()
    {
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        Board = board,
        Quantity = quantity,
    };

    private static DeckModulesCompilationRequest CreateCompilationRequest() => new()
    {
        BaselineToken = "token",
        CommandZone = new[] { CreateEntry("Commander Card", "commander") },
        BaselineMainboardEntries = new[] { CreateEntry("Old Card", "mainboard") },
        CoreEntries = new[] { CreateEntry("Core Card", "mainboard") },
        Alternatives =
        [
            new DeckModulesAlternativeInput
            {
                Id = "a",
                Name = "Alternative A",
                Profile = DeckModulesProfile.Casual,
                PlayPlan = "Play things.",
                MainboardEntries = new[] { CreateEntry("New Card", "mainboard") },
            },
            new DeckModulesAlternativeInput
            {
                Id = "b",
                Name = "Alternative B",
                Profile = DeckModulesProfile.Casual,
                PlayPlan = "Play other things.",
                MainboardEntries = new[] { CreateEntry("New Card", "mainboard") },
            },
        ],
        SelectedAlternativeId = "a",
    };

    private static DeckModulesCompilationViewModel CreateCompilationViewModel(bool isStructurallyValid) => new()
    {
        IsStructurallyValid = isStructurallyValid,
        IsVerifiedLegal = false,
        Diagnostics = isStructurallyValid
            ? System.Array.Empty<ModularDeckDiagnostic>()
            : new[]
            {
                new ModularDeckDiagnostic
                {
                    Rule = ModularDeckDiagnosticRule.TotalCardCount,
                    AffectedIdentifiers = new[] { "100" },
                },
            },
        SelectedStrategyId = "a",
        SelectedStrategyName = "Alternative A",
        SelectedManaSupportModuleName = string.Empty,
        CommandZoneEntries = new[] { CreateEntry("Commander Card", "commander") },
        MainboardEntries = new[] { CreateEntry("Sol Ring", "mainboard") },
        Entries = new[] { CreateEntry("Commander Card", "commander"), CreateEntry("Sol Ring", "mainboard") },
        TotalCardCount = 100,
        SwapPlan = new ModularDeckSwapPlan
        {
            ToAdd = new[]
            {
                new ModularDeckSwapEntry { Action = ModularDeckSwapAction.Add, Name = "New Card", NormalizedName = "new card", Quantity = 1 },
            },
            ToRemove = new[]
            {
                new ModularDeckSwapEntry { Action = ModularDeckSwapAction.Remove, Name = "Old Card", NormalizedName = "old card", Quantity = 1 },
            },
            ToReset = new[]
            {
                new ModularDeckSwapEntry { Action = ModularDeckSwapAction.Remove, Name = "New Card", NormalizedName = "new card", Quantity = 1 },
                new ModularDeckSwapEntry { Action = ModularDeckSwapAction.Add, Name = "Old Card", NormalizedName = "old card", Quantity = 1 },
            },
        },
    };

    private static string? GetMessage(object? value)
    {
        var property = value?.GetType().GetProperty("message");
        return property?.GetValue(value) as string;
    }

    private sealed class FakeDeckModulesPageService : IDeckModulesPageService
    {
        public DeckModulesServiceResult<DeckModulesViewModel>? ImportResult { get; set; }

        public DeckModulesImportRequest? LastImportRequest { get; private set; }

        public DeckModulesServiceResult<DeckModulesCompilationViewModel>? CompileResult { get; set; }

        public DeckModulesCompilationRequest? LastCompileRequest { get; private set; }

        public int CompileCallCount { get; private set; }

        public Task<DeckModulesServiceResult<DeckModulesViewModel>> ImportAsync(
            DeckModulesImportRequest request,
            CancellationToken cancellationToken = default)
        {
            LastImportRequest = request;
            return Task.FromResult(ImportResult ?? DeckModulesServiceResult<DeckModulesViewModel>.Failure("not configured"));
        }

        public DeckModulesServiceResult<DeckModulesCompilationViewModel> Compile(DeckModulesCompilationRequest request)
        {
            LastCompileRequest = request;
            CompileCallCount++;
            return CompileResult ?? DeckModulesServiceResult<DeckModulesCompilationViewModel>.Failure("not configured");
        }
    }

    private sealed class FakeConfigurationAnalysisService : IConfigurationAnalysisService
    {
        public Task<DeckModulesServiceResult<ConfigurationAnalysisResult>> AnalyzeAsync(
            ConfigurationAnalysisRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DeckModulesServiceResult<ConfigurationAnalysisResult>.Failure("not configured"));
    }
}
