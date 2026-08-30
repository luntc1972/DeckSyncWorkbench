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
        typeof(CreatorProfileDeckCrawler),
        typeof(CreatorDeckCategoryResolver),
        typeof(MeasuredStyleProfileBuilder),
        typeof(ISubmittedDeckStatsBuilder),
        typeof(ICreatorStylePacketService),
    ];

    [Fact]
    public void ServiceCollection_ValidateOnBuild_ResolvesCreatorStyleScopedServicesWithinScope()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "deckflow-98-05-di", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<ICreatorDeckCacheStore>(_ =>
                new CreatorDeckCacheStore(RelationalDatabaseConnection.FromSqlitePath(Path.Combine(tempDirectory, "creator-deck-cache.db"))));
            services.AddSingleton<ICreatorProfileSourceStore>(_ =>
                new CreatorProfileSourceStore(RelationalDatabaseConnection.FromSqlitePath(Path.Combine(tempDirectory, "creator-deck-cache.db"))));
            services.AddSingleton(_ =>
                new CategoryKnowledgeRepository(RelationalDatabaseConnection.FromSqlitePath(Path.Combine(tempDirectory, "category-knowledge.db"))));
            services.AddSingleton<IArchidektOwnerClient, FakeArchidektOwnerClient>();
            services.AddSingleton<IArchidektDeckImporter, FakeArchidektDeckImporter>();
            services.AddSingleton<IScryfallTaggerLookupService, FakeScryfallTaggerLookupService>();
            services.AddSingleton<ICommanderSpellbookService, FakeCommanderSpellbookService>();
            services.AddSingleton<IScryfallCardResolver, FakeScryfallCardResolver>();
            services.AddSingleton<ICreatorStyleProfileStore, FakeCreatorStyleProfileStore>();
            services.AddSingleton<ICardGroundingGuard, FakeCardGroundingGuard>();
            services.AddSingleton<IDeckEntryLoader, FakeDeckEntryLoader>();
            services.AddMemoryCache();
            services.AddSingleton<PacketSessionCache>();
            services.AddSingleton<CreatorWhitelistPoolBuilder>();
            services.AddScoped<CreatorProfileDeckCrawler>();
            services.AddScoped<CreatorDeckCategoryResolver>();
            services.AddScoped<MeasuredStyleProfileBuilder>();
            services.AddScoped<ISubmittedDeckStatsBuilder>(sp =>
                new SubmittedDeckStatsBuilder(
                    sp.GetRequiredService<IDeckEntryLoader>(),
                    sp.GetRequiredService<CategoryKnowledgeRepository>(),
                    sp.GetRequiredService<ICommanderSpellbookService>(),
                    sp.GetRequiredService<IScryfallCardResolver>(),
                    sp.GetService<Microsoft.Extensions.Logging.ILogger<SubmittedDeckStatsBuilder>>()));
            services.AddScoped<ICreatorStylePacketService>(sp =>
                new CreatorStylePacketService(
                    sp.GetRequiredService<ICreatorStyleProfileStore>(),
                    sp.GetRequiredService<ISubmittedDeckStatsBuilder>(),
                    sp.GetRequiredService<CreatorWhitelistPoolBuilder>(),
                    sp.GetRequiredService<ICardGroundingGuard>(),
                    sp.GetRequiredService<ICreatorDeckCacheStore>(),
                    sp.GetRequiredService<PacketSessionCache>(),
                    sp.GetService<IFeatureFlagCache>(),
                    sp.GetService<Microsoft.Extensions.Logging.ILogger<CreatorStylePacketService>>()));

            using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            using IServiceScope scope = provider.CreateScope();

            Assert.NotNull(scope.ServiceProvider.GetRequiredService<CreatorProfileDeckCrawler>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<CreatorDeckCategoryResolver>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<MeasuredStyleProfileBuilder>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICreatorStylePacketService>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Resolve_RealArchidektOwnerClient_DoesNotThrow_WhenArchidektPipelineRegistered()
    {
        using var testRoot = CreatorStyleTestRoot.Create();
        var services = CreateCreatorStyleServiceCollection(testRoot.Environment);
        services.AddDeckFlowResiliencePipelines();
        services.AddDeckFlowCreatorStyle(testRoot.Environment);

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        // Why: a fake IArchidektOwnerClient cannot catch the missing-pipeline failure mode because ArchidektOwnerClient throws in its constructor.
        var ownerClient = provider.GetRequiredService<IArchidektOwnerClient>();
        Assert.IsType<ArchidektOwnerClient>(ownerClient);

        using IServiceScope scope = provider.CreateScope();
        Assert.IsType<CreatorProfileDeckCrawler>(scope.ServiceProvider.GetRequiredService<CreatorProfileDeckCrawler>());
    }

    [Fact]
    public void AddDeckFlowCreatorStyle_DescriptorDelta_ResolvesEveryCreatorStyleRegistration()
    {
        using var testRoot = CreatorStyleTestRoot.Create();
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

    private sealed class FakeArchidektOwnerClient : IArchidektOwnerClient
    {
        public Task<IReadOnlyList<ArchidektDeckSummary>> ListDeckSummariesAsync(string ownerUsername, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ArchidektDeckSummary>>([]);

        public Task<string?> ResolveUsernameAsync(string usernameOrUrl, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(usernameOrUrl);
    }

    private sealed class FakeArchidektDeckImporter : IArchidektDeckImporter
    {
        public Task<List<DeckEntry>> ImportAsync(string urlOrDeckId, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<DeckEntry>());
    }

    private sealed class FakeScryfallTaggerLookupService : IScryfallTaggerLookupService
    {
        public Task<IReadOnlyList<string>> LookupOracleTagsAsync(string cardName, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeCommanderSpellbookService : ICommanderSpellbookService
    {
        public Task<CommanderSpellbookResult?> FindCombosAsync(IReadOnlyList<DeckEntry> entries, CancellationToken cancellationToken = default)
            => Task.FromResult<CommanderSpellbookResult?>(null);
    }

    private sealed class FakeCardGroundingGuard : ICardGroundingGuard
    {
        public Task<CardGroundingVerdict> TryValidateAsync(
            string candidateName,
            CardGroundingDeckContext deckContext,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CardGroundingVerdict
            {
                Accepted = true,
                CanonicalName = candidateName,
                RejectReason = CardGroundingRejectReason.None,
            });

        public Task<CardGroundingBatchResult> ValidateAllAsync(
            IReadOnlyList<string> candidateNames,
            CardGroundingDeckContext deckContext,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts = candidateNames
                    .Select(candidateName => new CardGroundingVerdict
                    {
                        Accepted = true,
                        CanonicalName = candidateName,
                        RejectReason = CardGroundingRejectReason.None,
                    })
                    .ToArray(),
                HasUpstreamFailure = false,
            });
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

    private sealed class FakeScryfallCardResolver : IScryfallCardResolver
    {
        public Task<RestSharp.RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestSharp.RestRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RestSharp.RestResponse<ScryfallCard>> ExecuteNamedFuzzyAsync(string cardName, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);
    }

    private sealed class FakeCreatorStyleProfileStore : ICreatorStyleProfileStore
    {
        public Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CreatorStyleProfileSummary>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CreatorStyleProfileSummary>>(Array.Empty<CreatorStyleProfileSummary>());

        public Task<CreatorStyleProfile?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
            => Task.FromResult<CreatorStyleProfile?>(null);

        public Task UpsertAsync(CreatorStyleProfile profile, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
