using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Normalization;
using MtgDeckStudio.Web.Controllers.Api;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Models.Api;
using MtgDeckStudio.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class DeckSyncApiControllerTests
{
    [Fact]
    public async Task PostDiffAsync_ReturnsBadRequest_WhenMoxfieldInputMissing()
    {
        var controller = new DeckSyncApiController(new FakeDeckSyncService(), NullLogger<DeckSyncApiController>.Instance);

        var response = await controller.PostDiffAsync(new DeckSyncApiRequest
        {
            MoxfieldInputSource = DeckInputSource.PasteText,
            ArchidektInputSource = DeckInputSource.PasteText,
            ArchidektText = "1 Sol Ring"
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task PostDiffAsync_ReturnsStructuredResponse()
    {
        var controller = new DeckSyncApiController(new FakeDeckSyncService(), NullLogger<DeckSyncApiController>.Instance);

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

    [Fact]
    public async Task PostDiffAsync_ReturnsSiteSpecificMessage_WhenUpstreamRequestFails()
    {
        var controller = new DeckSyncApiController(
            new ThrowingDeckSyncService(new HttpRequestException("Archidekt returned HTTP 503.", null, System.Net.HttpStatusCode.ServiceUnavailable)),
            NullLogger<DeckSyncApiController>.Instance);

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
