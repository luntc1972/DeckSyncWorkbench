using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Normalization;
using MtgDeckStudio.Core.Parsing;

namespace MtgDeckStudio.Core.Tests;

public sealed class ParserTests
{
    [Fact]
    public void Normalize_HandlesCaseSpacesAndMdfc()
    {
        var normalized = CardNormalizer.Normalize(" Bridgeworks Battle // Tanglespan Bridgeworks ");
        Assert.Equal("bridgeworks battle", normalized);
    }

    [Fact]
    public void Normalize_IgnoresCommaDifferences()
    {
        Assert.Equal(
            CardNormalizer.Normalize("Bello, Bard of the Brambles"),
            CardNormalizer.Normalize("Bello Bard of the Brambles"));
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
    public void MoxfieldParser_AllowsImplicitQuantityOfOne()
    {
        var entries = new MoxfieldParser().ParseText("""
            Bello, Bard of the Brambles (BLC) 1
            1 Arcane Signet
            """);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Bello, Bard of the Brambles", entries[0].Name);
        Assert.Equal(1, entries[0].Quantity);
        Assert.Equal("BLC", entries[0].SetCode);
        Assert.Equal("1", entries[0].CollectorNumber);
    }

    [Fact]
    public void MoxfieldParser_IgnoresPossibleNamesAndTrailingNotes()
    {
        var entries = new MoxfieldParser().ParseText("""
            1 Bello, Bard of the Brambles
            1 Arcane Signet
            Possible names:
            The Fire You Saved
            """);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Bello, Bard of the Brambles", entries[0].Name);
        Assert.Equal("Arcane Signet", entries[1].Name);
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
    public void ArchidektParser_AllowsCommanderLineWithoutLeadingQuantity()
    {
        var entries = new ArchidektParser().ParseText("""
            Edgin, Larcenous Lutenist (SLD) 1242 #Commander #ExileEngine
            Deck
            1 Arcane Signet (LCC) 299 #Ramp
            """);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Edgin, Larcenous Lutenist", entries[0].Name);
        Assert.Equal(1, entries[0].Quantity);
        Assert.Equal("commander", entries[0].Board);
        Assert.Equal("ExileEngine", entries[0].Category);
        Assert.Equal("SLD", entries[0].SetCode);
        Assert.Equal("1242", entries[0].CollectorNumber);
    }

    [Fact]
    public void ArchidektParser_IgnoresPossibleNamesAndTrailingNotes()
    {
        var entries = new ArchidektParser().ParseText("""
            Edgin, Larcenous Lutenist (SLD) 1242 #Commander #ExileEngine
            Deck
            1 Arcane Signet (LCC) 299 #Ramp
            1 Goblin Bombardment (WOT) 43 #TokenConversion

            Possible names
            Anytime, Anywhere, All at Once
            The Fire You Saved
            """);

        Assert.Equal(3, entries.Count);
        Assert.Equal("Edgin, Larcenous Lutenist", entries[0].Name);
        Assert.Equal("Arcane Signet", entries[1].Name);
        Assert.Equal("Goblin Bombardment", entries[2].Name);
    }
}
