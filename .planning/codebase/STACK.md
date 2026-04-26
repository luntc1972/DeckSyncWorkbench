# Technology Stack

**Analysis Date:** 2026-04-26

## Languages

**Primary:**
- C# 13 (net10.0) - All server-side logic across DeckFlow.Core, DeckFlow.Web, DeckFlow.CLI, and test projects
- TypeScript 6.x - Client-side interactivity compiled via `tsc` into `wwwroot/js/`

**Secondary:**
- JavaScript (ES2017 target) - Compiled output from TypeScript; also used directly in browser extension (`browser-extensions/deckflow-bridge/`)
- HTML/CSS/Razor - View layer in `DeckFlow.Web/Views/` and `wwwroot/`

## Runtime

**Environment:**
- .NET 10.0 (all four projects target `net10.0`)
- Node.js 20.x (required at build time for TypeScript compilation; installed in Docker via NodeSource)

**Package Manager:**
- NuGet for .NET packages; lockfile: none (standard restore)
- npm for TypeScript — `DeckFlow.Web/package.json`, `package-lock.json`
- `Directory.Build.props` clears NuGet fallback folders for portability across Windows/WSL/Linux

## Frameworks

**Core:**
- ASP.NET Core MVC 10 (`Microsoft.NET.Sdk.Web`) - MVC + Razor Views, Web API controllers, middleware pipeline

**Build/Dev:**
- `Microsoft.TypeScript.MSBuild` 5.2.2 — triggers `tsc` via MSBuild `CompileTypeScriptAssets` target
- `Swashbuckle.AspNetCore` 7.0.0 — Swagger/OpenAPI UI, enabled in Development only (`/swagger`)
- `System.CommandLine` 2.0.0-beta4 — CLI argument parsing in `DeckFlow.CLI`
- Docker multi-stage build (`Dockerfile` at repo root) — sdk:10.0 build stage, aspnet:10.0 runtime stage

**Testing:**
- xunit 2.9.3 — test framework for both `DeckFlow.Core.Tests` and `DeckFlow.Web.Tests`
- xunit.runner.visualstudio 3.1.4 — VS / dotnet test integration
- Microsoft.NET.Test.Sdk 17.14.1
- coverlet.collector 6.0.4 — code coverage (Core.Tests only)

## Key Dependencies

**Critical:**
- `RestSharp` 114.0.0 — HTTP client used throughout Core and Web for all external API calls (Scryfall, Archidekt, Moxfield, EDHREC, EdhTop16)
- `Microsoft.Data.Sqlite` 10.0.0 — SQLite storage for category knowledge DB (used in both Core and Web)
- `Polly` 8.1.0 — resilience/retry policies in `DeckFlow.Core`
- `Markdig` 0.38.0 — Markdown-to-HTML rendering for Help content pages in `DeckFlow.Web`

**Infrastructure:**
- `Serilog.AspNetCore` 9.0.0 — structured logging host integration
- `Serilog.Sinks.Console` 6.0.0 — console sink (Development only)
- `Serilog.Sinks.File` 6.0.0 / 7.0.0 — rolling file sink, 14-day retention (`DeckFlow.Web/logs/`, `DeckFlow.CLI`)
- `Microsoft.Extensions.Logging.Abstractions` 10.0.0 — logging interfaces in Core
- `System.Threading.RateLimiting` — built-in .NET rate limiter used for feedback-submit endpoint (5 req/hr per IP)

## Configuration

**Environment Variables:**
- `MTG_DATA_DIR` — base directory for SQLite knowledge DB and ChatGPT artifacts (defaults to `./` locally, `/data` in Docker/Fly)
- `FEEDBACK_ADMIN_USER` / `FEEDBACK_ADMIN_PASSWORD` — Basic Auth credentials for `/Admin` routes
- `FEEDBACK_IP_SALT` — salt for IP address hashing in feedback store
- `MTGDECKSTUDIO_DISABLE_AUTO_BROWSER` — set `true` to suppress dev auto-browser launch
- `ASPNETCORE_ENVIRONMENT` — `Development` or `Production`
- `PORT` — overrides default listen port (8080) in Docker entrypoint

**Config Files:**
- `DeckFlow.Web/appsettings.json` — Logging levels only; no secrets
- `DeckFlow.Web/appsettings.Development.json` — dev overrides
- `DeckFlow.Web/tsconfig.json` — TypeScript: target es2017, no modules, strict mode, out to `wwwroot/js/`
- `Directory.Build.props` — NuGet fallback folder suppression (cross-platform portability)
- `fly.toml` — Fly.io deployment config (app: `mtg-deck-studio`, region: sea, shared-cpu-1x, 512MB)

**Build:**
- MSBuild `CompileTypeScriptAssets` target runs `tsc` before every build
- MSBuild `ZipDeckFlowBridge` target packages browser extension into `wwwroot/extensions/deckflow-bridge.zip`
- `TypeScriptCompileBlocked=true` suppresses the default TS MSBuild behavior; custom target controls it

## Platform Requirements

**Development:**
- WSL2 or Windows; .NET 10 SDK; Node.js 20 (for `npm install typescript`)
- Visual Studio or `dotnet` CLI

**Production:**
- Fly.io (configured); Docker container, aspnet:10.0 runtime image
- Persistent volume mounted at `/data` for SQLite DB and artifact storage
- Reverse proxy terminates TLS; app listens on HTTP port 8080; `UseForwardedHeaders` enabled

---

*Stack analysis: 2026-04-26*
