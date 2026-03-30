using System.Text;
using System.Text.RegularExpressions;
using MtgDeckStudio.Web.Models;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;

namespace MtgDeckStudio.Web.Services;

public interface IScryfallSetService
{
    Task<IReadOnlyList<ScryfallSetOption>> GetSetsAsync(CancellationToken cancellationToken = default);

    Task<string> BuildSetPacketAsync(IReadOnlyList<string> setCodes, CancellationToken cancellationToken = default);
}

public sealed partial class ScryfallSetService : IScryfallSetService
{
    private const string SetCacheKey = "scryfall-set-options";
    private static readonly TimeSpan SetCacheDuration = TimeSpan.FromHours(6);
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
        _executeSetListAsync = executeSetListAsync ?? ((request, cancellationToken) => client.ExecuteAsync<ScryfallSetListResponse>(request, cancellationToken));
        _executeSearchAsync = executeSearchAsync ?? ((request, cancellationToken) => client.ExecuteAsync<ScryfallSearchResponse>(request, cancellationToken));
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
                $"Scryfall search returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var sets = response.Data.Data
            .Where(set => set.CardCount > 0)
            .Where(set => !string.Equals(set.SetType, "token", StringComparison.OrdinalIgnoreCase))
            .Where(set => !string.Equals(set.SetType, "minigame", StringComparison.OrdinalIgnoreCase))
            .Where(set => !string.Equals(set.SetType, "memorabilia", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(set => ParseReleasedAt(set.ReleasedAt))
            .ThenBy(set => set.Name, StringComparer.OrdinalIgnoreCase)
            .Select(set => new ScryfallSetOption(set.Code, set.Name, set.ReleasedAt, set.SetType, set.CardCount))
            .ToList();

        _cache.Set(SetCacheKey, sets, SetCacheDuration);
        return sets;
    }

    public async Task<string> BuildSetPacketAsync(IReadOnlyList<string> setCodes, CancellationToken cancellationToken = default)
    {
        var normalizedCodes = setCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedCodes.Count == 0)
        {
            return string.Empty;
        }

        var knownSets = await GetSetsAsync(cancellationToken).ConfigureAwait(false);
        var cardsBySet = new List<(ScryfallSetOption Set, IReadOnlyList<ScryfallCard> Cards)>();
        var mechanicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var setCode in normalizedCodes)
        {
            var set = knownSets.FirstOrDefault(option => string.Equals(option.Code, setCode, StringComparison.OrdinalIgnoreCase))
                ?? new ScryfallSetOption(setCode, setCode.ToUpperInvariant(), null, null, 0);
            var cards = await FetchCardsForSetAsync(setCode, cancellationToken).ConfigureAwait(false);
            cardsBySet.Add((set, cards));

            foreach (var card in cards)
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
                    $"Scryfall search returned HTTP {(int)response.StatusCode}.",
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
        foreach (var mechanicName in mechanicNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
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

    [GeneratedRegex(@"^(?<term>[A-Za-z][A-Za-z' -]{1,40})\s+—\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AbilityWordPattern();
}
