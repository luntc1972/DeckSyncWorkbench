using MtgDeckStudio.Core.Reporting;
using Microsoft.Extensions.Logging;

namespace MtgDeckStudio.Web.Services;

public interface ICategoryKnowledgeStore
{
    Task<IReadOnlyList<CategoryKnowledgeRow>> GetCategoryRowsAsync(string cardName, string? boardFilter = null, CancellationToken cancellationToken = default);
    Task<int> GetProcessedDeckCountAsync(CancellationToken cancellationToken = default);
    Task<int> RunCacheSweepAsync(ILogger logger, int durationSeconds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetCategoriesAsync(string cardName, CancellationToken cancellationToken = default);
    Task PersistObservedCategoriesAsync(string source, string cardName, IReadOnlyList<string> categories, int quantity = 1, string board = "mainboard", int deckCountIncrement = 0, CancellationToken cancellationToken = default);
    Task<CardDeckTotals> GetCardDeckTotalsAsync(string cardName, string? boardFilter = null, CancellationToken cancellationToken = default);
}
