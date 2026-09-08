using Bunit;
using DeckFlow.Studio.Shared;

namespace DeckFlow.Studio.Tests;

/// <summary>
/// bUnit tests for NavMenu.razor.
/// Covers A3 (SUI-04): grouped Pipeline / Support sections with all existing hrefs preserved.
/// </summary>
public sealed class NavMenuTests : BunitContext
{
    // ── A3: Pipeline section contains the expected nav links ──────────────────

    [Fact]
    public void NavMenu_Renders_HomeLinkInPipelineSection()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='']"));
    }

    [Fact]
    public void NavMenu_Renders_GuideLink()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='guide']"));
    }

    [Fact]
    public void NavMenu_Renders_HarvestLink()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='harvest']"));
    }

    [Fact]
    public void NavMenu_Renders_CreatorsLink()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='creators']"));
    }

    [Fact]
    public void NavMenu_Renders_ReviewLink()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='review']"));
    }

    [Fact]
    public void NavMenu_Renders_PublishLink()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='publish']"));
    }

    [Fact]
    public void NavMenu_Renders_DirectPushLink()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='direct-push']"));
    }

    [Fact]
    public void NavMenu_Renders_PullFromProdLink()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='pull-from-prod']"));
    }

    [Fact]
    public void NavMenu_Renders_ReconcileLink()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='reconcile']"));
    }

    [Fact]
    public void NavMenu_Renders_GitBodyCoverageLink()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='git-body-coverage']"));
    }

    // ── A3: Support section contains Skipped and Blocked ─────────────────────

    [Fact]
    public void NavMenu_Renders_SkippedLink()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='skipped']"));
    }

    [Fact]
    public void NavMenu_Renders_BlockedLink()
    {
        var cut = Render<NavMenu>();
        Assert.NotNull(cut.Find("a[href='blocked']"));
    }

    // ── A3: Both section headers are rendered ─────────────────────────────────

    [Fact]
    public void NavMenu_Renders_PipelineSectionHeader()
    {
        var cut = Render<NavMenu>();
        var headers = cut.FindAll(".nav-section-header");
        Assert.Contains(headers, h => h.TextContent.Contains("Pipeline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NavMenu_Renders_SupportSectionHeader()
    {
        var cut = Render<NavMenu>();
        var headers = cut.FindAll(".nav-section-header");
        Assert.Contains(headers, h => h.TextContent.Contains("Support", StringComparison.OrdinalIgnoreCase));
    }

    // ── A3/91-07: All destinations are present (count check) ──────────────────

    [Fact]
    public void NavMenu_Renders_AllTwelveDestinations()
    {
        var cut = Render<NavMenu>();
        var navLinks = cut.FindAll("nav a.nav-link");
        // Home, Guide, Harvest, Creators, Review, Publish, Direct Push, Pull from Prod,
        // Reconcile, Git Body Coverage, Skipped, Blocked = 12
        Assert.Equal(12, navLinks.Count);
    }

    // ── A3: Pipeline links appear before Support links in document order ──────

    [Fact]
    public void NavMenu_PipelineLinksAppearBeforeSupportLinks()
    {
        var cut = Render<NavMenu>();
        var allLinks = cut.FindAll("nav a.nav-link");
        var hrefs = allLinks.Select(a => a.GetAttribute("href") ?? string.Empty).ToList();

        var harvestIdx = hrefs.IndexOf("harvest");
        var skippedIdx = hrefs.IndexOf("skipped");

        Assert.True(harvestIdx >= 0, "harvest link not found");
        Assert.True(skippedIdx >= 0, "skipped link not found");
        Assert.True(harvestIdx < skippedIdx, "Pipeline links should precede Support links");
    }

    // ── F-02: Collapse control is keyboard-complete and close-only ────────────

    [Fact]
    public void NavMenu_Toggler_StartsCollapsed_AndAnnouncesIt()
    {
        var cut = Render<NavMenu>();
        var toggler = cut.Find(".navbar-toggler");

        Assert.Equal("false", toggler.GetAttribute("aria-expanded"));
        Assert.Equal("studio-nav-menu", toggler.GetAttribute("aria-controls"));
    }

    [Fact]
    public void NavMenu_Wrapper_CarriesTheAriaControlsTargetId()
    {
        var cut = Render<NavMenu>();
        var wrapper = cut.Find("#studio-nav-menu");

        Assert.True(wrapper.ClassList.Contains("collapse"));
    }

    [Fact]
    public void NavMenu_ClickingToggler_Expands()
    {
        var cut = Render<NavMenu>();

        cut.Find(".navbar-toggler").Click();

        Assert.Equal("true", cut.Find(".navbar-toggler").GetAttribute("aria-expanded"));
        Assert.False(cut.Find("#studio-nav-menu").ClassList.Contains("collapse"));
    }

    [Fact]
    public void NavMenu_ClickingTogglerTwice_ReturnsToCollapsed()
    {
        var cut = Render<NavMenu>();
        var toggler = cut.Find(".navbar-toggler");

        toggler.Click();
        toggler.Click();

        Assert.Equal("false", toggler.GetAttribute("aria-expanded"));
        Assert.True(cut.Find("#studio-nav-menu").ClassList.Contains("collapse"));
    }

    [Fact]
    public void NavMenu_ClickingWrapperWhileCollapsed_DoesNotOpenIt()
    {
        var cut = Render<NavMenu>();

        cut.Find("#studio-nav-menu").Click();

        Assert.True(cut.Find("#studio-nav-menu").ClassList.Contains("collapse"));
        Assert.Equal("false", cut.Find(".navbar-toggler").GetAttribute("aria-expanded"));
    }
}
