# Plan 112-05 Summary — AddDeckFlowCreatorStyle DI extension and the two Program.cs edits

**Wave:** 5 of 6 · **Executed:** 2026-07-27 · **Branch:** `gsd/cycle20-personal-tools` (base `147f82c9`)
**Commit:** none — plan 06 owns Commit 2.

## Task 1 — `DeckFlow.Web/Extensions/CreatorStyleServiceCollectionExtensions.cs` (NEW, 66 lines)

File-scoped `namespace DeckFlow.Web.Extensions;`, public static class, one extension method
`AddDeckFlowCreatorStyle(this IServiceCollection services, IWebHostEnvironment environment)` with
`ArgumentNullException.ThrowIfNull` on both parameters, returning `services` — mirroring the house shape
of `CutLabServiceCollectionExtensions` / `ScryfallServiceCollectionExtensions`.

Registrations, in plan order:

| # | registration | lifetime |
|---|---|---|
| 1 | named `IHttpClientFactory` client `archidekt-owner` (base `https://archidekt.com/`, `DeckFlow/1.0` UA, JSON Accept) | named client |
| 2 | `ICardNameGrounder` → `ScryfallCardNameGrounder`; `ICardGroundingGuard` → `CardGroundingGuard` | singleton |
| 3 | `ICreatorDeckCacheStore` → `CreatorDeckCacheStore` over `CreateCreatorDeckCacheConnection`; `ICreatorProfileSourceStore` → `CreatorProfileSourceStore`; `CategoryKnowledgeRepository` over `CreateCategoryKnowledgeConnection`; `ICreatorStyleProfileStore` → `CreatorStyleProfileStore` over `CreateLocalContentKbConnection` | singleton |
| 4 | `CreatorWhitelistPoolBuilder` | singleton |
| 5 | `ICreatorStyleSeedLoader` → `CreatorStyleSeedLoader` | singleton |
| 6 | `IArchidektOwnerClient` → `ArchidektOwnerClient` | singleton |
| 7 | `CreatorProfileDeckCrawler`; `CreatorDeckCategoryResolver`; `MeasuredStyleProfileBuilder`; `ISubmittedDeckStatsBuilder` → `SubmittedDeckStatsBuilder`; `ICreatorStylePacketService` → `CreatorStylePacketService` | scoped |

All 14 identifiers present; lifetimes independently re-read against the plan by the blind verifier
(a scoped service captured by a singleton compiles and passes tests but corrupts state at runtime, so
this was checked registration by registration, not by grep alone).

`// Why:` comment recorded on the `ICreatorStyleProfileStore` registration: creator-style profiles bind to
the local-only content-kb database because production never crawls, it only reads git-shipped seeds.

Per D-17 the `archidekt-owner` client lives HERE, not in the shared
`HttpClientServiceCollectionExtensions.cs` — that file shows 0 diff lines, as do
`PacketServiceCollectionExtensions.cs` and `ScryfallServiceCollectionExtensions.cs`. `new HttpClient(`
count in the new file: 0. Flags/registry/SEO/route tokens: 0.

**Pre-flight that de-risked this task:** confirmed the target file did not already exist; confirmed all of
`CreateCreatorDeckCacheConnection`, `CreateCategoryKnowledgeConnection`, `CreateLocalContentKbConnection`
exist on the extended factory; and confirmed `CategoryKnowledgeRepository` was registered nowhere, so the
new singleton could not shadow an existing one.

## Task 2 — `DeckFlow.Web/Program.cs`, **42 added / 1 deleted**

Bounded well inside the plan's limits (≤ 2 deleted, < 50 added). Exactly two edits:

- **(a)** one line, `builder.Services.AddDeckFlowCreatorStyle(builder.Environment);`, directly after
  `builder.Services.AddDeckFlowScryfallServices();`.
- **(b)** the D-19 startup seed fork-join at ~:277-289. `IContentSiteIndexStore.EnsureSchemaAsync()` is
  still awaited FIRST (ordering is load-bearing — schema must exist before seeds load), then both
  `IContentKbSeedLoader.LoadIfPresentAsync()` and `ICreatorStyleSeedLoader.LoadIfPresentAsync()` start as
  tasks and are joined through the new `AwaitStartupSeedTasksAsync`. The pre-existing log line
  "Content site-index schema ensured and seed load completed during startup." survives verbatim, and the
  body-hash and seed_managed backfills that follow are unreordered.

Two static helpers added beside `DeriveAdminPartitionKey`: `AwaitStartupSeedTasksAsync` and
`LogFaultedSeedTask`, using the structured template `"Startup seed task {SeedTask} faulted."` — named
placeholder, no string interpolation.

`git diff HEAD -- Program.cs` contains 0 occurrences of `MapControllerRoute|FeatureFlag|ToolRegistry|
SeoPaths|tool.creator-style`. No route, flag, nav tile, admin section, or help topic was added; the admin
surface remains Phase 114 (T-112-13).

### The helper contract came from the test, not the prose

`DeckFlow.Web.Tests/ProgramStartupTests.cs` was ported in wave 4 and could not compile until this wave —
it is an executable specification, and it is stricter than the plan's prose. Four constraints it imposes
that "log both faults and rethrow" would not surface:

1. `Assert.Same(contentException, exception)` — the escaping exception must be the content task's own
   instance. `await Task.WhenAll(t1, t2)` rethrows the first-listed task's unwrapped exception, which
   satisfies this; an `AggregateException`, `.Wait()`, or `.Result` would fail it.
2. `Assert.Collection` asserts the entry count **exactly** — a third summary log line would fail.
3. Order-sensitive: contentKb must log first, creatorStyle second, regardless of which faulted first in
   wall-clock time. Both `LogFaultedSeedTask` calls therefore run unconditionally in fixed order.
4. `FakeLogger<T>` records `formatter(state, exception)` — the *rendered* message — so the literal
   `"contentKbSeedTask"` must survive formatting. It is passed as the placeholder's argument value, which
   keeps structured logging intact per CLAUDE.md.

This satisfies T-112-12: a faulted seed load logs a cause from EACH task before rethrowing, rather than
the first failure hiding the second during incident triage.

## Build and test result vs baseline

`dotnet build DeckFlow.sln` (forced `--no-incremental /t:Rebuild`) →
**Build succeeded. 9 Warning(s), 0 Error(s).**

All 9 are `CS8629` in `DeckFlow.Core.Tests/Manabase/ManabaseBaselineWeightingTests.cs` at lines
52, 54, 56, 69, 123, 125, 137, 139, 141 — byte-identical to `112-BASELINE.md`. **No new warning ID.**
`DeckFlow.Web.Tests` builds again, closing the expected wave-4 `CS0117`.

`dotnet test DeckFlow.Web.Tests --filter "FullyQualifiedName~ProgramStartup"` →
**Passed! Failed: 0, Passed: 1.** `CreatorStyleDiRegistrationTests` also passes (1/1) — the real
DI-resolution test relevant to D-13, resolving `ArchidektOwnerClient` for real rather than through a fake.

## Gates

Write-set fence empty. Program.cs diff bounded (42/1). Three shared extensions 0 diff. EOL: 0 CR in both
touched files. Blind verifier (Claude, cross-family vs Codex): **PASS_WITH_NOTES**, 11/11 areas PASS,
with the only finding being that this SUMMARY did not yet exist — now written.

Nothing staged from this wave, nothing committed, HEAD still `147f82c9`.
