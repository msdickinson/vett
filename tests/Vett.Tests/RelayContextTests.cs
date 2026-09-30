using Vett.Agent;
using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// R1 (relay): context-fullness telemetry decision logic + YAML schema.
/// The loop wiring stages a nudge at usage capture and injects it at the
/// next iteration top; these tests pin the pure decision math and the
/// config contract (absent block / zero window = feature entirely off).
/// </summary>
public class RelayContextTests
{
    // ── Pct ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 131072, 0)]        // no usage reported -> 0, never nudge
    [InlineData(65536, 131072, 50)]
    [InlineData(85197, 131072, 65)]   // exact threshold boundary
    [InlineData(131072, 131072, 100)]
    [InlineData(700000, 1048576, 66)] // v4 1M window
    [InlineData(50000, 0, 0)]         // window 0 = disabled -> 0
    public void Pct_Computes_WholePercent_And_Guards_Zero(int tokens, int window, int expected)
        => Assert.Equal(expected, RelayContext.Pct(tokens, window));

    [Fact]
    public void Pct_Does_Not_Overflow_On_Large_Inputs()
        // 2^31-ish tokens x 100 overflows int; the 100L multiply must not.
        => Assert.Equal(100, RelayContext.Pct(int.MaxValue, int.MaxValue));

    // ── ShouldNudge ──────────────────────────────────────────────────

    [Fact]
    public void Below_Threshold_Never_Nudges()
        => Assert.False(RelayContext.ShouldNudge(64, 65, 0));

    [Fact]
    public void First_Crossing_Nudges()
        => Assert.True(RelayContext.ShouldNudge(65, 65, 0));

    [Fact]
    public void Same_Pct_Does_Not_ReNudge()
        => Assert.False(RelayContext.ShouldNudge(65, 65, 65));

    [Fact]
    public void Four_Points_Later_Does_Not_ReNudge()
        => Assert.False(RelayContext.ShouldNudge(69, 65, 65));

    [Fact]
    public void Five_Points_Later_ReNudges()
        => Assert.True(RelayContext.ShouldNudge(70, 65, 65));

    [Fact]
    public void Big_First_Crossing_Then_Five_Point_Cadence()
    {
        // First observation is already at 90 -> nudge, remember 90.
        Assert.True(RelayContext.ShouldNudge(90, 65, 0));
        Assert.False(RelayContext.ShouldNudge(94, 65, 90));
        Assert.True(RelayContext.ShouldNudge(95, 65, 90));
    }

    // ── NudgeText: the R2 supervisor contract ────────────────────────

    [Fact]
    public void NudgeText_Names_Pct_Threshold_And_HANDOFF_Token()
    {
        var cfg = new RelayConfig { ContextWindow = 131072, HandoffThresholdPct = 65 };
        var text = RelayContext.NudgeText(72, cfg);
        Assert.Contains("72%", text);
        Assert.Contains("65%", text);
        Assert.Contains("HANDOFF", text);          // R2 grep contract
        Assert.Contains("declare_done", text);
        Assert.Contains("Do NOT start new work", text);
    }

    // ── YAML schema ──────────────────────────────────────────────────

    [Fact]
    public void Relay_Block_Parses_With_Defaults()
    {
        var yaml = """
            name: relay-parse-test
            llm:
              provider: local
              endpoint: http://x/v1
              model: m
            relay:
              context_window: 1048576
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"relay-parse-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(tmp, yaml);
        try
        {
            var p = Vett.Config.Yaml.LoadProfile(tmp);
            Assert.NotNull(p.Relay);
            Assert.Equal(1_048_576, p.Relay!.ContextWindow);
            Assert.Equal(65, p.Relay!.HandoffThresholdPct); // default
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Omitted_Relay_Block_Is_Null_Feature_Off()
    {
        var yaml = """
            name: relay-absent-test
            llm:
              provider: local
              endpoint: http://x/v1
              model: m
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"relay-absent-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(tmp, yaml);
        try
        {
            var p = Vett.Config.Yaml.LoadProfile(tmp);
            Assert.Null(p.Relay);
        }
        finally { File.Delete(tmp); }
    }
}
