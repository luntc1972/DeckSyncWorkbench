using System.Net;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Services.Manabase;
using DeckFlow.Web.Services.Scryfall;
using RestSharp;

namespace DeckFlow.Web.Services.CreatorStyle;

/// <summary>
/// Shared creator-style Scryfall resolution and manabase analysis helper.
/// </summary>
internal static class CreatorStyleDeckAnalysis
{
    internal static async Task<SubmittedDeckResolution> AnalyzeSubmittedDeckAsync(
        IReadOnlyList<DeckEntry> entries,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> executeCollectionAsync,
        Func<string, CancellationToken, Task<ScryfallCard?>> searchFallbackCardAsync,
        Action<string> unresolvedCardLogger,
        string errorMessageSuffix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(executeCollectionAsync);
        ArgumentNullException.ThrowIfNull(searchFallbackCardAsync);
        ArgumentNullException.ThrowIfNull(unresolvedCardLogger);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessageSuffix);

        ResolvedDeckEntries resolvedDeck = await ResolveDeckEntriesAsync(
            entries,
            executeCollectionAsync,
            searchFallbackCardAsync,
            unresolvedCardLogger,
            errorMessageSuffix,
            includeCommanderCard: true,
            cancellationToken).ConfigureAwait(false);

        if (resolvedDeck.Entries.Count == 0)
        {
            return new SubmittedDeckResolution
            {
                Report = EmptyReport(),
                DeckContext = new CardGroundingDeckContext
                {
                    CommanderColorIdentity = new HashSet<string>(StringComparer.Ordinal),
                    DeckProducedColors = new HashSet<char>(),
                    DeckCardNames = new HashSet<string>(StringComparer.Ordinal)
                },
                ResolvedCommanderName = null,
                HasResolvedDeck = false
            };
        }

        ManabaseDeck deck = Classify(resolvedDeck.Entries);
        ManabaseReport report = Analyze(deck);

        return new SubmittedDeckResolution
        {
            Report = report,
            DeckContext = new CardGroundingDeckContext
            {
                CommanderColorIdentity = resolvedDeck.ResolvedCommanderCard?.ColorIdentity?
                    .Where(IsWubrgSymbol)
                    .ToHashSet(StringComparer.Ordinal)
                    ?? new HashSet<string>(StringComparer.Ordinal),
                DeckProducedColors = deck.Sources
                    .SelectMany(source => source.Produces)
                    .Select(ToWubrgChar)
                    .Where(color => color != '\0')
                    .ToHashSet(),
                DeckCardNames = entries
                    .Select(entry => CardNormalizer.Normalize(entry.Name))
                    .ToHashSet(StringComparer.Ordinal)
            },
            ResolvedCommanderName = resolvedDeck.ResolvedCommanderCard?.Name,
            HasResolvedDeck = true
        };
    }

    internal static async Task<ManabaseReport> AnalyzeDeckAsync(
        IReadOnlyList<DeckEntry> entries,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> executeCollectionAsync,
        Func<string, CancellationToken, Task<ScryfallCard?>> searchFallbackCardAsync,
        Action<string> unresolvedCardLogger,
        string errorMessageSuffix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(executeCollectionAsync);
        ArgumentNullException.ThrowIfNull(searchFallbackCardAsync);
        ArgumentNullException.ThrowIfNull(unresolvedCardLogger);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessageSuffix);

        ResolvedDeckEntries resolvedDeck = await ResolveDeckEntriesAsync(
            entries,
            executeCollectionAsync,
            searchFallbackCardAsync,
            unresolvedCardLogger,
            errorMessageSuffix,
            includeCommanderCard: false,
            cancellationToken).ConfigureAwait(false);

        if (resolvedDeck.Entries.Count == 0)
        {
            return EmptyReport();
        }

        return Analyze(Classify(resolvedDeck.Entries));
    }

    private static async Task<ResolvedDeckEntries> ResolveDeckEntriesAsync(
        IReadOnlyList<DeckEntry> entries,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> executeCollectionAsync,
        Func<string, CancellationToken, Task<ScryfallCard?>> searchFallbackCardAsync,
        Action<string> unresolvedCardLogger,
        string errorMessageSuffix,
        bool includeCommanderCard,
        CancellationToken cancellationToken)
    {
        ResolvedScryfallCards resolvedCards = await ResolveCardsAsync(
            entries,
            executeCollectionAsync,
            errorMessageSuffix,
            cancellationToken).ConfigureAwait(false);
        var deckEntries = new List<DeckCardEntry>(entries.Count);
        ScryfallCard? resolvedCommanderCard = null;

        foreach (DeckEntry entry in entries)
        {
            if (!resolvedCards.TryResolve(entry.Name, entry.SetCode, entry.CollectorNumber, out ScryfallCardData? card))
            {
                ScryfallCard? fallback = await searchFallbackCardAsync(entry.Name, cancellationToken).ConfigureAwait(false);
                if (fallback is not null)
                {
                    resolvedCards.Add(fallback);
                    resolvedCards.TryResolve(entry.Name, entry.SetCode, entry.CollectorNumber, out card);
                }
            }

            if (card is null)
            {
                unresolvedCardLogger(entry.Name);
                continue;
            }

            bool isCommander = string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase);
            if (includeCommanderCard && isCommander && resolvedCommanderCard is null)
            {
                resolvedCommanderCard = resolvedCards.GetRawCard(card);
            }

            deckEntries.Add(new DeckCardEntry
            {
                Card = card,
                Quantity = entry.Quantity,
                IsCommander = isCommander
            });
        }

        return new ResolvedDeckEntries(deckEntries, resolvedCommanderCard);
    }

    internal static async Task<ResolvedScryfallCards> ResolveCardsAsync(
        IReadOnlyList<DeckEntry> deckCards,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallCollectionResponse>>> executeCollectionAsync,
        string errorMessageSuffix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deckCards);
        ArgumentNullException.ThrowIfNull(executeCollectionAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessageSuffix);

        var resolvedCards = new ResolvedScryfallCards();
        IReadOnlyList<ScryfallCard> cards = await ScryfallCollectionResolver.ResolveCardsAsync(
            deckCards,
            executeCollectionAsync,
            errorMessageSuffix,
            cancellationToken).ConfigureAwait(false);
        foreach (ScryfallCard card in cards)
        {
            resolvedCards.Add(card);
        }

        return resolvedCards;
    }

    internal static double ToHealthScore(ManabaseHealth health)
    {
        return health switch
        {
            ManabaseHealth.Healthy => 3,
            ManabaseHealth.Functional => 2,
            ManabaseHealth.Workable => 1,
            _ => 0
        };
    }

    internal static ManabaseReport EmptyReport()
        => new()
        {
            ActualLands = 0,
            TargetLands = 0,
            ColorFindings = Array.Empty<ColorSourceFinding>(),
            Mode = ManabaseMode.Casual,
            Castability = Array.Empty<CardCastability>(),
            ColorSpellCounts = new Dictionary<ManaColor, int>(),
            CommanderColors = Array.Empty<ManaColor>(),
            LandTarget = null,
            TapAnalysis = null,
            MulliganEvaluation = null,
            DemandingCards = Array.Empty<DemandingCard>(),
            RampSourceNames = Array.Empty<string>(),
            RampAndDrawNames = Array.Empty<string>(),
            UnsupportedInteractions = Array.Empty<UnsupportedInteraction>(),
            Summary = string.Empty
        };

    internal static ManabaseDeck Classify(IReadOnlyList<DeckCardEntry> deckEntries)
        => ManabaseClassifier.Classify(ScryfallCardFactMapper.ToCardFacts(deckEntries), isSingleton: true);

    internal static ManabaseReport Analyze(ManabaseDeck deck)
        => ManabaseAnalyzer.Analyze(deck, ManabaseMode.Casual);

    private static bool IsWubrgSymbol(string symbol)
        => symbol is "W" or "U" or "B" or "R" or "G";

    private static char ToWubrgChar(ManaColor color)
    {
        return color switch
        {
            ManaColor.White => 'W',
            ManaColor.Blue => 'U',
            ManaColor.Black => 'B',
            ManaColor.Red => 'R',
            ManaColor.Green => 'G',
            _ => '\0'
        };
    }

    private sealed record ResolvedDeckEntries(
        IReadOnlyList<DeckCardEntry> Entries,
        ScryfallCard? ResolvedCommanderCard);

    internal sealed class ResolvedScryfallCards
    {
        private readonly ScryfallCardNameIndex _nameIndex = new();
        private readonly Dictionary<ScryfallCardData, ScryfallCard> _rawCardsByData = new(ReferenceEqualityComparer.Instance);

        public void Add(ScryfallCard card)
        {
            ArgumentNullException.ThrowIfNull(card);

            ScryfallCardData cardData = ScryfallCardDataMapper.ToCardData(card);
            _nameIndex.Add(cardData);
            _rawCardsByData[cardData] = card;
        }

        public bool TryResolve(string name, string? setCode, string? collectorNumber, out ScryfallCardData? card)
        {
            ArgumentNullException.ThrowIfNull(name);

            return _nameIndex.TryResolve(name, setCode, collectorNumber, out card);
        }

        public ScryfallCard GetRawCard(ScryfallCardData cardData)
        {
            if (_rawCardsByData.TryGetValue(cardData, out ScryfallCard? rawCard))
            {
                return rawCard;
            }

            throw new InvalidOperationException("Resolved creator-style card data did not have a matching raw Scryfall card.");
        }
    }
}
