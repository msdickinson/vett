using System.Text.Json;
using Vett.Agent;
using Vett.Bench.Team;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Failure tests for team-bench RESULT AGGREGATION — the numbers the
/// harness publishes, not the work it drives.
///
/// WHY THIS FILE EXISTS: a crash is visible, a wrong number gets
/// published. Two things live here:
///
///  1. <see cref="Harness.IterationThreadId"/> — the single decision point
///     for which iteration counter an event increments. Before 2026-08-24
///     an `iteration_start` with NO thread_id (the solo / non-team code
///     path) matched neither the leader branch nor the member branch and
///     was counted NOWHERE, so `leader_iterations` was published as 0 for
///     every run of every solo suite, and `budget: leader_iters_max`
///     could not fail on those suites.
///
///  2. The `--json` wire format's round trip, including the two fields the
///     older TeamBenchJsonWireFormatTests document does not exercise
///     (`stop_reason`, `self_assessment`). A field that silently stops
///     serialising reads back as null months later and gets called zero.
/// </summary>
public class TeamBenchResultAggregationTests
{
    private static Event Iter(string? threadId, int n)
    {
        var data = new Dictionary<string, object?> { ["iteration"] = n };
        if (threadId is not null) data["thread_id"] = threadId;
        return new Event("iteration_start", data);
    }

    /// <summary>
    /// Mirrors the reduction inside Harness.RunInstanceAsync's OnEvent
    /// (Harness.cs, the `IterationThreadId(ev)` branch) so the aggregate
    /// consequence — not just the classifier's return value — is asserted.
    /// </summary>
    private static (int Leader, Dictionary<string, int> Members) Fold(IEnumerable<Event> events)
    {
        var leader = 0;
        var members = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ev in events)
        {
            var tid = Harness.IterationThreadId(ev);
            if (tid is null) continue;
            if (tid == Harness.LeaderThreadId) leader++;
            else
            {
                members.TryGetValue(tid, out var n);
                members[tid] = n + 1;
            }
        }
        return (leader, members);
    }

    // ---------------------------------------------------------------
    // 1. Iteration attribution
    // ---------------------------------------------------------------

    /// <summary>
    /// THE DEFECT. AgentLoop.Emit (AgentLoop.cs:293-294) does not tag
    /// events, so the solo path's iteration_start (AgentLoop.cs:489)
    /// carries only {"iteration": N}. Harness's solo branch wires OnEvent
    /// straight into AgentEnvironment with no tagging wrapper, unlike the
    /// team path which goes through TeamCoordinator's LeaderEmit
    /// (Coordinator.cs:1043-1049). An untagged iteration_start is the sole
    /// agent's, and the sole agent is the leader.
    /// </summary>
    [Fact]
    public void Untagged_iteration_start_is_attributed_to_the_leader()
    {
        Assert.Equal(Harness.LeaderThreadId, Harness.IterationThreadId(Iter(threadId: null, 1)));
    }

    /// <summary>
    /// The aggregate consequence: a solo run's published
    /// `leader_iterations`. A COULD-NOT-MEASURE that reads as a
    /// MEASURED-ZERO is the failure mode this asserts against — 0 here is
    /// exactly what the bug produced, and 0 is also what a genuinely
    /// zero-iteration run produces, so the two were indistinguishable.
    /// </summary>
    [Fact]
    public void Solo_event_stream_counts_leader_iterations_not_zero()
    {
        var (leader, members) = Fold(new[]
        {
            Iter(threadId: null, 1),
            new Event("tool_call_end", new() { ["tool_name"] = "mcp__spec_diff__diff_apply" }),
            Iter(threadId: null, 2),
            Iter(threadId: null, 3),
        });

        Assert.Equal(3, leader);
        Assert.Empty(members);
    }

    /// <summary>
    /// The gate this defect disarmed. suites/spec-authoring-tier0.yaml
    /// (profile spec-authoring-solo, no `team:` block) asserts
    /// `budget: leader_iters_max: 5` on every instance.
    /// AssertionEngine.EvalBudget fails only when the count EXCEEDS the
    /// cap, so while the count was pinned at 0 the assertion could not
    /// fail for any run — a gate that spans none of its population passes
    /// vacuously. Feed it an over-budget solo stream and require a FAIL.
    /// </summary>
    [Fact]
    public void Solo_over_budget_run_fails_the_leader_iters_max_assertion()
    {
        var events = Enumerable.Range(1, 9).Select(i => Iter(threadId: null, i)).ToList();
        var (leader, members) = Fold(events);

        var results = AssertionEngine.Evaluate(
            new List<TeamBenchAssertion> { new() { Budget = new BudgetSpec { LeaderItersMax = 5 } } },
            events,
            workspaceDir: Path.GetTempPath(),
            leaderIterations: leader,
            memberIterations: members,
            countsComplete: true);

        var only = Assert.Single(results);
        Assert.False(only.Pass);
        Assert.Contains("9", only.Detail ?? "");
    }

    /// <summary>
    /// PREDICT-THE-PASSES guard for the team path. Explicit thread_ids must
    /// keep their existing meaning: "main" is the leader, anything else is
    /// a member keyed by task id (Coordinator.cs:369). If this ever goes
    /// red the fix over-shot and started stealing member iterations.
    /// </summary>
    [Fact]
    public void Team_event_stream_attribution_is_unchanged()
    {
        var (leader, members) = Fold(new[]
        {
            Iter("main", 1),
            Iter("implementer-1", 1),
            Iter("implementer-1", 2),
            Iter("implementer-1", 3),
            Iter("researcher-1", 1),
            Iter("main", 2),
        });

        Assert.Equal(2, leader);
        Assert.Equal(3, members["implementer-1"]);
        Assert.Equal(1, members["researcher-1"]);
        Assert.Equal(2, members.Count);
    }

    /// <summary>Non-iteration events must not touch either counter — the
    /// classifier returning a thread id for them would double-count every
    /// tool call as an iteration.</summary>
    [Theory]
    [InlineData("dispatch_start")]
    [InlineData("dispatch_end")]
    [InlineData("tool_call_end")]
    [InlineData("llm_response")]
    public void Non_iteration_events_are_not_counted(string type)
    {
        var ev = new Event(type, new Dictionary<string, object?> { ["thread_id"] = "main" });
        Assert.Null(Harness.IterationThreadId(ev));
    }

    /// <summary>A thread_id present but not a string (or empty) is a
    /// malformed tag, not a member. Falling through to "member" would
    /// invent a phantom member keyed off ToString(); falling through to
    /// "counted nowhere" is the original bug. Leader is the honest
    /// default — it is the only thread guaranteed to exist.</summary>
    [Fact]
    public void Malformed_thread_id_falls_back_to_the_leader()
    {
        Assert.Equal(Harness.LeaderThreadId,
            Harness.IterationThreadId(new Event("iteration_start",
                new Dictionary<string, object?> { ["thread_id"] = 42 })));
        Assert.Equal(Harness.LeaderThreadId,
            Harness.IterationThreadId(new Event("iteration_start",
                new Dictionary<string, object?> { ["thread_id"] = "" })));
    }

    // ---------------------------------------------------------------
    // 2. JSON envelope round trip
    // ---------------------------------------------------------------

    /// <summary>
    /// Full round trip of every field TeamBenchCommand copies out of a
    /// TeamBenchResult, including the two the existing wire-format test
    /// leaves null: `stop_reason` and `self_assessment`. A rename or a
    /// dropped [JsonPropertyName] on either reads back as null downstream,
    /// and NULL IS NOT "settled" / "not captured" — it is "we don't know".
    /// </summary>
    [Fact]
    public void Every_run_field_survives_a_serialize_deserialize_round_trip()
    {
        var run = new TeamBenchJsonRun {
            InstanceId = "sa-t0-py-01-bump-literal",
            RunIndex = 2,
            Pass = false,
            WallClockSeconds = 41.5,
            LeaderIterations = 7,
            MemberIterations = new() { ["implementer-1"] = 4, ["researcher-1"] = 2 },
            Error = "leader loop ENDED after 200 iterations",
            StopReason = "leader_loop_exhausted",
            SessionLogPath = @"C:\tmp\ws\_bench-session.jsonl",
            Assertions = [ new TeamBenchJsonAssertion { Description = "budget leader<=5", Pass = false, Detail = "leader ran 7 iters (cap 5)" } ],
            Taxonomy = new() { ["language"] = "python" },
            SelfAssessment = new TeamBenchRunSelfAssessment {
                Captured = true, Confidence = 30, PredictedPass = false,
                Reasoning = "Unsure the exit was renamed.", RawText = "CONFIDENCE: 30\nREASON: Unsure the exit was renamed.",
            },
        };
        var summary = new TeamBenchJsonSummary {
            RunId = "team-bench-20260824T000000Z-spec-authoring-tier0",
            SuiteName = "spec-authoring-tier0", SuiteTier = 0,
            ProfileName = "spec-authoring-solo", ProfileOverridden = true,
            Endpoint = "http://gpu-1:8000/v1", Model = "aeon-mtp",
            Repeat = 3, KeepAllWorktrees = true,
            InstanceCount = 1, TotalRuns = 3, TotalPassed = 1,
            StartedAt = "2026-08-24T00:00:00.0000000Z",
            CompletedAt = "2026-08-24T00:10:00.0000000Z",
            TotalDurationSeconds = 600.25,
            ExitCode = 1,
            Runs = [ run ],
            PerInstance = [ new TeamBenchJsonPerInstance { InstanceId = "sa-t0-py-01-bump-literal", Passed = 1, Total = 3 } ],
        };

        var json = JsonSerializer.Serialize(summary);
        // Keys the older wire-format test does not lock.
        Assert.Contains("\"stop_reason\":\"leader_loop_exhausted\"", json);
        Assert.Contains("\"self_assessment\":", json);

        var rt = JsonSerializer.Deserialize<TeamBenchJsonSummary>(json)!;

        Assert.Equal(1, rt.SchemaVersion);
        Assert.Equal("team_bench_summary", rt.Kind);
        Assert.Equal(summary.RunId, rt.RunId);
        Assert.Equal(summary.SuiteName, rt.SuiteName);
        Assert.Equal(summary.SuiteTier, rt.SuiteTier);
        Assert.Equal(summary.ProfileName, rt.ProfileName);
        Assert.True(rt.ProfileOverridden);
        Assert.Equal(summary.Endpoint, rt.Endpoint);
        Assert.Equal(summary.Model, rt.Model);
        Assert.Equal(summary.Repeat, rt.Repeat);
        Assert.True(rt.KeepAllWorktrees);
        Assert.Equal(summary.InstanceCount, rt.InstanceCount);
        Assert.Equal(summary.TotalRuns, rt.TotalRuns);
        Assert.Equal(summary.TotalPassed, rt.TotalPassed);
        Assert.Equal(summary.StartedAt, rt.StartedAt);
        Assert.Equal(summary.CompletedAt, rt.CompletedAt);
        Assert.Equal(summary.TotalDurationSeconds, rt.TotalDurationSeconds);
        Assert.Equal(summary.ExitCode, rt.ExitCode);
        Assert.Equal(summary.PerInstance[0].Passed, rt.PerInstance[0].Passed);
        Assert.Equal(summary.PerInstance[0].Total, rt.PerInstance[0].Total);

        var r = Assert.Single(rt.Runs);
        Assert.Equal(run.InstanceId, r.InstanceId);
        Assert.Equal(run.RunIndex, r.RunIndex);
        Assert.False(r.Pass);
        Assert.Equal(run.WallClockSeconds, r.WallClockSeconds);
        Assert.Equal(run.LeaderIterations, r.LeaderIterations);
        Assert.Equal(4, r.MemberIterations["implementer-1"]);
        Assert.Equal(2, r.MemberIterations["researcher-1"]);
        Assert.Equal(run.Error, r.Error);
        Assert.Equal("leader_loop_exhausted", r.StopReason);
        Assert.Equal(run.SessionLogPath, r.SessionLogPath);
        Assert.Equal("python", r.Taxonomy!["language"]);
        Assert.Equal("budget leader<=5", r.Assertions[0].Description);
        Assert.False(r.Assertions[0].Pass);
        Assert.Equal("leader ran 7 iters (cap 5)", r.Assertions[0].Detail);
        Assert.NotNull(r.SelfAssessment);
        Assert.True(r.SelfAssessment!.Captured);
        Assert.Equal(30, r.SelfAssessment.Confidence);
        Assert.False(r.SelfAssessment.PredictedPass);
        Assert.Equal(run.SelfAssessment!.Reasoning, r.SelfAssessment.Reasoning);
        Assert.Equal(run.SelfAssessment.RawText, r.SelfAssessment.RawText);
    }

    /// <summary>
    /// stop_reason absence must stay distinguishable from "settled".
    /// Legacy runs and the team_bench_error envelope both carry null; a
    /// consumer that coalesced null to "settled" would silently re-create
    /// the collapse HarnessStopReasonTests exists to prevent.
    /// </summary>
    [Fact]
    public void Null_stop_reason_round_trips_as_null_not_settled()
    {
        var json = JsonSerializer.Serialize(new TeamBenchJsonRun { InstanceId = "legacy" });
        Assert.Contains("\"stop_reason\":null", json);
        Assert.Null(JsonSerializer.Deserialize<TeamBenchJsonRun>(json)!.StopReason);
    }
}
