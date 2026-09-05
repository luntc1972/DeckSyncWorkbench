namespace DeckFlow.Web.Models.DeckModules;

/// <summary>
/// Request to import exactly one public deck URL or pasted decklist as the browser-session
/// Deck Modules baseline. The imported command zone is treated as immutable from this point on.
/// </summary>
public sealed record DeckModulesImportRequest
{
    /// <summary>Gets the maximum accepted length of <see cref="Url"/>.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>Gets the maximum accepted length of <see cref="PasteText"/>.</summary>
    public const int MaxPasteTextLength = 100_000;

    /// <summary>Gets which input is active: a public deck URL or pasted decklist text.</summary>
    public required DeckInputSource ActiveSource { get; init; }

    /// <summary>Gets the public deck URL, used when <see cref="ActiveSource"/> is <see cref="DeckInputSource.PublicUrl"/>.</summary>
    public string? Url { get; init; }

    /// <summary>Gets the pasted decklist export text, used when <see cref="ActiveSource"/> is <see cref="DeckInputSource.PasteText"/>.</summary>
    public string? PasteText { get; init; }
}
