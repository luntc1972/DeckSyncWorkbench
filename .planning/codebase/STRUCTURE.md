# Codebase Structure

**Analysis Date:** 2026-04-26

## Directory Layout

```
decksyncworkbench/                      # Repo root
├── DeckFlow.Core/                      # Domain library — no ASP.NET deps
│   ├── Models/                         # Core data types (DeckEntry, DeckDiff, etc.)
│   ├── Parsing/                        # Moxfield + Archidekt text parsers
│   ├── Loading/                        # DeckEntryLoader — dispatcher to parsers/importers
│   ├── Diffing/                        # DiffEngine, DeckDiff
│   ├── Filtering/                      # DeckEntryFilter, CategoryFilter
│   ├── Exporting/                      # Delta/Full/MoxfieldText exporters
│   ├── Reporting/                      # Category and reconciliation reporters
│   ├── Normalization/                  # CardNormalizer, CategoryNormalization
│   ├── Integration/                    # IMoxfieldDeckImporter, IArchidektDeckImporter + impls
│   ├── Knowledge/                      # CategoryKnowledgeRepository (SQLite, ADO.NET)
│   └── DeckFlow.Core.csproj
├── DeckFlow.Core.Tests/                # xUnit tests for Core
│   └── *.Tests.cs                      # One file per class under test
├── DeckFlow.Web/                       # ASP.NET Core MVC app
│   ├── Controllers/                    # MVC controllers
│   │   ├── Api/                        # ApiController JSON endpoints
│   │   │   ├── DeckSyncApiController.cs
│   │   │   └── SuggestionsApiController.cs
│   │   ├── Admin/                      # Admin-only controllers (BasicAuth protected)
│   │   │   ├── AdminFeedbackController.cs
│   │   │   └── ArchidektCacheJobsController.cs
│   │   ├── DeckController.cs           # Main page controller (sync, lookup, ChatGPT)
│   │   ├── CommanderController.cs
│   │   ├── FeedbackController.cs
│   │   ├── AboutController.cs
│   │   └── HelpController.cs
│   ├── Services/                       # Business logic + external API clients
│   │   ├── DeckSyncService.cs          # IDeckSyncService
│   │   ├── CategorySuggestionService.cs
│   │   ├── CategoryKnowledgeStore.cs   # ICategoryKnowledgeStore
│   │   ├── ArchidektCacheJobService.cs # BackgroundService + IArchidektCacheJobService
│   │   ├── CardLookupService.cs
│   │   ├── CardSearchService.cs
│   │   ├── ChatGptDeckPacketService.cs
│   │   ├── ChatGptDeckComparisonService.cs
│   │   ├── ChatGptCedhMetaGapService.cs
│   │   ├── CommanderBanListService.cs
│   │   ├── CommanderCategoryService.cs
│   │   ├── ScryfallCommanderSearchService.cs
│   │   ├── ScryfallSetService.cs
│   │   ├── ScryfallTaggerService.cs
│   │   ├── ScryfallThrottle.cs
│   │   ├── EdhTop16Client.cs
│   │   ├── MechanicLookupService.cs
│   │   ├── FeedbackStore.cs
│   │   ├── HelpContentService.cs
│   │   ├── VersionService.cs
│   │   └── DeckSyncSupport.cs          # Static helpers for DeckSyncService
│   ├── Models/                         # View models + request/response DTOs
│   │   ├── Api/                        # API-specific request/response records
│   │   │   ├── DeckSyncApiRequest.cs
│   │   │   ├── DeckSyncApiResponse.cs
│   │   │   └── SuggestionResponses.cs
│   │   └── *.cs                        # View models keyed to controller actions
│   ├── Views/                          # Razor views, one folder per controller
│   │   ├── Deck/                       # All DeckController views
│   │   ├── Commander/
│   │   ├── Feedback/
│   │   ├── Help/
│   │   ├── About/
│   │   ├── Admin/Feedback/
│   │   └── Shared/                     # _Layout.cshtml, partials (_BusyIndicator, etc.)
│   ├── Infrastructure/                 # Middleware + builder extensions
│   │   ├── SecurityHeadersApplicationBuilderExtensions.cs
│   │   ├── BasicAuthMiddleware.cs
│   │   └── DevelopmentBrowserLauncher.cs
│   ├── Security/
│   │   └── SameOriginRequestValidator.cs
│   ├── Help/                           # Markdown help content (CopyToOutput)
│   ├── wwwroot/                        # Static assets served directly
│   │   ├── css/                        # Compiled/hand-authored CSS
│   │   │   ├── site.css                # Base styles
│   │   │   ├── site-common.css         # Shared layout (new additions go here)
│   │   │   ├── site-mobile.css         # Mobile responsive overrides
│   │   │   ├── site-commander-table.css
│   │   │   └── site-*.css              # Guild/theme variants (azorius, boros, etc.)
│   │   ├── js/                         # Compiled TypeScript output (do not edit directly)
│   │   ├── ts/                         # TypeScript source files
│   │   │   ├── site.ts
│   │   │   ├── deck-sync.ts
│   │   │   ├── card-lookup.ts
│   │   │   ├── card-search.ts
│   │   │   ├── category-suggestions.ts
│   │   │   ├── commander-search.ts
│   │   │   ├── judge-questions.ts
│   │   │   ├── df-select.ts            # Custom accessible select component
│   │   │   └── df-typeahead.ts         # Extracted typeahead module
│   │   ├── extensions/                 # Browser extension zip (build artifact)
│   │   │   └── deckflow-bridge.zip     # Generated at build from browser-extensions/
│   │   └── lib/                        # Vendored frontend libs (bootstrap, jquery)
│   ├── Program.cs                      # DI registration + middleware pipeline
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   ├── tsconfig.json                   # TypeScript compiler config
│   └── DeckFlow.Web.csproj
├── DeckFlow.Web.Tests/                 # xUnit tests for Web layer
│   ├── TestDoubles/                    # Fakes / stubs for service interfaces
│   └── *ControllerTests.cs / *ServiceTests.cs
├── DeckFlow.CLI/                       # Console app (System.CommandLine)
│   ├── Program.cs                      # All subcommand wiring
│   ├── CommandRunners.cs               # Static async runner methods
│   └── DeckFlow.CLI.csproj
├── browser-extensions/
│   └── deckflow-bridge/                # Chrome extension source (zipped into wwwroot at build)
│       ├── manifest.json
│       ├── background.js
│       ├── deckflow-bridge.js
│       └── options.html / options.js
├── .github/workflows/                  # CI pipeline definitions
├── .planning/codebase/                 # GSD codebase maps (this directory)
├── docs/                               # Developer-facing docs and cheat sheets
├── scripts/                            # Shell/PowerShell run scripts
├── tasks/                              # Claude task tracking (todo.md, lessons.md)
├── prompt-templates/                   # ChatGPT prompt template files
├── artifacts/                          # Build/analysis artifacts (gitignored)
├── Directory.Build.props               # Shared MSBuild properties
├── DeckFlow.sln
├── Dockerfile
├── fly.toml                            # Fly.io deployment config
├── render.yaml                         # Render.com deployment config
└── README.md
```

## Directory Purposes

**`DeckFlow.Core/Models/`:**
- Purpose: Canonical immutable data types shared across all layers
- Contains: `DeckEntry`, `DeckDiff`, `LoadedDecks`, `MatchMode`, `SyncDirection`, `PrintingConflict`, `CategorySyncMode`
- Key files: `DeckFlow.Core/Models/DeckEntry.cs`

**`DeckFlow.Core/Integration/`:**
- Purpose: Interfaces and implementations for fetching decks from external platforms
- Contains: `IMoxfieldDeckImporter`, `IArchidektDeckImporter`, API URL helpers, EDHREC lookup
- Key files: `DeckFlow.Core/Integration/DeckImporterInterfaces.cs`, `MoxfieldApiDeckImporter.cs`, `ArchidektApiDeckImporter.cs`

**`DeckFlow.Core/Knowledge/`:**
- Purpose: SQLite persistence layer for card-category observations crawled from Archidekt
- Contains: `CategoryKnowledgeRepository` (raw ADO.NET), `ArchidektDeckCacheSession`, `DeckCategoryCacheWriter`
- Key files: `DeckFlow.Core/Knowledge/CategoryKnowledgeRepository.cs`

**`DeckFlow.Web/Services/`:**
- Purpose: All web-layer business logic, external API clients, and DI-registered service implementations
- Contains: Service classes (both interface definition and implementation, colocated)
- Key files: `DeckFlow.Web/Services/DeckSyncService.cs`, `CategorySuggestionService.cs`, `ArchidektCacheJobService.cs`

**`DeckFlow.Web/wwwroot/ts/`:**
- Purpose: TypeScript source — edit here, never in `wwwroot/js/`
- Contains: One `.ts` per feature area, plus shared components (`df-select.ts`, `df-typeahead.ts`)
- Compiled by MSBuild `CompileTypeScriptAssets` target before each build

**`DeckFlow.Web/wwwroot/css/`:**
- Purpose: All site stylesheets
- Key pattern: New layout CSS goes in `site-common.css`; theme variants are standalone forks (`site-azorius.css` etc.); mobile overrides in `site-mobile.css`

**`DeckFlow.Web/Help/`:**
- Purpose: Markdown files for in-app help topics, copied to output at build
- Consumed by: `IHelpContentService` / `HelpContentService.cs`

## Key File Locations

**Entry Points:**
- `DeckFlow.Web/Program.cs`: Web app bootstrap, DI registrations, middleware pipeline
- `DeckFlow.CLI/Program.cs`: CLI subcommand wiring (all subcommands defined here)
- `DeckFlow.CLI/CommandRunners.cs`: Static runner methods for each CLI subcommand

**Configuration:**
- `DeckFlow.Web/appsettings.json`: App config skeleton
- `DeckFlow.Web/appsettings.Development.json`: Dev overrides
- `Directory.Build.props`: Shared MSBuild properties for all projects
- `DeckFlow.Web/tsconfig.json`: TypeScript compiler config
- `fly.toml`: Fly.io production deployment
- `render.yaml`: Render.com deployment

**Core Logic:**
- `DeckFlow.Core/Diffing/DiffEngine.cs`: Card-set diffing
- `DeckFlow.Core/Loading/DeckEntryLoader.cs`: Deck loading dispatch
- `DeckFlow.Core/Knowledge/CategoryKnowledgeRepository.cs`: SQLite access
- `DeckFlow.Web/Services/ArchidektCacheJobService.cs`: Background crawl job

**Security:**
- `DeckFlow.Web/Security/SameOriginRequestValidator.cs`: API origin guard
- `DeckFlow.Web/Infrastructure/BasicAuthMiddleware.cs`: Admin area auth
- `DeckFlow.Web/Infrastructure/SecurityHeadersApplicationBuilderExtensions.cs`: CSP + security headers

**Testing:**
- `DeckFlow.Web.Tests/TestDoubles/`: Fake implementations of service interfaces
- `DeckFlow.Core.Tests/`: Tests co-located at project root (one file per class)

## Naming Conventions

**Files:**
- C# files: PascalCase, match class/interface name exactly (`DeckEntryLoader.cs`)
- Interface files: `I` prefix when in separate file (`IFeedbackStore.cs`, `IHelpContentService.cs`)
- Test files: `{ClassName}Tests.cs` (e.g., `DiffEngineTests.cs`)
- TypeScript files: kebab-case (`deck-sync.ts`, `df-select.ts`)
- CSS theme files: `site-{theme-name}.css` (e.g., `site-azorius.css`)

**Directories:**
- C# projects: PascalCase with dot-separation (`DeckFlow.Core`, `DeckFlow.Web`)
- Subdirectories within projects: PascalCase noun (`Controllers`, `Services`, `Models`, `Diffing`)
- API sub-controllers: `Controllers/Api/`, admin: `Controllers/Admin/`

**Types:**
- Records for immutable value objects: `DeckEntry`, `DeckDiff`, `DeckSyncResult`
- `sealed` on all concrete classes and records (enforced throughout)
- Interfaces colocated with implementation (common) or in separate `I*.cs` file (less common; preferred direction)

## Where to Add New Code

**New MVC page feature:**
- Controller action: `DeckFlow.Web/Controllers/DeckController.cs` (if deck-related) or new `XxxController.cs`
- View: `DeckFlow.Web/Views/{ControllerName}/{ActionName}.cshtml`
- View model: `DeckFlow.Web/Models/XxxViewModel.cs`
- TypeScript: `DeckFlow.Web/wwwroot/ts/{feature-name}.ts` (compiles to `wwwroot/js/` automatically)

**New API endpoint:**
- File: `DeckFlow.Web/Controllers/Api/XxxApiController.cs` (use `[ApiController]`, `ControllerBase`)
- Request/Response models: `DeckFlow.Web/Models/Api/XxxRequest.cs` / `XxxResponse.cs`
- Add same-origin guard: call `SameOriginRequestValidator.IsValid(Request)` before processing

**New service:**
- File: `DeckFlow.Web/Services/XxxService.cs` (define interface + implementation in same file, or split to `IXxxService.cs`)
- Register in: `DeckFlow.Web/Program.cs` (`AddScoped` / `AddSingleton` as appropriate)
- Test double: `DeckFlow.Web.Tests/TestDoubles/FakeXxxService.cs`

**New Core domain logic:**
- Add to the appropriate namespace subdirectory (`DeckFlow.Core/Diffing/`, `DeckFlow.Core/Filtering/`, etc.)
- New concern: create a new subdirectory under `DeckFlow.Core/`
- Tests: `DeckFlow.Core.Tests/XxxTests.cs`

**New CSS:**
- Layout/structural styles: `DeckFlow.Web/wwwroot/css/site-common.css`
- Mobile overrides: `DeckFlow.Web/wwwroot/css/site-mobile.css`
- Theme-specific: in the relevant `site-{theme}.css` fork
- Do NOT edit `site.css` for new additions (it's the base; theme forks override it)

**New help topic:**
- Add markdown file to `DeckFlow.Web/Help/`
- Register topic in `IHelpContentService` / `HelpContentService.cs`

## Special Directories

**`DeckFlow.Web/wwwroot/js/`:**
- Purpose: TypeScript compile output — auto-generated from `wwwroot/ts/`
- Generated: Yes (MSBuild `CompileTypeScriptAssets` target)
- Committed: Yes (output committed to repo)

**`DeckFlow.Web/wwwroot/extensions/`:**
- Purpose: Browser extension zip for download
- Generated: Yes (MSBuild `ZipDeckFlowBridge` target from `browser-extensions/deckflow-bridge/`)
- Committed: No (build artifact)

**`artifacts/`:**
- Purpose: Build and analysis output artifacts
- Generated: Yes
- Committed: No

**`.planning/codebase/`:**
- Purpose: GSD codebase map documents (consumed by `/gsd-plan-phase`, `/gsd-execute-phase`)
- Generated: By GSD mapper agents
- Committed: Yes

**`tasks/`:**
- Purpose: Claude task tracking — `todo.md` (active plan), `lessons.md` (learned patterns)
- Generated: By Claude during task sessions
- Committed: Yes

---

*Structure analysis: 2026-04-26*
