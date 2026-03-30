using MtgDeckStudio.Core.Knowledge;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Normalization;

namespace MtgDeckStudio.Core.Tests;

public sealed class DeckCategoryCacheWriterTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _tempDirectory;

    public DeckCategoryCacheWriterTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "MtgDeckStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _databasePath = Path.Combine(_tempDirectory, "category-knowledge.db");
    }

    [Fact]
    public async Task ReplaceDeckEntriesAsync_ReplacesExistingDeckRows()
    {
        var repository = new CategoryKnowledgeRepository(_databasePath);
        var source = "archidekt_live:123";

        await DeckCategoryCacheWriter.ReplaceDeckEntriesAsync(repository, source, new[]
        {
            CreateEntry("Guardian Project", "Draw")
        });

        await DeckCategoryCacheWriter.ReplaceDeckEntriesAsync(repository, source, new[]
        {
            CreateEntry("Sol Ring", "Ramp")
        });

        var oldCardCategories = await repository.GetCategoriesAsync("Guardian Project");
        var newCardCategories = await repository.GetCategoriesAsync("Sol Ring");
        var oldCardTotals = await repository.GetCardDeckTotalsAsync("Guardian Project");
        var newCardTotals = await repository.GetCardDeckTotalsAsync("Sol Ring");

        Assert.Empty(oldCardCategories);
        Assert.Equal(new[] { "Ramp" }, newCardCategories);
        Assert.Equal(0, oldCardTotals.TotalDeckCount);
        Assert.Equal(1, newCardTotals.TotalDeckCount);
    }

    private static DeckEntry CreateEntry(string cardName, string category) => new()
    {
        Name = cardName,
        NormalizedName = CardNormalizer.Normalize(cardName),
        Quantity = 1,
        Board = "mainboard",
        Category = category
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
