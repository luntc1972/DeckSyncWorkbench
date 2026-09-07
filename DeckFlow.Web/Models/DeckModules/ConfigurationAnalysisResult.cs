namespace DeckFlow.Web.Models.DeckModules;

/// <summary>
/// The advisory mana-base analysis result for one compiled Deck Modules configuration. This is
/// the JSON contract returned to the browser (nested under the <c>analysis</c> property of the
/// <c>{ analysisKey, analysis }</c> envelope), the object cached in
/// <see cref="DeckFlow.Web.Services.PacketSessionCache"/>, and the object the browser persists in
/// its <c>sessionStorage</c> draft — so it carries only JSON-round-trippable scalars, strings and
/// lists.
/// </summary>
/// <remarks>
/// Per D-22, this record declares no property named <c>IsValid</c>, <c>IsLegal</c>,
/// <c>IsVerifiedLegal</c> or <c>IsStructurallyValid</c>: the analysis path is advisory by
/// construction and must never carry a legality/validity verdict. Only the Phase 1 compiler
/// diagnostics (<see cref="DeckModulesCompilationViewModel"/>) may say whether a build is legal.
/// This record also carries no <c>AnalysisKey</c> member — <see cref="DeckFlow.Web.Controllers.DeckModulesController.Analyze"/>
/// is the sole producer of the cache key, which travels only as the sibling <c>analysisKey</c>
/// field of the response envelope.
/// </remarks>
public sealed record ConfigurationAnalysisResult
{
    /// <summary>
    /// Display definition of "hard to cast": a non-commander card whose simulated cast percent
    /// falls below this threshold. A Phase 3 display definition, not a Core verdict.
    /// </summary>
    public const int HardToCastCastPercentThreshold = 90;

    /// <summary>Gets optional bracket and Game Changer signals for this configuration.</summary>
    public ConfigurationSignalSummary? Signals { get; init; }

    /// <summary>Gets the identifier of the analyzed compiled configuration's selected alternative.</summary>
    public required string ConfigurationId { get; init; }

    /// <summary>Gets the player-facing name of the analyzed compiled configuration's selected alternative.</summary>
    public required string ConfigurationName { get; init; }

    /// <summary>Gets the total number of cards analyzed (the compiled configuration's total card count).</summary>
    public required int AnalyzedCardCount { get; init; }

    /// <summary>Gets the number of lands actually in the analyzed configuration.</summary>
    public required int LandCount { get; init; }

    /// <summary>Gets the Karsten-recommended land count for the analyzed configuration's curve.</summary>
    public required double TargetLandCount { get; init; }

    /// <summary>Gets the land count minus the target land count; negative means too few lands.</summary>
    public required double LandDelta { get; init; }

    /// <summary>Gets the overall mana-base health verdict, as a display string.</summary>
    public required string Health { get; init; }

    /// <summary>Gets the count of non-land mana sources (rocks/dorks) in the analyzed configuration.</summary>
    public required int RampSourceCount { get; init; }

    /// <summary>Gets the count of non-commander cards whose cast percent falls below <see cref="HardToCastCastPercentThreshold"/>.</summary>
    public required int HardToCastCount { get; init; }

    /// <summary>Gets the per-colour source findings, in the order the report supplies (tail-risk composite — do not re-sort).</summary>
    public IReadOnlyList<ConfigurationColorSourceRow> ColorSources { get; init; } = Array.Empty<ConfigurationColorSourceRow>();

    /// <summary>Gets card names the analyzer could not resolve, if any.</summary>
    public IReadOnlyList<string> UnresolvedCardNames { get; init; } = Array.Empty<string>();

    /// <summary>Gets whether the analyzed alternative contributed zero mainboard/mana-support entries (D-21).</summary>
    public required bool IsCoreOnly { get; init; }

    /// <summary>
    /// Gets the incomplete-deck notice shown when <see cref="IsCoreOnly"/> is <see langword="true"/>;
    /// <see langword="null"/> otherwise.
    /// </summary>
    public string? AnalysisNotice { get; init; }
}

/// <summary>One per-colour mana-source finding row, projected for display from <c>ManabaseReport.ColorFindings</c>.</summary>
public sealed record ConfigurationColorSourceRow
{
    /// <summary>Gets the color examined, as a display string.</summary>
    public required string Color { get; init; }

    /// <summary>Gets the display label for this row — the special-category label when present, otherwise <see cref="Color"/>.</summary>
    public required string DisplayColor { get; init; }

    /// <summary>Gets the effective sources of this color currently in the deck (weighted).</summary>
    public required double ActualSources { get; init; }

    /// <summary>Gets the sources required by the most demanding spell of this color (Karsten threshold).</summary>
    public required int RequiredSources { get; init; }

    /// <summary>Gets the required sources minus the actual sources.</summary>
    public required double Deficit { get; init; }

    /// <summary>Gets the name of the spell driving this color's requirement.</summary>
    public required string DrivingSpell { get; init; }
}
