using DeckFlow.Web.Services.Tools;
using Xunit;

namespace DeckFlow.Web.Tests.Tools;

/// <summary>
/// File-level regression tests for registry-driven home tile rendering.
/// </summary>
public sealed class HomeTilesViewTests
{
    [Fact]
    public void Home_DoesNotContainOfflinePlaceholderCopy()
    {
        var content = ReadHome();

        Assert.DoesNotContain("Temporarily offline", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_DoesNotContainStatusPlaceholderCard()
    {
        var content = ReadHome();

        Assert.DoesNotContain("hub-card--status", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tool.manabase.enabled")]
    [InlineData("tool.knowledge-base.enabled")]
    [InlineData("tool.categories.enabled")]
    public void Home_DoesNotContainHardcodedToolFlagLiterals(string flagKey)
    {
        var content = ReadHome();

        Assert.DoesNotContain(flagKey, content, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_UsesVisibleBySection()
    {
        var content = ReadHome();

        Assert.Contains("VisibleBySection", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_UsesToolTileIconPartial()
    {
        var content = ReadHome();

        Assert.Contains("_ToolTileIcon", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_HeroCtaIsNotHardcodedToDeckAnalysisRoute()
    {
        var content = ReadHome();

        Assert.DoesNotContain("href=\"@Url.Content(\"~/deck-analysis\")\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_DeckAnalysisHeroExplainsExternalAiWorkflow()
    {
        var content = ReadHome();

        Assert.Contains("Five-step external-AI workflow:", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_MobileStylesUseSingleColumnToolDirectory()
    {
        var content = ReadMobileStyles();

        Assert.Contains("grid-template-columns: 1fr;", content, StringComparison.Ordinal);
        Assert.Contains(".hub-card::after", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every icon key the registry can actually emit. Derived from the registry rather than
    /// hand-listed: the previous hard-coded list had drifted to two keys that no longer existed
    /// ("ask-a-judge", "category-suggestions" are help slugs, not icon keys) while omitting three
    /// real ones, so it passed while three tiles rendered the fallback "?" glyph.
    /// </summary>
    public static TheoryData<string> IconKeys()
        => new(new ToolRegistry().All
            .Select(tool => tool.IconKey)
            .Distinct(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(IconKeys))]
    public void ToolTileIcon_PartialContainsIconArm(string iconKey)
    {
        var content = ReadToolTileIconPartial();

        // Assert the switch arm specifically — a bare quoted-string match would also be satisfied
        // by the key appearing in a comment.
        Assert.Contains($"case \"{iconKey}\":", content, StringComparison.Ordinal);
        Assert.Contains("<svg", content, StringComparison.Ordinal);
    }

    private static string ReadHome()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "DeckFlow.Web",
            "Views",
            "Deck",
            "Home.cshtml"));

    private static string ReadMobileStyles()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "DeckFlow.Web",
            "wwwroot",
            "css",
            "site-mobile.css"));

    private static string ReadToolTileIconPartial()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "DeckFlow.Web",
            "Views",
            "Shared",
            "_ToolTileIcon.cshtml"));
}
