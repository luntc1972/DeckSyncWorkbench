using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.MeasuredStyleExtraction;
using DeckFlow.Web.Services;

namespace DeckFlow.Web.Services.CreatorStyle;

/// <summary>
/// Resolves multi-bucket per-card categories for creator deck samples.
/// </summary>
public sealed class CreatorDeckCategoryResolver
{
    private readonly CategoryKnowledgeRepository _categoryKnowledgeRepository;
    private readonly IScryfallTaggerLookupService _taggerLookupService;

    /// <summary>
    /// Creates a category resolver that prefers harvested repository categories and uses Tagger only for the tail.
    /// </summary>
    public CreatorDeckCategoryResolver(
        CategoryKnowledgeRepository categoryKnowledgeRepository,
        IScryfallTaggerLookupService taggerLookupService)
    {
        ArgumentNullException.ThrowIfNull(categoryKnowledgeRepository);
        ArgumentNullException.ThrowIfNull(taggerLookupService);
        _categoryKnowledgeRepository = categoryKnowledgeRepository;
        _taggerLookupService = taggerLookupService;
    }

    /// <summary>
    /// Resolves categories for each distinct card name present in the supplied creator deck samples.
    /// </summary>
    /// <param name="samples">Creator deck samples to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Case-insensitive card-name map to multi-bucket category labels.</returns>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ResolveAsync(
        IReadOnlyList<CreatorDeckSample> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var distinctNames = samples
            .SelectMany(sample => sample.Entries)
            .Select(entry => entry.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolved = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cardName in distinctNames)
        {
            IReadOnlyList<string> categories = await _categoryKnowledgeRepository
                .GetCategoriesAsync(cardName!, cancellationToken)
                .ConfigureAwait(false);

            if (categories.Count == 0)
            {
                categories = await _taggerLookupService
                    .LookupOracleTagsAsync(cardName!, cancellationToken)
                    .ConfigureAwait(false);
            }

            resolved[cardName!] = categories
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return resolved;
    }
}
