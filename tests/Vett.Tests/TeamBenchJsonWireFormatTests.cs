using System.Text.Json;
using Vett.Bench.Team;

namespace Vett.Tests;

// Locks the snake_case wire format of `vett team-bench --json` output.
// Tier 1.24 event-stream consumers + AI Timeline + bench-report.py read
// these keys; a PascalCase refactor would silently break them.
public class TeamBenchJsonWireFormatTests
{
    [Fact]
    public void TeamBenchJsonSummary_Serializes_With_SnakeCase_Keys()
    {
        var summary = new TeamBenchJsonSummary {
            RunId = "team-bench-20260511T120000Z-team-bugfix-tier2",
            SuiteName = "team-bugfix-tier2",
            SuiteTier = 2,
            ProfileName = "coding-team-v2",
            ProfileOverridden = false,
            Endpoint = "http://old-runner:8100/v1",
            Model = "aeon-qwen3.6-27b",
            Repeat = 1,
            KeepAllWorktrees = false,
            InstanceCount = 9,
            TotalRuns = 9,
            TotalPassed = 8,
            StartedAt = "2026-05-11T12:00:00.0000000Z",
            CompletedAt = "2026-05-11T12:15:00.0000000Z",
            TotalDurationSeconds = 900.5,
            ExitCode = 1,
            Runs = [
                new TeamBenchJsonRun {
                    InstanceId = "b1-divide-by-zero",
                    RunIndex = 0,
                    Pass = true,
                    WallClockSeconds = 42.7,
                    LeaderIterations = 5,
                    MemberIterations = new() { ["implementer"] = 8 },
                    Error = null,
                    SessionLogPath = "/tmp/vett-team-bench/.../session.jsonl",
                    Assertions = [
                        new TeamBenchJsonAssertion {
                            Description = "file Program.cs exists",
                            Pass = true,
                            Detail = null,
                        },
                    ],
                    Taxonomy = new() {
                        ["file_count_bucket"] = "1",
                        ["complexity_tier"] = "2",
                    },
                },
            ],
            PerInstance = [
                new TeamBenchJsonPerInstance { InstanceId = "b1-divide-by-zero", Passed = 1, Total = 1 },
            ],
        };

        var json = JsonSerializer.Serialize(summary);

        // Top-level keys
        Assert.Contains("\"schema_version\":", json);
        Assert.Contains("\"kind\":", json);
        Assert.Contains("\"run_id\":", json);
        Assert.Contains("\"suite_name\":", json);
        Assert.Contains("\"suite_tier\":", json);
        Assert.Contains("\"profile_name\":", json);
        Assert.Contains("\"profile_overridden\":", json);
        Assert.Contains("\"endpoint\":", json);
        Assert.Contains("\"model\":", json);
        Assert.Contains("\"repeat\":", json);
        Assert.Contains("\"keep_all_worktrees\":", json);
        Assert.Contains("\"instance_count\":", json);
        Assert.Contains("\"total_runs\":", json);
        Assert.Contains("\"total_passed\":", json);
        Assert.Contains("\"started_at\":", json);
        Assert.Contains("\"completed_at\":", json);
        Assert.Contains("\"total_duration_seconds\":", json);
        Assert.Contains("\"runs\":", json);
        Assert.Contains("\"per_instance\":", json);
        Assert.Contains("\"exit_code\":", json);

        // Per-run keys
        Assert.Contains("\"instance_id\":", json);
        Assert.Contains("\"run_index\":", json);
        Assert.Contains("\"pass\":", json);
        Assert.Contains("\"wall_clock_seconds\":", json);
        Assert.Contains("\"leader_iterations\":", json);
        Assert.Contains("\"member_iterations\":", json);
        Assert.Contains("\"session_log_path\":", json);
        Assert.Contains("\"assertions\":", json);
        Assert.Contains("\"taxonomy\":", json);
        Assert.Contains("\"file_count_bucket\":", json);
        Assert.Contains("\"complexity_tier\":", json);

        // Per-assertion + per-instance keys
        Assert.Contains("\"description\":", json);
        Assert.Contains("\"detail\":", json);
        Assert.Contains("\"passed\":", json);
        Assert.Contains("\"total\":", json);

        // Locked default values
        Assert.Contains("\"schema_version\":1", json);
        Assert.Contains("\"kind\":\"team_bench_summary\"", json);

        // Round-trip preserves field values
        var roundTripped = JsonSerializer.Deserialize<TeamBenchJsonSummary>(json)!;
        Assert.Equal("team-bugfix-tier2", roundTripped.SuiteName);
        Assert.Equal(9, roundTripped.TotalRuns);
        Assert.Equal(8, roundTripped.TotalPassed);
        Assert.Single(roundTripped.Runs);
        Assert.Equal("b1-divide-by-zero", roundTripped.Runs[0].InstanceId);
        Assert.Equal(8, roundTripped.Runs[0].MemberIterations["implementer"]);
        Assert.Single(roundTripped.Runs[0].Assertions);
        Assert.True(roundTripped.Runs[0].Assertions[0].Pass);
        Assert.NotNull(roundTripped.Runs[0].Taxonomy);
        Assert.Equal("1", roundTripped.Runs[0].Taxonomy!["file_count_bucket"]);
        Assert.Equal("2", roundTripped.Runs[0].Taxonomy!["complexity_tier"]);
    }

    [Fact]
    public void TeamBenchJsonSummary_Defaults_Are_Schema_V1()
    {
        var s = new TeamBenchJsonSummary();
        Assert.Equal(1, s.SchemaVersion);
        Assert.Equal("team_bench_summary", s.Kind);
    }
}
