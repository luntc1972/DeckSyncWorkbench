using System.Reflection;
using System.Text.RegularExpressions;
using DeckFlow.Web.Services.FeatureFlags;
using DeckFlow.Web.Services.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeckFlow.Web.Tests.Tools;

/// <summary>
/// Guards the tool-flag seed contract for SQLite and registry alignment.
/// </summary>
public sealed class ToolFlagSeedConsistencyTests : IDisposable
{
    // Why: some tool flags are intentionally dark-launched (seeded present but disabled
    // so the UI stays byte-identical before the operator flips them on):
    // tool.primer.stale-flag (PRIMER-01, phase 78), tool.cut-lab.enabled (phase 101), and
    // tool.deck-modules.enabled (Modular Deck Compiler Phase 2, seeded OFF by plan requirement).
    // Bracket Check (BRACKET-05) and Deck History left dark launch and now seed ON.
    // All other tool flags default to enabled.
    private static readonly HashSet<string> DarkLaunchedFlags =
    [
        "tool.primer.stale-flag",
        "tool.cut-lab.enabled",
        "tool.deck-modules.enabled",
    ];

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tool-flags-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public async Task EnsureSchemaAsync_SeedsAllNewToolFlags_AndPreservesExistingOverrides()
    {
        var store = new FeatureFlagStore(_databasePath);
        var expectedKeys = GetSeedKeys("SqliteSeedSql");

        await store.EnsureSchemaAsync();

        var seeded = await store.GetAllAsync();

        Assert.Equal(19, expectedKeys.Count);
        Assert.All(expectedKeys, key =>
        {
            Assert.True(seeded.TryGetValue(key, out var enabled), $"Missing seeded key '{key}'.");
            if (DarkLaunchedFlags.Contains(key))
                Assert.False(enabled, $"'{key}' is a dark-launched tool flag: seeded present but disabled.");
            else
                Assert.True(enabled, $"Seeded key '{key}' should default to enabled.");
        });

        await store.SetEnabledAsync("tool.deck-primer.enabled", false);
        await store.EnsureSchemaAsync();

        var afterRerun = await store.GetAllAsync();
        Assert.False(afterRerun["tool.deck-primer.enabled"]);
    }

    [Fact]
    public void RegistryFlagKeys_AreSeeded()
    {
        var allowedKeys = GetSeedKeys("SqliteSeedSql");
        allowedKeys.UnionWith(GetSeedKeys("PostgresSeedSql"));

        var registry = new ToolRegistry();

        Assert.All(registry.All, tool => Assert.Contains(tool.FlagKey, allowedKeys));
    }

    [Fact]
    public async Task AnalysisInteractionAuditFlag_SeededOff_InBothDialects()
    {
        var sqliteAnalysisKeys = GetSeedKeysWithPrefix("SqliteSeedSql", "analysis.");
        var postgresAnalysisKeys = GetSeedKeysWithPrefix("PostgresSeedSql", "analysis.");

        Assert.Contains("analysis.interaction-audit", sqliteAnalysisKeys);
        Assert.Contains("analysis.interaction-audit", postgresAnalysisKeys);

        var store = new FeatureFlagStore(_databasePath);
        await store.EnsureSchemaAsync();

        var seeded = await store.GetAllAsync();
        Assert.True(seeded.TryGetValue("analysis.interaction-audit", out var enabled), "Missing seeded key 'analysis.interaction-audit'.");
        Assert.False(enabled);

        var field = typeof(FeatureFlagStore).GetField("PostgresSeedSql", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var postgresSql = Assert.IsType<string>(field!.GetRawConstantValue());
        Assert.Contains("('analysis.interaction-audit', FALSE)", postgresSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalysisWinConMapFlag_SeededOff_InBothDialects()
    {
        var sqliteAnalysisKeys = GetSeedKeysWithPrefix("SqliteSeedSql", "analysis.");
        var postgresAnalysisKeys = GetSeedKeysWithPrefix("PostgresSeedSql", "analysis.");

        Assert.Contains("analysis.wincon-map", sqliteAnalysisKeys);
        Assert.Contains("analysis.wincon-map", postgresAnalysisKeys);

        var store = new FeatureFlagStore(_databasePath);
        await store.EnsureSchemaAsync();

        var seeded = await store.GetAllAsync();
        Assert.True(seeded.TryGetValue("analysis.wincon-map", out var enabled), "Missing seeded key 'analysis.wincon-map'.");
        Assert.False(enabled);

        var field = typeof(FeatureFlagStore).GetField("PostgresSeedSql", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var postgresSql = Assert.IsType<string>(field!.GetRawConstantValue());
        Assert.Contains("('analysis.wincon-map', FALSE)", postgresSql, StringComparison.Ordinal);
    }

    private static HashSet<string> GetSeedKeys(string fieldName)
    {
        var field = typeof(FeatureFlagStore).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var sql = Assert.IsType<string>(field!.GetRawConstantValue());
        return Regex.Matches(sql, @"'(?<key>tool\.[^']+)'")
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> GetSeedKeysWithPrefix(string fieldName, string prefix)
    {
        var field = typeof(FeatureFlagStore).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var sql = Assert.IsType<string>(field!.GetRawConstantValue());
        return Regex.Matches(sql, @"'(?<key>" + Regex.Escape(prefix) + @"[^']+)'")
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
