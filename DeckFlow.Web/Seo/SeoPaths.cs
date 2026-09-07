using System;
using System.Collections.Generic;
using System.Linq;

namespace DeckFlow.Web.Seo;

/// <summary>
/// Drives both the JSON-LD graph emitted by <see cref="StructuredDataBuilder"/> and
/// whether <c>_Layout</c> renders the share bar; a single bool could not express
/// “ranks richly but is not shared”.
/// </summary>
internal enum SeoPageKind
{
    /// <summary>Home graph (WebSite + Organization + SoftwareApplication); shareable.</summary>
    Home,
    /// <summary>WebPage + BreadcrumbList; shareable.</summary>
    Tool,
    /// <summary>WebPage + BreadcrumbList; shareable. Content describing a capability, which is what people share.</summary>
    Landing,
    /// <summary>WebPage + BreadcrumbList; not shareable. A means, not a destination — e.g. an extension-install page.</summary>
    Utility,
    /// <summary>The bare WebSite fallback node; not shareable. Also returned for any unregistered path.</summary>
    Static,
}

/// <summary>
/// Single source of truth for the public page paths. Consumed by
/// <see cref="Controllers.SitemapController"/> (sitemap + robots) and
/// <see cref="StructuredDataBuilder"/> (JSON-LD) so the two never drift apart.
/// </summary>
public static class SeoPaths
{
    /// <summary>
    /// Every page and its independently declared indexability and page kind.
    /// Each page is declared here exactly once so the sitemap and structured-data views
    /// cannot drift.
    /// </summary>
    private static readonly SeoPage[] Pages =
    {
        new("/", true, SeoPageKind.Home),
        new("/sync", true, SeoPageKind.Tool),
        new("/convert", true, SeoPageKind.Tool),
        new("/card-lookup", true, SeoPageKind.Tool),
        new("/mechanic-lookup", true, SeoPageKind.Tool),
        new("/deck-analysis", true, SeoPageKind.Tool),
        new("/set-upgrade-analysis", true, SeoPageKind.Landing),
        new("/deck-comparison", true, SeoPageKind.Tool),
        new("/cedh-meta-gap", true, SeoPageKind.Tool),
        new("/deck-primer", true, SeoPageKind.Tool),
        new("/suggest-categories", true, SeoPageKind.Tool),
        new("/commander-categories", true, SeoPageKind.Tool),
        new("/judge-questions", true, SeoPageKind.Tool),
        new("/manabase", true, SeoPageKind.Tool),
        new("/bracket", true, SeoPageKind.Tool),
        new("/deck-history", true, SeoPageKind.Tool),
        new("/cut-lab", true, SeoPageKind.Tool),
        new("/deck-modules", true, SeoPageKind.Tool),
        new("/content-kb", false, SeoPageKind.Tool),
        new("/deckflow-bridge", true, SeoPageKind.Utility),
        new("/help", true, SeoPageKind.Static),
        new("/about", true, SeoPageKind.Static),
        new("/feedback", true, SeoPageKind.Static),
    };

    /// <summary>
    /// Every page declared indexable in <see cref="Pages"/>, in sitemap order.
    /// </summary>
    public static readonly IReadOnlyList<string> Indexable = Pages
        .Where(page => page.IsIndexable)
        .Select(page => page.Path)
        .ToArray();

    /// <summary>
    /// Every page with <see cref="SeoPageKind.Tool"/> in <see cref="Pages"/>. Tool kind is
    /// independent of indexability, allowing flag-gated tools to retain their share bar and tool JSON-LD.
    /// </summary>
    public static readonly IReadOnlySet<string> Tools = new HashSet<string>(
        Pages.Where(page => page.Kind == SeoPageKind.Tool).Select(page => page.Path),
        StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, SeoPageKind> Kinds =
        Pages.ToDictionary(page => page.Path, page => page.Kind, StringComparer.Ordinal);

    private sealed record SeoPage(string Path, bool IsIndexable, SeoPageKind Kind);

    /// <summary>
    /// Normalizes a request path for matching: lower-invariant, trailing slash stripped
    /// (except root). Null/empty becomes "/".
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        var lower = path.ToLowerInvariant();
        if (lower.Length > 1 && lower.EndsWith('/'))
        {
            lower = lower.TrimEnd('/');
        }

        return lower.Length == 0 ? "/" : lower;
    }

    /// <summary>Normalizes the path and returns <see cref="SeoPageKind.Static"/> for unregistered paths.</summary>
    internal static SeoPageKind KindOf(string? path)
    {
        var normalized = Normalize(path);
        return Kinds.TryGetValue(normalized, out var kind) ? kind : SeoPageKind.Static;
    }

    /// <summary>
    /// True when the page kind carries the share bar: home, tool, or landing.
    /// </summary>
    public static bool IsShareablePage(string? path)
    {
        return KindOf(path) is SeoPageKind.Home or SeoPageKind.Tool or SeoPageKind.Landing;
    }
}
