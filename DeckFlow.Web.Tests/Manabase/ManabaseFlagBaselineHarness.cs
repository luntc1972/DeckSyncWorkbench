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
/// Manual harness (NOT a CI test): the consolidated Phase-70 flag baseline. For each representative
/// real deck it runs the Core analyzer with each MQ flag turned on in isolation vs the all-off
/// baseline and dumps the cast% / verdict delta, so the bundled
/// <c>analysis.manabase.accuracy</c> toggle's default can be made against real numbers.
///
/// Decks: Brago (cached WU, exercises MQ-02 weakly + MQ-05 at 2 colors); a 5-color rocks/ramp deck
/// (MQ-02 burst rocks + MQ-05 at 5 colors); a Golgari ritual/land-ramp deck (MQ-03 one-shot vs
/// repeatable ramp). CardFacts are cached to .manabase-*-facts.json so reruns don't re-hit Scryfall.
///
/// Gated on DECKFLOW_MANABASE_HARNESS=1 (or a .manabase-harness-on sentinel) so it never runs in CI.
/// Run: DECKFLOW_MANABASE_HARNESS=1 dotnet test --filter ManabaseFlagBaselineHarness
/// </summary>
public sealed class ManabaseFlagBaselineHarness
{
    // 5-color rocks/ramp deck: heavy on burst rocks (Sol Ring, Mana Crypt, Gilded Lotus, Thran
    // Dynamo, Jeweled Lotus) for MQ-02 and a full 5-color base for MQ-05.
    private const string FiveColorList = """
1 Kenrith, the Returned King
1 Sol Ring
1 Mana Crypt
1 Mana Vault
1 Jeweled Lotus
1 Chrome Mox
1 Mox Diamond
1 Mox Opal
1 Arcane Signet
1 Fellwar Stone
1 Coalition Relic
1 Gilded Lotus
1 Thran Dynamo
1 Worn Powerstone
1 Azorius Signet
1 Dimir Signet
1 Golgari Signet
1 Boros Signet
1 Simic Signet
1 Birds of Paradise
1 Noble Hierarch
1 Ignoble Hierarch
1 Bloom Tender
1 Faeburrow Elder
1 Smothering Tithe
1 Dockside Extortionist
1 Atraxa, Praetors' Voice
1 Niv-Mizzet Reborn
1 Golos, Tireless Pilgrim
1 The Ur-Dragon
1 Cultivate
1 Kodama's Reach
1 Farseek
1 Nature's Lore
1 Swords to Plowshares
1 Cyclonic Rift
1 Anguished Unmaking
1 Assassin's Trophy
1 Teferi, Hero of Dominaria
1 Kaalia of the Vast
1 Command Tower
1 City of Brass
1 Mana Confluence
1 Exotic Orchard
1 Reflecting Pool
1 Forbidden Orchard
1 Flooded Strand
1 Polluted Delta
1 Bloodstained Mire
1 Wooded Foothills
1 Windswept Heath
1 Marsh Flats
1 Scalding Tarn
1 Verdant Catacombs
1 Arid Mesa
1 Misty Rainforest
1 Hallowed Fountain
1 Watery Grave
1 Sacred Foundry
1 Stomping Ground
1 Temple Garden
1 Steam Vents
1 Overgrown Tomb
1 Godless Shrine
1 Breeding Pool
1 Blood Crypt
1 Raugrin Triome
1 Savai Triome
1 Indatha Triome
1 Zagoth Triome
1 Ketria Triome
1 Spara's Headquarters
1 Xander's Lounge
1 Ziatora's Proving Ground
1 Jetmir's Garden
1 Raffine's Tower
2 Plains
2 Island
2 Swamp
2 Mountain
2 Forest
""";

    // Golgari ritual + land-ramp deck: one-shot rituals (Dark Ritual, Cabal Ritual, Culling the
    // Weak) and Treasure makers should LOSE the ramp/draw land-target credit under MQ-03 v2, while
    // repeatable land-ramp (Cultivate, Nature's Lore) and rocks/dorks keep it.
    private const string GolgariList = """
1 Meren of Clan Nel Toth
1 Dark Ritual
1 Cabal Ritual
1 Culling the Weak
1 Songs of the Damned
1 Cultivate
1 Kodama's Reach
1 Rampant Growth
1 Nature's Lore
1 Three Visits
1 Farseek
1 Sakura-Tribe Elder
1 Wood Elves
1 Sol Ring
1 Arcane Signet
1 Fellwar Stone
1 Mind Stone
1 Birds of Paradise
1 Llanowar Elves
1 Elvish Mystic
1 Fyndhorn Elves
1 Tireless Provisioner
1 Old Gnawbone
1 Eternal Witness
1 Grim Haruspex
1 Sakura-Tribe Scout
1 Sidisi, Undead Vizier
1 Grave Titan
1 Sheoldred, the Apocalypse
1 Massacre Wurm
1 Bone Shards
1 Go for the Throat
1 Infernal Grasp
1 Putrefy
1 Abrupt Decay
1 Casualties of War
1 Damnation
1 Toxic Deluge
1 Deadly Rollick
1 Beast Within
1 Reanimate
1 Animate Dead
1 Victimize
1 Diabolic Intent
1 Demonic Tutor
1 Vampiric Tutor
1 Sylvan Library
1 Phyrexian Arena
1 Command Tower
1 Overgrown Tomb
1 Verdant Catacombs
1 Llanowar Wastes
1 Woodland Cemetery
1 Twilight Mire
1 Hissing Quagmire
1 Blooming Marsh
1 Nurturing Peatland
1 Bojuka Bog
1 Castle Locthwain
12 Swamp
12 Forest
""";

    [Fact]
    public async Task DumpFlagBaselines()
    {
        if (Environment.GetEnvironmentVariable("DECKFLOW_MANABASE_HARNESS") != "1"
            && !File.Exists(Path.Combine(RepoPaths.Root(), ".manabase-harness-on")))
        {
            return; // gated
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Phase 70 — consolidated flag baseline (Core analyzer)");
        sb.AppendLine();
        sb.AppendLine("Each flag is turned on in ISOLATION vs the all-off baseline. MQ-02/MQ-05 are");
        sb.AppendLine("Analyze-time; MQ-03 is classify-time (re-classifies the deck). Cast% is the seeded");
        sb.AppendLine("Monte-Carlo display value; the verdict probe path is unaffected by MQ-02/MQ-05.");
        sb.AppendLine();

        foreach ((string name, string? list, string cacheFile) in Decks())
        {
            IReadOnlyList<CardFact> facts = await LoadFactsAsync(list, cacheFile);
            sb.AppendLine($"## Deck: {name} ({facts.Sum(f => f.Quantity)} cards, {facts.Count} distinct)");
            sb.AppendLine();
            AppendDeckSection(sb, facts);
        }

        string outDir = Path.Combine(RepoPaths.Root(), ".planning", "phases", "70-manabase-accuracy-mana-quantity");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, "70-flag-baseline.md");
        await File.WriteAllTextAsync(outPath, sb.ToString());
        System.Console.WriteLine(sb.ToString());
    }

    private static void AppendDeckSection(StringBuilder sb, IReadOnlyList<CardFact> facts)
    {
        ManabaseDeck deck = ManabaseClassifier.Classify(facts, isSingleton: true);

        // MQ-02 (Analyze useManaQuantity) and MQ-05 (Analyze colorAwareMulligan) on the same deck.
        foreach (ManabaseMode mode in new[] { ManabaseMode.Casual, ManabaseMode.Cedh })
        {
            ManabaseReport baseOff = ManabaseAnalyzer.Analyze(deck, mode, CommanderImportance.Standard);

            ManabaseReport mq02 = ManabaseAnalyzer.Analyze(
                deck, mode, CommanderImportance.Standard, null, useManaQuantity: true);
            AppendFlagDelta(sb, $"{mode} · MQ-02 source-mana-quantity", baseOff, mq02, targetMoved: false);

            ManabaseReport mq05 = ManabaseAnalyzer.Analyze(
                deck, mode, CommanderImportance.Standard, null, useManaQuantity: false, colorAwareMulligan: true);
            AppendFlagDelta(sb, $"{mode} · MQ-05 color-aware-mulligan", baseOff, mq05, targetMoved: false);

            // MQ-03 re-classifies (land-target credit), so compare two analyses of two decks.
            ManabaseDeck deckV2 = ManabaseClassifier.Classify(facts, isSingleton: true, rampCreditV2: true);
            ManabaseReport mq03 = ManabaseAnalyzer.Analyze(deckV2, mode, CommanderImportance.Standard);
            AppendFlagDelta(sb, $"{mode} · MQ-03 ramp-credit-v2", baseOff, mq03, targetMoved: true);

            // 70-03b re-classifies too (the land-ramp sim source is added at classify time). Sim-only /
            // colorless → the land target does not move.
            ManabaseDeck deckLandRamp = ManabaseClassifier.Classify(facts, isSingleton: true, landRampSim: true);
            ManabaseReport mqLandRamp = ManabaseAnalyzer.Analyze(deckLandRamp, mode, CommanderImportance.Standard);
            AppendFlagDelta(sb, $"{mode} · 70-03b land-ramp-sim", baseOff, mqLandRamp, targetMoved: false);
        }

        // Karsten closed-form cross-check (Casual): the same hypergeometric Salubrious Snail uses, per
        // card's hardest single-color requirement, vs our sim with all flags OFF and all flags ON.
        AppendKarstenComparison(sb, facts);
    }

    // Per-card Karsten reference = P(>=T lands) x CastConsistency(hardest color requirement). This is
    // the closed-form Snail/Karsten metric. We show it next to our sim (all flags OFF and all ON) so
    // the flag flip can be judged by whether ON tracks the independent Karsten reference more closely.
    private static void AppendKarstenComparison(StringBuilder sb, IReadOnlyList<CardFact> facts)
    {
        ManabaseDeck deckOff = ManabaseClassifier.Classify(facts, isSingleton: true);
        ManabaseDeck deckOn = ManabaseClassifier.Classify(facts, isSingleton: true, rampCreditV2: true);
        ManabaseReport off = ManabaseAnalyzer.Analyze(deckOff, ManabaseMode.Casual, CommanderImportance.Standard);
        ManabaseReport on = ManabaseAnalyzer.Analyze(
            deckOn, ManabaseMode.Casual, CommanderImportance.Standard, null, useManaQuantity: true, colorAwareMulligan: true);

        int deckSize = deckOff.TotalCards - deckOff.CommanderCount;
        int totalLands = off.ActualLands;
        var colorSources = off.ColorFindings.ToDictionary(f => f.Color, f => (int)Math.Round(f.ActualSources));
        var pipsByName = deckOff.Spells.ToDictionary(s => s.Name, s => s.Pips, StringComparer.Ordinal);
        var onByName = on.Castability.ToDictionary(c => c.Name, c => c.CastPercent, StringComparer.Ordinal);

        var rows = new List<(string Name, int Mv, int Karsten, int SimOff, int SimOn)>();
        foreach (CardCastability row in off.Castability)
        {
            if (!pipsByName.TryGetValue(row.Name, out var pips))
            {
                continue;
            }

            int mv = Math.Max(1, row.OnCurveTurn);
            int landSeen = KarstenManabase.CardsSeenByTurn(mv, onPlay: true);
            double landProb = Hypergeometric.AtLeast(deckSize, totalLands, landSeen, mv);

            var colored = pips.Where(p => p.Key != ManaColor.Colorless && p.Value > 0).ToList();
            double karsten;
            if (colored.Count == 0)
            {
                karsten = landProb; // no color requirement → pure land-drop probability
            }
            else
            {
                // Hardest single-color requirement: most pips, tie-break to the scarcest color source.
                var hardest = colored
                    .OrderByDescending(p => p.Value)
                    .ThenBy(p => colorSources.GetValueOrDefault(p.Key, 0))
                    .First();
                int src = colorSources.GetValueOrDefault(hardest.Key, 0);
                karsten = landProb * KarstenManabase.CastConsistency(deckSize, totalLands, src, hardest.Value, mv);
            }

            rows.Add((row.Name, row.ManaValue,
                (int)Math.Round(karsten * 100),
                row.CastPercent,
                onByName.TryGetValue(row.Name, out int o) ? o : row.CastPercent));
        }

        if (rows.Count == 0)
        {
            return;
        }

        double maeOff = rows.Average(r => Math.Abs(r.SimOff - r.Karsten));
        double maeOn = rows.Average(r => Math.Abs(r.SimOn - r.Karsten));

        sb.AppendLine("### Karsten closed-form cross-check (Casual)");
        sb.AppendLine();
        sb.AppendLine("Karsten = P(≥T lands) × hardest-color CastConsistency (the Snail/Karsten metric). "
            + "Multi-color cards: our sim requires ALL colors jointly, so sim ≤ Karsten-hardest-single is expected.");
        sb.AppendLine($"- Mean |sim − Karsten|: OFF {maeOff:F1} pts → ALL-ON {maeOn:F1} pts "
            + $"({(maeOn < maeOff ? "ON closer to Karsten" : maeOn > maeOff ? "OFF closer" : "tie")})");
        sb.AppendLine();
        sb.AppendLine("| Card | MV | Karsten | sim OFF | sim ON | ON−Karsten |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in rows.OrderByDescending(r => Math.Abs(r.SimOn - r.Karsten)).ThenBy(r => r.Name).Take(15))
        {
            sb.AppendLine($"| {r.Name} | {r.Mv} | {r.Karsten} | {r.SimOff} | {r.SimOn} | {r.SimOn - r.Karsten:+0;-0} |");
        }

        sb.AppendLine();
    }

    private static void AppendFlagDelta(
        StringBuilder sb, string label, ManabaseReport off, ManabaseReport on, bool targetMoved)
    {
        var offByName = off.Castability.ToDictionary(c => c.Name, c => c.CastPercent, StringComparer.Ordinal);
        var rows = on.Castability
            .Where(c => offByName.ContainsKey(c.Name))
            .Select(c => (c.Name, c.ManaValue, Off: offByName[c.Name], On: c.CastPercent, Delta: c.CastPercent - offByName[c.Name]))
            .ToList();

        int changed = rows.Count(r => r.Delta != 0);
        double meanAbs = rows.Count > 0 ? rows.Average(r => Math.Abs(r.Delta)) : 0;
        int maxUp = rows.Count > 0 ? rows.Max(r => r.Delta) : 0;
        int maxDown = rows.Count > 0 ? rows.Min(r => r.Delta) : 0;

        sb.AppendLine($"### {label}");
        sb.AppendLine();
        sb.AppendLine($"- Health: {off.Health} → {on.Health}");
        sb.AppendLine($"- Avg/Worst color cast: {off.AvgOnCurvePercent}/{off.WorstColorCastPercent:F0} → "
            + $"{on.AvgOnCurvePercent}/{on.WorstColorCastPercent:F0}");
        if (targetMoved)
        {
            sb.AppendLine($"- Land target: {off.TargetLands:F1} → {on.TargetLands:F1} "
                + $"(ramp/draw<=2 {off.LandTarget?.RampAndDrawUnderThree} → {on.LandTarget?.RampAndDrawUnderThree})");
        }
        else
        {
            sb.AppendLine($"- Land target: {on.TargetLands:F1} (unchanged by this flag)");
        }

        sb.AppendLine($"- Weakest color: {off.WeakestColor?.Color.ToString() ?? "none"} → {on.WeakestColor?.Color.ToString() ?? "none"}");
        sb.AppendLine($"- Cast%: {changed}/{rows.Count} cards changed · mean |Δ| {meanAbs:F1} pts · range {maxDown:+0;-0}..{maxUp:+0;-0}");
        if (changed > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| Card | MV | Off | On | Δ |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var r in rows.Where(r => r.Delta != 0).OrderByDescending(r => Math.Abs(r.Delta)).ThenBy(r => r.Name).Take(15))
            {
                sb.AppendLine($"| {r.Name} | {r.ManaValue} | {r.Off} | {r.On} | {r.Delta:+0;-0} |");
            }
        }

        sb.AppendLine();
    }

    private static IEnumerable<(string Name, string? List, string CacheFile)> Decks()
    {
        yield return ("Brago (WU control)", null, ".manabase-brago-facts.json");
        yield return ("Kenrith 5-color rocks", FiveColorList, ".manabase-5c-facts.json");
        yield return ("Meren Golgari ramp/ritual", GolgariList, ".manabase-golgari-facts.json");

        // 5 recent real Commander decks harvested from Archidekt by scripts/harvest-archidekt-decks.py.
        string harvestPath = Path.Combine(RepoPaths.Root(), "archidekt-baseline-decks.json");
        if (File.Exists(harvestPath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(harvestPath));
            foreach (JsonElement deck in doc.RootElement.EnumerateArray())
            {
                string id = deck.GetProperty("id").GetString()!;
                string name = deck.GetProperty("name").GetString()!;
                string list = deck.GetProperty("list").GetString()!;
                yield return ($"Archidekt {id} — {name}", list, $".manabase-arch-{id}-facts.json");
            }
        }
    }

    // Load CardFacts from cache, or resolve the decklist through Scryfall once and cache it. The
    // Brago cache predates the ManaAmount field, so re-derive it from oracle text on load.
    private static async Task<IReadOnlyList<CardFact>> LoadFactsAsync(string? list, string cacheFile)
    {
        string cachePath = Path.Combine(RepoPaths.Root(), "DeckFlow.Web.Tests", "Manabase", "fixtures", cacheFile);
        List<CardFact> facts;
        if (File.Exists(cachePath))
        {
            facts = await CardFactFixtureFile.LoadAsync(cachePath);
        }
        else
        {
            Assert.False(list is null, $"missing cache {cacheFile} and no decklist to resolve");
            IReadOnlyList<DeckCardEntry> entries = await ResolveAsync(ParseDeck(list!));
            facts = ScryfallCardFactMapper.ToCardFacts(entries).ToList();
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(facts));
        }

        // Re-derive ManaAmount from oracle text (caches may predate the field).
        return facts.Select(f => f with { ManaAmount = ManaProductionAmount.Parse(f.OracleText) }).ToList();
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
        foreach (var l in lines)
        {
            byName[l.Name] = l;
        }

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
                string key = card.Name.Split(" // ")[0];
                (int Qty, string Name, bool IsCommander) line =
                    byName.TryGetValue(key, out var m) ? m :
                    byName.TryGetValue(card.Name, out var m2) ? m2 : (1, card.Name, false);
                entries.Add(new DeckCardEntry { Card = mapped, Quantity = line.Qty, IsCommander = line.IsCommander });
            }

            await Task.Delay(120);
        }

        return entries;
    }
}
