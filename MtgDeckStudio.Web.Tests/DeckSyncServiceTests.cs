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

public sealed class DeckSyncServiceTests
{
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
        private readonly List<DeckEntry> _entries;

        public FakeMoxfieldDeckImporter(List<DeckEntry> entries)
        {
            _entries = entries;
        }

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries);
    }

    private sealed class FakeArchidektDeckImporter : IArchidektDeckImporter
    {
        private readonly List<DeckEntry> _entries;

        public FakeArchidektDeckImporter(List<DeckEntry> entries)
        {
            _entries = entries;
        }

        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries);
    }
}
