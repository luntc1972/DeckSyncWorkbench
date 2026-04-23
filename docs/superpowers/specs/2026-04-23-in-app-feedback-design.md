# In-App Feedback Capture — Design

**Date:** 2026-04-23
**Status:** Approved, pending implementation
**Owner:** Chris Lunt

## Problem

DeckFlow has no channel for end-user comments, suggestions, or bug reports. The only current contact path is the GitHub URL on the About page, which requires a GitHub account and routes outside the app.

## Goal

Give any user a frictionless in-app form to submit a comment, suggestion, or bug report, and give the admin (Chris Lunt) a protected page to read, triage, and archive submissions.

## Non-Goals

- Multi-admin workflows, teams, roles, per-user accounts.
- Email notifications on submit (may layer later).
- Third-party integrations (GitHub Issues bridge, Slack, etc.).
- Threaded replies or back-and-forth with submitter.
- Analytics, trend dashboards, export.

## Decisions Locked During Brainstorming

| # | Question | Decision |
|---|---|---|
| 1 | How to access submissions | In-app admin page protected by env-var credentials |
| 2 | Storage format | SQLite table with status field (New / Read / Archived) |
| 3 | Admin auth mechanism | HTTP Basic Auth middleware |
| 4 | Spam protection | Honeypot field + per-IP rate limit |
| 5 | UI placement | Footer link site-wide + card on Help page |
| 6 | Form fields | Type, Message, optional Email, honeypot, auto-captured context |
| 7 | After-submit UX | Post-Redirect-Get with TempData flash banner |

## Architecture

New vertical slice inside `DeckFlow.Web`. No changes to `DeckFlow.Core` or other projects.

```
DeckFlow.Web/
  Controllers/
    FeedbackController.cs              # public GET/POST /Feedback
    Admin/
      AdminFeedbackController.cs       # auth-gated /Admin/Feedback
  Services/
    IFeedbackStore.cs
    FeedbackStore.cs                   # SQLite CRUD, schema init
  Infrastructure/
    BasicAuthMiddleware.cs             # scoped to /Admin/* segment
  Models/
    FeedbackType.cs                    # enum: Bug, Suggestion, Comment
    FeedbackStatus.cs                  # enum: New, Read, Archived
    FeedbackSubmission.cs              # form binding model
    FeedbackItem.cs                    # persistence / read model
  Views/
    Feedback/
      Index.cshtml                     # form + success banner
    Admin/
      Feedback/
        Index.cshtml                   # list with filters + pagination
        Detail.cshtml                  # single item + actions
    Shared/
      _Layout.cshtml                   # footer link (modified)
    Help/
      Index.cshtml                     # feedback card (modified)
```

DI registration added in `Program.cs`:

```csharp
builder.Services.AddSingleton<IFeedbackStore, FeedbackStore>();
builder.Services.AddRateLimiter(o => { /* feedback-submit policy */ });
// BasicAuthMiddleware inserted via app.UseWhen on /Admin path
```

## Data Model

New SQLite database `feedback.db` under `MTG_DATA_DIR` (separate file from `category-knowledge.db` — unrelated domain, independent lifecycle, easier to wipe/export).

```sql
CREATE TABLE IF NOT EXISTS feedback (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  created_utc  TEXT    NOT NULL,             -- ISO8601 UTC
  type         TEXT    NOT NULL,             -- Bug | Suggestion | Comment
  message      TEXT    NOT NULL,
  email        TEXT    NULL,
  page_url     TEXT    NULL,                 -- referrer at submit
  user_agent   TEXT    NULL,
  ip_hash      TEXT    NULL,                 -- SHA256(ip + salt)
  app_version  TEXT    NULL,
  status       TEXT    NOT NULL DEFAULT 'New'  -- New | Read | Archived
);

CREATE INDEX IF NOT EXISTS idx_feedback_status_created
  ON feedback(status, created_utc DESC);

CREATE TABLE IF NOT EXISTS feedback_meta (
  key    TEXT PRIMARY KEY,
  value  TEXT NOT NULL
);
```

- Schema init lazy on first `FeedbackStore` use, gated by `SemaphoreSlim` per `CategoryKnowledgeStore` precedent.
- IP stored as `SHA256(ip || salt)` to support "submitted 5 times in one hour" rate limiting across restarts without retaining PII.
- Salt read from env `FEEDBACK_IP_SALT`; if absent, a 32-byte random salt is generated on first run and persisted to `feedback_meta.key='ip_salt'` (so hashes remain stable across restarts without requiring ops to set the env var).

## Submission Flow

### Form (`GET /Feedback`)

Razor view with themed panel:

- Type dropdown (required): `Bug` / `Suggestion` / `Comment`.
- Message textarea (required, 10–4000 chars).
- Email input (optional; `type="email"`; max 200 chars).
- Hidden honeypot input `name="website"` + CSS `position:absolute; left:-9999px` and `tabindex="-1"` + `autocomplete="off"`. Must remain empty on submit.
- Antiforgery token (built-in MVC `@Html.AntiForgeryToken()`).
- TempData success banner rendered when `TempData["FeedbackSuccess"]` is set.

### Submit (`POST /Feedback`)

Controller action order-of-checks:

1. `[ValidateAntiForgeryToken]` — built-in.
2. `[EnableRateLimiting("feedback-submit")]` — .NET `AddRateLimiter` fixed-window policy: **5 submits per hour per IP**. Partition key = `httpContext.Connection.RemoteIpAddress`. Rejection returns 429 with themed "too many submissions" view.
3. Honeypot check: if `submission.Website` is non-empty, log a warning and return `RedirectToAction("Index")` with TempData success (indistinguishable from a real success, per anti-bot hygiene) WITHOUT persisting.
4. Model validation via data annotations:
   - `Type` required, must parse to `FeedbackType` enum.
   - `Message` required, `[StringLength(4000, MinimumLength=10)]`.
   - `Email` optional, `[EmailAddress]` + `[StringLength(200)]` when provided.
5. On invalid model: return `View(submission)` with validation messages.
6. On valid: build `FeedbackItem` capturing:
   - `CreatedUtc = DateTime.UtcNow`
   - `PageUrl = Request.Headers.Referer` (truncated to 500 chars)
   - `UserAgent = Request.Headers.UserAgent` (truncated to 500 chars)
   - `IpHash = Sha256(RemoteIp + salt)`
   - `AppVersion` from `IVersionService`
7. Call `IFeedbackStore.AddAsync(item, ct)`.
8. Set `TempData["FeedbackSuccess"] = true`, `return RedirectToAction("Index")`.

## Admin Flow

### Auth — `BasicAuthMiddleware`

Registered via `app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/Admin"), ...)` before MVC endpoints.

Logic:

1. Read env `FEEDBACK_ADMIN_USER` and `FEEDBACK_ADMIN_PASSWORD`.
2. If either missing/empty: return `503 Service Unavailable` with body "Admin not configured". **Fail closed; never allow default creds.**
3. Read `Authorization` header. If missing or not `Basic <base64>`: return `401` with `WWW-Authenticate: Basic realm="DeckFlow Admin", charset="UTF-8"`.
4. Decode, split on first `:`. Compare using `CryptographicOperations.FixedTimeEquals` on UTF-8 bytes of both username and password (guards against timing attacks).
5. On mismatch: `401` + `WWW-Authenticate` header.
6. On match: call `next(context)`.
7. Never log header value or decoded creds; scrub via Serilog enricher if any request logging is added.

### List (`GET /Admin/Feedback`)

Query params: `status` (default `New`), `type` (default `All`), `page` (default 1), `pageSize=50`.

View:

- Filter bar: status buttons (`New (n)` / `Read (n)` / `Archived (n)` / `All (n)`), type dropdown.
- Table columns: Created (UTC, local-time tooltip), Type badge, truncated Message (first 80 chars with ellipsis), Email, actions.
- Actions per row: `View` (→ detail), `Archive` (inline POST).
- Pagination: Prev/Next with current page / total pages.
- Empty state: "No feedback in this view."

### Detail (`GET /Admin/Feedback/{id}`)

View shows:

- All stored fields, including `page_url`, `user_agent`, `app_version`, hashed IP (for repeat-submitter correlation), current status.
- Action buttons as POST forms with antiforgery:
  - `Mark Read` → status=Read (hidden if already Read/Archived).
  - `Archive` → status=Archived.
  - `Delete` → hard delete, confirm dialog via `onclick="return confirm(...)"`.

### Actions (`POST /Admin/Feedback/{id}/{action}`)

Single route with `action` in `{markRead, archive, delete}`. Antiforgery required. Return `RedirectToAction("Index", new { status = <prev filter> })` with TempData banner.

## UI Placement

### Footer link

`Views/Shared/_Layout.cshtml` — add `<a asp-controller="Feedback" asp-action="Index">Feedback</a>` in the footer row next to existing About/Help/GitHub links. Style using existing footer link classes; accent color via `--accent-strong` per project theming memory.

### Help page card

`Views/Help/Index.cshtml` — add a new card to the existing grid:

> **Found a bug or have an idea?**
> Send feedback directly to the developer — bugs, suggestions, or just say hi.
> [Send feedback →]

Uses existing Help card styling (`--panel` background, `--line` border).

### Feedback page styling

`Views/Feedback/Index.cshtml` wrapped in the same panel pattern as About/Help pages — `--panel`, `--line`, themed inputs — so every guild theme renders correctly. TempData success banner uses green accent derived from theme variables, not a hardcoded color.

## Configuration

New environment variables, documented in `README.md`:

| Var | Purpose | Required |
|---|---|---|
| `FEEDBACK_ADMIN_USER` | Basic auth username for `/Admin/Feedback` | yes to enable admin route |
| `FEEDBACK_ADMIN_PASSWORD` | Basic auth password | yes |
| `FEEDBACK_IP_SALT` | Salt for hashing submitter IPs | optional (auto-generated and persisted if absent) |

Reuses existing `MTG_DATA_DIR` for the SQLite file location. When `MTG_DATA_DIR` is not set (local dev), falls back to `<repo>/artifacts/feedback.db` — same pattern as `CategoryKnowledgeStore`.

Local dev setup: developer sets `FEEDBACK_ADMIN_USER=admin` + `FEEDBACK_ADMIN_PASSWORD=dev` via `dotnet user-secrets` or `launchSettings.json` (do NOT commit).

Production: set via Fly secrets (`fly secrets set FEEDBACK_ADMIN_USER=... FEEDBACK_ADMIN_PASSWORD=...`) and Render environment settings.

## Testing

New test file set in `DeckFlow.Web.Tests`, following the existing xUnit pattern (and custom sandbox runner when VSTest is blocked, per recent project memory):

### `FeedbackStoreTests`

Uses a tmp-file SQLite DB per test (`IDisposable`), fresh schema.

- `AddAsync_PersistsItem_AndReturnsNewId`
- `GetAsync_UnknownId_ReturnsNull`
- `ListAsync_FiltersByStatus`
- `ListAsync_FiltersByType`
- `ListAsync_OrdersByCreatedDesc`
- `ListAsync_PaginationReturnsRequestedSlice`
- `UpdateStatusAsync_TransitionsStatus`
- `DeleteAsync_RemovesItem`
- `CountByStatusAsync_ReturnsCorrectTotals`
- `IpHashing_SameIp_ProducesSameHash`
- `IpHashing_DifferentSalts_ProduceDifferentHashes`

### `FeedbackControllerTests`

Uses fake `IFeedbackStore` (either hand-rolled or Moq, matching whichever the existing suite uses).

- `Index_GET_ReturnsView`
- `Index_POST_HoneypotFilled_DoesNotPersist_AndRedirectsSuccess`
- `Index_POST_InvalidModel_ReturnsViewWithErrors_AndDoesNotPersist`
- `Index_POST_ValidSubmission_CallsStore_AndSetsTempDataSuccess`
- `Index_POST_ValidSubmission_CapturesReferrer_UserAgent_IpHash_AppVersion`

### `BasicAuthMiddlewareTests`

Uses `TestServer` or direct middleware invocation with fake `HttpContext`.

- `EnvVarsMissing_Returns503`
- `NoAuthHeader_Returns401WithChallenge`
- `MalformedHeader_Returns401`
- `WrongCredentials_Returns401`
- `CorrectCredentials_InvokesNext`
- `TimingAttack_ConstantTimeCompare_ApproximateDurationEqual` (best-effort)

### `AdminFeedbackControllerTests`

- `Index_RendersItemsFromStore_WithCorrectFilters`
- `Detail_UnknownId_Returns404`
- `Action_MarkRead_CallsUpdateStatusAsync_AndRedirects`
- `Action_Archive_CallsUpdateStatusAsync_AndRedirects`
- `Action_Delete_CallsDeleteAsync_AndRedirects`
- `Action_RequiresAntiforgeryToken`

### Rate limit

Integration smoke test asserting 6th submit in window returns 429. Acceptable to run in `DeckFlow.Web.Tests` against `WebApplicationFactory`, or skip in sandbox and verify manually.

## Security Notes

- HTTPS already forced via `force_https = true` in `fly.toml` and Render defaults.
- CSRF: antiforgery enforced on all POST endpoints (public + admin).
- XSS: Razor auto-encoding; admin views use `@item.Message` / `@Html.DisplayFor`, never `Html.Raw`. Admin list truncates message for display and escapes.
- IP PII: stored as salted SHA-256 hash only; raw IP never persisted.
- Basic Auth: `CryptographicOperations.FixedTimeEquals` for comparisons; `WWW-Authenticate` challenge only on wrong creds, not on misconfig; credentials never logged.
- Fail-closed admin: missing env vars → 503, no default password.
- Input limits: message 4000 chars, email 200 chars, user-agent / referrer truncated to 500 chars server-side before persist to prevent unbounded DB growth.
- Honeypot: success response indistinguishable from real submit (do not return distinct status/flash, which would let bots detect filtering).

## Rollout

1. Ship the code behind the existing deploy pipeline (Fly + Render auto-deploy).
2. Set `FEEDBACK_ADMIN_USER` / `FEEDBACK_ADMIN_PASSWORD` via `fly secrets` and Render env settings before (or on same deploy as) code landing. Without them, `/Admin/Feedback` returns 503 — public `/Feedback` form still works and data persists.
3. Verify `/Feedback` submit works in prod with a real test entry.
4. Verify `/Admin/Feedback` prompts and shows the test entry after creds set.
5. Update README with user-facing note ("Feedback link in footer") and ops note (new env vars, admin URL).

## Open Questions / Deferred

- No email notification on new submission. If admin visit frequency turns out too low, consider layering an optional SMTP notifier later.
- No user-facing ability to attach files/screenshots. Deferred; if needed later, requires blob storage decisions.
- No i18n — English only for now, matching rest of app.
