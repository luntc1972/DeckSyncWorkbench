using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Controllers.Api;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.Api;
using DeckFlow.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Verifies the JSON deck-sync controller's validation, labeling, and error handling.
/// </summary>
public sealed class DeckSyncApiControllerTests
{
    /// <summary>
    /// Rejects requests that omit the left-side deck input.
    /// </summary>
    [Fact]
    public async Task PostDiffAsync_ReturnsBadRequest_WhenMoxfieldInputMissing()
    {
        var controller = CreateController();

        var response = await controller.PostDiffAsync(new DeckSyncApiRequest
        {
            MoxfieldInputSource = DeckInputSource.PasteText,
            ArchidektInputSource = DeckInputSource.PasteText,
            ArchidektText = "1 Sol Ring"
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    /// <summary>
    /// Returns a structured response when the compare service succeeds.
    /// </summary>
    [Fact]
    public async Task PostDiffAsync_ReturnsStructuredResponse()
    {
        var controller = CreateController();

        var response = await controller.PostDiffAsync(new DeckSyncApiRequest
        {
            Direction = SyncDirection.MoxfieldToArchidekt,
            Mode = MatchMode.Loose,
            CategorySyncMode = CategorySyncMode.TargetCategories,
            MoxfieldInputSource = DeckInputSource.PasteText,
            MoxfieldText = "1 Sol Ring",
            ArchidektInputSource = DeckInputSource.PasteText,
            ArchidektText = "1 Arcane Signet"
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<DeckSyncApiResponse>(ok.Value);
        Assert.Equal("Moxfield", payload.SourceSystem);
        Assert.Equal("Archidekt", payload.TargetSystem);
        Assert.Contains("Cards to Add", payload.ReportText);
        Assert.Contains("Sol Ring", payload.DeltaText);
        Assert.Contains("Sol Ring", payload.FullImportText);
        Assert.False(string.IsNullOrWhiteSpace(payload.InstructionsText));
        Assert.Single(payload.PrintingConflicts);
    }

    /// <summary>
    /// Converts upstream HTTP failures into a user-facing site-specific message.
    /// </summary>
    [Fact]
    public async Task PostDiffAsync_ReturnsSiteSpecificMessage_WhenUpstreamRequestFails()
    {
        var controller = new DeckSyncApiController(
            new ThrowingDeckSyncService(new HttpRequestException("Archidekt returned HTTP 503.", null, System.Net.HttpStatusCode.ServiceUnavailable)),
            NullLogger<DeckSyncApiController>.Instance);
        SetSameOriginHeaders(controller);

        var response = await controller.PostDiffAsync(new DeckSyncApiRequest
        {
            Direction = SyncDirection.MoxfieldToArchidekt,
            MoxfieldInputSource = DeckInputSource.PasteText,
            MoxfieldText = "1 Sol Ring",
            ArchidektInputSource = DeckInputSource.PasteText,
            ArchidektText = "1 Arcane Signet"
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        var message = badRequest.Value?.GetType().GetProperty("Message")?.GetValue(badRequest.Value) as string;
        Assert.Equal("Archidekt returned HTTP 503. Try again shortly.", message);
    }

    /// <summary>
    /// Preserves same-system labels when both the source and target decks come from Moxfield.
    /// </summary>
    [Fact]
    public async Task PostDiffAsync_ReturnsSameSystemLabels_ForMoxfieldToMoxfield()
    {
        var controller = CreateController();

        var response = await controller.PostDiffAsync(new DeckSyncApiRequest
        {
            Direction = SyncDirection.MoxfieldToMoxfield,
            MoxfieldInputSource = DeckInputSource.PasteText,
            MoxfieldText = "1 Sol Ring",
            ArchidektInputSource = DeckInputSource.PasteText,
            ArchidektText = "1 Arcane Signet"
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<DeckSyncApiResponse>(ok.Value);
        Assert.Equal("Moxfield", payload.SourceSystem);
        Assert.Equal("Moxfield", payload.TargetSystem);
    }

    /// <summary>
    /// Rejects cross-site browser POSTs before running the sync workflow.
    /// </summary>
    [Fact]
    public async Task PostDiffAsync_ReturnsForbidden_WhenOriginIsCrossSite()
    {
        var controller = new DeckSyncApiController(new FakeDeckSyncService(), NullLogger<DeckSyncApiController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("deckflow.test");
        controller.Request.Headers.Origin = "https://evil.test";

        var response = await controller.PostDiffAsync(new DeckSyncApiRequest
        {
            MoxfieldInputSource = DeckInputSource.PasteText,
            MoxfieldText = "1 Sol Ring",
            ArchidektInputSource = DeckInputSource.PasteText,
            ArchidektText = "1 Arcane Signet"
        }, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    /// <summary>
    /// Creates a controller configured with same-origin headers for tests that exercise the happy path.
    /// </summary>
    /// <returns>Configured deck-sync API controller.</returns>
    private static DeckSyncApiController CreateController()
    {
        var controller = new DeckSyncApiController(new FakeDeckSyncService(), NullLogger<DeckSyncApiController>.Instance);
        SetSameOriginHeaders(controller);
        return controller;
    }

    /// <summary>
    /// Adds same-origin request headers to a controller test context.
    /// </summary>
    /// <param name="controller">Controller to configure.</param>
    private static void SetSameOriginHeaders(ControllerBase controller)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("deckflow.test");
        controller.Request.Headers.Origin = "https://deckflow.test";
    }

    private sealed class FakeDeckSyncService : IDeckSyncService
    {
        public Task<DeckSyncResult> CompareDecksAsync(DeckDiffRequest request, CancellationToken cancellationToken)
        {
            var diff = new DeckDiff(
                new List<DeckEntry>
                {
                    new()
                    {
                        Name = "Sol Ring",
                        NormalizedName = CardNormalizer.Normalize("Sol Ring"),
                        Quantity = 1,
                        Board = "mainboard"
                    }
                },
                Array.Empty<DeckEntry>(),
                Array.Empty<DeckEntry>(),
                new List<PrintingConflict>
                {
                    new()
                    {
                        CardName = "Guardian Project",
                        ArchidektVersion = new DeckEntry
                        {
                            Name = "Guardian Project",
                            NormalizedName = CardNormalizer.Normalize("Guardian Project"),
                            Quantity = 1,
                            Board = "mainboard",
                            SetCode = "rna",
                            CollectorNumber = "130",
                            Category = "Draw"
                        },
                        MoxfieldVersion = new DeckEntry
                        {
                            Name = "Guardian Project",
                            NormalizedName = CardNormalizer.Normalize("Guardian Project"),
                            Quantity = 1,
                            Board = "mainboard",
                            SetCode = "clb",
                            CollectorNumber = "777"
                        },
                        Resolution = PrintingChoice.KeepArchidekt
                    }
                });

            var loadedDecks = new LoadedDecks(
                new List<DeckEntry>
                {
                    new()
                    {
                        Name = "Sol Ring",
                        NormalizedName = CardNormalizer.Normalize("Sol Ring"),
                        Quantity = 1,
                        Board = "mainboard"
                    }
                },
                new List<DeckEntry>
                {
                    new()
                    {
                        Name = "Arcane Signet",
                        NormalizedName = CardNormalizer.Normalize("Arcane Signet"),
                        Quantity = 1,
                        Board = "mainboard",
                        Category = "Ramp"
                    }
                });

            return Task.FromResult(new DeckSyncResult(diff, loadedDecks));
        }
    }

    private sealed class ThrowingDeckSyncService : IDeckSyncService
    {
        private readonly Exception _exception;

        public ThrowingDeckSyncService(Exception exception)
        {
            _exception = exception;
        }

        public Task<DeckSyncResult> CompareDecksAsync(DeckDiffRequest request, CancellationToken cancellationToken)
            => Task.FromException<DeckSyncResult>(_exception);
    }
}
