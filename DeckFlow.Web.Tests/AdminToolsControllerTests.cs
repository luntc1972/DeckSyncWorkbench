using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DeckFlow.Web.Controllers.Admin;
using DeckFlow.Web.Services.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Controller tests for the dedicated /Admin/Tools toggle surface.
/// </summary>
public sealed class AdminToolsControllerTests
{
    [Fact]
    public void Index_ListsAllRegistryTools_GroupedBySection_WithDisabledCoreWarningList()
    {
        var controller = Build(
            store: new FakeFeatureFlagStore(),
            cache: new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                ["tool.deck-analysis.enabled"] = false,
            }),
            crossOrigin: false);

        var result = controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        Assert.NotNull(view.Model);

        var sections = ReadSequence(view.Model, "Sections");
        Assert.Equal(4, sections.Count);
        Assert.Equal(new[] { ToolNavSection.Analyze, ToolNavSection.Build, ToolNavSection.Reference, ToolNavSection.Categories },
            sections.Select(section => ReadProperty<ToolNavSection>(section, "Section")).ToArray());
        Assert.Equal(17, sections.Sum(section => ReadSequence(section, "Tools").Count));

        var disabledCore = ReadStringSequence(view.Model, "DisabledCoreToolLabels");
        Assert.Equal(new[] { "Deck Analysis" }, disabledCore);
    }

    [Fact]
    public async Task Toggle_CrossOrigin_Returns403_AndDoesNotWrite()
    {
        var store = new FakeFeatureFlagStore();
        var cache = new FakeFeatureFlagCache();
        var controller = Build(store, cache, crossOrigin: true);

        var result = await controller.Toggle("tool.deck-analysis.enabled", enabled: false, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        Assert.Equal(0, store.SetCallCount);
        Assert.Equal(0, cache.ReloadCallCount);
    }

    [Fact]
    public async Task Toggle_SameOrigin_UnknownTool_ReturnsBadRequest_AndDoesNotWrite()
    {
        var store = new FakeFeatureFlagStore();
        var cache = new FakeFeatureFlagCache();
        var controller = Build(store, cache, crossOrigin: false);

        var result = await controller.Toggle("service.scryfall-tagger.enabled", enabled: false, default);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Unknown tool.", badRequest.Value);
        Assert.Equal(0, store.SetCallCount);
        Assert.Equal(0, cache.ReloadCallCount);
    }

    [Fact]
    public async Task Toggle_SameOrigin_BlankKey_ReturnsBadRequest_AndDoesNotWrite()
    {
        var store = new FakeFeatureFlagStore();
        var cache = new FakeFeatureFlagCache();
        var controller = Build(store, cache, crossOrigin: false);

        var result = await controller.Toggle(string.Empty, enabled: false, default);

        Assert.IsType<BadRequestResult>(result);
        Assert.Equal(0, store.SetCallCount);
        Assert.Equal(0, cache.ReloadCallCount);
    }

    [Fact]
    public async Task Toggle_SameOrigin_KnownTool_Writes_Reloads_AndRedirects()
    {
        var store = new FakeFeatureFlagStore();
        var cache = new FakeFeatureFlagCache();
        var controller = Build(store, cache, crossOrigin: false);

        var result = await controller.Toggle("tool.deck-primer.enabled", enabled: false, default);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(1, store.SetCallCount);
        Assert.Equal("tool.deck-primer.enabled", store.LastSetKey);
        Assert.False(store.LastSetEnabled);
        Assert.Equal(1, cache.ReloadCallCount);
        Assert.Equal("Tool 'Deck Primer' is now disabled.", controller.TempData["AdminToolsAction"]);
        Assert.Null(controller.TempData["AdminToolsWarning"]);
    }

    [Fact]
    public async Task Toggle_DisablingCoreTool_Warns_ButStillWritesAndRedirects()
    {
        var store = new FakeFeatureFlagStore();
        var cache = new FakeFeatureFlagCache();
        var controller = Build(store, cache, crossOrigin: false);

        var result = await controller.Toggle("tool.deck-analysis.enabled", enabled: false, default);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(1, store.SetCallCount);
        Assert.Equal("tool.deck-analysis.enabled", store.LastSetKey);
        Assert.False(store.LastSetEnabled);
        Assert.Equal(1, cache.ReloadCallCount);
        Assert.Equal("Warning: 'Deck Analysis' is a core Analyze workflow and is now hidden.", controller.TempData["AdminToolsWarning"]);
    }

    private static AdminToolsController Build(FakeFeatureFlagStore store, FakeFeatureFlagCache cache, bool crossOrigin)
    {
        var controller = new AdminToolsController(store, cache, new ToolRegistry());
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("deckflow.test");
        httpContext.Request.Headers.Origin = crossOrigin ? "https://evil.test" : "https://deckflow.test";

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new StubTempDataProvider());
        return controller;
    }

    private static T ReadProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(instance));
    }

    private static IReadOnlyList<object> ReadSequence(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var value = Assert.IsAssignableFrom<System.Collections.IEnumerable>(property!.GetValue(instance));
        return value.Cast<object>().ToArray();
    }

    private static IReadOnlyList<string> ReadStringSequence(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var value = Assert.IsAssignableFrom<System.Collections.IEnumerable>(property!.GetValue(instance));
        return value.Cast<string>().ToArray();
    }

    private sealed class StubTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
