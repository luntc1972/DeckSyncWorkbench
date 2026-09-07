using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DeckFlow.Core.Manabase;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Manabase;
using Xunit;

namespace DeckFlow.Web.Tests.Manabase;

/// <summary>
/// Regression guard for the health-band/castability coupling fix (debug session
/// manabase-health-band-coupling, Gate C). The Avatar (Sokka/Aang Jeskai) fixture is the
/// calibration deck, read directly from a committed facts fixture (no HTTP) so it runs in CI.
///
/// The Avatar deck reads "Solid" both flag-off and flag-on with no weak color: no color is source-
/// limited, but a cheap colored spell casts just under the bar on curve, an honest minor note that
/// keeps it out of "Excellent" (and well clear of "needs work"). It previously read Solid/Workable because three
/// "Noncreature spells you cast cost {1} less" cards (Gran-Gran, Longshot, Lyse Hext) were
/// mis-scoped as CREATURE reducers — the substring check read "noncreature" as "creature" — which
/// fictitiously discounted every creature by up to {2}, pulling their on-curve turns earlier and
/// depressing castability. With word-boundary scope matching those reducers are correctly dropped
/// (no Noncreature scope exists), the creatures cast at their true turns, and White is no longer
/// source-limited. The flag-on color-issue coupling (Gate C) that Avatar used to exercise
/// end-to-end is fully guarded by the synthetic SyntheticReport tests below; no real fixture sits
/// at that boundary post-recalibration.
/// </summary>
public sealed class ManabaseHealthBandRegressionTests
{
    // Path to the committed Avatar facts fixture (copied next to the test assembly via the
    // csproj Content item, so it is present in CI with no network dependency). Re-derives
    // ManaAmount from OracleText on load (same as the baseline harness) to survive older
    // cache formats.
    private static readonly string FactsCachePath =
        Path.Combine(AppContext.BaseDirectory, "Manabase", "avatar-facts.json");

    private static readonly IReadOnlyList<CalibrationDeck> CalibrationDecks =
    [
        // Labels re-baselined when the sim started drawing on turn 1 (Commander is multiplayer, so the
        // starting player draws their first turn — see CastabilitySimulator). The extra card per game
        // lifts castability, so Stale Brago's floor promotes, Meren clears to Excellent, and army-now
        // rises out of the red band.
        //
        // Re-baselined again for efficacy R2 H1/H2 (classifier correctness): taplands with the live
        // "enters tapped" wording now classify tapped, and produced_mana-only producers
        // (Treasure-makers, one-shot sac mana, sac-outlets) no longer count as permanent sources.
        // Both remove phantom optimism: Meren (ritual/Treasure sources gone) drops Excellent →
        // Solid, and army-now (worst color 46% once its phantom sources vanish) dropped Solid →
        // Needs work.
        //
        // Re-baselined a THIRD time for efficacy R2 M1/M2 (sim realism): the London mulligan no
        // longer redraws the exact card it just bottomed (M1), and slack turns now develop a tapped
        // fixer over a color-useless untapped land (M2). Both raise real castability on tapland/fixing
        // decks. army-now recovers Needs work → Solid: M2's better tapland sequencing turns its
        // tightest color from a deficit into a surplus (MaxColorDeficit -4.8, ColorLimitedUnderSupported
        // 0), so no color issue fires; its 46% worst-color spell is mana/curve-limited, not a fixing
        // gap. No other calibration deck changes band.
        //
        // Re-baselined a FOURTH time for efficacy R2 M5 (reducer scope correctness): "Noncreature
        // spells you cast cost {1} less" is no longer mis-read as a CREATURE reducer (word-boundary
        // scope match; no Noncreature scope exists → dropped). Only the Avatar deck holds such cards
        // (3 of them); removing the fictitious deck-wide creature discount lets its creatures cast at
        // their true turns, so Avatar rises Solid/Workable → Excellent/Excellent with no weak color.
        // No other calibration deck contains a noncreature reducer, so none else changes band.
        //
        // Re-baselined a FIFTH time for the keep-band + under-support-consistency fix (field report:
        // Avatar/Sokka read "needs work" while its color table showed every color over-supplied). Two
        // changes: (1) the London keep band tightened to the sweet spot — keep 3 lands (2 with ramp),
        // mulligan 4-5 land floods (high-curve decks keep their wider band) — which shifts real cast
        // rates; (2) a color counts as "starved"/under-supported by the base ONLY when it actually lacks
        // sources (deficit > 0), so a cheap colored spell that misses its turn-1 window while its color
        // runs a surplus no longer drags the verdict. The Avatar deck drops Excellent -> Solid: its worst
        // color still casts a cheap spell just under the bar on curve (a minor note), but no color is
        // source-limited, so it is Solid, not Excellent and never "needs work". No other deck changes band.
        new("Stale Brago (WU control)", ".manabase-brago-facts.json", "Needs work", "Workable"),
        new("Kenrith 5-color rocks", ".manabase-5c-facts.json", "Excellent", "Excellent"),
        new("Meren Golgari ramp/ritual", ".manabase-golgari-facts.json", "Solid", "Solid"),
        new("Avatar - Sokka/Aang", "avatar-facts.json", "Solid", "Solid", IsAssemblyFixture: true),
        new("Archidekt 23563520 - Marchesa", ".manabase-arch-23563520-facts.json", "Needs work", "Needs work"),
        new("Archidekt 23753514 - graveyard fungus", ".manabase-arch-23753514-facts.json", "Solid", "Solid"),
        new("Archidekt 23638601 - Townos", ".manabase-arch-23638601-facts.json", "Excellent", "Excellent"),
        new("Archidekt 8066726 - The Necrobloom", ".manabase-arch-8066726-facts.json", "Needs work", "Needs work"),
        new("Archidekt 7084567 - army now", ".manabase-arch-7084567-facts.json", "Solid", "Solid"),
    ];

    // Retained only for the measurement harness/dump. Post turn-1-draw recalibration this deck no
    // longer sits at the promotion boundary (reads Needs work with the floor both off and on); the
    // NeedsWork->Workable promotion is now guarded synthetically (see
    // HealthBandHeadlineFloor_SingleSoftColorLandShort_PromotesNeedsWorkToWorkable).
    private static readonly CalibrationDeck BragoPromoteDeck =
        new("Brago promote (WU control)", ".manabase-brago-promote-facts.json", "Needs work", "Needs work");

    [Fact]
    public async Task Avatar_FlagOff_BandIsSolid()
    {
        IReadOnlyList<CardFact> facts = await LoadFactsAsync();
        ManabaseDeck deck = ManabaseClassifier.Classify(facts, isSingleton: true);
        ManabaseReport report = ManabaseAnalyzer.Analyze(
            deck, ManabaseMode.Casual, CommanderImportance.Standard,
            useHealthBandCastability: false);

        string label = ManabaseDisplay.HealthLabel(report.Health);
        Assert.Equal("Solid", label);
    }

    [Fact]
    public async Task Avatar_FlagOn_BandIsSolid_NoWeakColor()
    {
        // No weak COLOR: nothing is source-limited (ColorLimitedUnderSupportedCount == 0), so the deck
        // never reads "needs work" or advises adding lands. It lands on Solid rather than Excellent only
        // because a cheap colored spell casts just under the bar on curve (turn-1 variance, not weak
        // support) — an honest "minor note", not a color problem. The flag-on color-issue coupling this
        // deck used to exercise end-to-end is now guarded by the synthetic SyntheticReport tests below.
        IReadOnlyList<CardFact> facts = await LoadFactsAsync();
        ManabaseDeck deck = ManabaseClassifier.Classify(facts, isSingleton: true);
        ManabaseReport report = ManabaseAnalyzer.Analyze(
            deck, ManabaseMode.Casual, CommanderImportance.Standard,
            useHealthBandCastability: true);

        Assert.Equal("Solid", ManabaseDisplay.HealthLabel(report.Health));
        Assert.DoesNotContain(report.ColorFindings, f => f.ColorLimitedUnderSupportedCount > 0);
    }

    public static IEnumerable<object[]> HeadlineFloorCalibrationCases() =>
        CalibrationDecks.Select(d => new object[] { d });

    [Theory]
    [MemberData(nameof(HeadlineFloorCalibrationCases))]
    public async Task HealthBandHeadlineFloor_FlagOffVersusFlagOn_CalibrationDecks(CalibrationDeck calibration)
    {
        IReadOnlyList<CardFact> facts = await LoadFactsAsync(calibration);
        ManabaseDeck deck = ManabaseClassifier.Classify(facts, isSingleton: true);

        ManabaseReport off = ManabaseAnalyzer.Analyze(
            deck, ManabaseMode.Casual, CommanderImportance.Standard,
            useHealthBandHeadlineFloor: false);
        ManabaseReport on = ManabaseAnalyzer.Analyze(
            deck, ManabaseMode.Casual, CommanderImportance.Standard,
            useHealthBandHeadlineFloor: true);

        string offLabel = ManabaseDisplay.HealthLabel(off.Health);
        string onLabel = ManabaseDisplay.HealthLabel(on.Health);
        Assert.True(offLabel == calibration.FlagOffLabel,
            $"{calibration.Name} flag OFF expected {calibration.FlagOffLabel}, got {offLabel}; "
            + $"avg {off.AvgOnCurvePercent}, worst {off.WorstColorCastPercent:F0}");
        Assert.True(onLabel == calibration.FlagOnLabel,
            $"{calibration.Name} flag ON expected {calibration.FlagOnLabel}, got {onLabel}; "
            + $"avg {on.AvgOnCurvePercent}, worst {on.WorstColorCastPercent:F0}");
    }

    [Fact]
    public void HealthBandHeadlineFloor_SingleSoftColorLandShort_PromotesNeedsWorkToWorkable()
    {
        // A land-short deck whose ONLY red signal is one soft, contained single-color issue:
        // NeedsWork with the floor off, promoted to Workable with it on. Synthetic (hand-built
        // report) because after the turn-1-draw recalibration no REAL fixture sits at this boundary
        // — the calibration decks are all either clearly fine or clearly broken (every one reads the
        // same band flag-off and flag-on). The promotion LOGIC is what this guards, and it is
        // draw-independent. (Was an end-to-end Brago-promote fixture assertion.)
        ManabaseReport off = SyntheticReport(false, false, SoftSingleColorFinding());
        ManabaseReport on = SyntheticReport(false, true, SoftSingleColorFinding());

        Assert.Equal(ManabaseHealth.NeedsWork, off.Health);
        Assert.Equal(ManabaseHealth.Workable, on.Health);
    }

    [Fact]
    public async Task HealthBandHeadlineFloor_SevereColorDeficit_StaysNeedsWork()
    {
        // Marchesa carries a real >2-source color deficit, so the headline floor must NOT promote it.
        // (Repointed from Stale Brago, which dropped below the severe threshold once the sim drew on
        // turn 1 — its extra card lifts it into Workable.)
        CalibrationDeck severe = CalibrationDecks.Single(d => d.Name.StartsWith("Archidekt 23563520", StringComparison.Ordinal));
        IReadOnlyList<CardFact> facts = await LoadFactsAsync(severe);
        ManabaseDeck deck = ManabaseClassifier.Classify(facts, isSingleton: true);
        ManabaseReport report = ManabaseAnalyzer.Analyze(
            deck, ManabaseMode.Casual, CommanderImportance.Standard,
            useHealthBandHeadlineFloor: true);

        Assert.Contains(report.ColorFindings, f => f.Deficit > 2);
        Assert.Equal("Needs work", ManabaseDisplay.HealthLabel(report.Health));
    }

    [Fact]
    public void HealthBandHeadlineFloor_BothFlagsOn_DoesNotOverrideTwoColorHardFail()
    {
        ManabaseReport report = SyntheticReport(
            true,
            true,
            new ColorSourceFinding
            {
                Color = ManaColor.Blue,
                ActualSources = 30,
                RequiredSources = 24,
                DrivingSpell = "Sim weak",
                WorstSpell = "Sim weak",
                WorstSpellCastPercent = 70,
                UnderSupportedCount = 1,
                ColorLimitedUnderSupportedCount = 1,
            },
            new ColorSourceFinding
            {
                Color = ManaColor.White,
                ActualSources = 23.5,
                RequiredSources = 25,
                DrivingSpell = "Raw short",
                WorstSpell = "Raw short",
                WorstSpellCastPercent = 90,
            });

        Assert.Equal(90, report.AvgOnCurvePercent);
        Assert.Equal(70, report.WorstColorCastPercent);
        Assert.Equal(ManabaseHealth.NeedsWork, report.Health);
    }

    [Fact]
    public void HealthBandHeadlineFloor_BroadUnderSupport_StaysNeedsWork()
    {
        ManabaseReport report = SyntheticReport(
            false,
            true,
            new ColorSourceFinding
            {
                Color = ManaColor.White,
                ActualSources = 23.5,
                RequiredSources = 25,
                DrivingSpell = "Raw short",
                WorstSpell = "Raw short",
                WorstSpellCastPercent = 90,
                UnderSupportedCount = 9,
                ColorLimitedUnderSupportedCount = 9,
            });

        Assert.Equal(90, report.AvgOnCurvePercent);
        Assert.Equal(90, report.WorstColorCastPercent);
        Assert.Equal(ManabaseHealth.NeedsWork, report.Health);
    }

    [Fact]
    public void HealthBandHeadlineFloor_Promotion_CouplesRampAndPrimaryFix()
    {
        // When the floor promotes a land-short deck to Workable, the land shortfall must read as
        // ramp-covered and the biggest fix must NOT be "add lands" — the sim says the paper land gap
        // is not the real problem, so verdict + land-advice + PrimaryFix stay coupled. Synthetic for
        // the same reason as above (no real fixture sits at this boundary post-recalibration).
        ManabaseReport report = SyntheticReport(false, true, SoftSingleColorFinding());

        Assert.Equal(ManabaseHealth.Workable, report.Health);
        Assert.True(report.LandShortfallCoveredByRamp);
        Assert.NotEqual(ManabaseFixKind.Lands, report.PrimaryFix.Kind);
    }

    // One soft, contained single-color issue (1.5 sources short, above rounding noise but not the
    // >2 severe bar), casting at 90% — the exact single-red-signal shape the headline floor promotes.
    private static ColorSourceFinding SoftSingleColorFinding() => new()
    {
        Color = ManaColor.White,
        ActualSources = 23.5,
        RequiredSources = 25,
        DrivingSpell = "Soft white",
        WorstSpell = "Soft white",
        WorstSpellCastPercent = 90,
    };

    [Fact]
    public async Task ResolveBragoPromoteFactsCache()
    {
        if (!HarnessEnabled())
        {
            return;
        }

        string deckPath = Path.Combine(RepoPaths.Root(), ".planning", "debug", "manabase-brago-promote-deck.txt");
        string cachePath = Path.Combine(RepoPaths.Root(), "DeckFlow.Web.Tests", "Manabase", "fixtures", BragoPromoteDeck.FactsFile);
        string list = await File.ReadAllTextAsync(deckPath);
        IReadOnlyList<DeckCardEntry> entries = await ResolveAsync(ParseDeck(list));
        IReadOnlyList<CardFact> facts = ScryfallCardFactMapper.ToCardFacts(entries).ToList();
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(facts));

        Assert.True(File.Exists(cachePath));
    }

    [Fact]
    public async Task DumpHeadlineFloorMeasurements()
    {
        if (!HarnessEnabled())
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("| Deck | Avg | WorstColor | MaxColorLimited | MaxUnderSupported | BroadColorUnderSupport | AnySevereColorDeficit | MaxColorDeficit | Flag OFF | Flag ON |");
        sb.AppendLine("|---|---:|---:|---:|---:|---|---|---:|---|---|");

        AppendMeasurement(sb, BragoPromoteDeck, await LoadFactsAsync(BragoPromoteDeck));
        foreach (CalibrationDeck calibration in CalibrationDecks)
        {
            AppendMeasurement(sb, calibration, await LoadFactsAsync(calibration));
        }

        string table = sb.ToString();
        System.Console.WriteLine(table);
    }

    private static async Task<IReadOnlyList<CardFact>> LoadFactsAsync()
    {
        Assert.True(File.Exists(FactsCachePath),
            $"Avatar facts cache not found at {FactsCachePath}. Run the baseline harness once to populate it.");

        List<CardFact> facts = await CardFactFixtureFile.LoadAsync(FactsCachePath);

        // Re-derive ManaAmount from oracle text: older caches may predate this field.
        return facts.Select(f => f with { ManaAmount = ManaProductionAmount.Parse(f.OracleText) }).ToList();
    }

    private static async Task<IReadOnlyList<CardFact>> LoadFactsAsync(CalibrationDeck calibration)
    {
        string path = calibration.IsAssemblyFixture
            ? FactsCachePath
            : Path.Combine(RepoPaths.Root(), "DeckFlow.Web.Tests", "Manabase", "fixtures", calibration.FactsFile);

        Assert.True(File.Exists(path), $"Facts cache not found at {path}.");

        List<CardFact> facts = await CardFactFixtureFile.LoadAsync(path);

        return facts.Select(f => f with { ManaAmount = ManaProductionAmount.Parse(f.OracleText) }).ToList();
    }

    private static void AppendMeasurement(StringBuilder sb, CalibrationDeck calibration, IReadOnlyList<CardFact> facts)
    {
        ManabaseDeck deck = ManabaseClassifier.Classify(facts, isSingleton: true);
        ManabaseReport off = ManabaseAnalyzer.Analyze(deck, ManabaseMode.Casual, CommanderImportance.Standard);
        ManabaseReport on = ManabaseAnalyzer.Analyze(
            deck, ManabaseMode.Casual, CommanderImportance.Standard,
            useHealthBandHeadlineFloor: true);
        (int maxColorLimited, int maxUnderSupported, bool broadColor, bool severe, double maxDeficit) = Signals(on);

        sb.AppendLine($"| {calibration.Name} | {on.AvgOnCurvePercent} | {on.WorstColorCastPercent:F0} | "
            + $"{maxColorLimited} | {maxUnderSupported} | {broadColor} | {severe} | {maxDeficit:F1} | "
            + $"{ManabaseDisplay.HealthLabel(off.Health)} | {ManabaseDisplay.HealthLabel(on.Health)} |");
    }

    private static (int MaxColorLimited, int MaxUnderSupported, bool BroadColorUnderSupport, bool AnySevereColorDeficit, double MaxColorDeficit)
        Signals(ManabaseReport report)
    {
        int maxColorLimited = report.ColorFindings.Count == 0 ? 0 : report.ColorFindings.Max(f => f.ColorLimitedUnderSupportedCount);
        int maxUnderSupported = report.ColorFindings.Count == 0 ? 0 : report.ColorFindings.Max(f => f.UnderSupportedCount);
        bool broadColor = report.ColorFindings.Any(f =>
        {
            int colorCards = report.ColorSpellCounts.TryGetValue(f.Color, out int count) ? count : 0;
            int tolerance = Math.Max(1, (int)Math.Ceiling(colorCards * 0.15));
            return f.ColorLimitedUnderSupportedCount > tolerance;
        });
        bool severe = report.ColorFindings.Any(f => f.Deficit > 2);
        double maxDeficit = report.ColorFindings.Count == 0 ? 0 : report.ColorFindings.Max(f => f.Deficit);

        return (maxColorLimited, maxUnderSupported, broadColor, severe, maxDeficit);
    }

    private static ManabaseReport SyntheticReport(
        bool useHealthBandCastability = false,
        bool useHealthBandHeadlineFloor = false,
        params ColorSourceFinding[] findings) =>
        new()
        {
            ActualLands = 35,
            TargetLands = 37,
            ColorFindings = findings,
            Castability =
            [
                new CardCastability { Name = "A", ManaValue = 2, OnCurveTurn = 2, CastPercent = 90, LimitingFactor = "mana" },
                new CardCastability { Name = "B", ManaValue = 3, OnCurveTurn = 3, CastPercent = 90, LimitingFactor = "mana" },
            ],
            ColorSpellCounts = findings.ToDictionary(f => f.Color, _ => 40),
            Summary = "test",
            UseHealthBandCastability = useHealthBandCastability,
            UseHealthBandHeadlineFloor = useHealthBandHeadlineFloor,
        };

    private static bool HarnessEnabled() =>
        Environment.GetEnvironmentVariable("DECKFLOW_MANABASE_HARNESS") == "1"
        || File.Exists(Path.Combine(RepoPaths.Root(), ".manabase-harness-on"));

    private static List<(int Qty, string Name, bool IsCommander)> ParseDeck(string list)
    {
        var result = new List<(int, string, bool)>();
        bool first = true;
        foreach (string raw in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int sp = raw.IndexOf(' ');
            int qty = int.Parse(raw[..sp]);
            string rest = raw[(sp + 1)..];
            int paren = rest.IndexOf(" (", StringComparison.Ordinal);
            string name = paren > 0 ? rest[..paren] : rest;
            int slash = name.IndexOf(" / ", StringComparison.Ordinal);
            if (slash > 0)
            {
                name = name[..slash];
            }

            result.Add((qty, name.Trim(), first));
            first = false;
        }

        return result;
    }

    private static async Task<IReadOnlyList<DeckCardEntry>> ResolveAsync(
        List<(int Qty, string Name, bool IsCommander)> lines)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "DeckFlow-manabase-harness/1.0");
        http.DefaultRequestHeaders.Add("Accept", "application/json");

        var byName = new Dictionary<string, (int Qty, string Name, bool IsCommander)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            byName[line.Name] = line;
        }

        var entries = new List<DeckCardEntry>();
        foreach (var batch in lines.Chunk(75))
        {
            var body = new { identifiers = batch.Select(line => new { name = line.Name }).ToArray() };
            using HttpResponseMessage response = await http.PostAsJsonAsync(
                "https://api.scryfall.com/cards/collection", body);
            response.EnsureSuccessStatusCode();
            ScryfallCollectionResponse? data = await response.Content.ReadFromJsonAsync<ScryfallCollectionResponse>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            foreach (ScryfallCard card in data!.Data)
            {
                ScryfallCardData mapped = ScryfallCardDataMapper.ToCardData(card);
                string key = card.Name.Split(" // ")[0];
                (int Qty, string Name, bool IsCommander) line =
                    byName.TryGetValue(key, out var front) ? front :
                    byName.TryGetValue(card.Name, out var exact) ? exact : (1, card.Name, false);
                entries.Add(new DeckCardEntry { Card = mapped, Quantity = line.Qty, IsCommander = line.IsCommander });
            }

            await Task.Delay(120);
        }

        return entries;
    }

    public sealed record CalibrationDeck(
        string Name,
        string FactsFile,
        string FlagOffLabel,
        string FlagOnLabel,
        bool IsAssemblyFixture = false)
    {
        public override string ToString() => Name;
    }
}
