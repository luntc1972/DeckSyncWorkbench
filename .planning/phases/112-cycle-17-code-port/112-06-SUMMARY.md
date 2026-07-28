# Plan 112-06 Summary — Real-ArchidektOwnerClient DI test, headless boot smoke, Commit 2

**Wave:** 6 of 6 (final) · **Executed:** 2026-07-27 · **Branch:** `gsd/cycle20-personal-tools`
**Commit 2 SHA: `91fcfe23`** — `feat(112): port Cycle 17 creator-style Web services and DI`, author `luntc1972`, no `Co-Authored-By`. **Not pushed.**

## Task 1 — DI-resolution tests (D-13, D-20)

`DeckFlow.Web.Tests/Services/CreatorStyle/CreatorStyleDiRegistrationTests.cs`, **+398 / −0**
(201 → 599 lines). Every pre-existing test and fake retained.

Two tests added:

1. **`Resolve_RealArchidektOwnerClient_DoesNotThrow_WhenArchidektPipelineRegistered`** — builds a real
   `ServiceCollection`, calls `AddDeckFlowResiliencePipelines()` + `AddDeckFlowCreatorStyle(env)`, resolves
   `IArchidektOwnerClient` and asserts `IsType<ArchidektOwnerClient>`, then resolves
   `CreatorProfileDeckCrawler` from an `IServiceScope`. No fake substituted, no inline pipeline
   registration, no `catch (KeyNotFoundException`, no network call.
2. **`AddDeckFlowCreatorStyle_DescriptorDelta_ResolvesEveryCreatorStyleRegistration`** — snapshots
   `services.Count`, calls the extension, iterates the descriptors added after the snapshot and resolves
   each `ServiceType`, skipping open generics and `AddHttpClient` plumbing. Backed by the 14-name floor
   list so an emptied delta cannot pass vacuously. The five scoped services resolve from inside a scope.

Verified independently: `FakeArchidektOwnerClient` appears only at line 74 (the pre-existing test) and
line 292 (its class declaration) — **not** in either new test.

### Guard value proven, not asserted

The point of these tests is that they redden when the thing they guard disappears. Both proofs performed:

| proof | action | observed failure |
|---|---|---|
| A | commented out the `archidekt` registration in `ResiliencePipelineFactory.cs` | `KeyNotFoundException : Unable to find a generic resilience pipeline of 'RestResponse' associated with the key 'archidekt'.` |
| B | commented out `services.AddSingleton<ICreatorStyleSeedLoader, CreatorStyleSeedLoader>();` | `Creator-style descriptor delta is missing floor registrations: ICreatorStyleSeedLoader` |

Both files restored and re-run to green. Restoration verified by SHA-256 fingerprint match and by
`grep -cE '^\s*//\s*services\.Add'` = 0 — no commented-out registration left behind.

**Negative control: NOT included, deliberately.** Without `AddDeckFlowResiliencePipelines()` there is no
clean way to register only `ResiliencePipelineProvider<string>`, so the provider fails earlier on a
missing provider rather than on the missing `archidekt` key — it would have tested the wrong failure
mode. Reported rather than faked, as the plan required.

**Result:** `--filter "FullyQualifiedName~CreatorStyleDiRegistration"` → **3 passed / 0 failed** (was 1).
After two consecutive runs, `find` located no `content-kb.db`, `category-knowledge.db`, or
`creator-deck-cache.db` anywhere under the working tree or temp root — the parent/child temp layout and
`SqliteConnection.ClearAllPools()`-before-delete both work.

## Task 2 — headless boot smoke

Launched via `scripts/run-web-test.sh` (`DECKFLOW_DISABLE_AUTO_BROWSER=true`); no browser opened. Port
5173 probed with `curl` first and confirmed free — WSL's `ss` cannot see Windows-side listeners.

- **HTTP 200** after 2 polls.
- `DeckFlow.Web/logs/web-20260727.log` contains the fork-join line **once**:
  `19:01:51.334 [INF] Content site-index schema ensured and seed load completed during startup.`
  followed by `Now listening on: http://localhost:5173` and `Application started.` — the ordering proves
  the fork-join completed *during* startup rather than after.
- `grep -Ec 'KeyNotFoundException|Unable to resolve service for type|\[FTL\]|Fatal'` → **0**. This is the
  real-host confirmation that the `archidekt` pipeline resolves (T-112-01) and satisfies ROADMAP
  criterion 3.
- Teardown needed care: `pkill` cannot see a Windows process and a blanket `taskkill /IM dotnet.exe`
  would have killed other worktrees' builds, so the specific listener PID (84124) was resolved via
  `netstat.exe` and killed. Port re-probed → **HTTP 000, released**. No `logs/` file staged.

## Task 3 — gates, audit, Commit 2

| gate | result |
|---|---|
| staged set | **45 paths** = 38 allowlist + 6 Web M-files + 1 new extension; nothing from `DeckFlow.Core*`/`DeckFlow.CLI` |
| `scripts/format-check-changed.sh staged` | **exit 0** — the 6 reported violations are all off-hunk (lines 14/20/22/143-145; our added ranges are 32 and 49-67) |
| `dotnet build DeckFlow.sln --no-incremental` | **0 errors, 9 warnings** — all `CS8629`, identical to `112-BASELINE.md` |
| `DeckFlow.Core.Tests` | **1843 passed / 0 failed** |
| `DeckFlow.Web.Tests` | **2148 passed / 0 failed / 16 skipped** |
| unstaged production leftovers | 0 |
| pushed? | no — Commit 2 is unpushed |

### Path audit (D-08) — 5 of 6 clean

(a) 45 production paths in `main..HEAD`, every one on a published allowlist or the declared M-file set —
    **see the count deviation below**. (b) never-port names: **0**. (d) `PackageReference` in csproj diff:
    **0**. (e) `PacketSessionCache.cs` deleted lines: **0** (additive-only, D-18 honoured).
    (f) six D-02/D-16/D-10 hands-off files: **0** diff lines.

**(c) `tool.creator-style.enabled` in added content: 1 — expected, and a plan self-contradiction.** The
never-port bullet forbids the string "anywhere in added content" while D-18 explicitly places
`PromptMutatingCreatorStyleFlags` inside the allowlisted `CreatorStylePacketService.cs` (:121). Verified
inert: one `internal const string`, NOT registered in `FeatureFlagCatalog`, and no route / nav tile /
ToolRegistry / SeoPaths / sitemap entry exists. This is the third independent surfacing of the same
defect (blind verifier in wave 4, LEAD adjudication, now the audit). Ruled ACCEPT.

### The 119-path expectation was unreachable

The plan's audit (a) and its automated `<verify>` demand `main..HEAD` list exactly **119** paths — 74 from
Commit 1 plus 45 from Commit 2. Actual: **45**, and it could never have been 119, because **Commit 1
(`f23b7580`) is already in `main`** — waves 1-3 executed onto `main`, not onto a feature branch. Before
Commit 2 the count was 0, not 74. The audit's *intent* (no unlisted path sneaks in) is fully preserved at
45 and was executed in full. Same root cause as the `feat/personal-tools` correction in `147f82c9`.

### One flaky test run, honestly reported

The first full Web run reported **1 failure / 2147 passed**. I ran the Core and Web suites *concurrently*,
and both open SQLite connections through the same artifacts-path resolution while the new DI tests create
real temp databases — file-lock contention is the plausible cause. Two subsequent **solo** runs passed
2148/0/16. I did not capture the failing test's name before it disappeared, so this is an attribution on
evidence, not a diagnosis. If a Web failure recurs, do not assume it is this.

## Decisions recorded

- **README not updated at 112** — no user-visible behavior changes, because no creator-style route is
  wired until Phase 114. Stated explicitly rather than silently skipped.
- **Commit made without an interactive test gate**, under the plan's `<commit_rule_note>` waiver: AI
  commits to the feature branch, task 2's headless boot supplied real-host evidence beforehand, nothing
  reached `main`, and no push happened.

## Phase 112 status

All six plans executed. Cycle 17's Core engine (Commit 1, on `main`) and Web layer (Commit 2,
`91fcfe23`) are ported and resolve at startup. Remaining Cycle 20 phases: 113 shared-infra re-derivation,
114 port verification + admin surface, 115 real data.
