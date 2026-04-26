# Testing Patterns

**Analysis Date:** 2026-04-26

## Test Framework

**Runner:**
- xUnit 2.9.3 with `xunit.runner.visualstudio` 3.1.4
- Config: `DeckFlow.Core.Tests/DeckFlow.Core.Tests.csproj`, `DeckFlow.Web.Tests/DeckFlow.Web.Tests.csproj`

**Assertion Library:**
- xUnit built-in (`Assert.*`) — no FluentAssertions or Shouldly

**Coverage:**
- `coverlet.collector` 6.0.4 — present in Core.Tests project only

**Run Commands:**
```bash
dotnet test                                     # Run all tests (both projects)
dotnet test DeckFlow.Core.Tests                 # Core tests only
dotnet test DeckFlow.Web.Tests                  # Web tests only
dotnet test --collect:"XPlat Code Coverage"    # With coverage (Core.Tests)
```

## Test File Organization

**Location:**
- Separate test projects: `DeckFlow.Core.Tests/` and `DeckFlow.Web.Tests/`
- Flat — all test files at project root, no subdirectory grouping (except `TestDoubles/`)
- One test class per subject class

**Naming:**
- Test class: `{SubjectClass}Tests` (e.g., `ParserTests`, `DeckControllerTests`, `ScryfallThrottleTests`)
- Test method: `{MethodUnderTest}_{Scenario}_{ExpectedOutcome}` (e.g., `LookupAsync_PreservesQuantities_AndCollectsMissingLines`)
- For GET/POST pairs: `{ActionName}_Get_{Outcome}` / `{ActionName}_Post_{Outcome}`

**Structure:**
```
DeckFlow.Core.Tests/
├── ParserTests.cs
├── FilteringTests.cs
├── DiffEngineTests.cs
├── ReportingTests.cs
└── ...

DeckFlow.Web.Tests/
├── DeckControllerTests.cs
├── CardLookupServiceTests.cs
├── ScryfallThrottleTests.cs
├── BasicAuthMiddlewareTests.cs
├── UpstreamErrorMessageBuilderTests.cs
├── TestDoubles/
│   └── FakeCategoryKnowledgeStore.cs
└── ...
```

## Test Structure

**Suite Organization:**
```csharp
public sealed class ParserTests
{
    [Fact]
    public void MethodName_Scenario_ExpectedResult()
    {
        // Arrange
        var subject = new SubjectClass();

        // Act
        var result = subject.Method(input);

        // Assert
        Assert.Equal(expected, result);
    }
}
```

**Patterns:**
- `public sealed class` for all test classes — consistent with production code style
- No constructor setup — each test is fully self-contained (no `[ClassFixture]` or `[Collection]` observed)
- No `[Theory]` / `[InlineData]` — each case is a separate `[Fact]`
- Arrange/Act/Assert implicit (no comment dividers)
- `Assert.Single(collection)` preferred over `Assert.Equal(1, collection.Count)`
- `Assert.IsType<T>(result)` used to simultaneously assert type and obtain cast value

## Mocking

**Framework:** No mocking library (no Moq, NSubstitute, or FakeItEasy)

**Pattern: Hand-rolled fakes nested inside the test class as `private sealed class`**

```csharp
// In DeckControllerTests.cs — all test doubles declared as nested private sealed classes:

private sealed class FakeCardLookupService : ICardLookupService
{
    public Task<CardLookupResult> LookupAsync(string cardList, CancellationToken cancellationToken = default)
        => Task.FromResult(new CardLookupResult(Array.Empty<string>(), Array.Empty<string>()));

    public Task<SingleCardLookupResult?> LookupSingleAsync(string cardName, CancellationToken cancellationToken = default)
        => Task.FromResult<SingleCardLookupResult?>(null);
}

private sealed class ThrowingCardLookupService : ICardLookupService
{
    private readonly Exception _exception;

    public ThrowingCardLookupService(Exception exception)
    {
        _exception = exception;
    }

    public Task<CardLookupResult> LookupAsync(string cardList, CancellationToken cancellationToken = default)
        => Task.FromException<CardLookupResult>(_exception);

    public Task<SingleCardLookupResult?> LookupSingleAsync(string cardName, CancellationToken cancellationToken = default)
        => Task.FromException<SingleCardLookupResult?>(_exception);
}

private sealed class CapturingChatGptDeckPacketService : IChatGptDeckPacketService
{
    public ChatGptDeckRequest? LastRequest { get; private set; }

    public Task<ChatGptDeckPacketResult> BuildAsync(ChatGptDeckRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(new ChatGptDeckPacketResult(...));
    }
}
```

**Test double naming conventions:**
- `Fake{Interface}` — returns safe no-op / empty result
- `Throwing{Interface}` — accepts exception in constructor, always throws it
- `Successful{Interface}` — returns a happy-path result with realistic data
- `Configurable{Interface}` — accepts result in constructor, returns it
- `Capturing{Interface}` — records the last call's arguments for assertion

**For services with HTTP dependencies (e.g., `ScryfallCardLookupService`):**
- Constructor accepts optional `Func<RestRequest, CancellationToken, Task<RestResponse<T>>>` delegates
- Tests inject lambda fakes directly without any mocking framework:
```csharp
var service = new ScryfallCardLookupService(
    executeAsync: (request, _) => Task.FromResult(CreateCollectionResponse(...)));
```

**What to Mock:**
- All external HTTP dependencies (Scryfall, Moxfield, Archidekt, EDHREC)
- Services injected into controllers
- File system access

**What NOT to Mock:**
- Pure logic (parsers, normalizers, diff engine) — tested directly with real inputs
- Static helper classes (`UpstreamErrorMessageBuilder`, `CategorySuggestionMessageBuilder`) — called directly

## Fixtures and Factories

**Test Data:**
- Inline literals in each test — no shared fixture files
- Raw deck text uses C# raw string literals (`"""..."""`) for multi-line inputs:
```csharp
var entries = new MoxfieldParser().ParseText("""
    Commander
    1 Atraxa, Praetors' Voice (MH2) 17 *F*

    1 Arcane Signet
    """);
```

**Factory Helpers:**
- `private static RestResponse<T> CreateResponse<T>(HttpStatusCode statusCode, params (string name, string value)[] headers)` — defined inside the test class for HTTP response construction

**Shared Test Doubles:**
- `DeckFlow.Web.Tests/TestDoubles/FakeCategoryKnowledgeStore.cs` — one shared fake for the category knowledge store (used across multiple test classes)
- All other fakes are nested private classes inside the test class that uses them

**Environment Isolation:**
```csharp
// EnvScope pattern for environment variable manipulation (in BasicAuthMiddlewareTests):
using var _ = EnvScope.Set(EnvUser, "admin", EnvPass, "secret");
// Scope auto-restores previous values on Dispose
```

## Coverage

**Requirements:** No enforced coverage threshold detected

**View Coverage:**
```bash
dotnet test --collect:"XPlat Code Coverage"
# Output in TestResults/ directory as coverage.cobertura.xml
```

## Test Types

**Unit Tests:**
- Parser tests: pure text-in, list-out, no dependencies (`DeckFlow.Core.Tests/ParserTests.cs`)
- Filter/diff/reporting tests: pure logic, real inputs (`FilteringTests.cs`, `DiffEngineTests.cs`, `ReportingTests.cs`)
- Service unit tests: inject fakes via constructor, test one behavior per `[Fact]`

**Integration / Behavioral Tests:**
- Controller tests (`DeckControllerTests.cs`): instantiate real `DeckController` with fakes, verify `IActionResult` shape and `ViewModel` state
- Service tests with delegate injection (`CardLookupServiceTests.cs`): real service class, fake HTTP layer

**No E2E Tests** — no Playwright, Selenium, or browser-driving framework detected

## Common Patterns

**Async Testing:**
```csharp
[Fact]
public async Task MethodName_Scenario_Outcome()
{
    var result = await controller.ActionAsync(request);
    var view = Assert.IsType<ViewResult>(result);
    Assert.Equal("expected", view.ViewName);
}
```

**Error / Exception Testing:**
```csharp
// Assert exception thrown:
var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LookupAsync(lines));
Assert.Equal("expected message", exception.Message);

// Assert exception suppressed and surfaced as model error:
var result = await controller.Action(request);
var view = Assert.IsType<ViewResult>(result);
var model = Assert.IsType<SomeViewModel>(view.Model);
Assert.Equal("expected user message", model.ErrorMessage);
```

**HTTP Status Code Testing:**
```csharp
var objectResult = Assert.IsType<ObjectResult>(result);
Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
var message = payload.GetType().GetProperty("Message")?.GetValue(payload) as string;
Assert.Equal("Scryfall returned HTTP 503. Try again shortly.", message);
```

**Assert.All for multi-case checks without `[Theory]`:**
```csharp
Assert.All(new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable }, statusCode =>
{
    var exception = Assert.Throws<HttpRequestException>(() => ScryfallThrottle.ThrowIfUpstreamUnavailable(statusCode));
    Assert.Equal(statusCode, exception.StatusCode);
});
```

**Timing / pacing tests:**
```csharp
await Task.Delay(250); // Isolate from prior test state
var stopwatch = Stopwatch.StartNew();
// ... execute operations ...
Assert.True(stopwatch.ElapsedMilliseconds >= 180, $"Expected at least ~180ms, saw {stopwatch.ElapsedMilliseconds}ms.");
```

---

*Testing analysis: 2026-04-26*
