using DeckFlow.Core.Content;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.MeasuredStyleExtraction;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Knowledge.StatedRulesExtraction;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Models;
using DeckFlow.Core.Storage;
using DeckFlow.Web.Extensions;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.CreatorStyle;
using DeckFlow.Web.Services.FeatureFlags;
using DeckFlow.Web.Services.Http;
using DeckFlow.Web.Services.Scryfall;
using DeckFlow.Web.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DeckFlow.Web.Tests.Services.CreatorStyle;

/// <summary>
/// Guards the creator-style DI graph required by Development ValidateOnBuild.
/// </summary>
// Why (WR-18): CreatorFlowDatabaseConnectionFactory reads three process-wide env vars
// (DECKFLOW_DATABASE_PROVIDER, DECKFLOW_DATABASE_CONNECTION_STRING, MTG_DATA_DIR) when resolving
// the stores below; this class was not previously in the serial collection, so xUnit could run it
// in parallel with a test that sets those variables (e.g. ProgramStartupTests) and either target
// Postgres unexpectedly or leak SQLite files outside its own temp CreatorStyleTestRoot.
[Collection("AdminEnvSerial")]
public sealed class CreatorStyleDiRegistrationTests
{
    private static readonly Type[] CreatorStyleRegistrationFloor =
    [
        typeof(ICardNameGrounder),
        typeof(ICardGroundingGuard),
        typeof(ICreatorDeckCacheStore),
        typeof(ICreatorProfileSourceStore),
        typeof(CategoryKnowledgeRepository),
        typeof(ICreatorStyleProfileStore),
        typeof(CreatorWhitelistPoolBuilder),
        typeof(ICreatorStyleSeedLoader),
        typeof(IArchidektOwnerClient),
        typeof(CreatorProfileDeckCrawler),
        typeof(CreatorDeckCategoryResolver),
        typeof(MeasuredStyleProfileBuilder),
        typeof(ISubmittedDeckStatsBuilder),
        typeof(ICreatorStylePacketService),
    ];

    private static readonly HashSet<Type> ScopedCreatorStyleServices =
    [
        typeof(IArchidektOwnerClient),
        typeof(CreatorProfileDeckCrawler),
        typeof(CreatorDeckCategoryResolver),
        typeof(MeasuredStyleProfileBuilder),
        typeof(ISubmittedDeckStatsBuilder),
        typeof(ICreatorStylePacketService),
    ];

    // Why (WR-14): ServiceCollection_ValidateOnBuild_ResolvesCreatorStyleScopedServicesWithinScope
    // used to hand-roll all fifteen registrations here instead of calling
    // AddDeckFlowCreatorStyle, so it validated a parallel copy of the DI graph - deleting a
    // registration from CreatorStyleServiceCollectionExtensions or changing a lifetime there
    // would not fail it. AddDeckFlowCreatorStyle_DescriptorDelta_ResolvesEveryCreatorStyleRegistration
    // below already resolves every descriptor the real extension adds (superset of the four
    // types the deleted test asserted), scoped ones from within a scope per
    // ScopedCreatorStyleServices, against the production AddDeckFlowCreatorStyle extension - so
    // it was deleted rather than kept as a redundant, driftable duplicate.

    [Fact]
    public void Resolve_RealArchidektOwnerClient_DoesNotThrow_WhenArchidektPipelineRegistered()
    {
        using var testRoot = CreatorStyleTestRoot.Create();
        using var providerScope = EnvScope.Clear("DECKFLOW_DATABASE_PROVIDER", "DECKFLOW_DATABASE_CONNECTION_STRING");
        using var dataDirScope = EnvScope.Set("MTG_DATA_DIR", Path.Combine(testRoot.Environment.ContentRootPath, "..", "artifacts"));
        var services = CreateCreatorStyleServiceCollection(testRoot.Environment);
        services.AddDeckFlowResiliencePipelines();
        services.AddDeckFlowCreatorStyle(testRoot.Environment);

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        using IServiceScope scope = provider.CreateScope();
        // Why: a fake IArchidektOwnerClient cannot catch the missing-pipeline failure mode because ArchidektOwnerClient throws in its constructor.
        var ownerClient = scope.ServiceProvider.GetRequiredService<IArchidektOwnerClient>();
        Assert.IsType<ArchidektOwnerClient>(ownerClient);
        Assert.IsType<CreatorProfileDeckCrawler>(scope.ServiceProvider.GetRequiredService<CreatorProfileDeckCrawler>());
    }

    [Fact]
    public void AddDeckFlowCreatorStyle_DescriptorDelta_ResolvesEveryCreatorStyleRegistration()
    {
        using var testRoot = CreatorStyleTestRoot.Create();
        using var providerScope = EnvScope.Clear("DECKFLOW_DATABASE_PROVIDER", "DECKFLOW_DATABASE_CONNECTION_STRING");
        using var dataDirScope = EnvScope.Set("MTG_DATA_DIR", Path.Combine(testRoot.Environment.ContentRootPath, "..", "artifacts"));
        var services = CreateCreatorStyleServiceCollection(testRoot.Environment);
        services.AddDeckFlowResiliencePipelines();

        var baselineCount = services.Count;
        services.AddDeckFlowCreatorStyle(testRoot.Environment);
        ServiceDescriptor[] addedDescriptors = services
            .Skip(baselineCount)
            .ToArray();

        HashSet<Type> addedServiceTypes = addedDescriptors
            .Select(descriptor => descriptor.ServiceType)
            .ToHashSet();
        var missingFloor = CreatorStyleRegistrationFloor
            .Where(serviceType => !addedServiceTypes.Contains(serviceType))
            .Select(serviceType => serviceType.Name)
            .ToArray();
        Assert.True(
            missingFloor.Length == 0,
            "Creator-style descriptor delta is missing floor registrations: " + string.Join(", ", missingFloor));

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        using IServiceScope scope = provider.CreateScope();

        foreach (ServiceDescriptor descriptor in addedDescriptors)
        {
            if (descriptor.ServiceType.IsGenericTypeDefinition || IsHttpClientFactoryPlumbing(descriptor.ServiceType))
            {
                continue;
            }

            IServiceProvider resolver = ScopedCreatorStyleServices.Contains(descriptor.ServiceType)
                ? scope.ServiceProvider
                : provider;
            object? resolved = resolver.GetRequiredService(descriptor.ServiceType);
            Assert.NotNull(resolved);
        }
    }

    private static ServiceCollection CreateCreatorStyleServiceCollection(IWebHostEnvironment environment)
    {
        var services = new ServiceCollection();

        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddMemoryCache();
        services.AddOptions();
        services.AddSingleton<IFeatureFlagCache, FakeFeatureFlagCache>();
        services.AddSingleton<ContentKbArtifactPathResolver>();
        services.AddSingleton<PacketSessionCache>();
        services.AddSingleton<IArchidektDeckImporter, ArchidektApiDeckImporter>();
        services.AddScoped<IDeckEntryLoader, FakeDeckEntryLoader>();

        services.AddDeckFlowHttpClients();
        services.AddDeckFlowScryfallServices();

        return services;
    }

    private static bool IsHttpClientFactoryPlumbing(Type serviceType)
    {
        if (serviceType == typeof(IHttpClientFactory)
            || serviceType == typeof(IHttpMessageHandlerFactory))
        {
            return true;
        }

        if (!serviceType.IsGenericType)
        {
            return false;
        }

        Type genericTypeDefinition = serviceType.GetGenericTypeDefinition();
        Type[] genericArguments = serviceType.GetGenericArguments();
        return genericArguments.Length == 1
            && genericArguments[0] == typeof(Microsoft.Extensions.Http.HttpClientFactoryOptions)
            && (genericTypeDefinition == typeof(Microsoft.Extensions.Options.IConfigureOptions<>)
                || genericTypeDefinition == typeof(Microsoft.Extensions.Options.IOptionsChangeTokenSource<>));
    }

    private sealed class CreatorStyleTestRoot : IDisposable
    {
        private readonly string _parentDirectory;

        private CreatorStyleTestRoot(string parentDirectory, FakeWebHostEnvironment environment)
        {
            _parentDirectory = parentDirectory;
            Environment = environment;
        }

        public FakeWebHostEnvironment Environment { get; }

        public static CreatorStyleTestRoot Create()
        {
            var parentDirectory = Path.Combine(Path.GetTempPath(), "deckflow-creator-style-di", Guid.NewGuid().ToString("N"));
            var contentRootPath = Path.Combine(parentDirectory, "app");

            Directory.CreateDirectory(Path.Combine(contentRootPath, "content-kb", "seed"));
            Directory.CreateDirectory(Path.Combine(parentDirectory, "artifacts"));

            return new CreatorStyleTestRoot(parentDirectory, new FakeWebHostEnvironment(contentRootPath));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_parentDirectory))
            {
                Directory.Delete(_parentDirectory, recursive: true);
            }
        }
    }

    private sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeckFlow.Web.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string WebRootPath { get; set; } = contentRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeFeatureFlagCache : IFeatureFlagCache
    {
        public bool IsEnabled(string key) => true;

        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyDictionary<string, bool> Snapshot() => new Dictionary<string, bool>(0, StringComparer.Ordinal);
    }

    private sealed class FakeDeckEntryLoader : IDeckEntryLoader
    {
        public Task<List<DeckEntry>> LoadAsync(DeckLoadRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<DeckEntry>());

        public Task<DeckSourceLoadResult> LoadFromSourceAsync(
            string deckSource,
            UnrecognizedPasteBehavior unrecognizedBehavior = UnrecognizedPasteBehavior.ThrowNotRecognized,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DeckSourceLoadResult(new List<DeckEntry>(), null));

        public void ValidateCommanderDeckSize(string systemName, IReadOnlyList<DeckEntry> entries, int requiredDeckSize = 100)
        {
        }
    }

}
