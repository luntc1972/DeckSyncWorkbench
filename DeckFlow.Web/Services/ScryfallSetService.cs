using System.Text;
using System.Text.RegularExpressions;
using DeckFlow.Web.Models;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;

namespace DeckFlow.Web.Services;

public interface IScryfallSetService
{
    Task<IReadOnlyList<ScryfallSetOption>> GetSetsAsync(CancellationToken cancellationToken = default);

    Task<string> BuildSetPacketAsync(
        IReadOnlyList<string> setCodes,
        IReadOnlyList<string>? commanderColorIdentity = null,
        CancellationToken cancellationToken = default);
}

public sealed partial class ScryfallSetService : IScryfallSetService
{
    private const string SetCacheKey = "scryfall-set-options-v2";
    private static readonly TimeSpan SetCacheDuration = TimeSpan.FromHours(6);
    private const int MaxCardsPerSetPacket = 60;
    private const int MaxMechanicsPerSetPacket = 12;
    private static readonly Regex AbilityWordRegex = AbilityWordPattern();
    private readonly IMemoryCache _cache;
    private readonly IMechanicLookupService _mechanicLookupService;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSetListResponse>>> _executeSetListAsync;
    private readonly Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>> _executeSearchAsync;

    public ScryfallSetService(
        IMemoryCache cache,
        IMechanicLookupService mechanicLookupService,
        RestClient? restClient = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSetListResponse>>>? executeSetListAsync = null,
        Func<RestRequest, CancellationToken, Task<RestResponse<ScryfallSearchResponse>>>? executeSearchAsync = null)
    {
        _cache = cache;
        _mechanicLookupService = mechanicLookupService;
        var client = restClient ?? ScryfallRestClientFactory.Create();
        _executeSetListAsync = executeSetListAsync ?? ((request, cancellationToken) => ScryfallThrottle.ExecuteAsync(token => client.ExecuteAsync<ScryfallSetListResponse>(request, token), cancellationToken));
        _executeSearchAsync = executeSearchAsync ?? ((request, cancellationToken) => ScryfallThrottle.ExecuteAsync(token => client.ExecuteAsync<ScryfallSearchResponse>(request, token), cancellationToken));
    }

    public async Task<IReadOnlyList<ScryfallSetOption>> GetSetsAsync(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue<IReadOnlyList<ScryfallSetOption>>(SetCacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var request = new RestRequest("sets", Method.Get);
        var response = await _executeSetListAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
        {
            throw new HttpRequestException(
                $"Scryfall set catalog lookup returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var sets = response.Data.Data
            .Where(set => !set.Digital)
            .Where(set => set.CardCount > 0)
            .Where(set => !string.Equals(set.SetType, "token", StringComparison.OrdinalIgnoreCase))
            .Where(set => !string.Equals(set.SetType, "minigame", StringComparison.OrdinalIgnoreCase))
            .Where(set => !string.Equals(set.SetType, "memorabilia", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(set => ParseReleasedAt(set.ReleasedAt))
            .ThenBy(set => set.Name, StringComparer.OrdinalIgnoreCase)
            .Select(set => new ScryfallSetOption(set.Code, set.Name, set.ReleasedAt, set.SetType))
            .ToList();

        _cache.Set(SetCacheKey, sets, SetCacheDuration);
        return sets;
    }

    public async Task<string> BuildSetPacketAsync(
        IReadOnlyList<string> setCodes,
        IReadOnlyList<string>? commanderColorIdentity = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCodes = setCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var normalizedCommanderIdentity = (commanderColorIdentity ?? Array.Empty<string>())
            .Where(color => !string.IsNullOrWhiteSpace(color))
            .Select(color => color.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedCodes.Count == 0)
        {
            return string.Empty;
        }

        var knownSets = await GetSetsAsync(cancellationToken).ConfigureAwait(false);
        var cardsBySet = new List<(ScryfallSetOption Set, IReadOnlyList<ScryfallCard> Cards, int LegalCardCount)>();
        var mechanicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var setCode in normalizedCodes)
        {
            var set = knownSets.FirstOrDefault(option => string.Equals(option.Code, setCode, StringComparison.OrdinalIgnoreCase))
                ?? new ScryfallSetOption(setCode, setCode.ToUpperInvariant(), null);
            var cards = await FetchCardsForSetAsync(setCode, cancellationToken).ConfigureAwait(false);
            if (normalizedCommanderIdentity.Count > 0)
            {
                cards = cards
                    .Where(card => IsPlayableInCommanderIdentity(card, normalizedCommanderIdentity))
                    .ToList();
            }

            var compactCards = BuildCompactCardPacket(cards);
            cardsBySet.Add((set, compactCards, cards.Count));

            foreach (var card in compactCards)
            {
                foreach (var keyword in card.Keywords ?? Array.Empty<string>())
                {
                    mechanicNames.Add(keyword);
                }

                foreach (var abilityWord in ExtractAbilityWords(card.OracleText))
                {
                    mechanicNames.Add(abilityWord);
                }
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine("set_packet:");
        builder.AppendLine($"generated_at_utc: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        builder.AppendLine("sets:");
        foreach (var item in cardsBySet)
        {
            builder.AppendLine($"- {item.Set.Name} ({item.Set.Code.ToUpperInvariant()})");
        }

        builder.AppendLine();
        builder.AppendLine("selection_notes:");
        builder.AppendLine("- This is a compact candidate packet, not a full set dump.");
        builder.AppendLine($"- Each set is trimmed to the top {MaxCardsPerSetPacket} color-legal candidate cards by heuristic relevance.");
        builder.AppendLine("- Basic lands and low-signal generic mana-fixing cards are excluded to keep the prompt small enough to send reliably.");
        builder.AppendLine();
        builder.AppendLine("mechanics:");
        var mechanics = await BuildMechanicLinesAsync(mechanicNames, cancellationToken).ConfigureAwait(false);
        if (mechanics.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var line in mechanics)
            {
                builder.AppendLine(line);
            }
        }

        foreach (var item in cardsBySet)
        {
            builder.AppendLine();
            builder.AppendLine($"set: {item.Set.Name} ({item.Set.Code.ToUpperInvariant()})");
            builder.AppendLine($"candidate_cards_included: {item.Cards.Count}");
            builder.AppendLine($"color_legal_cards_scanned: {item.LegalCardCount}");
            builder.AppendLine("cards:");
            foreach (var card in item.Cards)
            {
                builder.AppendLine($"{card.Name} | {card.ManaCost ?? string.Empty} | {card.TypeLine} | {NormalizeOracleText(card)}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<IReadOnlyList<ScryfallCard>> FetchCardsForSetAsync(string setCode, CancellationToken cancellationToken)
    {
        var cards = new List<ScryfallCard>();
        var nextPage = $"cards/search?q=e%3A{Uri.EscapeDataString(setCode)}&order=set&unique=cards&include_extras=false&include_multilingual=false";

        while (!string.IsNullOrWhiteSpace(nextPage))
        {
            var resource = nextPage;
            if (Uri.TryCreate(nextPage, UriKind.Absolute, out var nextUri))
            {
                resource = $"{nextUri.AbsolutePath.TrimStart('/')}?{nextUri.Query.TrimStart('?')}".TrimEnd('?');
            }

            var request = new RestRequest(resource, Method.Get);

            var response = await _executeSearchAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || response.Data is null)
            {
                throw new HttpRequestException(
                    $"Scryfall set card lookup returned HTTP {(int)response.StatusCode} for set {setCode}.",
                    null,
                    response.StatusCode);
            }

            cards.AddRange(response.Data.Data);
            nextPage = response.Data.HasMore ? response.Data.NextPage : null;
        }

        return cards
            .OrderBy(card => ParseCollectorNumber(card.CollectorNumber))
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> BuildMechanicLinesAsync(IReadOnlyCollection<string> mechanicNames, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        foreach (var mechanicName in mechanicNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(MaxMechanicsPerSetPacket))
        {
            var result = await _mechanicLookupService.LookupAsync(mechanicName, cancellationToken).ConfigureAwait(false);
            if (!result.Found)
            {
                continue;
            }

            var description = result.SummaryText ?? result.RulesText ?? string.Empty;
            lines.Add($"{mechanicName}: {CollapseWhitespace(description)}");
        }

        return lines;
    }

    private static IEnumerable<string> ExtractAbilityWords(string? oracleText)
    {
        if (string.IsNullOrWhiteSpace(oracleText))
        {
            yield break;
        }

        foreach (var line in oracleText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = AbilityWordRegex.Match(line.Trim());
            if (match.Success)
            {
                yield return match.Groups["term"].Value.Trim();
            }
        }
    }

    private static string NormalizeOracleText(ScryfallCard card)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(card.OracleText))
        {
            parts.Add(CollapseWhitespace(card.OracleText));
        }

        if (!string.IsNullOrWhiteSpace(card.Power) && !string.IsNullOrWhiteSpace(card.Toughness))
        {
            parts.Add($"{card.Power}/{card.Toughness}");
        }

        return string.Join(" ", parts);
    }

    private static string CollapseWhitespace(string value)
        => string.Join(" ", value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static IReadOnlyList<ScryfallCard> BuildCompactCardPacket(IReadOnlyList<ScryfallCard> cards)
    {
        return cards
            .Select(card => new { Card = card, Score = ScoreSetCard(card) })
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => ParseManaValue(entry.Card.ManaCost))
            .ThenBy(entry => entry.Card.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCardsPerSetPacket)
            .Select(entry => entry.Card)
            .ToList();
    }

    private static int ScoreSetCard(ScryfallCard card)
    {
        var typeLine = card.TypeLine ?? string.Empty;
        var oracleText = NormalizeOracleText(card);
        var score = 0;

        if (typeLine.Contains("Basic Land", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (typeLine.Contains("Land", StringComparison.OrdinalIgnoreCase) && !HasHighSignalLandText(oracleText))
        {
            return 0;
        }

        if (typeLine.Contains("Creature", StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        if (typeLine.Contains("Instant", StringComparison.OrdinalIgnoreCase)
            || typeLine.Contains("Sorcery", StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }

        if (typeLine.Contains("Artifact", StringComparison.OrdinalIgnoreCase)
            || typeLine.Contains("Enchantment", StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        if (typeLine.Contains("Legendary", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        score += ScoreTextSignals(oracleText);

        var manaValue = ParseManaValue(card.ManaCost);
        if (manaValue <= 2)
        {
            score += 3;
        }
        else if (manaValue <= 4)
        {
            score += 1;
        }
        else if (manaValue >= 7)
        {
            score -= 1;
        }

        return score;
    }

    private static int ScoreTextSignals(string oracleText)
    {
        var score = 0;

        string[] graveyardSignals =
        [
            "graveyard",
            "mill",
            "discard",
            "sacrifice",
            "dies",
            "died",
            "return target",
            "from your graveyard",
            "flashback",
            "escape",
            "surveil",
            "reanimate"
        ];
        string[] pressureSignals =
        [
            "create",
            "token",
            "attack",
            "combat",
            "haste",
            "trample",
            "double strike",
            "deals",
            "damage"
        ];
        string[] selectionSignals =
        [
            "draw",
            "search your library",
            "look at the top",
            "scry",
            "surveil"
        ];
        string[] interactionSignals =
        [
            "destroy target",
            "exile target",
            "fight",
            "counter target",
            "target player sacrifices"
        ];

        if (graveyardSignals.Any(signal => oracleText.Contains(signal, StringComparison.OrdinalIgnoreCase)))
        {
            score += 6;
        }

        if (pressureSignals.Any(signal => oracleText.Contains(signal, StringComparison.OrdinalIgnoreCase)))
        {
            score += 4;
        }

        if (selectionSignals.Any(signal => oracleText.Contains(signal, StringComparison.OrdinalIgnoreCase)))
        {
            score += 3;
        }

        if (interactionSignals.Any(signal => oracleText.Contains(signal, StringComparison.OrdinalIgnoreCase)))
        {
            score += 2;
        }

        if (oracleText.Contains("+1/+1 counter", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        return score;
    }

    private static bool HasHighSignalLandText(string oracleText)
    {
        if (string.IsNullOrWhiteSpace(oracleText))
        {
            return false;
        }

        string[] highSignalLandPhrases =
        [
            "graveyard",
            "draw",
            "create",
            "token",
            "sacrifice",
            "attacks",
            "combat",
            "return target",
            "cast",
            "copy",
            "destroy target",
            "exile target"
        ];

        return highSignalLandPhrases.Any(phrase => oracleText.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlayableInCommanderIdentity(ScryfallCard card, IReadOnlySet<string> commanderIdentity)
    {
        var cardIdentity = (card.ColorIdentity ?? Array.Empty<string>())
            .Where(color => !string.IsNullOrWhiteSpace(color))
            .Select(color => color.Trim().ToUpperInvariant());

        foreach (var color in cardIdentity)
        {
            if (!commanderIdentity.Contains(color))
            {
                return false;
            }
        }

        return true;
    }

    private static DateOnly ParseReleasedAt(string? releasedAt)
        => DateOnly.TryParse(releasedAt, out var date) ? date : DateOnly.MinValue;

    private static int ParseCollectorNumber(string? collectorNumber)
    {
        if (string.IsNullOrWhiteSpace(collectorNumber))
        {
            return int.MaxValue;
        }

        var digits = new string(collectorNumber.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : int.MaxValue;
    }

    private static int ParseManaValue(string? manaCost)
    {
        if (string.IsNullOrWhiteSpace(manaCost))
        {
            return int.MaxValue;
        }

        var total = 0;
        foreach (Match match in Regex.Matches(manaCost, @"\{([^}]+)\}"))
        {
            var symbol = match.Groups[1].Value;
            if (int.TryParse(symbol, out var genericValue))
            {
                total += genericValue;
                continue;
            }

            total += 1;
        }

        return total == 0 ? int.MaxValue : total;
    }

    [GeneratedRegex(@"^(?<term>[A-Za-z][A-Za-z' -]{1,40})\s+—\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AbilityWordPattern();
}
