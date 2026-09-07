using DeckFlow.Core.Analysis;
using DeckFlow.Core.Manabase;
using DeckFlow.Web.Services.Manabase;

namespace DeckFlow.Web.Services.CutLab;

/// <summary>
/// Assigns each pool card to zero or more of Cut Lab's nine structural role keys using only the
/// existing role and deck-stat classifiers. This taxonomy is wider than <see cref="PlanRole"/>:
/// lands, ramp, and filler draw still matter for slot competition even though
/// <see cref="PlanRoleClassifier"/> deliberately excludes them from plan-presence roles, and Cut
/// Lab intentionally splits interaction more finely than the shared <see cref="PlanRole"/>
/// taxonomy. Multi-role membership is allowed; cutting a card reduces every role count it
/// currently fills.
/// </summary>
public static class CutLabRoleAssigner
{
    private const string LandsRole = "lands";
    private const string RampRole = "ramp";
    private const string DrawRole = "draw";
    private const string InteractionTargetedRole = "interaction-targeted";
    private const string InteractionMassRole = "interaction-mass";
    private const string ProtectionRole = "protection";
    private const string EnginesRole = "engines";
    private const string PayoffsRole = "payoffs";
    private const string WinconsRole = "wincons";
    private const string OtherRole = "other";

    private static readonly string[] RoleKeys =
    [
        LandsRole,
        RampRole,
        DrawRole,
        InteractionTargetedRole,
        InteractionMassRole,
        ProtectionRole,
        EnginesRole,
        PayoffsRole,
        WinconsRole,
    ];

    // Shared primary-type order used both for UI grouping and deterministic service-side ranking.
    internal static readonly string[] TypeGroupOrder =
    [
        "Creature",
        "Planeswalker",
        "Battle",
        "Instant",
        "Sorcery",
        "Artifact",
        "Enchantment",
        "Land",
        "Other",
    ];

    internal static readonly IReadOnlyDictionary<string, string> RoleDisplayLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lands"] = "Lands",
            ["ramp"] = "Ramp",
            ["draw"] = "Card draw",
            ["interaction-targeted"] = "Targeted removal",
            ["interaction-mass"] = "Mass removal",
            ["protection"] = "Protection",
            ["engines"] = "Engines",
            ["payoffs"] = "Payoffs",
            ["wincons"] = "Win conditions",
            ["other"] = "Other",
        };

    /// <summary>Maps the Cut Lab play-experience string to the shared classifier mode.</summary>
    /// <param name="playExperience">User-selected play-experience label.</param>
    /// <returns>The matching mode, or <see cref="ManabaseMode.Casual"/> when unspecified or unknown.</returns>
    public static ManabaseMode ResolveMode(string? playExperience)
    {
        if (string.Equals(playExperience, "cEDH", StringComparison.OrdinalIgnoreCase))
        {
            return ManabaseMode.Cedh;
        }

        if (string.Equals(playExperience, "Focused", StringComparison.OrdinalIgnoreCase))
        {
            return ManabaseMode.Focused;
        }

        return ManabaseMode.Casual;
    }

    internal static string DisplayLabelFor(string roleKey)
        => RoleDisplayLabels.TryGetValue(roleKey, out string? label) ? label : roleKey;

    internal static IReadOnlyList<CutLabLockedOvershootGroupProjection> BuildLockedOvershootGroups(
        IReadOnlyList<CutLabLockedOvershootGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        return groups
            .Select(group => new CutLabLockedOvershootGroupProjection(
                DisplayLabelFor(group.RoleKey),
                group.CardNames))
            .ToArray();
    }

    /// <summary>
    /// Assigns the fixed-order Cut Lab role keys for a card using only existing classifier signals.
    /// </summary>
    /// <param name="fact">Resolved card fact.</param>
    /// <param name="categories">Crowd-sourced category tags for the card.</param>
    /// <param name="isComboPiece">Whether Commander Spellbook lists the card in an included combo.</param>
    /// <param name="mode">Classifier mode derived from play experience.</param>
    /// <returns>The subset of role keys the card fills, in canonical order.</returns>
    public static IReadOnlyList<string> AssignRoles(
        CardFact fact,
        IReadOnlyList<string> categories,
        bool isComboPiece,
        ManabaseMode mode)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(categories);

        string typeLine = fact.TypeLine;
        string oracle = fact.FrontOracleText;
        bool isLand = CutLabLockRules.IsLand(typeLine) || fact.HasLandFace;
        PlanRole roles = PlanRoleClassifier.Classify(fact, categories, isComboPiece, mode, out bool interactionMeritPreGate);

        List<string> assigned = new(RoleKeys.Length);

        if (isLand)
        {
            assigned.Add(LandsRole);
        }

        // Why: DeckStatClassifier.IsRampCard first returns true for every type line containing
        // "Land", so without the gate every land would double-count as ramp and inflate downstream
        // role counts. Lands and ramp stay disjoint by construction.
        if (!isLand && DeckStatClassifier.IsRampCard(typeLine, oracle))
        {
            assigned.Add(RampRole);
        }

        if (DeckStatClassifier.IsDrawCard(oracle))
        {
            assigned.Add(DrawRole);
        }

        // Why: interactionMeritPreGate is a SUPERSET signal -- PlanRoleClassifier sets
        // PlanRole.Interaction from board wipes (GrantsInteraction) AND from the "wipe" category
        // tag (IsInteractionCategory), so routing it to targeted unsubtracted would put every
        // sweeper in the targeted bucket. Mass is computed first and subtracted.
        // Why: targeted/mass are mutually exclusive -- IsTargetedRemovalCard already opens with
        // !IsBoardWipeCard (DeckStatClassifier.cs:185); the !isMass gate makes that structural
        // rather than emergent, and also covers the category-tag path.
        bool isMass = DeckStatClassifier.IsBoardWipeCard(oracle) || HasWipeCategoryTag(categories);
        if (!isMass
            && (DeckStatClassifier.IsTargetedRemovalCard(typeLine, oracle) || interactionMeritPreGate))
        {
            assigned.Add(InteractionTargetedRole);
        }

        if (isMass)
        {
            assigned.Add(InteractionMassRole);
        }

        if (DeckStatClassifier.IsProtectionCard(fact.Name, oracle))
        {
            assigned.Add(ProtectionRole);
        }

        // Why: the shared PlanRoleClassifier keeps Engine on one-shot card advantage for the manabase
        // plan-presence lens (locked 2026-07-09), but Cut Lab's role display wants true repeatable
        // engines. Gate locally on permanent + repeatable draw, mirroring FromHeuristic's Engine rule,
        // so one-shot "draw two" spells tagged "card draw"/"value" no longer flood the engines role.
        if (roles.HasFlag(PlanRole.Engine)
            && !CardTypeLine.IsNonPermanentFront(typeLine)
            && DeckStatClassifier.IsDrawCard(oracle))
        {
            assigned.Add(EnginesRole);
        }

        if (roles.HasFlag(PlanRole.Payoff))
        {
            assigned.Add(PayoffsRole);
        }

        if (DeckStatClassifier.IsClosingPowerCard(typeLine, oracle) || isComboPiece)
        {
            assigned.Add(WinconsRole);
        }

        if (assigned.Count == 0)
        {
            assigned.Add(OtherRole);
        }

        return assigned;
    }

    private static bool HasWipeCategoryTag(IReadOnlyList<string> categories)
    {
        // Why: mirror PlanRoleClassifier.IsInteractionCategory's matching semantics for the
        // "wipe" needle locally so Cut Lab's targeted/mass split covers the category-tag path
        // without changing the shared lens.
        foreach (string category in categories)
        {
            if (category.ToLowerInvariant().Contains("wipe", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed record CutLabLockedOvershootGroupProjection(
    string RoleLabel,
    IReadOnlyList<string> CardNames);
