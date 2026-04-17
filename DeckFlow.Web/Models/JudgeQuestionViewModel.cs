namespace DeckFlow.Web.Models;

/// <summary>
/// Represents the state for the judge-questions page that links to the live MTG judge chat
/// and offers a secondary ChatGPT prompt generator.
/// </summary>
public sealed class JudgeQuestionViewModel
{
    /// <summary>
    /// Gets the active tab for shared deck tool navigation.
    /// </summary>
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.JudgeQuestions;

    /// <summary>
    /// Gets the optional card name pre-populated from a Card Lookup deep link.
    /// </summary>
    public string? PrefilledCardName { get; init; }
}
