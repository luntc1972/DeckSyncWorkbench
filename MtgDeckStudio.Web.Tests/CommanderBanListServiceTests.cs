using MtgDeckStudio.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class CommanderBanListServiceTests
{
    [Fact]
    public void ParseBannedCards_ReturnsSummaryNames()
    {
        const string html = """
<html>
<body>
<details><summary>Ancestral Recall</summary><p>text</p></details>
<details><summary>Dockside Extortionist</summary><p>text</p></details>
<details><summary>Mana Crypt</summary><p>text</p></details>
</body>
</html>
""";

        var cards = CommanderBanListService.ParseBannedCards(html);

        Assert.Collection(
            cards,
            card => Assert.Equal("Ancestral Recall", card),
            card => Assert.Equal("Dockside Extortionist", card),
            card => Assert.Equal("Mana Crypt", card));
    }

    [Fact]
    public async Task GetBannedCardsAsync_CachesResults()
    {
        var fetchCount = 0;
        var service = new CommanderBanListService(
            new MemoryCache(new MemoryCacheOptions()),
            _ =>
            {
                fetchCount++;
                return Task.FromResult("<details><summary>Mana Crypt</summary></details>");
            });

        var first = await service.GetBannedCardsAsync();
        var second = await service.GetBannedCardsAsync();

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(1, fetchCount);
    }
}
