using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Normalization;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Services;
using RestSharp;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

/// <summary>
/// Covers DFC/alternate name normalization and commander detection in <see cref="DeckConvertService"/>.
/// </summary>
public sealed class DeckConvertServiceTests
{
    /// <summary>
    /// When converting Moxfield→Archidekt, entries with set+collector info have their names
    /// replaced with the canonical Scryfall name (handles DFC back-face names).
    /// </summary>
    [Fact]
    public async Task ConvertAsync_NormalizesAlternateNames_WhenMoxfieldToArchidekt()
    {
        var entries = new List<DeckEntry>
        {
            MakeEntry("Delver of Secrets", "ltr", "1"),
            MakeEntry("Insectile Aberration", "ltr", "2"),  // DFC back-face name
        };

        var scryfallResponse = MakeCollectionResponse(
        [
            MakeScryfallCard("Delver of Secrets // Insectile Aberration", "ltr", "2"),
            MakeScryfallCard("Delver of Secrets", "ltr", "1"),
        ]);

        var service = BuildService(
            moxfieldEntries: entries,
            collectionResponse: scryfallResponse);

        var result = await service.ConvertAsync(new DeckConvertRequest
        {
            SourceFormat = "Moxfield",
            TargetFormat = "Archidekt",
            InputSource = DeckInputSource.PublicUrl,
            DeckUrl = "https://moxfield.com/decks/test"
        });

        Assert.Contains("Delver of Secrets // Insectile Aberration", result.ConvertedText);
        Assert.DoesNotContain("\nInsectile Aberration ", result.ConvertedText);
    }

    /// <summary>
    /// Entries that have no set or collector info are passed through unchanged.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_LeavesEntriesWithoutSetInfo_Unchanged()
    {
        var entries = new List<DeckEntry>
        {
            MakeEntry("Lightning Bolt", null, null),
        };

        var service = BuildService(
            moxfieldEntries: entries,
            collectionResponse: MakeCollectionResponse([]));

        var result = await service.ConvertAsync(new DeckConvertRequest
        {
            SourceFormat = "Moxfield",
            TargetFormat = "Archidekt",
            InputSource = DeckInputSource.PublicUrl,
            DeckUrl = "https://moxfield.com/decks/test"
        });

        Assert.Contains("Lightning Bolt", result.ConvertedText);
    }

    /// <summary>
    /// Normalization is skipped when converting in the non-Moxfield-to-Archidekt direction.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_SkipsNormalization_WhenNotMoxfieldToArchidekt()
    {
        var collectionCallCount = 0;
        var entries = new List<DeckEntry>
        {
            MakeEntry("Some Card", "abc", "1"),
        };

        var service = BuildService(
            archidektEntries: entries,
            collectionResponse: MakeCollectionResponse([]),
            onCollectionCall: () => collectionCallCount++);

        await service.ConvertAsync(new DeckConvertRequest
        {
            SourceFormat = "Archidekt",
            TargetFormat = "Moxfield",
            InputSource = DeckInputSource.PublicUrl,
            DeckUrl = "https://archidekt.com/decks/123/test"
        });

        Assert.Equal(0, collectionCallCount);
    }

    /// <summary>
    /// When the Scryfall name matches the entry name, the entry is returned unchanged.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_KeepsName_WhenScryfallReturnsMatchingName()
    {
        var entries = new List<DeckEntry>
        {
            MakeEntry("Lightning Bolt", "lea", "161"),
        };

        var scryfallResponse = MakeCollectionResponse(
        [
            MakeScryfallCard("Lightning Bolt", "lea", "161"),
        ]);

        var service = BuildService(
            moxfieldEntries: entries,
            collectionResponse: scryfallResponse);

        var result = await service.ConvertAsync(new DeckConvertRequest
        {
            SourceFormat = "Moxfield",
            TargetFormat = "Archidekt",
            InputSource = DeckInputSource.PublicUrl,
            DeckUrl = "https://moxfield.com/decks/test"
        });

        Assert.Contains("Lightning Bolt", result.ConvertedText);
    }

    /// <summary>
    /// When a Moxfield import has no commander board entry and exactly 99 cards,
    /// CommanderMissing is true in the result.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_ReportsCommanderMissing_WhenNoCommanderAndOnly99Cards()
    {
        var entries = new List<DeckEntry>();
        for (var i = 0; i < 99; i++)
        {
            entries.Add(MakeEntry($"Card {i}", null, null));
        }

        var service = BuildService(moxfieldEntries: entries);

        var result = await service.ConvertAsync(new DeckConvertRequest
        {
            SourceFormat = "Moxfield",
            TargetFormat = "Archidekt",
            InputSource = DeckInputSource.PublicUrl,
            DeckUrl = "https://moxfield.com/decks/test"
        });

        Assert.True(result.CommanderMissing);
    }

    /// <summary>
    /// When CommanderOverride is provided and no commander is in the import,
    /// the override is prepended as the commander and CommanderMissing is false.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_InjectsCommanderOverride_WhenCommanderMissing()
    {
        var entries = new List<DeckEntry>
        {
            MakeEntry("Lightning Bolt", null, null),
        };

        var service = BuildService(moxfieldEntries: entries);

        var result = await service.ConvertAsync(new DeckConvertRequest
        {
            SourceFormat = "Moxfield",
            TargetFormat = "Archidekt",
            InputSource = DeckInputSource.PublicUrl,
            DeckUrl = "https://moxfield.com/decks/test",
            CommanderOverride = "Atraxa, Praetors' Voice"
        });

        Assert.False(result.CommanderMissing);
        Assert.Contains("Atraxa, Praetors' Voice", result.ConvertedText);
        Assert.Contains("Commander", result.ConvertedText);
    }

    private static DeckConvertService BuildService(
        List<DeckEntry>? moxfieldEntries = null,
        List<DeckEntry>? archidektEntries = null,
        RestResponse<ScryfallCollectionResponse>? collectionResponse = null,
        System.Action? onCollectionCall = null)
    {
        return new DeckConvertService(
            new FakeMoxfieldDeckImporter(moxfieldEntries ?? []),
            new FakeArchidektDeckImporter(archidektEntries ?? []),
            new MoxfieldParser(),
            new ArchidektParser(),
            executeCollectionAsync: (_, _) =>
            {
                onCollectionCall?.Invoke();
                return Task.FromResult(collectionResponse ?? MakeCollectionResponse([]));
            });
    }

    private static DeckEntry MakeEntry(string name, string? setCode, string? collectorNumber) =>
        new()
        {
            Name = name,
            NormalizedName = CardNormalizer.Normalize(name),
            Quantity = 1,
            Board = "mainboard",
            SetCode = setCode,
            CollectorNumber = collectorNumber
        };

    private static RestResponse<ScryfallCollectionResponse> MakeCollectionResponse(List<ScryfallCard> cards) =>
        new(new RestRequest("cards/collection"))
        {
            StatusCode = HttpStatusCode.OK,
            Data = new ScryfallCollectionResponse(cards, null)
        };

    private static ScryfallCard MakeScryfallCard(string name, string setCode, string collectorNumber) =>
        new(name, null, string.Empty, null, null, null, null, null, setCode, null, collectorNumber);

    private sealed class FakeMoxfieldDeckImporter : IMoxfieldDeckImporter
    {
        private readonly List<DeckEntry> _entries;

        public FakeMoxfieldDeckImporter(List<DeckEntry> entries) => _entries = entries;

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries);
    }

    private sealed class FakeArchidektDeckImporter : IArchidektDeckImporter
    {
        private readonly List<DeckEntry> _entries;

        public FakeArchidektDeckImporter(List<DeckEntry> entries) => _entries = entries;

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries);
    }
}
