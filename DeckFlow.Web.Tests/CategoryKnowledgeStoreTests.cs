using DeckFlow.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace DeckFlow.Web.Tests;

// MTG_DATA_DIR is process-wide, so these tests are serialized to avoid cross-test interference.
[CollectionDefinition("CategoryKnowledgeStoreTests", DisableParallelization = true)]
public sealed class CategoryKnowledgeStoreTestsCollection
{
}

[Collection("CategoryKnowledgeStoreTests")]
public sealed class CategoryKnowledgeStoreTests
{
    [Fact]
    public void DatabasePath_UsesMtgDataDirWhenSet()
    {
        var original = Environment.GetEnvironmentVariable("MTG_DATA_DIR");
        var tempDir = Path.Combine(Path.GetTempPath(), "deckflow-data-" + Guid.NewGuid().ToString("N"));

        try
        {
            Environment.SetEnvironmentVariable("MTG_DATA_DIR", tempDir);

            var store = CreateStore("/repo/content-root");
            var expectedRoot = Path.GetFullPath(tempDir);

            Assert.StartsWith(expectedRoot, store.DatabasePath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("category-knowledge.db", store.DatabasePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MTG_DATA_DIR", original);
        }
    }

    [Fact]
    public void DatabasePath_DefaultsFromContentRootPathWhenMtgDataDirUnset()
    {
        var original = Environment.GetEnvironmentVariable("MTG_DATA_DIR");

        try
        {
            Environment.SetEnvironmentVariable("MTG_DATA_DIR", null);

            var contentRoot = Path.Combine(Path.GetTempPath(), "deckflow-content-" + Guid.NewGuid().ToString("N"));
            var store = CreateStore(contentRoot);

            Assert.Contains("artifacts", store.DatabasePath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("category-knowledge.db", store.DatabasePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MTG_DATA_DIR", original);
        }
    }

    [Theory]
    [InlineData(null, typeof(ArgumentNullException))]
    [InlineData("", typeof(ArgumentException))]
    [InlineData("   ", typeof(ArgumentException))]
    public async Task GetCategoriesAsync_ThrowsForBlankCardName(string? cardName, Type expectedExceptionType)
    {
        var store = CreateStore();

        if (expectedExceptionType == typeof(ArgumentNullException))
        {
            var nullException = await Assert.ThrowsAsync<ArgumentNullException>(() => store.GetCategoriesAsync(cardName!));
            Assert.Equal("cardName", nullException.ParamName);
            return;
        }

        var valueException = await Assert.ThrowsAsync<ArgumentException>(() => store.GetCategoriesAsync(cardName!));
        Assert.Equal("cardName", valueException.ParamName);
    }

    [Theory]
    [InlineData(null, typeof(ArgumentNullException))]
    [InlineData("", typeof(ArgumentException))]
    [InlineData("   ", typeof(ArgumentException))]
    public async Task GetCategoryRowsAsync_ThrowsForBlankCardName(string? cardName, Type expectedExceptionType)
    {
        var store = CreateStore();

        if (expectedExceptionType == typeof(ArgumentNullException))
        {
            var nullException = await Assert.ThrowsAsync<ArgumentNullException>(() => store.GetCategoryRowsAsync(cardName!));
            Assert.Equal("cardName", nullException.ParamName);
            return;
        }

        var valueException = await Assert.ThrowsAsync<ArgumentException>(() => store.GetCategoryRowsAsync(cardName!));
        Assert.Equal("cardName", valueException.ParamName);
    }

    [Theory]
    [InlineData(null, typeof(ArgumentNullException))]
    [InlineData("", typeof(ArgumentException))]
    [InlineData("   ", typeof(ArgumentException))]
    public async Task GetCardDeckTotalsAsync_ThrowsForBlankCardName(string? cardName, Type expectedExceptionType)
    {
        var store = CreateStore();

        if (expectedExceptionType == typeof(ArgumentNullException))
        {
            var nullException = await Assert.ThrowsAsync<ArgumentNullException>(() => store.GetCardDeckTotalsAsync(cardName!));
            Assert.Equal("cardName", nullException.ParamName);
            return;
        }

        var valueException = await Assert.ThrowsAsync<ArgumentException>(() => store.GetCardDeckTotalsAsync(cardName!));
        Assert.Equal("cardName", valueException.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PersistObservedCategoriesAsync_IgnoresBlankSource(string? source)
    {
        var store = CreateStore();

        await store.PersistObservedCategoriesAsync(source!, "Card Name", ["Draw"], quantity: 1);
    }

    [Theory]
    [InlineData("", new[] { "Draw" }, 1)]
    [InlineData("Card Name", new string[0], 1)]
    [InlineData("Card Name", new[] { "Draw" }, 0)]
    public async Task PersistObservedCategoriesAsync_IgnoresEmptyCardNameEmptyCategoriesOrNonPositiveQuantity(string cardName, string[] categories, int quantity)
    {
        var store = CreateStore();

        await store.PersistObservedCategoriesAsync("source", cardName, categories, quantity);
    }

    private static CategoryKnowledgeStore CreateStore(string? contentRootPath = null)
        => new(new FakeWebHostEnvironment(contentRootPath ?? Path.Combine(Path.GetTempPath(), "deckflow-content-root")));

    private sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeckFlow.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
