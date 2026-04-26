# Coding Conventions

**Analysis Date:** 2026-04-26

## Naming Patterns

**Files:**
- C# classes: PascalCase matching the type name, one type per file (e.g., `CardLookupService.cs`, `DeckEntry.cs`)
- Interfaces: `I`-prefix PascalCase in same file as primary implementation (e.g., `ICardLookupService` in `CardLookupService.cs`)
- Result records: `{Verb}Result` suffix (e.g., `CardLookupResult`, `DeckSyncResult`, `MechanicLookupResult`)
- Request records: `{Noun}Request` suffix (e.g., `DeckDiffRequest`, `ChatGptDeckRequest`)
- View models: `{Page}ViewModel` suffix (e.g., `CardLookupViewModel`, `ChatGptDeckViewModel`)
- Test files: `{ClassName}Tests.cs` exactly matching the subject class (e.g., `CardLookupServiceTests.cs`)
- JS modules: kebab-case with `df-` prefix for shared widgets (e.g., `df-select.js`, `df-typeahead.js`), feature names otherwise (e.g., `card-lookup.js`, `deck-sync.js`)

**Types (C#):**
- Classes: `public sealed class` by default — `sealed` is the default, not the exception
- Interfaces: plain `public interface` (no `sealed`)
- Data transfer objects: `public sealed record` with positional or init-only properties
- Enums: PascalCase members (e.g., `DeckPageTab.CardLookup`, `DeckInputSource.PublicUrl`)

**Functions/Methods:**
- PascalCase for all public and private methods
- Async methods: `{Verb}Async` suffix (e.g., `LookupAsync`, `CompareDecksAsync`, `BuildAsync`)
- Private helpers: descriptive verbs like `LoadLeftEntriesAsync`, `TryParseEntry`, `TryGetBoardHeader`
- Boolean-returning helpers: `Try` prefix for `out`-param pattern, `Is`/`Has` prefix for predicates (e.g., `TryGetDeckId`, `IsStoppingLine`, `IsIgnorableLine`)

**Variables:**
- Local variables: camelCase
- Private fields: `_camelCase` underscore prefix (e.g., `_deckSyncService`, `_logger`)
- Constants: PascalCase for `private const` (e.g., `CollectionBatchSize`, `MaxCardsPerSubmission`)
- Static readonly: PascalCase (e.g., `SuggestionTimeout`, `QuantityPrefixRegex`)

**JavaScript:**
- Module-level: `camelCase` constants and functions
- DOM helpers: verb-noun style (e.g., `hideLookupSuggestionPanel`, `createTypeaheadPanel`, `attachTypeahead`)
- Modules wrapped in IIFE: `(() => { 'use strict'; ... })()`

## Code Style

**Formatting:**
- No `.editorconfig` or `.prettierrc` detected — formatting appears to follow Visual Studio defaults
- 4-space indentation (C#), 4-space indentation (JS)
- `using` directives: explicit `using System.*` listed; global usings `enable` in project files (xunit implicitly imported in test projects)

**Linting:**
- No ESLint or Biome config detected for JS
- C# uses nullable reference types enabled (`<Nullable>enable</Nullable>`) and implicit usings enabled in all projects

**Null handling:**
- Nullable reference types enforced (`?` annotation required for nullable)
- `ArgumentNullException.ThrowIfNull(x)` — preferred guard pattern
- `ArgumentException.ThrowIfNullOrWhiteSpace(x)` — preferred for string guards
- Null-coalescing `??` and null-conditional `?.` used freely in JS

## Import Organization

**C# using order (observed pattern):**
1. `System.*` framework namespaces
2. Third-party namespaces (e.g., `RestSharp`, `Microsoft.AspNetCore.*`)
3. Internal project namespaces (`DeckFlow.Core.*`, `DeckFlow.Web.*`)

**No path aliases** — standard .NET project references used

## Error Handling

**Service layer (Core):**
- Throws `DeckParseException` for parse failures: `DeckFlow.Core/Parsing/DeckParseException.cs`
- Throws `InvalidOperationException` for business-rule violations (e.g., deck too large, bad URL)
- Throws `HttpRequestException` with `StatusCode` populated for upstream HTTP failures
- Uses `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace` for parameter guards

**Controller layer (Web):**
- Catches `InvalidOperationException` → renders view with `model.ErrorMessage` set (user-visible)
- Catches `HttpRequestException` → renders view with `model.ErrorMessage` or returns `StatusCode(503)`
- Catches `DeckParseException` → same view-with-error pattern
- `OperationCanceledException` caught separately to distinguish client abort from internal timeout
- Pattern: `catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)`
- `UpstreamErrorMessageBuilder` (`DeckFlow.Web/Services/UpstreamErrorMessageBuilder.cs`) translates raw exceptions to user-facing strings, site-specific where possible (Scryfall, Moxfield, Archidekt, EDHREC)

**API endpoints:**
- Validation failures: return `View(viewName, model)` with `ErrorMessage` set (never throw to global handler)
- HTTP failures: return `StatusCode(StatusCodes.Status503ServiceUnavailable, new { Message = ... })`

## Logging

**Framework:** `ILogger<T>` via ASP.NET Core DI (injected as `_logger`)

**Patterns:**
- `LogInformation` — validation failures, expected user errors (e.g., missing field, bad input)
- `LogWarning` — upstream dependency failures (Scryfall, Moxfield unavailable)
- Structured logging with named parameters: `_logger.LogWarning(exception, "Search failed for {Query}.", q)`
- No `LogError` or `LogCritical` observed in controller — upstream failures are warnings, not errors

## Comments / Documentation

**XML doc comments:**
- All public classes, interfaces, methods, and constructors have `/// <summary>` doc comments
- Constructor summaries describe dependencies injected
- Method summaries describe what the method does and any notable behavior
- `<param>` tags used on multi-param public methods

**Inline comments:**
- Rare — code is self-explanatory with descriptive names; comments reserved for non-obvious logic

## Function Design

**Size:** Most methods are short (10–30 lines). Large orchestration methods (controller actions) kept readable via extracted private helpers.

**Parameters:** Constructor injection via DI; method parameters kept minimal. `CancellationToken cancellationToken = default` appended to all async service methods.

**Return Values:**
- Service operations return typed result records (e.g., `CardLookupResult`, `DeckSyncResult`)
- Boolean lookups use `TryGet` / `out` parameter pattern
- Controller actions return `IActionResult` / `Task<IActionResult>`
- Nullable return: `T?` with XML doc clarifying when null is returned

## Module Design

**Exports (C#):**
- Interface + implementation co-located in same file (e.g., `ICardLookupService` and `ScryfallCardLookupService` both in `CardLookupService.cs`)
- Result records co-located with their service interface in same file
- No barrel files — .NET project references used

**Dependency injection:**
- Services registered by interface: `ICardLookupService` → `ScryfallCardLookupService`
- `sealed` on all implementation classes
- `NullLogger<T>.Instance` used in tests to satisfy `ILogger<T>` dependency

## Async Conventions

- All I/O-bound operations are `async Task<T>`
- `ConfigureAwait(false)` used consistently in Core library (102 call sites observed); not required in MVC controllers
- `HttpContext.RequestAborted` passed as `CancellationToken` from controller to service

---

*Convention analysis: 2026-04-26*
