namespace MtgDeckStudio.Web.Models;

public sealed record ScryfallSetOption(
    string Code,
    string Name,
    string? ReleasedAt,
    string? SetType,
    int CardCount)
{
    public string DisplayLabel
        => string.IsNullOrWhiteSpace(ReleasedAt)
            ? $"{Name} ({Code.ToUpperInvariant()})"
            : $"{Name} ({Code.ToUpperInvariant()}) - {ReleasedAt}";
}
