using System.Text.RegularExpressions;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Guards Cut Lab's mobile workspace treatment without changing shared tool-page panels.
/// </summary>
public sealed class CutLabMobileLayoutCssTests
{
    [Fact]
    public void CutLabMobileLayout_IsScopedToWorkspaceAtMobileBreakpoint()
    {
        string content = ReadSiteMobileCss();

        Assert.Matches(
            new Regex(
                "@media\\s*\\(max-width:\\s*900px\\)[^{]*\\{(?:(?!@media).)*\\.cutlab-workspace",
                RegexOptions.Singleline),
            content);
        Assert.Matches(
            new Regex(
                "\\.cutlab-workspace\\s+button:not\\(\\.card-picker__add\\):not\\(\\.card-picker__remove\\)[^{}]*\\{[^}]*min-height:\\s*44px",
                RegexOptions.Singleline),
            content);
        Assert.Matches(
            new Regex(
                "\\.cutlab-workspace\\s+label\\.kb-chip[^{}]*\\{[^}]*min-height:\\s*44px",
                RegexOptions.Singleline),
            content);
        Assert.DoesNotMatch(
            new Regex(
                "\\.cutlab-workspace\\s+\\.kb-chip[^{}]*\\{[^}]*display:\\s*inline-flex",
                RegexOptions.Singleline),
            content);
        Assert.DoesNotMatch(
            new Regex(
                "\\.cutlab-workspace\\s+\\.cutlab-collapsible__summary[^{}]*\\{[^}]*display:\\s*inline-flex",
                RegexOptions.Singleline),
            content);
        Assert.Matches(
            new Regex(
                "\\.cutlab-workspace\\s+label\\.kb-chip|\\.cutlab-workspace\\s+\\.cutlab-anchor-nav\\s+a",
                RegexOptions.Singleline),
            content);
    }

    [Fact]
    public void CutLabMobileLayout_ContainsWideContentWithinWorkspace()
    {
        string content = ReadSiteMobileCss();

        Assert.Matches(
            new Regex(
                "@media\\s*\\(max-width:\\s*900px\\)[^{]*\\{(?:(?!@media).)*\\.cutlab-workspace\\s+textarea[^{}]*\\{[^}]*overflow-x:\\s*auto",
                RegexOptions.Singleline),
            content);
        Assert.Matches(
            new Regex(
                "@media\\s*\\(max-width:\\s*900px\\)[^{]*\\{(?:(?!@media).)*\\.cutlab-workspace\\s+table[^{}]*\\{[^}]*overflow-x:\\s*auto",
                RegexOptions.Singleline),
            content);
    }

    [Fact]
    public void CutLabMobileLayout_TreatsIntakeFormAsResultPanel()
    {
        string content = ReadSiteMobileCss();

        Assert.Equal(2, Regex.Matches(
            content,
            @"\.cutlab-workspace > \.cutlab-intake > form\.result-panel").Count);
    }

    private static string ReadSiteMobileCss()
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
}
