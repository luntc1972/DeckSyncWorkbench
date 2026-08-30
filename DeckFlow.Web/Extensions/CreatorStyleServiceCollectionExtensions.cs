using System.Net.Http.Headers;
using DeckFlow.Core.Content;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Knowledge.StatedRulesExtraction;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.CreatorStyle;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DeckFlow.Web.Extensions;

/// <summary>
/// DI registration extension for the creator-style engine.
/// </summary>
public static class CreatorStyleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the single creator-style service graph for the admin-only creator-style engine (D-10).
    /// </summary>
    /// <param name="services">DI service collection.</param>
    /// <param name="environment">Web host environment used to resolve local artifact paths.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDeckFlowCreatorStyle(this IServiceCollection services, IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddHttpClient("archidekt-owner", client =>
        {
            client.BaseAddress = new Uri("https://archidekt.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DeckFlow/1.0");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddSingleton<ICardNameGrounder, ScryfallCardNameGrounder>();
        services.AddSingleton<ICardGroundingGuard, CardGroundingGuard>();

        services.AddSingleton<ICreatorDeckCacheStore>(_ =>
            new CreatorDeckCacheStore(
                DeckFlowDatabaseConnectionFactory.CreateCreatorDeckCacheConnection(environment)));
        services.AddSingleton<ICreatorProfileSourceStore>(_ =>
            new CreatorProfileSourceStore(
                DeckFlowDatabaseConnectionFactory.CreateCreatorDeckCacheConnection(environment)));
        services.AddSingleton(_ =>
            new CategoryKnowledgeRepository(
                DeckFlowDatabaseConnectionFactory.CreateCategoryKnowledgeConnection(environment)));
        services.AddSingleton<ICreatorStyleProfileStore>(_ =>
            // Why: creator-style profiles live in the local-only content-kb DB because production never crawls; it only reads git-shipped seeds.
            new CreatorStyleProfileStore(
                DeckFlowDatabaseConnectionFactory.CreateLocalContentKbConnection(environment)));

        services.AddSingleton<CreatorWhitelistPoolBuilder>();
        services.AddSingleton<ICreatorStyleSeedLoader, CreatorStyleSeedLoader>();
        services.AddSingleton<IArchidektOwnerClient, ArchidektOwnerClient>();

        services.AddScoped<CreatorProfileDeckCrawler>();
        services.AddScoped<CreatorDeckCategoryResolver>();
        services.AddScoped<MeasuredStyleProfileBuilder>();
        services.AddScoped<ISubmittedDeckStatsBuilder, SubmittedDeckStatsBuilder>();
        services.AddScoped<ICreatorStylePacketService, CreatorStylePacketService>();

        return services;
    }
}
