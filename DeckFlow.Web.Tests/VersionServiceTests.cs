using System.Reflection;
using DeckFlow.Web.Services;
using Xunit;

namespace DeckFlow.Web.Tests;

public class VersionServiceTests
{
    [Fact]
    public void GetVersion_returns_non_empty_string()
    {
        var service = new VersionService(typeof(VersionServiceTests).Assembly);
        Assert.False(string.IsNullOrWhiteSpace(service.GetVersion()));
    }

    [Fact]
    public void GetVersion_prefers_InformationalVersion_and_strips_commit_suffix()
    {
        var asm = typeof(VersionServiceTests).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is null) return;

        var plus = informational.IndexOf('+');
        var expected = plus >= 0 ? informational[..plus] : informational;

        var service = new VersionService(asm);

        Assert.Equal(expected, service.GetVersion());
    }
}
