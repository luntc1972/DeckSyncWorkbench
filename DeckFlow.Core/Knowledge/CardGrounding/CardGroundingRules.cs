using System.Text.RegularExpressions;
using DeckFlow.Core.Normalization;

namespace DeckFlow.Core.Knowledge.CardGrounding;

/// <summary>
/// Pure decision rules for strict card grounding.
/// </summary>
public static partial class CardGroundingRules
{
    private const string ColoredManaPips = "WUBRG";

    [GeneratedRegex(@"\{([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ManaSymbolRegex();

    /// <summary>
    /// Determines whether a card is legal in Commander using Scryfall legality data.
    /// </summary>
    /// <param name="legalities">Scryfall legality map keyed by format name.</param>
    /// <returns><see langword="true"/> only when the commander legality entry exists and is <c>legal</c>.</returns>
    public static bool IsLegalForCommander(IReadOnlyDictionary<string, string>? legalities)
    {
        return legalities is not null
            && legalities.TryGetValue("commander", out var status)
            && string.Equals(status, "legal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a card's color identity fits within the commander's color identity.
    /// </summary>
    /// <param name="cardColorIdentity">Card color identity in Scryfall WUBRG string form.</param>
    /// <param name="commanderIdentity">Commander's color identity in Scryfall WUBRG string form.</param>
    /// <returns><see langword="true"/> when the card identity is colorless or a subset of the commander identity.</returns>
    public static bool IsWithinColorIdentity(IReadOnlyList<string>? cardColorIdentity, IReadOnlySet<string> commanderIdentity)
    {
        ArgumentNullException.ThrowIfNull(commanderIdentity);

        if (cardColorIdentity is null || cardColorIdentity.Count == 0)
        {
            return true;
        }

        foreach (var color in cardColorIdentity)
        {
            if (!commanderIdentity.Contains(color))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether a card would violate singleton constraints in the submitted deck.
    /// </summary>
    /// <param name="canonicalName">Canonical card name to check.</param>
    /// <param name="typeLine">Resolved Scryfall type line.</param>
    /// <param name="deckCardNames">
    /// Existing deck names populated with <see cref="CardNormalizer.Normalize(string)"/> outputs so duplicate
    /// checks match punctuation-collapsed, DFC-front-face, star/foil-stripped names exactly.
    /// </param>
    /// <returns><see langword="true"/> when the card is already present and not exempt as a basic land.</returns>
    public static bool IsSingletonViolation(string canonicalName, string typeLine, IReadOnlySet<string> deckCardNames)
    {
        ArgumentNullException.ThrowIfNull(canonicalName);
        ArgumentNullException.ThrowIfNull(typeLine);
        ArgumentNullException.ThrowIfNull(deckCardNames);

        // Why: Scryfall's type line is the authoritative marker for basic-land status, so this stays correct
        // across named basics and snow basics without hardcoding a brittle card-name allowlist.
        if (typeLine.Contains("Basic Land", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedName = CardNormalizer.Normalize(canonicalName);
        return deckCardNames.Contains(normalizedName);
    }

    /// <summary>
    /// Determines whether the deck can currently produce every colored mana pip in the card's mana cost,
    /// honoring Magic's payment alternatives: a hybrid symbol (<c>{W/U}</c>) requires only one of its
    /// colors, and a Phyrexian (<c>{W/P}</c>) or twobrid (<c>{2/W}</c>) symbol is always payable via its
    /// unconditional life/generic alternative regardless of produced colors.
    /// </summary>
    /// <param name="manaCost">Scryfall mana-cost string.</param>
    /// <param name="deckProducedColors">WUBRG colors the submitted deck can already produce.</param>
    /// <returns><see langword="true"/> when every colored-pip requirement in the cost is satisfiable.</returns>
    public static bool IsCastable(string? manaCost, IReadOnlySet<char> deckProducedColors)
    {
        ArgumentNullException.ThrowIfNull(deckProducedColors);

        if (string.IsNullOrEmpty(manaCost))
        {
            return true;
        }

        foreach (Match match in ManaSymbolRegex().Matches(manaCost))
        {
            var parts = match.Groups[1].Value.Split('/');
            var coloredParts = parts.Where(part => part.Length == 1 && ColoredManaPips.Contains(part[0])).ToArray();

            if (coloredParts.Length == 0)
            {
                // Not a colored-pip requirement: generic {3}, variable {X}, colorless {C}, snow {S}.
                continue;
            }

            if (parts.Length == 1)
            {
                // Plain colored pip: the single color must be producible.
                if (!deckProducedColors.Contains(coloredParts[0][0]))
                {
                    return false;
                }

                continue;
            }

            // Why: any slash-separated alternative that is NOT a bare color letter is an
            // unconditional payment (Phyrexian "{W/P}" pays 2 life, twobrid "{2/W}" pays 2
            // generic), so the whole symbol is always payable regardless of produced colors.
            if (coloredParts.Length != parts.Length)
            {
                continue;
            }

            // Pure color hybrid ("{W/U}"): satisfied when any alternative color is producible.
            if (!coloredParts.Any(part => deckProducedColors.Contains(part[0])))
            {
                return false;
            }
        }

        return true;
    }
}
