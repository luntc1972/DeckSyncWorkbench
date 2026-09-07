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
    /// The protection effect this needle detects. Allowed values are whatever effects
    /// <see cref="DeckStatClassifier.ProtectionOracleNeedles"/> currently carries — that table is
    /// the single source of truth, so this doc does not enumerate a closed set that would drift
    /// every time a needle is added.
    /// </summary>
    public required string Effect { get; init; }

    /// <summary>
    /// The grammatical subject number the phrasing assumes. Documented allowed values:
    /// <c>singular</c>, <c>plural</c>, <c>none</c> (for phrasings with no subject-number
    /// distinction).
    /// </summary>
    public required string SubjectForm { get; init; }

    /// <summary>
    /// <see langword="true"/> for the four needles the classifier carried before Phase 9.1 widened
    /// it to the corpus-derived table. This is the single source of truth for "the historical
    /// narrow vocabulary" — consumers that need that frozen four-needle snapshot (the CLI's
    /// disclosure report, the blast-radius test's before/after measurement) filter this flag
    /// instead of each re-typing the four strings.
    /// </summary>
    public bool IsPreWideningBaseline { get; init; }
}
