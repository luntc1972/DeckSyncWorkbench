using System.Text.RegularExpressions;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Guards Cut Lab's desktop workspace treatment without changing shared tool-page panels.
/// </summary>
public sealed class CutLabDesktopLayoutCssTests
{
    [Fact]
    public void CutLabDesktopLayout_IsScopedToWorkspaceAtDesktopBreakpoint()
    {
        string content = ReadSiteCommonCss();

        Assert.Matches(
            new Regex(
                "@media\\s*\\(min-width:\\s*1024px\\)[^{]*\\{(?:(?!@media).)*\\.cutlab-workspace",
                RegexOptions.Singleline),
            content);
        Assert.Matches(
            new Regex(
                "\\.cutlab-workspace\\s+>\\s+\\.result-panel[^{}]*\\{[^}]*border:\\s*1px\\s+solid\\s+var\\(--line\\)",
                RegexOptions.Singleline),
            content);
    }

    [Fact]
    public void CutLabDesktopLayout_UsesWorkspaceWrapperWithoutReplacingWorkflowHooks()
    {
        string content = ReadCutLabView();

        Assert.Contains("<div class=\"cutlab-workspace\">", content, StringComparison.Ordinal);
        Assert.Contains("data-cut-lab-intake-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cut-lab-decide-action", content, StringComparison.Ordinal);
        Assert.Contains("id=\"cut-lab-step-panel-1\"", content, StringComparison.Ordinal);
    }

    private static string ReadSiteCommonCss()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "DeckFlow.Web",
            "wwwroot",
            "css",
            "site-common.css"));

    private static string ReadCutLabView()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "DeckFlow.Web",
            "Views",
            "Deck",
            "CutLab.cshtml"));
}
