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
    public string DeckSource
    {
        get => DeckInputSource == DeckInputSource.PublicUrl ? _deckUrl : _deckText;
        set
        {
            var normalized = value ?? string.Empty;
            if (DeckInputSource == DeckInputSource.PublicUrl)
            {
                _deckUrl = normalized;
            }
            else
            {
                _deckText = normalized;
            }
        }
    }

    /// <summary>
    /// Magic: The Gathering format the deck targets; defaults to "Commander".
    /// </summary>
    public string Format
    {
        get => _format;
        set => _format = value ?? "Commander";
    }
}
