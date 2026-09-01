using Microsoft.Extensions.Logging;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Startup-specific tests for the web composition root.
/// </summary>
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

    // Why (WR-11, 112-REVIEW.md): IsCreatorStyleEnabled gates whether Program.Main registers
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
        const string variableName = "DECKFLOW_CREATOR_STYLE_ENABLED";
        var originalValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, rawValue);
            Assert.Equal(expected, Program.IsCreatorStyleEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
        }
    }
}
