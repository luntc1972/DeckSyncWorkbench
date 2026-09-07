namespace DeckFlow.Web.Tests;

/// <summary>
/// Resolves the checkout's repo root by walking up from the test binary's output directory until
/// <c>DeckFlow.sln</c> is found. Every manabase/protection harness and regression test that needs a
/// path relative to the repo root (not the test <c>bin/</c> output) shares this instead of
/// re-implementing the walk.
/// </summary>
internal static class RepoPaths
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeckFlow.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
