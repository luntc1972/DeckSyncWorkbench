using DeckSyncWorkbench.Core.Diffing;
using DeckSyncWorkbench.Core.Exporting;
using DeckSyncWorkbench.Core.Filtering;
using DeckSyncWorkbench.Core.Integration;
using DeckSyncWorkbench.Core.Models;
using DeckSyncWorkbench.Core.Normalization;
using DeckSyncWorkbench.Core.Parsing;
using DeckSyncWorkbench.Core.Reporting;

namespace DeckSyncWorkbench.Core.Tests;

public sealed class DiffEngineTests
{
    [Fact]
    public void Normalize_HandlesCaseSpacesAndMdfc()
    {
        var normalized = CardNormalizer.Normalize(" Bridgeworks Battle // Tanglespan Bridgeworks ");
        Assert.Equal("bridgeworks battle", normalized);
    }

    [Fact]
    public void MoxfieldApiUrl_ExtractsDeckIdFromPublicUrl()
    {
        var success = MoxfieldApiUrl.TryGetDeckId("https://moxfield.com/decks/fNC0NaQftkO8uWFMD8O49g", out var deckId);

        Assert.True(success);
        Assert.Equal("fNC0NaQftkO8uWFMD8O49g", deckId);
        Assert.Equal("https://api.moxfield.com/v2/decks/all/fNC0NaQftkO8uWFMD8O49g", MoxfieldApiUrl.BuildDeckApiUri(deckId).ToString());
    }

    [Fact]
    public void ArchidektApiUrl_ExtractsDeckIdFromPublicUrl()
    {
        var success = ArchidektApiUrl.TryGetDeckId("https://archidekt.com/decks/15918942/trashpanda", out var deckId);

        Assert.True(success);
        Assert.Equal("15918942", deckId);
        Assert.Equal("https://archidekt.com/api/decks/15918942/", ArchidektApiUrl.BuildDeckApiUri(deckId).ToString());
    }

    [Fact]
    public void Normalize_IgnoresCommaDifferences()
    {
        Assert.Equal(
            CardNormalizer.Normalize("Bello, Bard of the Brambles"),
            CardNormalizer.Normalize("Bello Bard of the Brambles"));
    }

    [Fact]
    public void MoxfieldParser_TracksBoardsAndFoilMarkers()
    {
        var entries = new MoxfieldParser().ParseText("""
            Commander
            1 Atraxa, Praetors' Voice (MH2) 17 *F*

            1 Arcane Signet
            Sideboard
            2 Snow-Covered Mountain
            """);

        Assert.Equal(2, entries.Count);
        Assert.Equal("commander", entries[0].Board);
        Assert.True(entries[0].IsFoil);
        Assert.Equal("mainboard", entries[1].Board);
    }

    [Fact]
    public void MoxfieldParser_IgnoresSideboardAndEverythingBelowIt()
    {
        var entries = new MoxfieldParser().ParseText("""
            1 Bello, Bard of the Brambles
            Sideboard
            1 Counterspell
            1 Negate
            """);

        var entry = Assert.Single(entries);
        Assert.Equal("mainboard", entry.Board);
        Assert.Equal("Bello, Bard of the Brambles", entry.Name);
    }

    [Fact]
    public void MoxfieldParser_IgnoresSideboardHeaderWithColon()
    {
        var entries = new MoxfieldParser().ParseText("""
            1 Bello, Bard of the Brambles
            SIDEBOARD:
            1 Counterspell
            """);

        var entry = Assert.Single(entries);
        Assert.Equal("Bello, Bard of the Brambles", entry.Name);
    }

    [Fact]
    public void ArchidektParser_ParsesCategoriesAndMaybeboard()
    {
        var entries = new ArchidektParser().ParseText("""
            1 Wandering Archaic (stx) 6 [Maybeboard{noDeck}{noPrice},Ramp]
            1 Aggravated Assault (wot) 39 [Finisher]
            """);

        Assert.Equal("maybeboard", entries[0].Board);
        Assert.Equal("Ramp", entries[0].Category);
        Assert.Equal("mainboard", entries[1].Board);
        Assert.Equal("Finisher", entries[1].Category);
    }

    [Fact]
    public void ArchidektParser_AllowsFoilMarkerBeforeCategories()
    {
        var entries = new ArchidektParser().ParseText("1 Guardian Project (pip) 727 *F* [Draw]");

        var entry = Assert.Single(entries);
        Assert.Equal("Guardian Project", entry.Name);
        Assert.Equal("pip", entry.SetCode);
        Assert.Equal("727", entry.CollectorNumber);
        Assert.Equal("Draw", entry.Category);
        Assert.True(entry.IsFoil);
    }

    [Fact]
    public void Compare_LooseMode_FindsPrintingConflictAndSkipsDelta()
    {
        var moxfield = new MoxfieldParser().ParseText("1 Giggling Skitterspike (DSC) 66");
        var archidekt = new ArchidektParser().ParseText("1 Giggling Skitterspike (dsc) 39 [Burn]");

        var diff = new DiffEngine(MatchMode.Loose).Compare(moxfield, archidekt);

        Assert.Empty(diff.ToAdd);
        Assert.Single(diff.PrintingConflicts);
        Assert.Equal("Burn", diff.PrintingConflicts[0].ArchidektVersion.Category);
    }

    [Fact]
    public void Compare_DoesNotAddCommanderWhenTargetParsedItAsMainboard()
    {
        var moxfield = new MoxfieldParser().ParseText("""
            Commander:
            1 Bello, Bard of the Brambles
            """);
        var archidekt = new ArchidektParser().ParseText("1 Bello Bard of the Brambles (blb) 1");

        var diff = new DiffEngine(MatchMode.Loose).Compare(moxfield, archidekt);

        Assert.Empty(diff.ToAdd);
        Assert.Empty(diff.OnlyInArchidekt);
    }

    [Fact]
    public void MoxfieldParser_KeepsMultipleCommanderLinesInCommanderSection()
    {
        var entries = new MoxfieldParser().ParseText("""
            Commander:
            1 Thrasios, Triton Hero
            1 Tymna the Weaver

            1 Birds of Paradise
            """);

        Assert.Equal("commander", entries[0].Board);
        Assert.Equal("commander", entries[1].Board);
        Assert.Equal("mainboard", entries[2].Board);
    }

    [Fact]
    public void Compare_StrictMode_UsesLooseFallbackForPrintingConflict()
    {
        var moxfield = new MoxfieldParser().ParseText("1 Birds of Paradise (7ED) 231 *F*");
        var archidekt = new ArchidektParser().ParseText("1 Birds of Paradise (cn2) 176 [Ramp]");

        var diff = new DiffEngine(MatchMode.Strict).Compare(moxfield, archidekt);

        Assert.Empty(diff.ToAdd);
        Assert.Single(diff.PrintingConflicts);
    }

    [Fact]
    public void Compare_SumsCategorySplitsBeforeComparing()
    {
        var moxfield = new MoxfieldParser().ParseText("3 Snow-Covered Mountain");
        var archidekt = new ArchidektParser().ParseText("""
            1 Snow-Covered Mountain (khm) 283 [Ramp]
            1 Snow-Covered Mountain (khm) 283 [Lands]
            """);

        var diff = new DiffEngine(MatchMode.Loose).Compare(moxfield, archidekt);

        var toAdd = Assert.Single(diff.ToAdd);
        Assert.Equal(1, toAdd.Quantity);
    }

    [Fact]
    public void DeltaExporter_OutputsBoardHeadersForNonMainboard()
    {
        var text = DeltaExporter.ToText(new List<DeckEntry>
        {
            new() { Name = "Atraxa, Praetors' Voice", NormalizedName = "atraxa, praetors' voice", Quantity = 1, Board = "commander" },
            new() { Name = "Counterspell", NormalizedName = "counterspell", Quantity = 2, Board = "sideboard" },
        });

        Assert.Contains("// Commander", text);
        Assert.Contains("// Sideboard", text);
    }

    [Fact]
    public void DeltaExporter_IncludesPrintingWhenPresent()
    {
        var text = DeltaExporter.ToText(new List<DeckEntry>
        {
            new() { Name = "Destiny Spinner", NormalizedName = "destiny spinner", Quantity = 1, Board = "mainboard", SetCode = "thb", CollectorNumber = "168" },
        });

        Assert.Contains("1 Destiny Spinner (thb) 168", text);
    }

    [Fact]
    public void DeltaExporter_UsesDoubleSlashForArchidektDoubleFacedCards()
    {
        var text = DeltaExporter.ToText(new List<DeckEntry>
        {
            new() { Name = "Bridgeworks Battle / Tanglespan Bridgeworks", NormalizedName = "bridgeworks battle", Quantity = 1, Board = "mainboard", SetCode = "mh3", CollectorNumber = "249" },
        }, "Archidekt");

        Assert.Contains("1 Bridgeworks Battle // Tanglespan Bridgeworks (mh3) 249", text);
    }

    [Fact]
    public void DeltaExporter_KeepsCommanderTagForArchidekt()
    {
        var text = DeltaExporter.ToText(new List<DeckEntry>
        {
            new() { Name = "Bello, Bard of the Brambles", NormalizedName = "bello bard of the brambles", Quantity = 1, Board = "commander", SetCode = "blc", CollectorNumber = "1" },
        }, "Archidekt");

        Assert.Contains("1 Bello, Bard of the Brambles (blc) 1 [Commander]", text);
    }

    [Fact]
    public void DeltaExporter_EmitsMoxfieldTagsWhenTargetIsMoxfield()
    {
        var text = DeltaExporter.ToText(new List<DeckEntry>
        {
            new() { Name = "Bello, Bard of the Brambles", NormalizedName = "bello bard of the brambles", Quantity = 1, Board = "commander", SetCode = "blc", CollectorNumber = "1" },
            new() { Name = "Guardian Project", NormalizedName = "guardian project", Quantity = 1, Board = "mainboard", SetCode = "rna", CollectorNumber = "130", Category = "Draw,Ramp" },
        }, "Moxfield");

        Assert.DoesNotContain("// Commander", text);
        Assert.Contains("1 Bello, Bard of the Brambles (blc) 1", text);
        Assert.Contains("1 Guardian Project (rna) 130 #Draw #Ramp", text);
    }

    [Fact]
    public void MoxfieldTextExporter_WritesCommanderAndTags()
    {
        var text = MoxfieldTextExporter.ToText(new List<DeckEntry>
        {
            new() { Name = "Bello, Bard of the Brambles", NormalizedName = "bello bard of the brambles", Quantity = 1, Board = "commander", SetCode = "blc", CollectorNumber = "1" },
            new() { Name = "Guardian Project", NormalizedName = "guardian project", Quantity = 1, Board = "mainboard", SetCode = "rna", CollectorNumber = "130", Category = "Draw,Ramp" },
        });

        Assert.DoesNotContain("Commander:", text);
        Assert.Contains("1 Bello, Bard of the Brambles (blc) 1", text);
        Assert.Contains("1 Guardian Project (rna) 130 #Draw #Ramp", text);
    }

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
    public void EdhrecCardLookup_SlugifiesNames()
    {
        Assert.Equal("wandering-archaic-explore-the-vastlands", EdhrecCardLookup.Slugify("Wandering Archaic // Explore the Vastlands"));
        Assert.Equal("bello-bard-of-the-brambles", EdhrecCardLookup.Slugify("Bello, Bard of the Brambles"));
    }

    [Fact]
    public void FullImportExporter_PreservesArchidektCategoriesForExistingCards()
    {
        var moxfield = new MoxfieldParser().ParseText("""
            Commander
            1 Bello, Bard of the Brambles

            1 Guardian Project (pip) 727
            Sideboard
            1 Counterspell
            """);
        var archidekt = new ArchidektParser().ParseText("""
            1 Bello Bard of the Brambles (blb) 1 [Commander]
            1 Guardian Project (pip) 727 [Draw]
            """);

        var text = FullImportExporter.ToText(moxfield, archidekt, MatchMode.Loose);

        Assert.Contains("1 Bello, Bard of the Brambles (blb) 1 [Commander]", text);
        Assert.Contains("1 Guardian Project (pip) 727 [Draw]", text);
        Assert.DoesNotContain("// Maybeboard", text);
        Assert.DoesNotContain("Counterspell", text);
    }

    [Fact]
    public void FullImportExporter_PreservesTargetCommanderBoardWhenSourceIsMainboard()
    {
        var source = new List<DeckEntry>
        {
            new() { Name = "Bello, Bard of the Brambles", NormalizedName = "bello bard of the brambles", Quantity = 1, Board = "mainboard", SetCode = "BLC", CollectorNumber = "1" },
            new() { Name = "Bello, Bard of the Brambles", NormalizedName = "bello bard of the brambles", Quantity = 1, Board = "commander", SetCode = "BLC", CollectorNumber = "1" },
        };
        var target = new ArchidektParser().ParseText("1 Bello, Bard of the Brambles (blc) 1 [Commander,Draw]");

        var text = FullImportExporter.ToText(source, target, MatchMode.Loose);

        Assert.Contains("// Commander", text);
        Assert.Contains("1 Bello, Bard of the Brambles (blc) 1 [Commander,Draw]", text);
        Assert.DoesNotContain("// Mainboard\n1 Bello, Bard of the Brambles", text);
        Assert.Equal(1, text.Split("Bello, Bard of the Brambles", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void FullImportExporter_OmitsCategoriesWhenTargetIsMoxfield()
    {
        var source = new ArchidektParser().ParseText("""
            1 Bello, Bard of the Brambles (blc) 1 [Commander,Draw]
            1 Guardian Project (pip) 727 [Draw]
            """);
        var target = new MoxfieldParser().ParseText("""
            Commander:
            1 Bello, Bard of the Brambles (BLC) 1

            1 Guardian Project (PIP) 727
            """);

        var text = FullImportExporter.ToText(source, target, MatchMode.Loose, "Moxfield");

        Assert.DoesNotContain("// Commander", text);
        Assert.Contains("1 Bello, Bard of the Brambles (BLC) 1 #Draw", text);
        Assert.DoesNotContain("#Commander", text);
        Assert.Contains("1 Guardian Project (PIP) 727", text);
        Assert.DoesNotContain("[Draw]", text);
        Assert.Contains("#Draw", text);
    }

    [Fact]
    public void DeckEntryFilter_ExcludesMaybeboardEntries()
    {
        var entries = new List<DeckEntry>
        {
            new() { Name = "Main Card", NormalizedName = "main card", Quantity = 1, Board = "mainboard" },
            new() { Name = "Maybe Card", NormalizedName = "maybe card", Quantity = 1, Board = "maybeboard", Category = "Maybeboard" },
        };

        var filtered = DeckEntryFilter.ExcludeMaybeboard(entries);

        var entry = Assert.Single(filtered);
        Assert.Equal("Main Card", entry.Name);
    }
}
