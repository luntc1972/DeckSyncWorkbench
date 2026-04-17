using System.Diagnostics;

namespace DeckFlow.Web.Infrastructure;

/// <summary>
/// Opens the development site in a browser after the application starts.
/// </summary>
public static class DevelopmentBrowserLauncher
{
    /// <summary>
    /// Opens the supplied URL in a new Chrome window when Chrome is installed, or falls back to the default browser.
    /// </summary>
    /// <param name="launchUrl">Application URL to open.</param>
    public static void OpenNewWindow(string launchUrl)
    {
        var chromePath = GetChromePath();
        if (!string.IsNullOrWhiteSpace(chromePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = chromePath,
                Arguments = $"--new-window \"{launchUrl}\"",
                UseShellExecute = true
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = launchUrl,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Resolves the first Chrome executable found in the standard Windows install locations.
    /// </summary>
    /// <returns>The absolute executable path when found; otherwise <see langword="null"/>.</returns>
    private static string? GetChromePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidates = new[]
        {
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
