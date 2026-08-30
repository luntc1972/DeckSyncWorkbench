using DeckFlow.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DeckFlow.Web.Services;

/// <summary>
/// Creates relational database handles for DeckFlow stores, choosing SQLite artifacts by default or Postgres from environment configuration while small operational stores share the feedback database.
/// </summary>
public static class DeckFlowDatabaseConnectionFactory
{
    private const string DatabaseProviderEnvVar = "DECKFLOW_DATABASE_PROVIDER";
    private const string DatabaseConnectionStringEnvVar = "DECKFLOW_DATABASE_CONNECTION_STRING";

    /// <summary>
    /// Returns the relational connection used by feedback persistence.
    /// </summary>
    /// <param name="environment">Web host environment used to resolve local artifact paths.</param>
    public static RelationalDatabaseConnection CreateFeedbackConnection(IWebHostEnvironment environment)
        => CreateConnection(environment, "feedback.db");

    /// <summary>
    /// Returns the relational connection used by AdminBruteForceTrackerStore (Phase 5 BUG-02).
    /// In production, shares the feedback Postgres connection (single logical DB; the
    /// admin_brute_force_buckets table sits alongside feedback). In local-dev SQLite, also
    /// shares the feedback.db file so we don't multiply SQLite files for tiny tables.
    /// </summary>
    public static RelationalDatabaseConnection CreateAdminThrottleConnection(IWebHostEnvironment environment)
        => CreateFeedbackConnection(environment);

    /// <summary>
    /// Returns the relational connection used by FeatureFlagStore (Phase 6, FLAG-01).
    /// Shares the feedback Postgres connection in production (single logical DB; the
    /// feature_flags table sits alongside feedback and admin_brute_force_buckets, per D-07).
    /// In local-dev SQLite, also shares the feedback.db file.
    /// </summary>
    public static RelationalDatabaseConnection CreateFeatureFlagConnection(IWebHostEnvironment environment)
        => CreateFeatureFlagConnection(ResolveArtifactsPath(environment));

    /// <summary>
    /// Returns the relational connection used by FeatureFlagStore for a caller-resolved artifacts path.
    /// </summary>
    /// <param name="artifactsPath">Directory containing local SQLite artifact databases.</param>
    public static RelationalDatabaseConnection CreateFeatureFlagConnection(string artifactsPath)
        => CreateConnection(artifactsPath, "feedback.db");

    /// <summary>
    /// Returns the relational connection used by the Phase 7 harvest stores.
    /// Harvest state shares the feedback Postgres connection in production and the
    /// feedback.db SQLite file in local-dev because the harvest tables are tiny-row
    /// operational metadata (D-07 / RESEARCH Q1 RESOLVED).
    /// </summary>
    public static RelationalDatabaseConnection CreateHarvestStateConnection(IWebHostEnvironment environment)
        => CreateFeedbackConnection(environment);

    /// <summary>
    /// Returns the relational connection used by the category knowledge cache.
    /// </summary>
    /// <param name="environment">Web host environment used to resolve local artifact paths.</param>
    public static RelationalDatabaseConnection CreateCategoryKnowledgeConnection(IWebHostEnvironment environment)
        => CreateConnection(environment, "category-knowledge.db");

    /// <summary>
    /// Returns the relational connection used by the manabase baseline store. Co-locates with the
    /// category-knowledge database because the baseline is derived from that crawl corpus and the
    /// Phase 3 aggregation job reads the corpus and writes the baseline together.
    /// </summary>
    /// <param name="environment">Web host environment used to resolve local artifact paths.</param>
    public static RelationalDatabaseConnection CreateManabaseBaselineConnection(IWebHostEnvironment environment)
        => CreateCategoryKnowledgeConnection(environment);

    /// <summary>
    /// Returns the always-SQLite Content KB connection, ignoring the provider environment because transcripts,
    /// audio, and spend data are local-only and must never be uploaded to Render (D-14).
    /// </summary>
    public static RelationalDatabaseConnection CreateLocalContentKbConnection(IWebHostEnvironment environment)
    {
        var artifactsPath = ResolveArtifactsPath(environment);
        Directory.CreateDirectory(artifactsPath);
        return RelationalDatabaseConnection.FromSqlitePath(Path.Combine(artifactsPath, "content-kb.db"));
    }

    /// <summary>
    /// Returns the relational connection used by the creator deck cache.
    /// </summary>
    /// <param name="environment">Web host environment used to resolve local artifact paths.</param>
    public static RelationalDatabaseConnection CreateCreatorDeckCacheConnection(IWebHostEnvironment environment)
        => CreateConnection(environment, "creator-deck-cache.db");

    /// <summary>
    /// Returns the provider-aware content site-index connection, the only Render-bound content shape for the
    /// slim index (D-12/D-14).
    /// </summary>
    public static RelationalDatabaseConnection CreateContentSiteIndexConnection(IWebHostEnvironment environment)
        => CreateConnection(environment, "content-site-index.db");

    private static RelationalDatabaseConnection CreateConnection(IWebHostEnvironment environment, string sqliteFileName)
        => CreateConnection(ResolveArtifactsPath(environment), sqliteFileName);

    private static RelationalDatabaseConnection CreateConnection(string artifactsPath, string sqliteFileName)
    {
        var providerText = Environment.GetEnvironmentVariable(DatabaseProviderEnvVar);
        if (string.IsNullOrWhiteSpace(providerText))
        {
            return RelationalDatabaseConnection.FromSqlitePath(Path.Combine(artifactsPath, sqliteFileName));
        }

        if (!Enum.TryParse<RelationalDatabaseProvider>(providerText, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException(
                $"Unsupported {DatabaseProviderEnvVar} value '{providerText}'. Supported values: Sqlite, Postgres.");
        }

        var configuredConnectionString = Environment.GetEnvironmentVariable(DatabaseConnectionStringEnvVar);
        if (provider == RelationalDatabaseProvider.Postgres)
        {
            if (string.IsNullOrWhiteSpace(configuredConnectionString))
            {
                throw new InvalidOperationException(
                    $"{DatabaseConnectionStringEnvVar} is required when {DatabaseProviderEnvVar}=Postgres.");
            }

            return new RelationalDatabaseConnection(
                RelationalDatabaseProvider.Postgres,
                NormalizePostgresConnectionString(configuredConnectionString));
        }

        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return RelationalDatabaseConnection.FromSqlitePath(Path.Combine(artifactsPath, sqliteFileName));
        }

        var sqliteConnectionString = configuredConnectionString.Contains('=', StringComparison.Ordinal)
            ? configuredConnectionString
            : new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(configuredConnectionString) }.ToString();
        return new RelationalDatabaseConnection(RelationalDatabaseProvider.Sqlite, sqliteConnectionString);
    }

    internal static string NormalizePostgresConnectionString(string raw)
        => PostgresConnectionStringNormalizer.Normalize(raw);

    private static string ResolveArtifactsPath(IWebHostEnvironment environment)
    {
        var dataDir = Environment.GetEnvironmentVariable("MTG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return Path.GetFullPath(dataDir);
        }

        return Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "artifacts"));
    }
}
