using System.Text;
using DeckFlow.Core.Manabase;
using Xunit;

namespace DeckFlow.Web.Tests.Manabase;

/// <summary>
/// Manual harness (NOT a CI test): MQ-02 baseline diff. Loads the cached real Brago CardFacts,
/// re-derives per-source mana amount from oracle text (the cache predates the ManaAmount field),
/// then runs the Core analyzer with the mana-quantity flag OFF and ON and dumps a per-card cast%
/// diff plus the verdict delta to .planning so the flag default can be decided against the
/// Salubrious Snail reference. Gated on DECKFLOW_MANABASE_HARNESS=1 so it never runs in CI.
/// Run: DECKFLOW_MANABASE_HARNESS=1 dotnet test --filter ManaQuantityBaselineHarness
/// </summary>
public sealed class ManaQuantityBaselineHarness
{
    [Fact]
    public async Task DumpManaQuantityDiff()
    {
        if (Environment.GetEnvironmentVariable("DECKFLOW_MANABASE_HARNESS") != "1"
            && !File.Exists(Path.Combine(RepoPaths.Root(), ".manabase-harness-on")))
        {
            return; // gated
        }

        string cachePath = Path.Combine(RepoPaths.Root(), "DeckFlow.Web.Tests", "Manabase", "fixtures", ".manabase-brago-facts.json");
        Assert.True(File.Exists(cachePath), $"missing cached facts: {cachePath}");

        List<CardFact> raw = await CardFactFixtureFile.LoadAsync(cachePath);

        // The cache predates MQ-02, so re-parse the amount each source makes from its oracle text.
        var facts = raw.Select(f => f with { ManaAmount = ManaProductionAmount.Parse(f.OracleText) }).ToList();

        ManabaseDeck deck = ManabaseClassifier.Classify(facts, isSingleton: true);

        var sb = new StringBuilder();
        sb.AppendLine("# MQ-02 baseline diff — Brago (Core analyzer)");
        sb.AppendLine();

        // Which sources actually got amount > 1 (the cards MQ-02 changes anything for).
        var multi = facts.Where(f => f.ManaAmount > 1).Select(f => $"{f.Name} ({f.ManaAmount})").ToList();
        sb.AppendLine($"Sources with mana amount > 1: {(multi.Count == 0 ? "(none)" : string.Join(", ", multi))}");
        sb.AppendLine();

        foreach (ManabaseMode mode in new[] { ManabaseMode.Casual, ManabaseMode.Cedh })
        {
            ManabaseReport off = ManabaseAnalyzer.Analyze(deck, mode, CommanderImportance.Standard, null, useManaQuantity: false);
            ManabaseReport on = ManabaseAnalyzer.Analyze(deck, mode, CommanderImportance.Standard, null, useManaQuantity: true);

            sb.AppendLine($"## {mode}");
            sb.AppendLine();
            sb.AppendLine($"- Health: off={off.Health} → on={on.Health}");
            sb.AppendLine($"- Lands {off.ActualLands} / target {off.TargetLands:F1} (unchanged by MQ-02)");
            sb.AppendLine($"- Weakest color: off={off.WeakestColor?.Color.ToString() ?? "none"} → on={on.WeakestColor?.Color.ToString() ?? "none"}");

            var offByName = off.Castability.ToDictionary(c => c.Name, c => c.CastPercent, StringComparer.Ordinal);
            var rows = on.Castability
                .Select(c => (c.Name, c.ManaValue, Off: offByName.TryGetValue(c.Name, out int o) ? o : -1, On: c.CastPercent))
                .Select(r => (r.Name, r.ManaValue, r.Off, r.On, Delta: r.On - r.Off))
                .Where(r => r.Off >= 0)
                .ToList();

            double meanAbs = rows.Count > 0 ? rows.Average(r => Math.Abs(r.Delta)) : 0;
            int changed = rows.Count(r => r.Delta != 0);
            int maxUp = rows.Count > 0 ? rows.Max(r => r.Delta) : 0;
            sb.AppendLine($"- Cast%: {changed}/{rows.Count} cards changed · mean |Δ| {meanAbs:F1} pts · max +{maxUp}");
            sb.AppendLine();
            sb.AppendLine("| Card | MV | Off | On | Δ |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var r in rows.OrderByDescending(r => r.Delta).ThenBy(r => r.Name).Where(r => r.Delta != 0))
            {
                sb.AppendLine($"| {r.Name} | {r.ManaValue} | {r.Off} | {r.On} | {r.Delta:+0;-0} |");
            }

            sb.AppendLine();
        }

        string outDir = Path.Combine(RepoPaths.Root(), ".planning", "phases", "70-manabase-accuracy-mana-quantity");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, "70-02-baseline-diff.md");
        await File.WriteAllTextAsync(outPath, sb.ToString());

        // Echo to test output too.
        System.Console.WriteLine(sb.ToString());
    }
}
