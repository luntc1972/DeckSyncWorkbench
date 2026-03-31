using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Normalization;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Web.Models;
using MtgDeckStudio.Web.Services;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

/// <summary>
/// Covers deck-size validation and same-system compare paths in <see cref="DeckSyncService"/>.
/// </summary>
public sealed class DeckSyncServiceTests
{
    /// <summary>
    /// Rejects compares when the Moxfield-side deck does not contain exactly 100 playable cards.
    /// </summary>
    [Fact]
    public async Task CompareDecksAsync_ThrowsWhenMoxfieldDeckIsNot100Cards()
    {
        var service = new DeckSyncService(
            new FakeMoxfieldDeckImporter(CreateDeckEntries(99)),
            new FakeArchidektDeckImporter(CreateDeckEntries(100)),
            new MoxfieldParser(),
            new ArchidektParser());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompareDecksAsync(
            new DeckDiffRequest
            {
                MoxfieldInputSource = DeckInputSource.PublicUrl,
                MoxfieldUrl = "https://moxfield.com/decks/test",
                ArchidektInputSource = DeckInputSource.PublicUrl,
                ArchidektUrl = "https://archidekt.com/decks/123/test"
            },
            CancellationToken.None));

        Assert.Equal("Moxfield deck must contain exactly 100 cards across commander and mainboard. Found 99.", exception.Message);
    }

    /// <summary>
    /// Allows standard compares when both decks have a legal Commander card count.
    /// </summary>
    [Fact]
    public async Task CompareDecksAsync_AllowsExactly100Cards()
    {
        var service = new DeckSyncService(
            new FakeMoxfieldDeckImporter(CreateDeckEntries(100)),
            new FakeArchidektDeckImporter(CreateDeckEntries(100)),
            new MoxfieldParser(),
            new ArchidektParser());

        var result = await service.CompareDecksAsync(
            new DeckDiffRequest
            {
                MoxfieldInputSource = DeckInputSource.PublicUrl,
                MoxfieldUrl = "https://moxfield.com/decks/test",
                ArchidektInputSource = DeckInputSource.PublicUrl,
                ArchidektUrl = "https://archidekt.com/decks/123/test"
            },
            CancellationToken.None);

        Assert.NotNull(result);
    }

    /// <summary>
    /// Verifies that two Moxfield decks can be compared directly.
    /// </summary>
    [Fact]
    public async Task CompareDecksAsync_AllowsMoxfieldToMoxfieldComparisons()
    {
        var service = new DeckSyncService(
            new FakeMoxfieldDeckImporter(url => CreateDeckEntries(url.Contains("source", StringComparison.OrdinalIgnoreCase) ? 100 : 100)),
            new FakeArchidektDeckImporter(url => CreateDeckEntries(99)),
            new MoxfieldParser(),
            new ArchidektParser());

        var result = await service.CompareDecksAsync(
            new DeckDiffRequest
            {
                Direction = SyncDirection.MoxfieldToMoxfield,
                MoxfieldInputSource = DeckInputSource.PublicUrl,
                MoxfieldUrl = "https://moxfield.com/decks/source",
                ArchidektInputSource = DeckInputSource.PublicUrl,
                ArchidektUrl = "https://moxfield.com/decks/target"
            },
            CancellationToken.None);

        Assert.NotNull(result);
    }

    /// <summary>
    /// Verifies that two Archidekt decks can be compared directly.
    /// </summary>
    [Fact]
    public async Task CompareDecksAsync_AllowsArchidektToArchidektComparisons()
    {
        var service = new DeckSyncService(
            new FakeMoxfieldDeckImporter(url => CreateDeckEntries(99)),
            new FakeArchidektDeckImporter(url => CreateDeckEntries(url.Contains("source", StringComparison.OrdinalIgnoreCase) ? 100 : 100)),
            new MoxfieldParser(),
            new ArchidektParser());

        var result = await service.CompareDecksAsync(
            new DeckDiffRequest
            {
                Direction = SyncDirection.ArchidektToArchidekt,
                MoxfieldInputSource = DeckInputSource.PublicUrl,
                MoxfieldUrl = "https://archidekt.com/decks/target",
                ArchidektInputSource = DeckInputSource.PublicUrl,
                ArchidektUrl = "https://archidekt.com/decks/source"
            },
            CancellationToken.None);

        Assert.NotNull(result);
    }

    private static List<DeckEntry> CreateDeckEntries(int count)
    {
        return
        [
            new DeckEntry
            {
                Name = "Commander Card",
                NormalizedName = CardNormalizer.Normalize("Commander Card"),
                Quantity = 1,
                Board = "commander"
            },
            new DeckEntry
            {
                Name = "Mainboard Card",
                NormalizedName = CardNormalizer.Normalize("Mainboard Card"),
                Quantity = count - 1,
                Board = "mainboard"
            },
            new DeckEntry
            {
                Name = "Maybeboard Card",
                NormalizedName = CardNormalizer.Normalize("Maybeboard Card"),
                Quantity = 5,
                Board = "maybeboard"
            }
        ];
    }

    private sealed class FakeMoxfieldDeckImporter : IMoxfieldDeckImporter
    {
        private readonly Func<string, List<DeckEntry>> _entriesFactory;

        public FakeMoxfieldDeckImporter(List<DeckEntry> entries)
            : this(_ => entries)
        {
        }

        public FakeMoxfieldDeckImporter(Func<string, List<DeckEntry>> entriesFactory)
        {
            _entriesFactory = entriesFactory;
        }

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(_entriesFactory(urlOrDeckId));
    }

    private sealed class FakeArchidektDeckImporter : IArchidektDeckImporter
    {
        private readonly Func<string, List<DeckEntry>> _entriesFactory;

        public FakeArchidektDeckImporter(List<DeckEntry> entries)
            : this(_ => entries)
        {
        }

        public FakeArchidektDeckImporter(Func<string, List<DeckEntry>> entriesFactory)
        {
            _entriesFactory = entriesFactory;
        }

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(_entriesFactory(urlOrDeckId));
    }
}
