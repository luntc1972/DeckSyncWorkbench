using DeckFlow.Web.Controllers;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DeckFlow.Web.Tests;

public class AboutControllerTests
{
    private sealed class FixedVersionService : IVersionService
    {
        public string GetVersion() => "9.9.9-test";
    }

    [Fact]
    public void Index_returns_view_with_populated_AboutViewModel()
    {
        var controller = new AboutController(new FixedVersionService());

        var result = Assert.IsType<ViewResult>(controller.Index());
        var model = Assert.IsType<AboutViewModel>(result.Model);

        Assert.False(string.IsNullOrWhiteSpace(model.Tagline));
        Assert.Equal("9.9.9-test", model.Version);
        Assert.Equal("https://github.com/luntc1972/DeckFlow", model.RepositoryUrl);
        Assert.NotEmpty(model.Credits);
    }

    [Fact]
    public void Credits_include_all_expected_sources()
    {
        var controller = new AboutController(new FixedVersionService());
        var result = (ViewResult)controller.Index();
        var model = (AboutViewModel)result.Model!;

        var names = model.Credits.Select(c => c.Name).ToList();
        Assert.Contains("Scryfall", names);
        Assert.Contains("Commander Spellbook", names);
        Assert.Contains("EDH Top 16", names);
        Assert.Contains("Wizards of the Coast", names);
        Assert.Contains("Archidekt", names);
        Assert.Contains("Moxfield", names);
    }

    [Fact]
    public void All_credit_urls_are_absolute_https()
    {
        var controller = new AboutController(new FixedVersionService());
        var result = (ViewResult)controller.Index();
        var model = (AboutViewModel)result.Model!;

        Assert.All(model.Credits, c => Assert.StartsWith("https://", c.Url));
    }
}
