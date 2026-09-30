using System.Text.Json;
using Vett.Runner;

namespace Vett.Tests;

/// <summary>
/// Lock the summary.json wire format. AI Timeline's vett.ts parser keys
/// off snake_case fields (instance_id, total_iterations, etc.). A typo
/// or refactor that PascalCases anything would silently break the
/// visualizer with no compile-time warning.
/// </summary>
public class RunSummaryWireFormatTests
{
    [Fact]
    public void RunSummary_RoundTrips_With_SnakeCase_Keys()
    {
        var summary = new RunSummary
        {
            RunId = "run-2026-04-29",
            Suite = "swe-bench-verified",
            Profile = "openhands",
            Model = "nvidia/Nemotron-3-Super-120B-A12B-FP8",
            InstanceCount = 25,
            Completed = 21,
            Errored = 4,
            DurationSec = 13_440.5,
            TotalInputTokens = 1_234_567,
            TotalOutputTokens = 234_567,
            StartedAt = "2026-04-29T08:00:00Z",
            CompletedAt = "2026-04-29T11:44:00Z",
            Instances = [
                new InstanceResult
                {
                    InstanceId = "django__django-12345",
                    Patch = "diff --git a/x.py b/x.py\n+pass",
                    Iterations = 42,
                    InputTokens = 50_000,
                    OutputTokens = 5_000,
                    DurationSec = 120.5,
                    EndReason = "finish_tool",
                    StartedAt = "2026-04-29T08:01:00Z",
                    CompletedAt = "2026-04-29T08:03:00Z",
                },
            ],
        };

        var json = JsonSerializer.Serialize(summary);

        // Every snake_case key the AI Timeline parser keys off MUST appear.
        Assert.Contains("\"run_id\":", json);
        Assert.Contains("\"suite_name\":", json);
        Assert.Contains("\"profile_name\":", json);
        Assert.Contains("\"model\":", json);
        Assert.Contains("\"total\":", json);
        Assert.Contains("\"completed\":", json);
        Assert.Contains("\"errored\":", json);
        Assert.Contains("\"total_duration_seconds\":", json);
        Assert.Contains("\"total_input_tokens\":", json);
        Assert.Contains("\"total_output_tokens\":", json);
        Assert.Contains("\"started_at\":", json);
        Assert.Contains("\"completed_at\":", json);
        Assert.Contains("\"instances\":", json);
        Assert.Contains("\"instance_id\":", json);
        Assert.Contains("\"patch\":", json);
        Assert.Contains("\"total_iterations\":", json);
        Assert.Contains("\"duration_seconds\":", json);
        Assert.Contains("\"end_reason\":", json);

        // None of the original PascalCase property names should leak.
        Assert.DoesNotContain("\"RunId\":", json);
        Assert.DoesNotContain("\"InstanceCount\":", json);
        Assert.DoesNotContain("\"Iterations\":", json);
        Assert.DoesNotContain("\"DurationSec\":", json);

        // Round-trip — deserializing the JSON gives us the same data back.
        var rt = JsonSerializer.Deserialize<RunSummary>(json)!;
        Assert.Equal(summary.RunId, rt.RunId);
        Assert.Equal(summary.Instances.Count, rt.Instances.Count);
        Assert.Equal(summary.Instances[0].InstanceId, rt.Instances[0].InstanceId);
        Assert.Equal(summary.Instances[0].Iterations, rt.Instances[0].Iterations);
        Assert.Equal(summary.Instances[0].InputTokens, rt.Instances[0].InputTokens);
        Assert.Equal(summary.TotalInputTokens, rt.TotalInputTokens);
        Assert.Equal(summary.StartedAt, rt.StartedAt);
    }

    [Fact]
    public void InstanceResult_Defaults_Are_Empty_Strings_And_Zero()
    {
        var r = new InstanceResult();
        Assert.Equal("", r.InstanceId);
        Assert.Equal("", r.Patch);
        Assert.Equal("", r.EndReason);
        Assert.Equal(0, r.Iterations);
        Assert.Null(r.Error);
        Assert.Null(r.StartedAt);
        Assert.Null(r.CompletedAt);
    }

    [Fact]
    public void RunSummary_With_No_Instances_Still_Serializes()
    {
        var summary = new RunSummary { RunId = "empty-run" };
        var json = JsonSerializer.Serialize(summary);
        Assert.Contains("\"instances\":[]", json);
        Assert.Contains("\"run_id\":\"empty-run\"", json);
    }
}
