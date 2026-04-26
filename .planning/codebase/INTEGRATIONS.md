# External Integrations

**Analysis Date:** 2026-04-26

## APIs & External Services

**MTG Card Data — Scryfall:**
- Service: Scryfall REST API — card search, bulk card lookup by ID, named card lookup, rulings, sets, Tagger tags
- Base URL: `https://api.scryfall.com` (`DeckFlow.Web/Services/ScryfallRestClientFactory.cs`)
- Tagger URL: `https://tagger.scryfall.com` (`DeckFlow.Web/Services/ScryfallTaggerService.cs`)
- SDK/Client: RestSharp `RestClient` via `ScryfallRestClientFactory.Create()`
- Auth: None (public API); User-Agent: `DeckFlow/1.0 (+https://github.com/luntc1972/DeckFlow)`
- Rate limiting: `ScryfallThrottle.cs` enforces per-request throttle; Polly retries in Core
- Endpoints used: `cards/collection` (POST), `cards/search`, `cards/named`, `cards/{id}/rulings`, `sets/*`
- Consumers: `ScryfallCardSearchService`, `ScryfallCardLookupService`, `ScryfallCommanderSearchService`, `ScryfallSetService`, `ScryfallTaggerService`, `ChatGptDeckComparisonService`, `ChatGptCedhMetaGapService`, `ChatGptDeckPacketService`

**MTG Deck Platform — Moxfield:**
- Service: Moxfield private API — deck import by deck ID
- Base URL: `https://api.moxfield.com/v2/decks/all/{deckId}` (`DeckFlow.Core/Integration/MoxfieldApiUrl.cs`)
- SDK/Client: RestSharp in `DeckFlow.Core/Integration/MoxfieldApiDeckImporter.cs`
- Auth: None for public decks; private decks require browser session cookie relayed via browser extension
- Headers: `Referer: https://moxfield.com/`
- Consumer: `MoxfieldApiDeckImporter` (registered as `IMoxfieldDeckImporter`)

**MTG Deck Platform — Archidekt:**
- Service: Archidekt REST API — deck import, recent decks via websocket endpoint
- Base URL: `https://archidekt.com` / `https://websockets.archidekt.com` (`DeckFlow.Core/Integration/`)
- Deck URL pattern: `https://archidekt.com/api/decks/{deckId}/`
- SDK/Client: RestSharp in `DeckFlow.Core/Integration/ArchidektApiDeckImporter.cs` and `ArchidektRecentDecksImporter.cs`
- Auth: None; `Referer: https://archidekt.com/` header
- Consumer: `ArchidektApiDeckImporter` (registered as `IArchidektDeckImporter`), `ArchidektCacheJobService` (background service polling recent decks)

**Commander Combo Lookup — Commander Spellbook:**
- Service: Commander Spellbook GraphQL-style REST API — combo lookup by card list
- URL: `https://backend.commanderspellbook.com/find-my-combos` (`DeckFlow.Web/Services/CommanderSpellbookService.cs`)
- Also used as intermediary: `https://backend.commanderspellbook.com/card-list-from-url` (Moxfield import path)
- SDK/Client: `HttpClient` (raw) in `CommanderSpellbookService`; RestSharp for card-list-from-url in `MoxfieldApiDeckImporter`
- Auth: None

**EDH Card Data — EDHREC:**
- Service: EDHREC JSON data — card recommendations by card slug
- Base URL: `https://json.edhrec.com` (`DeckFlow.Core/Integration/EdhrecCardLookup.cs`)
- URL pattern: `https://json.edhrec.com/pages/cards/{slug}.json`
- SDK/Client: RestSharp in `EdhrecCardLookup`
- Auth: None; `Referer: https://edhrec.com/`

**EDH Tournament Data — EdhTop16:**
- Service: EdhTop16 GraphQL API — commander tournament meta data
- URL: `https://edhtop16.com/api/graphql` (`DeckFlow.Web/Services/EdhTop16Client.cs`)
- SDK/Client: RestSharp in `EdhTop16Client`
- Auth: None; POST with GraphQL query body

**Commander Rules — MTGCommander.net:**
- Service: Commander ban list scraping
- URL: `https://mtgcommander.net/index.php/banned-list/` (`DeckFlow.Web/Services/CommanderBanListService.cs`)
- SDK/Client: Raw `HttpClient`; result cached in `IMemoryCache`
- Auth: None

**Official Rules — Wizards of the Coast:**
- Service: WotC comprehensive rules document scraping for mechanic lookup
- URL: `https://magic.wizards.com/en/rules` (`DeckFlow.Web/Services/MechanicLookupService.cs`)
- SDK/Client: RestSharp
- Auth: None; result cached in `IMemoryCache` with key `wotc-mechanic-rules-document`

**ChatGPT / OpenAI (indirect):**
- Integration is prompt-generation only — DeckFlow generates structured text packets for users to paste into ChatGPT
- No direct API calls; no OpenAI SDK; no API key required
- Artifacts: JSON/text packets written to `MTG_DATA_DIR` directory by `ChatGptArtifactsDirectory`, `ChatGptPacketArtifactStore`
- Services: `ChatGptDeckPacketService`, `ChatGptDeckComparisonService`, `ChatGptCedhMetaGapService`, `ChatGptResponseParsers`

## Data Storage

**Databases:**
- SQLite — local file-based database for category knowledge and deck queue
  - File location: `{MTG_DATA_DIR}/category-knowledge.db` (env-driven path)
  - Dev artifact: `artifacts/category-knowledge.db`
  - Client: `Microsoft.Data.Sqlite` (direct ADO.NET, no ORM)
  - Schema managed by: `DeckFlow.Core/Knowledge/CategoryKnowledgeRepository.cs` (inline DDL with migrations)
  - Tables: `card_category_observations`, `card_deck_totals`, deck queue columns
  - Used by: `CategoryKnowledgeStore` (Web), `CategoryKnowledgeRepository` (Core)

**File Storage:**
- Local filesystem only — ChatGPT artifact JSON/text files persisted under `MTG_DATA_DIR`
- Fly.io persistent volume (`mtg_data`) mounted at `/data` keeps DB and artifacts across restarts
- Help content: Markdown files in `DeckFlow.Web/Help/` copied to output; served via `HelpContentService`

**Caching:**
- `IMemoryCache` (ASP.NET Core in-memory) — card search results, commander ban list, spellbook results, WotC rules document
- Services explicitly call `TryGetValue` / `Set` on `IMemoryCache`; no distributed cache

## Authentication & Identity

**Auth Provider:**
- No user authentication — application is public with no login
- Admin routes (`/Admin`) protected by HTTP Basic Auth middleware
  - Implementation: `DeckFlow.Web/Infrastructure/BasicAuthMiddleware.cs`
  - Credentials sourced from `FEEDBACK_ADMIN_USER` / `FEEDBACK_ADMIN_PASSWORD` environment variables

**Request Integrity:**
- `SameOriginRequestValidator` (`DeckFlow.Web/Infrastructure/`) validates `Origin` header for state-mutating requests
- `UseForwardedHeaders` middleware ensures correct scheme/host behind Fly.io/Render proxy
- Security headers applied via `UseDeckFlowSecurityHeaders()` (`SecurityHeadersApplicationBuilderExtensions.cs`)
- Rate limiter on `feedback-submit` policy: 5 requests/hour per IP (`Program.cs`)

## Monitoring & Observability

**Error Tracking:**
- None (no Sentry, Datadog, etc.)

**Logs:**
- Serilog structured logging throughout
- Development: console + rolling file at `DeckFlow.Web/logs/web-.log`
- Production: file only; 14-day retention
- `UseSerilogRequestLogging()` for HTTP request logs
- CLI: Serilog file sink only (`DeckFlow.CLI`)

## CI/CD & Deployment

**Hosting:**
- Fly.io (primary) — app name `mtg-deck-studio`, region `sea`, shared-cpu-1x, 512MB RAM
- Auto-stop/auto-start enabled; min 0 machines running (scales to zero)
- Health check: `GET /` every 15s

**Container:**
- Docker multi-stage build — `Dockerfile` at repo root
- Build image: `mcr.microsoft.com/dotnet/sdk:10.0` + Node.js 20
- Runtime image: `mcr.microsoft.com/dotnet/aspnet:10.0`

**CI Pipeline:**
- None detected (no GitHub Actions, no `.github/` directory)

## Browser Extension

**DeckFlow Bridge** (`browser-extensions/deckflow-bridge/`):
- Manifest V3 Chrome/Edge extension
- Purpose: relays Moxfield private deck data from user's authenticated browser session to DeckFlow server
- Host permissions: `https://moxfield.com/*`, `https://api.moxfield.com/*`, `https://api2.moxfield.com/*`
- Packaged automatically into `wwwroot/extensions/deckflow-bridge.zip` by MSBuild `ZipDeckFlowBridge` target
- Served as a download from DeckFlow.Web

## Environment Configuration

**Required env vars (production):**
- `MTG_DATA_DIR` — persistent data directory (SQLite DB + artifacts)
- `FEEDBACK_ADMIN_USER` — admin Basic Auth username
- `FEEDBACK_ADMIN_PASSWORD` — admin Basic Auth password
- `FEEDBACK_IP_SALT` — HMAC salt for IP hashing in feedback log
- `ASPNETCORE_ENVIRONMENT` — set to `Production`

**Optional env vars:**
- `MTGDECKSTUDIO_DISABLE_AUTO_BROWSER=true` — suppresses dev auto-launch
- `PORT` — overrides listen port (default 8080)

**Secrets location:**
- Environment variables only; no secrets files committed; `.env` files not detected in repo

## Webhooks & Callbacks

**Incoming:** None

**Outgoing:** None (all external calls are request-response polling; no webhook subscriptions)

---

*Integration audit: 2026-04-26*
