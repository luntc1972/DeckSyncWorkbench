namespace DeckFlow.Web.Models;

/// <summary>
/// Identifies the currently-active tab in the Deck workflow strip rendered by _DeckToolTabs.cshtml.
/// </summary>
public enum DeckPageTab
{
    /// <summary>Deck sync (compare-and-update) page.</summary>
    Sync = 0,

    /// <summary>Category suggestion page.</summary>
    SuggestCategories = 1,

    /// <summary>Commander category knowledge page.</summary>
    CommanderCategories = 2,

    /// <summary>Card lookup page.</summary>
    CardLookup = 3,

    /// <summary>Mechanic lookup page.</summary>
    MechanicLookup = 4,

    /// <summary>Deck-analysis artifact generator page.</summary>
    DeckAnalysis = 5,

    /// <summary>Deck format conversion page.</summary>
    Convert = 7,

    /// <summary>Two-deck comparison artifact generator page.</summary>
    DeckComparison = 8,

    /// <summary>cEDH meta-gap finder artifact generator page.</summary>
    CedhMetaGap = 9,

    /// <summary>Home / landing page.</summary>
    Home = 10,

    /// <summary>Judge-questions artifact generator page.</summary>
    JudgeQuestions = 11,

    /// <summary>Content Knowledge Base browse/detail pages.</summary>
    ContentKb = 12,

    /// <summary>Deck-primer artifact generator page.</summary>
    DeckPrimer = 13,

    /// <summary>Mana-base analysis page.</summary>
    Manabase = 14,

    /// <summary>Bracket classifier and balancer page.</summary>
    Bracket = 15,

    /// <summary>Deck version-history tracking page.</summary>
    DeckHistory = 16,

    /// <summary>Cut Lab intake, protection, and cut-recommendation page.</summary>
    CutLab = 17,

    /// <summary>Deck Modules standalone import/compile/export page.</summary>
    DeckModules = 18,
}
