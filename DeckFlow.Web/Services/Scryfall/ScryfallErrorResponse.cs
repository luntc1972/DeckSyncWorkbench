using System.Text.Json.Serialization;

namespace DeckFlow.Web.Services;

/// <summary>
/// Represents a Scryfall error payload for named-card lookups.
/// </summary>
/// <remarks>
/// Ambiguous fuzzy 404s are distinguished by <c>type == "ambiguous"</c>; both ambiguous and plain
/// not-found payloads still report <c>code == "not_found"</c>.
/// </remarks>
public sealed record ScryfallErrorResponse(
    [property: JsonPropertyName("object")] string? Object,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("status")] int? Status = null,
    [property: JsonPropertyName("details")] string? Details = null);
