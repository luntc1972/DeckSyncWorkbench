namespace DeckFlow.Core.Manabase;

/// <summary>
/// The minimal per-card data the classifier needs, shaped after Scryfall's card fields.
/// The Web layer fills these from its Scryfall adapter; <see cref="DeckFlow.Core"/> stays
/// HTTP-free and just consumes the facts.
/// </summary>
public sealed record CardFact
{
    /// <summary>Card name.</summary>
    public required string Name { get; init; }

    /// <summary>Copies of this card in the deck.</summary>
    public required int Quantity { get; init; }

    /// <summary>Scryfall mana cost of the front face (e.g. <c>{2}{U}{U}</c>); null for lands.</summary>
    public string? ManaCost { get; init; }

    /// <summary>Scryfall mana value (cmc) of the front face.</summary>
    public double ManaValue { get; init; }

    /// <summary>Scryfall type line (e.g. "Legendary Creature — Elf Druid", "Land").</summary>
    public required string TypeLine { get; init; }

    /// <summary>Scryfall oracle text (joined across faces), used for ramp/dork/rock heuristics.</summary>
    public string? OracleText { get; init; }

    /// <summary>
    /// FRONT-face oracle text only (the permanent/castable face). Falls back to <see cref="OracleText"/>
    /// for single-face cards. Used where joined text would leak a back/adventure face's mana into a
    /// front-face permanent check (MQ-03): a creature with a one-shot mana adventure must NOT read as
    /// repeatable ramp. Null is treated as "use OracleText".
    /// </summary>
    public string? FrontFaceOracleText { get; init; }

    /// <summary>
    /// Oracle text of the card's LAND face (the front for a front-face land; the MDFC back face for
    /// a spell//land card). Null when the card has no land face. Used for precise tapped/pay-life
    /// detection without cross-face bleed from joined OracleText.
    /// </summary>
    public string? LandFaceOracleText { get; init; }

    /// <summary>Scryfall <c>produced_mana</c> letters (e.g. ["U","R","G"]); empty if none.</summary>
    public IReadOnlyList<string> ProducedMana { get; init; } = Array.Empty<string>();

    /// <summary>Scryfall rarity ("common", "uncommon", "rare", "mythic"). Populated from Scryfall; not read by the analyzer.</summary>
    public string? Rarity { get; init; }

    /// <summary>Scryfall layout ("normal", "modal_dfc", "transform", "split", ...).</summary>
    public string? Layout { get; init; }

    /// <summary>True if any face of the card is a land (front, or the back of an MDFC).</summary>
    public bool HasLandFace { get; init; }

    /// <summary>True when in the command zone (commander) rather than the library.</summary>
    public bool IsCommander { get; init; }

    /// <summary>
    /// Mana produced per activation when this card is a mana source (MQ-02): Sol Ring / Ancient
    /// Tomb = 2, Gilded Lotus = 3, a normal land/dork = 1. Defaults to 1. Parsed from oracle text by
    /// <see cref="ManaProductionAmount"/>; unused for non-source cards.
    /// </summary>
    public int ManaAmount { get; init; } = 1;

    /// <summary>
    /// Fixed printed power of the front face when this card is a creature with a numeric power
    /// (e.g. 5). Null for non-creatures and for variable power ("*", as on *goyf cards). Used to
    /// resolve board-scaling self cost reducers that read "costs {X} less, where X is the greatest
    /// power among creatures you control" (e.g. The Skullspore Nexus).
    /// </summary>
    public int? Power { get; init; }

    /// <summary>
    /// The oracle text to read for front-face-sensitive heuristics: <see cref="FrontFaceOracleText"/>
    /// when set, else <see cref="OracleText"/>, else empty — the fallback documented on
    /// <see cref="FrontFaceOracleText"/>. Every classifier that needs front-face-only text reads this
    /// instead of re-deriving the same two-property fallback at each call site.
    /// </summary>
    public string FrontOracleText => FrontFaceOracleText ?? OracleText ?? string.Empty;
}
