using System;
using System.Collections.Generic;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Reporting;

namespace MtgDeckStudio.Web.Models.Api;

/// <summary>
/// Response payload returned from the card suggestion API.
/// </summary>
public sealed class CategorySuggestionApiResponse
{
    /// <summary>
    /// Card name that was queried.
    /// </summary>
    public string CardName { get; init; } = string.Empty;
    /// <summary>
    /// Exact category text from the optional reference deck.
    /// </summary>
    public string ExactCategoriesText { get; init; } = string.Empty;
    public string ExactSuggestionContextText { get; init; } = string.Empty;
    /// <summary>
    /// Categories inferred from the local cache.
    /// </summary>
    public string InferredCategoriesText { get; init; } = string.Empty;
    public string InferredSuggestionContextText { get; init; } = string.Empty;
    /// <summary>
    /// Fallback themes inferred from EDHREC data.
    /// </summary>
    public string EdhrecCategoriesText { get; init; } = string.Empty;
    public string EdhrecSuggestionContextText { get; init; } = string.Empty;
    public bool HasExactCategories { get; init; }
    public bool HasInferredCategories { get; init; }
    public bool HasEdhrecCategories { get; init; }
    /// <summary>
    /// Oracle/functional tags from Scryfall Tagger.
    /// </summary>
    public string TaggerCategoriesText { get; init; } = string.Empty;
    public string TaggerSuggestionContextText { get; init; } = string.Empty;
    public bool HasTaggerCategories { get; init; }
    public string? SuggestionSourceSummary { get; init; }
    public bool NoSuggestionsFound { get; init; }
    public string? NoSuggestionsMessage { get; init; }
    public CardDeckTotals CardDeckTotals { get; init; } = CardDeckTotals.Empty;
    public int AdditionalDecksFound { get; init; }
    public bool CacheSweepPerformed { get; init; }
}

/// <summary>
/// Response payload returned from the commander category API.
/// </summary>
public sealed class CommanderCategoryApiResponse
{
    /// <summary>
    /// Commander name that was queried.
    /// </summary>
    public string CommanderName { get; init; } = string.Empty;
    public int CardRowCount { get; init; }
    public int CategoryCount { get; init; }
    public int HarvestedDeckCount { get; init; }
    public int AdditionalDecksFound { get; init; }
    public CardDeckTotals CardDeckTotals { get; init; } = CardDeckTotals.Empty;
    public bool CacheSweepPerformed { get; init; }
    public IReadOnlyList<CommanderCategorySummaryDto> Summaries { get; init; } = Array.Empty<CommanderCategorySummaryDto>();
    public string? NoResultsMessage { get; init; }
}

/// <summary>
/// Simple DTO describing a commander category summary.
/// </summary>
public sealed class CommanderCategorySummaryDto
{
    public string Category { get; init; } = string.Empty;
    public int Count { get; init; }
    public int DeckCount { get; init; }
}

/// <summary>
/// Response payload returned from the mechanic rules lookup API.
/// </summary>
public sealed class MechanicLookupApiResponse
{
    /// <summary>
    /// Mechanic name that was queried.
    /// </summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>
    /// Whether a matching mechanic entry was found.
    /// </summary>
    public bool Found { get; init; }

    /// <summary>
    /// Matched mechanic or rules term.
    /// </summary>
    public string? MechanicName { get; init; }

    /// <summary>
    /// Primary rule reference for the match.
    /// </summary>
    public string? RuleReference { get; init; }

    /// <summary>
    /// Explains how the mechanic was matched.
    /// </summary>
    public string? MatchType { get; init; }

    /// <summary>
    /// Official rules text returned from Wizards.
    /// </summary>
    public string? RulesText { get; init; }

    /// <summary>
    /// Optional summary text when available.
    /// </summary>
    public string? SummaryText { get; init; }

    /// <summary>
    /// Official Wizards rules page URL.
    /// </summary>
    public string RulesPageUrl { get; init; } = string.Empty;

    /// <summary>
    /// Direct URL to the current Comprehensive Rules text file.
    /// </summary>
    public string? RulesTextUrl { get; init; }

    /// <summary>
    /// User-facing not found message.
    /// </summary>
    public string? NotFoundMessage { get; init; }
}

public sealed class ArchidektCacheJobStartRequest
{
    public int DurationSeconds { get; init; }
}

public class ArchidektCacheJobStatusResponse
{
    public Guid JobId { get; init; }
    public string State { get; init; } = string.Empty;
    public int DurationSeconds { get; init; }
    public DateTimeOffset RequestedUtc { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public int DecksProcessed { get; init; }
    public int AdditionalDecksFound { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ArchidektCacheJobEnqueueResponse : ArchidektCacheJobStatusResponse
{
    public bool StartedNewJob { get; init; }
    public string StatusUrl { get; init; } = string.Empty;
}
