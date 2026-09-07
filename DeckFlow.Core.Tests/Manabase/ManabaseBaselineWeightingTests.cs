using DeckFlow.Core.Manabase;

namespace DeckFlow.Core.Tests;

/// <summary>
/// Tests for the confidence-weighted manabase baseline: commander value when the sample is solid,
/// linear blend toward the global bracket baseline in the mid band, global when the sample is thin
/// or the commander cell is missing.
/// </summary>
public sealed class ManabaseBaselineWeightingTests
{
    [Fact]
    public void Solid_sample_uses_commander_values()
    {
        var r = ManabaseBaselineWeighting.Compute(
            commanderLands: 34, commanderRamp: 10, commanderDraw: 9, commanderDeckCount: 500,
            globalLands: 35.5, globalRamp: 9, globalDraw: 8);

        Assert.Equal(34, r.Lands.Value);
        Assert.Equal(ManabaseBaselineSource.Commander, r.Lands.Source);
        Assert.Equal(10, r.Ramp.Value);
        Assert.Equal(ManabaseBaselineSource.Commander, r.Ramp.Source);
        Assert.Equal(9, r.Draw.Value);
        Assert.Equal(ManabaseBaselineSource.Commander, r.Draw.Source);
        Assert.Equal(44, r.TotalSources);          // lands + ramp
        Assert.Equal(500, r.CommanderDeckCount);
    }

    [Fact]
    public void Thin_sample_uses_global_values()
    {
        var r = ManabaseBaselineWeighting.Compute(
            commanderLands: 34, commanderRamp: 10, commanderDraw: 9, commanderDeckCount: 50,
            globalLands: 35.5, globalRamp: 9, globalDraw: 8);

        Assert.Equal(35.5, r.Lands.Value);
        Assert.Equal(ManabaseBaselineSource.Global, r.Lands.Source);
        Assert.Equal(9, r.Ramp.Value);
        Assert.Equal(ManabaseBaselineSource.Global, r.Ramp.Source);
        Assert.Equal(8, r.Draw.Value);
        Assert.Equal(ManabaseBaselineSource.Global, r.Draw.Source);
    }

    [Fact]
    public void Mid_sample_blends_linearly()
    {
        // deckCount 250 -> w = (250-100)/(400-100) = 0.5
        var r = ManabaseBaselineWeighting.Compute(
            commanderLands: 30, commanderRamp: 12, commanderDraw: 10, commanderDeckCount: 250,
            globalLands: 36, globalRamp: 8, globalDraw: 6);

        Assert.Equal(33, r.Lands.Value!.Value, 3);        // 0.5*30 + 0.5*36
        Assert.Equal(ManabaseBaselineSource.Blended, r.Lands.Source);
        Assert.Equal(10, r.Ramp.Value!.Value, 3);         // 0.5*12 + 0.5*8
        Assert.Equal(ManabaseBaselineSource.Blended, r.Ramp.Source);
        Assert.Equal(8, r.Draw.Value!.Value, 3);          // 0.5*10 + 0.5*6
        Assert.Equal(ManabaseBaselineSource.Blended, r.Draw.Source);
    }

    [Fact]
    public void Missing_commander_cell_falls_back_to_global()
    {
        var r = ManabaseBaselineWeighting.Compute(
            commanderLands: null, commanderRamp: null, commanderDraw: null, commanderDeckCount: 0,
            globalLands: 35.5, globalRamp: 9, globalDraw: 8);

        Assert.Equal(35.5, r.Lands.Value);
        Assert.Equal(ManabaseBaselineSource.Global, r.Lands.Source);
        Assert.Equal(44.5, r.TotalSources!.Value, 3);     // 35.5 + 9
    }

    [Fact]
    public void Missing_both_yields_none()
    {
        var r = ManabaseBaselineWeighting.Compute(
            commanderLands: null, commanderRamp: null, commanderDraw: null, commanderDeckCount: 0,
            globalLands: null, globalRamp: null, globalDraw: null);

        Assert.Null(r.Lands.Value);
        Assert.Equal(ManabaseBaselineSource.None, r.Lands.Source);
        Assert.Null(r.TotalSources);               // can't sum with a null
    }

    [Fact]
    public void Metrics_are_independent_missing_draw_uses_global_draw()
    {
        // Solid sample, but this commander cell has no draw figure -> draw falls to global.
        var r = ManabaseBaselineWeighting.Compute(
            commanderLands: 34, commanderRamp: 10, commanderDraw: null, commanderDeckCount: 500,
            globalLands: 35.5, globalRamp: 9, globalDraw: 8);

        Assert.Equal(34, r.Lands.Value);
        Assert.Equal(ManabaseBaselineSource.Commander, r.Lands.Source);
        Assert.Equal(8, r.Draw.Value);
        Assert.Equal(ManabaseBaselineSource.Global, r.Draw.Source);
    }

    [Fact]
    public void Mid_sample_without_global_yields_none()
    {
        // Mid band but no global to blend against: we cannot express confidence, so omit the metric
        // rather than upgrade a weak sample to full trust. (Degenerate — global is normally always present.)
        var r = ManabaseBaselineWeighting.Compute(
            commanderLands: 30, commanderRamp: 12, commanderDraw: 10, commanderDeckCount: 250,
            globalLands: null, globalRamp: null, globalDraw: null);

        Assert.Null(r.Lands.Value);
        Assert.Equal(ManabaseBaselineSource.None, r.Lands.Source);
        Assert.Null(r.Ramp.Value);
        Assert.Equal(ManabaseBaselineSource.None, r.Ramp.Source);
        Assert.Null(r.TotalSources);
    }

    [Fact]
    public void At_low_threshold_weight_is_zero_so_value_equals_global_but_source_is_blended()
    {
        // deckCount == LOW (100): NOT below LOW, so it enters the blend with w = 0 -> value == global,
        // but the source is Blended (it went through the blend path, not the pure-global path).
        var r = ManabaseBaselineWeighting.Compute(
            commanderLands: 30, commanderRamp: 12, commanderDraw: 10, commanderDeckCount: 100,
            globalLands: 36, globalRamp: 8, globalDraw: 6);

        Assert.Equal(36, r.Lands.Value!.Value, 3);
        Assert.Equal(ManabaseBaselineSource.Blended, r.Lands.Source);
        Assert.Equal(8, r.Ramp.Value!.Value, 3);
        Assert.Equal(ManabaseBaselineSource.Blended, r.Ramp.Source);
    }

    [Fact]
    public void At_high_threshold_uses_commander()
    {
        // deckCount == HIGH (400): trusted fully.
        var r = ManabaseBaselineWeighting.Compute(
            commanderLands: 30, commanderRamp: 12, commanderDraw: 10, commanderDeckCount: 400,
            globalLands: 36, globalRamp: 8, globalDraw: 6);

        Assert.Equal(30, r.Lands.Value!.Value, 3);
        Assert.Equal(ManabaseBaselineSource.Commander, r.Lands.Source);
        Assert.Equal(12, r.Ramp.Value!.Value, 3);
        Assert.Equal(ManabaseBaselineSource.Commander, r.Ramp.Source);
        Assert.Equal(10, r.Draw.Value!.Value, 3);
        Assert.Equal(ManabaseBaselineSource.Commander, r.Draw.Source);
    }

    [Fact]
    public void TotalSources_is_null_when_a_component_is_null()
    {
        // Solid sample, lands present but ramp missing -> ramp falls to global; if global ramp is also
        // null, ramp is None/null and TotalSources cannot be summed.
        var r = ManabaseBaselineWeighting.Compute(
            commanderLands: 34, commanderRamp: null, commanderDraw: 9, commanderDeckCount: 500,
            globalLands: 35.5, globalRamp: null, globalDraw: 8);

        Assert.Equal(34, r.Lands.Value);
        Assert.Null(r.Ramp.Value);
        Assert.Equal(ManabaseBaselineSource.None, r.Ramp.Source);
        Assert.Null(r.TotalSources);
    }
}
