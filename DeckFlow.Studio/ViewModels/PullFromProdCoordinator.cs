using DeckFlow.Core.Content;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Orchestration;
using DeckFlow.Studio.Services;
using Microsoft.Extensions.Logging;

namespace DeckFlow.Studio.ViewModels;

/// <summary>
/// Orchestration for the Pull-from-Production workflow, extracted from the <c>PullFromProd</c> page
/// code-behind (H1 god-component split). Owns the read-only prod pull (read-only prod
/// read, local git-tree body resolution, local classify) and the local-only adopt apply (content
/// upsert + approval mirror + body copy). This type performs no rendering and holds no
/// per-page UI state — the page keeps the progress log, resolution map, busy guards, cancellation,
/// and <c>StateHasChanged</c>. It NEVER writes to production. Behavior is identical to the prior
/// inline implementation.
/// </summary>
public sealed class PullFromProdCoordinator
{
    private readonly IContentSiteIndexStore _indexStore;
    private readonly IGitRepository _git;
    private readonly IProdContentReader _prodReader;
    private readonly IStudioProdConnectionSource _prodConnection;
    private readonly ContentKbOrchestratorOptions _options;
    private readonly ILogger<PullFromProdCoordinator> _logger;

    /// <summary>Creates the coordinator with the local store, git repository, read-only prod reader, config, options, and logger.</summary>
    public PullFromProdCoordinator(
        IContentSiteIndexStore indexStore,
        IGitRepository git,
        IProdContentReader prodReader,
        IStudioProdConnectionSource prodConnection,
        ContentKbOrchestratorOptions options,
        ILogger<PullFromProdCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(indexStore);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(prodReader);
        ArgumentNullException.ThrowIfNull(prodConnection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _indexStore = indexStore;
        _git = git;
        _prodReader = prodReader;
        _prodConnection = prodConnection;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the data root (parent of <c>ArtifactRoot</c>, which already carries content-kb/) and
    /// the isolated pull-staging directory under it.
    /// </summary>
    public PullPaths ResolvePaths()
    {
        var dataRoot = Path.GetDirectoryName(_options.ArtifactRoot) ?? _options.ArtifactRoot;
        return new PullPaths(dataRoot);
    }

    /// <summary>Builds the acknowledged-divergence lookup key for a diff entry.</summary>
    /// <param name="entry">The diff entry whose natural key should be encoded.</param>
    /// <returns>The byte-identical <c>{NaturalKeyType}:{NaturalKeyValue}</c> acknowledgment key.</returns>
    public static string AcknowledgmentKey(SyncDiffEntry entry) => $"{entry.NaturalKeyType}:{entry.NaturalKeyValue}";

    /// <summary>
    /// Reads the live production content index (read-only, NO DDL), resolves each prod body from the
    /// local git tree, and classifies the result against the local store — returning only the
    /// differing entries with their per-entry artifact-available flag stamped. Sets the current
    /// stage name via <paramref name="onStage"/> (a synchronous callback so a fault reads the exact
    /// stage in flight — diagnostic copy) and emits human-readable progress lines to
    /// <paramref name="log"/>. NEVER writes to production.
    /// </summary>
    public async Task<PullClassifyResult> PullAndClassifyAsync(
        IProgress<string> log,
        Action<string> onStage,
        CancellationToken cancellationToken)
    {
        var repoRoot = await _git.ResolveRepoRootAsync(StudioRepoLocator.ResolveStartDirectory(), cancellationToken).ConfigureAwait(false);
        var freshness = await CheckFreshnessAsync(repoRoot, log, onStage, cancellationToken).ConfigureAwait(false);

        // R1: read prod via the read-only reader — plain SELECT, NO EnsureSchemaAsync/DDL.
        onStage("read production content_site_index");
        log.Report("Reading production content_site_index…");

        // Why: the prod conn string is read ephemerally here, never materialized into DI state (D-03/D-07).
        var rawConnStr = _prodConnection.ConnectionString;
        var prodRows = await _prodReader.ReadAllAsync(rawConnStr, cancellationToken).ConfigureAwait(false);

        log.Report($"  {prodRows.Count} row(s) read from production.");

        onStage("resolve local repo bodies");
        log.Report($"Resolving {prodRows.Count} body/bodies from local repository…");

        var availableBodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prodRow in prodRows)
        {
            if (!ArtifactPathSafety.TryBuildContainedPath(repoRoot, prodRow.ArtifactPath, out var repoBody))
            {
                log.Report("  body SKIPPED (invalid path)");
                continue;
            }

            var present = File.Exists(repoBody);
            log.Report(present
                ? $"  body present: {prodRow.ArtifactPath}"
                : $"  body not in local git repo (prod-only/unpublished): {prodRow.ArtifactPath}");
            if (present)
            {
                availableBodies[prodRow.ArtifactPath] = repoBody;
            }
        }

        onStage("classify");
        log.Report("Classifying diff against local store…");

        var localRows = await _indexStore.GetAllRowsAsync(cancellationToken).ConfigureAwait(false);

        // Classify (omits in-sync pairs, R3), then stamp ArtifactDownloaded per entry. Pass the logger so
        // rows with no natural key are surfaced as warnings, not dropped silently (D-08).
        var entries = ContentSyncDiffClassifier.Classify(prodRows, localRows, _logger)
            .Select(e => StampArtifactAvailabilityAndDivergence(
                e,
                availableBodies.TryGetValue(e.ArtifactPath, out var repoBody),
                repoBody))
            .ToList();

        log.Report($"Done — {entries.Count} differing entry/entries found. "
            + $"{availableBodies.Count}/{prodRows.Count} body/bodies resolved from the local repo.");

        return new PullClassifyResult(entries, freshness);
    }

    /// <summary>
    /// Applies "adopt prod" resolutions to the LOCAL store only: content-columns-only upsert +
    /// approval-status mirror, then best-effort copy of the git-tree body into the live tree.
    /// Production is never modified. Reports the running per-entry result list to
    /// <paramref name="progress"/> after each entry so the page can render incrementally. The caller
    /// pre-filters <paramref name="adoptEntries"/> to entries whose resolution is "adopt prod", that
    /// are not local-only, and that carry a prod row.
    /// </summary>
    public async Task<IReadOnlyList<PullApplyRowResult>> ApplyAdoptionsAsync(
        IReadOnlyList<SyncDiffEntry> adoptEntries,
        string dataRoot,
        IProgress<IReadOnlyList<PullApplyRowResult>> progress,
        IReadOnlySet<string> acknowledgedDivergentKeys,
        CancellationToken cancellationToken)
    {
        var results = new List<PullApplyRowResult>();
        var repoRoot = await _git.ResolveRepoRootAsync(StudioRepoLocator.ResolveStartDirectory(), cancellationToken).ConfigureAwait(false);

        foreach (var entry in adoptEntries)
        {
            // Defensive: the page pre-filters these, but never adopt a local-only / prod-less row.
            if (entry.Kind == SyncDiffKind.LocalOnly || entry.ProdRow is null)
            {
                continue;
            }

            if ((entry.BodyDivergence is BodyDivergenceStatus.Confirmed or BodyDivergenceStatus.Indeterminate)
                && !acknowledgedDivergentKeys.Contains(AcknowledgmentKey(entry)))
            {
                results.Add(new PullApplyRowResult(entry.Title, entry.NaturalKeyType, entry.NaturalKeyValue, true,
                    "Skipped (divergent, not acknowledged)",
                    "Entry was not applied because body divergence was not explicitly acknowledged."));
                progress.Report(results.ToList());
                continue;
            }

            var prodRow = entry.ProdRow;
            // entry.NaturalKeyType now carries the stored ContentSourceType vocabulary
            // ("youtube_channel"/"podcast_rss") that the store + SetApprovalStatusAsync key on (D-07), so
            // read it directly instead of re-deriving from the prod row.
            var keyType = entry.NaturalKeyType;
            var keyValue = entry.NaturalKeyValue;

            try
            {
                // LOCAL-only apply: content columns + mirror prod approval_status (Q2 — reflect prod's
                // actual state, never a blind pending). is_visible/is_hidden untouched — adopting never
                // auto-publishes. Never the full-row upsert (Pitfall 3).
                await _indexStore.UpsertContentColumnsOnlyAsync(prodRow, cancellationToken).ConfigureAwait(false);
                await _indexStore.SetApprovalStatusAsync(keyType, keyValue, prodRow.ApprovalStatus, cancellationToken).ConfigureAwait(false);

                var note = "row updated; approval mirrored from prod";
                var validSource = ArtifactPathSafety.TryBuildContainedPath(repoRoot, entry.ArtifactPath, out var repoBody);
                var validDest = ArtifactPathSafety.TryBuildContainedPath(dataRoot, entry.ArtifactPath, out var liveDest);
                if (!validSource || !validDest)
                {
                    note = "row updated; body path invalid, not copied; approval mirrored from prod";
                }
                else if (File.Exists(repoBody))
                {
                    // Copy the git-tree body into the live tree (local only). The row upsert above
                    // is the primary effect of adopt and has already succeeded; body copy is
                    // best-effort and must NOT fail the whole entry.
                    var liveDir = Path.GetDirectoryName(liveDest);
                    if (!string.IsNullOrEmpty(liveDir))
                    {
                        Directory.CreateDirectory(liveDir);
                    }

                    File.Copy(repoBody, liveDest, overwrite: true);
                    note = "row updated + body copied from local repo; approval mirrored from prod";
                }
                else
                {
                    // R4: partial local repo — upsert + approval still applied; skip ONLY File.Copy.
                    note = "row updated; body not in local git repo (prod-only or unpublished), not copied; approval mirrored from prod";
                }

                results.Add(new PullApplyRowResult(entry.Title, entry.NaturalKeyType, keyValue, true, "Adopted", note));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Why: log the full exception to the server-side Serilog file so "see logs" is truthful
                // and the failure is diagnosable; the UI note stays sanitized — never surface ex.Message,
                // which can carry secrets/paths (D-07).
                _logger.LogError(ex, "Local apply failed for pull-from-prod entry {KeyType}:{KeyValue}.", keyType, keyValue);
                results.Add(new PullApplyRowResult(entry.Title, entry.NaturalKeyType, keyValue, false, "Failed",
                    "Local apply failed for this entry — see logs."));
            }

            progress.Report(results.ToList());
        }

        return results;
    }

    private async Task<PullFreshnessStatus> CheckFreshnessAsync(
        string repoRoot,
        IProgress<string> log,
        Action<string> onStage,
        CancellationToken cancellationToken)
    {
        onStage("check local checkout freshness");
        using var freshnessCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        freshnessCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var branch = await _git.GetCurrentBranchAsync(repoRoot, freshnessCts.Token).ConfigureAwait(false);
            await _git.FetchAsync(repoRoot, "origin", branch, freshnessCts.Token).ConfigureAwait(false);
            var behindCount = await _git.GetBehindCountAsync(repoRoot, "origin", branch, freshnessCts.Token).ConfigureAwait(false);
            if (behindCount > 0)
            {
                log.Report($"WARNING: Local checkout is {behindCount} commit(s) behind origin/{branch}; consider running 'git pull' before adopting. Proceeding with the current git tree.");
                return new PullFreshnessStatus(PullFreshnessKind.Behind, behindCount, branch);
            }

            return new PullFreshnessStatus(PullFreshnessKind.Fresh, 0, branch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            log.Report("Could not verify checkout freshness (fetch timed out — offline, VPN, or slow network). Proceeding with the local git tree as-is.");
            return new PullFreshnessStatus(PullFreshnessKind.Unverified, 0, string.Empty);
        }
        catch (GitCommandException ex)
        {
            _logger.LogWarning(ex, "Could not verify checkout freshness before pull-from-prod.");
            log.Report("Could not verify checkout freshness (fetch failed — offline, VPN, or auth). Proceeding with the local git tree as-is.");
            return new PullFreshnessStatus(PullFreshnessKind.Unverified, 0, string.Empty);
        }
    }

    private SyncDiffEntry StampArtifactAvailabilityAndDivergence(SyncDiffEntry entry, bool downloaded, string? repoBody) =>
        entry with
        {
            ArtifactDownloaded = downloaded,
            BodyDivergence = ComputeBodyDivergence(entry, downloaded, repoBody)
        };

    private BodyDivergenceStatus ComputeBodyDivergence(SyncDiffEntry entry, bool downloaded, string? repoBody)
    {
        if (entry.ProdRow is null)
        {
            return BodyDivergenceStatus.NotApplicable;
        }

        // Why: body-less prod row: prod's body_sha256 cannot be shown to match a local body,
        // so adopting would leave an incoherent index; surface + opt-in only.
        if (!downloaded || string.IsNullOrEmpty(repoBody) || entry.ProdRow.BodySha256 is null)
        {
            return BodyDivergenceStatus.Indeterminate;
        }

        try
        {
            var bodyText = File.ReadAllText(repoBody);
            var computedHash = ContentSiteIndexContentSignature.ComputeBodySha256(bodyText);
            return string.Equals(entry.ProdRow.BodySha256, computedHash, StringComparison.Ordinal)
                ? BodyDivergenceStatus.Clean
                : BodyDivergenceStatus.Confirmed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unreadable git body for {ArtifactPath}; stamping Indeterminate.", entry.ArtifactPath);
            return BodyDivergenceStatus.Indeterminate;
        }
    }

}

/// <summary>Data root + isolated pull-staging directory resolved for the Pull-from-Prod page.</summary>
/// <param name="DataRoot">Studio data root (parent of <c>ArtifactRoot</c>).</param>
public sealed record PullPaths(string DataRoot);

/// <summary>Freshness state of the local checkout relative to origin before pull-from-prod classification.</summary>
public enum PullFreshnessKind
{
    /// <summary>The checkout was fetched and is not behind origin.</summary>
    Fresh,

    /// <summary>The checkout was fetched and is behind origin by one or more commits.</summary>
    Behind,

    /// <summary>The checkout freshness could not be verified because fetch timed out or failed.</summary>
    Unverified
}

/// <summary>Freshness details surfaced to the operator after the bounded pre-check.</summary>
/// <param name="Kind">Freshness outcome.</param>
/// <param name="BehindCount">Number of commits behind origin when <see cref="Kind"/> is <see cref="PullFreshnessKind.Behind"/>.</param>
/// <param name="Branch">Current branch name when known.</param>
public sealed record PullFreshnessStatus(PullFreshnessKind Kind, int BehindCount, string Branch);

/// <summary>Result of pull-from-prod classification: diff entries plus checkout freshness status.</summary>
/// <param name="Entries">Differing entries classified against the local store.</param>
/// <param name="Freshness">Checkout freshness status from the pre-check stage.</param>
public sealed record PullClassifyResult(IReadOnlyList<SyncDiffEntry> Entries, PullFreshnessStatus Freshness);

/// <summary>One per-entry outcome of applying a Pull-from-Prod adopt resolution to the local store.</summary>
/// <param name="Title">Entry title (display).</param>
/// <param name="KeyType">Natural key type label.</param>
/// <param name="KeyValue">Natural key value.</param>
/// <param name="Success">True when the local row upsert + approval mirror succeeded.</param>
/// <param name="Action">Short action label ("Adopted" / "Failed").</param>
/// <param name="Note">Sanitized per-entry note (never carries a secret).</param>
public sealed record PullApplyRowResult(string Title, string KeyType, string KeyValue, bool Success, string Action, string Note);
