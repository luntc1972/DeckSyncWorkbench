using DeckFlow.Web.Services.Modular;

namespace DeckFlow.Web.Extensions;

/// <summary>Registers Deck Modules configuration analysis services.</summary>
public static class DeckModulesAnalysisServiceCollectionExtensions
{
    /// <summary>Registers services used to analyze compiled Deck Modules configurations.</summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The supplied service collection.</returns>
    public static IServiceCollection AddDeckFlowDeckModulesAnalysisServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IConfigurationAnalysisService, ConfigurationAnalysisService>();
        return services;
    }
}
