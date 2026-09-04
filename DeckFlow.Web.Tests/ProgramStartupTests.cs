using DeckFlow.Web.Services;
using DeckFlow.Web.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Startup-specific tests for the web composition root.
/// </summary>
[Collection("AdminEnvSerial")]
public sealed class ProgramStartupTests
{
    [Fact]
    public async Task AwaitStartupSeedTasksAsync_WhenBothSeedTasksFault_LogsEachFailureBeforeRethrow()
    {
        var logger = new FakeLogger<Program>();
        var contentException = new InvalidOperationException("Malformed content seed.");
        var creatorException = new InvalidOperationException("Malformed creator-style seed.");

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            Program.AwaitStartupSeedTasksAsync(
                Task.FromException(contentException),
                Task.FromException(creatorException),
                logger));

        Assert.Same(contentException, exception);
        Assert.Collection(
            logger.Entries,
            entry =>
            {
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Contains("contentKbSeedTask", entry.Message, StringComparison.Ordinal);
            },
            entry =>
            {
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Contains("creatorStyleSeedTask", entry.Message, StringComparison.Ordinal);
            });
    }

    // Why (WR-11, 112-REVIEWS.md): IsCreatorStyleEnabled gates whether Program.Main registers
    // AddDeckFlowCreatorStyle at all. This targets the pure env-var-parsing helper directly
    // (same seam pattern as DeriveCloudflareClientIp/DeriveFeedbackPartitionKey below, covered by
    // ForwardedHeadersOptionsTests.cs) rather than booting the full app, since Program.Main's DI
    // wiring is not otherwise exercised by a WebApplicationFactory in this suite.
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("not-a-bool", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void IsCreatorStyleEnabled_ParsesEnvironmentVariable(string? rawValue, bool expected)
    {
        using var env = rawValue is null
            ? EnvScope.Clear("DECKFLOW_CREATOR_STYLE_ENABLED")
            : EnvScope.Set("DECKFLOW_CREATOR_STYLE_ENABLED", rawValue);
        Assert.Equal(expected, Program.IsCreatorStyleEnabled());
    }

    [Fact]
    public async Task LoadCreatorStyleSeedIfEnabledAsync_WhenLoaderIsNotRegistered_ReturnsZeroWithoutThrowing()
    {
        using var env = EnvScope.Clear("DECKFLOW_CREATOR_STYLE_ENABLED");
        await using var services = new ServiceCollection().BuildServiceProvider();

        Assert.Equal(0, await Program.LoadCreatorStyleSeedIfEnabledAsync(services));
    }

    // Why (IN-01): CompositionRoot_WithCreatorStyleDisabled_DoesNotRegisterSeedLoader was deleted.
    // It re-implemented the gate inside its own body (`if (Program.IsCreatorStyleEnabled()) ...`),
    // so with EnvScope.Clear guaranteeing the gate false, the `if` never ran and Assert.Null was
    // trivially true — deleting the real gate from Program.cs would not fail it. It exercised the
    // test's own copy of the logic, not Program.Main's real registration. The only real behavior
    // (the env-var parse) is already covered by IsCreatorStyleEnabled_ParsesEnvironmentVariable
    // above.

    private sealed class RecordingCreatorStyleSeedLoader : ICreatorStyleSeedLoader
    {
        public int CallCount { get; private set; }

        public Task<int> LoadIfPresentAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(3);
        }
    }

    [Fact]
    public async Task LoadCreatorStyleSeedIfEnabledAsync_WhenLoaderIsRegistered_InvokesLoaderAndReturnsItsCount()
    {
        var loader = new RecordingCreatorStyleSeedLoader();
        await using var services = new ServiceCollection()
            .AddSingleton<ICreatorStyleSeedLoader>(loader)
            .BuildServiceProvider();

        var result = await Program.LoadCreatorStyleSeedIfEnabledAsync(services);

        Assert.Equal(1, loader.CallCount);
        Assert.Equal(3, result);
    }
}
