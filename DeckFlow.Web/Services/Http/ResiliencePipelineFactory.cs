using System;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;
using RestSharp;

namespace DeckFlow.Web.Services.Http
{

    /// <summary>
    /// Registers per-service Polly v8 ResiliencePipeline&lt;RestResponse&gt; instances into the
    /// IResiliencePipelineRegistry&lt;string&gt; per the tuning matrix locked in 01-CONTEXT.md (D-04).
    /// Pipelines are constructed once at composition time (Program.cs) and resolved per-service
    /// via ResiliencePipelineProvider&lt;string&gt;.GetPipeline&lt;RestResponse&gt;(name) (D-05) - never
    /// rebuilt per call. Replaces the keyed-services attribute approach (checker B2).
    /// </summary>
    public static class ResiliencePipelineFactory
    {
        internal static readonly TimeSpan ScryfallTotalTimeout = TimeSpan.FromSeconds(30);
        // Why: total must comfortably exceed 3 attempts * per-attempt plus retry delays, and must
        // stay below the edhrec HttpClient's own 15s outer Timeout, or a slow-but-responding EDHREC
        // starves every retry (WR-03). 3*4s + 2*200ms = 12.4s against a 14s total budget.
        internal static readonly TimeSpan EdhrecTotalTimeout = TimeSpan.FromSeconds(14);
        internal static readonly TimeSpan EdhrecAttemptTimeout = TimeSpan.FromSeconds(4);

        internal const int ScryfallMaxRetryAttempts = 2;

        /// <summary>Registers all five named pipelines into the supplied IServiceCollection.</summary>
        public static IServiceCollection AddDeckFlowResiliencePipelines(this IServiceCollection services)
        {
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "banlist", builder => BuildBanList(builder));
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "edhrec", builder => BuildEdhrec(builder));
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "spellbook", builder => BuildSpellbook(builder));
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "tagger", builder => BuildTagger(builder));
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "tagger-post", builder => BuildTaggerPost(builder));
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "scryfall", builder => BuildScryfall(builder));
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "archidekt", builder => BuildArchidekt(builder));
            return services;
        }

        /// <summary>BanList: Retry(2, 200ms constant), AttemptTimeout(5s), no CB.</summary>
        private static void BuildBanList(ResiliencePipelineBuilder<RestResponse> builder) => builder
            .AddRetry(new RetryStrategyOptions<RestResponse>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder<RestResponse>()
                    .HandleResult(static r => IsTransientFailure(r))
                    .Handle<Exception>(static ex => IsTransientException(ex)),
            })
            .AddTimeout(TimeSpan.FromSeconds(5));

        /// <summary>EDHREC static CDN: retry transient failures only, no shared circuit breaker.</summary>
        internal static void BuildEdhrec(
            ResiliencePipelineBuilder<RestResponse> builder,
            TimeSpan? totalTimeout = null,
            TimeSpan? attemptTimeout = null,
            TimeSpan? retryDelay = null) => builder
                // Why: the total budget must cover all three 4-second attempts plus retry delays.
                .AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = totalTimeout ?? EdhrecTotalTimeout,
                    Name = "edhrec-total",
                })
                .AddRetry(new RetryStrategyOptions<RestResponse>
                {
                    MaxRetryAttempts = 2,
                    Delay = retryDelay ?? TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Constant,
                    // Why: 403 is deliberately not transient, so S3 AccessDenied is never retried.
                    ShouldHandle = new PredicateBuilder<RestResponse>()
                        .HandleResult(static r => IsTransientFailure(r))
                        .Handle<Exception>(static ex => IsTransientException(ex)),
                })
                .AddTimeout(attemptTimeout ?? EdhrecAttemptTimeout);

        /// <summary>
        /// Archidekt: TotalTimeout(30s) as OUTERMOST strategy with Retry(2, exponential+jitter) on 5xx and transient exceptions.
        /// </summary>
        private static void BuildArchidekt(ResiliencePipelineBuilder<RestResponse> builder) => builder
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
                Name = "archidekt-total",
            })
            .AddRetry(new RetryStrategyOptions<RestResponse>
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<RestResponse>()
                    .HandleResult(static r => r.StatusCode >= HttpStatusCode.InternalServerError)
                    .Handle<Exception>(static ex => IsTransientException(ex)),
            });

        /// <summary>Spellbook: Retry(3, exponential+jitter), AttemptTimeout(10s), CB(50% / 30s).</summary>
        private static void BuildSpellbook(ResiliencePipelineBuilder<RestResponse> builder) => builder
            .AddRetry(new RetryStrategyOptions<RestResponse>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<RestResponse>()
                    .HandleResult(static r => IsTransientFailure(r))
                    .Handle<Exception>(static ex => IsTransientException(ex)),
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<RestResponse>
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder<RestResponse>()
                    .HandleResult(static r => IsTransientFailure(r))
                    .Handle<Exception>(static ex => IsTransientException(ex)),
            })
            .AddTimeout(TimeSpan.FromSeconds(10));

        /// <summary>
        /// Tagger GET path: Retry(3, exponential+jitter); AttemptTimeout(8s); CB(50% / 30s).
        /// POST is on a SEPARATE pipeline ("tagger-post") with retry=0 (W6 - avoids the
        /// POST-predicate hole that would read args.Outcome.Result.Request?.Method).
        /// </summary>
        private static void BuildTagger(ResiliencePipelineBuilder<RestResponse> builder) => builder
            .AddRetry(new RetryStrategyOptions<RestResponse>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<RestResponse>()
                    .HandleResult(static r => IsTransientFailure(r))
                    .Handle<Exception>(static ex => IsTransientException(ex)),
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<RestResponse>
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder<RestResponse>()
                    .HandleResult(static r => IsTransientFailure(r))
                    .Handle<Exception>(static ex => IsTransientException(ex)),
            })
            .AddTimeout(TimeSpan.FromSeconds(8));

        /// <summary>
        /// Tagger POST path: NO retry (GraphQL POST is not idempotent - duplicate-write hazard,
        /// PITFALLS Pitfall 2). AttemptTimeout(8s); CB(50% / 30s) - separate CB state from the
        /// GET pipeline.
        /// </summary>
        private static void BuildTaggerPost(ResiliencePipelineBuilder<RestResponse> builder) => builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<RestResponse>
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder<RestResponse>()
                    .HandleResult(static r => IsTransientFailure(r))
                    .Handle<Exception>(static ex => IsTransientException(ex)),
            })
            .AddTimeout(TimeSpan.FromSeconds(8));

        /// <summary>
        /// Scryfall: TotalTimeout(30s) as OUTERMOST strategy - wraps retries so the entire
        /// pipeline (including retry waits) must complete within 30s (MEDIUM-2 fix - true total
        /// budget, not per-attempt). Individual attempts have no separate per-try timeout.
        /// Retry(2 on 5xx ONLY - NOT 429, defer to ScryfallThrottle backoff).
        /// ScryfallThrottle.ExecuteAsync wraps this pipeline at the call site (D-04).
        /// </summary>
        internal static void BuildScryfall(
            ResiliencePipelineBuilder<RestResponse> builder,
            TimeSpan? totalTimeout = null)
        {
            // Total budget - wraps retries; individual attempts have no separate per-try timeout
            // (handler-level timeout disabled per D-08 pattern). Name used in Polly telemetry.
            builder
                .AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = totalTimeout ?? ScryfallTotalTimeout,
                    Name = "scryfall-total",
                })
                .AddRetry(new RetryStrategyOptions<RestResponse>
                {
                    MaxRetryAttempts = ScryfallMaxRetryAttempts,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder<RestResponse>()
                        .HandleResult(static r => r.StatusCode >= HttpStatusCode.InternalServerError)
                        .Handle<Exception>(static ex => IsTransientException(ex)),
                });
        }

        private static bool IsTransientFailure(RestResponse response) =>
            response.StatusCode == HttpStatusCode.RequestTimeout
            || response.StatusCode == HttpStatusCode.TooManyRequests
            || (int)response.StatusCode >= 500;

        private static bool IsTransientException(Exception exception) =>
            exception is HttpRequestException
            || exception is TimeoutRejectedException
            || exception is TaskCanceledException;
    }

    internal static class DeckFlowResiliencePipelineRegistry
    {
        private static readonly ResiliencePipelineRegistry<string> Registry = new();
        private static readonly ResiliencePipelineProvider<string> Provider = new DeckFlowResiliencePipelineProvider(Registry);

        public static IServiceCollection Register(IServiceCollection services)
        {
            // TryAddSingleton (not a process-global `registered` flag) so EVERY IServiceCollection
            // that calls AddDeckFlowResiliencePipelines receives the registration. The old static
            // guard registered into only the first container in the process — harmless for the
            // single app host, but it silently left later containers (e.g. per-test ones) without
            // a provider. The shared static Registry/Provider hold the pipelines either way.
            services.TryAddSingleton(Registry);
            services.TryAddSingleton<ResiliencePipelineProvider<string>>(Provider);

            return services;
        }

        public static IServiceCollection AddResiliencePipeline<TKey, TResult>(
            IServiceCollection services,
            string key,
            Action<ResiliencePipelineBuilder<TResult>> configure) where TResult : RestResponse
        {
            Register(services);
            Registry.GetOrAddPipeline(key, configure);
            return services;
        }

        private sealed class DeckFlowResiliencePipelineProvider : ResiliencePipelineProvider<string>
        {
            private readonly ResiliencePipelineRegistry<string> registry;

            public DeckFlowResiliencePipelineProvider(ResiliencePipelineRegistry<string> registry) =>
                this.registry = registry;

            public override bool TryGetPipeline(string key, out ResiliencePipeline pipeline)
            {
                var found = this.registry.TryGetPipeline(key, out var candidate);
                pipeline = candidate!;
                return found;
            }

            public override bool TryGetPipeline<TResult>(string key, out ResiliencePipeline<TResult> pipeline)
            {
                var found = this.registry.TryGetPipeline<TResult>(key, out var candidate);
                pipeline = candidate!;
                return found;
            }
        }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    internal static class DeckFlowResiliencePipelineFactoryServiceCollectionExtensions
    {
        public static IServiceCollection AddResiliencePipeline<TKey, TResult>(
            this IServiceCollection services,
            string key,
            Action<ResiliencePipelineBuilder<TResult>> configure) where TResult : RestResponse =>
            DeckFlow.Web.Services.Http.DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<TKey, TResult>(services, key, configure);
    }
}
