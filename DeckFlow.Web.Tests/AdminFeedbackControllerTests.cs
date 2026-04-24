using DeckFlow.Web.Controllers.Admin;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class AdminFeedbackControllerTests
{
    [Fact]
    public async Task Index_RendersItemsFromStore_WithFilters()
    {
        var store = new FakeStore();
        store.Items.Add(NewItem(1, FeedbackStatus.New, FeedbackType.Bug));
        var controller = Build(store);

        var result = await controller.Index(FeedbackStatus.New, null, 1);

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<AdminFeedbackListViewModel>(view.Model);
        Assert.Single(vm.Items);
        Assert.Equal(FeedbackStatus.New, vm.StatusFilter);
    }

    [Fact]
    public async Task Detail_UnknownId_ReturnsNotFound()
    {
        var controller = Build(new FakeStore());
        var result = await controller.Detail(999);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Detail_Known_ReturnsView()
    {
        var store = new FakeStore();
        store.Items.Add(NewItem(7, FeedbackStatus.Read, FeedbackType.Comment));
        var controller = Build(store);
        var result = await controller.Detail(7);
        var view = Assert.IsType<ViewResult>(result);
        var item = Assert.IsType<FeedbackItem>(view.Model);
        Assert.Equal(7, item.Id);
    }

    [Fact]
    public async Task Apply_MarkRead_UpdatesStatus_AndRedirects()
    {
        var store = new FakeStore();
        store.Items.Add(NewItem(3, FeedbackStatus.New, FeedbackType.Bug));
        var controller = Build(store);
        var result = await controller.Apply(3, "markRead");
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Single(store.StatusUpdates);
        Assert.Equal((3L, FeedbackStatus.Read), store.StatusUpdates[0]);
    }

    [Fact]
    public async Task Apply_Archive_UpdatesStatus()
    {
        var store = new FakeStore();
        store.Items.Add(NewItem(4, FeedbackStatus.New, FeedbackType.Bug));
        var controller = Build(store);
        await controller.Apply(4, "archive");
        Assert.Equal((4L, FeedbackStatus.Archived), store.StatusUpdates[0]);
    }

    [Fact]
    public async Task Apply_Delete_CallsDelete()
    {
        var store = new FakeStore();
        store.Items.Add(NewItem(5, FeedbackStatus.Read, FeedbackType.Bug));
        var controller = Build(store);
        await controller.Apply(5, "delete");
        Assert.Contains(5L, store.Deletes);
    }

    [Fact]
    public async Task Apply_UnknownOp_Returns400()
    {
        var controller = Build(new FakeStore());
        var result = await controller.Apply(1, "bogus");
        Assert.IsType<BadRequestResult>(result);
    }

    private static AdminFeedbackController Build(IFeedbackStore store)
    {
        var httpContext = new DefaultHttpContext();
        return new AdminFeedbackController(store)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider()),
        };
    }

    private static FeedbackItem NewItem(long id, FeedbackStatus status, FeedbackType type) =>
        new(id, DateTime.UtcNow, type, "msg with enough chars", null, null, null, null, null, status);

    private sealed class FakeStore : IFeedbackStore
    {
        public List<FeedbackItem> Items { get; } = new();
        public List<(long Id, FeedbackStatus Status)> StatusUpdates { get; } = new();
        public List<long> Deletes { get; } = new();

        public Task<long> AddAsync(FeedbackSubmission s, FeedbackRequestContext c, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<FeedbackItem?> GetAsync(long id, CancellationToken ct = default) =>
            Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task<IReadOnlyList<FeedbackItem>> ListAsync(FeedbackListQuery query, CancellationToken ct = default)
        {
            var filtered = Items.AsEnumerable();
            if (query.Status.HasValue) filtered = filtered.Where(i => i.Status == query.Status.Value);
            if (query.Type.HasValue) filtered = filtered.Where(i => i.Type == query.Type.Value);
            return Task.FromResult<IReadOnlyList<FeedbackItem>>(filtered.ToList());
        }
        public Task<int> CountAsync(FeedbackStatus? status, FeedbackType? type, CancellationToken ct = default) =>
            Task.FromResult(Items.Count(i => (!status.HasValue || i.Status == status.Value) && (!type.HasValue || i.Type == type.Value)));
        public Task<IReadOnlyDictionary<FeedbackStatus, int>> CountsByStatusAsync(CancellationToken ct = default)
        {
            var map = new Dictionary<FeedbackStatus, int>
            {
                [FeedbackStatus.New] = Items.Count(i => i.Status == FeedbackStatus.New),
                [FeedbackStatus.Read] = Items.Count(i => i.Status == FeedbackStatus.Read),
                [FeedbackStatus.Archived] = Items.Count(i => i.Status == FeedbackStatus.Archived),
            };
            return Task.FromResult<IReadOnlyDictionary<FeedbackStatus, int>>(map);
        }
        public Task UpdateStatusAsync(long id, FeedbackStatus status, CancellationToken ct = default)
        {
            StatusUpdates.Add((id, status));
            return Task.CompletedTask;
        }
        public Task DeleteAsync(long id, CancellationToken ct = default)
        {
            Deletes.Add(id);
            return Task.CompletedTask;
        }
        public string HashIp(string? ip) => ip ?? "";
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
