using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Text.Json.Serialization;

namespace DeckFlow.Web.Models;

/// <summary>
/// Form-bound request DTO for creator-style artifact generation.
/// </summary>
public sealed class CreatorStyleRequest
{
    private string _creatorSlug = string.Empty;
    private string _deckText = string.Empty;
    private string _deckUrl = string.Empty;
    private string _format = "Commander";

    /// <summary>
    /// Selects whether the deck is supplied via a public URL or pasted export text.
    /// </summary>
    public DeckInputSource DeckInputSource { get; set; } = DeckInputSource.PasteText;

    /// <summary>
    /// Creator slug whose style profile should be loaded.
    /// </summary>
    public string CreatorSlug
    {
        get => _creatorSlug;
        set => _creatorSlug = value ?? string.Empty;
    }

    /// <summary>
    /// Public deck URL used when <see cref="DeckInputSource"/> is <see cref="DeckInputSource.PublicUrl"/>.
    /// </summary>
    public string DeckUrl
    {
        get => _deckUrl;
        set => _deckUrl = value ?? string.Empty;
    }

    /// <summary>
    /// Pasted deck export text used when <see cref="DeckInputSource"/> is <see cref="DeckInputSource.PasteText"/>.
    /// </summary>
    public string DeckText
    {
        get => _deckText;
        set => _deckText = value ?? string.Empty;
    }

    /// <summary>
    /// Returns the raw deck input routed through <see cref="DeckUrl"/> or <see cref="DeckText"/>
    /// based on <see cref="DeckInputSource"/>.
    /// </summary>
    /// <remarks>
    /// Why (WR-12): this is a computed, read-only projection, not a settable field. A settable
    /// <c>DeckSource</c> on a form-bound DTO would let a posted <c>DeckSource</c> field populate
    /// <see cref="DeckUrl"/> depending on whichever order the model binder happens to visit
    /// properties in, bypassing whatever validation the caller expected on <see cref="DeckUrl"/>
    /// or <see cref="DeckText"/> directly. Callers must set those explicitly.
    /// </remarks>
    [BindNever]
    [JsonIgnore]
    public string DeckSource => DeckInputSource == DeckInputSource.PublicUrl ? _deckUrl : _deckText;

    /// <summary>
    /// Magic: The Gathering format the deck targets; defaults to "Commander".
    /// </summary>
    public string Format
    {
        get => _format;
        set => _format = value ?? "Commander";
    }
}
