using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Guards the mobile back-to-top affordance for long tool pages.
/// </summary>
public sealed class BackToTopMobileStyleTests
{
    [Fact]
    public void MobileStyles_KeepBackToTopAvailableWithSafeAreaSpacing()
    {
        var content = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "DeckFlow.Web",
            "wwwroot",
            "css",
            "site-mobile.css"));

        Assert.Contains("bottom: max(1rem, env(safe-area-inset-bottom));", content, StringComparison.Ordinal);
        Assert.Contains("min-width: 44px;", content, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px;", content, StringComparison.Ordinal);
        Assert.DoesNotContain(".back-to-top-button {\n    display: none;", content, StringComparison.Ordinal);
    }
}
