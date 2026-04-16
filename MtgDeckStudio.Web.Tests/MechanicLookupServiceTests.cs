using Microsoft.Extensions.Caching.Memory;
using MtgDeckStudio.Web.Services;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class MechanicLookupServiceTests
{
    private const string RulesPageUrl = "https://magic.wizards.com/en/rules";
    private const string RulesTextUrl = "https://media.wizards.com/2026/downloads/MagicCompRules%2020260227.txt";

    private const string RulesPageHtml = """
        <html>
        <body>
        <a href="https://media.wizards.com/2026/downloads/MagicCompRules 20260227.txt">txt</a>
        </body>
        </html>
        """;

    private const string RulesText = """
        122. Glossary

        207.2c An ability word appears in italics at the beginning of some abilities. Ability words are similar to keywords in that they tie together cards that have similar functionality, but they have no special rules meaning and no individual entries in the Comprehensive Rules. The ability words are landfall and magecraft.

        702. Keyword Abilities

        702.108. Prowess
        702.108a Prowess is a triggered ability. “Prowess” means “Whenever you cast a noncreature spell, this creature gets +1/+1 until end of turn.”

        702.157. Squad

        702.157a Squad is a keyword that represents two linked abilities. The first is a static ability that functions while the creature spell with squad is on the stack. The second is a triggered ability that functions when the creature with squad enters the battlefield.

        702.157b If a spell has multiple instances of squad, each is paid separately. If a permanent has multiple instances of squad, each triggers based on the payments made for that squad ability as it was cast, not based on payments for any other instance of squad.

        Glossary

        Prowess
        A keyword ability that causes a creature to get +1/+1 whenever its controller casts a noncreature spell. See rule 702.108, “Prowess.”

        Squad
        A keyword ability that lets you pay an additional cost to create token copies. See rule 702.157, “Squad.”

        Warp
        A keyword ability found on permanent cards that allows them to be cast for an alternative cost. See rule 702.185, “Warp.”
        """;

    [Fact]
    public async Task LookupAsync_ReturnsExactRulesSection_WhenMechanicHasSection()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = new WotcMechanicLookupService(memoryCache, FakeFetchAsync);

        var result = await service.LookupAsync("Prowess");

        Assert.True(result.Found);
        Assert.Equal("Prowess", result.MechanicName);
        Assert.Equal("702.108", result.RuleReference);
        Assert.Equal("Exact rules section", result.MatchType);
        Assert.Contains("702.108a Prowess is a triggered ability.", result.RulesText);
        Assert.Contains("See rule 702.108", result.SummaryText);
    }

    [Fact]
    public async Task LookupAsync_IncludesAllSubsections_WhenSeparatedByBlankLines()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = new WotcMechanicLookupService(memoryCache, FakeFetchAsync);

        var result = await service.LookupAsync("Squad");

        Assert.True(result.Found);
        Assert.Equal("Squad", result.MechanicName);
        Assert.Equal("702.157", result.RuleReference);
        Assert.Equal("Exact rules section", result.MatchType);
        Assert.Contains("702.157a Squad is a keyword", result.RulesText);
        Assert.Contains("702.157b If a spell has multiple instances", result.RulesText);
        // Blank lines separate the header and each subsection for readability.
        var expectedSeparator = $"Squad{Environment.NewLine}{Environment.NewLine}702.157a";
        Assert.Contains(expectedSeparator, result.RulesText);
        Assert.Contains("See rule 702.157", result.SummaryText);
    }

    [Fact]
    public async Task LookupAsync_ReturnsReferencedRule_WhenMechanicIsAbilityWord()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = new WotcMechanicLookupService(memoryCache, FakeFetchAsync);

        var result = await service.LookupAsync("Landfall");

        Assert.True(result.Found);
        Assert.Equal("207.2c", result.RuleReference);
        Assert.Equal("Referenced in rule text", result.MatchType);
        Assert.Contains("ability word", result.RulesText);
        Assert.Contains("landfall", result.RulesText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LookupAsync_ReturnsNotFound_WhenMechanicDoesNotExist()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = new WotcMechanicLookupService(memoryCache, FakeFetchAsync);

        var result = await service.LookupAsync("MadeUpMechanic");

        Assert.False(result.Found);
        Assert.Null(result.RulesText);
        Assert.Equal(RulesTextUrl, result.RulesTextUrl);
    }

    private static Task<string> FakeFetchAsync(string url, CancellationToken cancellationToken)
        => Task.FromResult(url == RulesPageUrl ? RulesPageHtml : RulesText);
}
