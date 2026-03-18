using Serilog;
using DeckSyncWorkbench.Core.Integration;
using DeckSyncWorkbench.Core.Parsing;
using DeckSyncWorkbench.Web.Services;

namespace DeckSyncWorkbench.Web;

public class Program
{
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
        builder.Services.AddControllersWithViews();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<CategoryKnowledgeStore>();
        builder.Services.AddScoped<IDeckSyncService, DeckSyncService>();
        builder.Services.AddHttpClient<IMoxfieldDeckImporter, MoxfieldApiDeckImporter>();
        builder.Services.AddHttpClient<IArchidektDeckImporter, ArchidektApiDeckImporter>();
        builder.Services.AddTransient<MoxfieldParser>();
        builder.Services.AddTransient<ArchidektParser>();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Deck");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseSerilogRequestLogging();

        app.UseAuthorization();

        app.MapDefaultControllerRoute();

        app.Run();
    }
}
