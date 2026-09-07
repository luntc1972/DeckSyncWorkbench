using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeckFlow.Core.Manabase;
using DeckFlow.Web.Controllers;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Manabase;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Verifies the mode + commander-importance selections flow from the form through
/// <see cref="ManabaseController"/> into the analysis service, and that the resulting view model
/// gates the castability table correctly (Casual shows it, cEDH hides it).
/// </summary>
public sealed class ManabaseControllerModeTests
{
    [Fact]
    public async Task Post_ThreadsModeAndImportance_IntoTheService()
    {
        var fake = new CapturingService(CasualReport());
        var controller = BuildController(fake);

        await controller.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            Mode = ManabaseMode.Cedh,
            CommanderImportance = CommanderImportance.Central,
        });

        Assert.NotNull(fake.LastOptions);
        Assert.Equal(ManabaseMode.Cedh, fake.LastOptions!.Mode);
        Assert.Equal(CommanderImportance.Central, fake.LastOptions.CommanderImportance);
    }

    [Fact]
    public async Task Post_ThreadsSelectedCommander_IntoTheService()
    {
        var fake = new CapturingService(CasualReport());
        var controller = BuildController(fake);

        await controller.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Winota, Joiner of Forces",
            SelectedCommander = "Winota, Joiner of Forces",
        });

        Assert.NotNull(fake.LastOptions);
        Assert.Equal("Winota, Joiner of Forces", fake.LastOptions!.SelectedCommander);
    }

    [Fact]
    public async Task Post_DefaultRequest_IsCasualStandard()
    {
        var fake = new CapturingService(CasualReport());
        var controller = BuildController(fake);

        // A bare request (mode/importance unset) must default to Casual / Standard.
        await controller.Manabase(new ManabaseRequest { DeckText = "1 Sol Ring", DeckInputSource = DeckInputSource.PasteText });

        Assert.NotNull(fake.LastOptions);
        Assert.Equal(ManabaseMode.Casual, fake.LastOptions!.Mode);
        Assert.Equal(CommanderImportance.Standard, fake.LastOptions.CommanderImportance);
    }

    [Fact]
    public async Task Post_InvalidMode_NormalizesToCasual_AndWritesBackOntoRequest()
    {
        // MEDIUM-1: a hand-crafted post can carry an out-of-range enum int. The controller must
        // coerce it to the default, run the analyzer with the valid value, AND write it back so the
        // re-rendered view selects the correct radio.
        var fake = new CapturingService(CasualReport());
        var controller = BuildController(fake);

        var request = new ManabaseRequest
        {
            DeckText = "1 Sol Ring",
            DeckInputSource = DeckInputSource.PasteText,
            Mode = (ManabaseMode)999,
            CommanderImportance = (CommanderImportance)(-7),
        };

        var result = await controller.Manabase(request);

        // The analyzer ran with normalized values.
        Assert.NotNull(fake.LastOptions);
        Assert.Equal(ManabaseMode.Casual, fake.LastOptions!.Mode);
        Assert.Equal(CommanderImportance.Standard, fake.LastOptions.CommanderImportance);

        // The request object was mutated so the view re-renders the correct radio.
        Assert.Equal(ManabaseMode.Casual, request.Mode);
        Assert.Equal(CommanderImportance.Standard, request.CommanderImportance);
        Assert.Equal(ManabaseMode.Casual, ModelOf(result).Request.Mode);
    }

    [Fact]
    public async Task Post_CasualReport_ShowsCastability()
    {
        var fake = new CapturingService(CasualReport());
        var controller = BuildController(fake);

        var result = await controller.Manabase(new ManabaseRequest { DeckText = "x", DeckInputSource = DeckInputSource.PasteText });

        var model = ModelOf(result);
        Assert.True(model.ShowCastability);
    }

    [Fact]
    public async Task Post_CedhReport_HidesCastability()
    {
        // Even though the report carries castability rows, cEDH mode hides the table (v1).
        var fake = new CapturingService(CedhReport());
        var controller = BuildController(fake);

        var result = await controller.Manabase(new ManabaseRequest
        {
            DeckText = "x",
            DeckInputSource = DeckInputSource.PasteText,
            Mode = ManabaseMode.Cedh,
        });

        var model = ModelOf(result);
        Assert.Equal(ManabaseMode.Cedh, model.Report!.Mode);
        Assert.False(model.ShowCastability);
    }

    [Fact]
    public async Task Post_CopiesPlainLanguageFieldsOntoViewModel()
    {
        var verdict = new ManabaseVerdict
        {
            HasIssues = true,
            Headline = "Reading the deck",
            Lines = new[] { "Issue line" },
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
        var fake = new CapturingService(CasualReport(), verdict, budget, showPlainLanguage: true);
        var controller = BuildController(fake);

        var result = await controller.Manabase(new ManabaseRequest
        {
            DeckText = "x",
            DeckInputSource = DeckInputSource.PasteText,
        });

        var model = ModelOf(result);
        Assert.Same(verdict, GetOptionalProperty<ManabaseVerdict>(model, "PlainLanguageVerdict"));
        Assert.Same(budget, GetOptionalProperty<ManabaseRampDrawBudget>(model, "RampDrawBudget"));
        Assert.True(GetBoolProperty(model, "ShowPlainLanguage"));
    }

    [Fact]
    public async Task Post_CommanderSelectionRequired_RendersViewWithoutNullRef_AndStoresChoices()
    {
        var fake = new SelectionRequiredService();
        var controller = BuildController(fake);

        var result = await controller.Manabase(new ManabaseRequest
        {
            DeckText = "x",
            DeckInputSource = DeckInputSource.PasteText,
        });

        var model = ModelOf(result);
        Assert.Null(model.Report);
        // Selection is a routine prompt, not an error — no alert banner; the picker is the message.
        Assert.Null(model.ErrorMessage);
        Assert.True(model.CommanderSelectionRequired);
        Assert.Equal(new[] { "Winota, Joiner of Forces" }, model.CommanderChoices);
    }

    [Fact]
    public async Task CommanderSearch_ReturnsJsonCommanderNames()
    {
        var service = new CapturingService(CasualReport());
        var search = new StubCardSearchService("Winota, Joiner of Forces", "Winota's Friend");
        var controller = BuildController(service, search);

        var result = await controller.CommanderSearch("wino");

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal("wino", search.LastCommanderQuery);
        Assert.Equal(
            new[] { "Winota, Joiner of Forces", "Winota's Friend" },
            Assert.IsAssignableFrom<IReadOnlyList<string>>(json.Value));
    }

    // --- helpers -------------------------------------------------------------

    private static ManabaseController BuildController(
        IManabaseAnalysisService service,
        StubCardSearchService? cardSearchService = null)
    {
        var controller = new ManabaseController(
            service,
            cardSearchService ?? new StubCardSearchService(),
            new FakeFeatureFlagCache(),
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

    private static ManabaseReport CasualReport() => BuildReport(ManabaseMode.Casual);

    private static ManabaseReport CedhReport() => BuildReport(ManabaseMode.Cedh);

    private static ManabaseReport BuildReport(ManabaseMode mode) => new()
    {
        ActualLands = 36,
        TargetLands = 37.0,
        ColorFindings = Array.Empty<ColorSourceFinding>(),
        Mode = mode,
        Castability = new[]
        {
            new CardCastability { Name = "Counterspell", ManaValue = 2, OnCurveTurn = 2, CastPercent = 62, LimitingFactor = "color:U" },
        },
        Summary = "ok",
    };

    private sealed class CapturingService : IManabaseAnalysisService
    {
        private readonly ManabaseReport _report;
        private readonly ManabaseVerdict? _verdict;
        private readonly ManabaseRampDrawBudget? _budget;
        private readonly bool _showPlainLanguage;

        public CapturingService(
            ManabaseReport report,
            ManabaseVerdict? verdict = null,
            ManabaseRampDrawBudget? budget = null,
            bool showPlainLanguage = false)
        {
            _report = report;
            _verdict = verdict;
            _budget = budget;
            _showPlainLanguage = showPlainLanguage;
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
                _verdict, _budget, _showPlainLanguage));
        }

        public Task<ManabaseLoadResult> LoadAsync(
            string deckSource,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ManabaseLoadResult(
                "1 cards · 36 lands", Array.Empty<string>(), null, Array.Empty<CostSuggestion>()));
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

    private static ManabaseAnalysisResult CreateResult(
        ManabaseReport report,
        string inputSummary,
        string chatGptSwapPrompt,
        IReadOnlyList<CostSuggestion> suggestions,
        ManabaseVerdict? verdict,
        ManabaseRampDrawBudget? budget,
        bool showPlainLanguage)
    {
        ConstructorInfo constructor = typeof(ManabaseAnalysisResult).GetConstructors().Single();
        object?[] args = constructor.GetParameters().Length == 9
            ? new object?[] { report, inputSummary, Array.Empty<string>(), null, chatGptSwapPrompt, suggestions, verdict, budget, showPlainLanguage }
            : new object?[] { report, inputSummary, Array.Empty<string>(), null, chatGptSwapPrompt, suggestions };
        return (ManabaseAnalysisResult)constructor.Invoke(args);
    }

    private static T? GetOptionalProperty<T>(object target, string name)
        where T : class
    {
        PropertyInfo property = target.GetType().GetProperty(name)
            ?? throw new Xunit.Sdk.XunitException($"{target.GetType().Name}.{name} property missing.");
        return property.GetValue(target) as T;
    }

    private static bool GetBoolProperty(object target, string name)
    {
        PropertyInfo property = target.GetType().GetProperty(name)
            ?? throw new Xunit.Sdk.XunitException($"{target.GetType().Name}.{name} property missing.");
        return (bool)(property.GetValue(target) ?? false);
    }
}
