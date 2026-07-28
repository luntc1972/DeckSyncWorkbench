# Plan 112-04 Summary — Web file allowlist checkout, seed placeholders, Web hunks, archidekt pipeline

**Wave:** 4 of 6 · **Executed:** 2026-07-27 · **Branch:** `gsd/cycle20-personal-tools` (base `147f82c9`)
**Commit:** none — plan 06 owns Commit 2 per D-07's two-commit structure.

## Task 1 — path-allowlist checkout (38 paths)

One `git checkout plan/cycle-17-creator-style -- <38 explicit paths>` invocation, no globs, no
directory arguments. Result: **38 files changed, 7875 insertions(+), 0 deletions(-)**.

- 21 Web production: `Models/CreatorStyleRequest.cs`; `Services/Content/{I,}CreatorStyleSeedLoader.cs`;
  `Services/CreatorStyle/` × 10 (`ArchidektOwnerClient`, `ArchidektOwnerUrl`, `CreatorDeckCategoryResolver`,
  `CreatorDeckExemplarSelector`, `CreatorProfileDeckCrawler`, `CreatorStyleDeckAnalysis`,
  `CreatorStylePacketService`, `CreatorWhitelistPoolBuilder`, `MeasuredStyleProfileBuilder`,
  `SubmittedDeckStatsBuilder`); `Services/Scryfall/` × 7 (`CachedNameResolution`, `CardGroundingGuard`,
  `ScryfallBatching`, `ScryfallCardNameGrounder`, `ScryfallCollectionResolver`, `ScryfallErrorResponse`,
  `ScryfallLimits`); `Services/SeedJson.cs`.
- 15 Web tests: `CreatorStyleSeedLoaderTests`, `ProgramStartupTests`,
  `Services/CreatorStyle/` × 10, `Services/Scryfall/` × 3.
- 2 seed placeholders: `content-kb/seed/creator-style-profiles.json`, `content-kb/seed/creator-deck-cache.json`.

**Pre-flight before the checkout** (this is what made it safe): 0 of the 38 paths existed on HEAD, so the
D-09 / T-112-11 wholesale-copy trap could not fire; 0 were missing on the source branch; and both seed
files were already exactly `[]` on the branch, so no scrubbing was needed.

Acceptance criteria 8/8 PASS — missing=0; both seeds `[]` with `grep -Ec '[A-Za-z]'`=0; never-port
leaks=0; the six D-02/D-16 hands-off files show 0 diff lines; D-12 strings in `git status`=0;
`DeckFlow.Core*` entries=0; no csproj modified.

## Task 2 — four Web M-file hunks (additive)

| file | +/− | what was added |
|---|---|---|
| `Services/Scryfall/ScryfallDtos.cs` | 3 / 1 | `Legalities` as the LAST optional record parameter, `[property: JsonPropertyName("legalities")]` on its own line (D-16) |
| `Services/Scryfall/ScryfallCardResolver.cs` | 20 / 0 | `ExecuteNamedFuzzyAsync` — interface default that throws `NotSupportedException`, plus concrete override routing through the existing execute delegate and calling `ScryfallThrottle.ThrowIfUpstreamUnavailable` (D-16) |
| `Services/PacketSessionCache.cs` | 29 / 0 | `CreatorStyleCacheInputs` record + one `PacketSizeEstimator.EstimateSizeBytes` overload (D-18, the single ratified exception to D-12) |
| `Services/Persistence/DeckFlowDatabaseConnectionFactory.cs` | 7 / 0 | `CreateCreatorDeckCacheConnection`; `CreateManabaseBaselineConnection` and `CreateLocalContentKbConnection` both verified still present |

The one deleted line is in `ScryfallDtos.cs` and is terminator-only: `Rarity = null);` → `Rarity = null,`.
Appending a trailing positional record parameter in C# cannot be done without re-terminating the previous
one, so the plan's literal "0 deleted lines" criterion is unsatisfiable for A1. No member was lost.

## Task 3 — archidekt resilience pipeline (D-17)

`Services/Http/ResiliencePipelineFactory.cs`, **20 / 0**. Pipeline key `"archidekt"` registered alongside
the existing five (`banlist`, `spellbook`, `tagger`, `tagger-post`, `scryfall`, all verified surviving),
delegating to a new private `BuildArchidekt`: 30-second total timeout named `archidekt-total`, then retry
with `MaxRetryAttempts` 2, exponential backoff, jitter, handling status ≥ 500 plus the file's existing
`IsTransientException` — reused, not duplicated (1 declaration, 9 references).

This closes T-112-01 and unblocks ROADMAP criterion 3: Polly 8.6.6's `GetPipeline<T>` throws
`KeyNotFoundException` on an unregistered key, and `ArchidektOwnerClient` resolves it in its
**constructor**, so DI resolution would have failed outright without this.
`Extensions/HttpClientServiceCollectionExtensions.cs` untouched (0 diff lines).

## Build result

`dotnet build DeckFlow.Web/DeckFlow.Web.csproj` → **Build succeeded, 0 Warning(s), 0 Error(s)**, exit 0.

`DeckFlow.Web.Tests` does NOT build at this wave (`CS0117` on `AwaitStartupSeedTasksAsync`) — expected and
stated in the plan's objective; plan 05 adds the helper.

## Gates

Write-set fence empty (nothing outside the 38 + 5). Staged set == the 38 allowlist exactly, both `comm`
sides empty; the 5 M-files unstaged. EOL: 0 CR in all 43 files, and `git diff --stat` identical to
`git diff --ignore-all-space --stat` (7954+/1−) proving zero whitespace churn.
`scripts/format-check-changed.sh staged` exit 0. Blind verifier (Claude, cross-family vs Codex):
**PASS_WITH_NOTES**, 9/9 substantive.

## Open plan defects (code is correct; the plan's literal criteria are not)

1. "additive-only, 0 deleted lines" — unsatisfiable for task 2 A1, see above.
2. "no line adding `new HttpClient(`" — 2 occurrences, both in the ported test
   `Services/CreatorStyle/ArchidektOwnerClientTests.cs`; **0** in production code. CLAUDE.md's
   anti-pattern targets services, not test doubles.
3. The never-port list forbids the string `tool.creator-style.enabled` "anywhere in added content", but
   D-18 explicitly places `PromptMutatingCreatorStyleFlags` inside the allowlisted
   `CreatorStylePacketService.cs` (:121). The plan contradicts itself. Verified inert — one
   `internal const string`, NOT registered in `FeatureFlagCatalog`, and FeatureFlagCatalog/Store/
   ToolRegistry/SeoPaths/sitemap all show 0 modifications.

Also: the plan says "do not stage", but its own mandated task-1 mechanism
(`git checkout <ref> -- <paths>`) stages by design. HEAD untouched; wave 6 owns the commit.
