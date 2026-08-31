using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using RestSharp;

namespace DeckFlow.Web.Services.CreatorStyle;

/// <summary>
/// Deck summaries obtained from Archidekt, including upstream enumeration status.
/// </summary>
public sealed record ArchidektDeckListResult
{
    /// <summary>Discovered deck summaries, including any successfully collected before a failure.</summary>
    public required IReadOnlyList<ArchidektDeckSummary> Decks { get; init; }

    /// <summary>Whether an Archidekt response could not be enumerated completely.</summary>
    public required bool HasUpstreamFailure { get; init; }
}

/// <summary>
/// Resolves creator usernames and enumerates public Archidekt deck summaries.
/// </summary>
public interface IArchidektOwnerClient
{
    /// <summary>
    /// Resolves the canonical Archidekt username from a username or profile URL.
    /// </summary>
    /// <param name="usernameOrUrl">Username or Archidekt profile URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved username when found; otherwise <see langword="null"/>.</returns>
    Task<string?> ResolveUsernameAsync(string usernameOrUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists public deck summaries for an Archidekt owner.
    /// </summary>
    /// <param name="ownerUsername">Owner username.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered deck summaries.</returns>
    Task<ArchidektDeckListResult> ListDeckSummariesAsync(string ownerUsername, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches Archidekt owner metadata and public deck summaries with capped JSON parsing input.
/// </summary>
public sealed class ArchidektOwnerClient : IArchidektOwnerClient
{
    internal const int MaxPages = 20;
    internal const int MaxDecks = 500;
    internal const int PageSize = 50;
    internal const int MaxResponseBytes = 5 * 1024 * 1024;

    private readonly RestClient _restClient;
    private readonly ResiliencePipeline<RestResponse> _resiliencePipeline;
    private readonly ILogger<ArchidektOwnerClient> _logger;

    /// <summary>
    /// Creates an Archidekt owner client.
    /// </summary>
    /// <param name="httpClientFactory">Named HTTP client factory.</param>
    /// <param name="pipelineProvider">Named resilience pipeline provider.</param>
    /// <param name="logger">Optional logger.</param>
    public ArchidektOwnerClient(
        IHttpClientFactory httpClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<ArchidektOwnerClient>? logger = null)
        : this(
            pipelineProvider,
            new RestClient(CreateNamedClient(httpClientFactory)),
            logger)
    {
    }

    internal ArchidektOwnerClient(
        ResiliencePipelineProvider<string> pipelineProvider,
        RestClient restClient,
        ILogger<ArchidektOwnerClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pipelineProvider);
        ArgumentNullException.ThrowIfNull(restClient);
        _restClient = restClient;
        _resiliencePipeline = pipelineProvider.GetPipeline<RestResponse>("archidekt") ?? ResiliencePipeline<RestResponse>.Empty;
        _logger = logger ?? NullLogger<ArchidektOwnerClient>.Instance;
    }

    /// <inheritdoc />
    public async Task<string?> ResolveUsernameAsync(string usernameOrUrl, CancellationToken cancellationToken = default)
    {
        if (!ArchidektOwnerUrl.TryGetUsername(usernameOrUrl, out var requestedUsername))
        {
            return null;
        }

        var request = new RestRequest("api/users/", Method.Get);
        request.AddQueryParameter("username", requestedUsername);
        request.AddHeader("Accept", "application/json");

        var response = await _resiliencePipeline.ExecuteAsync(
            async ct => await _restClient.ExecuteAsync(request, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessful)
        {
            _logger.LogWarning(
                "Archidekt owner resolve failed for {Username}: HTTP {StatusCode}.",
                requestedUsername,
                (int)response.StatusCode);
            return null;
        }

        if (!TryGetResponseContent(response, "resolve", out var content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                return null;
            }

            var first = results[0];
            if (!first.TryGetProperty("username", out var usernameElement))
            {
                return null;
            }

            var resolvedUsername = usernameElement.GetString();
            // Why: /api/users is a search endpoint; only an exact match resolves an owner.
            return string.Equals(resolvedUsername, requestedUsername, StringComparison.OrdinalIgnoreCase)
                ? resolvedUsername
                : null;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Archidekt owner resolve returned malformed JSON for {Username}.", requestedUsername);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ArchidektDeckListResult> ListDeckSummariesAsync(string ownerUsername, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUsername);

        var decks = new List<ArchidektDeckSummary>();
        var page = 1;
        string? next = string.Empty;

        while (page <= MaxPages && decks.Count < MaxDecks && next is not null)
        {
            var request = new RestRequest("api/decks/v3/", Method.Get);
            request.AddQueryParameter("ownerUsername", ownerUsername);
            request.AddQueryParameter("pageSize", PageSize);
            request.AddQueryParameter("page", page);
            request.AddHeader("Accept", "application/json");

            var response = await _resiliencePipeline.ExecuteAsync(
                async ct => await _restClient.ExecuteAsync(request, ct).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessful)
            {
                _logger.LogWarning(
                    "Archidekt owner deck list failed for {Username} page {Page}: HTTP {StatusCode}.",
                    ownerUsername,
                    page,
                    (int)response.StatusCode);
                return new ArchidektDeckListResult { Decks = decks, HasUpstreamFailure = true };
            }

            if (!TryGetResponseContent(response, "list", out var content))
            {
                return new ArchidektDeckListResult { Decks = decks, HasUpstreamFailure = true };
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                next = document.RootElement.TryGetProperty("next", out var nextElement) && nextElement.ValueKind != JsonValueKind.Null
                    ? nextElement.GetString()
                    : null;

                if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                foreach (var item in results.EnumerateArray())
                {
                    var summary = new ArchidektDeckSummary
                    {
                        Id = ReadString(item, "id"),
                        Name = ReadString(item, "name"),
                        Size = ReadInt32(item, "size"),
                        ParentFolderId = ReadNullableInt32(item, "parentFolderId"),
                        ParentFolderName = ReadNullableString(item, "parentFolderName")
                    };

                    if (string.IsNullOrWhiteSpace(summary.Id) || string.IsNullOrWhiteSpace(summary.Name))
                    {
                        continue;
                    }

                    decks.Add(summary);
                    if (decks.Count == MaxDecks)
                    {
                        break;
                    }
                }
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Archidekt owner deck list returned malformed JSON for {Username} page {Page}.", ownerUsername, page);
                return new ArchidektDeckListResult { Decks = decks, HasUpstreamFailure = true };
            }

            page += 1;
        }

        return new ArchidektDeckListResult { Decks = decks, HasUpstreamFailure = false };
    }

    private bool TryGetResponseContent(RestResponse response, string operation, out string content)
    {
        content = response.Content ?? string.Empty;
        var byteCount = response.RawBytes?.LongLength ?? Encoding.UTF8.GetByteCount(content);
        if (byteCount > MaxResponseBytes)
        {
            _logger.LogWarning(
                "Archidekt owner {Operation} payload exceeded the {MaxResponseBytes} byte cap ({ByteCount}).",
                operation,
                MaxResponseBytes,
                byteCount);
            return false;
        }

        return true;
    }

    private static HttpClient CreateNamedClient(IHttpClientFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.CreateClient("archidekt-owner");
    }

    private static string ReadString(JsonElement item, string propertyName)
    {
        return ReadNullableString(item, propertyName) ?? string.Empty;
    }

    private static string? ReadNullableString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static int ReadInt32(JsonElement item, string propertyName)
    {
        return ReadNullableInt32(item, propertyName) ?? 0;
    }

    private static int? ReadNullableInt32(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
        {
            return value;
        }

        return null;
    }
}

/// <summary>
/// Lightweight summary of an owner's public Archidekt deck.
/// </summary>
public sealed record ArchidektDeckSummary
{
    /// <summary>Stable deck identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Deck size reported by Archidekt.</summary>
    public required int Size { get; init; }

    /// <summary>Optional parent-folder identifier.</summary>
    public int? ParentFolderId { get; init; }

    /// <summary>Optional parent-folder display name.</summary>
    public string? ParentFolderName { get; init; }
}
