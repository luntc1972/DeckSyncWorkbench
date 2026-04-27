using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using Polly;
using Polly.Registry;
using RestSharp;
using DeckFlow.Web.Services.Http;

namespace DeckFlow.Web.Services;

/// <summary>
/// Searches for card names via the Scryfall API.
/// </summary>
public interface ICardSearchService
{
    /// <summary>
    /// Returns card name suggestions that match the provided query.
    /// </summary>
    Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns commander-eligible card name suggestions (legendary creatures and planeswalkers that can be commanders) matching the query.
    /// </summary>
    Task<IReadOnlyList<string>> SearchCommandersAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides card name suggestions using Scryfall's cards/search endpoint.
/// </summary>
public sealed class ScryfallCardSearchService : ICardSearchService
{
    private const int SuggestionLimit = 20;
    private readonly IMemoryCache _cache;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>> _executeAsync;

    /// <summary>
    /// Private ctor accepting a pre-resolved Polly pipeline. Used by both the production ctor
    /// and the public test-compat overload.
    /// </summary>
    private ScryfallCardSearchService(
        IScryfallRestClientFactory scryfallRestClientFactory,
        ResiliencePipeline<RestResponse> scryfallPipeline,
        IMemoryCache cache,
        RestClient? restClientOverride,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeAsyncOverride)
    {
        ArgumentNullException.ThrowIfNull(scryfallRestClientFactory);
        ArgumentNullException.ThrowIfNull(cache);
        var pipeline = scryfallPipeline ?? ResiliencePipeline<RestResponse>.Empty;
        _cache = cache;
        var client = restClientOverride ?? scryfallRestClientFactory.Create();
        _executeAsync = executeAsyncOverride ?? ((request, cancellationToken) =>
            ScryfallThrottle.ExecuteAsync(
                token => pipeline.ExecuteAsync(
                    async pollyCt => await client.ExecuteAsync<ScryfallSearchResponse>(request, pollyCt).ConfigureAwait(false),
                    token).AsTask(),
                cancellationToken));
    }

    /// <summary>
    /// Production ctor — DI resolves IScryfallRestClientFactory + named "scryfall"
    /// pipeline from ResiliencePipelineProvider&lt;string&gt;.
    /// </summary>
    public ScryfallCardSearchService(
        IScryfallRestClientFactory scryfallRestClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        IMemoryCache cache)
        : this(
            scryfallRestClientFactory,
            pipelineProvider?.GetPipeline<RestResponse>("scryfall") ?? ResiliencePipeline<RestResponse>.Empty,
            cache,
            null,
            null)
    {
        ArgumentNullException.ThrowIfNull(pipelineProvider);
    }

    /// <summary>
    /// Internal test-compat overload preserving the legacy
    /// <c>(IMemoryCache, RestClient?, Func&lt;...&gt;?)</c> signature used by
    /// <c>ScryfallCardSearchServiceTests</c>. Internal per pre-existing AssemblyInfo
    /// InternalsVisibleTo for DeckFlow.Web.Tests - public would create a DI ctor-resolution
    /// ambiguity at startup. Uses <see cref="ResiliencePipeline{T}.Empty"/> directly per MEDIUM-4.
    /// </summary>
    internal ScryfallCardSearchService(
        IMemoryCache cache,
        RestClient? restClient = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeAsync = null)
        : this(
            NullScryfallRestClientFactory.Instance,
            ResiliencePipeline<RestResponse>.Empty,
            cache,
            restClient,
            executeAsync)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            return Array.Empty<string>();
        }

        var normalized = query.Trim().ToLowerInvariant();
        if (_cache.TryGetValue(normalized, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        var request = new RestRequest("cards/search", Method.Get);
        request.AddQueryParameter("q", $"name:{query}");
        request.AddQueryParameter("order", "name");
        request.AddQueryParameter("unique", "cards");
        request.AddQueryParameter("include_extras", "false");
        request.AddQueryParameter("include_multilingual", "false");

        var response = await _executeAsync(request, cancellationToken);
        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
        {
            throw new HttpRequestException(
                $"Scryfall search returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var names = response.Data?.Data?
            .Select(card => card.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(SuggestionLimit)
            .ToList() ?? new List<string>();

        _cache.Set(normalized, names, TimeSpan.FromMinutes(10));
        return names;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> SearchCommandersAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            return Array.Empty<string>();
        }

        var cacheKey = $"commander:{query.Trim().ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        var request = new RestRequest("cards/search", Method.Get);
        request.AddQueryParameter("q", $"is:commander name:{query}");
        request.AddQueryParameter("order", "name");
        request.AddQueryParameter("unique", "cards");
        request.AddQueryParameter("include_extras", "false");
        request.AddQueryParameter("include_multilingual", "false");

        var response = await _executeAsync(request, cancellationToken);
        if ((int)response.StatusCode == 404)
        {
            _cache.Set(cacheKey, (IReadOnlyList<string>)Array.Empty<string>(), TimeSpan.FromMinutes(10));
            return Array.Empty<string>();
        }

        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
        {
            throw new HttpRequestException(
                $"Scryfall search returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var names = response.Data?.Data?
            .Select(card => card.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(SuggestionLimit)
            .ToList() ?? new List<string>();

        _cache.Set(cacheKey, names, TimeSpan.FromMinutes(10));
        return names;
    }
}
