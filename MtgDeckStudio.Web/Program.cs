using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using Serilog;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Parsing;
using MtgDeckStudio.Web.Services;

namespace MtgDeckStudio.Web;

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
                .Enrich.FromLogContext()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14);
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
        builder.Services.AddSingleton<IChatGptJsonTextFormatterService, ChatGptJsonTextFormatterService>();
        builder.Services.AddSingleton<ICommanderBanListService, CommanderBanListService>();
        builder.Services.AddSingleton<IScryfallSetService, ScryfallSetService>();
        builder.Services.AddScoped<IChatGptDeckPacketService, ChatGptDeckPacketService>();
        builder.Services.AddSingleton<ICategoryKnowledgeStore, CategoryKnowledgeStore>();
        builder.Services.AddScoped<ICategorySuggestionService, CategorySuggestionService>();
        builder.Services.AddScoped<ICommanderCategoryService, CommanderCategoryService>();
        builder.Services.AddScoped<IDeckSyncService, DeckSyncService>();
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

        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

                var path = context.Request.Path.Value ?? string.Empty;
                var skipCsp = path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase);
                if (!skipCsp)
                {
                    headers["Content-Security-Policy"] =
                        "default-src 'self'; " +
                        "script-src 'self'; " +
                        "style-src 'self' 'unsafe-inline'; " +
                        "img-src 'self' data:; " +
                        "font-src 'self'; " +
                        "connect-src 'self'; " +
                        "object-src 'none'; " +
                        "base-uri 'self'; " +
                        "form-action 'self'; " +
                        "frame-ancestors 'none'";
                }

                return Task.CompletedTask;
            });

            await next();
        });

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseSerilogRequestLogging();
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Deck Sync Workbench API v1");
                c.RoutePrefix = "swagger";
            });
        }

        app.UseAuthorization();

        app.MapControllers();
        app.MapDefaultControllerRoute();

        app.Run();
    }
}
