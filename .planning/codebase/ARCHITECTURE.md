<!-- refreshed: 2026-04-26 -->
# Architecture

**Analysis Date:** 2026-04-26

## System Overview

```text
┌─────────────────────────────────────────────────────────────────────────┐
│                         DeckFlow.Web (ASP.NET Core MVC)                  │
│                                                                           │
│  MVC Controllers          API Controllers         Background Services    │
│  `Controllers/*.cs`       `Controllers/Api/*.cs`  `Services/Archidekt*`  │
│  DeckController           DeckSyncApiController   ArchidektCacheJobSvc   │
│  CommanderController      SuggestionsApiCtrlr     (IHostedService)       │
│  FeedbackController       ArchidektCacheJobsCtrl                         │
│                                                                           │
│  Web Services Layer (`Services/`)                                         │
│  IDeckSyncService · ICategorySuggestionService · IChatGptDeckPacketSvc   │
│  ICardLookupService · ICommanderCategoryService · IDeckConvertService    │
│  ICategoryKnowledgeStore · IScryfallSetService · IMechanicLookupService  │
└───────────────────────────────┬─────────────────────────────────────────┘
                                 │  references
                                 ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                          DeckFlow.Core (.NET class library)              │
│                                                                           │
│  Loading/          Parsing/         Diffing/         Integration/        │
│  DeckEntryLoader   MoxfieldParser   DiffEngine       IMoxfieldDeckImp.   │
│  DeckLoadRequest   ArchidektParser  DeckDiff         IArchidektDeckImp.  │
│                                                                           │
│  Filtering/        Exporting/       Reporting/       Knowledge/          │
│  DeckEntryFilter   DeltaExporter    CategoryCardRpt  CategoryKnowledge   │
│  CategoryFilter    FullImportExp.   ReconciliationRpt Repository (SQLite)│
│                    MoxfieldTextExp                                        │
│                                                                           │
│  Normalization/    Models/                                               │
│  CardNormalizer    DeckEntry (record) · DeckDiff · LoadedDecks           │
│  CategoryNorm.     MatchMode · SyncDirection · PrintingConflict          │
└───────────────────────────────┬─────────────────────────────────────────┘
                                 │  direct HTTP
                                 ▼
┌─────────────────────────────────────────────────────────────────────────┐
│  External APIs                                                           │
│  Moxfield API · Archidekt API · Scryfall API · EDHREC · Commander       │
│  Spellbook · EdhTop16 · WotC Mechanics                                  │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│  DeckFlow.CLI (.NET console app)                                         │
│  `DeckFlow.CLI/Program.cs` — System.CommandLine subcommands             │
│  Depends directly on DeckFlow.Core (no DeckFlow.Web)                   │
└─────────────────────────────────────────────────────────────────────────┘
```

## Component Responsibilities

| Component | Responsibility | Location |
|-----------|----------------|----------|
| `DeckController` | Serves all MVC page actions (sync, convert, lookup, ChatGPT workflows) | `DeckFlow.Web/Controllers/DeckController.cs` |
| `CommanderController` | Commander category lookup page | `DeckFlow.Web/Controllers/CommanderController.cs` |
| `FeedbackController` | User feedback form + rate-limited submission | `DeckFlow.Web/Controllers/FeedbackController.cs` |
| `DeckSyncApiController` | JSON API for deck diff/merge, same-origin guarded | `DeckFlow.Web/Controllers/Api/DeckSyncApiController.cs` |
| `SuggestionsApiController` | JSON API for category suggestions | `DeckFlow.Web/Controllers/Api/SuggestionsApiController.cs` |
| `ArchidektCacheJobsController` | Admin API for triggering knowledge cache runs | `DeckFlow.Web/Controllers/Admin/ArchidektCacheJobsController.cs` |
| `AdminFeedbackController` | Admin view of feedback submissions | `DeckFlow.Web/Controllers/Admin/AdminFeedbackController.cs` |
| `IDeckSyncService` | Orchestrates deck loading + diff | `DeckFlow.Web/Services/DeckSyncService.cs` |
| `ICategorySuggestionService` | Multi-source category suggestion (store + EDHREC + Tagger) | `DeckFlow.Web/Services/CategorySuggestionService.cs` |
| `ICategoryKnowledgeStore` | In-process façade over SQLite knowledge DB | `DeckFlow.Web/Services/CategoryKnowledgeStore.cs` |
| `ArchidektCacheJobService` | `IHostedService` + `IArchidektCacheJobService`; queues and runs crawl jobs | `DeckFlow.Web/Services/ArchidektCacheJobService.cs` |
| `DiffEngine` | Core card-set comparison, loose/strict match modes | `DeckFlow.Core/Diffing/DiffEngine.cs` |
| `DeckEntryLoader` | Routes load requests to parser or API importer | `DeckFlow.Core/Loading/DeckEntryLoader.cs` |
| `CategoryKnowledgeRepository` | SQLite access for card-category observations | `DeckFlow.Core/Knowledge/CategoryKnowledgeRepository.cs` |
| `DeckFlow.CLI/Program.cs` | System.CommandLine subcommands wrapping Core operations | `DeckFlow.CLI/Program.cs` |

## Pattern Overview

**Overall:** Clean layered architecture — Core domain library + Web presentation layer + CLI consumer. No circular dependencies between projects.

**Key Characteristics:**
- `DeckFlow.Core` has zero dependency on ASP.NET — pure .NET class library
- `DeckFlow.Web` references `DeckFlow.Core`; `DeckFlow.CLI` references `DeckFlow.Core`
- Web services are interface-driven; all registered via DI in `Program.cs`
- Controllers are thin — delegate to injected service interfaces
- `record` types used for immutable value objects throughout Core

## Layers

**Presentation Layer (DeckFlow.Web/Controllers/):**
- Purpose: Handle HTTP, validate input, delegate to services, return Views or JSON
- Location: `DeckFlow.Web/Controllers/`
- Contains: MVC controllers (`Controller` base), API controllers (`ControllerBase`)
- Depends on: Web Services layer, Core models
- Used by: Browser, external API consumers

**Web Services Layer (DeckFlow.Web/Services/):**
- Purpose: Orchestrate Core operations, external HTTP calls (Scryfall, Moxfield, Archidekt), and knowledge store access
- Location: `DeckFlow.Web/Services/`
- Contains: Service classes + interfaces (colocated in same file)
- Depends on: `DeckFlow.Core`, external REST APIs via RestSharp
- Used by: Controllers

**Domain Layer (DeckFlow.Core/):**
- Purpose: All business logic — parsing, diffing, filtering, exporting, reporting, normalization
- Location: `DeckFlow.Core/`
- Contains: Namespace-per-concern subdirectories
- Depends on: Nothing (no framework dependencies)
- Used by: `DeckFlow.Web`, `DeckFlow.CLI`

**Infrastructure Layer (DeckFlow.Web/Infrastructure/, DeckFlow.Web/Security/):**
- Purpose: Middleware, security headers, same-origin validation
- Location: `DeckFlow.Web/Infrastructure/`, `DeckFlow.Web/Security/`
- Contains: Extension methods for middleware, `BasicAuthMiddleware`, `SameOriginRequestValidator`
- Depends on: ASP.NET Core abstractions
- Used by: `Program.cs` pipeline

**Data Layer (DeckFlow.Core/Knowledge/):**
- Purpose: SQLite persistence of card-category observations harvested from Archidekt
- Location: `DeckFlow.Core/Knowledge/CategoryKnowledgeRepository.cs`
- Contains: Raw ADO.NET via `Microsoft.Data.Sqlite`; no ORM
- Depends on: `Microsoft.Data.Sqlite`
- Used by: `ICategoryKnowledgeStore` in `DeckFlow.Web/Services/`

## Data Flow

### Deck Diff (Primary Path)

1. Browser POSTs JSON to `POST /api/deck/diff` (`DeckFlow.Web/Controllers/Api/DeckSyncApiController.cs`)
2. `SameOriginRequestValidator.IsValid()` gate (`DeckFlow.Web/Security/SameOriginRequestValidator.cs`)
3. `IDeckSyncService.CompareDecksAsync()` (`DeckFlow.Web/Services/DeckSyncService.cs`)
4. `IDeckEntryLoader.LoadAsync()` resolves each deck: text → `MoxfieldParser`/`ArchidektParser` or URL → `IMoxfieldDeckImporter`/`IArchidektDeckImporter` (`DeckFlow.Core/Loading/DeckEntryLoader.cs`)
5. `DiffEngine.Compare()` builds `DeckDiff` with toAdd / countMismatch / printingConflicts lists (`DeckFlow.Core/Diffing/DiffEngine.cs`)
6. Result serialized to `DeckSyncApiResponse` and returned as JSON

### Category Suggestion Flow

1. Browser requests suggestions via `POST /api/suggestions/...` (`DeckFlow.Web/Controllers/Api/SuggestionsApiController.cs`)
2. `ICategorySuggestionService.SuggestAsync()` queries: `ICategoryKnowledgeStore` → EDHREC fallback → Scryfall Tagger fallback (`DeckFlow.Web/Services/CategorySuggestionService.cs`)
3. `ICategoryKnowledgeStore` reads SQLite DB via `CategoryKnowledgeRepository` (`DeckFlow.Core/Knowledge/CategoryKnowledgeRepository.cs`)
4. Results ranked and returned as JSON

### Archidekt Cache Crawl Flow

1. Admin triggers via `POST /Admin/archidekt-cache-jobs` → `ArchidektCacheJobsController` (`DeckFlow.Web/Controllers/Admin/ArchidektCacheJobsController.cs`)
2. `IArchidektCacheJobService.EnqueueAsync()` pushes job to unbounded `Channel<T>` (`DeckFlow.Web/Services/ArchidektCacheJobService.cs`)
3. Background loop in `BackgroundService.ExecuteAsync()` dequeues, runs `IArchidektDeckImporter` fetches, writes to `CategoryKnowledgeRepository`
4. Status polled via `GET /Admin/archidekt-cache-jobs/{id}`

**State Management:**
- Per-request state: none (stateless HTTP)
- Singleton singletons: `ICategoryKnowledgeStore`, `ICommanderBanListService`, `IScryfallSetService`, all Scryfall-backed services
- Scoped: `ICategorySuggestionService`, `IChatGpt*Service`, `IDeckSyncService`, `IDeckConvertService`, `IDeckEntryLoader`
- Persistent: SQLite DB for knowledge store (file on disk, path from config)

## Key Abstractions

**`DeckEntry` (record):**
- Purpose: Canonical card representation across all parsers, importers, and diff operations
- Location: `DeckFlow.Core/Models/DeckEntry.cs`
- Pattern: Immutable sealed record with `init`-only properties

**`IDeckEntryLoader`:**
- Purpose: Unified entry point for loading a deck from either text or URL, any supported platform
- Location: `DeckFlow.Core/Loading/DeckEntryLoader.cs`
- Pattern: Interface + default implementation; dispatches to parsers or importers

**`DiffEngine`:**
- Purpose: Computes set difference between two `List<DeckEntry>` using configurable `MatchMode` (Loose/Strict)
- Location: `DeckFlow.Core/Diffing/DiffEngine.cs`
- Pattern: Constructed with mode, single `Compare()` method

**`ICategoryKnowledgeStore`:**
- Purpose: In-process cache + SQLite façade for card→category observations
- Location: `DeckFlow.Web/Services/ICategoryKnowledgeStore.cs` + `CategoryKnowledgeStore.cs`
- Pattern: Singleton service; reads from `CategoryKnowledgeRepository`

**`IParser` (Moxfield/Archidekt):**
- Purpose: Convert pasted deck text strings into `List<DeckEntry>`
- Location: `DeckFlow.Core/Parsing/IParser.cs`, `MoxfieldParser.cs`, `ArchidektParser.cs`
- Pattern: Concrete classes registered as Transient; no shared state

## Entry Points

**Web Application:**
- Location: `DeckFlow.Web/Program.cs`
- Triggers: `dotnet run` / Kestrel / Docker / Fly.io / Render
- Responsibilities: DI container setup, middleware pipeline, Serilog configuration

**CLI:**
- Location: `DeckFlow.CLI/Program.cs`
- Triggers: `dotnet run -- <subcommand>` or compiled binary
- Responsibilities: Maps System.CommandLine subcommands to `CommandRunners` static methods

**Default Route:**
- `GET /` → `DeckController.Home()` renders `Views/Deck/Home.cshtml`
- `GET /sync` → `DeckController.Index()` renders deck sync page

## Architectural Constraints

- **Threading:** ASP.NET Core async throughout; `ArchidektCacheJobService` uses `Channel<T>` for producer-consumer; no raw threads
- **Global state:** Singleton services hold in-memory caches (ban list, commander data, Scryfall results) — all read-only after warm-up
- **Circular imports:** None — strict one-way dependency: Web → Core, CLI → Core
- **Admin protection:** All `/Admin/*` routes protected by `BasicAuthMiddleware` registered conditionally in `Program.cs`
- **Same-origin guard:** API endpoints that mutate or return sensitive data check `SameOriginRequestValidator` before processing
- **TypeScript compilation:** `wwwroot/ts/*.ts` compiled to `wwwroot/js/*.js` via `tsc` MSBuild target before each build; source maps not committed
- **Browser extension:** `browser-extensions/deckflow-bridge/` zipped into `wwwroot/extensions/deckflow-bridge.zip` by MSBuild at build/publish time

## Anti-Patterns

### Mixing interface and implementation in the same file

**What happens:** Service interfaces (`IDeckSyncService`, `ICategorySuggestionService`) are defined in the same `.cs` file as their concrete implementation class.

**Why it's wrong:** Makes interfaces harder to find and violates single-responsibility at the file level; test doubles must import the full implementation file.

**Do this instead:** Keep interfaces in a dedicated `I*.cs` file or in `DeckFlow.Web/Services/` with a clear naming convention. `IFeedbackStore.cs` in the same directory demonstrates the already-established pattern of separate interface files.

## Error Handling

**Strategy:** Exceptions propagate to controller action boundaries; controllers return `BadRequest`/`StatusCode` responses. Upstream API failures surfaced via `UpstreamErrorMessageBuilder` (`DeckFlow.Web/Services/UpstreamErrorMessageBuilder.cs`).

**Patterns:**
- `DeckParseException` (`DeckFlow.Core/Parsing/DeckParseException.cs`) for malformed deck text — caught at service layer, translated to user-facing error
- Timeout `CancellationTokenSource` created per-action (20s) and linked with `HttpContext.RequestAborted`
- Serilog structured logging at Warning/Error for upstream failures; request logging via `UseSerilogRequestLogging()`

## Cross-Cutting Concerns

**Logging:** Serilog — `WriteTo.File` (rolling daily, 14-day retention in `DeckFlow.Web/logs/`), `WriteTo.Console` in Development
**Validation:** Input validated at controller boundary; `[ValidateAntiForgeryToken]` on form POST actions; `SameOriginRequestValidator` on API mutations
**Authentication:** `/Admin/*` branch uses `BasicAuthMiddleware` (`DeckFlow.Web/Infrastructure/BasicAuthMiddleware.cs`); rest of app is unauthenticated
**Rate Limiting:** `feedback-submit` policy — 5 requests/IP/hour on feedback submission endpoint
**Security Headers:** `UseDeckFlowSecurityHeaders()` extension applies CSP, X-Frame-Options, X-Content-Type-Options, Referrer-Policy, Permissions-Policy to every response

---

*Architecture analysis: 2026-04-26*
