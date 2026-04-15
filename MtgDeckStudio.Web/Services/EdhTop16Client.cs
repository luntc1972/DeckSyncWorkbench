using System.Text.Json;
using RestSharp;
using MtgDeckStudio.Web.Models;

namespace MtgDeckStudio.Web.Services;

public interface IEdhTop16Client
{
    Task<IReadOnlyList<EdhTop16Entry>> SearchCommanderEntriesAsync(
        string commanderName,
        CedhMetaTimePeriod timePeriod,
        CedhMetaSortBy sortBy,
        int minEventSize,
        int? maxStanding,
        int count,
        CancellationToken cancellationToken = default);
}

public sealed class EdhTop16Client : IEdhTop16Client
{
    private const string Endpoint = "https://edhtop16.com/api/graphql";
    private const string CommanderEntriesQuery = """
        query($name:String!,$first:Int!,$sortBy:EntriesSortBy!,$timePeriod:TimePeriod!,$minEventSize:Int!,$maxStanding:Int){
          commander(name:$name){
            name
            colorId
            entries(first:$first,sortBy:$sortBy,filters:{timePeriod:$timePeriod,minEventSize:$minEventSize,maxStanding:$maxStanding}){
              edges{
                node{
                  standing
                  wins
                  losses
                  draws
                  decklist
                  player{name}
                  tournament{name tournamentDate size TID}
                  maindeck{name type}
                }
              }
            }
          }
        }
        """;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<RestRequest, CancellationToken, Task<RestResponse>> _executeAsync;

    public EdhTop16Client(RestClient? restClient = null, Func<RestRequest, CancellationToken, Task<RestResponse>>? executeAsync = null)
    {
        var client = restClient ?? new RestClient(new RestClientOptions(Endpoint));
        _executeAsync = executeAsync ?? ((request, cancellationToken) => client.ExecuteAsync(request, cancellationToken));
    }

    public async Task<IReadOnlyList<EdhTop16Entry>> SearchCommanderEntriesAsync(
        string commanderName,
        CedhMetaTimePeriod timePeriod,
        CedhMetaSortBy sortBy,
        int minEventSize,
        int? maxStanding,
        int count,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commanderName))
        {
            throw new InvalidOperationException("A commander name is required before querying EDH Top 16.");
        }

        if (count < 1)
        {
            throw new InvalidOperationException("At least one EDH Top 16 entry must be requested.");
        }

        var trimmedCommanderName = commanderName.Trim();

        var request = new RestRequest(string.Empty, Method.Post);
        request.AddHeader("Content-Type", "application/json");
        request.AddJsonBody(new
        {
            query = CommanderEntriesQuery,
            variables = new
            {
                name = trimmedCommanderName,
                first = count,
                sortBy = sortBy.ToString(),
                timePeriod = timePeriod.ToString(),
                minEventSize,
                maxStanding
            }
        });

        var response = await _executeAsync(request, cancellationToken).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;
        if (statusCode is < 200 or >= 300 || string.IsNullOrWhiteSpace(response.Content))
        {
            throw new HttpRequestException(
                $"EDH Top 16 request failed with HTTP {statusCode}.",
                null,
                response.StatusCode);
        }

        var payload = JsonSerializer.Deserialize<EdhTop16GraphQlResponse>(response.Content, JsonOptions)
            ?? throw new InvalidOperationException("EDH Top 16 returned an unreadable response payload.");

        if (payload.Errors.Count > 0)
        {
            throw new InvalidOperationException(payload.Errors[0].Message);
        }

        if (payload.Data?.Commander is null)
        {
            throw new InvalidOperationException($"No EDH Top 16 commander record was found for {trimmedCommanderName}.");
        }

        return payload.Data.Commander.Entries?.Edges?
            .Select(edge => edge.Node)
            .OfType<EdhTop16EntryNode>()
            .Select(MapEntry)
            .ToList()
            ?? new List<EdhTop16Entry>();
    }

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParse(value, out var parsed) ? parsed : null;

    private static EdhTop16Entry MapEntry(EdhTop16EntryNode node)
        => new()
        {
            Standing = node.Standing,
            Wins = node.Wins,
            Losses = node.Losses,
            Draws = node.Draws,
            DecklistUrl = node.Decklist ?? string.Empty,
            PlayerName = node.Player?.Name ?? string.Empty,
            TournamentName = node.Tournament?.Name ?? string.Empty,
            TournamentId = node.Tournament?.TournamentId ?? string.Empty,
            TournamentDate = ParseDate(node.Tournament?.TournamentDate),
            TournamentSize = node.Tournament?.Size ?? 0,
            MainDeck = node.MainDeck
                .Where(card => !string.IsNullOrWhiteSpace(card.Name))
                .Select(MapCard)
                .ToList()
        };

    private static EdhTop16Card MapCard(EdhTop16CardNode card)
        => new()
        {
            Name = card.Name ?? string.Empty,
            Type = card.Type ?? string.Empty
        };

    private sealed class EdhTop16GraphQlResponse
    {
        public EdhTop16GraphQlData? Data { get; init; }

        public List<EdhTop16GraphQlError> Errors { get; init; } = new();
    }

    private sealed class EdhTop16GraphQlData
    {
        public EdhTop16CommanderNode? Commander { get; init; }
    }

    private sealed class EdhTop16GraphQlError
    {
        public string Message { get; init; } = string.Empty;
    }

    private sealed class EdhTop16CommanderNode
    {
        public EdhTop16EntryConnection? Entries { get; init; }
    }

    private sealed class EdhTop16EntryConnection
    {
        public List<EdhTop16EntryEdge> Edges { get; init; } = new();
    }

    private sealed class EdhTop16EntryEdge
    {
        public EdhTop16EntryNode? Node { get; init; }
    }

    private sealed class EdhTop16EntryNode
    {
        public int Standing { get; init; }

        public int Wins { get; init; }

        public int Losses { get; init; }

        public int Draws { get; init; }

        public string? Decklist { get; init; }

        public EdhTop16PlayerNode? Player { get; init; }

        public EdhTop16TournamentNode? Tournament { get; init; }

        public List<EdhTop16CardNode> MainDeck { get; init; } = new();
    }

    private sealed class EdhTop16PlayerNode
    {
        public string? Name { get; init; }
    }

    private sealed class EdhTop16TournamentNode
    {
        public string? Name { get; init; }

        public string? TournamentDate { get; init; }

        public int Size { get; init; }

        public string? TID { get; init; }

        public string TournamentId => TID ?? string.Empty;
    }

    private sealed class EdhTop16CardNode
    {
        public string? Name { get; init; }

        public string? Type { get; init; }
    }
}
