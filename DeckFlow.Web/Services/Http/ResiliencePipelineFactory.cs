using System;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
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
        /// <summary>Registers all five named pipelines into the supplied IServiceCollection.</summary>
        public static IServiceCollection AddDeckFlowResiliencePipelines(this IServiceCollection services)
        {
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "banlist", builder => BuildBanList(builder));
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "spellbook", builder => BuildSpellbook(builder));
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "tagger", builder => BuildTagger(builder));
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "tagger-post", builder => BuildTaggerPost(builder));
            DeckFlowResiliencePipelineRegistry.AddResiliencePipeline<string, RestResponse>(services, "scryfall", builder => BuildScryfall(builder));
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
        private static void BuildScryfall(ResiliencePipelineBuilder<RestResponse> builder) => builder
            // Total budget - wraps retries; individual attempts have no separate per-try timeout
            // (handler-level timeout disabled per D-08 pattern). Name used in Polly telemetry.
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
                Name = "scryfall-total",
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
        private static bool registered;

        public static IServiceCollection Register(IServiceCollection services)
        {
            if (!registered)
            {
                services.AddSingleton(Registry);
                services.AddSingleton(Provider);
                registered = true;
            }

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
