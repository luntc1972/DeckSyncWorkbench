using DeckFlow.Core.Manabase;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Manabase;
using DeckFlow.Web.Services.Modular;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// WR-03 regressions: <see cref="PacketSizeEstimator"/> must not silently under-count what
/// <see cref="PacketSessionCache"/> actually stores, or the 10 MB cache cap no longer bounds
/// memory on the 512 MB Render tier.
/// </summary>
public sealed class PacketSizeEstimatorTests
{
    [Fact]
    public void EstimateSizeBytes_ConfigurationAnalysisResult_CountsDeclaredDisclosureText()
    {
        var withoutDeclared = Analysis(declared: null);
        var withDeclared = Analysis(declared: new ConfigurationDeclaredDisclosure
        {
            Profile = "cEDH",
            PlayPlan = new string('x', 500),
            IsDeclared = true,
            ProfileDisagreementNote = new string('y', 300),
        });

        var baseline = PacketSizeEstimator.EstimateSizeBytes(withoutDeclared);
        var withDisclosure = PacketSizeEstimator.EstimateSizeBytes(withDeclared);

        // The declared block alone contributes ~800 characters that a stale estimator would have
        // dropped entirely.
        Assert.True(withDisclosure - baseline >= 800, $"Expected the Declared disclosure text to be counted; delta was {withDisclosure - baseline}.");
    }

    [Fact]
    public void EstimateSizeBytes_ManabaseHandoffPayload_GrowsWithAnalyzedSpellCount()
    {
        var fewSpells = Handoff(spellCount: 1);
        var manySpells = Handoff(spellCount: 99);

        var small = PacketSizeEstimator.EstimateSizeBytes(fewSpells);
        var large = PacketSizeEstimator.EstimateSizeBytes(manySpells);

        // A hand-summed estimator that never walked AnalyzedSpells reported an identical size
        // regardless of how many spells the analyzed 99 contained.
        Assert.True(large > small, $"Expected the size estimate to grow with AnalyzedSpells count; small={small}, large={large}.");
    }

    private static ConfigurationAnalysisResult Analysis(ConfigurationDeclaredDisclosure? declared) => new()
    {
        ConfigurationId = "config",
        ConfigurationName = "Configuration",
        AnalyzedCardCount = 100,
        LandCount = 35,
        TargetLandCount = 36,
        LandDelta = -1,
        Health = "Healthy",
        RampSourceCount = 10,
        HardToCastCount = 0,
        IsCoreOnly = false,
        Signals = new ConfigurationSignalSummary
        {
            BracketNumber = 3,
            ComboDetectionAvailable = true,
            CatalogEffectiveDate = "2026-01-01",
            Declared = declared,
        },
    };

    private static ManabaseHandoffPayload Handoff(int spellCount) => new()
    {
        DecklistText = "1 Sol Ring",
        DeckName = "Handoff deck",
        Mode = ManabaseMode.Casual,
        Result = new ManabaseAnalysisResult(
            new ManabaseReport { ActualLands = 36, TargetLands = 36, ColorFindings = [], Summary = "" },
            string.Empty,
            [],
            null,
            string.Empty,
            [],
            null,
            null,
            false)
        {
            AnalyzedSpells = Enumerable.Range(0, spellCount)
                .Select(index => new SpellRequirement
                {
                    Name = $"Spell {index}",
                    ManaValue = 3,
                    Pips = new Dictionary<ManaColor, int> { [ManaColor.Blue] = 1 },
                })
                .ToArray(),
        },
    };
}
