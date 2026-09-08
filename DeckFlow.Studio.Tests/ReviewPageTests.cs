using Bunit;
using DeckFlow.Core.Content;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Orchestration;
using DeckFlow.Studio.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DeckFlow.Studio.Tests;

/// <summary>
/// bUnit behavioral tests for Review.razor.
/// Covers REVQ-02 (filter tabs + per-row approve/reject) and REVQ-03 (batch approve/reject).
/// </summary>
public sealed class ReviewPageTests : BunitContext
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ContentSiteIndexRow MakeYoutubeRow(long id, string videoId, string status = "pending")
        => new ContentSiteIndexRow
        {
            Id = id,
            Source = "test-channel",
            Title = $"Video {id}",
            VideoUrl = $"https://youtu.be/{videoId}",
            ArtifactPath = $"content-kb/test-channel/{videoId}.md",
            IndexedUtc = DateTimeOffset.UtcNow,
            ApprovalStatus = status,
            ArchetypeTags = Array.Empty<string>(),
            BracketTags = Array.Empty<string>(),
            CardCategoryTags = Array.Empty<string>(),
            YoutubeVideoId = videoId,
        };

    private static ContentSiteIndexRow MakePodcastRow(long id, string guid, string status = "pending")
        => new ContentSiteIndexRow
        {
            Id = id,
            Source = "test-podcast",
            Title = $"Podcast {id}",
            VideoUrl = $"https://example.com/podcast/{guid}",
            ArtifactPath = $"content-kb/test-podcast/{guid}.md",
            IndexedUtc = DateTimeOffset.UtcNow,
            ApprovalStatus = status,
            ArchetypeTags = Array.Empty<string>(),
            BracketTags = Array.Empty<string>(),
            CardCategoryTags = Array.Empty<string>(),
            RssGuid = guid,
        };

    private (IRenderedComponent<Review> Cut, FakeContentSiteIndexStore Store) RenderReview(
        IEnumerable<ContentSiteIndexRow> rows)
        => RenderReview(rows, null);

    private (IRenderedComponent<Review> Cut, FakeContentSiteIndexStore Store) RenderReview(
        IEnumerable<ContentSiteIndexRow> rows,
        string? tab)
    {
        var store = new FakeContentSiteIndexStore();
        foreach (var r in rows)
        {
            store.Rows.Add(r);
        }

        var artifactRoot = Path.Combine(Path.GetTempPath(), "deckflow-tests", "content-kb");
        Services.AddSingleton<IContentSiteIndexStore>(store);
        Services.AddSingleton(new ContentKbOrchestratorOptions { ArtifactRoot = artifactRoot });
        Services.AddSingleton<PublishStateDeriver>();
        // Why: the page now resolves its store I/O + artifact reads through ReviewCoordinator (H1
        // split); the coordinator is built from the fakes registered above, so behavior is unchanged.
        Services.AddSingleton<DeckFlow.Studio.ViewModels.ReviewCoordinator>();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uri = navigationManager.GetUriWithQueryParameter("tab", tab);
        navigationManager.NavigateTo(uri);
        var cut = Render<Review>();
        return (cut, store);
    }

    // ── REVQ-02: Filter tabs show correct count badges ───────────────────────

    [Fact]
    public void FilterTabs_ShowCorrectCountBadges_ForMixedRows()
    {
        // Arrange: 2 pending, 1 approved, 1 rejected
        var rows = new[]
        {
            MakeYoutubeRow(1, "vid1", "pending"),
            MakeYoutubeRow(2, "vid2", "pending"),
            MakeYoutubeRow(3, "vid3", "approved"),
            MakeYoutubeRow(4, "vid4", "rejected"),
        };

        var (cut, _) = RenderReview(rows);

        // Act: wait for OnInitializedAsync Task.Run to complete, then assert counts.
        // Why: each tab button is the only <button> inside its <li class="nav-item">, so
        // :nth-of-type(N) never matches across siblings. Grab all four badges in document
        // order instead — index 0=Pending, 1=Approved, 2=Rejected, 3=All.
        cut.WaitForAssertion(() =>
        {
            var badges = cut.FindAll("ul.nav-tabs button[role='tab'] .badge");
            Assert.Equal(4, badges.Count);
            Assert.Equal("2", badges[0].TextContent.Trim());
            Assert.Equal("1", badges[1].TextContent.Trim());
            Assert.Equal("1", badges[2].TextContent.Trim());
            Assert.Equal("4", badges[3].TextContent.Trim());
        });
    }

    // ── REVQ-02: Empty-state message shown for empty tab ────────────────────

    [Fact]
    public void FilterTab_Approved_ShowsEmptyStateMessage_WhenNoApprovedRows()
    {
        // Arrange: only pending rows
        var rows = new[] { MakeYoutubeRow(1, "vid1", "pending") };
        var (cut, _) = RenderReview(rows);

        // Wait for load
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        // Act: click Approved tab
        var tabs = cut.FindAll("button[role='tab']");
        tabs[1].Click(); // Approved tab

        // Assert: empty-state message is visible
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No approved entries yet", cut.Markup);
        });
    }

    [Fact]
    public void FilterTab_Pending_ShowsEmptyStateMessage_WhenNoPendingRows()
    {
        // Arrange: only approved rows
        var rows = new[] { MakeYoutubeRow(1, "vid1", "approved") };
        var (cut, _) = RenderReview(rows);

        // Wait for load — default tab is "pending"
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No entries pending review", cut.Markup);
        });
    }

    [Fact]
    public void ReviewPage_PublishStateColumn_ShowsNeverPublishedForUnpushedRow()
    {
        var rows = new[] { MakeYoutubeRow(1, "vid1", "pending") };
        var (cut, _) = RenderReview(rows);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Never published", cut.Markup);
        });
    }

    // ── REVQ-02: Per-row Approve calls SetApprovalStatusAsync(type,value,"approved") ──

    [Fact]
    public void ApproveEntry_OnPendingYoutubeRow_CallsSetApprovalStatusWithCorrectArgs()
    {
        // Arrange
        var row = MakeYoutubeRow(1, "vidABC", "pending");
        var (cut, store) = RenderReview(new[] { row });

        // Wait for load
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        // Act: click "Approve Entry" for the pending row
        cut.InvokeAsync(() => cut.Find("button[aria-label='Approve Entry']").Click());

        // Assert: single-overload called with (youtube_channel, "vidABC", "approved")
        cut.WaitForAssertion(() =>
        {
            Assert.Single(store.SingleApprovalCalls);
            var (type, value, status) = store.SingleApprovalCalls[0];
            Assert.Equal(ContentSourceType.Youtube, type);
            Assert.Equal("vidABC", value);
            Assert.Equal("approved", status);
        });
    }

    [Fact]
    public void ApproveEntry_OnPendingYoutubeRow_FlipsRowBadgeToApproved()
    {
        // Arrange
        var row = MakeYoutubeRow(1, "vidDEF", "pending");
        var (cut, store) = RenderReview(new[] { row });

        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        // Before: row class should not be table-success
        Assert.DoesNotContain("table-success", cut.Markup);

        // Act: approve the pending row.
        cut.InvokeAsync(() => cut.Find("button[aria-label='Approve Entry']").Click());

        // The default "pending" tab filters out the now-approved row, so view it on the "All"
        // tab where the approved row renders with its table-success class. (Tab buttons are the
        // 4 role='tab' buttons in document order: Pending, Approved, Rejected, All.)
        cut.WaitForAssertion(() => Assert.Single(store.SingleApprovalCalls));
        cut.InvokeAsync(() => cut.FindAll("ul.nav-tabs button[role='tab']")[3].Click());

        // Assert: row now has table-success class
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("table-success", cut.Markup);
        });
    }

    // ── REVQ-02: Per-row Reject calls SetApprovalStatusAsync(type,value,"rejected") ──

    [Fact]
    public void RejectEntry_OnPendingRow_CallsSetApprovalStatusWithRejectedStatus()
    {
        // Arrange
        var row = MakeYoutubeRow(1, "vidGHI", "pending");
        var (cut, store) = RenderReview(new[] { row });

        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        // Act: re-find immediately before dispatch and wrap in InvokeAsync so the click
        // runs on the renderer's dispatcher with a fresh (non-stale) event-handler id.
        cut.InvokeAsync(() => cut.Find("button[aria-label='Reject Entry']").Click());

        // Assert
        cut.WaitForAssertion(() =>
        {
            Assert.Single(store.SingleApprovalCalls);
            var (type, value, status) = store.SingleApprovalCalls[0];
            Assert.Equal(ContentSourceType.Youtube, type);
            Assert.Equal("vidGHI", value);
            Assert.Equal("rejected", status);
        });
    }

    [Fact]
    public void RejectEntry_OnPendingRow_FlipsRowBadgeToRejected()
    {
        // Arrange
        var row = MakeYoutubeRow(1, "vidJKL", "pending");
        var (cut, store) = RenderReview(new[] { row });

        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        // Act: reject the pending row.
        cut.InvokeAsync(() => cut.Find("button[aria-label='Reject Entry']").Click());

        // The default "pending" tab filters out the now-rejected row, so view it on the "All"
        // tab where the rejected row renders with its table-danger class.
        cut.WaitForAssertion(() => Assert.Single(store.SingleApprovalCalls));
        cut.InvokeAsync(() => cut.FindAll("ul.nav-tabs button[role='tab']")[3].Click());

        // Assert: row now has table-danger class
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("table-danger", cut.Markup);
        });
    }

    // ── REVQ-02: Natural key derivation — podcast row uses RssGuid ──────────

    [Fact]
    public void ApproveEntry_OnPendingPodcastRow_CallsSetApprovalStatusWithPodcastType()
    {
        // Arrange
        var row = MakePodcastRow(1, "guid-xyz", "pending");
        var (cut, store) = RenderReview(new[] { row });

        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        // Act
        cut.InvokeAsync(() => cut.Find("button[aria-label='Approve Entry']").Click());

        // Assert: natural key type is podcast_rss, value is the RssGuid
        cut.WaitForAssertion(() =>
        {
            Assert.Single(store.SingleApprovalCalls);
            var (type, value, status) = store.SingleApprovalCalls[0];
            Assert.Equal(ContentSourceType.Podcast, type);
            Assert.Equal("guid-xyz", value);
            Assert.Equal("approved", status);
        });
    }

    // ── Distill-baked prompt shown on expand ────────────────────────────────

    [Fact]
    public async Task ExpandRow_WithBakedPromptSibling_ShowsPasteReadyPrompt()
    {
        // Write a notes artifact + its baked sibling under the shared test artifact root at the
        // row's stored ArtifactPath (unique id avoids cross-test collision in the shared dir).
        var videoId = "prompt" + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "deckflow-tests", "content-kb", "test-channel");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, videoId + ".md"), "---\ntitle: T\n---\n## Summary\nNotes body.");
        File.WriteAllText(Path.Combine(dir, videoId + ".prompt.md"), "BAKED-PROMPT-SENTINEL");

        var (cut, _) = RenderReview(new[] { MakeYoutubeRow(1, videoId, "pending") });
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        // Expand the row via the chevron button. Await the dispatch so the click's async
        // continuation (LoadArtifactAsync) is scheduled before we assert — avoids the handler-id race.
        await cut.InvokeAsync(() => cut.Find("button[aria-label^='Expand entry']").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Paste-ready AI Prompt", cut.Markup);
            Assert.Contains("BAKED-PROMPT-SENTINEL", cut.Markup);
        });
    }

    [Fact]
    public async Task ExpandRow_RendersNotesAsHtml_NotRawMarkdown()
    {
        var videoId = "render" + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "deckflow-tests", "content-kb", "test-channel");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, videoId + ".md"),
            "---\nsource: \"@Creator\"\ntitle: \"Vid\"\n---\n## Summary\n\nRendered body text.");

        var (cut, _) = RenderReview(new[] { MakeYoutubeRow(1, videoId, "pending") });
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        await cut.InvokeAsync(() => cut.Find("button[aria-label^='Expand entry']").Click());

        cut.WaitForAssertion(() =>
        {
            // Scope to the notes preview div — the prompt panel below legitimately embeds the raw
            // notes verbatim, so assert on the preview only. The markdown renders to HTML there
            // (## Summary -> <h2>), not shown as raw markdown.
            var preview = cut.Find(".kb-notes-preview");
            Assert.Contains("<h2", preview.InnerHtml);
            Assert.Contains("Rendered body text.", preview.InnerHtml);
            Assert.DoesNotContain("## Summary", preview.InnerHtml);
            // The YAML frontmatter (source/title metadata) is stripped, like the public detail page.
            Assert.DoesNotContain("title:", preview.InnerHtml);
            Assert.DoesNotContain("@Creator", preview.InnerHtml);
        });
    }

    // ── REVQ-03: Batch bar hidden when no rows selected ──────────────────────

    [Fact]
    public void BatchBar_IsHidden_WhenNoRowsSelected()
    {
        // Arrange: two pending rows; no checkbox ticked
        var rows = new[]
        {
            MakeYoutubeRow(1, "vid1", "pending"),
            MakeYoutubeRow(2, "vid2", "pending"),
        };

        var (cut, _) = RenderReview(rows);
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        // Assert: "Approve Selected" text not present
        Assert.DoesNotContain("Approve Selected", cut.Markup);
        Assert.DoesNotContain("Reject Selected", cut.Markup);
    }

    // ── REVQ-03: Batch approve calls the batch overload ──────────────────────

    [Fact]
    public void BatchApprove_TwoPendingRows_CallsBatchOverloadWithBothKeys()
    {
        // Arrange: two pending rows
        var rows = new[]
        {
            MakeYoutubeRow(1, "vid1", "pending"),
            MakeYoutubeRow(2, "vid2", "pending"),
        };
        var (cut, store) = RenderReview(rows);

        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        // Act: check all via select-all checkbox
        var selectAll = cut.Find("input[aria-label='Select all']");
        selectAll.Click();

        // Batch bar should appear
        cut.WaitForAssertion(() => Assert.Contains("Approve Selected", cut.Markup));

        // Click batch approve
        var batchApproveBtn = cut.Find("button.btn-primary.btn-sm");
        batchApproveBtn.Click();

        // Assert: batch overload was called with two keys
        cut.WaitForAssertion(() =>
        {
            Assert.Single(store.BatchApprovalCalls);
            var (keys, status) = store.BatchApprovalCalls[0];
            Assert.Equal("approved", status);
            Assert.Equal(2, keys.Count);
            Assert.Contains(keys, k => k.Value == "vid1");
            Assert.Contains(keys, k => k.Value == "vid2");
        });
    }

    [Fact]
    public void BatchApprove_AllSelectedPendingRows_FlipAllToApproved()
    {
        // Arrange
        var rows = new[]
        {
            MakeYoutubeRow(1, "vid1", "pending"),
            MakeYoutubeRow(2, "vid2", "pending"),
        };
        var (cut, store) = RenderReview(rows);

        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        // Select all
        cut.Find("input[aria-label='Select all']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Approve Selected", cut.Markup));

        // Batch approve
        cut.Find("button.btn-primary.btn-sm").Click();

        // Assert: both rows have approved status in the store
        cut.WaitForAssertion(() =>
        {
            Assert.All(store.Rows, r => Assert.Equal("approved", r.ApprovalStatus));
        });
    }

    // ── A1 (SUI-02): Go to Publish link ──────────────────────────────────────

    [Fact]
    public void GoToPublishLink_IsPresent_WhenApprovedCountGreaterThanZero()
    {
        // Arrange: at least one approved row
        var rows = new[]
        {
            MakeYoutubeRow(1, "vid1", "approved"),
            MakeYoutubeRow(2, "vid2", "pending"),
        };

        var (cut, _) = RenderReview(rows);

        // Assert: link is rendered with the correct href
        cut.WaitForAssertion(() =>
        {
            var link = cut.Find("a[aria-label='Go to Publish page']");
            Assert.NotNull(link);
            Assert.Contains("/publish", link.GetAttribute("href") ?? string.Empty);
        });
    }

    [Fact]
    public void GoToPublishLink_IsAbsent_WhenApprovedCountIsZero()
    {
        // Arrange: no approved rows
        var rows = new[]
        {
            MakeYoutubeRow(1, "vid1", "pending"),
            MakeYoutubeRow(2, "vid2", "rejected"),
        };

        var (cut, _) = RenderReview(rows);

        // Assert: link is not rendered
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));
        Assert.Empty(cut.FindAll("a[aria-label='Go to Publish page']"));
    }

    [Fact]
    public void GoToPublishLink_ShowsApprovedCount_InLinkText()
    {
        // Arrange: two approved rows
        var rows = new[]
        {
            MakeYoutubeRow(1, "vid1", "approved"),
            MakeYoutubeRow(2, "vid2", "approved"),
            MakeYoutubeRow(3, "vid3", "pending"),
        };

        var (cut, _) = RenderReview(rows);

        cut.WaitForAssertion(() =>
        {
            var link = cut.Find("a[aria-label='Go to Publish page']");
            Assert.Contains("2", link.TextContent);
        });
    }

    // ── A2 (SUI-02): Select-All scoped to visible/filtered rows ──────────────

    [Fact]
    public void SelectAll_OnPendingTab_OnlySelectsVisiblePendingRows()
    {
        // Arrange: 2 pending, 1 approved — default tab is "pending"
        var rows = new[]
        {
            MakeYoutubeRow(1, "vid1", "pending"),
            MakeYoutubeRow(2, "vid2", "pending"),
            MakeYoutubeRow(3, "vid3", "approved"),
        };

        var (cut, store) = RenderReview(rows);
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        // Click select-all
        cut.Find("input[aria-label='Select all']").Click();

        // Batch approve fires — should only include the 2 pending rows (visible in pending tab)
        cut.WaitForAssertion(() => Assert.Contains("Approve Selected", cut.Markup));
        cut.Find("button.btn-primary.btn-sm").Click();

        // Assert: batch approve called with exactly 2 keys (the pending ones)
        cut.WaitForAssertion(() =>
        {
            Assert.Single(store.BatchApprovalCalls);
            var (keys, status) = store.BatchApprovalCalls[0];
            Assert.Equal("approved", status);
            Assert.Equal(2, keys.Count);
            Assert.Contains(keys, k => k.Value == "vid1");
            Assert.Contains(keys, k => k.Value == "vid2");
            // The approved row (vid3) must NOT appear in the batch
            Assert.DoesNotContain(keys, k => k.Value == "vid3");
        });
    }

    // ── F-07: query-addressable tab ─────────────────────────────────────────

    [Fact]
    public void Review_TabQueryValue_PreSelectsThatTab()
    {
        var (cut, _) = RenderReview(QueryTabRows(), "approved");

        cut.WaitForAssertion(() =>
        {
            var tabs = cut.FindAll("ul.nav-tabs button[role='tab']");
            Assert.Contains("active", tabs[1].ClassList);
            Assert.DoesNotContain("active", tabs[0].ClassList);
        });
    }

    [Fact]
    public void Review_TabQueryValue_IsCaseInsensitive()
    {
        var (cut, _) = RenderReview(QueryTabRows(), "Approved");

        cut.WaitForAssertion(() =>
        {
            var tabs = cut.FindAll("ul.nav-tabs button[role='tab']");
            Assert.Contains("active", tabs[1].ClassList);
            Assert.DoesNotContain("active", tabs[0].ClassList);
        });
    }

    [Fact]
    public void Review_UnknownTabQueryValue_FallsBackToPending()
    {
        var (cut, _) = RenderReview(QueryTabRows(), "bogus");

        cut.WaitForAssertion(() =>
            Assert.Contains("active", cut.FindAll("ul.nav-tabs button[role='tab']")[0].ClassList));
    }

    [Fact]
    public void Review_NoTabQueryValue_KeepsPendingDefault()
    {
        var (cut, _) = RenderReview(QueryTabRows(), null);

        cut.WaitForAssertion(() =>
            Assert.Contains("active", cut.FindAll("ul.nav-tabs button[role='tab']")[0].ClassList));
    }

    private static ContentSiteIndexRow[] QueryTabRows()
        =>
        [
            MakeYoutubeRow(1, "pending", "pending"),
            MakeYoutubeRow(2, "approved", "approved"),
        ];

    // ── F-10: in-table actions opt out of the touch floor ──

    [Fact]
    public void TableActionButtons_CarryTheCompactClass()
    {
        var row = MakeYoutubeRow(1, "vidJKL", "pending");
        var (cut, _) = RenderReview(new[] { row });

        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading review queue", cut.Markup));

        cut.WaitForAssertion(() =>
        {
            var tableButtons = cut.FindAll("td button");
            Assert.NotEmpty(tableButtons);
            Assert.All(tableButtons, b => Assert.Contains("btn-table-action", b.ClassList));
        });
    }
}
