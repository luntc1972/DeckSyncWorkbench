using DeckFlow.Core.Integration;

namespace DeckFlow.Core.Knowledge.StatedRulesExtraction;

/// <summary>
/// Pure coordinator for multi-pass stated-rules extraction over transcript chunks.
/// </summary>
// Why (WR-09, maintainer decision 2026-09-06 per WAITING.json): no production host wires this
// coordinator yet - it is the intended entry point once a host calls ExtractAsync against real
// transcripts, exercised today only by DeckFlow.Core.Tests. Kept rather than deleted; see
// ai-context-deckflow/repos/deckflow/notes/2026-09-06-cycle20-branch-divergence.md.
public sealed class StatedRulesExtractor
{
    private readonly ILlmDistillationService _distiller;
    private readonly ICardNameGrounder? _cardGrounder;

    /// <summary>
    /// Initializes the stated-rules extraction coordinator.
    /// </summary>
    /// <param name="distiller">Injected multi-stage distillation service.</param>
    /// <param name="cardGrounder">Optional card-name grounder.</param>
    public StatedRulesExtractor(ILlmDistillationService distiller, ICardNameGrounder? cardGrounder = null)
    {
        _distiller = distiller ?? throw new ArgumentNullException(nameof(distiller));
        _cardGrounder = cardGrounder;
    }

    /// <summary>
    /// Extracts validated stated-rule candidates from a transcript.
    /// </summary>
    /// <param name="transcript">Transcript text to process.</param>
    /// <param name="videoDateUtc">Source video publish date for provenance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validated, reduced, and optionally grounded rules.</returns>
    public async Task<IReadOnlyList<StatedRuleCandidate>> ExtractAsync(
        string transcript,
        DateTimeOffset videoDateUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);

        IReadOnlyList<string> chunks = TranscriptChunker.Chunk(transcript);
        var allChunkRules = new List<StatedRuleCandidate>();

        foreach (string chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            SelectResult selected = await _distiller
                .SelectStatedClaimsAsync(chunk, ct)
                .ConfigureAwait(false);
            DisambiguateResult disambiguated = await _distiller
                .DisambiguateStatedClaimsAsync(selected.Claims, ct)
                .ConfigureAwait(false);
            DecomposeResult decomposed = await _distiller
                .DecomposeStatedClaimsAsync(disambiguated.Claims, videoDateUtc, ct)
                .ConfigureAwait(false);

            allChunkRules.AddRange(decomposed.Rules);
        }

        ReduceResult reduced = await _distiller
            .ReduceStatedRulesAsync(allChunkRules, videoDateUtc, ct)
            .ConfigureAwait(false);

        // Why: Claimify stops at per-claim decomposition; DeckFlow adds an LLM reduce pass plus
        // deterministic dedupe so cross-chunk repeats collapse without inventing new rules.
        IReadOnlyList<StatedRuleCandidate> deduped = StatedRuleReducer.Reduce(reduced.Rules, allChunkRules);
        IReadOnlyList<StatedRuleCandidate> grounded = _cardGrounder is null
            ? deduped
            : await GroundCardReferencesAsync(deduped, ct).ConfigureAwait(false);

        DistillationValidation.ValidateStatedRules(grounded);
        return grounded;
    }

    private async Task<IReadOnlyList<StatedRuleCandidate>> GroundCardReferencesAsync(
        IReadOnlyList<StatedRuleCandidate> rules,
        CancellationToken ct)
    {
        var groundingCache = new Dictionary<string, CardGroundingResult>(StringComparer.OrdinalIgnoreCase);

        foreach (string cardReference in rules
                     .Select(rule => rule.CardReference)
                     .Where(cardReference => !string.IsNullOrWhiteSpace(cardReference))
                     .Distinct(StringComparer.OrdinalIgnoreCase)!)
        {
            ct.ThrowIfCancellationRequested();
            groundingCache[cardReference] = await _cardGrounder!
                .TryGroundAsync(cardReference, ct)
                .ConfigureAwait(false);
        }

        return rules
            .Select(
                rule =>
                {
                    if (string.IsNullOrWhiteSpace(rule.CardReference))
                    {
                        return rule;
                    }

                    CardGroundingResult grounding = groundingCache[rule.CardReference];
                    return grounding.Resolved
                        ? rule with
                        {
                            CardReference = grounding.CanonicalName,
                            CardGrounded = true,
                        }
                        : rule with
                        {
                            CardGrounded = false,
                        };
                })
            .ToList();
    }
}
