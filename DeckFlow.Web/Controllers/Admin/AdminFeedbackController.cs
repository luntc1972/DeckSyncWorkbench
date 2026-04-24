using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeckFlow.Web.Controllers.Admin;

public sealed class AdminFeedbackListViewModel
{
    public IReadOnlyList<FeedbackItem> Items { get; init; } = Array.Empty<FeedbackItem>();
    public FeedbackStatus? StatusFilter { get; init; }
    public FeedbackType? TypeFilter { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public int TotalCount { get; init; }
    public IReadOnlyDictionary<FeedbackStatus, int> CountsByStatus { get; init; } =
        new Dictionary<FeedbackStatus, int>();
    public int TotalPages => (int)Math.Ceiling((double)Math.Max(TotalCount, 1) / Math.Max(PageSize, 1));
}

[Route("Admin/Feedback")]
public sealed class AdminFeedbackController : Controller
{
    private readonly IFeedbackStore _store;

    public AdminFeedbackController(IFeedbackStore store)
    {
        _store = store;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(FeedbackStatus? status = FeedbackStatus.New, FeedbackType? type = null, int page = 1)
    {
        page = Math.Max(page, 1);
        const int pageSize = 50;
        var query = new FeedbackListQuery { Status = status, Type = type, Page = page, PageSize = pageSize };
        var items = await _store.ListAsync(query);
        var total = await _store.CountAsync(status, type);
        var counts = await _store.CountsByStatusAsync();

        var vm = new AdminFeedbackListViewModel
        {
            Items = items,
            StatusFilter = status,
            TypeFilter = type,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            CountsByStatus = counts,
        };
        return View(vm);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Detail(long id)
    {
        var item = await _store.GetAsync(id);
        if (item is null) return NotFound();
        return View(item);
    }

    [HttpPost("{id:long}/{op}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(long id, string op)
    {
        switch (op?.ToLowerInvariant())
        {
            case "markread":
                await _store.UpdateStatusAsync(id, FeedbackStatus.Read);
                break;
            case "archive":
                await _store.UpdateStatusAsync(id, FeedbackStatus.Archived);
                break;
            case "delete":
                await _store.DeleteAsync(id);
                break;
            default:
                return BadRequest();
        }

        TempData["AdminFeedbackAction"] = $"{op} applied to #{id}";
        return RedirectToAction(nameof(Index));
    }
}
