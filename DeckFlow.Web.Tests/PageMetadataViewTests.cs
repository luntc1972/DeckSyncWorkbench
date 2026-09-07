using System.Text.RegularExpressions;
using DeckFlow.Web.Seo;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// File-level SEO regression guard: every public, indexable page must set a unique
/// per-page meta description (<c>ViewData["Description"]</c>) so search engines do not
/// see the shared default description site-wide, and the shared layout must render the
/// computed <c>pageTitle</c> (not raw <c>ViewData["Title"]</c>, which left a dangling
/// "- DeckFlow" when the title was empty).
/// </summary>
public sealed class PageMetadataViewTests
{
    private static readonly IReadOnlyDictionary<string, (string Folder, string File)> IndexableViewFiles =
        new Dictionary<string, (string Folder, string File)>(StringComparer.Ordinal)
        {
            ["/"] = ("Deck", "Home.cshtml"),
            ["/sync"] = ("Deck", "DeckSync.cshtml"),
            ["/convert"] = ("Deck", "DeckConvert.cshtml"),
            ["/card-lookup"] = ("Deck", "CardLookup.cshtml"),
            ["/mechanic-lookup"] = ("Deck", "MechanicLookup.cshtml"),
            ["/deck-analysis"] = ("Deck", "DeckAnalysis.cshtml"),
            ["/set-upgrade-analysis"] = ("SetUpgradeAnalysis", "Index.cshtml"),
            ["/deck-comparison"] = ("Deck", "DeckComparison.cshtml"),
            ["/cedh-meta-gap"] = ("Deck", "CedhMetaGap.cshtml"),
            ["/deck-primer"] = ("Deck", "DeckPrimer.cshtml"),
            ["/suggest-categories"] = ("Deck", "SuggestCategories.cshtml"),
            ["/commander-categories"] = ("Commander", "CommanderCategories.cshtml"),
            ["/judge-questions"] = ("Deck", "JudgeQuestions.cshtml"),
            ["/manabase"] = ("Deck", "Manabase.cshtml"),
            ["/bracket"] = ("Deck", "Bracket.cshtml"),
            ["/deck-history"] = ("Deck", "DeckHistory.cshtml"),
            ["/cut-lab"] = ("Deck", "CutLab.cshtml"),
            ["/deck-modules"] = ("Deck", "DeckModules.cshtml"),
            ["/deckflow-bridge"] = ("Bridge", "Index.cshtml"),
            ["/help"] = ("Help", "Index.cshtml"),
            ["/about"] = ("About", "Index.cshtml"),
            ["/feedback"] = ("Feedback", "Index.cshtml"),
        };

    private static readonly Regex DefaultDescriptionLiteral = new(
        "const string defaultDescription\\s*=\\s*\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled);

    // Matches a string-literal assignment: ViewData["Description"] = "....";
    private static readonly Regex DescriptionLiteral = new(
        "ViewData\\[\"Description\"\\]\\s*=\\s*\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled);

    private static IReadOnlyList<(string Folder, string File)> IndexableViews => SeoPaths.Indexable
        .Select(path => IndexableViewFiles.TryGetValue(path, out var view)
            ? view
            : throw new InvalidOperationException($"No view file is mapped for indexable path '{path}'."))
        .ToArray();

    private static string DefaultDescription
    {
        get
        {
            var match = DefaultDescriptionLiteral.Match(ReadView("Shared", "_Layout.cshtml"));
            return match.Success
                ? match.Groups["value"].Value
                : throw new InvalidOperationException("_Layout.cshtml does not define defaultDescription as a string literal.");
        }
    }

    public static TheoryData<string, string> IndexableViewData()
    {
        var data = new TheoryData<string, string>();
        foreach (var (folder, file) in IndexableViews)
        {
            data.Add(folder, file);
        }

        return data;
    }

    [Fact]
    public void EveryIndexablePath_HasAMappedViewFile()
    {
        Assert.Equal(SeoPaths.Indexable.Count, IndexableViews.Count);
    }

    [Theory]
    [MemberData(nameof(IndexableViewData))]
    public void IndexableView_SetsNonDefaultMetaDescription(string folder, string file)
    {
        var content = ReadView(folder, file);
        var match = DescriptionLiteral.Match(content);

        Assert.True(match.Success, $"{folder}/{file} does not set a string-literal ViewData[\"Description\"].");

        var description = match.Groups["value"].Value;
        Assert.False(string.IsNullOrWhiteSpace(description), $"{folder}/{file} has an empty meta description.");
        Assert.NotEqual(DefaultDescription, description);
    }

    [Fact]
    public void IndexableViews_AllHaveDistinctMetaDescriptions()
    {
        var descriptions = IndexableViews
            .Select(view => DescriptionLiteral.Match(ReadView(view.Folder, view.File)).Groups["value"].Value)
            .ToList();

        var duplicates = descriptions
            .GroupBy(value => value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, $"Duplicate meta descriptions: {string.Join(" | ", duplicates)}");
    }

    [Theory]
    [InlineData("Bracket.cshtml", "MTG Commander Bracket Checker", "Check a Commander deck against the official Magic: The Gathering bracket system and get a local classification, target-bracket gaps, and recommended cuts.")]
    [InlineData("DeckHistory.cshtml", "MTG Commander Deck Version Tracker", "Track Commander deck versions in a local history, compare any two snapshots, and get a card-by-card changelog with an AI-ready review prompt.")]
    [InlineData("CutLab.cshtml", "Cut Lab", "Trim an oversized Commander card pool with a structured cut workspace that identifies removable cards, protects locked roles, and exports your finished deck.")]
    public void UncoveredDeckViews_SetPlannedTitleAndMetaDescription(string file, string title, string description)
    {
        var content = ReadView("Deck", file);

        Assert.Contains($"ViewData[\"Title\"] = \"{title}\"", content, StringComparison.Ordinal);
        Assert.Contains($"ViewData[\"Description\"] = \"{description}\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpTopicDetail_SetsDescriptionFromSummary()
    {
        var content = ReadView("Help", "Topic.cshtml");

        Assert.Contains("ViewData[\"Description\"] = Model.Summary", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_RendersComputedPageTitle_NotRawViewData()
    {
        var content = ReadView("Shared", "_Layout.cshtml");

        // The <title> tag must use the computed pageTitle, and that computation must keep
        // its collapse-when-empty / suffix-otherwise logic so the tag matches the og/twitter
        // titles and never emits a dangling "- DeckFlow".
        Assert.Contains("<title>@pageTitle</title>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<title>@ViewData[\"Title\"] - DeckFlow</title>", content, StringComparison.Ordinal);
        Assert.Contains("? \"DeckFlow\" : $\"{pageTitle} - DeckFlow\"", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Manabase")]
    [InlineData("/manabase/")]
    [InlineData("/manabase")]
    public void Layout_normalizes_each_manabase_request_to_the_same_canonical_and_open_graph_url(string requestPath)
    {
        var content = ReadView("Shared", "_Layout.cshtml");
        var normalizedPath = SeoPaths.Normalize(requestPath);
        var canonicalUrl = $"https://deckflow.test{normalizedPath}";

        Assert.Equal("https://deckflow.test/manabase", canonicalUrl);
        Assert.Contains("var canonicalPath = DeckFlow.Web.Seo.SeoPaths.Normalize(requestPath.Value);", content, StringComparison.Ordinal);
        Assert.Contains("var canonicalUrl = $\"{requestScheme}://{requestHost}{requestPathBase}{canonicalPath}\";", content, StringComparison.Ordinal);
        Assert.Contains("<link rel=\"canonical\" href=\"@canonicalUrl\" />", content, StringComparison.Ordinal);
        Assert.Contains("<meta property=\"og:url\" content=\"@canonicalUrl\" />", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_lede_names_commander_cedh_and_the_core_deck_capabilities()
    {
        var content = ReadView("Deck", "Home.cshtml");

        Assert.Contains("Magic: The Gathering", content, StringComparison.Ordinal);
        Assert.Contains("Commander", content, StringComparison.Ordinal);
        Assert.Contains("cEDH", content, StringComparison.Ordinal);
        Assert.Contains("mana base analysis", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bracket checking", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deck comparison", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deck primers", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("version tracking", content, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadView(string folder, string file)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "DeckFlow.Web",
            "Views",
            folder,
            file));
}
