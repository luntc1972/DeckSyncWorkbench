using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Reporting;

namespace MtgDeckStudio.Web.Services;

/// <summary>
/// Builds user-facing messages for category suggestion lookups.
/// </summary>
public static class CategorySuggestionMessageBuilder
{
    private const string NoCachedDataMessage = "No card categories for {0} have been observed in the cached data yet. Run Show Categories again to refresh the cache.";
    private const string NoSuggestionsElsewhereMessage = "No category suggestions were found for {0}. You can run the lookup again to retry the live Archidekt and EDHREC checks.";

    /// <summary>
    /// Builds the message that appears when no category suggestions were found.
    /// </summary>
    /// <param name="cardName">Card name that was looked up.</param>
    /// <param name="deckTotals">Deck totals for the card.</param>
    public static string BuildNoSuggestionsMessage(string cardName, CardDeckTotals deckTotals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);
        return deckTotals.TotalDeckCount == 0
            ? string.Format(NoCachedDataMessage, cardName)
            : string.Format(NoSuggestionsElsewhereMessage, cardName);
    }
}
