using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MtgDeckStudio.Web.Controllers;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Core.Reporting;
using MtgDeckStudio.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class DeckControllerTests
{
    [Fact]
    public void BuildNoSuggestionsMessage_UsesCacheRefreshNotice_WhenNoDecks()
    {
        var totals = new CardDeckTotals(0, new Dictionary<string, int>());
        var message = CategorySuggestionMessageBuilder.BuildNoSuggestionsMessage("Guardian Project", totals);

        Assert.Equal("No card categories for Guardian Project have been observed in the cached data yet. Run Show Categories again to refresh the cache.", message);
    }

    [Fact]
    public void BuildNoSuggestionsMessage_UsesGeneralMessage_WhenDecksExist()
    {
        var totals = new CardDeckTotals(5, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["mainboard"] = 5
        });
        var message = CategorySuggestionMessageBuilder.BuildNoSuggestionsMessage("Guardian Project", totals);

        Assert.Equal("No category suggestions were found for Guardian Project. You can run the lookup again to retry the live Archidekt and EDHREC checks.", message);
    }

    [Fact]
    public async Task CardSearch_ReturnsServiceUnavailable_WhenScryfallFails()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Scryfall search returned HTTP 503.", null, HttpStatusCode.ServiceUnavailable)),
            new FakeCardLookupService(),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new FakeChatGptDeckPacketService(),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.CardSearch("bello");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        var payload = objectResult.Value!;
        var message = payload.GetType().GetProperty("Message")?.GetValue(payload) as string;
        Assert.Equal("Scryfall returned HTTP 503. Try again shortly.", message);
    }

    [Fact]
    public async Task CardLookup_ReturnsValidationError_WhenCardListMissing()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new FakeCardLookupService(),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new FakeChatGptDeckPacketService(),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.CardLookup(new CardLookupRequest());

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CardLookupViewModel>(view.Model);
        Assert.Equal("A card list is required.", model.ErrorMessage);
    }

    [Fact]
    public async Task CardLookup_ReturnsUserFacingError_WhenScryfallFails()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new ThrowingCardLookupService(new HttpRequestException("Scryfall search returned HTTP 503.", null, HttpStatusCode.ServiceUnavailable)),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new FakeChatGptDeckPacketService(),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.CardLookup(new CardLookupRequest
        {
            CardList = "Sol Ring"
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CardLookupViewModel>(view.Model);
        Assert.Equal("Scryfall returned HTTP 503. Try again shortly.", model.ErrorMessage);
    }

    [Fact]
    public async Task CardLookup_ReturnsValidationMessage_WhenTooManyLinesSubmitted()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new ThrowingCardLookupService(new InvalidOperationException("Please verify 100 non-empty lines or fewer per submission.")),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new FakeChatGptDeckPacketService(),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.CardLookup(new CardLookupRequest
        {
            CardList = "Sol Ring"
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CardLookupViewModel>(view.Model);
        Assert.Equal("Please verify 100 non-empty lines or fewer per submission.", model.ErrorMessage);
    }

    [Fact]
    public async Task DownloadCardLookup_ReturnsTextFile_WhenVerificationSucceeds()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new SuccessfulCardLookupService(),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new FakeChatGptDeckPacketService(),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.DownloadCardLookup(new CardLookupRequest
        {
            CardList = "Sol Ring"
        });

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/plain; charset=utf-8", fileResult.ContentType);
        var text = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);
        Assert.Contains("Verified Cards", text);
        Assert.Contains("Sol Ring", text);
    }

    [Fact]
    public async Task MechanicLookup_ReturnsValidationError_WhenMechanicMissing()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new FakeCardLookupService(),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new FakeChatGptDeckPacketService(),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.MechanicLookup(new MechanicLookupRequest());

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<MechanicLookupViewModel>(view.Model);
        Assert.Equal("A mechanic name is required.", model.ErrorMessage);
    }

    [Fact]
    public async Task MechanicLookup_ReturnsRules_WhenMechanicFound()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new FakeCardLookupService(),
            new SuccessfulMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new FakeChatGptDeckPacketService(),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.MechanicLookup(new MechanicLookupRequest
        {
            MechanicName = "Prowess"
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<MechanicLookupViewModel>(view.Model);
        Assert.Equal("Prowess", model.MechanicName);
        Assert.Equal("702.108", model.RuleReference);
        Assert.Contains("Prowess", model.RulesText);
    }

    [Fact]
    public async Task ChatGptPackets_ReturnsValidationError_WhenBracketMissingForAnalysisStep()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new FakeCardLookupService(),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new ThrowingChatGptDeckPacketService(new InvalidOperationException("Choose a target Commander bracket before generating the analysis packet.")),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.ChatGptPackets(new ChatGptDeckRequest
        {
            WorkflowStep = 2,
            DeckSource = "Commander\n1 Atraxa, Praetors' Voice",
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ChatGptDeckViewModel>(view.Model);
        Assert.Equal("Choose a target Commander bracket before generating the analysis packet.", model.ErrorMessage);
        Assert.Equal(2, model.Request.WorkflowStep);
    }

    [Fact]
    public async Task ChatGptPackets_ReturnsValidationError_WhenQuestionsMissingForAnalysisStep()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new FakeCardLookupService(),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new ThrowingChatGptDeckPacketService(new InvalidOperationException("Select at least one analysis question before generating the analysis packet.")),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.ChatGptPackets(new ChatGptDeckRequest
        {
            WorkflowStep = 2,
            DeckSource = "Commander\n1 Atraxa, Praetors' Voice",
            TargetCommanderBracket = "Upgraded"
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ChatGptDeckViewModel>(view.Model);
        Assert.Equal("Select at least one analysis question before generating the analysis packet.", model.ErrorMessage);
        Assert.Equal(2, model.Request.WorkflowStep);
    }

    [Fact]
    public async Task ChatGptPackets_ReturnsValidationError_WhenSetSourceMissingForUpgradeStep()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new FakeCardLookupService(),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new ThrowingChatGptDeckPacketService(new InvalidOperationException("Select at least one set or paste a condensed set packet override before generating the set-upgrade packet.")),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.ChatGptPackets(new ChatGptDeckRequest
        {
            WorkflowStep = 3,
            DeckSource = "Commander\n1 Atraxa, Praetors' Voice",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["consistency"],
            DeckProfileJson = "{\"game_plan\":\"midrange\"}"
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ChatGptDeckViewModel>(view.Model);
        Assert.Equal("Select at least one set or paste a condensed set packet override before generating the set-upgrade packet.", model.ErrorMessage);
        Assert.Equal(3, model.Request.WorkflowStep);
    }

    [Fact]
    public async Task ChatGptPackets_PassesMultipleSelectedQuestionsAndSetsToService()
    {
        var capturingService = new CapturingChatGptDeckPacketService();
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new FakeCardLookupService(),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            capturingService,
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var request = new ChatGptDeckRequest
        {
            WorkflowStep = 3,
            DeckSource = "Commander\n1 Atraxa, Praetors' Voice",
            TargetCommanderBracket = "Upgraded",
            SelectedAnalysisQuestions = ["consistency", "strengths-weaknesses", "budget-upgrades"],
            BudgetUpgradeAmount = "75",
            DeckProfileJson = "{\"game_plan\":\"midrange\"}",
            SelectedSetCodes = ["dsk", "fdn"]
        };

        await controller.ChatGptPackets(request);

        Assert.NotNull(capturingService.LastRequest);
        Assert.Equal(3, capturingService.LastRequest!.SelectedAnalysisQuestions.Count);
        Assert.Contains("consistency", capturingService.LastRequest.SelectedAnalysisQuestions);
        Assert.Contains("strengths-weaknesses", capturingService.LastRequest.SelectedAnalysisQuestions);
        Assert.Contains("budget-upgrades", capturingService.LastRequest.SelectedAnalysisQuestions);
        Assert.Equal(2, capturingService.LastRequest.SelectedSetCodes.Count);
        Assert.Contains("dsk", capturingService.LastRequest.SelectedSetCodes);
        Assert.Contains("fdn", capturingService.LastRequest.SelectedSetCodes);
        Assert.Equal("75", capturingService.LastRequest.BudgetUpgradeAmount);
    }

    [Fact]
    public void ChatGptDeckComparison_Get_RendersPage()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new FakeCardLookupService(),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new FakeChatGptDeckPacketService(),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance);

        var result = controller.ChatGptDeckComparison();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ChatGptDeckComparisonViewModel>(view.Model);
        Assert.Equal(DeckPageTab.ChatGptDeckComparison, model.ActiveTab);
    }

    [Fact]
    public async Task ChatGptDeckComparison_Post_ReturnsExpectedResultModel()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new FakeCardLookupService(),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new FakeChatGptDeckPacketService(),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.ChatGptDeckComparison(new ChatGptDeckComparisonRequest
        {
            WorkflowStep = 2,
            DeckABracket = "Upgraded",
            DeckASource = "Commander\n1 Atraxa, Praetors' Voice",
            DeckBBracket = "Optimized",
            DeckBSource = "Commander\n1 Tymna the Weaver"
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ChatGptDeckComparisonViewModel>(view.Model);
        Assert.Equal("comparison prompt", model.ComparisonPromptText);
        Assert.Equal("comparison follow-up prompt", model.FollowUpPromptText);
        Assert.NotNull(model.ComparisonResponse);
    }

    [Fact]
    public async Task ChatGptDeckComparison_Post_ReturnsViewWithError_WhenModelStateInvalid()
    {
        var controller = new DeckController(
            new FakeDeckSyncService(),
            new FakeDeckConvertService(),
            new ThrowingCardSearchService(new HttpRequestException("Unused")),
            new FakeCardLookupService(),
            new FakeMechanicLookupService(),
            new FakeCategorySuggestionService(),
            new FakeChatGptDeckPacketService(),
            new FakeChatGptDeckComparisonService(),
            new FakeScryfallSetService(),
            NullLogger<DeckController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.ModelState.AddModelError("DeckASource", "Required");

        var result = await controller.ChatGptDeckComparison(new ChatGptDeckComparisonRequest());

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ChatGptDeckComparisonViewModel>(view.Model);
        Assert.Equal("The comparison form contains invalid values. Review the highlighted fields and try again.", model.ErrorMessage);
    }

    private sealed class FakeDeckSyncService : IDeckSyncService
    {
        public Task<DeckSyncResult> CompareDecksAsync(DeckDiffRequest request, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class FakeDeckConvertService : IDeckConvertService
    {
        public Task<DeckConvertResult> ConvertAsync(DeckConvertRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeChatGptDeckPacketService : IChatGptDeckPacketService
    {
        public Task<ChatGptDeckPacketResult> BuildAsync(ChatGptDeckRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeChatGptDeckComparisonService : IChatGptDeckComparisonService
    {
        public Task<ChatGptDeckComparisonResult> BuildAsync(ChatGptDeckComparisonRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatGptDeckComparisonResult(
                "comparison summary",
                "deck a list",
                "deck b list",
                "deck a combos",
                "deck b combos",
                "comparison context",
                "comparison prompt",
                "comparison follow-up prompt",
                "{}",
                new ChatGptDeckComparisonResponse
                {
                    DeckAName = "Deck A",
                    DeckBName = "Deck B",
                    DeckACommander = "Atraxa, Praetors' Voice",
                    DeckBCommander = "Tymna the Weaver",
                    DeckAGameplan = "Snowball permanents.",
                    DeckBGameplan = "Interactive value.",
                    DeckABracket = "Bracket 3: Upgraded",
                    DeckBBracket = "Bracket 4: Optimized",
                    ManaConsistencyComparison = "Deck B is smoother.",
                    ComboComparison = "Deck A has the cleaner combo finish."
                },
                null,
                null));
    }

    private sealed class ThrowingChatGptDeckPacketService : IChatGptDeckPacketService
    {
        private readonly Exception _exception;

        public ThrowingChatGptDeckPacketService(Exception exception)
        {
            _exception = exception;
        }

        public Task<ChatGptDeckPacketResult> BuildAsync(ChatGptDeckRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<ChatGptDeckPacketResult>(_exception);
    }

    private sealed class CapturingChatGptDeckPacketService : IChatGptDeckPacketService
    {
        public ChatGptDeckRequest? LastRequest { get; private set; }

        public Task<ChatGptDeckPacketResult> BuildAsync(ChatGptDeckRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new ChatGptDeckPacketResult(
                "summary",
                "Test Deck | AI Deck Analysis",
                "{}",
                "reference",
                "analysis",
                "set-upgrade",
                null,
                null));
        }
    }

    private sealed class FakeScryfallSetService : IScryfallSetService
    {
        public Task<IReadOnlyList<ScryfallSetOption>> GetSetsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ScryfallSetOption>>(Array.Empty<ScryfallSetOption>());

        public Task<string> BuildSetPacketAsync(IReadOnlyList<string> setCodes, IReadOnlyList<string>? commanderColorIdentity = null, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }

    private sealed class ThrowingCardSearchService : ICardSearchService
    {
        private readonly Exception _exception;

        public ThrowingCardSearchService(Exception exception)
        {
            _exception = exception;
        }

        public Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<string>>(_exception);

        public Task<IReadOnlyList<string>> SearchCommandersAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<string>>(_exception);
    }

    private sealed class FakeCardLookupService : ICardLookupService
    {
        public Task<CardLookupResult> LookupAsync(string cardList, CancellationToken cancellationToken = default)
            => Task.FromResult(new CardLookupResult(Array.Empty<string>(), Array.Empty<string>()));
    }

    private sealed class ThrowingCardLookupService : ICardLookupService
    {
        private readonly Exception _exception;

        public ThrowingCardLookupService(Exception exception)
        {
            _exception = exception;
        }

        public Task<CardLookupResult> LookupAsync(string cardList, CancellationToken cancellationToken = default)
            => Task.FromException<CardLookupResult>(_exception);
    }

    private sealed class SuccessfulCardLookupService : ICardLookupService
    {
        public Task<CardLookupResult> LookupAsync(string cardList, CancellationToken cancellationToken = default)
            => Task.FromResult(new CardLookupResult(new[] { "Sol Ring" }, Array.Empty<string>()));
    }

    private sealed class FakeCategorySuggestionService : ICategorySuggestionService
    {
        public Task<CategorySuggestionResult> SuggestAsync(CategorySuggestionRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeMechanicLookupService : IMechanicLookupService
    {
        public Task<MechanicLookupResult> LookupAsync(string mechanicName, CancellationToken cancellationToken = default)
            => Task.FromResult(MechanicLookupResult.NotFound(mechanicName, "https://magic.wizards.com/en/rules", null));
    }

    private sealed class SuccessfulMechanicLookupService : IMechanicLookupService
    {
        public Task<MechanicLookupResult> LookupAsync(string mechanicName, CancellationToken cancellationToken = default)
            => Task.FromResult(new MechanicLookupResult(
                mechanicName,
                true,
                "Prowess",
                "702.108",
                "Exact rules section",
                "702.108. Prowess",
                "A keyword ability that causes a creature to get +1/+1 whenever its controller casts a noncreature spell.",
                "https://magic.wizards.com/en/rules",
                "https://media.wizards.com/2026/downloads/MagicCompRules%2020260227.txt"));
    }
}
