using System.Reflection;
using System.Net.Http;
using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Polly.Registry;
using Serilog;
using DeckFlow.Core.Integration;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Parsing;
using DeckFlow.Web.Configuration;
using DeckFlow.Web.Extensions;
using DeckFlow.Web.Infrastructure;
using DeckFlow.Web.Security;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Analytics;
using DeckFlow.Web.Services.Bracket;
using DeckFlow.Web.Services.CutLab;
using DeckFlow.Web.Services.Edhrec;
using DeckFlow.Web.Services.Harvest;
using DeckFlow.Web.Services.PromptBuilders.Bracket;
using DeckFlow.Web.Services.Http;
using DeckFlow.Web.Services.Manabase;
using DeckFlow.Web.Services.Modular;
using Microsoft.Extensions.Options;

namespace DeckFlow.Web;

/// <summary>
/// Configures and starts the DeckFlow web application.
/// </summary>
public partial class Program
{
    /// <summary>
    /// Bootstraps the ASP.NET Core MVC app with Serilog and service registrations.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    public static async Task Main(string[] args)
    {
        try
        {
            var builder = CreateBuilder(args);
            var logPath = Path.Combine(builder.Environment.ContentRootPath, "logs", "web-.log");

            builder.Host.UseSerilog((context, services, configuration) =>
            {
                configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext();

                // Render only captures stdout/stderr in the service logs, so keep console logging on
                // outside development as well as in development. The file sink remains available for
                // the local logs directory and persistent disk snapshots.
                configuration.WriteTo.Console();

                configuration.WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14);
            });

            // Add services to the container.
            builder.Services
                .AddControllersWithViews()
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                });
            builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
            builder.Services.Configure<Microsoft.AspNetCore.Mvc.Razor.RazorViewEngineOptions>(options => options.ViewLocationExpanders.Add(new DeckFlow.Web.Controllers.DeckViewLocationExpander()));
            builder.Services.AddMemoryCache();

            // AI platform toggles. Gemini is hidden in the UI by default because the full
            // packet frequently exceeds Gemini's paste limit (truncates instructions, produces
            // degraded output). Flip DECKFLOW_GEMINI_ENABLED=true to expose it again.
            builder.Services.Configure<AiPlatformOptions>(options =>
            {
                var raw = Environment.GetEnvironmentVariable("DECKFLOW_GEMINI_ENABLED");
                options.GeminiEnabled = bool.TryParse(raw, out var enabled) && enabled;
            });

            // HTTP infrastructure: IHttpClientFactory-backed clients (D-01) + Polly v8 pipelines (D-03..05).
            // Tagger uses a typed client with cookie-disabled SocketsHttpHandler (D-06); other three are named.
            // Pipelines are registered into IResiliencePipelineRegistry<string> via AddResiliencePipeline<...>;
            // services resolve them via ResiliencePipelineProvider<string> (no keyed-services attribute - checker B2).
            builder.Services.AddDeckFlowHttpClients();

            // Polly v8 pipelines registered into IResiliencePipelineRegistry<string>. Services resolve
            // them via ResiliencePipelineProvider<string>.GetPipeline<RestResponse>(name) - D-05, B2.
            builder.Services.AddDeckFlowResiliencePipelines();

            builder.Services.AddDeckFlowScryfallServices();

            // Why (WR-11, 112-REVIEW.md): the creator-style engine has no controller wired to it
            // yet (zero HTTP exposure) and its production seeds (content-kb/seed/creator-*.json)
            // are still literally "[]", so registering its full DI graph unconditionally pays a
            // real cost - an HttpClient, five singletons (two of them opening SQLite files via
            // DeckFlowDatabaseConnectionFactory), and five scoped services - for a subsystem that
            // cannot do anything yet. Gate it behind an explicit opt-in env var, mirroring the
            // DECKFLOW_GEMINI_ENABLED pattern above, distinct from the tool.creator-style.enabled
            // IFeatureFlagCache runtime flag (DB-backed, default-on-if-missing, meant for gating
            // per-request reachability once a controller exists - not for skipping startup DI,
            // which must resolve before the flag store's async load completes). A future phase
            // that wires a controller and populates the seeds flips this on without touching
            // Program.cs again.
            if (IsCreatorStyleEnabled())
            {
                builder.Services.AddDeckFlowCreatorStyle(builder.Environment);
            }

            builder.Services.AddSingleton<IHelpContentService, HelpContentService>();
            builder.Services.AddSingleton<IGameChangerCatalogService, GameChangerCatalogService>();
            builder.Services.AddSingleton<ICedhLandBaselineProvider, CedhLandBaselineProvider>();
            builder.Services.AddSingleton<IRoleFloorBaselineProvider, RoleFloorBaselineProvider>();
            builder.Services.AddSingleton<IManabaseBaselineProvider, ManabaseBaselineProvider>();
            builder.Services.AddSingleton<IVersionService, VersionService>();
            builder.Services.AddSingleton<IFeedbackStore, FeedbackStore>();
            // Why: foundation-only store registration for Phase 1; no consumer until Phase 3/4.
            builder.Services.AddSingleton<IManabaseBaselineStore, ManabaseBaselineStore>();
            builder.Services.AddSingleton<DeckFlow.Core.Content.IContentSiteIndexStore>(_ =>
                new DeckFlow.Core.Content.ContentSiteIndexStore(
                    DeckFlowDatabaseConnectionFactory.CreateContentSiteIndexConnection(builder.Environment)));
            builder.Services.AddSingleton<ContentKbArtifactPathResolver>();
            builder.Services.AddSingleton<IContentKbSeedLoader, ContentKbSeedLoader>();
            builder.Services.AddSingleton<DeckFlow.Core.Content.IContentArtifactBodyResolver, ContentKbArtifactBodyResolver>();
            builder.Services.AddSingleton<DeckFlow.Core.Content.ContentBodyHashBackfill>();
            builder.Services.AddSingleton<DeckFlow.Core.Content.ISeedKeyMembershipSource, WebSeedKeyMembershipSource>();
            builder.Services.AddSingleton<DeckFlow.Core.Content.SeedManagedBackfill>();
            builder.Services.AddSingleton<DeckFlow.Core.Content.PublishStateDeriver>();
            builder.Services.AddSingleton<IAdminBruteForceTrackerStore, AdminBruteForceTrackerStore>();
            builder.Services.AddDeckFlowFeatureFlags();
            builder.Services.AddDeckFlowTools();
            builder.Services.AddDeckFlowHarvest(builder.Environment);
            builder.Services.AddDeckFlowAnalytics(builder.Environment);

            // Honor X-Forwarded-* headers from the reverse proxy so request.Scheme reflects
            // the browser's https scheme, not the http hop from proxy to app. Without this,
            // SameOriginRequestValidator sees scheme=http while Origin=https and rejects the request.
            //
            // Note (TD-04, Phase 03 SC #4, retrieved 2026-04-30): Render does not publish enumerable
            // inbound proxy CIDR ranges (verified at https://render.com/docs/inbound-ip-rules and
            // https://feedback.render.com/features/p/send-the-correct-xforwardedfor). Rather than
            // trust an arbitrary upstream's X-Forwarded-For value to gate the feedback rate limit,
            // the partition key (DeriveFeedbackPartitionKey, below) reads the immediate-peer IP
            // directly. The default loopback trust list (127.0.0.1, ::1) is preserved here for
            // Kestrel container-internal health checks; we do NOT clear it.
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                    | ForwardedHeaders.XForwardedProto
                    | ForwardedHeaders.XForwardedHost;
                // Why: Render/Cloudflare proxy hops are not loopback, so the default KnownIPNetworks/KnownProxies
                // would cause ForwardedHeadersMiddleware to ignore X-Forwarded-Proto and leave Request.Scheme=http
                // (breaks https canonical/OG URLs + HTTPS redirect + same-origin CSRF). The container is only
                // reachable via Render's ingress, so trusting the forwarded headers here is safe.
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });

            builder.Services.AddRateLimiter(options =>
            {
                options.AddPolicy("feedback-submit", httpContext =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        DeriveFeedbackPartitionKey(httpContext),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 5,
                            Window = TimeSpan.FromHours(1),
                            QueueLimit = 0,
                            AutoReplenishment = true,
                        }));
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            });

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
            builder.Services.AddDeckFlowPromptVariants();
            builder.Services.AddSingleton<ICategoryKnowledgeStore, CategoryKnowledgeStore>();
            builder.Services.AddDeckFlowPacketServices();
            builder.Services.AddSingleton<ArchidektCacheJobService>();
            builder.Services.AddSingleton<IArchidektCacheJobService>(sp => sp.GetRequiredService<ArchidektCacheJobService>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<ArchidektCacheJobService>());
            builder.Services.AddSingleton<EdhrecCardLookup>();
            builder.Services.AddSingleton<IEdhrecCardLookup>(sp => new CachingEdhrecCardLookup(sp.GetRequiredService<EdhrecCardLookup>()));
            builder.Services.AddScoped<ICategorySuggestionService, CategorySuggestionService>();
            builder.Services.AddScoped<ICommanderCategoryService, CommanderCategoryService>();
            builder.Services.AddScoped<IDeckSyncService, DeckSyncService>();
            builder.Services.AddScoped<IDeckHistoryPageService, DeckHistoryPageService>();
            builder.Services.AddScoped<DeckFlow.Web.Services.CutLab.ICutLabPageService, DeckFlow.Web.Services.CutLab.CutLabPageService>();
            builder.Services.AddScoped<ICutLabFloorResolver, CutLabFloorResolver>();
            builder.Services.AddDeckFlowCutLabServices();
            builder.Services.AddSingleton<IEdhrecCommanderThemeService, EdhrecCommanderThemeService>();
            builder.Services.AddScoped<ICutLabPlanAffinityFactory, CutLabPlanAffinityFactory>();
            builder.Services.AddScoped<DeckFlow.Web.Services.CutLab.ICutLabWhatifService, DeckFlow.Web.Services.CutLab.CutLabWhatifService>();
            builder.Services.AddScoped<DeckFlow.Web.Services.CutLab.ICutLabExportService, DeckFlow.Web.Services.CutLab.CutLabExportService>();
            builder.Services.AddScoped<IDeckModulesPageService, DeckModulesPageService>();
            builder.Services.AddDeckFlowDeckModulesAnalysisServices();
            builder.Services.AddScoped<IDeckConvertService>(sp =>
                new DeckConvertService(
                    sp.GetRequiredService<IScryfallRestClientFactory>(),
                    sp.GetRequiredService<ResiliencePipelineProvider<string>>(),
                    sp.GetRequiredService<IDeckEntryLoader>()));
            builder.Services.AddScoped<IDeckEntryLoader, DeckEntryLoader>();
            builder.Services.AddDeckFlowManabaseServices();
            builder.Services.AddScoped<IBracketClassificationService>(sp =>
                new BracketClassificationService(
                    sp.GetRequiredService<IDeckEntryLoader>(),
                    sp.GetRequiredService<ICommanderSpellbookService>(),
                    sp.GetRequiredService<IGameChangerCatalogService>(),
                    sp.GetRequiredService<BracketPromptVariantRegistry>(),
                    sp.GetService<ILogger<BracketClassificationService>>()));
            builder.Services.AddSingleton<IMoxfieldDeckImporter, MoxfieldApiDeckImporter>();
            builder.Services.AddSingleton<IArchidektDeckImporter, ArchidektApiDeckImporter>();

            var app = builder.Build();

            // Must run before any middleware that reads request.Scheme/Host (HttpsRedirection,
            // security headers, SameOriginRequestValidator in controllers) so those see the
            // browser's original scheme/host, not the proxy hop.
            app.UseForwardedHeaders();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Deck/Error");
                app.UseHsts();
            }

            // UseExceptionHandler only covers thrown exceptions; a mistyped URL or a flag-gated
            // 404 would otherwise render the bare framework page. Excluded for API paths so
            // JSON callers keep getting an empty 404 body instead of an HTML document.
            app.UseWhen(
                context => !IsApiPath(context.Request.Path),
                branch => branch.UseStatusCodePagesWithReExecute("/Deck/Error", "?code={0}"));

            app.UseDeckFlowSecurityHeaders();

            app.UseHttpsRedirection();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.Equals("/extension-install.html", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Redirect("/deckflow-bridge", permanent: true);
                    return;
                }

                await next();
            });
            app.UseStaticFiles();
            app.UseRouting();
            app.UseAnalyticsMiddleware();   // D-12: after UseRouting (endpoint resolved), before MapControllers
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

            app.UseRateLimiter();

            app.UseWhen(
                ctx => ctx.Request.Path.StartsWithSegments("/Admin"),
                branch => branch.UseMiddleware<BasicAuthMiddleware>("DeckFlow Admin"));

            app.MapControllers();
            app.MapDefaultControllerRoute();

            static bool IsAutoBrowserDisabled()
            {
                var raw = Environment.GetEnvironmentVariable("DECKFLOW_DISABLE_AUTO_BROWSER");

                return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            }

            if (app.Environment.IsDevelopment()
                && !IsAutoBrowserDisabled())
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

            await ValidateDatabaseConnectionsAsync(app.Services, app.Environment, app.Logger);
            app.Logger.LogInformation("Ensuring content site-index schema during startup.");
            await app.Services.GetRequiredService<DeckFlow.Core.Content.IContentSiteIndexStore>().EnsureSchemaAsync();
            Task contentKbSeedTask = app.Services.GetRequiredService<IContentKbSeedLoader>().LoadIfPresentAsync();
            Task creatorStyleSeedTask = app.Services.GetRequiredService<ICreatorStyleSeedLoader>().LoadIfPresentAsync();
            await AwaitStartupSeedTasksAsync(contentKbSeedTask, creatorStyleSeedTask, app.Services.GetRequiredService<ILogger<Program>>());
            app.Logger.LogInformation("Content site-index schema ensured and seed load completed during startup.");

            // D-08: one-time deterministic body_sha256 backfill, third step after schema-ensure
            // (the column must exist) and seed load (freshly-seeded rows get hashed too). Idempotent
            // null-only pass — safe to run on every startup.
            app.Logger.LogInformation("Running Content KB body-hash backfill during startup.");
            await app.Services.GetRequiredService<DeckFlow.Core.Content.ContentBodyHashBackfill>().RunAsync();
            app.Logger.LogInformation("Content KB body-hash backfill completed during startup.");

            // SYNC-17/D-02: seed_managed backfill runs AFTER the seed load above so membership
            // reflects the just-loaded seed. Idempotent null-only pass; skips entirely (zero
            // writes) when the seed was unavailable this run (T-91-07) - safe to run every startup.
            app.Logger.LogInformation("Running Content KB seed_managed backfill during startup.");
            await app.Services.GetRequiredService<DeckFlow.Core.Content.SeedManagedBackfill>().RunAsync();
            app.Logger.LogInformation("Content KB seed_managed backfill completed during startup.");

            app.Logger.LogInformation("Ensuring harvest store schemas during startup.");
            await app.Services.GetRequiredService<IHarvestRunStore>().EnsureSchemaAsync();
            await app.Services.GetRequiredService<IHarvestScheduleStore>().EnsureSchemaAsync();
            app.Logger.LogInformation("Harvest store schemas ensured during startup.");

            app.Logger.LogInformation("Ensuring analytics store schema during startup.");
            await app.Services.GetRequiredService<IRequestMetricsStore>().EnsureSchemaAsync();
            app.Logger.LogInformation("Analytics store schema ensured during startup.");

            app.Logger.LogInformation("Warming Game Changer catalog into memory cache during startup.");
            app.Services.GetRequiredService<IGameChangerCatalogService>().GetCatalog();
            app.Logger.LogInformation("Game Changer catalog warm-loaded.");

            app.Logger.LogInformation("Warming cEDH land baseline into memory cache during startup.");
            app.Services.GetRequiredService<ICedhLandBaselineProvider>().EnsureLoaded();
            app.Logger.LogInformation("cEDH land baseline warm-loaded.");
            app.Services.GetRequiredService<IRoleFloorBaselineProvider>().EnsureLoaded();

            app.Services.GetRequiredService<IManabaseBaselineProvider>().EnsureLoaded();

            // Resolve the IP-hash salt once at startup so the analytics middleware does not
            // perform DB I/O on the hot path. Uses CreateHarvestStateConnection for explicit
            // factory parity with RequestMetricsStore writes (Plan 01) and admin reads (Plan 04).
            try
            {
                var saltAccessor = app.Services.GetRequiredService<AnalyticsSaltAccessor>();
                var harvestConn = DeckFlowDatabaseConnectionFactory.CreateHarvestStateConnection(app.Environment);
                await using var saltConnection = harvestConn.CreateConnection();
                await saltConnection.OpenAsync();
                var salt = await IpHasher.ResolveSaltAsync(saltConnection);
                saltAccessor.SetSalt(salt);
                app.Logger.LogInformation("Analytics IP salt resolved.");
            }
            catch (Exception saltEx)
            {
                app.Logger.LogWarning(saltEx,
                    "Analytics IP salt resolution failed; ip_hash will be null until next startup.");
            }

            await app.RunAsync();
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "DeckFlow web host terminated during startup or run.");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    internal static WebApplicationBuilder CreateBuilder(string[] args)
    {
        // Why: JSON reload watchers each consume a host-shared inotify instance
        // (fs.inotify.max_user_instances). Exhaustion throws IOException inside CreateBuilder,
        // before Main's try; this caused Render update_failed deployment on 2026-08-16.
        const string reloadConfigOnChangeVariable = "DOTNET_hostBuilder__reloadConfigOnChange";
        var originalValue = Environment.GetEnvironmentVariable(reloadConfigOnChangeVariable);

        try
        {
            Environment.SetEnvironmentVariable(reloadConfigOnChangeVariable, "false");
            return WebApplication.CreateBuilder(args);
        }
        finally
        {
            Environment.SetEnvironmentVariable(reloadConfigOnChangeVariable, originalValue);
        }
    }

    /// <summary>
    /// Reads the Cloudflare-injected real client IP from the CF-Connecting-IP request header
    /// (Phase 5 BUG-02 fix). Cloudflare always sets this to the originating client IP — single
    /// value per real client, immune to the multi-proxy fan-out that broke Phase 4's
    /// Connection.RemoteIpAddress-based partitioning. Cannot be spoofed past Cloudflare's edge
    /// PROVIDED Render Inbound IP Rules gate the origin to Cloudflare CIDRs (see README "Admin
    /// throttle" operations note). Returns "unknown" and logs a warning when the header is
    /// missing — fail-closed, all unidentifiable traffic shares one partition.
    /// </summary>
    internal static string DeriveCloudflareClientIp(HttpContext context)
    {
        var raw = context.Request.Headers["CF-Connecting-IP"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            Log.Warning("CF-Connecting-IP missing on {Path} — falling back to 'unknown' partition. Verify Render Inbound IP Rules + Cloudflare proxy.", context.Request.Path.Value ?? "(empty)");
            return "unknown";
        }
        return raw.Trim();
    }

    /// <summary>
    /// Partition key for the feedback-submit rate limiter (TD-04 / Phase 03 SC #4 +
    /// Phase 05 SC #5 corrective). Wraps DeriveCloudflareClientIp so the partition derivation
    /// matches the admin-throttle partition derivation — single source of truth. Phase 03's
    /// "peer:" prefix becomes "feedback:" to make the namespace explicit and disjoint from
    /// "admin:".
    /// </summary>
    internal static string DeriveFeedbackPartitionKey(HttpContext context)
        => "feedback:" + DeriveCloudflareClientIp(context);

    /// <summary>
    /// Partition key for the admin basic-auth brute-force throttle (BUG-02). Same CF-Connecting-IP
    /// derivation as DeriveFeedbackPartitionKey but with "admin:" namespace prefix so admin and
    /// feedback buckets cannot collide.
    /// </summary>
    internal static string DeriveAdminPartitionKey(HttpContext context)
        => "admin:" + DeriveCloudflareClientIp(context);

    private static bool IsApiPath(PathString path)
        => path.StartsWithSegments("/api") || path.StartsWithSegments("/Admin/api");

    /// <summary>
    /// Reads the DECKFLOW_CREATOR_STYLE_ENABLED opt-in switch that gates whether
    /// <c>AddDeckFlowCreatorStyle</c> is registered at startup (WR-11, 112-REVIEW.md). Defaults to
    /// disabled (missing or unparseable values are treated as off), matching the
    /// DECKFLOW_GEMINI_ENABLED precedent above — the creator-style engine has no HTTP entry point
    /// yet, so opting in without also wiring a controller has no observable effect.
    /// </summary>
    internal static bool IsCreatorStyleEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("DECKFLOW_CREATOR_STYLE_ENABLED");
        return bool.TryParse(raw, out var enabled) && enabled;
    }

    internal static async Task AwaitStartupSeedTasksAsync(
        Task contentKbSeedTask,
        Task creatorStyleSeedTask,
        ILogger<Program> logger)
    {
        ArgumentNullException.ThrowIfNull(contentKbSeedTask);
        ArgumentNullException.ThrowIfNull(creatorStyleSeedTask);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await Task.WhenAll(contentKbSeedTask, creatorStyleSeedTask);
        }
        catch
        {
            LogFaultedSeedTask(contentKbSeedTask, nameof(contentKbSeedTask), logger);
            LogFaultedSeedTask(creatorStyleSeedTask, nameof(creatorStyleSeedTask), logger);
            throw;
        }
    }

    internal static void LogFaultedSeedTask(Task seedTask, string seedTaskName, ILogger<Program> logger)
    {
        ArgumentNullException.ThrowIfNull(seedTask);
        ArgumentException.ThrowIfNullOrWhiteSpace(seedTaskName);
        ArgumentNullException.ThrowIfNull(logger);

        if (!seedTask.IsFaulted)
        {
            return;
        }

        logger.LogError(
            seedTask.Exception?.InnerException ?? seedTask.Exception,
            "Startup seed task {SeedTask} faulted.",
            seedTaskName);
    }

    private static async Task ValidateDatabaseConnectionsAsync(IServiceProvider services, IWebHostEnvironment environment, Microsoft.Extensions.Logging.ILogger logger)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        using var scope = services.CreateScope();
        var feedbackStore = scope.ServiceProvider.GetRequiredService<IFeedbackStore>();
        var knowledgeStore = scope.ServiceProvider.GetRequiredService<ICategoryKnowledgeStore>();

        logger.LogInformation("Validating database connections during startup.");

        await feedbackStore.CountAsync(null, null);
        await knowledgeStore.GetProcessedDeckCountAsync();

        logger.LogInformation("Database connection validation completed successfully.");
    }
}
