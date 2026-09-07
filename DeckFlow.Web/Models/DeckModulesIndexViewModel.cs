namespace DeckFlow.Web.Models;

/// <summary>
/// Represents the initial Deck Modules page state.
/// </summary>
public sealed class DeckModulesIndexViewModel
{
    /// <summary>
    /// Gets the active tab for the shared deck tool navigation.
    /// </summary>
    public DeckPageTab ActiveTab { get; init; } = DeckPageTab.DeckModules;
}
