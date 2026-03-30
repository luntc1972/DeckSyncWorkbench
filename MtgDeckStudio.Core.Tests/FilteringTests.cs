using MtgDeckStudio.Core.Filtering;
using MtgDeckStudio.Core.Models;

namespace MtgDeckStudio.Core.Tests;

public sealed class FilteringTests
{
    [Fact]
    public void DeckEntryFilter_ExcludesMaybeboardEntries()
    {
        var entries = new List<DeckEntry>
        {
            new() { Name = "Main Card", NormalizedName = "main card", Quantity = 1, Board = "mainboard" },
            new() { Name = "Maybe Card", NormalizedName = "maybe card", Quantity = 1, Board = "maybeboard", Category = "Maybeboard" },
        };

        var filtered = DeckEntryFilter.ExcludeMaybeboard(entries);

        var entry = Assert.Single(filtered);
        Assert.Equal("Main Card", entry.Name);
    }
}
