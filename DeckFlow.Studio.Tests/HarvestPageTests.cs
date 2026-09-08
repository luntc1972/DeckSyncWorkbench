using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using DeckFlow.Core.Content;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Orchestration;
using DeckFlow.Studio;
using DeckFlow.Studio.Pages;
using DeckFlow.Studio.Services;
using DeckFlow.Studio.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace DeckFlow.Studio.Tests
{
    public sealed class HarvestPageTests : BunitContext
    {
        private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

        // Per-test temp dir backing the AutoApproveSettingsStore so persistence assertions are isolated.
        private readonly string _autoApproveDir =
            Path.Combine(Path.GetTempPath(), "deckflow-harvest-autoapprove-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void HarvestPage_ConfirmBlock_Success_RecordsBlockAndRefreshesBadge()
        {
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var (cut, maint, _, _) = RenderHarvest(
                new[] { Vid("vidA") },
                blocked,
                index);

            BrowseChannel(cut);

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("T", cut.Markup);
                Assert.DoesNotContain(">Blocked<", cut.Markup);
            });

            cut.InvokeAsync(() => cut.Find("button[aria-label='Block T']").Click());

            cut.WaitForAssertion(() => Assert.Contains("Confirm Block", cut.Markup));

            blocked.Blocked.Add("vidA");

            cut.InvokeAsync(() => cut.Find("button[aria-label='Confirm block T']").Click());

            cut.WaitForAssertion(() => Assert.Contains("vidA", maint.BlockCalls));

            // HSEL-01: once blocked, the row leaves the unharvested-only default view; show all to assert the badge.
            cut.InvokeAsync(() => cut.Find("#showAllVideos").Change(true));

            cut.WaitForAssertion(() => Assert.Contains(">Blocked<", cut.Markup));
        }

        [Fact]
        public void HarvestPage_ConfirmBlock_ResultFailure_ShowsErrorAndLeavesBadgeUnchanged()
        {
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var cannedBlock = new ContentMaintenanceResult
            {
                Success = false,
                Message = "Block failed",
            };

            var (cut, maint, _, _) = RenderHarvest(
                new[] { Vid("vidF") },
                blocked,
                index,
                cannedBlock);

            BrowseChannel(cut);

            cut.InvokeAsync(() => cut.Find("button[aria-label='Block T']").Click());
            cut.WaitForAssertion(() => Assert.Contains("Confirm Block", cut.Markup));
            cut.InvokeAsync(() => cut.Find("button[aria-label='Confirm block T']").Click());

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("vidF", maint.BlockCalls);
                Assert.Contains("Block failed", cut.Markup);
                Assert.Contains("Block Video", cut.Markup);
                Assert.DoesNotContain("Confirm Block", cut.Markup);
                Assert.DoesNotContain(">Blocked<", cut.Markup);
            });
        }

        [Fact]
        public void HarvestPage_ChannelBrowse_BlockedVideoRendersBlockedBadge()
        {
            var blocked = new MapBlockedStore();
            blocked.Blocked.Add("vidBlk");
            var index = new MapSiteIndexStore();

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("vidBlk") },
                blocked,
                index);

            BrowseChannel(cut);
            // HSEL-01: blocked rows are hidden in the unharvested-only default view; show all to assert the badge.
            cut.InvokeAsync(() => cut.Find("#showAllVideos").Change(true));

            cut.WaitForAssertion(() =>
            {
                Assert.Contains(">Blocked<", cut.Markup);
                Assert.True(cut.Find("button[aria-label='Block T']").HasAttribute("disabled"));
            });
        }

        [Fact]
        public void HarvestPage_AddToQueue_ZeroResolved_ShowsWarning()
        {
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                blocked,
                index,
                byIds: Array.Empty<YouTubeChannelVideo>());

            cut.InvokeAsync(() => cut.Find("#pasteQueue").Change("notavideo"));
            cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Add to Queue", StringComparison.Ordinal)).Click());

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("No videos found for the pasted input", cut.Markup);
            });
        }

        [Fact]
        public void HarvestPage_BadgeArms_ApprovedAndPublished_RenderText()
        {
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            index.Rows["vidApp"] = MakeIndexRow("vidApp", "approved", null, false);
            index.Rows["vidPub"] = MakeIndexRow("vidPub", "approved", DateTimeOffset.UtcNow, true);

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("vidApp"), Vid("vidPub") },
                blocked,
                index);

            BrowseChannel(cut);
            // HSEL-01: approved/published rows are hidden in the unharvested-only default; show all to assert badges.
            cut.InvokeAsync(() => cut.Find("#showAllVideos").Change(true));

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("Approved", cut.Markup);
                Assert.Contains("Published", cut.Markup);
            });
        }

        [Fact]
        public void HarvestPage_MultiSelectHarvest_HarvestsOnlySelectedVideos()
        {
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var (cut, _, harv, _) = RenderHarvest(
                new[]
                {
                    Vid("v1", "Vid 1"),
                    Vid("v2", "Vid 2"),
                    Vid("v3", "Vid 3"),
                },
                blocked,
                index);

            BrowseChannel(cut);

            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("tbody tr").Count));

            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 1']").Change(true));
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 2']").Change(true));

            cut.WaitForAssertion(() =>
            {
                var button = cut.FindAll("button").First(b => b.TextContent.Contains("Harvest Selected", StringComparison.Ordinal));
                Assert.False(button.HasAttribute("disabled"));
            });

            cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Harvest Selected", StringComparison.Ordinal)).Click());

            cut.WaitForAssertion(() =>
            {
                var allIds = harv.HarvestCalls.SelectMany(c => c ?? Array.Empty<string>()).ToList();
                Assert.Contains("v1", allIds);
                Assert.Contains("v2", allIds);
                Assert.DoesNotContain("v3", allIds);
                Assert.Equal(2, allIds.Count);
            });
        }

        [Fact]
        public async Task HarvestPage_Dispose_DoesNotCancelRunningHarvestJob()
        {
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var runner = new HarvestJobRunner();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var harv = new RecordingHarvestOrchestrator
            {
                StartedSignal = started,
                ReleaseSignal = release,
            };

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("v1", "Vid 1") },
                blocked,
                index,
                harv: harv,
                runner: runner);

            BrowseChannel(cut);

            await cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 1']").Change(true));
            cut.WaitForAssertion(() =>
            {
                var button = cut.FindAll("button").First(b => b.TextContent.Contains("Harvest Selected", StringComparison.Ordinal));
                Assert.False(button.HasAttribute("disabled"));
            });

            await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Harvest Selected", StringComparison.Ordinal)).Click());

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(() => Assert.True(runner.IsRunning));

            cut.Dispose();

            Assert.True(runner.IsRunning);

            // Reconnect: render a fresh Harvest against the SAME already-registered services
            // (bUnit forbids re-registering services after the first render, so we cannot call
            // RenderHarvest again — the singleton runner is already in the container).
            var reconnected = Render<Harvest>();
            WaitForPageReady(reconnected);

            reconnected.WaitForAssertion(() => Assert.Contains("keeps running in the background if you switch pages", reconnected.Markup));

            release.TrySetResult(true);

            reconnected.WaitForAssertion(() => Assert.False(runner.IsRunning));
        }

        [Fact]
        public void AutoApprove_DefaultRender_ShowsToggleOnAndCutoffFive()
        {
            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                new MapBlockedStore(),
                new MapSiteIndexStore());

            Assert.Contains("Auto-approve", cut.Markup);
            var toggle = cut.Find("#autoApproveEnabled");
            Assert.True(toggle.HasAttribute("checked"));
            var cutoff = cut.Find("#autoApproveCutoff");
            Assert.Equal("5", cutoff.GetAttribute("value"));
            Assert.False(cutoff.HasAttribute("disabled"));
        }

        [Fact]
        public void AutoApprove_ToggleOff_SavesDisabledAndDisablesCutoffInput()
        {
            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                new MapBlockedStore(),
                new MapSiteIndexStore());

            cut.InvokeAsync(() => cut.Find("#autoApproveEnabled").Change(false));

            // Persisted to disk (D-04/D-07): a fresh store over the same dir reads back Enabled=false.
            // WaitForAssertion so the async save handler settles before the reload is read.
            cut.WaitForAssertion(() => Assert.False(new AutoApproveSettingsStore(_autoApproveDir).Load().Enabled));

            cut.WaitForAssertion(() => Assert.True(cut.Find("#autoApproveCutoff").HasAttribute("disabled")));
        }

        [Fact]
        public void AutoApprove_ChangeCutoff_SavesNewCutoff()
        {
            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                new MapBlockedStore(),
                new MapSiteIndexStore());

            cut.InvokeAsync(() => cut.Find("#autoApproveCutoff").Change("7"));

            // WaitForAssertion so the async save handler settles before the reload is read.
            cut.WaitForAssertion(() => Assert.Equal(7, new AutoApproveSettingsStore(_autoApproveDir).Load().Cutoff));
        }

        // ── One-click harvest→auto-distill→auto-approve (AUTO-01 / AUTO-02, Plan 03) ──────────

        [Fact]
        public void OneClick_Subscription_HarvestsThenDistillsHarvestReadyIds_NoManualClick()
        {
            // SUBSCRIPTION: 1 selected video, harvest leaves it pending → distilled inline. SC1.
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var distill = new RecordingDistillOrchestrator
            {
                Pending = new[] { Pending("v1") },
                LiveResult = new DistillResult
                {
                    Success = true,
                    VideosDistilled = 1,
                    DistilledVideos = new[] { Distilled("v1", 6) },
                },
            };

            var (cut, _, _, _) = RenderHarvest(new[] { Vid("v1", "Vid 1") }, blocked, index, distill: distill);

            BrowseChannel(cut);
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 1']").Change(true));
            ClickOneClick(cut);

            cut.WaitForAssertion(() =>
            {
                Assert.True(distill.LiveDistillCalled);
                var ids = distill.DistillCalls.SelectMany(c => c ?? Array.Empty<string>()).ToList();
                Assert.Equal(new[] { "v1" }, ids);
            });
        }

        [Fact]
        public void OneClick_MixedBatch_DistillsOnlyHarvestReadyIds()
        {
            // HIGH #2: 3 selected, harvest leaves only 2 pending (v2 skipped/no-caption). v3 is
            // already-distilled so ListPendingDistillAsync omits it. DistillAsync gets exactly v1.
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var distill = new RecordingDistillOrchestrator
            {
                // Only v1 is harvest-ready and selected; v2 not pending (no caption); v4 pending but not selected.
                Pending = new[] { Pending("v1"), Pending("v4") },
                LiveResult = new DistillResult { Success = true, VideosDistilled = 1 },
            };

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("v1", "Vid 1"), Vid("v2", "Vid 2"), Vid("v3", "Vid 3") },
                blocked,
                index,
                distill: distill);

            BrowseChannel(cut);
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 1']").Change(true));
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 2']").Change(true));
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 3']").Change(true));
            ClickOneClick(cut);

            cut.WaitForAssertion(() =>
            {
                Assert.True(distill.LiveDistillCalled);
                var ids = distill.DistillCalls.SelectMany(c => c ?? Array.Empty<string>()).ToList();
                // v2 (not pending) and v3 (already-distilled, not pending) and v4 (not selected) excluded.
                Assert.Equal(new[] { "v1" }, ids);
            });
        }

        [Fact]
        public void OneClick_AutoApproveOn_AboveCutoff_ApprovesNaturalKey()
        {
            // SC2: a 6-clip distill with cutoff 5 + auto-approve ON → SetApprovalStatusAsync('approved').
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var distill = new RecordingDistillOrchestrator
            {
                Pending = new[] { Pending("v1") },
                LiveResult = new DistillResult
                {
                    Success = true,
                    VideosDistilled = 1,
                    DistilledVideos = new[] { Distilled("v1", 6) },
                },
            };

            var (cut, _, _, _) = RenderHarvest(new[] { Vid("v1", "Vid 1") }, blocked, index, distill: distill);

            BrowseChannel(cut);
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 1']").Change(true));
            ClickOneClick(cut);

            cut.WaitForAssertion(() =>
            {
                Assert.Single(index.ApprovalBatchCalls);
                var (keys, status) = index.ApprovalBatchCalls[0];
                Assert.Equal("approved", status);
                Assert.Contains(keys, k => k.Value == "v1");
            });
        }

        [Fact]
        public void OneClick_AutoApproveOn_BelowCutoff_NotApproved()
        {
            // SC2: a 3-clip distill with cutoff 5 → below cutoff → its key is NOT in the approved batch.
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var distill = new RecordingDistillOrchestrator
            {
                Pending = new[] { Pending("v1") },
                LiveResult = new DistillResult
                {
                    Success = true,
                    VideosDistilled = 1,
                    DistilledVideos = new[] { Distilled("v1", 3) },
                },
            };

            var (cut, _, _, _) = RenderHarvest(new[] { Vid("v1", "Vid 1") }, blocked, index, distill: distill);

            BrowseChannel(cut);
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 1']").Change(true));
            ClickOneClick(cut);

            // No video qualifies → no batch approval call at all (empty list is skipped).
            cut.WaitForAssertion(() => Assert.Contains("review", cut.Markup, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(index.ApprovalBatchCalls);
        }

        [Fact]
        public void OneClick_AutoApproveOff_NeverApproves()
        {
            // SC3 / D-04: auto-approve OFF → SetApprovalStatusAsync('approved') is never called.
            new AutoApproveSettingsStore(_autoApproveDir).Save(new AutoApproveSettings(false, 5));

            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var distill = new RecordingDistillOrchestrator
            {
                Pending = new[] { Pending("v1") },
                LiveResult = new DistillResult
                {
                    Success = true,
                    VideosDistilled = 1,
                    DistilledVideos = new[] { Distilled("v1", 9) },
                },
            };

            var (cut, _, _, _) = RenderHarvest(new[] { Vid("v1", "Vid 1") }, blocked, index, distill: distill);

            BrowseChannel(cut);
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 1']").Change(true));
            ClickOneClick(cut);

            cut.WaitForAssertion(() => Assert.True(distill.LiveDistillCalled));
            Assert.Empty(index.ApprovalBatchCalls);
        }

        [Fact]
        public async Task OneClick_Metered_DoesNotDistill_ShowsRequiresSubscription()
        {
            // SC4 / HIGH #1: metered provider → one-click does NOT call DistillAsync and shows the message.
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var distill = new RecordingDistillOrchestrator
            {
                Pending = new[] { Pending("v1") },
            };

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("v1", "Vid 1") },
                blocked,
                index,
                distill: distill,
                isSubscriptionProvider: false);

            BrowseChannel(cut);
            await cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 1']").Change(true));
            await cut.InvokeAsync(() => cut.FindAll("button")
                .First(b => b.TextContent.Contains("Harvest + Auto-distill", StringComparison.Ordinal))
                .Click());

            cut.WaitForAssertion(
                () => Assert.Contains("subscription provider", cut.Markup, StringComparison.OrdinalIgnoreCase),
                UiTimeout);
            Assert.False(distill.LiveDistillCalled);
            Assert.Empty(index.ApprovalBatchCalls);
        }

        [Fact]
        public void OneClick_ContinueOnFailure_SurfacesFailedIds()
        {
            // D-10: DistillFailed>0 → action still completes and lists the failed ids.
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var distill = new RecordingDistillOrchestrator
            {
                Pending = new[] { Pending("v1"), Pending("v2") },
                LiveResult = new DistillResult
                {
                    Success = true,
                    VideosDistilled = 1,
                    DistillFailed = 1,
                    FailedVideoIds = new[] { "v2" },
                    DistilledVideos = new[] { Distilled("v1", 7) },
                },
            };

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("v1", "Vid 1"), Vid("v2", "Vid 2") },
                blocked,
                index,
                distill: distill);

            BrowseChannel(cut);
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 1']").Change(true));
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 2']").Change(true));
            ClickOneClick(cut);

            cut.WaitForAssertion(() => Assert.Contains("v2", cut.Markup));
        }

        [Fact]
        public void OneClick_OutcomeCard_ShowsCanonicalCounts()
        {
            // D-11: one card with harvested/distilled/auto-approved/left-in-review/dropped/failed.
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var distill = new RecordingDistillOrchestrator
            {
                Pending = new[] { Pending("v1"), Pending("v2") },
                LiveResult = new DistillResult
                {
                    Success = true,
                    VideosDistilled = 2,
                    VideosFiltered = 0,
                    DistilledVideos = new[] { Distilled("v1", 6), Distilled("v2", 3) },
                },
            };

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("v1", "Vid 1"), Vid("v2", "Vid 2") },
                blocked,
                index,
                distill: distill);

            BrowseChannel(cut);
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 1']").Change(true));
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 2']").Change(true));
            ClickOneClick(cut);

            cut.WaitForAssertion(() =>
            {
                // harvested 2, distilled 2, auto-approved 1 (only v1 >=5), left-in-review 1.
                Assert.Contains("Auto-approved", cut.Markup);
                Assert.Contains("review", cut.Markup, StringComparison.OrdinalIgnoreCase);
                // One approval batch for the single qualifying key.
                Assert.Single(index.ApprovalBatchCalls);
                Assert.Single(index.ApprovalBatchCalls[0].Keys);
            });
        }

        [Fact]
        public void ManualStageB_Subscription_SharedAutoApprove_ApprovesAboveCutoff()
        {
            // D-09 reuse: the manual fallback "Run Distill" (subscription) also auto-approves >=cutoff
            // via the SAME shared step. Load harvested → select → Run Distill → SetApprovalStatusAsync.
            var blocked = new MapBlockedStore();
            var index = new MapSiteIndexStore();
            var distill = new RecordingDistillOrchestrator
            {
                Pending = new[] { Pending("v1") },
                LiveResult = new DistillResult
                {
                    Success = true,
                    VideosDistilled = 1,
                    DistilledVideos = new[] { Distilled("v1", 8) },
                },
            };

            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                blocked,
                index,
                distill: distill);

            // Load the DB-backed pending-distill list (status Harvested).
            cut.InvokeAsync(() => cut.FindAll("button")
                .First(b => b.TextContent.Contains("Load harvested", StringComparison.Ordinal))
                .Click());
            cut.WaitForAssertion(() => Assert.Contains("Select v1", cut.Markup));

            cut.InvokeAsync(() => cut.Find("input[aria-label='Select v1']").Change(true));

            cut.WaitForAssertion(() =>
            {
                var button = cut.FindAll("button").First(b => b.TextContent.Contains("Run Distill", StringComparison.Ordinal));
                Assert.False(button.HasAttribute("disabled"));
            });

            cut.InvokeAsync(() => cut.FindAll("button")
                .First(b => b.TextContent.Contains("Run Distill", StringComparison.Ordinal))
                .Click());

            cut.WaitForAssertion(() =>
            {
                Assert.Single(index.ApprovalBatchCalls);
                var (keys, status) = index.ApprovalBatchCalls[0];
                Assert.Equal("approved", status);
                Assert.Contains(keys, k => k.Value == "v1");
            });
        }

        [Fact]
        public void PendingDistill_FilteredVideo_IsReadyAndRunsWithoutRedistillConfirm()
        {
            var distill = new RecordingDistillOrchestrator
            {
                Pending = new[] { Pending("vf1", "filtered") },
            };

            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                new MapBlockedStore(),
                new MapSiteIndexStore(),
                distill: distill);

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("Select vf1", cut.Markup);
                Assert.Contains("Filtered", cut.Markup);
            });

            cut.InvokeAsync(() => cut.Find("input[aria-label='Select vf1']").Change(true));

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("Videos ready to distill: 1", cut.Markup);
                var button = cut.FindAll("button").First(b => b.TextContent.Contains("Run Distill", StringComparison.Ordinal));
                Assert.False(button.HasAttribute("disabled"));
            });

            cut.InvokeAsync(() => cut.FindAll("button")
                .First(b => b.TextContent.Contains("Run Distill", StringComparison.Ordinal))
                .Click());

            cut.WaitForAssertion(() =>
            {
                Assert.True(distill.LiveDistillCalled);
                var ids = distill.DistillCalls.SelectMany(c => c ?? Array.Empty<string>()).ToList();
                Assert.Contains("vf1", ids);
            });
        }

        [Fact]
        public void PendingDistill_AutoLoadsOnInit_WithoutClick()
        {
            // The distill list populates on page init so harvested-but-not-distilled videos are
            // visible without clicking "Load harvested".
            var distill = new RecordingDistillOrchestrator
            {
                Pending = new[] { Pending("v1") },
            };

            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                new MapBlockedStore(),
                new MapSiteIndexStore(),
                distill: distill);

            cut.WaitForAssertion(() => Assert.Contains("Select v1", cut.Markup));
        }

        [Fact]
        public void PendingDistill_LoadButton_IsPrimaryEmphasis()
        {
            // The loader is the sole required action in the Distill section — it must carry
            // primary emphasis (btn-primary), not the low-emphasis outline-secondary style.
            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                new MapBlockedStore(),
                new MapSiteIndexStore());

            cut.WaitForAssertion(() =>
            {
                var button = cut.FindAll("button")
                    .First(b => b.TextContent.Contains("Load harvested", StringComparison.Ordinal));
                var css = button.GetAttribute("class") ?? string.Empty;
                Assert.Contains("btn-primary", css);
                Assert.DoesNotContain("btn-outline-secondary", css);
            });
        }

        [Fact]
        public void OneClick_RefreshesPendingList_AndClearsInFlight_WithoutNavigation()
        {
            // Regression: the post-distill LoadPendingDistillAsync ran while _operationInFlight was
            // still true, so its in-flight guard no-op'd — distilled videos lingered in the pending
            // list and the spinner stayed until a manual navigate-away/back. The refresh now runs in
            // the finally after the flag clears.
            var distill = new RecordingDistillOrchestrator
            {
                Pending = new[] { Pending("v1") },
                LiveResult = new DistillResult
                {
                    Success = true,
                    VideosDistilled = 1,
                    DistilledVideos = new[] { Distilled("v1", 6) },
                },
            };

            var (cut, _, _, _) = RenderHarvest(new[] { Vid("v1", "Vid 1") }, new MapBlockedStore(), new MapSiteIndexStore(), distill: distill);

            // Auto-load on init shows v1 in the pending-distill list.
            cut.WaitForAssertion(() => Assert.Contains("Select v1", cut.Markup));

            BrowseChannel(cut);
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Vid 1']").Change(true));
            ClickOneClick(cut);

            cut.WaitForAssertion(() =>
            {
                // v1 distilled → dropped from the pending list without navigating away and back.
                Assert.DoesNotContain("Select v1", cut.Markup);
                // In-flight state cleared: the Cancel button (shown only while _operationInFlight) is gone.
                Assert.DoesNotContain("Cancel Harvest", cut.Markup);
            });
        }

        [Fact]
        public void ShowHidden_RevealsSkippedVideo_UnskipRestoresIt()
        {
            var skipped = new FakeSkippedVideoStore();
            skipped.Seed("vSkip");

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("vSkip", "Skipped One"), Vid("vNew", "New One") },
                new MapBlockedStore(),
                new MapSiteIndexStore(),
                skipped: skipped);

            BrowseChannel(cut);

            // Default: skipped video is hidden entirely.
            cut.WaitForAssertion(() => Assert.DoesNotContain("Skipped One", cut.Markup));

            // Toggle Show hidden → it appears in the hidden panel with an Un-skip action.
            cut.InvokeAsync(() => cut.Find("#showHiddenVideos").Change(true));
            cut.WaitForAssertion(() =>
            {
                Assert.Contains("Skipped One", cut.Markup);
                Assert.NotNull(cut.Find("button[aria-label='Un-skip Skipped One']"));
            });

            // Un-skip → row returns to the harvest list (now selectable), leaves the hidden panel.
            cut.InvokeAsync(() => cut.Find("button[aria-label='Un-skip Skipped One']").Click());
            cut.WaitForAssertion(() =>
            {
                Assert.NotNull(cut.Find("input[aria-label='Select Skipped One']"));
                Assert.Empty(cut.FindAll("button[aria-label='Un-skip Skipped One']"));
            });
        }

        [Fact]
        public async Task ShowHidden_RevealsBlockedVideo_UnblockCallsOrchestrator()
        {
            var blocked = new MapBlockedStore();
            blocked.Blocked.Add("vBlk");

            var (cut, maint, _, _) = RenderHarvest(
                new[] { Vid("vBlk", "Blocked One") },
                blocked,
                new MapSiteIndexStore());

            BrowseChannel(cut);

            // Blocked is hidden from the default list; reveal it under Show hidden.
            await cut.InvokeAsync(() => cut.Find("#showHiddenVideos").Change(true));
            cut.WaitForAssertion(
                () =>
                {
                    Assert.Contains("Blocked One", cut.Markup);
                    Assert.NotNull(cut.Find("button[aria-label='Un-block Blocked One']"));
                },
                UiTimeout);

            // Simulate the store no longer blocking so the badge re-resolves after unblock.
            blocked.Blocked.Remove("vBlk");
            await cut.InvokeAsync(() => cut.Find("button[aria-label='Un-block Blocked One']").Click());
            cut.WaitForAssertion(() => Assert.Contains("vBlk", maint.UnblockCalls), UiTimeout);
        }

        private static void ClickOneClick(IRenderedComponent<Harvest> cut)
        {
            cut.InvokeAsync(() => cut.FindAll("button")
                .First(b => b.TextContent.Contains("Harvest + Auto-distill", StringComparison.Ordinal))
                .Click());
        }

        private static PendingDistillVideo Pending(string id, string? distillStatus = null) => new()
        {
            YoutubeVideoId = id,
            Title = id,
            VideoUrl = $"https://youtu.be/{id}",
            PublishedUtc = DateTimeOffset.UtcNow,
            DistillStatus = distillStatus,
        };

        private static DistilledVideoResult Distilled(string id, int clipCount) => new()
        {
            NaturalKeyType = "youtube",
            NaturalKeyValue = id,
            ClipCount = clipCount,
        };

        [Fact]
        public void HarvestPage_DefaultView_HidesHarvestedAndSkipped()
        {
            var index = new MapSiteIndexStore();
            index.Rows["vDist"] = MakeIndexRow("vDist", "pending", null, false);
            var skipped = new FakeSkippedVideoStore();
            skipped.Seed("vSkip");

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("vNew", "New"), Vid("vDist", "Distilled"), Vid("vSkip", "Skipped") },
                new MapBlockedStore(),
                index,
                skipped: skipped);

            BrowseChannel(cut);

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("New", cut.Markup);
                Assert.DoesNotContain("Distilled", cut.Markup);
                Assert.DoesNotContain("Skipped", cut.Markup);
            });
        }

        [Fact]
        public void HarvestPage_ShowAll_RevealsHarvestedButNotSkipped()
        {
            var index = new MapSiteIndexStore();
            index.Rows["vDist"] = MakeIndexRow("vDist", "pending", null, false);
            var skipped = new FakeSkippedVideoStore();
            skipped.Seed("vSkip");

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("vNew", "New"), Vid("vDist", "Distilled"), Vid("vSkip", "Skipped") },
                new MapBlockedStore(),
                index,
                skipped: skipped);

            BrowseChannel(cut);
            cut.InvokeAsync(() => cut.Find("#showAllVideos").Change(true));

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("New", cut.Markup);
                Assert.Contains("Distilled", cut.Markup);
                Assert.DoesNotContain("Skipped", cut.Markup);
            });
        }

        [Fact]
        public void HarvestPage_Skip_RemovesRowAndCallsStore()
        {
            var skipped = new FakeSkippedVideoStore();

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("vA", "Alpha") },
                new MapBlockedStore(),
                new MapSiteIndexStore(),
                skipped: skipped);

            BrowseChannel(cut);
            cut.WaitForAssertion(() => Assert.Contains("Alpha", cut.Markup));

            cut.InvokeAsync(() => cut.Find("button[aria-label='Skip Alpha']").Click());

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("vA", skipped.AddCalls);
                Assert.DoesNotContain("Alpha", cut.Markup);
            });
        }

        [Fact]
        public void HarvestPage_SelectedThenHiddenByToggle_IsNotHarvested()
        {
            // Codex HIGH: a row selected in Show-all, then hidden by toggling back to the
            // unharvested-only default, must NOT be harvested.
            var index = new MapSiteIndexStore();
            index.Rows["vDist"] = MakeIndexRow("vDist", "pending", null, false);

            var (cut, _, harv, _) = RenderHarvest(
                new[] { Vid("vNew", "New"), Vid("vDist", "Distilled") },
                new MapBlockedStore(),
                index);

            BrowseChannel(cut);

            // Show all so the Distilled row is visible + selectable, then select both.
            cut.InvokeAsync(() => cut.Find("#showAllVideos").Change(true));
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select New']").Change(true));
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Distilled']").Change(true));

            // Toggle back to default — the Distilled row is now hidden.
            cut.InvokeAsync(() => cut.Find("#showAllVideos").Change(false));

            cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Harvest Selected", StringComparison.Ordinal)).Click());

            cut.WaitForAssertion(() =>
            {
                var allIds = harv.HarvestCalls.SelectMany(c => c ?? Array.Empty<string>()).ToList();
                Assert.Contains("vNew", allIds);
                Assert.DoesNotContain("vDist", allIds);
            });
        }

        [Fact]
        public async Task HarvestPage_UnskippedVideo_ReappearsOnRebrowse()
        {
            // Codex LOW (HSEL-03 end-to-end): a skipped video is hidden from browse, and after
            // un-skip (as the Skipped page does) it reappears on re-browse.
            var skipped = new FakeSkippedVideoStore();
            skipped.Seed("vA");

            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("vA", "Alpha") },
                new MapBlockedStore(),
                new MapSiteIndexStore(),
                skipped: skipped);

            BrowseChannel(cut);
            cut.WaitForAssertion(() => Assert.DoesNotContain("Alpha", cut.Markup));

            await skipped.RemoveSkipAsync("vA");

            BrowseChannel(cut);
            cut.WaitForAssertion(() => Assert.Contains("Alpha", cut.Markup));
        }

        [Fact]
        public void HarvestPage_WithSavedCreators_ShowsDropdownAndKeepsUrlFallback()
        {
            var creators = new FakeCreatorSourceStore();
            creators.Seed(("The Command Zone", "https://youtube.com/@TheCommandZone"));

            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                new MapBlockedStore(),
                new MapSiteIndexStore(),
                creators: creators);

            cut.WaitForAssertion(() =>
            {
                // SRC-02: dropdown with the saved creator + the paste-URL fallback option.
                Assert.NotNull(cut.Find("#creatorSelect"));
                Assert.Contains("The Command Zone", cut.Markup);
                Assert.Contains("paste a URL instead", cut.Markup);
                // The URL/handle input remains available as the one-off fallback.
                Assert.NotNull(cut.Find("#channelInput"));
            });
        }

        [Fact]
        public void HarvestPage_NoSavedCreators_ShowsOnlyUrlInput()
        {
            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                new MapBlockedStore(),
                new MapSiteIndexStore());

            cut.WaitForAssertion(() =>
            {
                Assert.NotNull(cut.Find("#channelInput"));
                Assert.Empty(cut.FindAll("#creatorSelect"));
            });
        }

        // ── SUI-05: creator filter tests ──────────────────────────────────────

        [Fact]
        public async Task CreatorFilter_NarrowsBrowseRows_ToSelectedCreator()
        {
            // Browsed rows span two creators; filter to "Alice" shows only Alice's video.
            var (cut, _, _, _) = RenderHarvest(
                new[]
                {
                    Vid("v1", "Alpha", "Alice"),
                    Vid("v2", "Beta", "Bob"),
                },
                new MapBlockedStore(),
                new MapSiteIndexStore());

            BrowseChannel(cut);

            cut.WaitForAssertion(
                () =>
                {
                    // Both rows visible and a creator filter dropdown rendered.
                    Assert.Contains("Alpha", cut.Markup);
                    Assert.Contains("Beta", cut.Markup);
                    Assert.NotNull(cut.Find("#browseCreatorFilter"));
                },
                UiTimeout);

            // Select "Alice" from the filter.
            await cut.InvokeAsync(() => cut.Find("#browseCreatorFilter").Change("Alice"));

            cut.WaitForAssertion(
                () =>
                {
                    Assert.Contains("Alpha", cut.Markup);
                    Assert.DoesNotContain("Beta", cut.Markup);
                },
                UiTimeout);
        }

        [Fact]
        public void CreatorFilter_AllCreators_ShowsAllRows()
        {
            // After narrowing to a creator, switching back to "All creators" restores all rows.
            var (cut, _, _, _) = RenderHarvest(
                new[]
                {
                    Vid("v1", "Alpha", "Alice"),
                    Vid("v2", "Beta", "Bob"),
                },
                new MapBlockedStore(),
                new MapSiteIndexStore());

            BrowseChannel(cut);
            cut.InvokeAsync(() => cut.Find("#browseCreatorFilter").Change("Alice"));
            cut.WaitForAssertion(() => Assert.DoesNotContain("Beta", cut.Markup));

            // Reset to "All creators" (empty string).
            cut.InvokeAsync(() => cut.Find("#browseCreatorFilter").Change(string.Empty));

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("Alpha", cut.Markup);
                Assert.Contains("Beta", cut.Markup);
            });
        }

        [Fact]
        public void CreatorFilter_ComposesWithUnharvestedDefault()
        {
            // Alice-NotHarvested and Alice-Distilled; filter to Alice + default (unharvested-only)
            // shows only Alice-NotHarvested. Bob-NotHarvested is hidden by the creator filter.
            var index = new MapSiteIndexStore();
            index.Rows["vAliceDist"] = MakeIndexRow("vAliceDist", "pending", null, false);

            var (cut, _, _, _) = RenderHarvest(
                new[]
                {
                    Vid("vAliceNew", "Alice-NotHarvested", "Alice"),
                    Vid("vAliceDist", "Alice-Distilled", "Alice"),
                    Vid("vBobNew", "Bob-NotHarvested", "Bob"),
                },
                new MapBlockedStore(),
                index);

            BrowseChannel(cut);

            // Default: only NotHarvested rows visible.
            cut.WaitForAssertion(() =>
            {
                Assert.Contains("Alice-NotHarvested", cut.Markup);
                Assert.DoesNotContain("Alice-Distilled", cut.Markup);
                Assert.Contains("Bob-NotHarvested", cut.Markup);
            });

            // Apply creator filter "Alice" — only Alice-NotHarvested remains.
            cut.InvokeAsync(() => cut.Find("#browseCreatorFilter").Change("Alice"));

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("Alice-NotHarvested", cut.Markup);
                Assert.DoesNotContain("Alice-Distilled", cut.Markup);
                Assert.DoesNotContain("Bob-NotHarvested", cut.Markup);
            });
        }

        [Fact]
        public void CreatorFilter_ComposesWithSkipExclusion()
        {
            // A skipped video from Alice must not appear even if the creator filter is "Alice".
            // Include a Bob video so two distinct creators exist → the filter dropdown is rendered.
            var skipped = new FakeSkippedVideoStore();
            skipped.Seed("vAliceSkip");

            var (cut, _, _, _) = RenderHarvest(
                new[]
                {
                    Vid("vAliceNew", "Alice-New", "Alice"),
                    Vid("vAliceSkip", "Alice-Skipped", "Alice"),
                    Vid("vBobNew", "Bob-New", "Bob"),
                },
                new MapBlockedStore(),
                new MapSiteIndexStore(),
                skipped: skipped);

            BrowseChannel(cut);

            // The filter dropdown is rendered because Alice+Bob = 2 distinct creators.
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#browseCreatorFilter")));

            cut.InvokeAsync(() => cut.Find("#browseCreatorFilter").Change("Alice"));

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("Alice-New", cut.Markup);
                // Skip exclusion is unconditional — skipped rows never appear even under Alice filter.
                Assert.DoesNotContain("Alice-Skipped", cut.Markup);
                // Bob's row is hidden by the creator filter.
                Assert.DoesNotContain("Bob-New", cut.Markup);
            });
        }

        [Fact]
        public void CreatorFilter_SelectUnderA_ThenFilterToB_IsNotHarvested()
        {
            // Codex LOW (T-62-03): select v1 (Alice) with no filter, then switch filter to Bob
            // → v1 is hidden → harvest must NOT include v1 (only visible rows are harvestable).
            var (cut, _, harv, _) = RenderHarvest(
                new[]
                {
                    Vid("v1", "Alice-Video", "Alice"),
                    Vid("v2", "Bob-Video", "Bob"),
                },
                new MapBlockedStore(),
                new MapSiteIndexStore());

            BrowseChannel(cut);
            cut.WaitForAssertion(() => Assert.Contains("Alice-Video", cut.Markup));

            // Select Alice's video (both creators visible, no filter yet).
            cut.InvokeAsync(() => cut.Find("input[aria-label='Select Alice-Video']").Change(true));

            // Switch filter to Bob — Alice-Video is now hidden.
            cut.InvokeAsync(() => cut.Find("#browseCreatorFilter").Change("Bob"));

            cut.WaitForAssertion(() =>
            {
                Assert.DoesNotContain("Alice-Video", cut.Markup);
                Assert.Contains("Bob-Video", cut.Markup);
            });

            // Harvest: only Bob's (visible) row can be harvested. v1 was selected but hidden → excluded.
            cut.InvokeAsync(() => cut.FindAll("button")
                .First(b => b.TextContent.Contains("Harvest Selected", StringComparison.Ordinal))
                .Click());

            cut.WaitForAssertion(() =>
            {
                // No harvest call should be made (Bob's row was never selected, Alice's is hidden).
                // The harvest button requires at least one selected video; Bob is visible but unselected
                // → selectedCount = 0 → nothing harvested OR only visible-selected rows.
                var allIds = harv.HarvestCalls.SelectMany(c => c ?? Array.Empty<string>()).ToList();
                Assert.DoesNotContain("v1", allIds);
            });
        }

        [Fact]
        public void CreatorFilter_SingleCreator_DoesNotRenderDropdown()
        {
            // When all browsed rows belong to one creator, the filter dropdown is not shown
            // (no value in filtering when there's nothing to filter by).
            var (cut, _, _, _) = RenderHarvest(
                new[]
                {
                    Vid("v1", "Alpha", "Alice"),
                    Vid("v2", "Beta", "Alice"),
                },
                new MapBlockedStore(),
                new MapSiteIndexStore());

            BrowseChannel(cut);

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("Alpha", cut.Markup);
                Assert.Empty(cut.FindAll("#browseCreatorFilter"));
            });
        }

        // ── F-03: thumbnails carry a meaningful alternative text ──

        [Fact]
        public void ChannelBrowseThumbnail_AltNamesTheVideo()
        {
            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("v1", "Sultai Cavern Deep Dive") },
                new MapBlockedStore(),
                new MapSiteIndexStore());

            BrowseChannel(cut);

            cut.WaitForAssertion(() =>
            {
                var thumb = cut.Find("img[src*='img.youtube.com']");
                Assert.Equal("Sultai Cavern Deep Dive thumbnail", thumb.GetAttribute("alt"));
            });
        }

        [Fact]
        public void NoThumbnailDeclaresItselfDecorative()
        {
            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("v1", "Sultai Cavern Deep Dive") },
                new MapBlockedStore(),
                new MapSiteIndexStore());

            BrowseChannel(cut);

            cut.WaitForAssertion(() =>
            {
                Assert.NotEmpty(cut.FindAll("img[src*='img.youtube.com']"));
                Assert.Empty(cut.FindAll("img[alt='']"));
            });
        }

        // ── F-08: in-page section navigation ──

        [Fact]
        public void SectionNav_RendersFourJumpLinks()
        {
            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                new MapBlockedStore(),
                new MapSiteIndexStore());

            cut.WaitForAssertion(() =>
            {
                var links = cut.FindAll("nav.harvest-section-nav a");
                Assert.Equal(4, links.Count);
                Assert.Equal(
                    new[] { "#harvest-browse", "#harvest-queue", "#harvest-run", "#harvest-distill" },
                    links.Select(l => l.GetAttribute("href")).ToArray());
            });
        }

        [Fact]
        public void EverySectionNavTarget_ExistsOnThePage()
        {
            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                new MapBlockedStore(),
                new MapSiteIndexStore());

            cut.WaitForAssertion(() =>
            {
                Assert.NotNull(cut.Find("#harvest-browse"));
                Assert.NotNull(cut.Find("#harvest-queue"));
                Assert.NotNull(cut.Find("#harvest-run"));
                Assert.NotNull(cut.Find("#harvest-distill"));
            });
        }

        [Fact]
        public void SectionNavTargets_AreProgrammaticallyFocusable()
        {
            var (cut, _, _, _) = RenderHarvest(
                Array.Empty<YouTubeChannelVideo>(),
                new MapBlockedStore(),
                new MapSiteIndexStore());

            cut.WaitForAssertion(() =>
            {
                foreach (var id in new[] { "harvest-browse", "harvest-queue", "harvest-run", "harvest-distill" })
                {
                    Assert.Equal("-1", cut.Find($"#{id}").GetAttribute("tabindex"));
                }
            });
        }

        // ── F-10: in-table actions opt out of the touch floor ──

        [Fact]
        public void TableActionButtons_CarryTheCompactClass()
        {
            var (cut, _, _, _) = RenderHarvest(
                new[] { Vid("v1", "Sultai Cavern Deep Dive") },
                new MapBlockedStore(),
                new MapSiteIndexStore());

            BrowseChannel(cut);

            cut.WaitForAssertion(() =>
            {
                var tableButtons = cut.FindAll("td button");
                Assert.NotEmpty(tableButtons);
                Assert.All(tableButtons, b => Assert.Contains("btn-table-action", b.ClassList));
            });
        }

        private (IRenderedComponent<Harvest> Cut, FakeContentKbOrchestrator Maint, RecordingHarvestOrchestrator Harv, StubLister Lister) RenderHarvest(
            IReadOnlyList<YouTubeChannelVideo> recent,
            MapBlockedStore blocked,
            MapSiteIndexStore index,
            ContentMaintenanceResult? cannedBlock = null,
            IReadOnlyList<YouTubeChannelVideo>? byIds = null,
            IDistillOrchestrator? distill = null,
            bool isSubscriptionProvider = true,
            FakeCreatorSourceStore? creators = null,
            FakeSkippedVideoStore? skipped = null,
            RecordingHarvestOrchestrator? harv = null,
            HarvestJobRunner? runner = null)
        {
            JSInterop.Mode = JSRuntimeMode.Loose;

            var maint = new FakeContentKbOrchestrator();
            if (cannedBlock is not null)
            {
                maint.CannedMaintenanceResult = cannedBlock;
            }

            var harvestOrchestrator = harv ?? new RecordingHarvestOrchestrator();
            var lister = new StubLister
            {
                RecentResult = recent,
                ByIdsResult = byIds ?? Array.Empty<YouTubeChannelVideo>(),
            };
            var resolver = BuildResolver(blocked, index);
            var autoApproveSignal = new ClipCountAutoApproveSignal();
            var autoApproveSettingsStore = new AutoApproveSettingsStore(_autoApproveDir);
            var capOverride = new SessionCapOverride();
            var spendLedger = new StubLedger();
            var creatorStore = creators ?? new FakeCreatorSourceStore();
            var harvestRunner = runner ?? new HarvestJobRunner();

            Services.AddSingleton<IYouTubeChannelVideoLister>(lister);
            Services.AddSingleton<IHarvestOrchestrator>(harvestOrchestrator);
            Services.AddSingleton<IContentSourceManager>(new StubSourceManager());
            Services.AddSingleton<VideoStatusResolver>(resolver);
            Services.AddSingleton<IContentSiteIndexStore>(index);
            Services.AddSingleton<IAutoApproveSignal>(autoApproveSignal);
            Services.AddSingleton<IDistillOrchestrator>(distill ?? new RecordingDistillOrchestrator());
            Services.AddSingleton(new StudioDistillConfig(isSubscriptionProvider));
            Services.AddSingleton(capOverride);
            Services.AddSingleton<ILlmSpendLedger>(spendLedger);
            Services.AddSingleton<IContentMaintenanceOrchestrator>(maint);
            Services.AddSingleton(autoApproveSettingsStore);
            Services.AddSingleton<ICreatorSourceStore>(creatorStore);
            Services.AddSingleton<ISkippedVideoStore>(skipped ?? new FakeSkippedVideoStore());

            // Why: Harvest page collaborators (Phase 82 SRP split) — the page now [Inject]s these
            // instead of the raw services directly, so bUnit's DI container needs them registered too.
            Services.AddSingleton(new HarvestQueueCoordinator(lister, resolver));
            Services.AddSingleton(harvestRunner);
            Services.AddSingleton(new AutoApproveSettingsCoordinator(autoApproveSettingsStore, autoApproveSignal, index));
            Services.AddSingleton(new CreatorManagementCoordinator(creatorStore, maint));
            Services.AddSingleton(new SpendCapCoordinator(spendLedger, capOverride));

            var cut = Render<Harvest>();
            WaitForPageReady(cut);
            return (cut, maint, harvestOrchestrator, lister);
        }

        private VideoStatusResolver BuildResolver(MapBlockedStore blocked, MapSiteIndexStore index)
        {
            return new VideoStatusResolver(blocked, index, new EmptySourceStore(), new EmptyVideoStore());
        }

        private static YouTubeChannelVideo Vid(string id, string title = "T", string? channelTitle = null)
        {
            return new YouTubeChannelVideo
            {
                VideoId = id,
                Url = $"https://youtu.be/{id}",
                Title = title,
                ChannelId = "UCchan",
                ChannelTitle = channelTitle ?? "Chan",
                PublishedUtc = DateTimeOffset.UtcNow,
            };
        }

        private static ContentSiteIndexRow MakeIndexRow(
            string id,
            string approvalStatus,
            DateTimeOffset? pushedToProdUtc,
            bool isVisible)
        {
            return new ContentSiteIndexRow
            {
                Id = 1,
                Source = "test-channel",
                Title = "t",
                VideoUrl = $"https://youtu.be/{id}",
                ArtifactPath = $"content-kb/test-channel/{id}.md",
                IndexedUtc = DateTimeOffset.UtcNow,
                ArchetypeTags = Array.Empty<string>(),
                BracketTags = Array.Empty<string>(),
                CardCategoryTags = Array.Empty<string>(),
                ApprovalStatus = approvalStatus,
                PushedToProdUtc = pushedToProdUtc,
                IsVisible = isVisible,
                YoutubeVideoId = id,
            };
        }

        private static void BrowseChannel(IRenderedComponent<Harvest> cut)
        {
            WaitForPageReady(cut);
            cut.InvokeAsync(() => cut.Find("#channelInput").Change("https://youtube.com/@chan")).GetAwaiter().GetResult();
            cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Browse", StringComparison.Ordinal)).Click()).GetAwaiter().GetResult();
            cut.WaitForAssertion(() => Assert.DoesNotContain("Fetching channel videos", cut.Markup), UiTimeout);
        }

        private static void WaitForPageReady(IRenderedComponent<Harvest> cut)
        {
            cut.WaitForAssertion(
                () =>
                {
                    var marker = cut.Find("#harvestInitState");
                    Assert.Equal("true", marker.GetAttribute("data-ready"));
                },
                UiTimeout);
        }

        private sealed class MapBlockedStore : IBlockedVideoStore
        {
            public HashSet<string> Blocked { get; } = new();

            public Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task AddBlockAsync(string youtubeVideoId, string? reason, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<bool> RemoveBlockAsync(string youtubeVideoId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }

            public Task<bool> IsBlockedAsync(string youtubeVideoId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Blocked.Contains(youtubeVideoId));
            }

            public Task<IReadOnlyList<BlockedVideo>> ListBlockedAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult((IReadOnlyList<BlockedVideo>)Array.Empty<BlockedVideo>());
            }
        }

        private sealed class MapSiteIndexStore : IContentSiteIndexStore
        {
            public Dictionary<string, ContentSiteIndexRow> Rows { get; } = new();

            /// <summary>Records every batch SetApprovalStatusAsync(keys, status) call (keys + status).</summary>
            public List<(IReadOnlyList<(string Type, string Value)> Keys, string Status)> ApprovalBatchCalls { get; } = new();

            public Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task UpsertRowAsync(ContentSiteIndexRow row, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task UpsertRowPreservingVisibilityAsync(ContentSiteIndexRow row, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task UpsertContentColumnsOnlyAsync(ContentSiteIndexRow row, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<ContentSiteIndexRow?> GetByNaturalKeyAsync(
                string naturalKeyType,
                string naturalKeyValue,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Rows.TryGetValue(naturalKeyValue, out var row) ? row : null);
            }

            public Task<IReadOnlyList<ContentSiteIndexRow>> GetPublishedRowsAsync(CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<IReadOnlyList<ContentSiteIndexRow>> GetApprovedRowsAsync(CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<IReadOnlyList<ContentSiteIndexRow>> GetAllRowsAsync(CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<ContentSiteIndexRow?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<ContentSiteIndexRow?> GetPublishedByIdAsync(long id, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> SetVisibilityAsync(long id, bool visible, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> SetHiddenAsync(long id, bool hidden, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> DeleteByIdAsync(long id, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> DeleteAllRowsAsync(CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> SetEvergreenAsync(long id, bool evergreen, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> SetVisibilityBySourceAsync(string source, bool visible, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> SetHiddenBySourceAsync(string source, bool hidden, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> SetApprovalStatusAsync(
                string naturalKeyType,
                string naturalKeyValue,
                string status,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> SetApprovalStatusAsync(
                IReadOnlyList<(string Type, string Value)> keys,
                string status,
                CancellationToken cancellationToken = default)
            {
                ApprovalBatchCalls.Add((keys, status));
                return Task.FromResult(keys.Count);
            }

            public Task<int> StampPushedToProdAsync(
                IReadOnlyList<(string Type, string Value)> keys,
                DateTimeOffset pushedUtc,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> SetVisibilityAsync(
                IReadOnlyList<(string Type, string Value)> keys,
                bool visible,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class EmptySourceStore : IContentSourceStore
        {
            public Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<long> InsertSourceAsync(
                string sourceSlug,
                string displayName,
                string sourceType,
                string sourceUrl,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<ContentSource?> GetSourceAsync(long id, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<ContentSource?> GetSourceByUrlAsync(string url, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task SetEnabledAsync(long id, bool isEnabled, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<IReadOnlyList<ContentSource>> ListEnabledSourcesAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult((IReadOnlyList<ContentSource>)Array.Empty<ContentSource>());
            }
        }

        private sealed class EmptyVideoStore : IContentVideoStore
        {
            public Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<long> InsertVideoAsync(
                long sourceId,
                string? youtubeVideoId,
                string? rssGuid,
                string title,
                string videoUrl,
                DateTimeOffset? publishedUtc,
                string transcriptStatus,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<ContentVideo?> GetVideoByYoutubeIdAsync(
                long sourceId,
                string youtubeVideoId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<ContentVideo?>(null);
            }

            public Task<IReadOnlyList<ContentVideo>> ListVideosPendingDistillAsync(long sourceId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<IReadOnlyList<PendingDistillProjection>> ListPendingDistillDisplayAsync(long sourceId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task UpdateTranscriptStatusAsync(long videoId, string status, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<long> InsertTranscriptAsync(long videoId, string source, string body, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<ContentTranscriptBody?> GetLatestTranscriptAsync(long videoId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<long> InsertSummaryAsync(long videoId, string body, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<long> InsertClipAsync(
                long videoId,
                int timestampS,
                string excerpt,
                int sortOrder,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<long> InsertTagAsync(
                long videoId,
                string dimension,
                string tagValue,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DeleteVideoAsync(long videoId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> DeleteVideoByYoutubeIdAsync(string youtubeVideoId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> DeleteAllVideosAsync(CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task ClearDistillOutputAsync(long videoId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<string?> GetDistillStatusAsync(long videoId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task SetDistillStatusAsync(long videoId, string status, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> CountTranscriptsByVideoAsync(long videoId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> CountSummariesByVideoAsync(long videoId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> CountClipsByVideoAsync(long videoId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<int> CountTagsByVideoAsync(long videoId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class StubLister : IYouTubeChannelVideoLister
        {
            public IReadOnlyList<YouTubeChannelVideo> RecentResult { get; set; } = Array.Empty<YouTubeChannelVideo>();

            public IReadOnlyList<YouTubeChannelVideo> ByIdsResult { get; set; } = Array.Empty<YouTubeChannelVideo>();

            public Task<IReadOnlyList<YouTubeChannelVideo>> ListRecentAsync(string channelUrl, int limit, int skip = 0, CancellationToken ct = default)
            {
                return Task.FromResult(RecentResult);
            }

            public Task<IReadOnlyList<YouTubeChannelVideo>> GetByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
            {
                return Task.FromResult(ByIdsResult);
            }

            public Task<IReadOnlyList<YouTubeChannelVideo>> ListPlaylistAsync(string playlistUrl, int limit, int skip = 0, CancellationToken ct = default)
            {
                return Task.FromResult((IReadOnlyList<YouTubeChannelVideo>)Array.Empty<YouTubeChannelVideo>());
            }
        }

        private sealed class RecordingHarvestOrchestrator : IHarvestOrchestrator
        {
            public List<IReadOnlyList<string>?> HarvestCalls { get; } = new();
            public TaskCompletionSource<bool>? StartedSignal { get; set; }
            public TaskCompletionSource<bool>? ReleaseSignal { get; set; }

            public async Task<HarvestResult> HarvestAsync(
                int limit,
                IReadOnlyList<string>? videoIds = null,
                long? sourceId = null,
                IOrchestratorProgress? progress = null,
                CancellationToken cancellationToken = default)
            {
                HarvestCalls.Add(videoIds);
                StartedSignal?.TrySetResult(true);
                if (ReleaseSignal is not null)
                {
                    await ReleaseSignal.Task.WaitAsync(cancellationToken);
                }

                return new HarvestResult
                {
                    Success = true,
                    Captions = videoIds?.Count ?? 0,
                };
            }
        }

        private sealed class StubSourceManager : IContentSourceManager
        {
            public Task<ContentSourceResult> EnsureYoutubeSourceAsync(
                string url,
                string name,
                IOrchestratorProgress? progress = null,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ContentSourceResult
                {
                    Success = true,
                    Outcome = ContentSourceResult.ContentSourceOutcome.Added,
                    Id = 1L,
                });
            }

            public Task<ContentSourceResult> AddSourceAsync(
                string url,
                string name,
                string type,
                IOrchestratorProgress? progress = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<ContentSourceResult> SetSourceEnabledAsync(
                long id,
                bool enabled,
                IOrchestratorProgress? progress = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class StubLedger : ILlmSpendLedger
        {
            public Task RecordCallAsync(
                long videoId,
                int inputTokens,
                int outputTokens,
                decimal costUsd,
                string monthKey,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<decimal> GetMonthlyTotalAsync(string yearMonth, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(0m);
            }

            public Task<bool> WouldExceedCapAsync(
                decimal projectedCallCostUsd,
                string monthKey,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public decimal GetMonthlyCapUsd()
            {
                return 15m;
            }
        }

        // Recording/configurable distill fake (Codex MEDIUM): replaces the throwing stub so the
        // one-click harvest→distill chain can be exercised. Records the videoIds DistillAsync was
        // called with (proves harvest-ready-only input, HIGH #2) and returns a configured result.
        private sealed class RecordingDistillOrchestrator : IDistillOrchestrator
        {
            /// <summary>Pending-distill videos ListPendingDistillAsync returns (the harvest-ready set).</summary>
            public IReadOnlyList<PendingDistillVideo> Pending { get; set; } = Array.Empty<PendingDistillVideo>();

            /// <summary>The configured DistillResult DistillAsync returns for non-dry-run calls.</summary>
            public DistillResult? LiveResult { get; set; }

            /// <summary>Every set of videoIds passed to a non-dry-run DistillAsync call, in order.</summary>
            public List<IReadOnlyList<string>?> DistillCalls { get; } = new();

            /// <summary>Whether any non-dry-run DistillAsync call was made.</summary>
            public bool LiveDistillCalled => DistillCalls.Count > 0;

            public Task<DistillResult> DistillAsync(
                int limit,
                bool dryRun,
                bool isSubscriptionProvider,
                bool redistill = false,
                IReadOnlyList<string>? videoIds = null,
                IOrchestratorProgress? progress = null,
                CancellationToken cancellationToken = default)
            {
                if (!dryRun)
                {
                    DistillCalls.Add(videoIds);
                    // Mimic real behavior: a distilled video is no longer pending-distill, so a
                    // subsequent ListPendingDistillAsync drops it. Lets tests observe the post-op
                    // refresh of the pending list.
                    if (videoIds is not null)
                    {
                        Pending = Pending.Where(p => !videoIds.Contains(p.YoutubeVideoId)).ToList();
                    }
                }

                var result = LiveResult ?? new DistillResult { Success = true };
                return Task.FromResult(result);
            }

            public Task<IReadOnlyList<PendingDistillVideo>> ListPendingDistillAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Pending);
            }
        }
    }
}
