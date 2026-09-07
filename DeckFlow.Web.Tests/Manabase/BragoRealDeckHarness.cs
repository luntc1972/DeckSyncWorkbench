using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DeckFlow.Core.Manabase;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.Manabase;
using Xunit;

namespace DeckFlow.Web.Tests.Manabase;

/// <summary>
/// Manual harness (NOT a CI test): resolves the real Brago deck through Scryfall and runs the
/// Core analyzer in all four mode/importance configs, dumping a markdown report to .planning so
/// the numbers can be eyeballed and cross-checked against the Salubrious Snail calculator before
/// the Web UI (Wave 2) exists. Gated on env var DECKFLOW_MANABASE_HARNESS=1 so it never runs in CI.
/// Run: DECKFLOW_MANABASE_HARNESS=1 dotnet test --filter BragoRealDeckHarness
/// </summary>
public sealed class BragoRealDeckHarness
{
    private const string DeckList = """
1 Brago, King Eternal (EMA) 198
1 Aang, Airbending Master (TLE) 74
1 Academy Ruins (DRC) 58
1 Access Tunnel (MKC) 247
1 Adarkar Wastes (DRC) 144
1 Aether Channeler (BLC) 160
1 Altar of the Brood (KTK) 216
1 An Offer You Can't Refuse (PLST) SNC-51
1 Arcane Denial (DRC) 70
1 Arcane Signet (SCD) 257
1 Archaeomancer (M14) 43
1 Avatar's Wrath (TLA) 12
1 Azorius Signet (SCD) 259
1 Charming Prince (FDN) 568
1 Cloud of Faeries (MOC) 219
1 Dawnbringer Cleric (CLB) 15
1 Deadeye Navigator (SLD) 902
1 Delivery Moogle (FIN) 15
1 Delney, Streetwise Lookout (MKM) 12
1 Displace (EMN) 55
1 Dovin's Veto (WAR) 193
1 Eldrazi Displacer (OGW) 13
1 Ephemerate (MH1) 7
1 Felidar Guardian (AER) 19
1 Fellwar Stone (PLST) CMD-248
1 Flare of Denial (MH3) 326
1 Flare of Fortitude (MH3) 26
1 Flooded Strand (MH3) 220
1 Ghostly Flicker (PLST) KHC-39
1 Ghostway (RVR) 308
1 Glacial Fortress (DRC) 159
1 Gossip's Talent (BLB) 51
1 Grand Abolisher (PBIG) 2p
1 Hallowed Fountain (RNA) 251
1 Hengegate Pathway / Mistgate Pathway (KHM) 260
1 Hide on the Ceiling (SPM) 32
9 Island (MKM) 280
1 Laboratory Maniac (UMA) 61
1 Loran's Escape (BRO) 14
1 Machine God's Effigy (BRC) 63
1 Mystic Gate (M3C) 359
1 Mystic Remora (SLD) 406
1 Nimbus Maze (IMA) 242
1 Peregrine Drake (DMR) 292
1 Permission Denied (REX) 17
1 Peter Parker's Camera (SPM) 171
1 Plagon, Lord of the Beach (J25) 37
9 Plains (M13) 230
1 Prairie Stream (M3C) 365
1 Quantum Riddler (EOE) 305
1 Reality Acid (TSR) 81
1 Recruiter of the Guard (MH3) 266
1 Reflecting Pool (PCLB) 358s
1 Reflector Mage (OGW) 157
1 Relic of Progenitus (MB2) 230
1 Riptide Gearhulk (PDFT) 219p
1 Rishadan Cutpurse (PLST) MMQ-93
1 Rogue's Passage (DDM) 77
1 Sea of Clouds (CLB) 360
1 Seasoned Dungeoneer (CLB) 610
1 Skyclave Apparition (MB2) 18
1 Sol Ring (M3C) 305
1 Solemn Simulacrum (DRC) 138
1 Springleaf Drum (BRR) 118
1 Starfield Vocalist (EOE) 78
1 Strionic Resonator (M14) 224
1 Sun Titan (SLD) 1550
1 Swan Song (SLD) 1591
1 Swiftfoot Boots (LCC) 314
1 Swords to Plowshares (DSC) 106
1 Talisman of Progress (PIP) 249
1 Teleportation Circle (AFR) 39
1 Thassa, Deep-Dwelling (THB) 261
1 Thought Vessel (MB2) 100
1 Tribute Mage (MH1) 73
1 Urza's Saga (MB2) 114
1 Venser, Shaper Savant (J25) 66
1 Venser, the Sojourner (SLD) 1423
1 Wall of Omens (2X2) 344
1 Wastes (SLD) 706
1 Whirler Rogue (NEC) 101
1 Whispersilk Cloak (DSC) 257
1 Witch Enchanter / Witch-Blessed Meadow (MH3) 239
1 Y'shtola Rhul (FIN) 86
""";

    [Fact]
    public async Task DumpBragoReport()
    {
        bool enabled = Environment.GetEnvironmentVariable("DECKFLOW_MANABASE_HARNESS") == "1"
            || File.Exists(Path.Combine(RepoPaths.Root(), ".manabase-harness-on"));
        if (!enabled)
        {
            return; // gated: skipped in CI / normal runs (no env var, no sentinel file)
        }

        var lines = ParseDeck(DeckList);

        // Cache resolved CardFacts so the simulator can be iterated without re-hitting Scryfall.
        string cachePath = Path.Combine(RepoPaths.Root(), "DeckFlow.Web.Tests", "Manabase", "fixtures", ".manabase-brago-facts.json");
        IReadOnlyList<CardFact> facts;
        int resolvedCount;
        if (File.Exists(cachePath))
        {
            facts = await CardFactFixtureFile.LoadAsync(cachePath);
            resolvedCount = facts.Sum(f => f.Quantity);
        }
        else
        {
            IReadOnlyList<DeckCardEntry> entries = await ResolveAsync(lines);
            facts = ScryfallCardFactMapper.ToCardFacts(entries);
            resolvedCount = entries.Sum(e => e.Quantity);
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(facts));
        }

        ManabaseDeck deck = ManabaseClassifier.Classify(facts, isSingleton: true);
        ManabaseDeck prodDeck = ManabaseClassifier.Classify(facts, isSingleton: true, rampCreditV2: true, landRampSim: true);

        var sb = new StringBuilder();
        sb.AppendLine("# Brago real-deck harness output (Core analyzer)");
        sb.AppendLine();
        sb.AppendLine($"Resolved {resolvedCount} cards · {facts.Count} distinct · "
            + $"{deck.Sources.Count(s => s.IsLand)} lands · avg MV {deck.AverageManaValue} · "
            + $"ramp/draw<=2 {deck.RampAndDrawUnderThree} · cost-reducers {deck.CostReduction.Count}");
        sb.AppendLine();

        AppendSnailComparison(sb, deck);
        AppendReport(sb, "Casual · Standard", deck, ManabaseMode.Casual, CommanderImportance.Standard);
        AppendReport(sb, "Casual · Standard · prod flags", prodDeck, ManabaseMode.Casual, CommanderImportance.Standard,
            useManaQuantity: true, colorAwareMulligan: true, gateRampOnCastable: true, useHealthBandHeadlineFloor: true);
        AppendReport(sb, "cEDH · Standard", deck, ManabaseMode.Cedh, CommanderImportance.Standard);
        AppendReport(sb, "Casual · Central (Brago)", deck, ManabaseMode.Casual, CommanderImportance.Central);
        AppendReport(sb, "Casual · Low", deck, ManabaseMode.Casual, CommanderImportance.Low);

        string outDir = Path.Combine(RepoPaths.Root(), ".planning", "phases", "64-manabase-modes-castability");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, "64-harness-brago-output.md");
        await File.WriteAllTextAsync(outPath, sb.ToString());
        System.Console.WriteLine(sb.ToString());

        Assert.True(facts.Count > 70, $"expected the deck to resolve; got {facts.Count}");
    }

    // Focused cross-check of the seven Snail reference cards (Casual · Standard).
    private static void AppendSnailComparison(StringBuilder sb, ManabaseDeck deck)
    {
        var snail = new (string Name, double Target)[]
        {
            ("Brago, King Eternal", 85.4),
            ("Deadeye Navigator", 52.5),
            ("Sun Titan", 52.5),
            ("Riptide Gearhulk", 63.5),
            ("Quantum Riddler", 68.4),
            ("Grand Abolisher", 79.0),
            ("Archaeomancer", 79.0),
        };

        ManabaseReport report = ManabaseAnalyzer.Analyze(deck, ManabaseMode.Casual, CommanderImportance.Standard);
        var byName = report.Castability.ToDictionary(c => c.Name, StringComparer.Ordinal);

        sb.AppendLine("## Snail comparison (Casual · Standard)");
        sb.AppendLine();
        sb.AppendLine("| Card | Snail | Ours | Δ |");
        sb.AppendLine("|---|---|---|---|");
        double absSum = 0;
        int n = 0;
        foreach ((string name, double target) in snail)
        {
            if (!byName.TryGetValue(name, out CardCastability? row))
            {
                sb.AppendLine($"| {name} | {target:F1} | (missing) | — |");
                continue;
            }

            double delta = row.CastPercent - target;
            absSum += Math.Abs(delta);
            n++;
            sb.AppendLine($"| {name} | {target:F1} | {row.CastPercent} | {delta:+0;-0} |");
        }

        sb.AppendLine();
        sb.AppendLine($"Mean |Δ| over {n} cards: {(n > 0 ? absSum / n : 0):F1} pts");
        sb.AppendLine();
    }

    private static void AppendReport(
        StringBuilder sb,
        string label,
        ManabaseDeck deck,
        ManabaseMode mode,
        CommanderImportance importance,
        bool useManaQuantity = false,
        bool colorAwareMulligan = false,
        bool gateRampOnCastable = false,
        bool useHealthBandHeadlineFloor = false)
    {
        ManabaseReport report = ManabaseAnalyzer.Analyze(
            deck, mode, importance,
            useManaQuantity: useManaQuantity,
            colorAwareMulligan: colorAwareMulligan,
            gateRampOnCastable: gateRampOnCastable,
            useHealthBandHeadlineFloor: useHealthBandHeadlineFloor);
        sb.AppendLine($"## {label}");
        sb.AppendLine();
        sb.AppendLine($"- Lands {report.ActualLands} vs target {report.TargetLands:F1} (delta {report.LandDelta:F1})");
        sb.AppendLine($"- Health: {report.Health} · Weakest: {report.WeakestColor?.Color.ToString() ?? "none"}");
        sb.AppendLine($"- AvgOnCurvePercent: {report.AvgOnCurvePercent} · WorstColorCastPercent: {report.WorstColorCastPercent:F0}");
        if (report.DemandingCards.Count > 0)
        {
            sb.AppendLine($"- Demanding: {string.Join(", ", report.DemandingCards.Select(d => $"{d.Name} ({d.CastPercent}%)"))}");
        }
        sb.AppendLine($"- Summary: {report.Summary}");
        sb.AppendLine();
        sb.AppendLine("| Color | Sources | Need | UnderSupp | ColorLimited | AvgCast% | WorstCast% | WorstSpell |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (ColorSourceFinding f in report.ColorFindings)
        {
            sb.AppendLine($"| {f.Color} | {f.ActualSources:F1} | {f.RequiredSources} | {f.UnderSupportedCount} | {f.ColorLimitedUnderSupportedCount} "
                + $"| {f.AverageCastPercent:F0} | {f.WorstSpellCastPercent:F0} | {f.WorstSpell} |");
        }
        sb.AppendLine();
        sb.AppendLine("Castability (worst-first, commander pinned):");
        sb.AppendLine();
        sb.AppendLine("| Card | MV | Turn | Cast% | Limiting |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (CardCastability c in report.Castability.Take(25))
        {
            sb.AppendLine($"| {c.Name} | {c.ManaValue} | {c.OnCurveTurn} | {c.CastPercent} | {c.LimitingFactor} |");
        }
        sb.AppendLine();
    }

    private static List<(int Qty, string Name, bool IsCommander)> ParseDeck(string list)
    {
        var result = new List<(int, string, bool)>();
        bool first = true;
        foreach (string raw in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int sp = raw.IndexOf(' ');
            int qty = int.Parse(raw[..sp]);
            string rest = raw[(sp + 1)..];
            // Strip the trailing " (SET) collector" printing tag.
            int paren = rest.IndexOf(" (", StringComparison.Ordinal);
            string name = paren > 0 ? rest[..paren] : rest;
            // MDFC "Front / Back" -> resolve by front face name.
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

        var byName = lines.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
        var entries = new List<DeckCardEntry>();

        foreach (var batch in lines.Chunk(75))
        {
            var body = new { identifiers = batch.Select(l => new { name = l.Name }).ToArray() };
            using HttpResponseMessage resp = await http.PostAsJsonAsync(
                "https://api.scryfall.com/cards/collection", body);
            resp.EnsureSuccessStatusCode();
            ScryfallCollectionResponse? data = await resp.Content.ReadFromJsonAsync<ScryfallCollectionResponse>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            foreach (ScryfallCard card in data!.Data)
            {
                ScryfallCardData mapped = ScryfallCardDataMapper.ToCardData(card);
                // Match back to the requested line by front-face name.
                string key = card.Name.Split(" // ")[0];
                (int Qty, string Name, bool IsCommander) line =
                    byName.TryGetValue(key, out var m) ? m :
                    byName.TryGetValue(card.Name, out var m2) ? m2 : (1, card.Name, false);
                entries.Add(new DeckCardEntry { Card = mapped, Quantity = line.Qty, IsCommander = line.IsCommander });
            }

            await Task.Delay(120); // be polite to Scryfall
        }

        return entries;
    }
}
