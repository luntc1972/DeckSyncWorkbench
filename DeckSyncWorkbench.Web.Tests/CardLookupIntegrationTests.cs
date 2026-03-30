using System.Diagnostics;
using MtgDeckStudio.Web.Services;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class CardLookupIntegrationTests
{
    private const string IntegrationFlag = "DECKSYNC_RUN_SCRYFALL_INTEGRATION";

    [Fact]
    public async Task CardLookupCli_ReturnsQuantumRiddlerText()
    {
        if (Environment.GetEnvironmentVariable(IntegrationFlag) != "1")
        {
            return;
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "run",
                "--project",
                "/mnt/c/users/chrislunt/source/personal/MtgDeckStudio/MtgDeckStudio.CLI/MtgDeckStudio.CLI.csproj",
                "--",
                "card-lookup",
                "--name",
                "Quantum Riddler"
            }
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Quantum Riddler", output);
        Assert.Contains("{3}{U}{U}", output);
        Assert.Contains("Creature — Sphinx", output);
        Assert.Contains("When this creature enters", output);
    }

    [Fact]
    public async Task CardLookupService_ResolvesPastorDaSelvaToAncientGreenwarden()
    {
        if (Environment.GetEnvironmentVariable(IntegrationFlag) != "1")
        {
            return;
        }

        var service = new ScryfallCardLookupService();

        var result = await service.LookupAsync("Pastor da Selva");

        Assert.Single(result.VerifiedOutputs);
        Assert.Contains("Ancient Greenwarden", result.VerifiedOutputs[0]);
        Assert.DoesNotContain("ERROR: Pastor da Selva", result.MissingLines);
        Assert.Empty(result.MissingLines);
    }
}
