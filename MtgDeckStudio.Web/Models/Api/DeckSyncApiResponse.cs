using System.Collections.Generic;
using MtgDeckStudio.Core.Models;

namespace MtgDeckStudio.Web.Models.Api;

/// <summary>
/// Structured deck-sync response returned by the JSON API.
/// </summary>
public sealed record DeckSyncApiResponse(
    string ReportText,
    string DeltaText,
    string FullImportText,
    string InstructionsText,
    string SourceSystem,
    string TargetSystem,
    DeckSyncApiDiffSummary Summary,
    IReadOnlyList<PrintingConflictDto> PrintingConflicts);

/// <summary>
/// Summary counts from the generated deck diff.
/// </summary>
public sealed record DeckSyncApiDiffSummary(
    int AddCount,
    int CountMismatchCount,
    int OnlyInTargetCount,
    int PrintingConflictCount);

/// <summary>
/// Simplified printing-conflict row used by the web API.
/// </summary>
public sealed record PrintingConflictDto(
    string CardName,
    string ArchidektSetCode,
    string ArchidektCollectorNumber,
    string? ArchidektCategory,
    string? MoxfieldSetCode,
    string? MoxfieldCollectorNumber,
    string? SuggestedResolution);
