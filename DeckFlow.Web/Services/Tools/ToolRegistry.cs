using DeckFlow.Web.Models;

namespace DeckFlow.Web.Services.Tools;

/// <summary>
/// Default in-memory implementation of <see cref="IToolRegistry"/>.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private static readonly IReadOnlyList<ToolDefinition> Definitions =
    [
        Create("deck-analysis", "Deck Analysis", "/deck-analysis", ToolNavSection.Analyze, "tool.deck-analysis.enabled", true, "Deck Analysis", "Five-step workflow to generate a full analysis prompt for your deck and render the structured response.", "deck-analysis", DeckPageTab.DeckAnalysis, false, "/set-upgrade-analysis"),
        Create("manabase", "Mana Base", "/manabase", ToolNavSection.Analyze, "tool.manabase.enabled", false, "Commander Mana Base Analyzer", "Score a deck's lands and colored sources — Frank Karsten's source-count math, extended to weight rocks, dorks, and MDFCs and to count tapped vs. untapped lands. No AI needed.", "manabase", DeckPageTab.Manabase, true),
        Create("deck-comparison", "Deck Comparison", "/deck-comparison", ToolNavSection.Analyze, "tool.deck-comparison.enabled", true, "Deck Comparison", "Side-by-side comparison of two decks with an AI-authored breakdown of strengths, weaknesses, and trade-offs.", "deck-comparison", DeckPageTab.DeckComparison, false),
        Create("cedh-meta-gap", "cEDH Meta Gap", "/cedh-meta-gap", ToolNavSection.Analyze, "tool.cedh-meta-gap.enabled", true, "cEDH Meta Gap", "Measure your cEDH deck against top meta decks and surface the cards, lines, and roles you're missing.", "cedh-meta-gap", DeckPageTab.CedhMetaGap, false),
        Create("bracket", "Bracket Check", "/bracket", ToolNavSection.Analyze, "tool.bracket.enabled", false, "Commander Bracket Checker", "Classify a Commander deck into its official 1–5 bracket using Game Changers, two-card combos, and mass land denial — computed locally, no AI needed.", "bracket", DeckPageTab.Bracket, false),
        Create("deck-modules", "Deck Modules", "/deck-modules", ToolNavSection.Analyze, "tool.deck-modules.enabled", false, "Deck Modules", "Import one baseline deck, manually assign 2-4 named strategy alternatives with linked mana support, and compile a complete 100-card configuration with an exact swap and reset checklist — no saved projects.", null, DeckPageTab.DeckModules, false, "/deck-modules/import", "/deck-modules/compile", "/deck-modules/export"),
        Create("deck-history", "Deck History", "/deck-history", ToolNavSection.Build, "tool.deck-history.enabled", false, "Deck Version Tracker", "Track your deck's evolution in a file you own — snapshot each change with a note, diff any two versions, and generate an AI prompt about how the deck has grown.", "deck-history", DeckPageTab.DeckHistory, true, "/deck-history/download"),
        Create("cut-lab", "Cut Lab", "/cut-lab", ToolNavSection.Build, "tool.cut-lab.enabled", false, "Cut Lab", "Bring an oversized 101–150 card Commander pool into a workspace, declare your build intent, and lock the cards, packages, and roles that must never be cut — before any trimming begins.", "cut-lab", DeckPageTab.CutLab, false),
        Create("deck-primer", "Deck Primer", "/deck-primer", ToolNavSection.Build, "tool.deck-primer.enabled", false, "Deck Primer", "Build a staged primer for your deck's plan, lines, and key interactions — one-paste AI prompt.", "deck-primer", DeckPageTab.DeckPrimer, false),
        Create("deck-sync", "Deck Sync", "/sync", ToolNavSection.Build, "tool.deck-sync.enabled", false, "Moxfield–Archidekt Deck Sync", "Reconcile a Moxfield deck against an Archidekt deck (either direction) and generate add/cut text for the target.", "deck-sync", DeckPageTab.Sync, false, "/resolve", "/api/deck/diff"),
        Create("convert", "Convert Deck", "/convert", ToolNavSection.Build, "tool.convert.enabled", false, "MTG Decklist Converter", "Convert deck export text or a public URL between Moxfield and Archidekt formats.", "convert", DeckPageTab.Convert, false),
        Create("content-kb", "Knowledge Base", "/content-kb", ToolNavSection.Reference, "tool.knowledge-base.enabled", false, "Expert Knowledge Base", "Browse distilled creator advice, open any entry, and copy a ready-to-paste prompt from the Knowledge Base detail page.", "content-kb", DeckPageTab.ContentKb, true),
        Create("card-lookup", "Card Lookup", "/card-lookup", ToolNavSection.Reference, "tool.card-lookup.enabled", false, "Card Lookup", "Paste a card list and get back Oracle text and rulings for each match.", "card-lookup", DeckPageTab.CardLookup, false),
        Create("mechanic-lookup", "Mechanic Rules", "/mechanic-lookup", ToolNavSection.Reference, "tool.mechanic-lookup.enabled", false, "Mechanic Rules", "Look up official WOTC rules text for keyword mechanics found in your deck.", "mechanic-lookup", DeckPageTab.MechanicLookup, false, "/api/suggestions/mechanic"),
        Create("judge-questions", "Ask a Judge", "/judge-questions", ToolNavSection.Reference, "tool.judge-questions.enabled", false, "Ask a Judge", "Get rules answers from real MTG judges 24/7, with a ChatGPT prompt generator as backup.", "ask-a-judge", DeckPageTab.JudgeQuestions, false),
        Create("suggest-categories", "Category Suggestions", "/suggest-categories", ToolNavSection.Categories, "tool.categories.enabled", false, "Commander Deck Tag Suggestions", "Suggest categories for your cards using cached data, Scryfall Tagger, or a reference-deck comparison.", "category-suggestions", DeckPageTab.SuggestCategories, false, "/api/suggestions/card"),
        Create("commander-categories", "Category Reference", "/commander-categories", ToolNavSection.Categories, "tool.commander-categories.enabled", false, "Commander Category Reference", "Browse the category knowledge base the suggestion engine draws on.", "commander-categories", DeckPageTab.CommanderCategories, false, "/api/suggestions/commander"),
    ];

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> All => Definitions;

    private static ToolDefinition Create(
        string key,
        string label,
        string route,
        ToolNavSection section,
        string flagKey,
        bool core,
        string tileTitle,
        string tileDescription,
        string? helpSlug,
        DeckPageTab tab,
        bool isPrimaryTile,
        params string[] additionalRoutes)
    {
        return new ToolDefinition
        {
            Key = key,
            Label = label,
            Route = route,
            AdditionalRoutes = additionalRoutes,
            Section = section,
            FlagKey = flagKey,
            Core = core,
            TileTitle = tileTitle,
            TileDescription = tileDescription,
            HelpSlug = helpSlug,
            Tab = tab,
            IsPrimaryTile = isPrimaryTile,
            IconKey = key,
        };
    }
}
