using Bunit;
using DeckFlow.Studio.Shared;

namespace DeckFlow.Studio.Tests;

/// <summary>
/// bUnit coverage for MainLayout.razor, Phase 116 findings F-01 and F-11.
/// </summary>
public sealed class MainLayoutTests : BunitContext
{
    // Why: This invariant protects every new-tab link rendered by MainLayout, including child components.
    [Fact]
    public void MainLayout_EveryNewTabLink_CarriesNoopenerNoreferrer()
    {
        var cut = Render<MainLayout>();
        var newTabLinks = cut.FindAll("a[target='_blank']");

        Assert.NotEmpty(newTabLinks);
        Assert.All(newTabLinks, link => Assert.Equal("noopener noreferrer", link.GetAttribute("rel")));
    }

    [Fact]
    public void MainLayout_RendersAboutLink_ToPublicSite()
    {
        var cut = Render<MainLayout>();
        var aboutLink = cut.Find("a[href='https://www.deckflow.gg']");

        Assert.Equal("_blank", aboutLink.GetAttribute("target"));
    }

    // Why: The selector is scoped under main because the sidebar brand strip legitimately retains this class.
    [Fact]
    public void MainLayout_MainRegion_RendersNoChromeBar()
    {
        var cut = Render<MainLayout>();

        Assert.Empty(cut.FindAll("main .top-row"));
    }

    [Fact]
    public void MainLayout_MainRegion_ContainsOnlyTheContentArticle()
    {
        var cut = Render<MainLayout>();
        var mainChildren = cut.FindAll("main > *");

        Assert.Single(mainChildren);
        Assert.Contains("studio-content", mainChildren[0].ClassList);
    }
}
