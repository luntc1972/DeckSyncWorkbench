using System.Text.Json.Serialization;

namespace DeckFlow.Web.Services;

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
    [property: JsonPropertyName("not_found")] List<ScryfallCollectionNameIdentifier>? NotFound);

/// <summary>
/// Represents a Scryfall card payload.
/// </summary>
/// <remarks>
/// WR-10: every parameter from <c>CardFaces</c> onward is optional (<c>string?</c> or has a
/// default), and 51 call sites across the solution construct this positionally through
/// <c>CardFaces</c> (index 11) then switch to named arguments for everything after. Inserting a new
/// optional parameter anywhere before the end -- as <c>OracleId</c> once was, between <c>Id</c> and
/// <c>Layout</c> -- would silently rebind every later positional argument at any call site that ever
/// passes index 13+ positionally, with no compiler error. APPEND new optional parameters to the end
/// of this list only; never insert.
/// </remarks>
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
    [property: JsonPropertyName("card_faces")] IReadOnlyList<ScryfallCardFace>? CardFaces = null,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("layout")] string? Layout = null,
    [property: JsonPropertyName("released_at")] string? ReleasedAt = null,
    [property: JsonPropertyName("cmc")] double Cmc = 0,
    [property: JsonPropertyName("produced_mana")] IReadOnlyList<string>? ProducedMana = null,
    [property: JsonPropertyName("rarity")] string? Rarity = null,
    [property: JsonPropertyName("oracle_id")] string? OracleId = null,
    [property: JsonPropertyName("legalities")]
    IReadOnlyDictionary<string, string>? Legalities = null);

/// <summary>
/// Container for a Scryfall rulings list response.
/// </summary>
public sealed record ScryfallRulingsResponse(List<ScryfallRuling> Data);

/// <summary>
/// Represents a single Scryfall ruling (WOTC-sourced clarification).
/// </summary>
public sealed record ScryfallRuling(
    [property: JsonPropertyName("published_at")] string? PublishedAt,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("comment")] string? Comment);

/// <summary>
/// Represents one face of a multi-faced Scryfall card payload, such as a double-faced, split, or adventure card.
/// </summary>
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
public sealed record ScryfallCollectionNameIdentifier(
    [property: JsonPropertyName("name")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonPropertyName("set")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Set = null,
    [property: JsonPropertyName("collector_number")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CollectorNumber = null)
{
    /// <summary>
    /// Creates a name identifier.
    /// </summary>
    public static ScryfallCollectionNameIdentifier ForName(string name) => new(Name: name);

    /// <summary>
    /// Creates a printing identifier.
    /// </summary>
    public static ScryfallCollectionNameIdentifier ForPrinting(string set, string collectorNumber) =>
        new(Set: set, CollectorNumber: collectorNumber);

    /// <summary>
    /// Gets the identifier label for unresolved-card diagnostics.
    /// </summary>
    public string Label => Name ?? $"{Set} #{CollectorNumber}";
}

/// <summary>
/// Identifies a specific printing of a card by set code and collector number for a collection lookup.
/// </summary>
public sealed record ScryfallPrintingIdentifier(
    [property: JsonPropertyName("set")] string Set,
    [property: JsonPropertyName("collector_number")] string CollectorNumber);
