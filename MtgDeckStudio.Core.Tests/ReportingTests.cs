using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Knowledge;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Reporting;
using MtgDeckStudio.Core.Parsing;

namespace MtgDeckStudio.Core.Tests;

public sealed class ReportingTests
{
    [Fact]
    public void Reporter_AppendsInstructionsAndSwapChecklist()
    {
        var diff = new DeckDiff(
            new List<DeckEntry>
            {
                new() { Name = "Destiny Spinner", NormalizedName = "destiny spinner", Quantity = 1, Board = "mainboard" },
            },
            new List<DeckEntry>(),
            new List<DeckEntry>(),
            new List<PrintingConflict>
            {
                new()
                {
                    CardName = "Birds of Paradise",
                    ArchidektVersion = new DeckEntry { Name = "Birds of Paradise", NormalizedName = "birds of paradise", Quantity = 1, Board = "mainboard", SetCode = "cn2", CollectorNumber = "176", Category = "Ramp" },
                    MoxfieldVersion = new DeckEntry { Name = "Birds of Paradise", NormalizedName = "birds of paradise", Quantity = 1, Board = "mainboard", SetCode = "7ED", CollectorNumber = "231" },
                    Resolution = PrintingChoice.UseMoxfield,
                },
            });

        var reportText = ReconciliationReporter.ToText(diff);
        var checklist = ReconciliationReporter.GenerateSwapChecklist(diff.PrintingConflicts.ToList());

        Assert.Contains("=== How to fix missing or broken categories in Archidekt ===", reportText);
        Assert.Contains("Birds of Paradise", checklist);
        Assert.Contains("Swap to:  (7ED) 231", checklist);
    }

    [Fact]
    public void Reporter_CanLabelArchidektAsSourceAndMoxfieldAsTarget()
    {
        var diff = new DeckDiff(
            new List<DeckEntry> { new() { Name = "Arcane Signet", NormalizedName = "arcane signet", Quantity = 1, Board = "mainboard" } },
            new List<DeckEntry>(),
            new List<DeckEntry>(),
            new List<PrintingConflict>());

        var reportText = ReconciliationReporter.ToText(diff, "Archidekt", "Moxfield");

        Assert.Contains("=== Only in Moxfield", reportText);
        Assert.Contains("=== How to import into Moxfield safely ===", reportText);
    }

    [Fact]
    public void CategoryCountReporter_SumsQuantitiesAcrossCategories()
    {
        var text = CategoryCountReporter.ToText(new List<DeckEntry>
        {
            new() { Name = "Guardian Project", NormalizedName = "guardian project", Quantity = 1, Board = "mainboard", Category = "Draw" },
            new() { Name = "Birds of Paradise", NormalizedName = "birds of paradise", Quantity = 2, Board = "mainboard", Category = "Ramp" },
            new() { Name = "Gruul War Chant", NormalizedName = "gruul war chant", Quantity = 1, Board = "mainboard", Category = "Anthem,Evasion" },
        });

        Assert.Contains("Ramp: 2", text);
        Assert.Contains("Draw: 1", text);
        Assert.Contains("Anthem: 1", text);
        Assert.Contains("Evasion: 1", text);
    }

    [Fact]
    public void CategoryCardReporter_ListsCardsForCategory()
    {
        var text = CategoryCardReporter.ToText(new List<DeckEntry>
        {
            new() { Name = "Birds of Paradise", NormalizedName = "birds of paradise", Quantity = 2, Board = "mainboard", Category = "Ramp" },
            new() { Name = "Guardian Project", NormalizedName = "guardian project", Quantity = 1, Board = "mainboard", Category = "Draw" },
            new() { Name = "Gruul War Chant", NormalizedName = "gruul war chant", Quantity = 1, Board = "mainboard", Category = "Anthem,Evasion" },
        }, "Ramp");

        Assert.Contains("2 Birds of Paradise", text);
        Assert.DoesNotContain("Guardian Project", text);
    }

    [Fact]
    public void CategorySuggestionReporter_UsesExactCardMatchCategories()
    {
        var suggestions = CategorySuggestionReporter.SuggestCategories(new List<DeckEntry>
        {
            new() { Name = "Guardian Project", NormalizedName = "guardian project", Quantity = 1, Board = "mainboard", Category = "Draw,Enchantment" },
            new() { Name = "Guardian Project", NormalizedName = "guardian project", Quantity = 1, Board = "mainboard", Category = "Engine,Draw" },
            new() { Name = "Birds of Paradise", NormalizedName = "birds of paradise", Quantity = 1, Board = "mainboard", Category = "Ramp" },
        }, "Guardian Project");

        Assert.Equal(["Draw", "Engine"], suggestions);
    }

    [Fact]
    public void CategorySuggestionReporter_ExcludesTopLevelTypeCategories()
    {
        var suggestions = CategorySuggestionReporter.SuggestCategories(new List<DeckEntry>
        {
            new() { Name = "Swords to Plowshares", NormalizedName = "swords to plowshares", Quantity = 1, Board = "mainboard", Category = "Removal,Instant" },
            new() { Name = "Swords to Plowshares", NormalizedName = "swords to plowshares", Quantity = 1, Board = "mainboard", Category = "Protection" },
        }, "Swords to Plowshares");

        Assert.Equal(["Protection", "Removal"], suggestions);
    }

    [Fact]
    public void CategoryKnowledgeReporter_GroupsCardsWithinCategories()
    {
        var text = CategoryKnowledgeReporter.ToText(new List<DeckEntry>
        {
            new() { Name = "Birds of Paradise", NormalizedName = "birds of paradise", Quantity = 2, Board = "mainboard", Category = "Ramp" },
            new() { Name = "Birds of Paradise", NormalizedName = "birds of paradise", Quantity = 1, Board = "mainboard", Category = "Ramp" },
            new() { Name = "Guardian Project", NormalizedName = "guardian project", Quantity = 1, Board = "mainboard", Category = "Draw,Enchantment" },
            new() { Name = "Gruul War Chant", NormalizedName = "gruul war chant", Quantity = 1, Board = "mainboard", Category = "Anthem,Evasion,Artifact" },
        }, 3);

        Assert.Contains("Harvested 3 Archidekt decks.", text);
        Assert.Contains("[Ramp]", text);
        Assert.Contains("3 Birds of Paradise", text);
        Assert.Contains("[Draw]", text);
        Assert.Contains("1 Guardian Project", text);
        Assert.DoesNotContain("[Artifact]", text);
        Assert.DoesNotContain("[Enchantment]", text);
    }

    [Fact]
    public void CategoryInferenceReporter_FindsCategoriesFromKnowledgeFile()
    {
        var knowledge = """
            Harvested 2 Archidekt decks.

            [Draw]
            1 Guardian Project

            [Ramp]
            2 Birds of Paradise
            1 Guardian Project
            """;

        var inferred = CategoryInferenceReporter.InferCategoriesFromKnowledge(knowledge, "Guardian Project");

        Assert.Equal(["Draw", "Ramp"], inferred);
    }
}
