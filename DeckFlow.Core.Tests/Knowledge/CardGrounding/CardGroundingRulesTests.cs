using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Normalization;

namespace DeckFlow.Core.Tests;

/// <summary>
/// Tests for the pure card-grounding decision rules.
/// </summary>
public sealed class CardGroundingRulesTests
{
    /// <summary>
    /// Verifies Commander legality rejects null, missing, and non-legal statuses.
    /// </summary>
    /// <param name="commanderStatus">Commander legality status to evaluate.</param>
    /// <param name="expected">Expected legality verdict.</param>
    [Theory]
    [InlineData("legal", true)]
    [InlineData("LEGAL", true)]
    [InlineData("banned", false)]
    [InlineData("not_legal", false)]
    [InlineData("restricted", false)]
    public void IsLegalForCommander_ReturnsExpectedVerdictForCommanderStatus(string commanderStatus, bool expected)
    {
        IReadOnlyDictionary<string, string> legalities = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["commander"] = commanderStatus,
        };

        var result = CardGroundingRules.IsLegalForCommander(legalities);

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies Commander legality fails closed for missing commander data.
    /// </summary>
    [Fact]
    public void IsLegalForCommander_ReturnsFalseWhenLegalitiesAreNull()
    {
        var result = CardGroundingRules.IsLegalForCommander(null);

        Assert.False(result);
    }

    /// <summary>
    /// Verifies Commander legality fails closed when the commander key is absent.
    /// </summary>
    [Fact]
    public void IsLegalForCommander_ReturnsFalseWhenCommanderEntryIsMissing()
    {
        IReadOnlyDictionary<string, string> legalities = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["modern"] = "legal",
        };

        var result = CardGroundingRules.IsLegalForCommander(legalities);

        Assert.False(result);
    }

    /// <summary>
    /// Verifies color identity acceptance is a subset check with a colorless exemption.
    /// </summary>
    /// <param name="cardIdentity">Card identity to evaluate.</param>
    /// <param name="expected">Expected subset result.</param>
    [Theory]
    [MemberData(nameof(ColorIdentityCases))]
    public void IsWithinColorIdentity_ReturnsExpectedVerdict(
        IReadOnlyList<string>? cardIdentity,
        bool expected)
    {
        IReadOnlySet<string> commanderIdentity = new HashSet<string>(["G", "U"]);

        var result = CardGroundingRules.IsWithinColorIdentity(cardIdentity, commanderIdentity);

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies basic lands are exempt from singleton duplicate checks.
    /// </summary>
    [Fact]
    public void IsSingletonViolation_ReturnsFalseForBasicLandEvenWhenPresent()
    {
        IReadOnlySet<string> deckCardNames = new HashSet<string>(StringComparer.Ordinal)
        {
            CardNormalizer.Normalize("Forest"),
        };

        var result = CardGroundingRules.IsSingletonViolation("Forest", "Basic Land - Forest", deckCardNames);

        Assert.False(result);
    }

    /// <summary>
    /// Verifies a present non-basic card is treated as a singleton violation.
    /// </summary>
    [Fact]
    public void IsSingletonViolation_ReturnsTrueForPresentNonBasicCard()
    {
        IReadOnlySet<string> deckCardNames = new HashSet<string>(StringComparer.Ordinal)
        {
            CardNormalizer.Normalize("Sol Ring"),
        };

        var result = CardGroundingRules.IsSingletonViolation("Sol Ring", "Artifact", deckCardNames);

        Assert.True(result);
    }

    /// <summary>
    /// Verifies a missing non-basic card is not treated as a singleton violation.
    /// </summary>
    [Fact]
    public void IsSingletonViolation_ReturnsFalseForAbsentNonBasicCard()
    {
        IReadOnlySet<string> deckCardNames = new HashSet<string>(StringComparer.Ordinal)
        {
            CardNormalizer.Normalize("Arcane Signet"),
        };

        var result = CardGroundingRules.IsSingletonViolation("Sol Ring", "Artifact", deckCardNames);

        Assert.False(result);
    }

    /// <summary>
    /// Verifies punctuation normalization catches duplicate names.
    /// </summary>
    [Fact]
    public void IsSingletonViolation_ReturnsTrueForPunctuationNormalizedDuplicate()
    {
        IReadOnlySet<string> deckCardNames = new HashSet<string>(StringComparer.Ordinal)
        {
            CardNormalizer.Normalize("Commander's Sphere"),
        };

        var result = CardGroundingRules.IsSingletonViolation("Commander's Sphere", "Artifact", deckCardNames);

        Assert.True(result);
    }

    /// <summary>
    /// Verifies DFC names compare on their normalized front face.
    /// </summary>
    [Fact]
    public void IsSingletonViolation_ReturnsTrueForDoubleFacedCardFrontFaceMatch()
    {
        IReadOnlySet<string> deckCardNames = new HashSet<string>(StringComparer.Ordinal)
        {
            CardNormalizer.Normalize("Blex, Vexing Pest // Search for Blex"),
        };

        var result = CardGroundingRules.IsSingletonViolation("Blex, Vexing Pest // Search for Blex", "Legendary Creature - Pest", deckCardNames);

        Assert.True(result);
    }

    /// <summary>
    /// Verifies castability requires every colored pip to be present in the deck's produced colors.
    /// </summary>
    /// <param name="manaCost">Mana cost to evaluate.</param>
    /// <param name="producedColors">Produced colors available in the deck.</param>
    /// <param name="expected">Expected castability verdict.</param>
    [Theory]
    [MemberData(nameof(CastableCases))]
    public void IsCastable_ReturnsExpectedVerdict(string? manaCost, IReadOnlySet<char> producedColors, bool expected)
    {
        var result = CardGroundingRules.IsCastable(manaCost, producedColors);

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Supplies color identity scenarios for subset checking.
    /// </summary>
    /// <returns>Color identity test cases.</returns>
    public static IEnumerable<object?[]> ColorIdentityCases()
    {
        yield return [null, true];
        yield return [Array.Empty<string>(), true];
        yield return [new[] { "G" }, true];
        yield return [new[] { "G", "U" }, true];
        yield return [new[] { "W" }, false];
        yield return [new[] { "G", "W" }, false];
    }

    /// <summary>
    /// Supplies castability scenarios for pip-coverage checking.
    /// </summary>
    /// <returns>Castability test cases.</returns>
    public static IEnumerable<object?[]> CastableCases()
    {
        yield return [null, new HashSet<char>(), true];
        yield return [string.Empty, new HashSet<char>(), true];
        yield return ["{3}", new HashSet<char>(), true];
        yield return ["{1}{U}", new HashSet<char>(['U']), true];
        yield return ["{1}{U}", new HashSet<char>(), false];
        yield return ["{W/U}{G}", new HashSet<char>(['W', 'U', 'G']), true];
        yield return ["{W/U}{G}", new HashSet<char>(['U', 'G']), true];
        yield return ["{W/P}", new HashSet<char>(), true];
        yield return ["{2/W}", new HashSet<char>(), true];
        yield return ["{X}{2}{R}", new HashSet<char>(['R']), true];
    }
}
