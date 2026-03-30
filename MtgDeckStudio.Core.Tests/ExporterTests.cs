using MtgDeckStudio.Core.Exporting;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Parsing;

namespace MtgDeckStudio.Core.Tests;

/// <summary>
/// Validates exporter behavior across category and printing scenarios.
/// </summary>
public sealed class ExporterTests
{
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

    /// <summary>
    /// Ensures source tags are favored when that sync mode is selected.
    /// </summary>
    [Fact]
    public void FullImportExporter_FavorsSourceTagsWhenRequested()
    {
        var source = new List<DeckEntry>
        {
            new() { Name = "Birds of Paradise", NormalizedName = "birds of paradise", Quantity = 1, Board = "mainboard", Category = "Ramp" },
        };
        var target = new List<DeckEntry>
        {
            new() { Name = "Birds of Paradise", NormalizedName = "birds of paradise", Quantity = 1, Board = "mainboard", Category = "Draw" },
        };

        var text = FullImportExporter.ToText(source, target, MatchMode.Loose, "Archidekt", categoryMode: CategorySyncMode.SourceTags);

        Assert.Contains("[Ramp]", text);
        Assert.DoesNotContain("[Draw]", text);
    }

    [Fact]
    public void FullImportExporter_NormalizesMoxfieldTagsWhenUsingSourceTags()
    {
        var source = new List<DeckEntry>
        {
            new()
            {
                Name = "Bello, Bard of the Brambles",
                NormalizedName = "bello bard of the brambles",
                Quantity = 1,
                Board = "commander",
                SetCode = "blc",
                CollectorNumber = "1",
                Category = "Commander{top},Ramp",
            },
        };
        var target = new List<DeckEntry>
        {
            new()
            {
                Name = "Bello, Bard of the Brambles",
                NormalizedName = "bello bard of the brambles",
                Quantity = 1,
                Board = "commander",
                SetCode = "blc",
                CollectorNumber = "1",
                Category = "Draw",
            },
        };

        var text = FullImportExporter.ToText(source, target, MatchMode.Loose, "Archidekt", categoryMode: CategorySyncMode.SourceTags);

        Assert.Contains("[Commander,Ramp]", text);
        Assert.DoesNotContain("{top}", text);
    }

    /// <summary>
    /// Combines categories from both sides without duplicating entries.
    /// </summary>
    [Fact]
    public void FullImportExporter_CombinesCategoriesWithoutDuplicates()
    {
        var source = new List<DeckEntry>
        {
            new() { Name = "Birds of Paradise", NormalizedName = "birds of paradise", Quantity = 1, Board = "mainboard", Category = "Ramp" },
        };
        var target = new List<DeckEntry>
        {
            new() { Name = "Birds of Paradise", NormalizedName = "birds of paradise", Quantity = 1, Board = "mainboard", Category = "Draw" },
        };

        var text = FullImportExporter.ToText(source, target, MatchMode.Loose, "Archidekt", categoryMode: CategorySyncMode.Combined);

        Assert.Contains("[Draw,Ramp]", text);
    }
}
