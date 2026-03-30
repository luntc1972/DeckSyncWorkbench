using MtgDeckStudio.Core.Diffing;
using MtgDeckStudio.Core.Integration;
using MtgDeckStudio.Core.Models;
using MtgDeckStudio.Core.Parsing;

namespace MtgDeckStudio.Core.Tests;

public sealed class DiffEngineTests
{
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
}
