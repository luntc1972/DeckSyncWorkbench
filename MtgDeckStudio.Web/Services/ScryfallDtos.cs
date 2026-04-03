using System.Text.Json.Serialization;

namespace MtgDeckStudio.Web.Services;

/// <summary>
/// Container for a Scryfall card search response.
/// </summary>
public sealed record ScryfallSearchResponse(
    List<ScryfallCard> Data,
    [property: JsonPropertyName("has_more")] bool HasMore = false,
    [property: JsonPropertyName("next_page")] string? NextPage = null);

/// <summary>
/// Container for the Scryfall sets endpoint.
/// </summary>
public sealed record ScryfallSetListResponse(List<ScryfallSet> Data);

/// <summary>
/// Represents a Scryfall set payload.
/// </summary>
public sealed record ScryfallSet(
    string Code,
    string Name,
    [property: JsonPropertyName("released_at")] string? ReleasedAt,
    [property: JsonPropertyName("set_type")] string? SetType,
    [property: JsonPropertyName("card_count")] int CardCount,
    [property: JsonPropertyName("digital")] bool Digital);

/// <summary>
/// Container for a Scryfall collection lookup response.
/// </summary>
public sealed record ScryfallCollectionResponse(
    List<ScryfallCard> Data,
    [property: JsonPropertyName("not_found")] List<ScryfallCollectionIdentifier>? NotFound);

/// <summary>
/// Represents a Scryfall card payload.
/// </summary>

public sealed record ScryfallCard(
    string Name,
    [property: JsonPropertyName("mana_cost")] string? ManaCost,
    [property: JsonPropertyName("type_line")] string TypeLine,
    [property: JsonPropertyName("oracle_text")] string? OracleText,
    [property: JsonPropertyName("power")] string? Power,
    [property: JsonPropertyName("toughness")] string? Toughness,
    [property: JsonPropertyName("keywords")] IReadOnlyList<string>? Keywords,
    [property: JsonPropertyName("color_identity")] IReadOnlyList<string>? ColorIdentity,
    [property: JsonPropertyName("set")] string? SetCode,
    [property: JsonPropertyName("set_name")] string? SetName,
    [property: JsonPropertyName("collector_number")] string? CollectorNumber,
    [property: JsonPropertyName("card_faces")] IReadOnlyList<ScryfallCardFace>? CardFaces = null);

public sealed record ScryfallCardFace(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mana_cost")] string? ManaCost,
    [property: JsonPropertyName("type_line")] string? TypeLine,
    [property: JsonPropertyName("oracle_text")] string? OracleText,
    [property: JsonPropertyName("power")] string? Power,
    [property: JsonPropertyName("toughness")] string? Toughness);

/// <summary>
/// Represents an identifier Scryfall could not resolve from a collection request.
/// </summary>
public sealed record ScryfallCollectionIdentifier(string? Name);

/// <summary>
/// Identifies a specific printing of a card by set code and collector number for a collection lookup.
/// </summary>
public sealed record ScryfallPrintingIdentifier(
    [property: JsonPropertyName("set")] string Set,
    [property: JsonPropertyName("collector_number")] string CollectorNumber);
