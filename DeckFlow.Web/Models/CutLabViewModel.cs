using DeckFlow.Core.Analysis;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Research;
using DeckFlow.Web.Models.Api;
using DeckFlow.Web.Models.CutLab;
using DeckFlow.Web.Services.CutLab;
using System.Globalization;
using System.Text.Json.Serialization;

namespace DeckFlow.Web.Models;

/// <summary>View-only card text and printing details keyed by rendered card name.</summary>
public sealed record CutLabCardTextView
{
    /// <summary>Resolved card type line.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeLine { get; init; }

    /// <summary>Resolved card mana cost.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManaCost { get; init; }

    /// <summary>Resolved printing set code.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SetCode { get; init; }

    /// <summary>Resolved printing collector number.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CollectorNumber { get; init; }

    /// <summary>Resolved card oracle text.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OracleText { get; init; }

    /// <summary>Resolved card power.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Power { get; init; }

    /// <summary>Resolved card toughness.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Toughness { get; init; }

    /// <summary>Rounded mana value carried from the current working-pool castability report.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Cmc { get; init; }

    /// <summary>On-curve castability percentage carried from the current working-pool castability report.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CastPercent { get; init; }
}

/// <summary>Per-card combo badge state and disclosure context keyed by card name.</summary>
public sealed record CutLabComboBadgeView
{
    /// <summary>Badge state for the card's combo membership.</summary>
    public ComboBadgeState BadgeState { get; init; }

    /// <summary>Short combo-context string for the disclosure body.</summary>
    public string Context { get; init; } = string.Empty;
}

/// <summary>View model for the Cut Lab page.</summary>
public sealed record CutLabViewModel
{
    // Display order for the By-type groups. Must stay in sync with the priority list in
    // CardTypeLine.PrimaryType (this appends the "Other" fallback bucket).
    /// <summary>Shared primary-type display order used by Cut Lab groupings and advisory ranking.</summary>
    public static readonly string[] TypeGroupOrder = CutLabRoleAssigner.TypeGroupOrder;

    // Display order for the structural role groups. This mirrors CutLabFloorRules.RoleKeys and
    // appends the display-only fallback bucket.
    private static readonly string[] RoleGroupDisplayOrder =
    [
        .. CutLabFloorRules.RoleKeys,
        "other",
    ];

    /// <summary>The active deck tool tab.</summary>
    public DeckPageTab ActiveTab { get; init; }

    /// <summary>The current request values to re-render into the form.</summary>
    public CutLabRequest Request { get; init; } = new();

    /// <summary>User-facing error message for hard failures.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Non-commander pool count returned by the service.</summary>
    public int CardCount { get; init; }

    /// <summary>Mainboard-only quantity loaded from the source deck.</summary>
    public int MainboardCardCount { get; init; }

    /// <summary>Sideboard quantity loaded from the source deck.</summary>
    public int SideboardCardCount { get; init; }

    /// <summary>Considering or maybeboard quantity loaded from the source deck.</summary>
    public int MaybeboardCardCount { get; init; }

    /// <summary>Per-board counts used for the shared pool breakdown display.</summary>
    public BoardCounts BoardCounts { get; init; } = new();

    /// <summary>Compact intake details shown after a pool has been imported.</summary>
    public string IntakeSummaryText { get; init; } = string.Empty;

    /// <summary>Commander banned-card names present in the current pool.</summary>
    public IReadOnlyList<string> BannedCardsPresent { get; init; } = [];

    /// <summary>True when the current pool has no banned cards.</summary>
    public bool IsLegal { get; init; }

    /// <summary>True when the user must choose a commander manually.</summary>
    public bool CommanderSelectionRequired { get; init; }

    /// <summary>Commander-eligible choices to show when manual selection is required.</summary>
    public IReadOnlyList<string> CommanderChoices { get; init; } = [];

    /// <summary>Non-blocking warnings returned by the page service.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>True when a resolved state is available to render.</summary>
    public bool HasResult { get; init; }

    /// <summary>Optional workflow step selected by the server-rendered response.</summary>
    public int? CurrentStepOverride { get; init; }

    /// <summary>Serialized hidden-field working-session JSON.</summary>
    public string CutLabStateJson { get; init; } = string.Empty;

    /// <summary>Resolved pool cards for the current working session.</summary>
    public IReadOnlyList<CutLabPoolCard> Pool { get; init; } = [];

    /// <summary>Resolved lock packages for the current working session.</summary>
    public IReadOnlyList<CutLabPackage> Packages { get; init; } = [];

    /// <summary>Role-group views in the fixed structural analysis order.</summary>
    public IReadOnlyList<CutLabRoleGroupView> RoleGroups { get; init; } = [];

    /// <summary>Type-group views in the fixed Cut Lab primary-type order.</summary>
    public IReadOnlyList<CutLabRoleGroupView> TypeGroups { get; init; } = [];

    /// <summary>Structural findings rendered for the current pool.</summary>
    public IReadOnlyList<CutLabFindingView> Findings { get; init; } = [];

    /// <summary>Structural findings grouped for display in the findings panel.</summary>
    public IReadOnlyList<CutLabFindingGroupView> FindingGroups { get; init; } = [];

    /// <summary>True when combo-backed findings are incomplete because combo lookup was unavailable.</summary>
    public bool ComboDataUnavailable { get; init; }

    /// <summary>True when category-backed findings are incomplete because category lookup was unavailable.</summary>
    public bool CategoryDataUnavailable { get; init; }

    /// <summary>Role floor rows rendered in the fixed Cut Lab order.</summary>
    public IReadOnlyList<CutLabFloorRowView> FloorRows { get; init; } = [];

    /// <summary>The plan-panel checkbox view: generic strategies, commander themes, and their checked state.</summary>
    public CutLabPlanPanelView PlanPanel { get; init; } = new();

    /// <summary>Aggregate floor feasibility advisory, or null when the resolved floor set fits a 100-card deck.</summary>
    public CutLabFloorFeasibilityResult? FloorFeasibility { get; init; }

    /// <summary>True when the role-floors table should show the commander-aware Bracket/Commander columns.</summary>
    public bool CommanderFloorsEnabled { get; init; }

    /// <summary>Editable goal rows rendered in the fixed Cut Lab order.</summary>
    public IReadOnlyList<CutLabGoalRowView> GoalRows { get; init; } = [];

    /// <summary>Sticky round/count bar values for the Cut rounds workspace.</summary>
    public CutLabStickyBarView StickyBar { get; init; } = new();

    /// <summary>Actual lands in the current working-pool simulation, when available.</summary>
    public int? CurrentActualLands { get; init; }

    /// <summary>Target lands in the current working-pool simulation, when available.</summary>
    public double? CurrentTargetLands { get; init; }

    /// <summary>Current one-at-a-time proposal state for the Cut rounds workspace.</summary>
    public CutLabProposalView Proposal { get; init; } = new();

    /// <summary>Accepted cuts rendered in restore-list order.</summary>
    public IReadOnlyList<CutLabCutMadeRowView> CutsMade { get; init; } = [];

    /// <summary>Baseline-versus-current comparison rows.</summary>
    public IReadOnlyList<CutLabCompareRowView> CompareRows { get; init; } = [];

    /// <summary>Server-rendered what-if preview state for the no-JS swap flow.</summary>
    public CutLabWhatifPreviewView Whatif { get; init; } = new();

    /// <summary>Server-rendered export state for the no-JS export flow.</summary>
    public CutLabExportView Export { get; init; } = new();

    /// <summary>Working-list card options eligible to be swapped out.</summary>
    public IReadOnlyList<string> WhatifCardOutOptions { get; init; } = [];

    /// <summary>Cut-pile card options eligible to be swapped in.</summary>
    public IReadOnlyList<string> WhatifCardInOptions { get; init; } = [];

    /// <summary>Total card count of the original imported pool.</summary>
    public int BaselineCount { get; init; }

    /// <summary>Total card count of the current derived working list.</summary>
    public int CurrentCount { get; init; }

    /// <summary>Server-rendered pool summary for the lock-status chip.</summary>
    public string PoolStatusText { get; init; } = string.Empty;

    /// <summary>Adjustment-derived working-list rows eligible for inline quantity tuning.</summary>
    public IReadOnlyList<CutLabTunableRowView> WorkingListRows { get; init; } = [];

    /// <summary>Known basic lands not currently present in the derived working list.</summary>
    public IReadOnlyList<string> AddableBasics { get; init; } = [];

    /// <summary>Per-card display labels for the pool table, keyed by card name.</summary>
    public IReadOnlyDictionary<string, string> RoleListByCardName { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-card raw role-key token strings for the pool table, keyed by card name.</summary>
    public IReadOnlyDictionary<string, string> RoleKeysByCardName { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-card text and printing details for the pool table, keyed by card name.</summary>
    public IReadOnlyDictionary<string, CutLabCardTextView> CardTextByCardName { get; init; } =
        new Dictionary<string, CutLabCardTextView>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-card combo badge state and context keyed by card name.</summary>
    public IReadOnlyDictionary<string, CutLabComboBadgeView> ComboBadgeByCardName { get; init; } =
        new Dictionary<string, CutLabComboBadgeView>(CutLabCardNames.Comparer);

    /// <summary>Finds the lockable pool card identified by a rendered Structural evidence label.</summary>
    public CutLabPoolCard? FindLockableEvidenceCard(string evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        string normalizedEvidence = NormalizeAsciiCase(evidence);

        return Pool
            .Where(card => !card.IsCommander)
            .OrderByDescending(card => card.Name.Length)
            .FirstOrDefault(card =>
            {
                string normalizedCardName = NormalizeAsciiCase(card.Name);
                if (normalizedEvidence.Equals(normalizedCardName, StringComparison.Ordinal))
                {
                    return true;
                }

                string manaValuePrefix = $"{normalizedCardName} · mv ";
                return normalizedEvidence.StartsWith(manaValuePrefix, StringComparison.Ordinal)
                    && IsValidManaValueSuffix(normalizedEvidence.AsSpan(manaValuePrefix.Length));
            });
    }

    private static string NormalizeAsciiCase(string value) =>
        string.Create(value.Length, value, static (characters, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                char character = source[index];
                characters[index] = character is >= 'A' and <= 'Z'
                    ? (char)(character + ('a' - 'A'))
                    : character;
            }
        });

    private static bool IsValidManaValueSuffix(ReadOnlySpan<char> value)
    {
        int index = 0;
        while (index < value.Length && IsAsciiDigit(value[index]))
        {
            index++;
        }

        if (index == 0)
        {
            return false;
        }

        if (index == value.Length)
        {
            return true;
        }

        if (value[index] != '.')
        {
            return false;
        }

        int fractionalStart = ++index;
        while (index < value.Length && IsAsciiDigit(value[index]))
        {
            index++;
        }

        int fractionalDigits = index - fractionalStart;
        return index == value.Length && fractionalDigits is >= 1 and <= 2;
    }

    private static bool IsAsciiDigit(char character) =>
        character is >= '0' and <= '9';

    /// <summary>Builds the page model from the request and service result.</summary>
    /// <param name="request">Current request values.</param>
    /// <param name="result">Processed Cut Lab result.</param>
    /// <param name="whatif">Optional server-rendered what-if preview state.</param>
    /// <param name="export">Optional server-rendered export state.</param>
    /// <param name="currentStepOverride">Optional current step override.</param>
    public static CutLabViewModel From(
        CutLabRequest request,
        CutLabProcessResult result,
        CutLabWhatifPreviewView? whatif = null,
        CutLabExportView? export = null,
        int? currentStepOverride = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        IReadOnlyList<CutLabPoolCard> pool = result.State?.Pool ?? [];
        IReadOnlyList<CutLabQuantityAdjustment> adjustments = result.State?.QuantityAdjustments ?? [];
        IReadOnlyList<CutLabPoolCard> derivedWorkingList = CutLabWorkingList.Derive(pool, result.State?.Decisions ?? [], adjustments);
        IReadOnlyList<CutLabTunableRowView> workingListRows = BuildWorkingListRows(derivedWorkingList, pool, result.RoleAssignmentsByCardName);
        IReadOnlyList<string> addableBasics = BuildAddableBasics(workingListRows);
        IReadOnlyList<CutLabRoleGroupView> roleGroups = BuildRoleGroups(pool, result.RoleAssignmentsByCardName);
        IReadOnlyList<CutLabRoleGroupView> typeGroups = BuildTypeGroups(pool);
        IReadOnlyList<CutLabFindingView> findings = CutLabFindingPresenter.BuildFindings(result.Findings.Findings);
        IReadOnlyList<CutLabFindingGroupView> findingGroups = CutLabFindingPresenter.BuildFindingGroups(findings);
        Dictionary<string, int> countsByRole = CountRoles(derivedWorkingList, result.RoleAssignmentsByCardName);
        IReadOnlyList<CutLabFloorRowView> floorRows = BuildFloorRows(result.ResolvedFloors, countsByRole, request.PlayExperience);
        CutLabFloorFeasibilityResult? floorFeasibility = CutLabFloorFeasibility.Evaluate(result.ResolvedFloors);
        CutLabPlanPanelView planPanel = BuildPlanPanel(result.State?.Intent.PlanProfile, result.AvailableCommanderThemes, result.CommanderThemesUnavailable);
        IReadOnlyDictionary<string, string> roleListByCardName = BuildRoleListByCardName(pool, result.RoleAssignmentsByCardName);
        IReadOnlyDictionary<string, string> roleKeysByCardName = BuildRoleKeysByCardName(pool, result.RoleAssignmentsByCardName);
        IReadOnlyDictionary<string, CutLabCardTextView> cardTextByCardName = result.CardTextByCardName;
        IReadOnlyDictionary<string, CutLabComboBadgeView> comboBadgeByCardName = result.ComboBadgeByCardName;
        IReadOnlyDictionary<CutLabFindingKind, string> findingHeadingsByKind = BuildFindingHeadingsByKind(result.Findings.Findings);
        IReadOnlyList<CutLabCutMadeRowView> cutsMade = BuildCutsMade(result.State?.Decisions);
        int baselineCount = pool.Sum(card => card.Quantity);
        int currentCount = derivedWorkingList.Sum(card => card.Quantity);
        int lockedCount = pool.Where(card => card.IsLocked).Sum(card => card.Quantity);
        IReadOnlyList<string> whatifCardOutOptions = BuildWhatifCardOutOptions(derivedWorkingList);
        IReadOnlyList<string> whatifCardInOptions = BuildWhatifCardInOptions(pool, result.State?.Decisions);
        IReadOnlyList<CutLabGoalRowView> goalRows = BuildGoalRows(
            result.State?.Goals ?? new(),
            result.CurrentSnapshot,
            result.State?.BaselineSnapshot,
            request.PlayExperience);
        CutLabStickyBarView stickyBar = BuildStickyBar(result.RoundPlan, result.State?.Decisions, lockedCount, currentCount);
        CutLabProposalView proposal = BuildProposal(
            result.RoundPlan,
            result.InitialProposalDeltas,
            result.State,
            result.ResolvedFloors,
            result.RoleAssignmentsByCardName,
            countsByRole,
            findingHeadingsByKind);
        IReadOnlyList<CutLabCompareRowView> compareRows = BuildCompareRows(result.State?.BaselineSnapshot, result.CurrentSnapshot);

        return new CutLabViewModel
        {
            ActiveTab = DeckPageTab.CutLab,
            Request = request,
            ErrorMessage = result.ErrorMessage,
            CardCount = result.CardCount,
            MainboardCardCount = result.MainboardCardCount,
            SideboardCardCount = result.SideboardCardCount,
            MaybeboardCardCount = result.MaybeboardCardCount,
            BoardCounts = result.BoardCounts,
            IntakeSummaryText = BuildIntakeSummary(request, result.State, result.BoardCounts, baselineCount),
            BannedCardsPresent = result.BannedCardsPresent,
            IsLegal = result.IsLegal,
            CommanderSelectionRequired = result.CommanderSelectionRequired,
            CommanderChoices = result.CommanderChoices,
            Warnings = result.Warnings,
            HasResult = result.HasResult,
            CurrentStepOverride = currentStepOverride,
            CutLabStateJson = result.SerializedStateJson ?? request.CutLabStateJson,
            Pool = pool,
            Packages = result.State?.Packages ?? [],
            RoleGroups = roleGroups,
            TypeGroups = typeGroups,
            Findings = findings,
            FindingGroups = findingGroups,
            ComboDataUnavailable = result.HasResult && !result.Findings.ComboDataAvailable,
            CategoryDataUnavailable = result.HasResult && !result.Findings.CategoryDataAvailable,
            FloorRows = floorRows,
            FloorFeasibility = floorFeasibility,
            PlanPanel = planPanel,
            CommanderFloorsEnabled = result.CommanderFloorsEnabled,
            GoalRows = goalRows,
            StickyBar = stickyBar,
            CurrentActualLands = result.CurrentActualLands,
            CurrentTargetLands = result.CurrentTargetLands,
            Proposal = proposal,
            CutsMade = cutsMade,
            CompareRows = compareRows,
            Whatif = whatif ?? new(),
            Export = export ?? new(),
            WhatifCardOutOptions = whatifCardOutOptions,
            WhatifCardInOptions = whatifCardInOptions,
            BaselineCount = baselineCount,
            CurrentCount = currentCount,
            PoolStatusText = $"{baselineCount} cards in pool · {lockedCount} locked",
            WorkingListRows = workingListRows,
            AddableBasics = addableBasics,
            RoleListByCardName = roleListByCardName,
            RoleKeysByCardName = roleKeysByCardName,
            CardTextByCardName = cardTextByCardName,
            ComboBadgeByCardName = comboBadgeByCardName,
        };
    }

    internal static string FormatCutsMadeCount(int count)
        => $"{count} {ManabaseWording.Pluralize("card", count)}";

    internal static string FormatCutsAcceptedSoFar(int count)
        => $"{count} {ManabaseWording.Pluralize("cut", count)} so far";

    internal static string BuildFloorFeasibilityMessage(CutLabFloorFeasibilityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        string demandSentence = string.Format(
            CultureInfo.InvariantCulture,
            "These floors need at least {0} nonland slots, but only {1} remain after {2} lands and the commander.",
            result.RequiredNonlandSlots,
            result.AvailableNonlandSlots,
            result.LandsFloor);

        if (result.RelaxCandidates.Count == 0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} This is a conservative estimate — roles overlap, every engine is also a draw spell and win conditions usually double as another role, so the real requirement is at least this large and may be larger.",
                demandSentence);
        }

        string actionSentence = string.Format(
            CultureInfo.InvariantCulture,
            "Relax {0} first.",
            string.Join(
                ", ",
                result.RelaxCandidates.Select(candidate =>
                {
                    string label = DisplayLabelFor(candidate.RoleKey);
                    return candidate.CommanderRaise is int raise
                        ? string.Format(CultureInfo.InvariantCulture, "{0} (raised by {1})", label, raise)
                        : label;
                })));

        string honestySentence = "This is a conservative estimate — roles overlap, every engine is also a draw spell and win conditions usually double as another role, so the real requirement is at least this large and may be larger.";

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2}",
            demandSentence,
            actionSentence,
            honestySentence);
    }

    private static IReadOnlyList<CutLabRoleGroupView> BuildRoleGroups(
        IReadOnlyList<CutLabPoolCard> pool,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleAssignmentsByCardName)
    {
        return RoleGroupDisplayOrder
            .Select(roleKey =>
            {
                IReadOnlyList<CutLabRoleMemberView> members = pool
                    .Where(card => roleAssignmentsByCardName.TryGetValue(card.Name, out IReadOnlyList<string>? roles)
                        && roles.Contains(roleKey, StringComparer.Ordinal))
                    .Select(card => new CutLabRoleMemberView
                    {
                        Name = card.Name,
                        IsLocked = card.IsLocked,
                        IsCommander = card.IsCommander,
                    })
                    .ToArray();

                return new CutLabRoleGroupView
                {
                    RoleKey = roleKey,
                    DisplayLabel = DisplayLabelFor(roleKey),
                    IsLockable = !string.Equals(roleKey, "other", StringComparison.Ordinal),
                    Members = members,
                    LockedCount = pool
                        .Where(card => card.IsLocked
                            && roleAssignmentsByCardName.TryGetValue(card.Name, out IReadOnlyList<string>? roles)
                            && roles.Contains(roleKey, StringComparer.Ordinal))
                        .Sum(card => card.Quantity),
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<CutLabRoleGroupView> BuildTypeGroups(IReadOnlyList<CutLabPoolCard> pool)
    {
        // Bucket each card once by primary type, then order the present buckets by TypeGroupOrder.
        // Single-pass so PrimaryType runs once per card rather than once per (type x card).
        Dictionary<string, List<CutLabPoolCard>> byPrimaryType = new(StringComparer.Ordinal);
        foreach (CutLabPoolCard card in pool)
        {
            string primaryType = CardTypeLine.PrimaryType(card.TypeLine);
            if (!byPrimaryType.TryGetValue(primaryType, out List<CutLabPoolCard>? bucket))
            {
                bucket = [];
                byPrimaryType[primaryType] = bucket;
            }

            bucket.Add(card);
        }

        return TypeGroupOrder
            .Where(byPrimaryType.ContainsKey)
            .Select(typeLabel =>
            {
                List<CutLabPoolCard> cards = byPrimaryType[typeLabel];
                return new CutLabRoleGroupView
                {
                    RoleKey = typeLabel,
                    DisplayLabel = typeLabel,
                    Members = cards
                        .Select(card => new CutLabRoleMemberView
                        {
                            Name = card.Name,
                            IsLocked = card.IsLocked,
                            IsCommander = card.IsCommander,
                        })
                        .ToArray(),
                    CardCount = cards.Sum(card => card.Quantity),
                    LockedCount = cards.Where(card => card.IsLocked).Sum(card => card.Quantity),
                };
            })
            .ToArray();
    }

    internal static IReadOnlyList<CutLabFloorRowView> BuildFloorRows(
        IReadOnlyList<CutLabResolvedFloor> resolvedFloors,
        IReadOnlyDictionary<string, int> countsByRole,
        string playExperience)
    {
        IReadOnlyList<CutLabResolvedFloorDto> floorDtos = CutLabResolvedFloorDto.Create(resolvedFloors, countsByRole, playExperience);

        return resolvedFloors
            .Zip(floorDtos, (floor, floorDto) =>
            {
                bool supportsCommanderFloor = RoleFloorBaseline.AdoptedRoleKeys.Contains(floor.Role, StringComparer.OrdinalIgnoreCase);
                return new CutLabFloorRowView
                {
                    RoleKey = floorDto.RoleKey,
                    DisplayLabel = DisplayLabelFor(floor.Role),
                    InPoolCount = floorDto.InPoolCount,
                    BracketValue = floorDto.BracketValue,
                    CommanderValue = floor.CommanderValue,
                    SupportsCommanderFloor = supportsCommanderFloor,
                    CommanderDisplay = floorDto.CommanderDisplay,
                    Floor = floorDto.Floor,
                    DefaultValue = floorDto.DefaultValue,
                    PlanDelta = floorDto.PlanDelta,
                    IsUserSet = floorDto.IsUserSet,
                    AtFloor = floorDto.InPoolCount <= floorDto.Floor + 1,
                    SourceLabel = floorDto.SourceLabel,
                    SourceDetail = floorDto.SourceDetail,
                };
            })
            .ToArray();
    }

    internal static CutLabPlanPanelView BuildPlanPanel(
        CutLabPlanProfile? planProfile,
        IReadOnlyList<CutLabCommanderTheme> availableThemes,
        bool commanderThemesUnavailable)
    {
        HashSet<string> checkedStrategySlugs = new(
            planProfile?.GenericStrategies ?? [],
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> checkedThemeSlugs = new(
            (planProfile?.CommanderThemes ?? []).Select(theme => theme.Slug),
            StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<CutLabPlanStrategyRowView> strategyRows = DeckPlanStrategyCatalog.Entries
            .Select(entry => new CutLabPlanStrategyRowView
            {
                Slug = entry.Slug,
                DisplayName = entry.DisplayName,
                Definition = entry.Definition,
                Consequence = entry.Consequence,
                IsChecked = checkedStrategySlugs.Contains(entry.Slug),
            })
            .ToArray();

        int totalThemeDeckCount = availableThemes.Sum(theme => theme.DeckCount);
        IReadOnlyList<CutLabPlanThemeRowView> themeRows = availableThemes
            .Select(theme => new CutLabPlanThemeRowView
            {
                Slug = theme.Slug,
                DisplayName = theme.DisplayName,
                DeckCount = theme.DeckCount,
                SharePercent = totalThemeDeckCount > 0 ? (double)theme.DeckCount / totalThemeDeckCount * 100 : 0,
                IsChecked = checkedThemeSlugs.Contains(theme.Slug),
            })
            .ToArray();

        bool zeroSelectionNotice = strategyRows.All(row => !row.IsChecked) && themeRows.All(row => !row.IsChecked);

        return new CutLabPlanPanelView
        {
            StrategyRows = strategyRows,
            ThemeRows = themeRows,
            CommanderThemesUnavailable = commanderThemesUnavailable,
            ZeroSelectionNotice = zeroSelectionNotice,
        };
    }

    private static IReadOnlyList<CutLabGoalRowView> BuildGoalRows(
        CutLabGoalSettings goals,
        CutLabMetricSnapshot? currentSnapshot,
        CutLabMetricSnapshot? baselineSnapshot,
        string playExperience)
    {
        ArgumentNullException.ThrowIfNull(goals);

        bool representativeLineIsUncapped = CutLabRoleAssigner.ResolveMode(playExperience) != ManabaseMode.Cedh;
        IReadOnlyDictionary<CutLabMetricKind, CutLabMetricValue> currentByKind = currentSnapshot?.Metrics
            .ToDictionary(metric => metric.Kind) ?? new Dictionary<CutLabMetricKind, CutLabMetricValue>();
        IReadOnlyDictionary<CutLabMetricKind, CutLabMetricValue> baselineByKind = baselineSnapshot?.Metrics
            .ToDictionary(metric => metric.Kind) ?? new Dictionary<CutLabMetricKind, CutLabMetricValue>();

        return
        [
            BuildGoalRow(
                CutLabMetricKind.CommanderByTurn,
                "commander",
                "GoalCommanderByTurn",
                goals.CommanderByTurn,
                currentByKind,
                baselineByKind,
                isUncappedInCasual: false),
            BuildGoalRow(
                CutLabMetricKind.EngineByTurn,
                "engine",
                "GoalEngineByTurn",
                goals.EngineByTurn,
                currentByKind,
                baselineByKind,
                isUncappedInCasual: false),
            BuildGoalRow(
                CutLabMetricKind.RepresentativeLineByTurn,
                "representative-line",
                "GoalPlanByTurn",
                goals.RepresentativeLineByTurn,
                currentByKind,
                baselineByKind,
                representativeLineIsUncapped),
        ];
    }

    private static IReadOnlyDictionary<string, string> BuildRoleListByCardName(
        IReadOnlyList<CutLabPoolCard> pool,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleAssignmentsByCardName)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (CutLabPoolCard card in pool)
        {
            result[card.Name] = RoleLabelFor(card.Name, roleAssignmentsByCardName);
        }

        return result;
    }

    /// <summary>Formats a card's assigned roles into the shared " · "-joined display label.</summary>
    private static string RoleLabelFor(
        string cardName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleAssignmentsByCardName)
        => roleAssignmentsByCardName.TryGetValue(cardName, out IReadOnlyList<string>? roles)
            ? string.Join(" · ", roles.Select(DisplayLabelFor))
            : string.Empty;

    private static IReadOnlyDictionary<string, string> BuildRoleKeysByCardName(
        IReadOnlyList<CutLabPoolCard> pool,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleAssignmentsByCardName)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (CutLabPoolCard card in pool)
        {
            result[card.Name] = roleAssignmentsByCardName.TryGetValue(card.Name, out IReadOnlyList<string>? roles)
                ? string.Join(" ", roles)
                : string.Empty;
        }

        return result;
    }

    private static IReadOnlyDictionary<CutLabFindingKind, string> BuildFindingHeadingsByKind(IReadOnlyList<CutLabFinding> findings)
    {
        Dictionary<CutLabFindingKind, string> result = [];
        foreach (CutLabFinding finding in findings)
        {
            if (!result.ContainsKey(finding.Kind))
            {
                result[finding.Kind] = finding.Heading;
            }
        }

        return result;
    }

    private static CutLabStickyBarView BuildStickyBar(
        CutLabRoundPlan? roundPlan,
        IReadOnlyList<CutLabDecision>? decisions,
        int lockedCount,
        int currentCount)
    {
        CutLabRoundQueueItem? nextProposal = roundPlan?.NextProposal;
        return new CutLabStickyBarView
        {
            HasStickyBar = nextProposal is not null,
            LockedCount = lockedCount,
            CurrentCount = currentCount,
            RoundLabel = nextProposal?.RoundLabel ?? string.Empty,
            CardsRemainingToCut = roundPlan?.CardsRemainingToTarget ?? 0,
            CutsAcceptedCount = decisions?.Count(decision => decision.Kind == CutLabDecisionKind.Accepted) ?? 0,
        };
    }

    private static string BuildIntakeSummary(
        CutLabRequest request,
        CutLabState? state,
        BoardCounts boardCounts,
        int baselineCount)
    {
        string commander = !string.IsNullOrWhiteSpace(request.SelectedCommander)
            ? request.SelectedCommander
            : state?.Commander ?? string.Empty;
        string bracketLabel = request.Bracket switch
        {
            1 => "B1 Exhibition",
            2 => "B2 Core",
            3 => "B3 Upgraded",
            4 => "B4 Optimized",
            5 => "B5 cEDH",
            _ => "Bracket not set",
        };

        return $"{commander} · {baselineCount} cards · {bracketLabel} · {request.PlayExperience} · {boardCounts.ToBreakdown()}";
    }

    private static CutLabProposalView BuildProposal(
        CutLabRoundPlan? roundPlan,
        CutLabProposalDeltas? proposalDeltas,
        CutLabState? state,
        IReadOnlyList<CutLabResolvedFloor> resolvedFloors,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleAssignmentsByCardName,
        IReadOnlyDictionary<string, int> countsByRole,
        IReadOnlyDictionary<CutLabFindingKind, string> findingHeadingsByKind)
    {
        CutLabRoundQueueItem? nextProposal = roundPlan?.NextProposal;
        if (nextProposal is null)
        {
            bool isAtTarget = (roundPlan?.CardsRemainingToTarget ?? 0) == 0;
            return new CutLabProposalView
            {
                HasProposal = false,
                IsTerminal = true,
                IsAtTarget = isAtTarget,
                IsNothingToCut = !isAtTarget,
                LockedOvershootAdvisory = BuildLockedOvershootAdvisory(roundPlan?.LockedOvershootAdvisory),
            };
        }

        IReadOnlyList<string> findingChips = nextProposal.DiscriminatingFindingKinds
            .Where(findingHeadingsByKind.ContainsKey)
            .Select(kind => findingHeadingsByKind[kind])
            .ToArray();
        IReadOnlyList<string> floorWarnings = BuildFloorWarnings(nextProposal.CardName, state, resolvedFloors, roleAssignmentsByCardName, countsByRole);
        string findingSummary = nextProposal.FindingCount > 0
            ? $"Flagged by {nextProposal.FindingCount} findings:"
            : "No structural finding flags this card — it's a preference call.";
        if (proposalDeltas is null)
        {
            return new CutLabProposalView
            {
                HasProposal = true,
                CardName = nextProposal.CardName,
                RoundKey = nextProposal.RoundKey,
                RoundLabel = nextProposal.RoundLabel,
                RoundBannerBody = CutLabCutRoundEngine.RoundBannerBodyFor(nextProposal.RoundKey),
                FindingCount = nextProposal.FindingCount,
                FindingSummary = findingSummary,
                FindingChips = findingChips,
                DeltaUnavailableMessage = CutLabMessages.NoChangeMessage,
                GlanceLine = ComposeProposalGlance(null, CutLabMessages.NoChangeMessage),
                FloorWarnings = floorWarnings,
            };
        }

        IReadOnlyList<CutLabDeltaLineView> fullDeltaLines = BuildDeltaLines(nextProposal.CardName, proposalDeltas.Deltas);
        IReadOnlyList<CutLabDeltaLineView> changedDeltaLines = fullDeltaLines
            .Where(line => line.IsMeaningful)
            .ToArray();

        return new CutLabProposalView
        {
            HasProposal = true,
            CardName = nextProposal.CardName,
            RoundKey = nextProposal.RoundKey,
            RoundLabel = nextProposal.RoundLabel,
            RoundBannerBody = CutLabCutRoundEngine.RoundBannerBodyFor(nextProposal.RoundKey),
            FindingCount = nextProposal.FindingCount,
            FindingSummary = findingSummary,
            FindingChips = findingChips,
            ChangedDeltaLines = changedDeltaLines,
            FullDeltaLines = fullDeltaLines,
            ChangedFamilyCount = proposalDeltas.ChangedFamilyCount,
            GlanceLine = ComposeProposalGlance(proposalDeltas, CutLabMessages.NoChangeMessage),
            FloorWarnings = floorWarnings,
        };
    }

    /// <summary>Builds the compact, server-authored proposal summary used by the pinned decide header.</summary>
    public static string ComposeProposalGlance(CutLabProposalDeltas? proposalDeltas, string deltaUnavailableMessage)
    {
        if (proposalDeltas is null)
        {
            return deltaUnavailableMessage;
        }

        IReadOnlyList<CutLabMetricDelta> largestDeltas = proposalDeltas.Deltas
            .Where(delta => delta.IsMeaningful)
            .OrderByDescending(delta => Math.Abs(delta.Delta))
            .Take(2)
            .ToArray();
        if (largestDeltas.Count == 0)
        {
            return $"{proposalDeltas.ChangedFamilyCount} of 7 deck numbers move";
        }

        return $"{proposalDeltas.ChangedFamilyCount} of 7 deck numbers move · {string.Join(" · ", largestDeltas.Select(FormatGlanceDelta))}";
    }

    private static string FormatGlanceDelta(CutLabMetricDelta delta)
    {
        string sign = delta.Delta < 0 ? "−" : "+";
        string suffix = delta.Unit == CutLabMetricUnit.Percent ? "pt" : string.Empty;
        return $"{delta.Label} {sign}{Math.Abs(delta.Delta):0.##}{suffix}";
    }

    private static IReadOnlyList<CutLabDeltaLineView> BuildDeltaLines(
        string cardName,
        IReadOnlyList<CutLabMetricDelta> deltas)
    {
        return deltas
            .Select(delta =>
            {
                return new CutLabDeltaLineView
                {
                    MetricLabel = delta.Label,
                    Direction = delta.Direction,
                    FormattedValueToken = FormatDeltaToken(delta.Delta, delta.Unit),
                    IsMeaningful = delta.IsMeaningful,
                    Sentence = delta.IsMeaningful
                        ? $"cutting {cardName} {DirectionVerbFor(delta.Direction)} {delta.Label.ToLowerInvariant()} by {FormatDeltaToken(delta.Delta, delta.Unit)}."
                        : $"{delta.Label}: no meaningful change",
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<string> BuildFloorWarnings(
        string cardName,
        CutLabState? state,
        IReadOnlyList<CutLabResolvedFloor> resolvedFloors,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleAssignmentsByCardName,
        IReadOnlyDictionary<string, int> countsByRole)
    {
        if (state is null)
        {
            return [];
        }

        Dictionary<string, int> floorByRole = resolvedFloors.ToDictionary(
            floor => floor.Role,
            floor => floor.Floor,
            StringComparer.OrdinalIgnoreCase);
        CutLabPoolCard? card = state.Pool.FirstOrDefault(poolCard => string.Equals(poolCard.Name, cardName, StringComparison.OrdinalIgnoreCase));
        if (card is null || !roleAssignmentsByCardName.TryGetValue(card.Name, out IReadOnlyList<string>? roles))
        {
            return [];
        }

        return CutLabFloorRules.Evaluate(countsByRole, floorByRole, roles, cardName, card.Quantity)
            .Select(warning => warning.Message)
            .ToArray();
    }

    private static IReadOnlyList<CutLabCutMadeRowView> BuildCutsMade(IReadOnlyList<CutLabDecision>? decisions)
    {
        if (decisions is null)
        {
            return [];
        }

        return decisions
            .Where(decision => decision.Kind == CutLabDecisionKind.Accepted)
            .OrderByDescending(decision => decision.Ordinal)
            .Select(decision => new CutLabCutMadeRowView
            {
                CardName = decision.CardName,
                RoundKey = decision.Round,
                RoundLabel = CutLabCutRoundEngine.LabelFor(decision.Round),
            })
            .ToArray();
    }

    private static CutLabLockedOvershootAdvisoryView? BuildLockedOvershootAdvisory(CutLabLockedOvershootAdvisory? advisory)
    {
        if (advisory is null)
        {
            return null;
        }

        IReadOnlyList<CutLabLockedOvershootGroupProjection> groups = CutLabRoleAssigner.BuildLockedOvershootGroups(advisory.Groups);

        return new CutLabLockedOvershootAdvisoryView
        {
            CardsOverTarget = advisory.CardsOverTarget,
            HiddenCount = advisory.HiddenCount,
            Groups = groups
                .Select(group => new CutLabLockedOvershootGroupView
                {
                    RoleLabel = group.RoleLabel,
                    CardNames = group.CardNames,
                })
                .ToArray(),
        };
    }

    private static IReadOnlyList<CutLabCompareRowView> BuildCompareRows(
        CutLabMetricSnapshot? baselineSnapshot,
        CutLabMetricSnapshot? currentSnapshot)
    {
        if (baselineSnapshot is null || currentSnapshot is null)
        {
            return [];
        }

        IReadOnlyDictionary<CutLabMetricKind, CutLabMetricValue> currentByKind = currentSnapshot.Metrics
            .ToDictionary(metric => metric.Kind);
        return baselineSnapshot.Metrics
            .Where(metric => currentByKind.ContainsKey(metric.Kind))
            .Select(metric =>
            {
                CutLabMetricValue current = currentByKind[metric.Kind];
                CutLabMetricDelta? delta = CutLabMetricDelta.Between(metric, current);
                return new CutLabCompareRowView
                {
                    MetricLabel = metric.Label,
                    BaselineValue = FormatMetricValue(metric.Value, metric.Unit),
                    CurrentValue = FormatMetricValue(current.Value, current.Unit),
                    DeltaValueToken = delta is null ? string.Empty : FormatDeltaToken(delta.Delta, delta.Unit),
                    Direction = delta?.Direction ?? CutLabMetricDirection.None,
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<CutLabTunableRowView> BuildWorkingListRows(
        IReadOnlyList<CutLabPoolCard> derivedWorkingList,
        IReadOnlyList<CutLabPoolCard> pool,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleAssignmentsByCardName)
    {
        HashSet<string> originalPoolNames = pool
            .Select(card => CutLabCardNames.Normalize(card.Name))
            .ToHashSet(CutLabCardNames.Comparer);

        return derivedWorkingList
            .Select(card =>
            {
                bool isLegalMultiple = CutLabLegality.IsLegalMultiple(card.Name);
                bool isAddedBasic = CutLabBasicLands.Contains(card.Name)
                    && !originalPoolNames.Contains(CutLabCardNames.Normalize(card.Name));
                string roleLabel = RoleLabelFor(card.Name, roleAssignmentsByCardName);

                return new CutLabTunableRowView
                {
                    Name = card.Name,
                    RoleLabel = roleLabel,
                    CurrentQuantity = card.Quantity,
                    IsLegalMultiple = isLegalMultiple,
                    LegalMax = CutLabLegality.LegalMax(card.Name),
                    IsLocked = card.IsLocked,
                    IsAddedBasic = isAddedBasic,
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<string> BuildAddableBasics(IReadOnlyList<CutLabTunableRowView> workingListRows)
    {
        HashSet<string> presentBasicNames = workingListRows
            .Select(row => CutLabCardNames.Normalize(row.Name))
            .ToHashSet(CutLabCardNames.Comparer);

        return CutLabBasicLands.Names
            .Where(name => !presentBasicNames.Contains(CutLabCardNames.Normalize(name)))
            .ToArray();
    }

    internal static IReadOnlyList<CutLabCompareRowView> BuildCompareRows(IReadOnlyList<CutLabMetricDelta> deltas)
    {
        ArgumentNullException.ThrowIfNull(deltas);

        return deltas
            .Select(delta => new CutLabCompareRowView
            {
                MetricLabel = delta.Label,
                BaselineValue = FormatMetricValue(delta.Before, delta.Unit),
                CurrentValue = FormatMetricValue(delta.After, delta.Unit),
                DeltaValueToken = FormatDeltaToken(delta.Delta, delta.Unit),
                Direction = delta.Direction,
            })
            .ToArray();
    }

    private static IReadOnlyList<string> BuildWhatifCardOutOptions(
        IReadOnlyList<CutLabPoolCard> derivedWorkingList)
    {
        return derivedWorkingList
            .Where(card => !card.IsLocked && !card.IsCommander)
            .Select(card => card.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildWhatifCardInOptions(
        IReadOnlyList<CutLabPoolCard> pool,
        IReadOnlyList<CutLabDecision>? decisions)
    {
        if (pool.Count == 0)
        {
            return [];
        }

        IReadOnlySet<string> accepted = CutLabWorkingList.AcceptedCardNames(decisions ?? []);
        return pool
            .Where(card => accepted.Contains(card.Name))
            .Select(card => card.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CutLabGoalRowView BuildGoalRow(
        CutLabMetricKind kind,
        string goalKey,
        string fieldName,
        int turnValue,
        IReadOnlyDictionary<CutLabMetricKind, CutLabMetricValue> currentByKind,
        IReadOnlyDictionary<CutLabMetricKind, CutLabMetricValue> baselineByKind,
        bool isUncappedInCasual)
    {
        currentByKind.TryGetValue(kind, out CutLabMetricValue? currentMetric);
        baselineByKind.TryGetValue(kind, out CutLabMetricValue? baselineMetric);
        CutLabMetricDelta? delta = currentMetric is not null && baselineMetric is not null
            ? CutLabMetricDelta.Between(baselineMetric, currentMetric)
            : null;

        return new CutLabGoalRowView
        {
            Kind = kind,
            GoalKey = goalKey,
            FieldName = fieldName,
            Label = BuildGoalLabel(kind, turnValue),
            TurnValue = turnValue,
            CurrentProbability = currentMetric is null ? string.Empty : FormatMetricValue(currentMetric.Value, currentMetric.Unit),
            BaselineProbability = baselineMetric is null ? string.Empty : FormatMetricValue(baselineMetric.Value, baselineMetric.Unit),
            Direction = delta?.Direction ?? CutLabMetricDirection.None,
            DeltaValueToken = delta is null ? string.Empty : FormatDeltaToken(delta.Delta, delta.Unit),
            IsUncappedInCasual = isUncappedInCasual,
        };
    }

    private static Dictionary<string, int> CountRoles(
        IReadOnlyList<CutLabPoolCard> pool,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleAssignmentsByCardName)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (string roleKey in CutLabFloorRules.RoleKeys)
        {
            counts[roleKey] = 0;
        }

        foreach (CutLabPoolCard card in pool)
        {
            if (!roleAssignmentsByCardName.TryGetValue(card.Name, out IReadOnlyList<string>? roles))
            {
                continue;
            }

            foreach (string role in roles)
            {
                if (counts.ContainsKey(role))
                {
                    counts[role] += card.Quantity;
                }
            }
        }

        return counts;
    }

    private static string DisplayLabelFor(string roleKey)
        => CutLabRoleAssigner.DisplayLabelFor(roleKey);

    private static string BuildGoalLabel(CutLabMetricKind kind, int turnValue)
        => kind switch
        {
            CutLabMetricKind.CommanderByTurn => $"Commander by turn {turnValue}",
            CutLabMetricKind.EngineByTurn => $"Engine by turn {turnValue}",
            CutLabMetricKind.RepresentativeLineByTurn => $"Representative line by turn {turnValue}",
            _ => kind.ToString(),
        };

    private static string FormatMetricValue(double value, CutLabMetricUnit unit)
        => unit == CutLabMetricUnit.Cards
            ? FormatCardValue(value)
            : $"{value:0.0}%";

    private static string FormatDeltaToken(double delta, CutLabMetricUnit unit)
    {
        double magnitude = Math.Abs(delta);

        return unit == CutLabMetricUnit.Cards
            ? FormatCardValue(magnitude)
            : $"{magnitude:0.0}%";
    }

    private static string FormatCardValue(double value)
    {
        double rounded = Math.Round(value, 0, MidpointRounding.AwayFromZero);
        string count = $"{rounded:0}";
        return rounded == 1d ? $"{count} card" : $"{count} cards";
    }

    private static string DirectionVerbFor(CutLabMetricDirection direction)
        => direction == CutLabMetricDirection.Down ? "lowers" : "raises";

}

/// <summary>View-ready slot-competition group for one fixed Cut Lab role.</summary>
public sealed record CutLabRoleGroupView
{
    /// <summary>Stable role key for the group.</summary>
    public string RoleKey { get; init; } = string.Empty;

    /// <summary>User-facing label for the role group.</summary>
    public string DisplayLabel { get; init; } = string.Empty;

    /// <summary>Pool members that currently belong to the role group.</summary>
    public IReadOnlyList<CutLabRoleMemberView> Members { get; init; } = [];

    /// <summary>Number of locked cards inside the role group.</summary>
    public int LockedCount { get; init; }

    /// <summary>Total card quantity in the group (sums multiple copies). Computed once at build time.</summary>
    public int CardCount { get; init; }

    /// <summary>True when the role group supports bulk lock/unlock actions.</summary>
    public bool IsLockable { get; init; } = true;

    /// <summary>True when every non-commander member in the role group is locked.</summary>
    public bool AllLockableMembersLocked => Members.Count > 0 && Members.Where(member => !member.IsCommander).All(member => member.IsLocked);
}

/// <summary>View-ready role-group member entry for a single pool card.</summary>
public sealed record CutLabRoleMemberView
{
    /// <summary>Display card name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>True when the card is currently locked in the working session.</summary>
    public bool IsLocked { get; init; }

    /// <summary>True when the card is the resolved commander.</summary>
    public bool IsCommander { get; init; }
}

/// <summary>View-ready structural finding with preformatted evidence text.</summary>
public sealed record CutLabFindingView
{
    /// <summary>Underlying finding kind used for display grouping.</summary>
    public CutLabFindingKind Kind { get; init; }

    /// <summary>UI heading for the finding.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>Lead sentence describing the measured issue.</summary>
    public string Lead { get; init; } = string.Empty;

    /// <summary>Preformatted supporting evidence lines for the finding.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>Structured role display labels for findings that enumerate roles (e.g. Slot Congestion); empty otherwise.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>View-ready group of one or more structural findings for panel rendering.</summary>
public sealed record CutLabFindingGroupView
{
    /// <summary>Underlying finding kind represented by this rendered block.</summary>
    public CutLabFindingKind Kind { get; init; }

    /// <summary>UI heading for the rendered group.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>One or more findings rendered inside the group.</summary>
    public IReadOnlyList<CutLabFindingView> Items { get; init; } = [];
}

/// <summary>View-ready role-floor row including count state and provenance text.</summary>
public sealed record CutLabFloorRowView
{
    /// <summary>Stable role key for the floor row.</summary>
    public string RoleKey { get; init; } = string.Empty;

    /// <summary>User-facing role label.</summary>
    public string DisplayLabel { get; init; } = string.Empty;

    /// <summary>Current number of pool cards filling the role.</summary>
    public int InPoolCount { get; init; }

    /// <summary>Bracket band before commander-aware max() resolution and before the plan delta is applied.</summary>
    public int BracketValue { get; init; }

    /// <summary>The commander p25 floor when one was found; otherwise null.</summary>
    public int? CommanderValue { get; init; }

    /// <summary>True only for roles that can ever carry commander-aware floor data.</summary>
    public bool SupportsCommanderFloor { get; init; }

    /// <summary>Prebuilt Commander-cell text, including the two distinct empty states.</summary>
    public string CommanderDisplay { get; init; } = string.Empty;

    /// <summary>Effective floor after merging defaults and user overrides.</summary>
    public int Floor { get; init; }

    /// <summary>Freshly derived default value before user override merge.</summary>
    public int DefaultValue { get; init; }

    /// <summary>Strategy-plan raise applied to the derived default.</summary>
    public int PlanDelta { get; init; }

    /// <summary>True when the user has explicitly overridden the floor.</summary>
    public bool IsUserSet { get; init; }

    /// <summary>True when the pool count is at the caution band of floor plus one or below.</summary>
    public bool AtFloor { get; init; }

    /// <summary>Single-word label naming which number drove the effective default.</summary>
    public string SourceLabel { get; init; } = string.Empty;

    /// <summary>Tooltip sentence describing the floor's default provenance.</summary>
    public string SourceDetail { get; init; } = string.Empty;
}

/// <summary>Plan-panel view: the twelve generic strategy checkboxes, the commander theme checkboxes, and derived display flags.</summary>
public sealed record CutLabPlanPanelView
{
    /// <summary>One row per fixed generic strategy, in catalog declaration order.</summary>
    public IReadOnlyList<CutLabPlanStrategyRowView> StrategyRows { get; init; } = [];

    /// <summary>One row per known EDHREC commander theme, ordered by deck count descending.</summary>
    public IReadOnlyList<CutLabPlanThemeRowView> ThemeRows { get; init; } = [];

    /// <summary>True when the EDHREC commander-theme lookup failed or no commander is known.</summary>
    public bool CommanderThemesUnavailable { get; init; }

    /// <summary>True when nothing at all is checked, so the panel must state the engine is a no-op.</summary>
    public bool ZeroSelectionNotice { get; init; }
}

/// <summary>One generic-strategy checkbox row.</summary>
public sealed record CutLabPlanStrategyRowView
{
    /// <summary>Stable catalog slug, posted as the checkbox value.</summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>User-facing strategy name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>One-line plain-language definition of the strategy.</summary>
    public string Definition { get; init; } = string.Empty;

    /// <summary>One-line mechanical consequence of checking this strategy.</summary>
    public string Consequence { get; init; } = string.Empty;

    /// <summary>True when this strategy is currently checked.</summary>
    public bool IsChecked { get; init; }
}

/// <summary>One commander-theme checkbox row.</summary>
public sealed record CutLabPlanThemeRowView
{
    /// <summary>EDHREC theme slug, posted as the checkbox value.</summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>User-facing theme name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>EDHREC deck count for this theme.</summary>
    public int DeckCount { get; init; }

    /// <summary>This theme's share of the commander's total known-theme deck count, as a percent (0-100).</summary>
    public double SharePercent { get; init; }

    /// <summary>True when this theme is currently checked.</summary>
    public bool IsChecked { get; init; }
}

/// <summary>View-ready goal row including editable turn target and baseline/current trend.</summary>
public sealed record CutLabGoalRowView
{
    /// <summary>Underlying by-turn metric represented by the goal row.</summary>
    public CutLabMetricKind Kind { get; init; }

    /// <summary>Stable DOM/data slug for the goal row.</summary>
    public string GoalKey { get; init; } = string.Empty;

    /// <summary>Named no-JS field used to bind the posted turn target.</summary>
    public string FieldName { get; init; } = string.Empty;

    /// <summary>User-facing label for the goal at its current turn target.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>The editable turn target value.</summary>
    public int TurnValue { get; init; }

    /// <summary>Current working-list probability text for the goal metric.</summary>
    public string CurrentProbability { get; init; } = string.Empty;

    /// <summary>Original-pool baseline probability text for the goal metric.</summary>
    public string BaselineProbability { get; init; } = string.Empty;

    /// <summary>Direction of the current-vs-baseline change.</summary>
    public CutLabMetricDirection Direction { get; init; }

    /// <summary>Magnitude token for the current-vs-baseline change.</summary>
    public string DeltaValueToken { get; init; } = string.Empty;

    /// <summary>True when the representative-line goal should be annotated as uncapped in casual play.</summary>
    public bool IsUncappedInCasual { get; init; }
}

/// <summary>View-ready working-list row for the inline quantity tuner.</summary>
public sealed record CutLabTunableRowView
{
    /// <summary>Display card name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>User-facing role label string for the row.</summary>
    public string RoleLabel { get; init; } = string.Empty;

    /// <summary>Current quantity from the adjustment-derived working list.</summary>
    public int CurrentQuantity { get; init; }

    /// <summary>True when the card can legally appear in multiple copies.</summary>
    public bool IsLegalMultiple { get; init; }

    /// <summary>Legal upper bound for the row's quantity.</summary>
    public int LegalMax { get; init; }

    /// <summary>True when the row represents a locked card that cannot be quantity-tuned.</summary>
    public bool IsLocked { get; init; }

    /// <summary>True when this row was materialized from an added-basic adjustment.</summary>
    public bool IsAddedBasic { get; init; }
}

/// <summary>Sticky round/count bar state for the Cut rounds workspace.</summary>
public sealed record CutLabStickyBarView
{
    /// <summary>True when a current round exists and the sticky bar's round-specific spans should render.</summary>
    public bool HasStickyBar { get; init; }

    /// <summary>Commander-inclusive count of locked cards in the imported pool.</summary>
    public int LockedCount { get; init; }

    /// <summary>Current working-list total after accepted cuts and quantity adjustments.</summary>
    public int CurrentCount { get; init; }

    /// <summary>Round label shown in the left slot of the sticky bar.</summary>
    public string RoundLabel { get; init; } = string.Empty;

    /// <summary>Cards still remaining to cut to reach the target size.</summary>
    public int CardsRemainingToCut { get; init; }

    /// <summary>Accepted cuts recorded in the current session.</summary>
    public int CutsAcceptedCount { get; init; }
}

/// <summary>Current one-at-a-time proposal state for the Cut rounds workspace.</summary>
public sealed record CutLabProposalView
{
    /// <summary>True when there is a proposal card to render.</summary>
    public bool HasProposal { get; init; }

    /// <summary>True when the queue is terminal and there is no next proposal.</summary>
    public bool IsTerminal { get; init; }

    /// <summary>True when the terminal state means the working list is already at 100 cards.</summary>
    public bool IsAtTarget { get; init; }

    /// <summary>True when the terminal state means all remaining cards are locked or protected.</summary>
    public bool IsNothingToCut { get; init; }

    /// <summary>Display card name for the current proposal.</summary>
    public string CardName { get; init; } = string.Empty;

    /// <summary>Stable round key for decision routing and restore context.</summary>
    public string RoundKey { get; init; } = string.Empty;

    /// <summary>Round banner heading copy.</summary>
    public string RoundLabel { get; init; } = string.Empty;

    /// <summary>Round banner supporting copy.</summary>
    public string RoundBannerBody { get; init; } = string.Empty;

    /// <summary>Count of discriminating findings attached to the proposal.</summary>
    public int FindingCount { get; init; }

    /// <summary>Evidence-line sentence shown above the finding chips.</summary>
    public string FindingSummary { get; init; } = string.Empty;

    /// <summary>Neutral evidence chips naming the findings attached to the proposal.</summary>
    public IReadOnlyList<string> FindingChips { get; init; } = [];

    /// <summary>Meaningful delta lines shown in the compact proposal summary.</summary>
    public IReadOnlyList<CutLabDeltaLineView> ChangedDeltaLines { get; init; } = [];

    /// <summary>All proposal delta lines rendered in the full metric breakdown expander.</summary>
    public IReadOnlyList<CutLabDeltaLineView> FullDeltaLines { get; init; } = [];

    /// <summary>Count of metric families whose deltas exceeded the noise floor.</summary>
    public int ChangedFamilyCount { get; init; }

    /// <summary>Fallback copy shown when the proposal renders without delta data.</summary>
    public string DeltaUnavailableMessage { get; init; } = string.Empty;

    /// <summary>Server-authored compact summary for the pinned proposal header.</summary>
    public string GlanceLine { get; init; } = string.Empty;

    /// <summary>Non-blocking floor-warning copy for the proposed cut.</summary>
    public IReadOnlyList<string> FloorWarnings { get; init; } = [];

    /// <summary>Locked-overshoot advisory shown when the pool is still over target but every remaining card is locked.</summary>
    public CutLabLockedOvershootAdvisoryView? LockedOvershootAdvisory { get; init; }
}

/// <summary>View-only locked-overshoot advisory grouping.</summary>
public sealed record CutLabLockedOvershootAdvisoryView
{
    /// <summary>How many cards the working list is still over target.</summary>
    public int CardsOverTarget { get; init; }

    /// <summary>How many ranked cards were omitted after the top 20 cap.</summary>
    public int HiddenCount { get; init; }

    /// <summary>Role-grouped ranked suggestions.</summary>
    public IReadOnlyList<CutLabLockedOvershootGroupView> Groups { get; init; } = [];
}

/// <summary>One role bucket in the locked-overshoot advisory.</summary>
public sealed record CutLabLockedOvershootGroupView
{
    /// <summary>User-facing role label.</summary>
    public string RoleLabel { get; init; } = string.Empty;

    /// <summary>Suggested card names for this role bucket.</summary>
    public IReadOnlyList<string> CardNames { get; init; } = [];
}

/// <summary>One rendered metric delta sentence for the proposal workspace.</summary>
public sealed record CutLabDeltaLineView
{
    /// <summary>User-facing metric label.</summary>
    public string MetricLabel { get; init; } = string.Empty;

    /// <summary>Meaningful display direction for the numeric token.</summary>
    public CutLabMetricDirection Direction { get; init; }

    /// <summary>Formatted numeric token including any directional glyph.</summary>
    public string FormattedValueToken { get; init; } = string.Empty;

    /// <summary>True when the delta exceeds the configured noise floor.</summary>
    public bool IsMeaningful { get; init; }

    /// <summary>Neutral sentence or no-change label shown beside the numeric token.</summary>
    public string Sentence { get; init; } = string.Empty;
}

/// <summary>One restore-list row for an accepted cut.</summary>
public sealed record CutLabCutMadeRowView
{
    /// <summary>Display card name for the accepted cut.</summary>
    public string CardName { get; init; } = string.Empty;

    /// <summary>Stable round key where the cut was recorded.</summary>
    public string RoundKey { get; init; } = string.Empty;

    /// <summary>User-facing round label for the row's muted context text.</summary>
    public string RoundLabel { get; init; } = string.Empty;
}

/// <summary>One baseline-versus-current comparison table row.</summary>
public sealed record CutLabCompareRowView
{
    /// <summary>User-facing metric label.</summary>
    public string MetricLabel { get; init; } = string.Empty;

    /// <summary>Formatted baseline snapshot value.</summary>
    public string BaselineValue { get; init; } = string.Empty;

    /// <summary>Formatted current working-list value.</summary>
    public string CurrentValue { get; init; } = string.Empty;

    /// <summary>Formatted delta token including any directional glyph.</summary>
    public string DeltaValueToken { get; init; } = string.Empty;

    /// <summary>Display direction for the delta token.</summary>
    public CutLabMetricDirection Direction { get; init; }
}

/// <summary>Server-rendered what-if preview state for the no-JS swap form.</summary>
public sealed record CutLabWhatifPreviewView
{
    /// <summary>The working-list card selected to leave the deck.</summary>
    public string CardOut { get; init; } = string.Empty;

    /// <summary>The cut-pile card selected to re-enter the deck.</summary>
    public string CardIn { get; init; } = string.Empty;

    /// <summary>Compare-table rows describing the preview deltas.</summary>
    public IReadOnlyList<CutLabCompareRowView> DeltaRows { get; init; } = [];

    /// <summary>True when a server-rendered what-if preview is available.</summary>
    public bool HasPreview { get; init; }
}

/// <summary>Server-rendered export payload for the Cut Lab export flow.</summary>
public sealed record CutLabExportView
{
    /// <summary>True when a server-rendered export payload is available.</summary>
    public bool HasExport { get; init; }

    /// <summary>Finished-list export text for Moxfield.</summary>
    public string MoxfieldFullListText { get; init; } = string.Empty;

    /// <summary>Finished-list export text for Archidekt.</summary>
    public string ArchidektFullListText { get; init; } = string.Empty;

    /// <summary>CUT/ADD patch text for Moxfield.</summary>
    public string MoxfieldPatchText { get; init; } = string.Empty;

    /// <summary>CUT/ADD patch text for Archidekt.</summary>
    public string ArchidektPatchText { get; init; } = string.Empty;

    /// <summary>True when the finished list has exactly 100 cards.</summary>
    public bool CountOk { get; init; }

    /// <summary>Signed difference from the expected 100-card total.</summary>
    public int OffCount { get; init; }

    /// <summary>True when final-list copy must stay blocked.</summary>
    public bool HardBlock { get; init; }

    /// <summary>Cards confirmed outside the commander's color identity.</summary>
    public IReadOnlyList<string> IllegalColorIdentity { get; init; } = [];

    /// <summary>Cards whose color identity could not be verified.</summary>
    public IReadOnlyList<string> UnverifiedColorIdentity { get; init; } = [];

    /// <summary>Cards present in the finished list that are on the Commander banlist.</summary>
    public IReadOnlyList<string> BanlistOffenders { get; init; } = [];

    /// <summary>Warnings produced while reconstructing the original deck-entry metadata.</summary>
    public IReadOnlyList<string> ReconstructionWarnings { get; init; } = [];

    /// <summary>Additional non-blocking export warnings.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
