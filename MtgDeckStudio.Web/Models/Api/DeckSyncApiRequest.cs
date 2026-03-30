using System.ComponentModel.DataAnnotations;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Web.Models;

namespace MtgDeckStudio.Web.Models.Api;

/// <summary>
/// Request payload for deck sync and diff generation.
/// </summary>
public sealed record DeckSyncApiRequest
{
    /// <summary>
    /// Determines whether the sync runs from Moxfield to Archidekt or the reverse.
    /// </summary>
    public SyncDirection Direction { get; init; } = SyncDirection.MoxfieldToArchidekt;

    /// <summary>
    /// Match strategy used when comparing source and target cards.
    /// </summary>
    public MatchMode Mode { get; init; } = MatchMode.Loose;

    /// <summary>
    /// Controls whether generated output should favor source tags, target categories, or both.
    /// </summary>
    public CategorySyncMode CategorySyncMode { get; init; } = CategorySyncMode.TargetCategories;

    /// <summary>
    /// Describes whether the Moxfield deck is supplied as pasted text or a public URL.
    /// </summary>
    public DeckInputSource MoxfieldInputSource { get; init; } = DeckInputSource.PasteText;

    /// <summary>
    /// Pasted Moxfield export text when <see cref="MoxfieldInputSource"/> is <see cref="DeckInputSource.PasteText"/>.
    /// </summary>
    public string? MoxfieldText { get; init; }

    /// <summary>
    /// Public Moxfield deck URL when <see cref="MoxfieldInputSource"/> is <see cref="DeckInputSource.PublicUrl"/>.
    /// </summary>
    public string? MoxfieldUrl { get; init; }

    /// <summary>
    /// Describes whether the Archidekt deck is supplied as pasted text or a public URL.
    /// </summary>
    public DeckInputSource ArchidektInputSource { get; init; } = DeckInputSource.PasteText;

    /// <summary>
    /// Pasted Archidekt export text when <see cref="ArchidektInputSource"/> is <see cref="DeckInputSource.PasteText"/>.
    /// </summary>
    public string? ArchidektText { get; init; }

    /// <summary>
    /// Public Archidekt deck URL when <see cref="ArchidektInputSource"/> is <see cref="DeckInputSource.PublicUrl"/>.
    /// </summary>
    public string? ArchidektUrl { get; init; }

    /// <summary>
    /// Converts the API payload into the MVC deck diff request model used by the shared service layer.
    /// </summary>
    public DeckDiffRequest ToDeckDiffRequest()
    {
        return new DeckDiffRequest
        {
            Direction = Direction,
            Mode = Mode,
            CategorySyncMode = CategorySyncMode,
            MoxfieldInputSource = MoxfieldInputSource,
            MoxfieldText = MoxfieldText ?? string.Empty,
            MoxfieldUrl = MoxfieldUrl ?? string.Empty,
            ArchidektInputSource = ArchidektInputSource,
            ArchidektText = ArchidektText ?? string.Empty,
            ArchidektUrl = ArchidektUrl ?? string.Empty,
        };
    }
}
