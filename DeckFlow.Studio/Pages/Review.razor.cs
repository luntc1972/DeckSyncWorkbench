using DeckFlow.Core.Content;
using DeckFlow.Core.Knowledge;
using DeckFlow.Studio.Services;
using DeckFlow.Studio.ViewModels;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace DeckFlow.Studio.Pages;

/// <summary>
/// Code-behind for the Review Queue page. The queue load, approval-status writes, and the
/// security-sensitive artifact path resolution + read live in <see cref="ReviewCoordinator"/>
/// (H1 split); this page keeps tab/selection/filter UI state, the artifact expand cache, busy
/// guards, cancellation, and re-render marshalling. The <c>RenderFragment</c> helpers stay in the
/// <c>.razor</c> markup because they use inline Razor template syntax. Behavior is identical to the
/// prior inline implementation.
/// </summary>
public partial class Review
{
    // ── Injected services ───────────────────────────────────────────────────
    // Why: all store I/O and artifact reads are delegated to the coordinator so this page is thin
    // UI glue and the path-containment security logic is unit-testable without bUnit (H1).
    [Inject]
    private ReviewCoordinator Coordinator { get; set; } = default!;

    // Deriver stays injected here: the markup binds <PublishStateBadge State="Deriver.Derive(...)">.
    [Inject]
    private PublishStateDeriver Deriver { get; set; } = default!;

    // ── Query parameters ────────────────────────────────────────────────────
    /// <summary>
    /// Seeds the initially selected tab from the query string; in-page tab buttons own the state thereafter.
    /// </summary>
    [Parameter]
    [SupplyParameterFromQuery(Name = "tab")]
    public string? Tab { get; set; }

    // ── Page state ──────────────────────────────────────────────────────────
    private bool _loading = true;
    private string _loadError = string.Empty;
    private string _activeTab = "pending";
    private bool _operationInFlight;
    private string _batchAction = string.Empty;
    private bool _allSelected;
    // SUI-05: creator filter over the entry list. Empty = "All creators".
    private string _reviewCreatorFilter = string.Empty;

    // ── Queue rows (mutable view models) ───────────────────────────────────
    private List<ReviewViewModel> _allRows = new();

    // ── Artifact expand cache ───────────────────────────────────────────────
    // Why: stored ArtifactPath already carries the content-kb/ prefix; resolve under the data root
    // (parent of ArtifactRoot) so the segment isn't doubled — combining with ArtifactRoot directly
    // resolves every file MISSING. Containment guard rejects rooted/.. paths.
    private readonly Dictionary<string, string?> _expandCache = new();

    // ── Prompt expand cache ─────────────────────────────────────────────────
    // Why: parallel to _expandCache — holds the paste-ready AI prompt (baked sibling {id}.prompt.md
    // when present, else reconstructed from the notes) so reviewers see exactly what a KB user would
    // paste into ChatGPT. Keyed by natural key, populated lazily on expand alongside the notes.
    private readonly Dictionary<string, string?> _promptCache = new();

    // ── Filtered view ──────────────────────────────────────────────────────
    // Tab filter (approval-status axis).
    private List<ReviewViewModel> _filteredRows =>
        _activeTab == "all"
            ? _allRows
            : _allRows.Where(r => r.ApprovalStatus == _activeTab).ToList();

    // SUI-05: creator filter layered on top of the tab filter. Rendering, ToggleSelectAll,
    // and the batch bar all route through this so a creator-hidden row can never be acted on.
    private List<ReviewViewModel> CreatorFilteredRows =>
        string.IsNullOrEmpty(_reviewCreatorFilter)
            ? _filteredRows
            : _filteredRows
                .Where(r => CreatorNameResolver.FromArtifactPath(r.ArtifactPath) == _reviewCreatorFilter)
                .ToList();

    // ── Empty state copy ───────────────────────────────────────────────────
    private string EmptyStateMessage => _activeTab switch
    {
        "pending" => "No entries pending review. All entries have been approved or rejected.",
        "approved" => "No approved entries yet. Approve entries from the Pending tab.",
        "rejected" => "No rejected entries.",
        _ => "No distilled entries in the knowledge base. Run Harvest + Distill first.",
    };

    // ── Lifecycle ──────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        // Why: this runs once per component instance, so an in-page tab click cannot be reverted by a re-render.
        _activeTab = MapInitialTab(Tab);

        try
        {
            // Why: Task.Run moves the store calls off the Blazor sync context (Pitfall 1).
            var rows = await Task.Run(() => Coordinator.LoadRowsAsync(Cts.Token), Cts.Token);

            _allRows = rows.Select(r => new ReviewViewModel(r)).ToList();
        }
        catch (OperationCanceledException)
        {
            // Component disposed mid-load — swallow.
        }
        catch (Exception)
        {
            // Why: store errors can carry DB path/connection details — NEVER surface ex.Message (D-07);
            // show a generic operator-safe message.
            _loadError = "Could not load review queue — check the Studio data directory and retry.";
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static string MapInitialTab(string? tab)
    {
        // Why: an unrecognised value must land on the existing default rather than produce an empty grid,
        // because a stale bookmark or a mistyped link is the expected failure mode.
        var trimmedTab = tab?.Trim();
        return trimmedTab switch
        {
            var value when string.Equals(value, "pending", StringComparison.OrdinalIgnoreCase) => "pending",
            var value when string.Equals(value, "approved", StringComparison.OrdinalIgnoreCase) => "approved",
            var value when string.Equals(value, "rejected", StringComparison.OrdinalIgnoreCase) => "rejected",
            var value when string.Equals(value, "all", StringComparison.OrdinalIgnoreCase) => "all",
            _ => "pending",
        };
    }

    // ── Tab switching ───────────────────────────────────────────────────────
    private void SwitchTab(string tab)
    {
        _activeTab = tab;
        // Clear all selections and reset the creator filter when switching tabs.
        foreach (var vm in _allRows)
        {
            vm.Selected = false;
        }

        _allSelected = false;
        // SUI-05: reset filter so a stale creator from the previous tab doesn't carry over.
        _reviewCreatorFilter = string.Empty;
    }

    // SUI-05: updates the review-list creator filter. Empty string means "All creators".
    private void OnReviewCreatorFilterChanged(ChangeEventArgs args)
    {
        _reviewCreatorFilter = args.Value?.ToString() ?? string.Empty;
        // Clear selections when filter changes to avoid acting on now-hidden rows.
        foreach (var vm in _allRows)
        {
            vm.Selected = false;
        }

        _allSelected = false;
    }

    // ── Select all ─────────────────────────────────────────────────────────
    private void ToggleSelectAll()
    {
        _allSelected = !_allSelected;
        // SUI-05: only toggle the visible (creator-filtered) rows so a hidden row stays deselected.
        foreach (var vm in CreatorFilteredRows)
        {
            vm.Selected = _allSelected;
        }
    }

    // ── Per-row approve/reject (D-05 optimistic, no spinner) ───────────────
    // Why: approve and reject differ only by the target status and the approve-only
    // artifact-missing guard, so they share one optimistic-write helper.
    private async Task SetRowStatusAsync(ReviewViewModel vm, string status)
    {
        // Approving a row whose artifact is missing is blocked; rejecting it is always allowed.
        if (vm.ApprovalStatus == status || (status == "approved" && IsArtifactMissing(vm)))
        {
            return;
        }

        try
        {
            await Coordinator.SetApprovalStatusAsync(vm.NaturalKeyType, vm.NaturalKeyValue, status, Cts.Token);
            vm.ApprovalStatus = status;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Why: per-row optimistic write — swallow transient failures gracefully; queue re-load
            // (page refresh) is the recovery path for persistent errors.
        }

        StateHasChanged();
    }

    // ── Batch approve/reject ────────────────────────────────────────────────
    // Why: batch approve and reject differ only by the target status and the spinner
    // label (_batchAction), so they share one helper.
    private async Task BatchSetStatusAsync(List<ReviewViewModel> eligible, string status, string actionLabel)
    {
        if (eligible.Count == 0 || _operationInFlight)
        {
            return;
        }

        _operationInFlight = true;
        _batchAction = actionLabel;

        try
        {
            var keys = eligible
                .Select(vm => (vm.NaturalKeyType, vm.NaturalKeyValue))
                .ToList()
                .AsReadOnly();

            await Task.Run(
                () => Coordinator.SetApprovalStatusAsync(keys, status, Cts.Token),
                Cts.Token);

            foreach (var vm in eligible)
            {
                vm.ApprovalStatus = status;
                vm.Selected = false;
            }

            _allSelected = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            _operationInFlight = false;
            _batchAction = string.Empty;
            await SafeStateHasChangedAsync();
        }
    }

    // ── Expand/collapse with cached artifact read (D-08/D-09) ──────────────
    private void ToggleExpand(ReviewViewModel vm)
    {
        vm.Expanded = !vm.Expanded;

        if (vm.Expanded && !_expandCache.ContainsKey(vm.NaturalKeyValue))
        {
            // Fire and forget — load artifact text off sync context, cache result, re-render.
            _ = LoadArtifactAsync(vm);
        }
    }

    private async Task LoadArtifactAsync(ReviewViewModel vm)
    {
        string? text;
        string? prompt;
        try
        {
            // Why: Task.Run moves the file reads off the Blazor sync context (Pitfall 1).
            text = await Task.Run(() => Coordinator.ReadArtifactSafe(vm.ArtifactPath), Cts.Token);
            prompt = await Task.Run(
                () => Coordinator.ReadPromptSafe(vm.ArtifactPath, vm.Title, vm.Source, vm.VideoUrl),
                Cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _expandCache[vm.NaturalKeyValue] = text;
        _promptCache[vm.NaturalKeyValue] = prompt;

        await SafeStateHasChangedAsync();
    }

    // ── Notes markdown rendering ────────────────────────────────────────────
    // Why: the review preview showed raw markdown (## Summary, - **[mm:ss]** …) in a <pre>; render
    // it to HTML so reviewers read the notes the same way a KB visitor does on /content-kb. Mirrors
    // the public detail page's pipeline exactly, including DisableHtml() — the notes are transcript-
    // derived, so raw/embedded HTML must be escaped, never executed.
    private static readonly MarkdownPipeline NotesPipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    private static MarkupString RenderNotesHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return default;
        }

        // Strip the YAML frontmatter before rendering, exactly like the public detail page
        // (ContentKbController splits the header and renders only the body). Otherwise the
        // source/title/tags metadata block renders as visible text at the top of the preview.
        var (_, body) = ContentArtifactParser.SplitHeader(markdown);
        return (MarkupString)Markdown.ToHtml(body, NotesPipeline);
    }

    // ── Artifact missing detection (D-10) ──────────────────────────────────
    /// <summary>
    /// Returns <see langword="true"/> when the expand cache has been populated for this row
    /// and the cached value is <see langword="null"/> (file missing/unreadable).
    /// Returns <see langword="false"/> when the cache is empty (not yet expanded) or the
    /// file was read successfully.
    /// </summary>
    private bool IsArtifactMissing(ReviewViewModel vm)
    {
        return _expandCache.TryGetValue(vm.NaturalKeyValue, out var cached) && cached is null;
    }

    // ── View model ──────────────────────────────────────────────────────────
    private sealed class ReviewViewModel
    {
        public long Id { get; }
        public string Title { get; }
        public string Source { get; }
        public string VideoUrl { get; }
        public string ArtifactPath { get; }
        public string ApprovalStatus { get; set; }
        public DateTimeOffset? PushedToProdUtc { get; }
        public bool IsVisible { get; }
        public DateTimeOffset IndexedUtc { get; }
        public IReadOnlyList<string> ArchetypeTags { get; }
        public IReadOnlyList<string> BracketTags { get; }
        public IReadOnlyList<string> CardCategoryTags { get; }

        // Natural key: YouTube video id → (youtube_channel, videoId); else → (podcast_rss, rssGuid).
        public string NaturalKeyType { get; }
        public string NaturalKeyValue { get; }

        // Mutable UI state.
        public bool Selected { get; set; }
        public bool Expanded { get; set; }

        public ReviewViewModel(ContentSiteIndexRow row)
        {
            Id = row.Id;
            Title = row.Title;
            Source = row.Source;
            VideoUrl = row.VideoUrl;
            ArtifactPath = row.ArtifactPath;
            ApprovalStatus = row.ApprovalStatus;
            PushedToProdUtc = row.PushedToProdUtc;
            IsVisible = row.IsVisible;
            IndexedUtc = row.IndexedUtc;
            ArchetypeTags = row.ArchetypeTags;
            BracketTags = row.BracketTags;
            CardCategoryTags = row.CardCategoryTags;

            // Derive natural key per the store contract.
            if (!string.IsNullOrWhiteSpace(row.YoutubeVideoId))
            {
                NaturalKeyType = ContentSourceType.Youtube;
                NaturalKeyValue = row.YoutubeVideoId;
            }
            else
            {
                NaturalKeyType = ContentSourceType.Podcast;
                NaturalKeyValue = row.RssGuid ?? string.Empty;
            }
        }
    }
}
