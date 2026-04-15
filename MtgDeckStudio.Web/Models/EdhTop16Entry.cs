namespace MtgDeckStudio.Web.Models;

public sealed class EdhTop16Entry
{
    public int Standing { get; init; }

    public int Wins { get; init; }

    public int Losses { get; init; }

    public int Draws { get; init; }

    public string DecklistUrl { get; init; } = string.Empty;

    public string PlayerName { get; init; } = string.Empty;

    public string TournamentName { get; init; } = string.Empty;

    public string TournamentId { get; init; } = string.Empty;

    public DateOnly? TournamentDate { get; init; }

    public int TournamentSize { get; init; }

    public IReadOnlyList<EdhTop16Card> MainDeck { get; init; } = Array.Empty<EdhTop16Card>();

    public double WinRate =>
        Wins + Losses + Draws == 0
            ? 0
            : (Wins + (Draws * 0.5d)) / (Wins + Losses + Draws);
}

public sealed class EdhTop16Card
{
    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;
}
