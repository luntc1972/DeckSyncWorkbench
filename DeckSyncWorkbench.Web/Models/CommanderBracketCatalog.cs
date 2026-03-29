namespace DeckSyncWorkbench.Web.Models;

public sealed record CommanderBracketOption(
    string Value,
    string Label,
    string Summary,
    string TurnsExpectation);

public static class CommanderBracketCatalog
{
    public static IReadOnlyList<CommanderBracketOption> Options { get; } =
    [
        new(
            "Exhibition",
            "Bracket 1: Exhibition",
            "Prioritize theme, unusual ideas, flexible legality, and showcase gameplay over optimization.",
            "Expect to play at least nine turns before you win or lose."),
        new(
            "Core",
            "Bracket 2: Core",
            "Unoptimized and straightforward decks with incremental, disruptable wins and low-pressure gameplay.",
            "Expect to play at least eight turns before you win or lose."),
        new(
            "Upgraded",
            "Bracket 3: Upgraded",
            "Strong synergy, high card quality, meaningful interaction, and explosive but earned wins.",
            "Expect to play at least six turns before you win or lose."),
        new(
            "Optimized",
            "Bracket 4: Optimized",
            "Fast, lethal, efficient decks with strong game changers, fast mana, tutors, and explosive play.",
            "Expect to play at least four turns before you win or lose."),
        new(
            "cEDH",
            "Bracket 5: cEDH",
            "Metagame-tuned competitive Commander decks built for maximum efficiency and consistency.",
            "Games can end on any turn.")
    ];

    public static CommanderBracketOption? Find(string? value)
    {
        return Options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase));
    }
}
