namespace DeckFlow.Core.Analysis;

/// <summary>
/// One protection oracle needle: the substring <see cref="DeckStatClassifier.IsProtectionCard"/>
/// matches, the effect it detects, and the subject-number form the phrasing assumes. Making the
/// needle table data (rather than an inline <c>||</c> chain) is what lets a later needle addition
/// be machine-checked for a paired subject form, instead of silently leaving a verb form unpaired.
/// </summary>
public sealed record ProtectionNeedle
{
    /// <summary>
    /// The oracle-text substring matched via <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// No regex, no state machine — exactly the <c>Contains()</c> semantics every other
    /// <see cref="DeckStatClassifier"/> predicate uses.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// The protection effect this needle detects. Documented allowed values: <c>hexproof</c>,
    /// <c>indestructible</c>, <c>protection-from</c>, <c>phase-out</c>.
    /// </summary>
    public required string Effect { get; init; }

    /// <summary>
    /// The grammatical subject number the phrasing assumes. Documented allowed values:
    /// <c>singular</c>, <c>plural</c>, <c>none</c> (for phrasings with no subject-number
    /// distinction).
    /// </summary>
    public required string SubjectForm { get; init; }
}
