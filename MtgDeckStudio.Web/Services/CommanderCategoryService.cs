using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MtgDeckStudio.Core.Reporting;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Web.Models;
using Microsoft.Extensions.Logging;

namespace MtgDeckStudio.Web.Services;

/// <summary>
/// Provides cached insights about commander category usage.
/// </summary>
public interface ICommanderCategoryService
{
    /// <summary>
    /// Retrieves category usage for the specified commander.
    /// </summary>
    Task<CommanderCategoryResult> LookupAsync(string commanderName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the commander category lookup outcome.
/// </summary>
public sealed record CommanderCategoryResult(
    string CommanderName,
    IReadOnlyList<CategoryKnowledgeRow> Rows,
    IReadOnlyList<CommanderCategorySummary> Summaries,
    int HarvestedDeckCount,
    CardDeckTotals CardDeckTotals,
    int AdditionalDecksFound,
    bool CacheSweepPerformed);

/// <summary>
/// Default implementation of the commander category service.
/// </summary>
public sealed class CommanderCategoryService : ICommanderCategoryService
{
    private const int ClickSweepDurationSeconds = 30;
    private readonly ICategoryKnowledgeStore _knowledgeStore;
    private readonly ILogger<CommanderCategoryService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CommanderCategoryService"/>.
    /// </summary>
    public CommanderCategoryService(ICategoryKnowledgeStore knowledgeStore, ILogger<CommanderCategoryService> logger)
    {
        _knowledgeStore = knowledgeStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CommanderCategoryResult> LookupAsync(string commanderName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commanderName);

        var trimmed = commanderName.Trim();
        var initialDeckCount = await _knowledgeStore.GetProcessedDeckCountAsync(cancellationToken);
        await _knowledgeStore.RunCacheSweepAsync(_logger, ClickSweepDurationSeconds, cancellationToken);

        var rows = await _knowledgeStore.GetCategoryRowsAsync(trimmed, boardFilter: "commander", cancellationToken);

        var deckCount = await _knowledgeStore.GetProcessedDeckCountAsync(cancellationToken);
        var cardTotals = await _knowledgeStore.GetCardDeckTotalsAsync(trimmed, boardFilter: "commander", cancellationToken);
        var summaries = rows
            .GroupBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CommanderCategorySummary(
                group.Key,
                group.Sum(row => row.Count),
                group.Sum(row => row.DeckCount)))
            .OrderByDescending(summary => summary.Count)
            .ThenBy(summary => summary.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var additionalDecksFound = Math.Max(deckCount - initialDeckCount, 0);
        return new CommanderCategoryResult(trimmed, rows, summaries, deckCount, cardTotals, additionalDecksFound, true);
    }
}
