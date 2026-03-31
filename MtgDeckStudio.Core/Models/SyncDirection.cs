namespace MtgDeckStudio.Core.Models;

/// <summary>
/// Defines which deck systems are being compared and which side should be treated as the source.
/// </summary>
public enum SyncDirection
{
    /// <summary>
    /// Compares a Moxfield deck against an Archidekt deck.
    /// </summary>
    MoxfieldToArchidekt,

    /// <summary>
    /// Compares an Archidekt deck against a Moxfield deck.
    /// </summary>
    ArchidektToMoxfield,

    /// <summary>
    /// Compares one Moxfield deck against another Moxfield deck.
    /// </summary>
    MoxfieldToMoxfield,

    /// <summary>
    /// Compares one Archidekt deck against another Archidekt deck.
    /// </summary>
    ArchidektToArchidekt,
}
