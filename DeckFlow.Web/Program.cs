using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using Serilog;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Parsing;
using DeckFlow.Web.Infrastructure;
using DeckFlow.Web.Services;

namespace DeckFlow.Web;

/// <summary>
/// Configures and starts the DeckFlow web application.
/// </summary>
public class Program
{
    /// <summary>
    /// Bootstraps the ASP.NET Core MVC app with Serilog and service registrations.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var logPath = Path.Combine(builder.Environment.ContentRootPath, "logs", "web-.log");

        builder.Host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext();

            if (context.HostingEnvironment.IsDevelopment())
            {
                configuration.WriteTo.Console();
            }

            configuration.WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14);
        });

        // Add services to the container.
        builder.Services
            .AddControllersWithViews()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });
        builder.Services.AddMemoryCache();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Deck Sync Workbench API",
                Version = "v1",
                Description = "Card and commander category suggestion endpoints used by the UI."
            });
            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            if (File.Exists(xmlPath))
            {
                options.IncludeXmlComments(xmlPath);
            }
        });
        builder.Services.AddSingleton<ICommanderSearchService, ScryfallCommanderSearchService>();
        builder.Services.AddSingleton<ICardSearchService, ScryfallCardSearchService>();
        builder.Services.AddSingleton<ICardLookupService, ScryfallCardLookupService>();
        builder.Services.AddSingleton<IMechanicLookupService, WotcMechanicLookupService>();
        builder.Services.AddSingleton<ICommanderBanListService, CommanderBanListService>();
        builder.Services.AddSingleton<ICommanderSpellbookService, CommanderSpellbookService>();
        builder.Services.AddSingleton<IScryfallSetService, ScryfallSetService>();
        builder.Services.AddSingleton<IEdhTop16Client, EdhTop16Client>();
        builder.Services.AddSingleton<IScryfallTaggerService, ScryfallTaggerService>();
        builder.Services.AddSingleton<IChatGptArtifactsDirectory, ChatGptArtifactsDirectory>();
        builder.Services.AddScoped<IChatGptDeckPacketService, ChatGptDeckPacketService>();
        builder.Services.AddScoped<IChatGptDeckComparisonService, ChatGptDeckComparisonService>();
        builder.Services.AddScoped<IChatGptCedhMetaGapService, ChatGptCedhMetaGapService>();
        builder.Services.AddSingleton<ICategoryKnowledgeStore, CategoryKnowledgeStore>();
        builder.Services.AddSingleton<ArchidektCacheJobService>();
        builder.Services.AddSingleton<IArchidektCacheJobService>(sp => sp.GetRequiredService<ArchidektCacheJobService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ArchidektCacheJobService>());
        builder.Services.AddScoped<ICategorySuggestionService, CategorySuggestionService>();
        builder.Services.AddScoped<ICommanderCategoryService, CommanderCategoryService>();
        builder.Services.AddScoped<IDeckSyncService, DeckSyncService>();
        builder.Services.AddScoped<IDeckConvertService, DeckConvertService>();
        builder.Services.AddSingleton<IMoxfieldDeckImporter, MoxfieldApiDeckImporter>();
        builder.Services.AddSingleton<IArchidektDeckImporter, ArchidektApiDeckImporter>();
        builder.Services.AddTransient<MoxfieldParser>();
        builder.Services.AddTransient<ArchidektParser>();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Deck");
            app.UseHsts();
        }

        app.UseDeckFlowSecurityHeaders();

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseSerilogRequestLogging();
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("v1/swagger.json", "Deck Sync Workbench API v1");
                c.RoutePrefix = "swagger";
            });
        }

        app.UseAuthorization();

        app.MapControllers();
        app.MapDefaultControllerRoute();

        if (app.Environment.IsDevelopment()
            && !string.Equals(
                Environment.GetEnvironmentVariable("MTGDECKSTUDIO_DISABLE_AUTO_BROWSER"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var launchUrl = app.Urls
                    .OrderByDescending(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(launchUrl))
                {
                    return;
                }

                try
                {
                    DevelopmentBrowserLauncher.OpenNewWindow(launchUrl);
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "Failed to auto-open browser for {LaunchUrl}.", launchUrl);
                }
            });
        }

        app.Run();
    }
}
