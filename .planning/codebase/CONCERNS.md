# Codebase Concerns

**Analysis Date:** 2026-04-26

---

## Tech Debt

**Transient HttpClient instantiation in three services:**
- Issue: `new HttpClient()` / `new HttpClientHandler()` created per-call instead of using `IHttpClientFactory`. Each call opens a new TCP socket; under load this causes socket exhaustion and delayed TIME_WAIT recycling.
- Files:
  - `DeckFlow.Web/Services/CommanderBanListService.cs` (line 74) — in `FetchPageAsync`
  - `DeckFlow.Web/Services/CommanderSpellbookService.cs` (line 273)
  - `DeckFlow.Web/Services/ScryfallTaggerService.cs` (lines 99–100, 137, 146) — two separate per-call handlers in `FetchCsrfTokenAsync` and `QueryTaggerGraphQlAsync`
- Impact: At low request volumes, no visible problem. Under concurrent ChatGPT-packet builds that exercise all three services simultaneously, sockets pool is depleted, causing 503s or long waits.
- Fix approach: Register named `HttpClient` entries via `builder.Services.AddHttpClient(...)` in `Program.cs`; inject `IHttpClientFactory` into each service. `ScryfallTaggerService` needs the cookie-carrying handler refactored into a typed client.

**`CategoryKnowledgeRepository` has no interface:**
- Issue: The 703-line `DeckFlow.Core/Knowledge/CategoryKnowledgeRepository.cs` is a concrete `sealed class` with no abstraction. `CategoryKnowledgeStore` (web layer), `ArchidektDeckCacheSession`, `DeckCategoryCacheWriter`, and `DeckFlow.CLI/CommandRunners.cs` all depend on the concrete type directly.
- Impact: Cannot be swapped for an in-memory fake in unit tests; any schema-level changes force touches in every consumer.
- Fix approach: Extract `ICategoryKnowledgeRepository` interface covering the public async methods; update callers to depend on the interface.

**`ChatGptDeckPacketService` god-class (1855 lines, 30+ private methods):**
- Issue: `DeckFlow.Web/Services/ChatGptDeckPacketService.cs` handles deck loading, Scryfall lookups, prompt construction, commander validation, combo reference, set-upgrade packet generation, and artifact persistence in one class.
- Impact: High cyclomatic complexity; test file (`ChatGptDeckPacketServiceTests.cs`, 1626 lines) mirrors the sprawl, making new-feature placement ambiguous and regression risk high.
- Fix approach: Extract `PromptBuilder`, `DeckLoader`, and `ArtifactPersistence` collaborators to separate files/classes. The `BuildAnalysisPrompt` method alone is ~150 lines.

**Two-color CSS themes are stubs:**
- Issue: Ten two-color guild themes exist in `DeckFlow.Web/wwwroot/css/` but most are incomplete skeletons. Gruul (`site-gruul.css`, 35 lines), Izzet (`site-izzet.css`, 35 lines), and Orzhov (`site-orzhov.css`, 35 lines) have only variable overrides with no full layout rules. Compare to full three-color themes (~1200–1340 lines). Dimir, Golgari, Simic have 96–103 lines. Rakdos (223), Azorius (196), Boros (112), Selesnya (111) are partial.
- Impact: Selecting one of these themes produces a broken or near-default visual appearance. Misleads users browsing the theme picker.
- Fix approach: Either build them out to feature parity with `site-common.css` cascade + full variable set, or remove them from the picker until complete.

**`DeckSyncSupport` helper class has no test file:**
- Issue: `DeckFlow.Web/Services/DeckSyncSupport.cs` is used by both `DeckController` and `DeckSyncApiController` (source/target system resolution, direction mapping) but has no corresponding test file.
- Impact: Direction-mapping regressions go undetected; any change to `GetSourceSystem`/`GetTargetSystem` carries silent risk.
- Fix: Add `DeckSyncSupportTests.cs` covering each direction permutation.

---

## Security Considerations

**`@Html.Raw` rendering in Help topic view:**
- Risk: `DeckFlow.Web/Views/Help/Topic.cshtml` (line 13) renders pre-rendered HTML from `HelpContentService` via `@Html.Raw(Model.HtmlContent)`. The Markdig pipeline uses `UseAdvancedExtensions()` which includes raw HTML passthrough. If any `.md` file in `DeckFlow.Web/Help/` is ever edited by an attacker or contains injected content, arbitrary HTML/script executes in the browser.
- Files: `DeckFlow.Web/Services/HelpContentService.cs`, `DeckFlow.Web/Views/Help/Topic.cshtml`
- Current mitigation: Help files are server-side static assets, not user-supplied. CSP header (`script-src 'self'`) provides partial defense but `unsafe-inline` is allowed for styles.
- Recommendation: Configure Markdig to use `DisableHtml()` so raw HTML in markdown is stripped. If inline HTML is intentionally authored, use a strict allowlist and consider adding a dedicated nonce to CSP `script-src`.

**`BasicAuthMiddleware` reads credentials from environment variables per-request:**
- Risk: `DeckFlow.Web/Infrastructure/BasicAuthMiddleware.cs` calls `Environment.GetEnvironmentVariable` on every request (lines 20–21) rather than reading once at startup. If the environment is somehow modified at runtime, the change takes effect immediately without restart.
- Files: `DeckFlow.Web/Infrastructure/BasicAuthMiddleware.cs`
- Current mitigation: `FixedTimeEquals` is correctly used; timing-safe comparison prevents timing attacks.
- Recommendation: Read and cache `user`/`password` at middleware construction time (passed in as constructor args or `IOptions<AdminCredentials>`). Fail at startup if unconfigured rather than returning 503 per-request.

**Rate limiter scope is narrow:**
- Risk: The ASP.NET Core `AddRateLimiter` policy (`feedback-submit`, 5 req/hr per IP) covers only the feedback submission endpoint. The Scryfall-proxying API endpoints (`/api/cards/search`, `/api/suggestions/*`) have no inbound rate limit — a client could hammer them, triggering outbound Scryfall 429s that affect all users via the shared `ScryfallThrottle` gate.
- Files: `DeckFlow.Web/Program.cs` (lines 73–89)
- Recommendation: Add a `sliding-window` or `token-bucket` policy scoped to the Scryfall-backed API routes.

---

## Performance Bottlenecks

**Scryfall Tagger: two sequential HTTP round-trips per card lookup:**
- Problem: `ScryfallTaggerService.LookupOracleTagsAsync` makes (1) a Scryfall REST call to resolve set+number, (2) an HTML page fetch to grab the CSRF token, then (3) a GraphQL POST. Three sequential requests per card with no caching.
- Files: `DeckFlow.Web/Services/ScryfallTaggerService.cs`
- Cause: CSRF token is session-scoped and not persisted between requests. No `IMemoryCache` usage unlike `CommanderBanListService`.
- Improvement: Cache the CSRF token + cookies for a short window (e.g. 5 minutes). Cache oracle tag results per card name (tags change rarely).

**`HelpContentService` loads all markdown files on first access via `Lazy<T>`:**
- Problem: On cold start, all `.md` files in `DeckFlow.Web/Help/` are read and Markdig-rendered synchronously within the `Lazy<T>` initializer on the first request thread. If many help files exist, first-request latency is noticeable.
- Files: `DeckFlow.Web/Services/HelpContentService.cs` (lines 17, 40–60)
- Cause: `Lazy<T>` with default thread-safety mode blocks concurrent callers during initialization.
- Improvement: Warm up `HelpContentService` during `app.MapGet` startup phase or use `IHostedService` startup preload.

**`CategoryKnowledgeRepository` is synchronous SQLite (703 lines):**
- Problem: `DeckFlow.Core/Knowledge/CategoryKnowledgeRepository.cs` mixes async schema setup with synchronous query methods, creating potential ASP.NET thread-pool starvation under concurrent category suggestion requests.
- Files: `DeckFlow.Core/Knowledge/CategoryKnowledgeRepository.cs`
- Improvement: Audit all query methods; replace `ExecuteReader` with `ExecuteReaderAsync` throughout.

---

## Fragile Areas

**Scryfall Tagger scrape dependency:**
- Files: `DeckFlow.Web/Services/ScryfallTaggerService.cs`, `DeckFlow.Web/Services/ScryfallTaggerParsers.cs`
- Why fragile: The CSRF token extraction (`ScryfallTaggerParsers.TryExtractCsrfToken`) uses HTML regex against `tagger.scryfall.com` page structure. Any Scryfall Tagger front-end deploy silently breaks the entire category suggestion Tagger mode.
- Safe modification: Any change to `TaggerQuery` or the CSRF extraction regex must be validated against a live Tagger page snapshot. `ScryfallTaggerParsersTests.cs` covers the parser but uses a static HTML fixture — it will not detect upstream HTML changes.
- Test coverage: Parser is tested; the live HTTP flow is not integration-tested.

**Commander Spellbook API contract:**
- Files: `DeckFlow.Web/Services/CommanderSpellbookService.cs`
- Why fragile: `new HttpClient()` (line 273) fetches from the Commander Spellbook v2 API. The JSON deserialization shape is hardcoded to the current API response structure. No versioned endpoint is pinned. Silent breakage occurs if the API schema changes.
- Safe modification: Add a smoke-test or integration test that validates the top-level JSON shape. Consider pinning to a versioned API URL.

**Commander ban list HTML scraper:**
- Files: `DeckFlow.Web/Services/CommanderBanListService.cs`
- Why fragile: `ParseBannedCards` uses a `<summary>` regex against `mtgcommander.net` HTML. If the site's markup changes (e.g., switching from `<details>`/`<summary>` to `<ul>`), the regex returns zero results and the ban list silently shows as empty.
- Safe modification: Add a health check or assertion that the parsed list is non-empty before caching. Log a warning if count drops below a threshold (the ban list has historically ~10–15 cards).

**`deck-sync.ts` (2426 lines) is the largest single source file:**
- Files: `DeckFlow.Web/wwwroot/ts/deck-sync.ts`
- Why fragile: A single TypeScript file handles UI state management across the Sync, SuggestCategories, ChatGPT Packets, and related tabs. Coupling between feature areas inside one file makes targeted changes high-risk.
- Safe modification: Any edit must manually trace all event listeners and shared state. No TypeScript unit tests exist for this file.
- Test coverage: None.

---

## Test Coverage Gaps

**`ScryfallTaggerService` — HTTP flow untested:**
- What's not tested: The live `LookupOracleTagsAsync` call chain (Scryfall REST → CSRF fetch → GraphQL POST).
- Files: `DeckFlow.Web/Services/ScryfallTaggerService.cs`
- Risk: CSRF fetch or GraphQL response parsing regressions go undetected until production.
- Priority: Medium

**`CommanderSpellbookService` — no test file:**
- What's not tested: Combo lookup, response deserialization, caching behavior, error paths.
- Files: `DeckFlow.Web/Services/CommanderSpellbookService.cs`
- Risk: Spellbook combo references silently disappear from ChatGPT packets without failing CI.
- Priority: Medium

**`ChatGptPacketArtifactStore` — no test file:**
- What's not tested: Artifact directory creation, JSON serialization to disk, path resolution.
- Files: `DeckFlow.Web/Services/ChatGptPacketArtifactStore.cs`
- Risk: Artifact save failures are silent; users see no saved output directory.
- Priority: Low

**`DeckSyncSupport` — no test file:**
- What's not tested: Direction-to-system mapping for all sync direction enum values.
- Files: `DeckFlow.Web/Services/DeckSyncSupport.cs`
- Risk: New sync directions added without updating the mapping produce incorrect source/target resolution.
- Priority: High

**All TypeScript modules — no unit tests:**
- What's not tested: `deck-sync.ts`, `category-suggestions.ts`, `card-lookup.ts`, `df-select.ts`, `df-typeahead.ts`
- Files: `DeckFlow.Web/wwwroot/ts/`
- Risk: UI regressions are catch-only-by-manual-testing. Especially high risk in `deck-sync.ts` (2426 lines) and `df-select.ts` (845 lines, ARIA combobox logic).
- Priority: Medium (vitest or jest setup would cover the most-critical df-select/df-typeahead modules)

---

## Dependencies at Risk

**`CommanderBanListService` — scraping `mtgcommander.net`:**
- Risk: Not an official API. Any site redesign breaks the regex parser and silently empties the ban list.
- Impact: Ban-check on ChatGPT prompts stops working; banned cards pass through as valid recommendations.
- Migration plan: Monitor for an official mtgcommander.net JSON endpoint or switch to Scryfall legality data (`legalities.commander == "banned"` field already present in Scryfall card objects).

**`ScryfallTaggerService` — undocumented GraphQL scrape:**
- Risk: `tagger.scryfall.com/graphql` is an internal API with no SLA. CSRF token approach is a scrape workaround, not an official integration.
- Impact: Oracle tag feature silently breaks when Scryfall changes its auth model.
- Migration plan: Watch Scryfall developer Discord for any official Tagger API announcement. Build a fallback path that returns an empty tag list gracefully (already partially done).

---

*Concerns audit: 2026-04-26*
