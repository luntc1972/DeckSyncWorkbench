using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;

namespace DeckFlow.Web.Services;

/// <summary>
/// Encapsulates commander name search lookups for the web UI.
/// </summary>
public interface ICommanderSearchService
{
    /// <summary>
    /// Searches for commander names that match the query.
    /// </summary>
    /// <param name="query">Partial commander name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides Scryfall-powered commander name suggestions.
/// </summary>
public sealed class ScryfallCommanderSearchService : ICommanderSearchService
{
    private const int SuggestionLimit = 20;
    private readonly IMemoryCache _cache;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>> _executeAsync;

    /// <summary>
    /// Initializes the service with the provided cache and optional REST client.
    /// </summary>
    /// <param name="cache">Memory cache for storing lookups.</param>
    /// <param name="restClient">Optional REST client to use (tests can supply a fake client).</param>
    /// <param name="executeAsync">Optional handler to execute RestSharp requests for easier testing.</param>
    public ScryfallCommanderSearchService(
        IMemoryCache cache,
        RestClient? restClient = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeAsync = null)
    {
        _cache = cache;
        var client = restClient ?? ScryfallRestClientFactory.Create();

        _executeAsync = executeAsync ?? ((request, cancellationToken) => ScryfallThrottle.ExecuteAsync(token => client.ExecuteAsync<ScryfallSearchResponse>(request, token), cancellationToken));
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
        request.AddQueryParameter("q", $"is:commander type:legendary (type:creature or type:vehicle) name:{query}");
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
}
