using System.Xml.Linq;
using DeckFlow.Web.Controllers;
using DeckFlow.Web.Infrastructure;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.FeatureFlags;
using DeckFlow.Web.Services.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Tests for <see cref="SitemapController"/> and related SEO response headers.
/// </summary>
public sealed class SitemapControllerTests
{
    [Fact]
    public void ContextualCrossToolLinks_are_flag_gated_and_use_descriptive_anchors()
    {
        AssertRelatedToolLink("Manabase.cshtml", "tool.deck-analysis.enabled", "~/deck-analysis", "Analyze your Commander deck");
        AssertRelatedToolLink("DeckAnalysis.cshtml", "tool.deck-primer.enabled", "~/deck-primer", "Commander deck primer builder");
        AssertRelatedToolLink("DeckComparison.cshtml", "tool.deck-history.enabled", "~/deck-history", "deck version tracker");
        AssertRelatedToolLink("CedhMetaGap.cshtml", "tool.deck-comparison.enabled", "~/deck-comparison", "Commander deck comparison tool");
        AssertRelatedToolLink("Bracket.cshtml", "tool.deck-analysis.enabled", "~/deck-analysis", "analyze your Commander deck");
        AssertRelatedToolLink("CardLookup.cshtml", "tool.mechanic-lookup.enabled", "~/mechanic-lookup", "Magic mechanic lookup");
    }

    [Fact]
    public async Task RobotsTxt_contains_expected_disallow_rules_and_absolute_sitemap_url()
    {
        var controller = CreateController();

        var result = Assert.IsType<ContentResult>(controller.RobotsTxt());

        Assert.Equal("text/plain", result.ContentType);
        Assert.NotNull(result.Content);
        Assert.Contains("User-agent: *", result.Content, StringComparison.Ordinal);
        Assert.Contains("Disallow: /Admin", result.Content, StringComparison.Ordinal);
        Assert.Contains("Disallow: /api", result.Content, StringComparison.Ordinal);
        Assert.Contains("Disallow: /swagger", result.Content, StringComparison.Ordinal);
        Assert.Contains("Sitemap: https://deckflow.test/sitemap.xml", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void SitemapXml_returns_well_formed_absolute_urls_for_indexable_routes()
    {
        var controller = CreateController();

        var result = Assert.IsType<ContentResult>(controller.SitemapXml());

        Assert.Equal("application/xml", result.ContentType);
        Assert.NotNull(result.Content);

        var document = XDocument.Parse(result.Content);
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        var urls = document.Root!.Elements(ns + "url")
            .Select(element => element.Element(ns + "loc")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();

        Assert.Contains("https://deckflow.test/", urls);
        Assert.Contains("https://deckflow.test/help", urls);
        Assert.Contains("https://deckflow.test/deckflow-bridge", urls);
        Assert.Contains("https://deckflow.test/set-upgrade-analysis", urls);
        Assert.DoesNotContain("https://deckflow.test/content-kb", urls);
        Assert.Contains("https://deckflow.test/feedback", urls);
        Assert.Contains("https://deckflow.test/manabase", urls);
        Assert.Contains("https://deckflow.test/bracket", urls);
        Assert.Contains("https://deckflow.test/deck-history", urls);
        Assert.All(urls, url => Assert.StartsWith("https://deckflow.test", url, StringComparison.Ordinal));
    }

    [Fact]
    public void SitemapXml_omits_a_tool_when_its_flag_is_disabled_and_restores_it_when_enabled()
    {
        var flags = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["tool.bracket.enabled"] = false,
        });

        var disabledUrls = GetSitemapUrls(CreateController(flags));

        Assert.DoesNotContain("https://deckflow.test/bracket", disabledUrls);

        flags.Flags["tool.bracket.enabled"] = true;

        var enabledUrls = GetSitemapUrls(CreateController(flags));

        Assert.Contains("https://deckflow.test/bracket", enabledUrls);
    }

    [Fact]
    public void SitemapXml_drops_the_set_upgrade_landing_page_with_the_deck_analysis_flag()
    {
        // The landing page has no tool row of its own; it rides on deck-analysis's
        // AdditionalRoutes. Without that join the page is unflagged and would stay in the
        // sitemap after the workflow it describes goes dark — the defect T-1 exists to fix.
        var flags = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["tool.deck-analysis.enabled"] = false,
        });

        var disabledUrls = GetSitemapUrls(CreateController(flags));

        Assert.DoesNotContain("https://deckflow.test/set-upgrade-analysis", disabledUrls);
        Assert.DoesNotContain("https://deckflow.test/deck-analysis", disabledUrls);

        flags.Flags["tool.deck-analysis.enabled"] = true;

        var enabledUrls = GetSitemapUrls(CreateController(flags));

        Assert.Contains("https://deckflow.test/set-upgrade-analysis", enabledUrls);
    }

    [Fact]
    public void SitemapXml_includes_all_visible_help_topics_and_omits_flag_hidden_topics()
    {
        var visible = new HelpTopic("visible", "Visible", "s", 10, "<p>visible</p>");
        var hidden = new HelpTopic("hidden", "Hidden", "s", 20, "<p>hidden</p>", "tool.hidden.enabled");
        var helpContent = new StubHelpContentService(visible, hidden);
        var flags = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["tool.hidden.enabled"] = false,
        });

        var urls = GetSitemapUrls(CreateController(flags, helpContent));

        Assert.Contains("https://deckflow.test/help/visible", urls);
        Assert.DoesNotContain("https://deckflow.test/help/hidden", urls);
    }

    [Fact]
    public void SitemapXml_omits_every_help_topic_when_the_global_help_flag_is_disabled()
    {
        var visible = new HelpTopic("visible", "Visible", "s", 10, "<p>visible</p>");
        var gated = new HelpTopic("gated", "Gated", "s", 20, "<p>gated</p>", "tool.gated.enabled");
        var helpContent = new StubHelpContentService(visible, gated);
        var flags = new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            ["tool.help.enabled"] = false,
            ["tool.gated.enabled"] = true,
        });

        var urls = GetSitemapUrls(CreateController(flags, helpContent));

        Assert.DoesNotContain("https://deckflow.test/help/visible", urls);
        Assert.DoesNotContain("https://deckflow.test/help/gated", urls);
        Assert.DoesNotContain("https://deckflow.test/help", urls);
    }

    [Fact]
    public void SitemapXml_includes_every_project_help_topic_when_all_flags_are_enabled()
    {
        var helpContent = new HelpContentService(FindProjectHelpRoot());
        var urls = GetSitemapUrls(CreateController(helpContent: helpContent));

        var topics = helpContent.GetAll();
        Assert.Equal(19, topics.Count);
        Assert.All(topics, topic => Assert.Contains($"https://deckflow.test/help/{topic.Slug}", urls));
    }

    [Fact(Skip = "Validating OnStarting response headers here would require full TestServer host plumbing, which is out of scope for this change.")]
    public async Task Security_headers_add_admin_noindex_only_for_admin_paths()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseDeckFlowSecurityHeaders();
        app.Run(context => context.Response.WriteAsync("ok"));
        var pipeline = app.Build();

        var adminContext = new DefaultHttpContext();
        adminContext.Response.Body = new MemoryStream();
        adminContext.Request.Path = "/Admin";

        await pipeline(adminContext);
        await adminContext.Response.StartAsync();

        Assert.Equal("noindex, nofollow", adminContext.Response.Headers["X-Robots-Tag"].ToString());

        var publicContext = new DefaultHttpContext();
        publicContext.Response.Body = new MemoryStream();
        publicContext.Request.Path = "/";

        await pipeline(publicContext);
        await publicContext.Response.StartAsync();

        Assert.False(publicContext.Response.Headers.ContainsKey("X-Robots-Tag"));
    }

    private static List<string> GetSitemapUrls(SitemapController controller)
    {
        var result = Assert.IsType<ContentResult>(controller.SitemapXml());
        var document = XDocument.Parse(Assert.IsType<string>(result.Content));
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        return document.Root!.Elements(ns + "url")
            .Select(element => element.Element(ns + "loc")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();
    }

    private static SitemapController CreateController(
        IFeatureFlagCache? featureFlags = null,
        IHelpContentService? helpContent = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("deckflow.test");

        return new SitemapController(
            new ToolRegistry(),
            featureFlags ?? new FakeFeatureFlagCache(),
            helpContent ?? new StubHelpContentService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            },
        };
    }

    private static string FindProjectHelpRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var helpRoot = Path.Combine(current.FullName, "DeckFlow.Web", "Help");
            if (Directory.Exists(helpRoot))
                return helpRoot;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find DeckFlow.Web/Help from the test working directory.");
    }

    private static void AssertRelatedToolLink(string viewName, string flagKey, string route, string anchorText)
    {
        var projectRoot = Path.GetDirectoryName(FindProjectHelpRoot())!;
        var view = File.ReadAllText(Path.Combine(projectRoot, "Views", "Deck", viewName));

        Assert.Contains($"@if (FlagCache.IsEnabled(\"{flagKey}\"))", view, StringComparison.Ordinal);
        Assert.Contains($"href=\"@Url.Content(\"{route}\")\"", view, StringComparison.Ordinal);
        Assert.Contains(anchorText, view, StringComparison.Ordinal);
    }

    private sealed class StubHelpContentService : IHelpContentService
    {
        private readonly IReadOnlyList<HelpTopic> topics;

        public StubHelpContentService(params HelpTopic[] topics) => this.topics = topics;

        public IReadOnlyList<HelpTopic> GetAll() => topics;

        public HelpTopic? GetBySlug(string slug) =>
            topics.FirstOrDefault(topic => string.Equals(topic.Slug, slug, StringComparison.OrdinalIgnoreCase));

        public bool IsTopicVisible(HelpTopic topic, IFeatureFlagCache featureFlags) =>
            topic.RequiresFlag is null || featureFlags.IsEnabled(topic.RequiresFlag);
    }
}
