using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeckFlow.Core.Loading;
using DeckFlow.Core.Manabase;
using DeckFlow.Core.Models;
using DeckFlow.Web.Services;
using DeckFlow.Web.Controllers;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services.Manabase;
using DeckFlow.Web.Services.Scryfall;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using RestSharp;
using Xunit;

#pragma warning disable CS8602, CS8604

namespace DeckFlow.Web.Tests;

/// <summary>
/// Validates <see cref="ManabaseAnalysisService"/>: board filtering, printing-preferred
/// resolution (alternate names), unresolved handling, and report production — all with
/// faked deck loading and Scryfall HTTP.
/// </summary>
public sealed class ManabaseAnalysisServiceTests
{
    private static (ManabaseAnalysisService Baseline, ManabaseAnalysisService ExplicitOff, ManabaseAnalysisService On) BuildFlagServiceTriple(
        List<DeckEntry> entries,
        List<ScryfallCard> cards,
        string flagKey)
    {
        return (
            new ManabaseAnalysisService(
                new FakeLoader(entries),
                new FakeResolver(cards),
                new FakeFeatureFlagCache(new Dictionary<string, bool>())),
            new ManabaseAnalysisService(
                new FakeLoader(entries),
                new FakeResolver(cards),
                new FakeFeatureFlagCache(new Dictionary<string, bool>
                {
                    [flagKey] = false,
                })),
            new ManabaseAnalysisService(
                new FakeLoader(entries),
                new FakeResolver(cards),
                new FakeFeatureFlagCache(new Dictionary<string, bool>
                {
                    [flagKey] = true,
                })));
    }

    [Fact]
    public async Task AnalyzeAsync_ProducesReport_FiltersSideboard_ResolvesByPrinting()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Tymna the Weaver", 1, "commander", set: "cmr", cn: "1"),
            Land("Plains", 12),
            Land("Island", 10),
            Entry("Swords to Plowshares", 1, "mainboard"),
            // Alternate (flavor) name; resolves only via its printing.
            Entry("Godzilla, King of the Monsters", 1, "mainboard", set: "iko", cn: "275"),
            // Sideboard card must be excluded from the analysis.
            Entry("Black Lotus", 1, "sideboard"),
        };

        var cards = new List<ScryfallCard>
        {
            BasicLand("Plains", "W"),
            BasicLand("Island", "U"),
            Spell("Tymna the Weaver", "{1}{W}", 2, "Legendary Creature — Human Cleric"),
            Spell("Swords to Plowshares", "{W}", 1, "Instant"),
            // Canonical name differs from the deck entry; matched by set+collector.
            Spell("Zilortha, Strength Incarnate", "{2}{R}{R}", 4, "Legendary Creature — Dinosaur",
                set: "iko", cn: "275"),
        };

        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync(
            "https://archidekt.com/decks/1", "Test Deck", options: null, CancellationToken.None);

        Assert.NotNull(result.Report);
        Assert.Equal(22, result.Report.ActualLands); // 12 Plains + 10 Island; sideboard excluded.
        Assert.Empty(result.Unresolved); // Godzilla resolved via printing.
        Assert.Contains("Test Deck", result.PromptSwapPrompt);
        Assert.NotEmpty(result.Report.ColorFindings);
        // Default profile is Casual so existing output is unchanged.
        Assert.Equal(ManabaseMode.Casual, result.Report.Mode);
    }

    [Fact]
    public async Task AnalyzeAsync_HeaderlessMoxfieldPaste_InfersLeadingCommander()
    {
        // Moxfield plaintext exports carry no "Commander" header — the commander is simply the
        // leading card and every entry parses as "mainboard". The service must infer the leading
        // one-of as the commander so the callout and color weighting work. The rest of the deck is
        // alphabetical, so the third-entry guard keeps the inference to Bello alone.
        var entries = new List<DeckEntry>
        {
            Entry("Bello, Bard of the Brambles", 1, "mainboard", set: "blc", cn: "101"),
            Entry("Aggravated Assault", 1, "mainboard"),
            Entry("Ancient Tomb", 1, "mainboard"),
            Land("Forest", 34),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Forest", "G"),
            Spell("Bello, Bard of the Brambles", "{2}{R}{G}", 4, "Legendary Creature — Elemental Bard",
                set: "blc", cn: "101"),
            Spell("Aggravated Assault", "{2}{R}", 3, "Enchantment"),
            Spell("Ancient Tomb", "{0}", 0, "Land"),
        };

        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync("paste", "Bello Deck");

        var commanderRows = result.Report.Castability.Where(c => c.IsCommander).ToList();
        Assert.Single(commanderRows);
        Assert.Equal("Bello, Bard of the Brambles", commanderRows[0].Name);
    }

    [Fact]
    public async Task AnalyzeAsync_HeaderlessPaste_CommanderNameAlsoOnSideboard_PromotesOnlyLeadingCopy()
    {
        // A same-named copy on the sideboard must NOT be pulled into the analyzed set as a second
        // commander — only the leading mainboard copy is promoted.
        var entries = new List<DeckEntry>
        {
            Entry("Bello, Bard of the Brambles", 1, "mainboard", set: "blc", cn: "101"),
            Entry("Aggravated Assault", 1, "mainboard"),
            Entry("Ancient Tomb", 1, "mainboard"),
            Land("Forest", 34),
            Entry("Bello, Bard of the Brambles", 1, "sideboard", set: "blc", cn: "101"),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Forest", "G"),
            Spell("Bello, Bard of the Brambles", "{2}{R}{G}", 4, "Legendary Creature — Elemental Bard",
                set: "blc", cn: "101"),
            Spell("Aggravated Assault", "{2}{R}", 3, "Enchantment"),
            Spell("Ancient Tomb", "{0}", 0, "Land"),
        };

        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync("paste", "Bello Deck");

        // Exactly one commander row — the sideboard copy stayed out of the analyzed set.
        Assert.Single(result.Report.Castability, c => c.IsCommander);
    }

    [Fact]
    public async Task AnalyzeAsync_HeaderlessWinotaPaste_KeepsWinotaAsCommander_AndRejectsAcademyRector()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Winota, Joiner of Forces", 1, "mainboard"),
            Entry("Academy Rector", 1, "mainboard"),
            Entry("Ancient Tomb", 1, "mainboard"),
            Land("Mountain", 34),
            Land("Plains", 30),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Mountain", "R"),
            BasicLand("Plains", "W"),
            Spell("Winota, Joiner of Forces", "{2}{R}{W}", 4, "Legendary Creature — Human Warrior"),
            Spell("Academy Rector", "{3}{W}", 4, "Creature — Human Cleric"),
            Spell("Ancient Tomb", "{0}", 0, "Land"),
        };

        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync("paste", "Winota Deck");

        Assert.NotNull(result.Report);
        Assert.False(result.CommanderSelectionRequired);
        Assert.Equal(1, result.Report.LandTarget!.CommanderCount);
        CardCastability commander = Assert.Single(result.Report.Castability, row => row.IsCommander);
        Assert.Equal("Winota, Joiner of Forces", commander.Name);
        Assert.DoesNotContain(result.Report.Castability.Where(row => row.IsCommander), row => row.Name == "Academy Rector");
    }

    [Fact]
    public async Task AnalyzeAsync_InferredNonLegendaryCommander_ClearsFlag_AndRequiresSelection()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Academy Rector", 1, "mainboard"),
            Entry("Arcane Signet", 1, "mainboard"),
            Entry("Ancient Tomb", 1, "mainboard"),
            Entry("Winota, Joiner of Forces", 1, "mainboard"),
            Land("Mountain", 34),
            Land("Plains", 30),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Mountain", "R"),
            BasicLand("Plains", "W"),
            Spell("Academy Rector", "{3}{W}", 4, "Creature — Human Cleric"),
            Spell("Arcane Signet", "{2}", 2, "Artifact"),
            Spell("Ancient Tomb", "{0}", 0, "Land"),
            Spell("Winota, Joiner of Forces", "{2}{R}{W}", 4, "Legendary Creature — Human Warrior"),
        };

        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync("paste", "Selection Deck");

        Assert.True(result.CommanderSelectionRequired);
        Assert.Null(result.Report);
        Assert.NotEmpty(result.CommanderChoices);
        Assert.Contains("Winota, Joiner of Forces", result.CommanderChoices);
        Assert.DoesNotContain("Academy Rector", result.CommanderChoices);
    }

    [Fact]
    public async Task AnalyzeAsync_SelectedCommander_OverridesInferredCommander_AndProducesReport()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Academy Rector", 1, "mainboard"),
            Entry("Arcane Signet", 1, "mainboard"),
            Entry("Ancient Tomb", 1, "mainboard"),
            Entry("Winota, Joiner of Forces", 1, "mainboard"),
            Land("Mountain", 34),
            Land("Plains", 30),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Mountain", "R"),
            BasicLand("Plains", "W"),
            Spell("Academy Rector", "{3}{W}", 4, "Creature — Human Cleric"),
            Spell("Arcane Signet", "{2}", 2, "Artifact"),
            Spell("Ancient Tomb", "{0}", 0, "Land"),
            Spell("Winota, Joiner of Forces", "{2}{R}{W}", 4, "Legendary Creature — Human Warrior"),
        };

        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync(
            "paste",
            "Winota Deck",
            new ManabaseAnalysisOptions { SelectedCommander = "Winota, Joiner of Forces" });

        Assert.False(result.CommanderSelectionRequired);
        Assert.NotNull(result.Report);
        CardCastability commander = Assert.Single(result.Report.Castability, row => row.IsCommander);
        Assert.Equal("Winota, Joiner of Forces", commander.Name);
    }

    [Fact]
    public async Task AnalyzeAsync_HeaderlessPartnerPaste_PreservesTwoEligibleCommanders()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Tymna the Weaver", 1, "mainboard"),
            Entry("Thrasios, Triton Hero", 1, "mainboard"),
            Entry("Abrupt Decay", 1, "mainboard"),
            Land("Forest", 20),
            Land("Island", 20),
            Land("Plains", 20),
            Land("Swamp", 20),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Forest", "G"),
            BasicLand("Island", "U"),
            BasicLand("Plains", "W"),
            BasicLand("Swamp", "B"),
            Spell("Tymna the Weaver", "{1}{W}{B}", 3, "Legendary Creature — Human Cleric"),
            Spell("Thrasios, Triton Hero", "{G}{U}", 2, "Legendary Creature — Merfolk Wizard"),
            Spell("Abrupt Decay", "{B}{G}", 2, "Instant"),
        };

        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync("paste", "Partners");

        Assert.NotNull(result.Report);
        Assert.False(result.CommanderSelectionRequired);
        Assert.Equal(2, result.Report.LandTarget!.CommanderCount);
        Assert.Equal(2, result.Report.Castability.Count(row => row.IsCommander));
        Assert.Contains("Tymna the Weaver", result.CommanderChoices);
        Assert.Contains("Thrasios, Triton Hero", result.CommanderChoices);
    }

    [Fact]
    public async Task AnalyzeAsync_RampCreditV2Flag_DropsOneShotRitualFromLandTarget()
    {
        // MQ-03 plumbing: the flag is read BEFORE classification → narrows the ramp/draw credit. A
        // one-shot ritual (Dark Ritual, an Instant) loses the credit under v2; a mana rock (Sol Ring)
        // keeps it. Confirms the bool reaches ManabaseClassifier and fails safe OFF without the cache.
        static ScryfallCard Oracle(string name, string cost, double cmc, string type, string oracle) => new(
            Name: name, ManaCost: cost, TypeLine: type, OracleText: oracle,
            Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
            SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
            Layout: "normal", Cmc: cmc, ProducedMana: null, Rarity: "rare");

        var entries = new List<DeckEntry>
        {
            Entry("Tymna the Weaver", 1, "commander", set: "cmr", cn: "1"),
            Land("Swamp", 33),
            Entry("Dark Ritual", 1, "mainboard"),
            Entry("Sol Ring", 1, "mainboard"),
        };
        static List<ScryfallCard> Cards() => new()
        {
            BasicLand("Swamp", "B"),
            Spell("Tymna the Weaver", "{1}{W}", 2, "Legendary Creature — Human Cleric"),
            Oracle("Dark Ritual", "{B}", 1, "Instant", "Add {B}{B}{B}."),
            Oracle("Sol Ring", "{1}", 1, "Artifact", "{T}: Add {C}{C}."),
        };

        var off = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(Cards()));
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries), new FakeResolver(Cards()),
            new FakeFeatureFlagCache(new Dictionary<string, bool> { [ManabaseAnalysisService.AccuracyFlagKey] = true }));

        var rOff = await off.AnalyzeAsync("x", null);
        var rOn = await on.AnalyzeAsync("x", null);

        // Off (no cache → fail-safe off): broad predicate counts ritual + rock.
        Assert.Equal(2, rOff.Report.LandTarget!.RampAndDrawUnderThree);
        // On: the one-shot ritual is dropped, the rock is kept.
        Assert.Equal(1, rOn.Report.LandTarget!.RampAndDrawUnderThree);
        Assert.True(rOn.Report.TargetLands >= rOff.Report.TargetLands); // less ramp credit → higher target
    }

    [Fact]
    public async Task AnalyzeAsync_HealthBandHeadlineFloorFlag_ThreadsToReport()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Tymna the Weaver", 1, "commander", set: "cmr", cn: "1"),
            Land("Plains", 30),
            Entry("Swords to Plowshares", 1, "mainboard"),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Plains", "W"),
            Spell("Tymna the Weaver", "{1}{W}", 2, "Legendary Creature — Human Cleric"),
            Spell("Swords to Plowshares", "{W}", 1, "Instant"),
        };

        var off = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries), new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.AccuracyFlagKey] = true,
            }));

        ManabaseAnalysisResult offResult = await off.AnalyzeAsync("x", null);
        ManabaseAnalysisResult onResult = await on.AnalyzeAsync("x", null);

        Assert.False(offResult.Report.UseHealthBandHeadlineFloor);
        Assert.True(onResult.Report.UseHealthBandHeadlineFloor);
    }

    [Fact]
    public async Task AnalyzeAsync_LandRampSimFlag_RaisesPayoffCast_FailsSafeOff()
    {
        // 70-03b plumbing: the flag is read via IsFlagOn (fail-safe OFF) and threaded into Classify, so
        // repeatable land-ramp is modeled as colorless ramp in the sim. On a Forest + Cultivate deck the
        // expensive {6}{G} payoff casts more often when the flag is on; without a cache it does not.
        static ScryfallCard Oracle(string name, string cost, double cmc, string type, string oracle) => new(
            Name: name, ManaCost: cost, TypeLine: type, OracleText: oracle,
            Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
            SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
            Layout: "normal", Cmc: cmc, ProducedMana: null, Rarity: "rare");
        const string ramp = "Search your library for a basic land card, put it onto the battlefield tapped, then shuffle.";

        var entries = new List<DeckEntry>
        {
            Entry("Azusa, Lost but Seeking", 1, "commander", set: "chk", cn: "212"),
            Land("Forest", 33),
            Entry("Rampant Growth", 1, "mainboard"),
            Entry("Nature's Lore", 1, "mainboard"),
            Entry("Three Visits", 1, "mainboard"),
            Entry("Cultivate", 1, "mainboard"),
            Entry("Kodama's Reach", 1, "mainboard"),
            Entry("Big Green", 1, "mainboard"),
        };
        for (int i = 0; i < 55; i++)
        {
            entries.Add(Entry($"Filler {i}", 1, "mainboard"));
        }

        List<ScryfallCard> Cards()
        {
            var cards = new List<ScryfallCard>
            {
                BasicLand("Forest", "G"),
                Spell("Azusa, Lost but Seeking", "{2}{G}", 3, "Legendary Creature — Human Monk"),
                Oracle("Rampant Growth", "{1}{G}", 2, "Sorcery", ramp),
                Oracle("Nature's Lore", "{1}{G}", 2, "Sorcery", ramp),
                Oracle("Three Visits", "{1}{G}", 2, "Sorcery", ramp),
                Oracle("Cultivate", "{2}{G}", 3, "Sorcery", ramp),
                Oracle("Kodama's Reach", "{2}{G}", 3, "Sorcery", ramp),
                Oracle("Big Green", "{6}{G}", 7, "Creature — Hydra", "Trample."),
            };
            for (int i = 0; i < 55; i++)
            {
                cards.Add(Oracle($"Filler {i}", "{3}", 3, "Artifact", "Does nothing."));
            }

            return cards;
        }

        var off = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(Cards()));
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries), new FakeResolver(Cards()),
            new FakeFeatureFlagCache(new Dictionary<string, bool> { [ManabaseAnalysisService.AccuracyFlagKey] = true }));

        var rOff = await off.AnalyzeAsync("x", null);
        var rOn = await on.AnalyzeAsync("x", null);

        int castOff = rOff.Report.Castability.First(c => c.Name == "Big Green").CastPercent;
        int castOn = rOn.Report.Castability.First(c => c.Name == "Big Green").CastPercent;

        Assert.True(castOn > castOff, $"land-ramp sim should raise the payoff's cast% (off={castOff}, on={castOn})");
        // Colorless ramp source → land total + color verdict unchanged.
        Assert.Equal(rOff.Report.TargetLands, rOn.Report.TargetLands);
        Assert.Equal(rOff.Report.ActualLands, rOn.Report.ActualLands);
    }

    [Fact]
    public async Task AnalyzeAsync_MdfcBack_ModeledAsRealLand_RegardlessOfAccuracyFlag()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Tymna the Weaver", 1, "commander", set: "cmr", cn: "1"),
            Land("Swamp", 32),
            Entry("Bala Ged Recovery // Bala Ged Sanctuary", 1, "mainboard"),
            Entry("Agadeem's Awakening // Agadeem, the Undercrypt", 1, "mainboard"),
            Entry("Feed the Swarm", 1, "mainboard"),
        };

        var cards = new List<ScryfallCard>
        {
            BasicLand("Swamp", "B"),
            Spell("Tymna the Weaver", "{1}{W}", 2, "Legendary Creature — Human Cleric"),
            Spell("Feed the Swarm", "{1}{B}", 2, "Sorcery"),
            new(
                Name: "Bala Ged Recovery // Bala Ged Sanctuary",
                ManaCost: "{2}{G}",
                TypeLine: "Sorcery",
                OracleText: "Return target permanent card from your graveyard to your hand.\nBala Ged Sanctuary enters tapped.",
                Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
                SetCode: null, SetName: null, CollectorNumber: null, Id: null,
                CardFaces: new List<ScryfallCardFace>
                {
                    new(Name: "Bala Ged Recovery", ManaCost: "{2}{G}", TypeLine: "Sorcery", OracleText: "Return target permanent card from your graveyard to your hand.", Power: null, Toughness: null),
                    new(Name: "Bala Ged Sanctuary", ManaCost: null, TypeLine: "Land", OracleText: "Bala Ged Sanctuary enters tapped.", Power: null, Toughness: null),
                },
                Layout: "modal_dfc",
                Cmc: 3,
                ProducedMana: new[] { "G" },
                Rarity: "uncommon"),
            new(
                Name: "Agadeem's Awakening // Agadeem, the Undercrypt",
                ManaCost: "{X}{B}{B}{B}",
                TypeLine: "Sorcery",
                OracleText: "Return from graveyard.\nAs Agadeem, the Undercrypt enters, you may pay 3 life. If you don't, it enters tapped.",
                Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
                SetCode: null, SetName: null, CollectorNumber: null, Id: null,
                CardFaces: new List<ScryfallCardFace>
                {
                    new(Name: "Agadeem's Awakening", ManaCost: "{X}{B}{B}{B}", TypeLine: "Sorcery", OracleText: "Return from graveyard.", Power: null, Toughness: null),
                    new(Name: "Agadeem, the Undercrypt", ManaCost: null, TypeLine: "Land", OracleText: "As Agadeem, the Undercrypt enters, you may pay 3 life. If you don't, it enters tapped.", Power: null, Toughness: null),
                },
                Layout: "modal_dfc",
                Cmc: 3,
                ProducedMana: new[] { "B" },
                Rarity: "mythic"),
        };

        var off = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries), new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool> { [ManabaseAnalysisService.AccuracyFlagKey] = true }));

        var rOff = await off.AnalyzeAsync("x", null);
        var rOn = await on.AnalyzeAsync("x", null);

        // MDFC land backs are real lands whether or not the accuracy flag is on: 32 Swamps + 2 MDFC = 34.
        Assert.Equal(rOff.Report.ActualLands, rOn.Report.ActualLands);
        Assert.Equal(34, rOn.Report.ActualLands);
    }

    [Fact]
    public async Task AnalyzeAsync_AccuracyFlag_NewConditionalLandCycles_OnDiverges_OffMatchesBaseline()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Tymna the Weaver", 1, "commander", set: "cmr", cn: "1"),
            Land("Island", 12),
            Land("Plains", 10),
            Land("Mountain", 6),
            Land("Forest", 6),
            Entry("Seachrome Coast", 1, "mainboard"),
            Entry("Deserted Beach", 1, "mainboard"),
            Entry("Mystic Sanctuary", 1, "mainboard"),
            Entry("Floodfarm Verge", 1, "mainboard"),
            Entry("Training Compound", 1, "mainboard"),
            Entry("Vivid Meadow", 1, "mainboard"),
            Entry("Counterspell", 1, "mainboard"),
            Entry("Growth Spiral", 1, "mainboard"),
        };

        var cards = new List<ScryfallCard>
        {
            BasicLand("Island", "U"),
            BasicLand("Plains", "W"),
            BasicLand("Mountain", "R"),
            BasicLand("Forest", "G"),
            Spell("Tymna the Weaver", "{1}{W}", 2, "Legendary Creature — Human Cleric"),
            Spell("Counterspell", "{U}{U}", 2, "Instant"),
            Spell("Growth Spiral", "{G}{U}", 2, "Instant"),
            LandOracle("Seachrome Coast", "Land",
                "Seachrome Coast enters the battlefield tapped unless you control two or fewer other lands.",
                "W", "U"),
            LandOracle("Deserted Beach", "Land",
                "Deserted Beach enters the battlefield tapped unless you control two or more other lands.",
                "W", "U"),
            LandOracle("Mystic Sanctuary", "Land — Island",
                "Mystic Sanctuary enters the battlefield tapped unless you control three or more other Islands.",
                "U"),
            LandOracle("Floodfarm Verge", "Land",
                "{T}: Add {W}. {T}: Add {U}. Activate only if you control a Plains or an Island.",
                "W", "U"),
            LandOracle("Training Compound", "Land",
                "{T}: Add {C}. {T}: Add {R} or {G}. Activate only if this land entered this turn or if you control a basic land.",
                "C", "R", "G"),
            LandOracle("Vivid Meadow", "Land",
                "Vivid Meadow enters the battlefield tapped with two charge counters on it. {T}: Add {W}. {T}, Remove a charge counter from this land: Add one mana of any color.",
                "W", "U", "B", "R", "G"),
        };

        var baseline = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));
        var explicitOff = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.AccuracyFlagKey] = false,
            }));
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.AccuracyFlagKey] = true,
            }));

        ManabaseAnalysisResult baselineResult = await baseline.AnalyzeAsync("paste", "Conditional Cycles");
        ManabaseAnalysisResult offResult = await explicitOff.AnalyzeAsync("paste", "Conditional Cycles");
        ManabaseAnalysisResult onResult = await on.AnalyzeAsync("paste", "Conditional Cycles");

        Assert.Equal(
            baselineResult.Report.Castability.Select(FormatCastabilityRow),
            offResult.Report.Castability.Select(FormatCastabilityRow));
        Assert.Equal(baselineResult.Report.AvgOnCurvePercent, offResult.Report.AvgOnCurvePercent);
        Assert.Equal(baselineResult.Report.Health, offResult.Report.Health);
        Assert.Equal(baselineResult.Report.TargetLands, offResult.Report.TargetLands);
        Assert.Equal(baselineResult.Report.ActualLands, offResult.Report.ActualLands);
        Assert.Equal(baselineResult.PromptSwapPrompt, offResult.PromptSwapPrompt);

        int offCast = offResult.Report.Castability.Single(c => c.Name == "Counterspell").CastPercent;
        int onCast = onResult.Report.Castability.Single(c => c.Name == "Counterspell").CastPercent;
        Assert.NotEqual(offCast, onCast);
    }

    [Fact]
    public void Classify_RestrictedLandsFlagOff_CavernStaysFullWeightAndNamesStayEmpty()
    {
        var facts = new List<CardFact>
        {
            new()
            {
                Name = "Cavern of Souls",
                Quantity = 1,
                TypeLine = "Land",
                OracleText = "As Cavern of Souls enters, choose a creature type. {T}: Add {C}. {T}: Add one mana of any color. Spend this mana only to cast a creature spell of the chosen type, and that spell can't be countered.",
                ProducedMana = new[] { "C", "W", "U", "B", "R", "G" },
                ManaValue = 0,
                HasLandFace = true,
            },
            new()
            {
                Name = "Elf Body",
                Quantity = 2,
                ManaCost = "{G}",
                ManaValue = 1,
                TypeLine = "Creature — Elf Druid",
                OracleText = string.Empty,
                ProducedMana = Array.Empty<string>(),
            },
        };

        ManabaseDeck deck = ManabaseClassifier.Classify(facts, restrictedLands: false);

        ManaSource cavern = Assert.Single(deck.Sources, s => s.Name == "Cavern of Souls");
        Assert.Equal(1.0, cavern.Weight);
        Assert.Empty(deck.RestrictedSourceLandNames);
        Assert.False(deck.HasRestrictedSourceApproximation);
    }

    [Fact]
    public async Task AnalyzeAsync_RestrictedLandsFlag_OnDiverges_OffMatchesBaseline()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Tymna the Weaver", 1, "commander", set: "cmr", cn: "1"),
            Land("Island", 18),
            Land("Plains", 14),
            Entry("Cavern of Souls", 1, "mainboard"),
            Entry("Ancient Ziggurat", 1, "mainboard"),
            Entry("Nykthos, Shrine to Nyx", 1, "mainboard"),
            Entry("Elf One", 1, "mainboard"),
            Entry("Elf Two", 1, "mainboard"),
            Entry("Human One", 1, "mainboard"),
            Entry("Counterspell", 1, "mainboard"),
        };
        for (int i = 0; i < 55; i++)
        {
            entries.Add(Entry($"Filler {i}", 1, "mainboard"));
        }

        var cards = new List<ScryfallCard>
        {
            BasicLand("Island", "U"),
            BasicLand("Plains", "W"),
            Spell("Tymna the Weaver", "{1}{W}", 2, "Legendary Creature — Human Cleric"),
            Spell("Elf One", "{G}", 1, "Creature — Elf Druid"),
            Spell("Elf Two", "{1}{G}", 2, "Creature — Elf Warrior"),
            Spell("Human One", "{1}{W}", 2, "Creature — Human Soldier"),
            Spell("Counterspell", "{U}{U}", 2, "Instant"),
            LandOracle("Cavern of Souls", "Land",
                "As Cavern of Souls enters, choose a creature type. {T}: Add {C}. {T}: Add one mana of any color. Spend this mana only to cast a creature spell of the chosen type, and that spell can't be countered.",
                "C", "W", "U", "B", "R", "G"),
            LandOracle("Ancient Ziggurat", "Land",
                "{T}: Add one mana of any color. Spend this mana only to cast a creature spell.",
                "W", "U", "B", "R", "G"),
            LandOracle("Nykthos, Shrine to Nyx", "Legendary Land",
                "{T}: Add {C}. {2}, {T}: Choose a color. Add an amount of mana of that color equal to your devotion to that color.",
                "C", "G"),
        };
        cards.AddRange(Enumerable.Range(0, 55).Select(i => Spell($"Filler {i}", "{1}", 1, "Artifact")));

        var baseline = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));
        var explicitOff = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.RestrictedLandsFlagKey] = false,
            }));
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.RestrictedLandsFlagKey] = true,
            }));

        ManabaseAnalysisResult baselineResult = await baseline.AnalyzeAsync("paste", "Restricted Lands");
        ManabaseAnalysisResult offResult = await explicitOff.AnalyzeAsync("paste", "Restricted Lands");
        ManabaseAnalysisResult onResult = await on.AnalyzeAsync("paste", "Restricted Lands");

        Assert.Equal(
            baselineResult.Report.Castability.Select(FormatCastabilityRow),
            offResult.Report.Castability.Select(FormatCastabilityRow));
        Assert.Equal(baselineResult.Report.AvgOnCurvePercent, offResult.Report.AvgOnCurvePercent);
        Assert.Equal(baselineResult.Report.Health, offResult.Report.Health);
        Assert.Equal(baselineResult.Report.TargetLands, offResult.Report.TargetLands);
        Assert.Equal(baselineResult.Report.ActualLands, offResult.Report.ActualLands);
        Assert.Equal(baselineResult.PromptSwapPrompt, offResult.PromptSwapPrompt);
        Assert.Empty(offResult.Report.RestrictedSourceLandNames);
        Assert.False(offResult.Report.HasRestrictedSourceApproximation);

        double offBlue = offResult.Report.ColorFindings.Single(f => f.Color == ManaColor.Blue).ActualSources;
        double onBlue = onResult.Report.ColorFindings.Single(f => f.Color == ManaColor.Blue).ActualSources;
        Assert.NotEqual(offBlue, onBlue);
        Assert.Equal(
            new[]
            {
                "Cavern of Souls",
                "Ancient Ziggurat",
                "Nykthos, Shrine to Nyx",
            },
            onResult.Report.RestrictedSourceLandNames);
        Assert.True(onResult.Report.HasRestrictedSourceApproximation);
        UnsupportedInteraction restricted = Assert.Single(
            onResult.Report.UnsupportedInteractions,
            u => u.Name == "Restricted land approximation");
        Assert.Contains("Cavern of Souls", restricted.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            offResult.Report.UnsupportedInteractions,
            u => u.Name == "Restricted land approximation");
    }

    [Fact]
    public async Task AnalyzeAsync_ColorAwareMulliganFlag_ChangesCast_FailsSafeOff()
    {
        // MQ-05 plumbing: the flag is read via IsFlagOn (fail-safe OFF) and threaded into the analyzer
        // → the castability rows' London mulligan becomes color-aware. On a White-skewed WU deck (blue
        // scarce) the color-aware keep guarantees an Island in every kept opener, so the {U} spell
        // casts more often when the flag is on; without a cache it stays count-only.
        static ScryfallCard Oracle(string name, string cost, double cmc, string type, string oracle) => new(
            Name: name, ManaCost: cost, TypeLine: type, OracleText: oracle,
            Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
            SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
            Layout: "normal", Cmc: cmc, ProducedMana: null, Rarity: "rare");

        var entries = new List<DeckEntry>
        {
            Entry("Tymna the Weaver", 1, "commander", set: "cmr", cn: "1"),
            Land("Plains", 29),
            Land("Island", 5),
            Entry("Blue One", 1, "mainboard"),
            Entry("White One", 1, "mainboard"),
        };
        // Pad to a realistic ~96-card deck: ~35% lands so 7-card openers land in the count band
        // (an all-land deck busts the band every time and force-mulligans past the color gate).
        for (int i = 0; i < 60; i++)
        {
            entries.Add(Entry($"Filler {i}", 1, "mainboard"));
        }

        List<ScryfallCard> Cards()
        {
            var cards = new List<ScryfallCard>
            {
                BasicLand("Plains", "W"),
                BasicLand("Island", "U"),
                Spell("Tymna the Weaver", "{1}{W}", 2, "Legendary Creature — Human Cleric"),
                Oracle("Blue One", "{U}", 1, "Instant", "Draw a card."),
                Oracle("White One", "{W}", 1, "Instant", "Gain 1 life."),
            };
            for (int i = 0; i < 60; i++)
            {
                cards.Add(Oracle($"Filler {i}", "{3}", 3, "Artifact", "Does nothing."));
            }

            return cards;
        }

        var off = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(Cards()));
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries), new FakeResolver(Cards()),
            new FakeFeatureFlagCache(new Dictionary<string, bool> { [ManabaseAnalysisService.AccuracyFlagKey] = true }));

        var rOff = await off.AnalyzeAsync("x", null);
        var rOn = await on.AnalyzeAsync("x", null);

        int castOff = rOff.Report.Castability.First(c => c.Name == "Blue One").CastPercent;
        int castOn = rOn.Report.Castability.First(c => c.Name == "Blue One").CastPercent;

        Assert.True(castOn > castOff, $"color-aware mulligan should raise scarce-color cast% (off={castOff}, on={castOn})");
        // Color counts must not move with the flag (verdict probe path stays count-only).
        Assert.Equal(rOff.Report.TargetLands, rOn.Report.TargetLands);
    }

    [Fact]
    public async Task AnalyzeAsync_SourceManaQuantityFlag_RaisesAffordability_FailsSafeOff()
    {
        // Bundled accuracy plumbing: the flag "analysis.manabase.accuracy" is read via IsFlagOn (fail-safe OFF)
        // and threaded as useManaQuantity into ManabaseAnalyzer.Analyze → CastabilitySimulator. When ON
        // each colorless burst source (oracle "{T}: Add {C}{C}.") contributes ManaAmount=2 so a big
        // colorless payoff casts more often. Without a cache the key is absent → IsFlagOn returns false
        // → same result as explicit OFF. Mirrors the Core ManaQuantityTests.ManaQuantity_RaisesAffordability
        // deck shape: many burst rocks + thin land base + expensive colorless payoff.
        //
        // Rocks MUST carry ProducedMana: ["C"] — the classifier's IsRockOrDork gate short-circuits when
        // ProducedMana.Count == 0, so rocks without it are never added to deck.Sources and ManaAmount
        // never reaches the simulator.
        static ScryfallCard Oracle(string name, string cost, double cmc, string type, string oracle) => new(
            Name: name, ManaCost: cost, TypeLine: type, OracleText: oracle,
            Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
            SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
            Layout: "normal", Cmc: cmc, ProducedMana: null, Rarity: "rare");

        static ScryfallCard ColorlessRock(string name) => new(
            Name: name, ManaCost: "{1}", TypeLine: "Artifact", OracleText: "{T}: Add {C}{C}.",
            Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
            SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
            Layout: "normal", Cmc: 1,
            // "C" in ProducedMana is required: IsRockOrDork checks ProducedMana.Count > 0 as a gate.
            // ManaProductionAmount.Parse("{T}: Add {C}{C}.") == 2 → ManaAmount=2 when flag ON.
            ProducedMana: new[] { "C" }, Rarity: "rare");

        // 30 Islands + 20 burst colorless rocks + 1 expensive payoff = 51 cards; pad to ~99.
        var entries = new List<DeckEntry>
        {
            Entry("Commander Guy", 1, "commander"),
            Land("Island", 30),
            Entry("Big Colorless", 1, "mainboard"),
        };
        // 20 distinct rock names so the Scryfall resolver returns each; all produce {C}{C}.
        for (int i = 0; i < 20; i++)
        {
            entries.Add(Entry($"Burst Rock {i}", 1, "mainboard"));
        }
        for (int i = 0; i < 47; i++)
        {
            entries.Add(Entry($"Filler {i}", 1, "mainboard"));
        }

        List<ScryfallCard> Cards()
        {
            var cards = new List<ScryfallCard>
            {
                BasicLand("Island", "U"),
                Spell("Commander Guy", "{2}{U}", 3, "Legendary Creature — Human"),
                // MV=6 pure generic payoff — the sim must scrape together 6 mana on turn 6.
                Oracle("Big Colorless", "{6}", 6, "Artifact", "Does nothing."),
            };
            // 20 burst rocks with ProducedMana=["C"] so they reach deck.Sources and ManaAmount wires in.
            for (int i = 0; i < 20; i++)
            {
                cards.Add(ColorlessRock($"Burst Rock {i}"));
            }
            for (int i = 0; i < 47; i++)
            {
                cards.Add(Oracle($"Filler {i}", "{3}", 3, "Artifact", "Does nothing."));
            }

            return cards;
        }

        // OFF path: no cache at all → IsFlagOn("analysis.manabase.accuracy") returns false.
        var off = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(Cards()));

        // ON path: cache present with the flag enabled.
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries), new FakeResolver(Cards()),
            new FakeFeatureFlagCache(new Dictionary<string, bool> { [ManabaseAnalysisService.AccuracyFlagKey] = true }));

        var rOff = await off.AnalyzeAsync("x", null);
        var rOn = await on.AnalyzeAsync("x", null);

        int castOff = rOff.Report.Castability.First(c => c.Name == "Big Colorless").CastPercent;
        int castOn = rOn.Report.Castability.First(c => c.Name == "Big Colorless").CastPercent;

        // Fail-safe OFF: absent cache must behave identically to explicit false.
        var explicitOff = new ManabaseAnalysisService(
            new FakeLoader(entries), new FakeResolver(Cards()),
            new FakeFeatureFlagCache(new Dictionary<string, bool> { [ManabaseAnalysisService.AccuracyFlagKey] = false }));
        var rExplicitOff = await explicitOff.AnalyzeAsync("x", null);
        int castExplicitOff = rExplicitOff.Report.Castability.First(c => c.Name == "Big Colorless").CastPercent;

        Assert.Equal(castExplicitOff, castOff); // absent key == explicit false (fail-safe off)
        Assert.True(castOn > castOff, $"accuracy ON should raise payoff cast% via mana quantity (off={castOff}, on={castOn})");
    }

    [Fact]
    public async Task AnalyzeAsync_RitualBurstFlagOff_MatchesBaselineCastPercent()
    {
        var (entries, cards) = RitualBurstFixture();
        var baseline = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));
        var explicitOff = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.RitualBurstFlagKey] = false,
            }));

        var baselineResult = await baseline.AnalyzeAsync(
            "paste", "Ritual Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });
        var offResult = await explicitOff.AnalyzeAsync(
            "paste", "Ritual Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.Equal(
            baselineResult.Report.Castability.Single(c => c.Name == "Necropotence").CastPercent,
            offResult.Report.Castability.Single(c => c.Name == "Necropotence").CastPercent);
    }

    [Fact]
    public async Task AnalyzeAsync_RitualBurstFlagOn_Cedh_RaisesTripleBlackCastPercent()
    {
        var (entries, cards) = RitualBurstFixture();
        var off = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.RitualBurstFlagKey] = false,
            }));
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.RitualBurstFlagKey] = true,
            }));

        var offResult = await off.AnalyzeAsync(
            "paste", "Ritual Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });
        var onResult = await on.AnalyzeAsync(
            "paste", "Ritual Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        int castOff = offResult.Report.Castability.Single(c => c.Name == "Necropotence").CastPercent;
        int castOn = onResult.Report.Castability.Single(c => c.Name == "Necropotence").CastPercent;
        int lift = castOn - castOff;

        Assert.True(castOn > castOff, $"ritual burst should raise Necropotence cast% in cEDH (off={castOff}, on={castOn})");
        Assert.True(lift > 0, "ritual burst lift should be strictly positive; a zero lift suggests Dark Ritual failed to classify.");
    }

    [Fact]
    public async Task AnalyzeAsync_RitualBurstFlagOn_Casual_KeepsTripleBlackCastPercentUnchanged()
    {
        var (entries, cards) = RitualBurstFixture();
        var off = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.RitualBurstFlagKey] = false,
            }));
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.RitualBurstFlagKey] = true,
            }));

        var offResult = await off.AnalyzeAsync(
            "paste", "Ritual Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });
        var onResult = await on.AnalyzeAsync(
            "paste", "Ritual Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });

        Assert.Equal(
            offResult.Report.Castability.Single(c => c.Name == "Necropotence").CastPercent,
            onResult.Report.Castability.Single(c => c.Name == "Necropotence").CastPercent);
    }

    [Fact]
    public async Task AnalyzeAsync_RitualLandCreditFlag_OffMatchesBaseline_OnReducesCedhLandTarget()
    {
        var (entries, cards) = RitualBurstFixture();
        var baseline = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = true,
            }));
        var explicitOff = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = true,
                [ManabaseAnalysisService.RitualLandCreditFlagKey] = false,
            }));
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = true,
                [ManabaseAnalysisService.RitualLandCreditFlagKey] = true,
            }));

        var baselineResult = await baseline.AnalyzeAsync(
            "paste", "Ritual Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });
        var offResult = await explicitOff.AnalyzeAsync(
            "paste", "Ritual Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });
        var onResult = await on.AnalyzeAsync(
            "paste", "Ritual Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.Equal(baselineResult.Report.TargetLands, offResult.Report.TargetLands);
        Assert.True(onResult.Report.TargetLands < offResult.Report.TargetLands);
    }

    [Fact]
    public async Task AnalyzeAsync_ScryCreditFlag_AbsentAndExplicitOff_AreByteIdentical_OnAddsEffectiveSources()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Island", 10, "mainboard"),
            Entry("Preordain", 2, "mainboard"),
            Entry("Counterspell", 1, "mainboard"),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Island", "U"),
            Spell("Preordain", "{U}", 1, "Sorcery", oracle: "Scry 2, then draw a card."),
            Spell("Counterspell", "{U}{U}", 2, "Instant", oracle: "Counter target spell."),
        };

        var (baseline, explicitOff, on) = BuildFlagServiceTriple(entries, cards, ManabaseAnalysisService.ScryCreditFlagKey);

        var baselineResult = await baseline.AnalyzeAsync(
            "paste", "Scry Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });
        var offResult = await explicitOff.AnalyzeAsync(
            "paste", "Scry Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });
        var onResult = await on.AnalyzeAsync(
            "paste", "Scry Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });

        Assert.Equal(baselineResult.PromptSwapPrompt, offResult.PromptSwapPrompt);
        Assert.Equal(
            baselineResult.Report.ColorFindings.Single().ActualSources,
            offResult.Report.ColorFindings.Single().ActualSources);
        Assert.Equal(0.0, offResult.Report.ScrySourceCredit);
        Assert.Equal(0.4, onResult.Report.ScrySourceCredit, 6);
        Assert.Equal(
            offResult.Report.ColorFindings.Single().ActualSources + 0.4,
            onResult.Report.ColorFindings.Single().ActualSources,
            6);
    }

    [Fact]
    public async Task AnalyzeAsync_ColorlessSnowFlag_AbsentAndExplicitOff_AreByteIdentical_OnAddsDedicatedRequirementRows()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Wastes", 10, "mainboard"),
            Entry("Snow-Covered Island", 14, "mainboard"),
            Entry("Thought-Knot Seer", 1, "mainboard"),
            Entry("Arcum's Astrolabe", 1, "mainboard"),
        };
        var cards = new List<ScryfallCard>
        {
            LandOracle("Wastes", "Basic Land — Wastes", "{T}: Add {C}.", "C"),
            LandOracle("Snow-Covered Island", "Snow Land — Island", "{T}: Add {U}.", "U"),
            Spell("Thought-Knot Seer", "{3}{C}", 4, "Creature — Eldrazi"),
            Spell("Arcum's Astrolabe", "{S}", 1, "Artifact"),
        };

        var (baseline, explicitOff, on) = BuildFlagServiceTriple(entries, cards, ManabaseAnalysisService.ColorlessSnowFlagKey);

        var baselineResult = await baseline.AnalyzeAsync(
            "paste", "Category Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });
        var offResult = await explicitOff.AnalyzeAsync(
            "paste", "Category Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });
        var onResult = await on.AnalyzeAsync(
            "paste", "Category Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });

        Assert.Equal(baselineResult.PromptSwapPrompt, offResult.PromptSwapPrompt);
        Assert.Equal(baselineResult.Report.Summary, offResult.Report.Summary);
        Assert.Equal(
            baselineResult.Report.ColorFindings.Select(f => f.DisplayColor),
            offResult.Report.ColorFindings.Select(f => f.DisplayColor));
        Assert.Contains(onResult.Report.ColorFindings, finding => finding.DisplayColor == "Colorless");
        Assert.Contains(onResult.Report.ColorFindings, finding => finding.DisplayColor == "Snow");
    }

    [Fact]
    public async Task AnalyzeAsync_ColorlessSnowFlag_AbsentAndExplicitOff_PreserveReducerCastabilityParity_ForSnowBlueSpell()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Island", 20, "mainboard"),
            Entry("Goblin Electromancer", 1, "mainboard"),
            Entry("Snow Test Spell", 1, "mainboard"),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Island", "U"),
            Spell("Goblin Electromancer", "{U}", 1, "Creature — Goblin Wizard", oracle: "Instant and sorcery spells you cast cost {1} less to cast."),
            Spell("Snow Test Spell", "{S}{U}", 2, "Sorcery"),
        };

        var (baseline, explicitOff, on) = BuildFlagServiceTriple(entries, cards, ManabaseAnalysisService.ColorlessSnowFlagKey);

        var baselineResult = await baseline.AnalyzeAsync(
            "paste", "Snow Reducer Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });
        var offResult = await explicitOff.AnalyzeAsync(
            "paste", "Snow Reducer Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });
        var onResult = await on.AnalyzeAsync(
            "paste", "Snow Reducer Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });

        CardCastability baselineRow = baselineResult.Report.Castability.Single(row => row.Name == "Snow Test Spell");
        CardCastability offRow = offResult.Report.Castability.Single(row => row.Name == "Snow Test Spell");
        CardCastability onRow = onResult.Report.Castability.Single(row => row.Name == "Snow Test Spell");

        Assert.Equal(FormatCastabilityRow(baselineRow), FormatCastabilityRow(offRow));
        Assert.Equal(1, baselineRow.OnCurveTurn);
        Assert.Equal(1, offRow.OnCurveTurn);
        Assert.True(onRow.OnCurveTurn >= 1);
    }

    [Fact]
    public async Task AnalyzeAsync_TapAnalyzerFlagAbsent_ShowTapAnalyzerFalse()
    {
        var (entries, cards) = CurveFixture();
        // No cache at all → IsFlagOn(TapAnalyzerFlagKey) returns false (fail-safe OFF).
        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync("paste", "Curve Deck");

        Assert.False(GetResultShowTapAnalyzer(result));
    }

    [Fact]
    public async Task AnalyzeAsync_TapAnalyzerFlagExplicitlyFalse_ShowTapAnalyzerFalse()
    {
        var (entries, cards) = CurveFixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries), new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.TapAnalyzerFlagKey] = false,
            }));

        var result = await service.AnalyzeAsync("paste", "Curve Deck");

        Assert.False(GetResultShowTapAnalyzer(result));
    }

    [Fact]
    public async Task AnalyzeAsync_TapAnalyzerFlagOn_ShowTapAnalyzerTrue()
    {
        var (entries, cards) = CurveFixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries), new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.TapAnalyzerFlagKey] = true,
            }));

        var result = await service.AnalyzeAsync("paste", "Curve Deck");

        Assert.True(GetResultShowTapAnalyzer(result));
    }

    [Fact]
    public async Task AnalyzeAsync_SourceListFlagAbsent_ShowSourceListFalse()
    {
        var (entries, cards) = CurveFixture();
        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync("paste", "Curve Deck");

        Assert.False(GetResultShowSourceList(result));
    }

    [Fact]
    public async Task AnalyzeAsync_SourceListFlagOn_ShowSourceListTrue()
    {
        var (entries, cards) = CurveFixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries), new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.SourceListFlagKey] = true,
            }));

        var result = await service.AnalyzeAsync("paste", "Curve Deck");

        Assert.True(GetResultShowSourceList(result));
    }

    [Fact]
    public async Task AnalyzeAsync_CedhInteractionLensFlagOn_CedhMode_ShowsLens_AndThreadsSwapPrompt()
    {
        var (entries, cards) = CedhInteractionFixture();
        var store = new FakeCategoryKnowledgeStore();
        store.CategoriesByName["Drannith Magistrate"] = new[] { "Interaction" };
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhInteractionLensFlagKey] = true,
            }),
            store);

        var result = await service.AnalyzeAsync(
            "paste", "Interaction Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.True(GetResultShowCedhInteractionLens(result));
        Assert.NotNull(result.Report.InteractionLens);
        Assert.Equal(1, result.Report.InteractionLens!.QualifyingCount);
        Assert.Contains("1 / 1 cheap interaction spells are held up by turn 3", result.PromptSwapPrompt);
    }

    [Fact]
    public async Task AnalyzeAsync_CedhInteractionLensFlagOff_CedhMode_LeavesPromptByteIdentical()
    {
        var (entries, cards) = CedhInteractionFixture();
        var baseline = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));
        var explicitOff = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhInteractionLensFlagKey] = false,
            }));

        var baselineResult = await baseline.AnalyzeAsync(
            "paste", "Interaction Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });
        var offResult = await explicitOff.AnalyzeAsync(
            "paste", "Interaction Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.False(GetResultShowCedhInteractionLens(offResult));
        Assert.Null(offResult.Report.InteractionLens);
        Assert.Equal(baselineResult.PromptSwapPrompt, offResult.PromptSwapPrompt);

        string expectedPrompt = ManabaseSwapPromptBuilder.Build(
            offResult.Report, "Interaction Deck", BuildDecklistText(entries), ManabaseMode.Cedh);
        Assert.Equal(expectedPrompt, offResult.PromptSwapPrompt);
    }

    [Fact]
    public async Task AnalyzeAsync_CedhInteractionLensFlagOn_CasualMode_HidesLens()
    {
        var (entries, cards) = CedhInteractionFixture();
        var store = new FakeCategoryKnowledgeStore();
        store.CategoriesByName["Drannith Magistrate"] = new[] { "Interaction" };
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhInteractionLensFlagKey] = true,
            }),
            store);

        var result = await service.AnalyzeAsync(
            "paste", "Interaction Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });

        Assert.False(GetResultShowCedhInteractionLens(result));
        Assert.Null(result.Report.InteractionLens);
    }

    [Fact]
    public async Task AnalyzeAsync_CedhInteractionLensFlagOn_PlanPresenceOff_StillClassifiesInteractionRoles()
    {
        var (entries, cards) = CedhInteractionFixture();
        var store = new FakeCategoryKnowledgeStore();
        store.CategoriesByName["Drannith Magistrate"] = new[] { "Interaction" };
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhInteractionLensFlagKey] = true,
                [ManabaseAnalysisService.MulliganEvalFlagKey] = false,
                [ManabaseAnalysisService.PlanPresenceFlagKey] = false,
            }),
            store);

        var result = await service.AnalyzeAsync(
            "paste", "Interaction Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.False(GetResultShowPlanPresence(result));
        Assert.NotNull(result.Report.InteractionLens);
        Assert.True(result.Report.InteractionLens!.QualifyingCount > 0);
    }

    [Fact]
    public async Task AnalyzeAsync_CedhInteractionLensFlagOn_InstantInteractionUsesPreGateSignal_WhilePlanRoleStaysStripped()
    {
        var (entries, cards) = CedhInstantInteractionFixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhInteractionLensFlagKey] = true,
                [ManabaseAnalysisService.MulliganEvalFlagKey] = true,
                [ManabaseAnalysisService.PlanPresenceFlagKey] = true,
            }));

        var result = await service.AnalyzeAsync(
            "paste", "Instant Interaction Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        ManabaseInteractionLens lens = Assert.IsType<ManabaseInteractionLens>(result.Report.InteractionLens);
        Assert.True(lens.QualifyingCount > 0);
        Assert.Contains(lens.Rows, row => row.Name == "Counterspell");

        IReadOnlyList<CardFact> facts = ScryfallCardFactMapper.ToCardFacts(
            entries.Select(entry => new DeckCardEntry
            {
                Card = ScryfallCardDataMapper.ToCardData(cards.Single(card => card.Name == entry.Name)),
                Quantity = entry.Quantity,
                IsCommander = string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase),
            }).ToList());
        ManabaseDeck classified = ManabaseClassifier.Classify(
            facts,
            isSingleton: true,
            rampCreditV2: false,
            landRampSim: false,
            payLifeUntapped: false,
            checkLandUntapped: false,
            restrictedLands: false);

        MethodInfo tagPlanRoles = typeof(ManabaseAnalysisService).GetMethod(
            "TagPlanRolesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task<ManabaseDeck> tagTask = (Task<ManabaseDeck>)tagPlanRoles.Invoke(
            service,
            new object[] { classified, facts, entries, ManabaseMode.Cedh, CancellationToken.None })!;
        ManabaseDeck tagged = await tagTask;
        SpellRequirement counterspell = Assert.Single(tagged.Spells, spell => spell.Name == "Counterspell");
        Assert.False(counterspell.PlanRoles.HasFlag(PlanRole.Interaction));
        Assert.True(counterspell.IsInteractionSpell);
    }

    [Fact]
    public async Task AnalyzeAsync_PlanPresenceFlagsOn_FetchesCategoriesInOneBatch()
    {
        // Regression (Sokka-deck timeout): plan-role tagging fetched each spell's categories in its own
        // sequential DB query — ~65 round-trips on a full decklist, which exhausted the 20s request
        // budget. Tagging must now issue exactly ONE batched lookup regardless of spell count.
        var (entries, cards) = CurveFixture();

        // The permanents-only plan gate means a win-con category only earns a plan role on a PERMANENT —
        // CurveFixture's fillers are all sorceries, so tag a creature so the category flows into a live
        // plan-presence read. (The batched-lookup assertion below is independent of card type.)
        entries.Add(Entry("Plan Beater", 1, "mainboard"));
        cards.Add(Spell("Plan Beater", "{2}{U}", 3, "Creature — Avatar"));

        var store = new FakeCategoryKnowledgeStore();
        store.CategoriesByName["Plan Beater"] = new[] { "Win Condition" };

        var service = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.MulliganEvalFlagKey] = true,
                [ManabaseAnalysisService.PlanPresenceFlagKey] = true,
            }),
            store);

        var result = await service.AnalyzeAsync("paste", "Curve Deck");

        // The whole fix: one batch query, never one-per-card — even though the deck has 60+ spells.
        Assert.Equal(1, store.GetCategoriesForNamesCalls);
        Assert.True(GetResultShowPlanPresence(result));
        // The tagged "Win Condition" category flowed through into a live plan-presence read.
        Assert.NotNull(result.Report.MulliganEvaluation);
        Assert.NotNull(result.Report.MulliganEvaluation!.PlanPresence);
    }

    [Fact]
    public async Task AnalyzeAsync_PlanPresenceFlagOff_DoesNotQueryCategories()
    {
        // The default path must do ZERO category I/O — plan-role tagging is gated behind both the
        // plan-presence and mulligan-eval flags, so a store injected but unflagged is never touched.
        var (entries, cards) = CurveFixture();
        var store = new FakeCategoryKnowledgeStore();

        var service = new ManabaseAnalysisService(
            new FakeLoader(entries), new FakeResolver(cards), featureFlags: null, categoryKnowledge: store);

        var result = await service.AnalyzeAsync("paste", "Curve Deck");

        Assert.Equal(0, store.GetCategoriesForNamesCalls);
        Assert.False(GetResultShowPlanPresence(result));
    }

    [Fact]
    public async Task AnalyzeAsync_DefaultMode_IsCasual()
    {
        var (entries, cards) = CurveFixture();
        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync("paste", null);

        Assert.Equal(ManabaseMode.Casual, result.Report.Mode);
    }

    [Fact]
    public async Task AnalyzeAsync_PlainLanguageFlagOff_LeavesResultNullAndPromptByteIdentical()
    {
        var (entries, cards) = CurveFixture();
        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync("paste", "Curve Deck");

        Assert.Null(GetResultVerdict(result));
        Assert.Null(GetResultBudget(result));
        Assert.False(GetResultShowPlainLanguage(result));

        string expectedPrompt = ManabaseSwapPromptBuilder.Build(
            result.Report, "Curve Deck", BuildDecklistText(entries), result.Report.Mode);
        Assert.Equal(expectedPrompt, result.PromptSwapPrompt);
    }

    [Fact]
    public async Task AnalyzeAsync_CommanderCastabilityFlagOff_LeavesReportAndPromptByteIdentical()
    {
        var (entries, cards) = CommanderBackgroundCompanionFixture();

        var baseline = new ManabaseAnalysisService(
            new FakeLoader(entries, detectedCompanionName: "Jegantha, the Wellspring"),
            new FakeResolver(cards));
        var explicitOff = new ManabaseAnalysisService(
            new FakeLoader(entries, detectedCompanionName: "Jegantha, the Wellspring"),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CommanderCastabilityFlagKey] = false,
            }));

        var baselineResult = await baseline.AnalyzeAsync("paste", "Command Zone Deck");
        var offResult = await explicitOff.AnalyzeAsync("paste", "Command Zone Deck");

        Assert.Equal(
            baselineResult.Report.Castability.Select(FormatCastabilityRow),
            offResult.Report.Castability.Select(FormatCastabilityRow));
        Assert.Equal(baselineResult.Report.AvgOnCurvePercent, offResult.Report.AvgOnCurvePercent);
        Assert.Equal(baselineResult.Report.Health, offResult.Report.Health);
        Assert.Equal(baselineResult.PromptSwapPrompt, offResult.PromptSwapPrompt);
        Assert.False(GetResultCommanderCastabilityEnabled(offResult));
        Assert.Null(GetResultCompanionRow(offResult));
    }

    [Fact]
    public async Task AnalyzeAsync_CommanderCastabilityFlagOn_UsesDesignatorPrecedence_ExcludesCompanionAndKeepsTwoCommanders()
    {
        var (entries, cards) = CommanderBackgroundCompanionFixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries, detectedCompanionName: "Jegantha, the Wellspring"),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CommanderCastabilityFlagKey] = true,
            }));

        var result = await service.AnalyzeAsync(
            "paste",
            "Command Zone Deck",
            new ManabaseAnalysisOptions
            {
                CompanionDesignator = "  Kaheera, the Orphanguard  ",
            });

        CardCastability? companion = GetResultCompanionRow(result);
        Assert.True(GetResultCommanderCastabilityEnabled(result));
        Assert.NotNull(companion);
        Assert.Equal("Kaheera, the Orphanguard", companion!.Name);
        Assert.Equal(6, companion.ManaValue);
        Assert.DoesNotContain(result.Report.Castability, row => row.Name == "Kaheera, the Orphanguard");
        Assert.Equal(2, result.Report.LandTarget!.CommanderCount);
        Assert.Equal(2, result.Report.Castability.Count(row => row.IsCommander));
        Assert.True(
            result.PromptSwapPrompt.Contains("Command-zone castability:", StringComparison.Ordinal),
            result.PromptSwapPrompt);
        Assert.Contains("Companion: Kaheera, the Orphanguard", result.PromptSwapPrompt);
    }

    [Fact]
    public async Task AnalyzeAsync_CommanderCastabilityFlagOn_ManualCompanionResolveFailure_TreatedAsNoCompanion()
    {
        var (entries, cards) = CommanderBackgroundCompanionFixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries, detectedCompanionName: null),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CommanderCastabilityFlagKey] = true,
            }));

        var result = await service.AnalyzeAsync(
            "paste",
            "Command Zone Deck",
            new ManabaseAnalysisOptions
            {
                CompanionDesignator = "Unknown Companion",
            });

        Assert.True(GetResultCommanderCastabilityEnabled(result));
        Assert.Null(GetResultCompanionRow(result));
        Assert.DoesNotContain("Companion:", result.PromptSwapPrompt);
    }

    [Fact]
    public async Task AnalyzeAsync_PlainLanguageFlagOn_Casual_ThreadsVerdictBudgetAndPrompt()
    {
        var (entries, cards) = StrainedCommanderFixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.PlainLanguageVerdictFlagKey] = true,
            }));

        var result = await service.AnalyzeAsync("paste", "Strained Deck");

        Assert.True(GetResultShowPlainLanguage(result));
        Assert.NotNull(GetResultVerdict(result));
        Assert.NotNull(GetResultBudget(result));
        Assert.Contains("Reading the deck", result.PromptSwapPrompt);
    }

    [Fact]
    public async Task AnalyzeAsync_PlainLanguageFlagOn_Cedh_ShowsGlossesOnly()
    {
        var (entries, cards) = StrainedCommanderFixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.PlainLanguageVerdictFlagKey] = true,
            }));

        var result = await service.AnalyzeAsync(
            "paste", "Strained Deck", new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.True(GetResultShowPlainLanguage(result));
        Assert.Null(GetResultVerdict(result));
        Assert.Null(GetResultBudget(result));
        Assert.DoesNotContain("Reading the deck", result.PromptSwapPrompt);
    }

    [Fact]
    public async Task AnalyzeAsync_CedhMode_LowersTargetLands_AndEchoesMode()
    {
        var (entries, cards) = CurveFixture();

        var casual = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));
        var cedh = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var casualResult = await casual.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });
        var cedhResult = await cedh.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.Equal(ManabaseMode.Cedh, cedhResult.Report.Mode);
        Assert.True(
            cedhResult.Report.TargetLands < casualResult.Report.TargetLands,
            $"cEDH target {cedhResult.Report.TargetLands} should be below casual {casualResult.Report.TargetLands}");
    }

    [Fact]
    public async Task AnalyzeAsync_CedhLandTargetFlagOff_KeepsCedhTargetByteIdentical()
    {
        var (entries, cards) = KinnanCedhFixture();
        var provider = new FakeCedhLandBaselineProvider(found: true, mean: 25.7, n: 157);

        var baseline = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            cedhLandBaseline: provider);
        var explicitOff = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = false,
            }),
            cedhLandBaseline: provider);

        var baselineResult = await baseline.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });
        var offResult = await explicitOff.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.Equal(baselineResult.Report.TargetLands, offResult.Report.TargetLands);
    }

    [Fact]
    public async Task AnalyzeAsync_CedhLandTargetFlagOn_Kinnan_LowersCedhTarget()
    {
        var (entries, cards) = KinnanCedhFixture();
        var provider = new FakeCedhLandBaselineProvider(found: true, mean: 25.7, n: 157);

        var off = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = false,
            }),
            cedhLandBaseline: provider);
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = true,
            }),
            cedhLandBaseline: provider);

        var offResult = await off.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });
        var onResult = await on.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.True(onResult.Report.TargetLands < offResult.Report.TargetLands);
    }

    [Fact]
    public async Task AnalyzeAsync_CedhLandTargetFlagOn_CasualMode_Unchanged()
    {
        var (entries, cards) = KinnanCedhFixture();
        var provider = new FakeCedhLandBaselineProvider(found: true, mean: 25.7, n: 157);

        var off = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = false,
            }),
            cedhLandBaseline: provider);
        var on = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = true,
            }),
            cedhLandBaseline: provider);

        var offResult = await off.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });
        var onResult = await on.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { Mode = ManabaseMode.Casual });

        Assert.Equal(offResult.Report.TargetLands, onResult.Report.TargetLands);
    }

    [Fact]
    public async Task AnalyzeAsync_CedhBaselineRangeFields_ArePopulatedWhenProviderReturnsUsableBaseline()
    {
        var (entries, cards) = KinnanCedhFixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = true,
            }),
            cedhLandBaseline: new FakeCedhLandBaselineProvider(found: true, mean: 27.5, n: 33, sd: 1.6, generated: "2026-07"));

        var result = await service.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.NotNull(result.Report);
        Assert.NotNull(result.Report.TargetLandsRangeLow);
        Assert.NotNull(result.Report.TargetLandsRangeHigh);
        Assert.Equal(33, result.Report.BaselineDeckCount);
        Assert.NotNull(result.Report.BaselineLandsMean);
        Assert.NotNull(result.Report.BaselineLandsSd);
        Assert.Equal("2026-07", result.Report.BaselineMonth);
        Assert.Equal(25.9, result.Report.TargetLandsRangeLow.Value, 3);
        Assert.Equal(29.1, result.Report.TargetLandsRangeHigh.Value, 3);
        Assert.Equal(27.5, result.Report.BaselineLandsMean.Value, 3);
        Assert.Equal(1.6, result.Report.BaselineLandsSd.Value, 3);
        Assert.Equal(22.0, result.Report.LandTarget!.CedhSafetyFloor);
        Assert.True(result.Report.LandTarget.CedhBaselineBlended);
        Assert.Equal("Kinnan, Bonder Prodigy", Assert.Single(result.Report.Castability, row => row.IsCommander).Name);
    }

    [Fact]
    public async Task AnalyzeAsync_CedhBaselineRangeFields_StayNullWhenProviderMisses()
    {
        var (entries, cards) = KinnanCedhFixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = true,
            }),
            cedhLandBaseline: new FakeCedhLandBaselineProvider(found: false, mean: 0, n: 0, sd: 0));

        var result = await service.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.NotNull(result.Report);
        Assert.Null(result.Report.TargetLandsRangeLow);
        Assert.Null(result.Report.TargetLandsRangeHigh);
        Assert.Null(result.Report.BaselineDeckCount);
        Assert.Null(result.Report.BaselineLandsMean);
        Assert.Null(result.Report.BaselineLandsSd);
        Assert.Null(result.Report.BaselineMonth);
        Assert.Equal(22.0, result.Report.LandTarget!.CedhSafetyFloor);
        Assert.False(result.Report.LandTarget.CedhBaselineBlended);
        Assert.Equal("Kinnan, Bonder Prodigy", Assert.Single(result.Report.Castability, row => row.IsCommander).Name);
    }

    [Fact]
    public async Task AnalyzeAsync_CedhLandTargetFlagOff_UsesHistoricDisplayFloorWithoutBlend()
    {
        var (entries, cards) = KinnanCedhFixture();
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new FakeResolver(cards),
            new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [ManabaseAnalysisService.CedhLandTargetFlagKey] = false,
            }),
            cedhLandBaseline: new FakeCedhLandBaselineProvider(found: true, mean: 27.5, n: 33, sd: 1.6, generated: "2026-07"));

        var result = await service.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { Mode = ManabaseMode.Cedh });

        Assert.NotNull(result.Report);
        Assert.Equal(28.0, result.Report.LandTarget!.CedhSafetyFloor);
        Assert.False(result.Report.LandTarget.CedhBaselineBlended);
        Assert.Null(result.Report.BaselineMonth);
    }

    // A full ~99-card singleton fixture (so the Karsten regression target sits well above the
    // cEDH floor of 28 and the two modes genuinely differ). 36 lands + 63 distinct spells across
    // a normal curve gives a casual target around the mid-30s; cEDH cuts ~3.5 off it.
    private static (List<DeckEntry> Entries, List<ScryfallCard> Cards) CurveFixture()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Tymna the Weaver", 1, "commander", set: "cmr", cn: "1"),
            Land("Plains", 18),
            Land("Island", 18),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Plains", "W"),
            BasicLand("Island", "U"),
            Spell("Tymna the Weaver", "{1}{W}", 2, "Legendary Creature — Human Cleric"),
        };

        // 63 single-copy spells on a mid curve (avg MV ~3) so the regression is realistic.
        for (int i = 0; i < 63; i++)
        {
            int mv = 2 + (i % 4); // 2,3,4,5 repeating
            string name = $"Filler Spell {i}";
            entries.Add(Entry(name, 1, "mainboard"));
            cards.Add(Spell(name, $"{{{mv - 1}}}{{U}}", mv, "Sorcery"));
        }

        return (entries, cards);
    }

    private static (List<DeckEntry> Entries, List<ScryfallCard> Cards) KinnanCedhFixture()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Kinnan, Bonder Prodigy", 1, "commander"),
            Land("Island", 30),
            Entry("Cheap Spell", 69, "mainboard"),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Island", "U"),
            Spell("Kinnan, Bonder Prodigy", "{G}{U}", 2, "Legendary Creature — Human Druid"),
            Spell("Cheap Spell", "{U}", 1, "Sorcery"),
        };

        return (entries, cards);
    }

    private static (List<DeckEntry> Entries, List<ScryfallCard> Cards) CedhInteractionFixture()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Yoshimaru, Ever Faithful", 1, "commander"),
            Land("Plains", 34),
            Entry("Drannith Magistrate", 1, "mainboard"),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Plains", "W"),
            Spell("Yoshimaru, Ever Faithful", "{W}", 1, "Legendary Creature — Dog"),
            Spell("Drannith Magistrate", "{1}{W}", 2, "Creature — Human Wizard"),
        };

        for (int i = 0; i < 63; i++)
        {
            string name = $"Interaction Filler {i}";
            entries.Add(Entry(name, 1, "mainboard"));
            cards.Add(Spell(name, "{2}{W}", 3, "Sorcery"));
        }

        return (entries, cards);
    }

    private static (List<DeckEntry> Entries, List<ScryfallCard> Cards) CedhInstantInteractionFixture()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Baral, Chief of Compliance", 1, "commander"),
            Land("Island", 34),
            Entry("Counterspell", 1, "mainboard"),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Island", "U"),
            Spell("Baral, Chief of Compliance", "{1}{U}", 2, "Legendary Creature — Human Wizard"),
            Spell("Counterspell", "{U}{U}", 2, "Instant"),
        };

        for (int i = 0; i < 63; i++)
        {
            string name = $"Instant Interaction Filler {i}";
            entries.Add(Entry(name, 1, "mainboard"));
            cards.Add(Spell(name, "{2}{U}", 3, "Sorcery"));
        }

        return (entries, cards);
    }

    private static string BuildDecklistText(IEnumerable<DeckEntry> entries) =>
        string.Join("\n", entries
            .Where(entry => entry.Quantity > 0)
            .Select(entry => $"{entry.Quantity} {entry.Name}"));

    private static (List<DeckEntry> Entries, List<ScryfallCard> Cards) RitualBurstFixture()
    {
        static ScryfallCard Oracle(string name, string cost, double cmc, string type, string oracle) => new(
            Name: name, ManaCost: cost, TypeLine: type, OracleText: oracle,
            Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
            SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
            Layout: "normal", Cmc: cmc, ProducedMana: null, Rarity: "rare");

        var entries = new List<DeckEntry>
        {
            Entry("Yahenni, Undying Partisan", 1, "commander"),
            Land("Swamp", 22),
            Entry("Dark Ritual", 1, "mainboard"),
            Entry("Necropotence", 1, "mainboard"),
        };
        for (int i = 0; i < 75; i++)
        {
            entries.Add(Entry($"Ritual Filler {i}", 1, "mainboard"));
        }

        var cards = new List<ScryfallCard>
        {
            BasicLand("Swamp", "B"),
            Spell("Yahenni, Undying Partisan", "{2}{B}", 3, "Legendary Creature — Aetherborn Vampire"),
            Oracle("Dark Ritual", "{B}", 1, "Instant", "Add {B}{B}{B}."),
            Spell("Necropotence", "{B}{B}{B}", 3, "Enchantment"),
        };
        for (int i = 0; i < 75; i++)
        {
            cards.Add(Spell($"Ritual Filler {i}", "{2}", 2, "Artifact"));
        }

        return (entries, cards);
    }

    [Fact]
    public async Task AnalyzeAsync_CommanderImportance_ThreadsThroughToTheReport()
    {
        // The service must forward options.CommanderImportance to the analyzer. A WU commander on a
        // blue-thin base diverges: Central tightens the commander's blue bar (more under-supported)
        // versus Low. Same deck, only the importance knob differs.
        var (entries, cards) = StrainedCommanderFixture();

        var central = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));
        var low = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var centralResult = await central.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { CommanderImportance = CommanderImportance.Central });
        var lowResult = await low.AnalyzeAsync(
            "paste", null, new ManabaseAnalysisOptions { CommanderImportance = CommanderImportance.Low });

        // Land target is importance-orthogonal — identical regardless of the knob.
        Assert.Equal(centralResult.Report.TargetLands, lowResult.Report.TargetLands);

        var centralBlue = centralResult.Report.ColorFindings.FirstOrDefault(f => f.Color == ManaColor.Blue);
        var lowBlue = lowResult.Report.ColorFindings.FirstOrDefault(f => f.Color == ManaColor.Blue);
        Assert.NotNull(centralBlue);
        Assert.NotNull(lowBlue);
        Assert.True(centralBlue!.UnderSupportedCount >= lowBlue!.UnderSupportedCount,
            "Central must hold the commander's blue to at least as strict a bar as Low");
    }

    // A WU commander with thin blue support so Central vs Low diverges on the blue finding.
    private static (List<DeckEntry> Entries, List<ScryfallCard> Cards) StrainedCommanderFixture()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Brago, King Eternal", 1, "commander"),
            Land("Plains", 24),
            Land("Island", 9),
            Spell("Blue Spell", "{2}{U}", 3, "Sorcery").ToEntry(),
            Spell("White Spell", "{1}{W}", 2, "Sorcery").ToEntry(),
        };
        var cards = new List<ScryfallCard>
        {
            BasicLand("Plains", "W"),
            BasicLand("Island", "U"),
            Spell("Brago, King Eternal", "{2}{W}{U}", 4, "Legendary Creature — Spirit Noble"),
            Spell("Blue Spell", "{2}{U}", 3, "Sorcery"),
            Spell("White Spell", "{1}{W}", 2, "Sorcery"),
        };

        return (entries, cards);
    }

    private static (List<DeckEntry> Entries, List<ScryfallCard> Cards) CommanderBackgroundCompanionFixture()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Wilson, Refined Grizzly", 1, "commander"),
            Entry("Passionate Archaeologist", 1, "commander", category: "Background"),
            Land("Forest", 18),
            Land("Mountain", 18),
            Entry("Kaheera, the Orphanguard", 1, "mainboard"),
            Entry("Cultivate", 1, "mainboard"),
            Entry("Arcane Signet", 1, "mainboard"),
        };
        for (int i = 0; i < 20; i++)
        {
            entries.Add(Entry($"Filler Spell {i}", 1, "mainboard"));
        }

        var cards = new List<ScryfallCard>
        {
            BasicLand("Forest", "G"),
            BasicLand("Mountain", "R"),
            Spell("Wilson, Refined Grizzly", "{1}{G}", 2, "Legendary Creature — Bear Warrior"),
            Spell("Passionate Archaeologist", "{2}{R}", 3, "Legendary Enchantment — Background"),
            Spell("Kaheera, the Orphanguard", "{1}{G/W}{G/W}", 3, "Legendary Creature — Cat Beast"),
            Spell("Jegantha, the Wellspring", "{4}{R/G}", 5, "Legendary Creature — Elemental Elk"),
            Spell("Cultivate", "{2}{G}", 3, "Sorcery"),
            Spell("Arcane Signet", "{2}", 2, "Artifact"),
        };
        for (int i = 0; i < 20; i++)
        {
            cards.Add(Spell($"Filler Spell {i}", "{2}{R}", 3, "Sorcery"));
        }

        return (entries, cards);
    }

    private static ManabaseVerdict? GetResultVerdict(ManabaseAnalysisResult result) =>
        GetOptionalProperty<ManabaseVerdict>(result, "Verdict");

    private static ManabaseRampDrawBudget? GetResultBudget(ManabaseAnalysisResult result) =>
        GetOptionalProperty<ManabaseRampDrawBudget>(result, "Budget");

    private static bool GetResultShowPlainLanguage(ManabaseAnalysisResult result)
    {
        PropertyInfo property = typeof(ManabaseAnalysisResult).GetProperty("ShowPlainLanguage")
            ?? throw new Xunit.Sdk.XunitException("ManabaseAnalysisResult.ShowPlainLanguage property missing.");
        return (bool)(property.GetValue(result) ?? false);
    }

    private static bool GetResultCommanderCastabilityEnabled(ManabaseAnalysisResult result)
    {
        PropertyInfo property = typeof(ManabaseAnalysisResult).GetProperty("CommanderCastabilityEnabled")
            ?? throw new Xunit.Sdk.XunitException("ManabaseAnalysisResult.CommanderCastabilityEnabled property missing.");
        return (bool)(property.GetValue(result) ?? false);
    }

    private static bool GetResultShowTapAnalyzer(ManabaseAnalysisResult result)
    {
        PropertyInfo property = typeof(ManabaseAnalysisResult).GetProperty("ShowTapAnalyzer")
            ?? throw new Xunit.Sdk.XunitException("ManabaseAnalysisResult.ShowTapAnalyzer property missing.");
        return (bool)(property.GetValue(result) ?? false);
    }

    private static bool GetResultShowPlanPresence(ManabaseAnalysisResult result)
    {
        PropertyInfo property = typeof(ManabaseAnalysisResult).GetProperty("ShowPlanPresence")
            ?? throw new Xunit.Sdk.XunitException("ManabaseAnalysisResult.ShowPlanPresence property missing.");
        return (bool)(property.GetValue(result) ?? false);
    }

    private static bool GetResultShowSourceList(ManabaseAnalysisResult result)
    {
        PropertyInfo property = typeof(ManabaseAnalysisResult).GetProperty("ShowSourceList")
            ?? throw new Xunit.Sdk.XunitException("ManabaseAnalysisResult.ShowSourceList property missing.");
        return (bool)(property.GetValue(result) ?? false);
    }

    private static bool GetResultShowCedhInteractionLens(ManabaseAnalysisResult result)
    {
        PropertyInfo property = typeof(ManabaseAnalysisResult).GetProperty("ShowCedhInteractionLens")
            ?? throw new Xunit.Sdk.XunitException("ManabaseAnalysisResult.ShowCedhInteractionLens property missing.");
        return (bool)(property.GetValue(result) ?? false);
    }

    private static CardCastability? GetResultCompanionRow(ManabaseAnalysisResult result) =>
        GetOptionalProperty<CardCastability>(result, "CompanionRow");

    private static T? GetOptionalProperty<T>(object target, string name)
        where T : class
    {
        PropertyInfo property = target.GetType().GetProperty(name)
            ?? throw new Xunit.Sdk.XunitException($"{target.GetType().Name}.{name} property missing.");
        return property.GetValue(target) as T;
    }

    [Fact]
    public async Task AnalyzeAsync_UnresolvedCard_ListedNotThrown()
    {
        var entries = new List<DeckEntry>
        {
            Entry("Commander Guy", 1, "commander"),
            Land("Plains", 1),
            Spell("Swords to Plowshares", "{W}", 1, "Instant").ToEntry(),
            Entry("Totally Made Up Card", 1, "mainboard"),
        };
        var cards = new List<ScryfallCard>
        {
            Spell("Commander Guy", "{2}{W}", 3, "Legendary Creature — Human"),
            BasicLand("Plains", "W"),
            Spell("Swords to Plowshares", "{W}", 1, "Instant"),
        };

        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        var result = await service.AnalyzeAsync("paste", null);

        Assert.Contains("Totally Made Up Card", result.Unresolved);
        Assert.NotNull(result.Report);
    }

    [Fact]
    public async Task AnalyzeAsync_BlankSource_Throws()
    {
        var service = new ManabaseAnalysisService(new FakeLoader(new List<DeckEntry>()), new FakeResolver(new List<ScryfallCard>()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AnalyzeAsync("   ", null));
    }

    [Fact]
    public async Task AnalyzeAsync_OnlySideboard_Throws()
    {
        var entries = new List<DeckEntry> { Entry("Black Lotus", 1, "sideboard") };
        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(new List<ScryfallCard>()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AnalyzeAsync("paste", null));
    }

    [Fact]
    public async Task AnalyzeAsync_OversizeDeckSource_Throws()
    {
        var service = new ManabaseAnalysisService(new FakeLoader(new List<DeckEntry>()), new FakeResolver(new List<ScryfallCard>()));
        string huge = new string('x', 100_001);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AnalyzeAsync(huge, null));
    }

    [Fact]
    public async Task AnalyzeAsync_TooManyCards_Throws()
    {
        var entries = Enumerable.Range(0, 501)
            .Select(i => Entry($"Card {i}", 1, "mainboard"))
            .ToList();
        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(new List<ScryfallCard>()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AnalyzeAsync("paste", null));
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsSuggestions_AndAppliesOverride()
    {
        // Blue is deliberately thin (only 10 Islands in a ~60-card library) so a 5-MV {U}{U}
        // Force of Will is hard to cast on curve — leaving real room for the free override to lift it.
        var entries = new List<DeckEntry>
        {
            Entry("Commander Guy", 1, "commander"),
            Land("Island", 10),
            Spell("Force of Will", "{3}{U}{U}", 5, "Instant").ToEntry(),
        };
        for (int i = 0; i < 50; i++)
        {
            entries.Add(Entry($"Filler {i}", 1, "mainboard"));
        }

        var fow = new ScryfallCard(
            Name: "Force of Will", ManaCost: "{3}{U}{U}", TypeLine: "Instant",
            OracleText: "You may pay 1 life and exile a blue card from your hand rather than pay this spell's mana cost. Counter target spell.",
            Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
            SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
            Layout: "normal", Cmc: 5, ProducedMana: null, Rarity: "rare");
        var cards = new List<ScryfallCard>
        {
            BasicLand("Island", "U"),
            Spell("Commander Guy", "{2}{U}", 3, "Legendary Creature — Human"),
            fow,
        };
        for (int i = 0; i < 50; i++)
        {
            cards.Add(Spell($"Filler {i}", "{2}", 3, "Sorcery"));
        }

        // P3 auto-apply (debug session manabase-too-optimistic): a SELF-ANCHORED free cast ("rather
        // than pay this spell's mana cost") is now auto-applied to the default analysis, so the
        // detect-only path already casts Force of Will at effective MV 0 and marks it overridden — it is
        // surfaced as a suggestion AND applied, no longer a false "demanding" {U}{U} row.
        var detectOnly = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));
        var detect = await detectOnly.AnalyzeAsync("paste", null);
        Assert.Contains(detect.Suggestions, s => s.Name == "Force of Will" && s.EffectiveCost == "0");
        CardCastability before = detect.Report.Castability.Single(c => c.Name == "Force of Will");
        Assert.True(before.IsCostOverridden);   // auto-applied free cost (was: not overridden pre-P3)
        Assert.Equal(0, before.ManaValue);

        // An explicit override to the same "0" is consistent with the auto-applied state: still
        // overridden, still MV 0, and at least as castable (it cannot be made harder).
        var applied = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));
        var withOverride = await applied.AnalyzeAsync(
            "paste", null,
            new ManabaseAnalysisOptions
            {
                CostOverrides = new Dictionary<string, string> { ["Force of Will"] = "0" },
            });
        CardCastability after = withOverride.Report.Castability.Single(c => c.Name == "Force of Will");
        Assert.True(after.IsCostOverridden);
        Assert.Equal(0, after.ManaValue);
        Assert.True(after.CastPercent >= before.CastPercent);
    }

    [Fact]
    public async Task LoadAsync_DetectsSuggestions_WithoutRunningAnalysis()
    {
        // Load mirrors the detect-only half of AnalyzeAsync: it resolves the deck and surfaces the
        // same cost suggestions (Force of Will → 0) plus a card/land summary, but produces no report.
        var entries = new List<DeckEntry>
        {
            Entry("Commander Guy", 1, "commander"),
            Land("Island", 10),
            Spell("Force of Will", "{3}{U}{U}", 5, "Instant").ToEntry(),
        };

        var fow = new ScryfallCard(
            Name: "Force of Will", ManaCost: "{3}{U}{U}", TypeLine: "Instant",
            OracleText: "You may pay 1 life and exile a blue card from your hand rather than pay this spell's mana cost. Counter target spell.",
            Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
            SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
            Layout: "normal", Cmc: 5, ProducedMana: null, Rarity: "rare");
        var cards = new List<ScryfallCard>
        {
            BasicLand("Island", "U"),
            Spell("Commander Guy", "{2}{U}", 3, "Legendary Creature — Human"),
            fow,
        };

        var service = new ManabaseAnalysisService(new FakeLoader(entries), new FakeResolver(cards));

        ManabaseLoadResult result = await service.LoadAsync("paste", CancellationToken.None);

        Assert.Contains(result.Suggestions, s => s.Name == "Force of Will" && s.EffectiveCost == "0");
        Assert.Contains("10 lands", result.InputSummary);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public async Task LoadAsync_BlankSource_Throws()
    {
        var service = new ManabaseAnalysisService(new FakeLoader(new List<DeckEntry>()), new FakeResolver(new List<ScryfallCard>()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadAsync("   "));
    }

    [Fact]
    public async Task ResolveCardsAsync_CacheWarmth_DoesNotChangeCanonicalNameWinner()
    {
        var first = Spell("Shared", "{W}", 1, "Creature", set: "abc", cn: "1");
        var second = Spell("Shared", "{U}", 1, "Creature", set: "abc", cn: "2");
        var entries = new List<DeckEntry>
        {
            Entry("First printing", 1, "mainboard", "abc", "1"),
            Entry("Second printing", 1, "mainboard", "abc", "2"),
        };

        async Task<string?> ResolveWinnerAsync(ScryfallCard? cachedCard)
        {
            var cache = new ScryfallCollectionCardCache();
            if (cachedCard is not null)
            {
                cache.SetPrintingPositive(cachedCard.SetCode!, cachedCard.CollectorNumber!, cachedCard);
            }

            var resolver = new StubResolver([Response(HttpStatusCode.OK, new List<ScryfallCard> { first, second }.Where(card => card != cachedCard).ToList())]);
            var service = new ManabaseAnalysisService(new FakeLoader(entries), resolver, collectionCardCache: cache);
            var index = await InvokeResolveCardsAsync(service, entries);

            Assert.True(index.TryResolve("Shared", null, null, out ScryfallCardData? resolved));
            return resolved!.CollectorNumber;
        }

        string? firstCached = await ResolveWinnerAsync(first);
        string? secondCached = await ResolveWinnerAsync(second);
        string? neitherCached = await ResolveWinnerAsync(null);

        Assert.Equal(neitherCached, firstCached);
        Assert.Equal(neitherCached, secondCached);
        // Why: positions 0 and 1 both resolve to "Shared". Earliest deck position outranks.
        Assert.Equal("1", neitherCached);
    }

    [Fact]
    public async Task ResolveCardsAsync_PartiallyWarmDeckWithAtMostBatchSizeUncached_IssuesOneCollectionPost()
    {
        var entries = Enumerable.Range(0, 76).Select(number => Land($"Card {number}", 1)).ToList();
        var cache = new ScryfallCollectionCardCache();
        cache.SetNamePositive("Card 0", BasicLand("Card 0", "W"));
        var resolver = new StubResolver([
            Response(HttpStatusCode.OK, Enumerable.Range(1, 75).Select(number => BasicLand($"Card {number}", "W")).ToList()),
            // Why: a second response is queued deliberately. Without it a regression issuing 2 POSTs
            // would die on an empty queue; with it the test fails as "Expected: 1, Actual: 2".
            Response(HttpStatusCode.OK, [BasicLand("Card 75", "W")]),
        ]);
        var service = new ManabaseAnalysisService(new FakeLoader(entries), resolver, collectionCardCache: cache);

        await InvokeResolveCardsAsync(service, entries);

        Assert.Equal(1, resolver.CollectionCallCount);
    }

    [Fact]
    public async Task ResolveCardsAsync_UncachedCards_DelegatesCollectionSubmissionToProtocol()
    {
        var entries = new List<DeckEntry> { Land("Plains", 1), Land("Island", 1) };
        var protocol = new RecordingCollectionProtocol([BasicLand("Plains", "W"), BasicLand("Island", "U")]);
        var service = new ManabaseAnalysisService(
            new FakeLoader(entries),
            new ThrowingCollectionResolver(),
            collectionProtocol: protocol);

        await InvokeResolveCardsAsync(service, entries);

        ScryfallCollectionProtocolRequest request = Assert.Single(protocol.Requests);
        Assert.Equal(["Plains", "Island"], request.Identifiers.Select(identifier => identifier.Name));
    }

    [Fact]
    public async Task ResolveCardsAsync_NameDoubleFacedCard_NormalizesAndWarmsFaceCache()
    {
        const string combinedName = "Etali, Primal Conqueror // Etali, Primal Sickness";
        var entries = new List<DeckEntry> { Land(combinedName, 1) };
        var card = BasicLand(combinedName, "R");
        var protocol = new RecordingCollectionProtocol([card]);
        var cache = new ScryfallCollectionCardCache();
        var service = new ManabaseAnalysisService(new FakeLoader(entries), new ThrowingCollectionResolver(), collectionCardCache: cache, collectionProtocol: protocol);

        var index = await InvokeResolveCardsAsync(service, entries);

        Assert.Equal(["Etali, Primal Conqueror"], protocol.Requests.Single().Identifiers.Select(identifier => identifier.Name));
        Assert.True(index.TryResolve(combinedName, null, null, out _));
        Assert.True(cache.TryGetName("Etali, Primal Conqueror", out _));
        await InvokeResolveCardsAsync(service, entries);
        Assert.Single(protocol.Requests);
    }

    [Fact]
    public async Task ResolveCardsAsync_DoubleFacedNameVariants_SubmitsAndPairsOnce()
    {
        const string combinedName = "Etali, Primal Conqueror // Etali, Primal Sickness";
        var entries = new List<DeckEntry>
        {
            Land(combinedName, 1),
            Land("Etali, Primal Conqueror", 1)
        };
        var protocol = new RecordingCollectionProtocol([BasicLand(combinedName, "R")]);
        var cache = new ScryfallCollectionCardCache();
        var service = new ManabaseAnalysisService(new FakeLoader(entries), new ThrowingCollectionResolver(), collectionCardCache: cache, collectionProtocol: protocol);

        var index = await InvokeResolveCardsAsync(service, entries);

        Assert.Equal(["Etali, Primal Conqueror"], protocol.Requests.Single().Identifiers.Select(identifier => identifier.Name));
        Assert.True(index.TryResolve(combinedName, null, null, out _));
        Assert.True(index.TryResolve("Etali, Primal Conqueror", null, null, out _));
        Assert.True(cache.TryGetName("Etali, Primal Conqueror", out _));
    }

    [Fact]
    public async Task ResolveCardsAsync_PartiallyWarmDeck_SubmitsOnlyUncachedIdentifiers()
    {
        var entries = new List<DeckEntry> { Land("Cached", 1), Land("Uncached One", 1), Land("Uncached Two", 1) };
        var cache = new ScryfallCollectionCardCache();
        cache.SetNamePositive("Cached", BasicLand("Cached", "W"));
        var resolver = new StubResolver([Response(HttpStatusCode.OK, [BasicLand("Uncached One", "U"), BasicLand("Uncached Two", "B")])]);
        var service = new ManabaseAnalysisService(new FakeLoader(entries), resolver, collectionCardCache: cache);

        await InvokeResolveCardsAsync(service, entries);

        string?[] submittedNames = SubmittedIdentifierNames(resolver);
        Assert.Equal(2, submittedNames.Length);
        Assert.Contains("Uncached One", submittedNames);
        Assert.Contains("Uncached Two", submittedNames);
        Assert.DoesNotContain("Cached", submittedNames);
    }

    [Fact]
    public async Task ResolveCardsAsync_MultiChunkPositionedWinnerIsInvariantToCacheWarmth()
    {
        var entries = Enumerable.Range(0, 76).Select(number => Entry($"Request {number}", 1, "mainboard", "abc", number.ToString())).ToList();
        var cards = Enumerable.Range(0, 76).Select(number => Spell(number is 0 or 75 ? "Shared" : $"Card {number}", "{W}", 1, "Creature", set: "abc", cn: number.ToString())).ToList();

        async Task<string?> ResolveWinnerAsync(bool cacheLater, bool cacheAll)
        {
            var cache = new ScryfallCollectionCardCache();
            if (cacheAll)
            {
                foreach (var card in cards)
                {
                    cache.SetPrintingPositive(card.SetCode!, card.CollectorNumber!, card);
                }
            }
            else if (cacheLater)
            {
                var later = cards[75];
                cache.SetPrintingPositive(later.SetCode!, later.CollectorNumber!, later);
            }

            var responses = cacheAll
                ? Array.Empty<Func<RestRequest, Task<RestResponse<ScryfallCollectionResponse>>>>()
                : cacheLater
                    ? [Response(HttpStatusCode.OK, cards.Take(75).ToList())]
                    : [Response(HttpStatusCode.OK, cards.Take(75).ToList()), Response(HttpStatusCode.OK, cards.Skip(75).ToList())];
            var service = new ManabaseAnalysisService(new FakeLoader(entries), new StubResolver(responses), collectionCardCache: cache);
            var index = await InvokeResolveCardsAsync(service, entries);

            Assert.True(index.TryResolve("Shared", null, null, out ScryfallCardData? resolved));
            return resolved!.CollectorNumber;
        }

        // Why: "Shared" sits at deck positions 0 and 75. Earliest position outranks, so the
        // winner is cn 0 under every cache-warmth shape -- the index decides from the priority the
        // caller states, never from which chunk happened to deliver the card first.
        Assert.Equal("0", await ResolveWinnerAsync(cacheLater: false, cacheAll: false));
        Assert.Equal("0", await ResolveWinnerAsync(cacheLater: true, cacheAll: false));
        Assert.Equal("0", await ResolveWinnerAsync(cacheLater: false, cacheAll: true));
    }

    [Fact]
    public async Task ResolveCardsAsync_EarliestDeckPosition_BeatsTheBetterPrintingKey()
    {
        // Why: both entries resolve to "Shared", and the printing tiebreak would pick abc|1 -- the
        // LATER deck position. Only the caller-stated position priority yields abc|9, so this test
        // fails the moment the positional priority is dropped.
        var entries = new List<DeckEntry>
        {
            Entry("Early", 1, "mainboard", "abc", "9"),
            Entry("Late", 1, "mainboard", "abc", "1"),
        };
        var cards = new List<ScryfallCard>
        {
            Spell("Shared", "{W}", 1, "Creature", set: "abc", cn: "9"),
            Spell("Shared", "{U}", 1, "Creature", set: "abc", cn: "1"),
        };
        var service = new ManabaseAnalysisService(new FakeLoader(entries), new StubResolver([Response(HttpStatusCode.OK, cards)]));

        var index = await InvokeResolveCardsAsync(service, entries);

        Assert.True(index.TryResolve("Shared", null, null, out ScryfallCardData? resolved));
        Assert.Equal("9", resolved!.CollectorNumber);
    }

    [Fact]
    public async Task ResolveCardsAsync_UnpairedCard_LosesToPairedCard()
    {
        var entries = Enumerable.Range(0, 75)
            .Select(number => Entry($"Filler {number}", 1, "mainboard"))
            .Append(Entry("Shared", 1, "mainboard"))
            .ToList();
        var firstChunk = Enumerable.Range(0, 75)
            .Select(number => Spell($"Filler {number}", "{W}", 1, "Creature", cn: number.ToString()))
            .Append(Spell("Shared", "{W}", 1, "Creature", set: "abc", cn: "1"))
            .ToList();
        var responses = new[]
        {
            Response(HttpStatusCode.OK, firstChunk),
            Response(HttpStatusCode.OK, [Spell("Shared", "{W}", 1, "Creature", set: "abc", cn: "99")]),
        };
        var service = new ManabaseAnalysisService(new FakeLoader(entries), new StubResolver(responses));

        var index = await InvokeResolveCardsAsync(service, entries);

        // Why: chunk 1 returns a "Shared" (abc|1) that matched no submission, so it is unpaired.
        // Chunk 2 submits "Shared" and pairs abc|99 to it. The printing tiebreak would pick the
        // UNPAIRED card, so only the paired-beats-unpaired priority band yields cn 99 -- drop that
        // band and this test fails.
        Assert.True(index.TryResolve("Shared", null, null, out ScryfallCardData? resolved));
        Assert.Equal("99", resolved!.CollectorNumber);
    }

    [Fact]
    public void PriorityBands_RankSearchFallbackAboveUnpairedAboveFloor()
    {
        // Why: the two ResolveCardsAsync collision tests pin the paired band and the direction of
        // the comparison, but neither observes a band's VALUE -- dropping UnpairedPriority to the
        // 0 floor still loses to paired, so both stay green. Fallback-beats-unpaired is a product
        // decision (a search-fallback card matched an entry we asked for; an unpaired card is one
        // Scryfall volunteered against no submission), and nothing else fails if it inverts.
        int searchFallback = PriorityBand("SearchFallbackPriority");
        int unpaired = PriorityBand("UnpairedPriority");

        Assert.True(searchFallback > unpaired, $"SearchFallbackPriority ({searchFallback}) must outrank UnpairedPriority ({unpaired}).");
        Assert.True(unpaired > 0, $"UnpairedPriority ({unpaired}) must outrank the default 0 floor.");
        Assert.True(PairedBand(0) > searchFallback, "The paired band must outrank every stated band.");
        Assert.True(PairedBand(499) > searchFallback, "The paired band must outrank every stated band at the 500-card deck cap.");
    }

    // --- helpers -------------------------------------------------------------

    // Why: ResolveCardsAsync is private; every test that needs the batch/cache path directly goes
    // through this one seam rather than repeating the reflection boilerplate.
    private static Task<ScryfallCardNameIndex> InvokeResolveCardsAsync(ManabaseAnalysisService service, IReadOnlyList<DeckEntry> entries)
    {
        MethodInfo method = typeof(ManabaseAnalysisService).GetMethod("ResolveCardsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<ScryfallCardNameIndex>)method.Invoke(service, [entries, CancellationToken.None])!;
    }

    // Why: the bands are private consts on the service. Reflecting them is weaker than a behavioral
    // test, but fallback-vs-unpaired is only observable through a Scryfall response shape the
    // resolver does not produce, so pinning the decision beats leaving the ordering unguarded.
    private static int PriorityBand(string name) =>
        (int)typeof(ManabaseAnalysisService).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;

    private static int PairedBand(int globalPosition) =>
        (int)typeof(ManabaseAnalysisService).GetMethod("PairedPriority", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [globalPosition])!;

    private static string?[] SubmittedIdentifierNames(StubResolver resolver)
    {
        string payload = System.Text.Json.JsonSerializer.Serialize(resolver.Requests.Single().Parameters.Single(parameter => parameter.Type == ParameterType.RequestBody).Value);
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("identifiers").EnumerateArray().Select(identifier => identifier.GetProperty("name").GetString()).ToArray();
    }


    private static string FormatCastabilityRow(CardCastability row)
        => $"{row.Name}|{row.ManaValue}|{row.OnCurveTurn}|{row.CastPercent}|{row.IsCommander}";

    [Fact]
    public async Task AnalyzeAsync_SharedCollectionCache_SuppressesSecondCollectionPost()
    {
        var entries = new List<DeckEntry> { Land("Plains", 1) };
        var resolver = new CountingResolver([BasicLand("Plains", "W")]);
        var cache = new ScryfallCollectionCardCache();
        var service = new ManabaseAnalysisService(new FakeLoader(entries), resolver, collectionCardCache: cache);

        await service.AnalyzeAsync("paste", "first");
        await service.AnalyzeAsync("paste", "second");

        Assert.Equal(1, resolver.CollectionCallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_FullyCachedChunk_ResolvesWithoutCollectionOrFallbackRequests()
    {
        var entries = new List<DeckEntry> { Land("Plains", 1), Land("Island", 1) };
        var resolver = new CountingResolver([]);
        var cache = new ScryfallCollectionCardCache();
        cache.SetNamePositive("Plains", BasicLand("Plains", "W"));
        cache.SetNamePositive("Island", BasicLand("Island", "U"));
        var service = new ManabaseAnalysisService(new FakeLoader(entries), resolver, collectionCardCache: cache);

        var result = await service.AnalyzeAsync("paste", "cached");

        Assert.Empty(result.Unresolved);
        Assert.Equal(0, resolver.CollectionCallCount);
        Assert.Equal(0, resolver.FallbackCallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_SharedCollectionCache_UsesPrintingNamespaceWithoutCrossPopulatingName()
    {
        var card = Spell("Canonical Name", "{W}", 1, "Creature", set: "abc", cn: "1");
        var resolver = new CountingResolver([card]);
        var cache = new ScryfallCollectionCardCache();

        await new ManabaseAnalysisService(new FakeLoader([Entry("Flavor Name", 1, "mainboard", "abc", "1")]), resolver, collectionCardCache: cache)
            .AnalyzeAsync("paste", "printing");
        Assert.False(cache.TryGetName("Canonical Name", out _));
        await new ManabaseAnalysisService(new FakeLoader([Entry("Canonical Name", 1, "mainboard")]), resolver, collectionCardCache: cache)
            .AnalyzeAsync("paste", "name");

        Assert.Equal(2, resolver.CollectionCallCount);
        Assert.True(cache.TryGetPrinting("abc", "1", out var printing));
        Assert.NotNull(printing);
    }

    [Fact]
    public async Task AnalyzeAsync_CachedNameCollectionMiss_StillUsesFallback()
    {
        var card = BasicLand("Fallback Plains", "W");
        var resolver = new CountingResolver([card]);
        var cache = new ScryfallCollectionCardCache();
        cache.SetNameCollectionMiss("Fallback Plains");
        var service = new ManabaseAnalysisService(new FakeLoader([Land("Fallback Plains", 1)]), resolver, collectionCardCache: cache);

        await service.AnalyzeAsync("paste", "fallback");

        Assert.Equal(0, resolver.CollectionCallCount);
        Assert.Equal(1, resolver.FallbackCallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnalyzeAsync_FailedCollectionChunk_DoesNotCachePartialResponse(HttpStatusCode statusCode)
    {
        var cache = new ScryfallCollectionCardCache();
        var first = BasicLand("Saved", "W");
        var leaked = BasicLand("Leaked", "U");
        var entries = Enumerable.Range(0, 75).Select(i => Land(i == 0 ? "Saved" : $"First {i}", 1))
            .Append(Land("Leaked", 1)).Append(Land("Missing", 1)).ToList();
        var resolver = new StubResolver([
            Response(HttpStatusCode.OK, [first]),
            Response(statusCode, [leaked], [new ScryfallCollectionIdentifier("Missing")])]);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new ManabaseAnalysisService(new FakeLoader(entries), resolver, collectionCardCache: cache).AnalyzeAsync("paste", "failure"));

        Assert.Equal(statusCode, error.StatusCode);
        Assert.Equal($"Scryfall card lookup (cards/collection) returned HTTP {(int)statusCode} during mana-base analysis.", error.Message);
        Assert.True(cache.TryGetName("Saved", out var saved));
        Assert.NotNull(saved);
        Assert.False(cache.TryGetName("Leaked", out _));
        Assert.False(cache.TryGetName("Missing", out _));
    }

    [Fact]
    public async Task AnalyzeAsync_NotFoundEcho_CachesOnlyTheEchoedNameMiss()
    {
        var cache = new ScryfallCollectionCardCache();
        var resolver = new StubResolver([Response(HttpStatusCode.OK, [BasicLand("Found", "W")], [new ScryfallCollectionIdentifier("Missing")]), Response(HttpStatusCode.OK, [BasicLand("Found", "W")])]);
        await new ManabaseAnalysisService(new FakeLoader([Land("Found", 1), Land("Missing", 1)]), resolver, collectionCardCache: cache).AnalyzeAsync("paste", "one");
        await new ManabaseAnalysisService(new FakeLoader([Land("Found", 1), Land("Missing", 1)]), resolver, collectionCardCache: cache).AnalyzeAsync("paste", "two");
        Assert.Equal(1, resolver.CollectionCallCount);
        Assert.True(cache.TryGetName("Missing", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public async Task AnalyzeAsync_UnreturnedNameWithoutNotFoundEcho_IsNotCached()
    {
        var cache = new ScryfallCollectionCardCache();
        var resolver = new StubResolver([Response(HttpStatusCode.OK, [BasicLand("Found", "W")]), Response(HttpStatusCode.OK, [BasicLand("Found", "W")])]);
        var service = new ManabaseAnalysisService(new FakeLoader([Land("Found", 1), Land("Unknown", 1)]), resolver, collectionCardCache: cache);
        await service.AnalyzeAsync("paste", "one");
        await service.AnalyzeAsync("paste", "two");
        Assert.Equal(2, resolver.CollectionCallCount);
        Assert.False(cache.TryGetName("Unknown", out _));
    }

    [Fact]
    public async Task AnalyzeAsync_NullOrThrownCollectionResponse_DoesNotAlterExistingCache()
    {
        var cache = new ScryfallCollectionCardCache();
        cache.SetNamePositive("Saved", BasicLand("Saved", "W"));
        foreach (var response in new[] { Response(HttpStatusCode.OK, null), ThrowingResponse() })
        {
            var resolver = new StubResolver([response]);
            await Assert.ThrowsAsync<HttpRequestException>(() => new ManabaseAnalysisService(new FakeLoader([Land("Unknown", 1)]), resolver, collectionCardCache: cache).AnalyzeAsync("paste", "failure"));
            Assert.True(cache.TryGetName("Saved", out var saved));
            Assert.NotNull(saved);
            Assert.False(cache.TryGetName("Unknown", out _));
        }
    }

    [Fact]
    public async Task AnalyzeAsync_AmbiguousCollectionMatches_AreNotCached()
    {
        var cache = new ScryfallCollectionCardCache();
        var resolver = new StubResolver([Response(HttpStatusCode.OK, [Spell("Same", "{W}", 1, "Creature", set: "a", cn: "1"), Spell("Same", "{U}", 1, "Creature", set: "b", cn: "2"), Spell("Other", "{B}", 1, "Creature", set: "p", cn: "9"), Spell("Different", "{R}", 1, "Creature", set: "p", cn: "9"), Spell("No Print", "{G}", 1, "Creature"), Spell("Unique", "{G}", 1, "Creature")])]);
        await new ManabaseAnalysisService(new FakeLoader([Land("Same", 1), Entry("Request", 1, "mainboard", "p", "9"), Land("No Print", 1), Land("Unique", 1)]), resolver, collectionCardCache: cache).AnalyzeAsync("paste", "ambiguous");
        Assert.False(cache.TryGetName("Same", out _));
        Assert.False(cache.TryGetPrinting("p", "9", out _));
        Assert.False(cache.TryGetPrinting("", "", out _));
        Assert.True(cache.TryGetName("Unique", out var unique));
        Assert.NotNull(unique);
    }

    [Fact]
    public async Task AnalyzeAsync_PartialCache_SubmitsOnlyTheUncachedIdentifier()
    {
        var cache = new ScryfallCollectionCardCache();
        cache.SetNamePositive("Cached", BasicLand("Cached", "W"));
        var resolver = new StubResolver([Response(HttpStatusCode.OK, [BasicLand("Uncached", "U")])]);
        await new ManabaseAnalysisService(new FakeLoader([Land("Cached", 1), Land("Uncached", 1)]), resolver, collectionCardCache: cache).AnalyzeAsync("paste", "partial");
        Assert.Equal(1, resolver.CollectionCallCount);
        string payload = System.Text.Json.JsonSerializer.Serialize(resolver.Requests.Single().Parameters.Single(parameter => parameter.Type == ParameterType.RequestBody).Value);
        Assert.Contains("Uncached", payload);
        Assert.DoesNotContain("Cached", payload);
    }

    /// <summary>
    /// A deck entry written in the raw <c>A // B</c> form shares the front face's cache key.
    /// Why: Scryfall's <c>cards/collection</c> name identifier resolves a front-face name to the
    /// double-faced card and rejects the combined form outright, so the front face is the only
    /// identifier that can ever be submitted for this card -- which makes a warm front-face entry
    /// the correct answer for a combined-form entry, and makes the combined form unreachable as a key.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_RawDoubleFaceName_SharesTheFrontFaceCacheKey()
    {
        var cache = new ScryfallCollectionCardCache();
        cache.SetNamePositive("A", BasicLand("A", "W"));
        // Why: the queued response is the same card, so a regression surfaces as an unexpected
        // call count rather than as a queue-exhaustion exception.
        var resolver = new StubResolver([Response(HttpStatusCode.OK, [BasicLand("A", "W")])]);

        await new ManabaseAnalysisService(new FakeLoader([Land("A // B", 1)]), resolver, collectionCardCache: cache)
            .AnalyzeAsync("paste", "double-face");

        // Why: a one-card deck fails Commander validation, so this asserts the cache contract only.
        // Index resolution through the shared key is covered by
        // ResolveCardsAsync_NameDoubleFacedCard_NormalizesAndWarmsFaceCache.
        Assert.Equal(0, resolver.CollectionCallCount);
        Assert.True(cache.TryGetName("A", out var front));
        Assert.NotNull(front);
        Assert.False(cache.TryGetName("A // B", out _));
    }

    private static DeckEntry Entry(string name, int qty, string board, string? set = null, string? cn = null, string? category = null) => new()
    {
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        Quantity = qty,
        Board = board,
        SetCode = set,
        CollectorNumber = cn,
        Category = category,
    };

    private static DeckEntry Land(string name, int qty) => Entry(name, qty, "mainboard");

    private static ScryfallCard BasicLand(string name, string color) => new(
        Name: name, ManaCost: null, TypeLine: $"Basic Land — {name}", OracleText: null,
        Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
        SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
        Layout: "normal", Cmc: 0, ProducedMana: new[] { color }, Rarity: "common");

    private static ScryfallCard LandOracle(string name, string typeLine, string oracle, params string[] produced) => new(
        Name: name, ManaCost: null, TypeLine: typeLine, OracleText: oracle,
        Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
        SetCode: null, SetName: null, CollectorNumber: null, CardFaces: null, Id: null,
        Layout: "normal", Cmc: 0, ProducedMana: produced, Rarity: "rare");

    private static ScryfallCard Spell(string name, string manaCost, double cmc, string typeLine, string? oracle = null, string? set = null, string? cn = null) => new(
        Name: name, ManaCost: manaCost, TypeLine: typeLine, OracleText: oracle ?? "...",
        Power: null, Toughness: null, Keywords: null, ColorIdentity: null,
        SetCode: set, SetName: null, CollectorNumber: cn, CardFaces: null, Id: null,
        Layout: "normal", Cmc: cmc, ProducedMana: null, Rarity: "rare");

    private static Func<RestRequest, Task<RestResponse<ScryfallCollectionResponse>>> Response(HttpStatusCode statusCode, List<ScryfallCard>? cards, List<ScryfallCollectionIdentifier>? notFound = null)
        => request => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
        {
            StatusCode = statusCode,
            Data = cards is null ? null : new ScryfallCollectionResponse(cards, notFound),
        });

    private static Func<RestRequest, Task<RestResponse<ScryfallCollectionResponse>>> ThrowingResponse()
        => _ => Task.FromException<RestResponse<ScryfallCollectionResponse>>(new HttpRequestException("Scryfall request failed"));

    private sealed class RecordingCollectionProtocol : IScryfallCollectionProtocol
    {
        private readonly IReadOnlyList<ScryfallCard> _cards;

        public RecordingCollectionProtocol(IReadOnlyList<ScryfallCard> cards)
        {
            _cards = cards;
        }

        public List<ScryfallCollectionProtocolRequest> Requests { get; } = [];

        public Task<ScryfallCollectionProtocolResponse> ResolveAsync(
            ScryfallCollectionProtocolRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ScryfallCollectionProtocolResponse(HttpStatusCode.OK, _cards, [], HasPayload: true));
        }
    }

    private sealed class ThrowingCollectionResolver : IScryfallCardResolver
    {
        public Task<RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestRequest request, CancellationToken cancellationToken)
            => throw new Xunit.Sdk.XunitException("ManabaseAnalysisService must delegate collection requests to IScryfallCollectionProtocol.");

        public Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);
    }

    private sealed class StubResolver : IScryfallCardResolver
    {
        private readonly Queue<Func<RestRequest, Task<RestResponse<ScryfallCollectionResponse>>>> _responses;

        public StubResolver(IEnumerable<Func<RestRequest, Task<RestResponse<ScryfallCollectionResponse>>>> responses)
            => _responses = new Queue<Func<RestRequest, Task<RestResponse<ScryfallCollectionResponse>>>>(responses);

        public int CollectionCallCount { get; private set; }

        public List<RestRequest> Requests { get; } = [];

        public Task<RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestRequest request, CancellationToken cancellationToken)
        {
            CollectionCallCount++;
            Requests.Add(request);
            return _responses.Dequeue()(request);
        }

        public Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken) => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken) => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken) => Task.FromResult<ScryfallCard?>(null);
    }

    private sealed class FakeLoader : IDeckEntryLoader
    {
        private readonly List<DeckEntry> _entries;
        private readonly string? _detectedCompanionName;

        public FakeLoader(List<DeckEntry> entries, string? detectedCompanionName = null)
        {
            _entries = entries;
            _detectedCompanionName = detectedCompanionName;
        }

        public Task<DeckSourceLoadResult> LoadFromSourceAsync(
            string deckSource,
            UnrecognizedPasteBehavior unrecognizedBehavior = UnrecognizedPasteBehavior.ThrowNotRecognized,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DeckSourceLoadResult(_entries, null, _detectedCompanionName));

        public Task<List<DeckEntry>> LoadAsync(DeckLoadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void ValidateCommanderDeckSize(string systemName, IReadOnlyList<DeckEntry> entries, int requiredDeckSize = 100)
        {
        }
    }

    private sealed class FakeResolver : IScryfallCardResolver
    {
        private readonly List<ScryfallCard> _cards;

        public FakeResolver(List<ScryfallCard> cards) => _cards = cards;

        public Task<RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCollectionResponse(_cards, null),
            });

        public Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult(_cards.FirstOrDefault(card => string.Equals(card.Name, cardName, StringComparison.OrdinalIgnoreCase)));

        public Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken)
            => Task.FromResult(_cards.FirstOrDefault(card => string.Equals(card.Name, cardName, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class CountingResolver : IScryfallCardResolver
    {
        private readonly IReadOnlyList<ScryfallCard> _cards;

        public CountingResolver(IReadOnlyList<ScryfallCard> cards) => _cards = cards;

        public int CollectionCallCount { get; private set; }

        public int FallbackCallCount { get; private set; }

        public Task<RestResponse<ScryfallCollectionResponse>> ExecuteCollectionAsync(RestRequest request, CancellationToken cancellationToken)
        {
            CollectionCallCount++;
            return Task.FromResult(new RestResponse<ScryfallCollectionResponse>(request)
            {
                StatusCode = HttpStatusCode.OK,
                Data = new ScryfallCollectionResponse(_cards.ToList(), null),
            });
        }

        public Task<ScryfallCard?> SearchFallbackCardAsync(string cardName, CancellationToken cancellationToken)
        {
            FallbackCallCount++;
            return Task.FromResult(_cards.FirstOrDefault(card => string.Equals(card.Name, cardName, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<ScryfallCard?> SearchPrintingFallbackCardAsync(string cardName, CancellationToken cancellationToken) => Task.FromResult<ScryfallCard?>(null);

        public Task<ScryfallCard?> ResolveSingleAsync(string cardName, CancellationToken cancellationToken) => Task.FromResult<ScryfallCard?>(null);
    }

    private sealed class FakeCedhLandBaselineProvider : ICedhLandBaselineProvider
    {
        private readonly bool _found;
        private readonly double _mean;
        private readonly int _n;
        private readonly double _sd;
        private readonly string? _generated;

        public FakeCedhLandBaselineProvider(bool found, double mean, int n, double sd = 0, string? generated = null)
        {
            _found = found;
            _mean = mean;
            _n = n;
            _sd = sd;
            _generated = generated;
        }

        public void EnsureLoaded()
        {
        }

        public bool TryGetBaseline(IReadOnlyList<string> commanderNames, out double mean, out int n, out double sd, out string? generated)
        {
            mean = _mean;
            n = _n;
            sd = _sd;
            generated = _generated;
            return _found;
        }
    }
}

public sealed class ManabaseControllerCompanionTests
{
    [Fact]
    public async Task Post_ThreadsCompanionDesignator_AndMapsCommanderCastabilityFields()
    {
        var companion = new CardCastability
        {
            Name = "Kaheera, the Orphanguard",
            ManaValue = 6,
            OnCurveTurn = 6,
            CastPercent = 55,
            LimitingFactor = "curve",
        };
        var service = new CapturingControllerService(ManabaseControllerModeTestsAccessor.CasualReport(), companion, commanderCastabilityEnabled: true);
        var controller = BuildController(service);

        var result = await controller.Manabase(new ManabaseRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Kaheera, the Orphanguard",
            CompanionName = " Kaheera, the Orphanguard ",
        });

        Assert.NotNull(service.LastOptions);
        Assert.Equal(" Kaheera, the Orphanguard ", service.LastOptions!.CompanionDesignator);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ManabaseViewModel>(view.Model);
        Assert.True(model.ShowCommanderCastability);
        Assert.Same(companion, model.CompanionCallout);
    }

    private static ManabaseController BuildController(IManabaseAnalysisService service)
    {
        var controller = new ManabaseController(
            service,
            new StubCardSearchService(),
            new FakeFeatureFlagCache(),
            new FakeBracketClassificationService(),
            NullLogger<ManabaseController>.Instance,
            new PacketSessionCache())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    private sealed class CapturingControllerService : IManabaseAnalysisService
    {
        private readonly ManabaseReport _report;
        private readonly CardCastability? _companionRow;
        private readonly bool _commanderCastabilityEnabled;

        public CapturingControllerService(
            ManabaseReport report,
            CardCastability? companionRow,
            bool commanderCastabilityEnabled)
        {
            _report = report;
            _companionRow = companionRow;
            _commanderCastabilityEnabled = commanderCastabilityEnabled;
        }

        public ManabaseAnalysisOptions? LastOptions { get; private set; }

        public Task<ManabaseAnalysisResult> AnalyzeAsync(
            string deckSource,
            string? deckName,
            ManabaseAnalysisOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options ?? new ManabaseAnalysisOptions();
            return Task.FromResult(new ManabaseAnalysisResult(
                _report,
                "1 cards · 36 lands",
                Array.Empty<string>(),
                null,
                "prompt",
                Array.Empty<CostSuggestion>(),
                null,
                null,
                false)
            {
                CommanderCastabilityEnabled = _commanderCastabilityEnabled,
                CompanionRow = _companionRow,
            });
        }

        public Task<ManabaseLoadResult> LoadAsync(
            string deckSource,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ManabaseLoadResult(
                "1 cards · 36 lands", Array.Empty<string>(), null, Array.Empty<CostSuggestion>()));
    }

    private static class ManabaseControllerModeTestsAccessor
    {
        public static ManabaseReport CasualReport() => new()
        {
            ActualLands = 36,
            TargetLands = 37.0,
            ColorFindings = Array.Empty<ColorSourceFinding>(),
            Mode = ManabaseMode.Casual,
            Castability = new[]
            {
                new CardCastability { Name = "Counterspell", ManaValue = 2, OnCurveTurn = 2, CastPercent = 62, LimitingFactor = "color:U" },
            },
            Summary = "ok",
        };
    }
}

internal static class ManabaseTestExtensions
{
    // Build a mainboard entry whose name matches a spell card, for terse arrange blocks.
    public static DeckEntry ToEntry(this ScryfallCard card) => new()
    {
        Name = card.Name,
        NormalizedName = card.Name.ToLowerInvariant(),
        Quantity = 1,
        Board = "mainboard",
    };
}
