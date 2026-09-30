using System.Text.Json.Serialization;

namespace Vett.Bench.Team;

// Wire-format for `vett team-bench --json`. Field names locked to
// snake_case via [JsonPropertyName] — downstream consumers (Tier 1.24
// event stream, AI Timeline, dashboards) key off these names, so a
// rename silently breaks them. Mirrors the convention in
// Vett.Runner.RunSummary.
public sealed class TeamBenchJsonSummary
{
    [JsonPropertyName("schema_version")]         public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("kind")]                   public string Kind { get; set; } = "team_bench_summary";
    [JsonPropertyName("run_id")]                 public string RunId { get; set; } = "";
    [JsonPropertyName("suite_name")]             public string SuiteName { get; set; } = "";
    [JsonPropertyName("suite_tier")]             public int SuiteTier { get; set; }
    [JsonPropertyName("profile_name")]           public string ProfileName { get; set; } = "";
    [JsonPropertyName("profile_overridden")]     public bool ProfileOverridden { get; set; }

    /// <summary>Absolute path of the profile FILE this run actually loaded.
    /// profile_name alone cannot identify the ruler: the same name resolves
    /// from &lt;cwd&gt;/profiles, then ~/.vett/profiles, then the install dir,
    /// and those copies are not required to agree. Null only on legacy
    /// summaries written before 2026-08-24 and on team_bench_error
    /// envelopes emitted before the profile resolved.</summary>
    [JsonPropertyName("profile_path")]           public string? ProfilePath { get; set; }

    /// <summary>Keys present in the profile YAML that no C# property binds,
    /// i.e. settings this run SILENTLY IGNORED. Empty is the healthy value;
    /// null means the audit did not run (legacy summary). A non-empty list
    /// means at least one knob in the profile did not reach the code, so any
    /// A/B against another arm may have compared two identical
    /// configurations.</summary>
    [JsonPropertyName("profile_unknown_keys")]   public List<string>? ProfileUnknownKeys { get; set; }
    [JsonPropertyName("endpoint")]               public string Endpoint { get; set; } = "";
    [JsonPropertyName("model")]                  public string Model { get; set; } = "";
    [JsonPropertyName("repeat")]                 public int Repeat { get; set; }
    [JsonPropertyName("keep_all_worktrees")]     public bool KeepAllWorktrees { get; set; }
    [JsonPropertyName("instance_count")]         public int InstanceCount { get; set; }
    [JsonPropertyName("total_runs")]             public int TotalRuns { get; set; }
    [JsonPropertyName("total_passed")]           public int TotalPassed { get; set; }
    [JsonPropertyName("started_at")]             public string StartedAt { get; set; } = "";
    [JsonPropertyName("completed_at")]           public string CompletedAt { get; set; } = "";
    [JsonPropertyName("total_duration_seconds")] public double TotalDurationSeconds { get; set; }
    [JsonPropertyName("runs")]                   public List<TeamBenchJsonRun> Runs { get; set; } = new();
    [JsonPropertyName("per_instance")]           public List<TeamBenchJsonPerInstance> PerInstance { get; set; } = new();
    [JsonPropertyName("exit_code")]              public int ExitCode { get; set; }
}

public sealed class TeamBenchRunSelfAssessment
{
    [JsonPropertyName("captured")]      public bool Captured { get; set; }
    // Names WHICH not-captured state occurred — see TeamBenchSelfAssessment
    // .CaptureOutcome. Without it `captured: false` pools "the model declined"
    // with "the call threw", and the analyzer scores a harness fault as model
    // behaviour.
    [JsonPropertyName("capture_outcome")] public string? CaptureOutcome { get; set; }
    [JsonPropertyName("confidence")]    public int? Confidence { get; set; }
    [JsonPropertyName("predicted_pass")] public bool? PredictedPass { get; set; }
    [JsonPropertyName("reasoning")]     public string? Reasoning { get; set; }
    [JsonPropertyName("raw_text")]      public string? RawText { get; set; }
}

public sealed class TeamBenchJsonRun
{
    [JsonPropertyName("instance_id")]        public string InstanceId { get; set; } = "";
    [JsonPropertyName("run_index")]          public int RunIndex { get; set; }
    [JsonPropertyName("pass")]               public bool Pass { get; set; }
    [JsonPropertyName("wall_clock_seconds")] public double WallClockSeconds { get; set; }
    [JsonPropertyName("leader_iterations")]  public int LeaderIterations { get; set; }
    [JsonPropertyName("member_iterations")]  public Dictionary<string, int> MemberIterations { get; set; } = new();
    [JsonPropertyName("error")]              public string? Error { get; set; }
    /// <summary>
    /// The run's stop reason. ⛔ DO NOT RESTATE THE VALUE LIST HERE — see
    /// <see cref="TeamBenchResult.StopReason"/>, which is the single authority
    /// and carries the meaning of each value.
    ///
    /// This comment used to carry its own copy of the list, and the copy went
    /// stale exactly the way copies do: it still read "settled |
    /// wall_clock_timeout | leader_loop_exhausted | leader_faulted" after
    /// `leader_finished` had already shipped, and would have gone stale a
    /// second time when `harness_cancelled` landed 2026-08-26. A reader
    /// consulting the WIRE contract — which is what a calibration consumer
    /// reads, not the model class — was told a four-value vocabulary that had
    /// been six for some time. A pointer cannot drift; a duplicated list can,
    /// and silently, because nothing fails when a comment is wrong.
    ///
    /// ⚠ Two different fields in this codebase are called `stop_reason` and
    /// they are NOT the same instrument. THIS one is the team-bench run
    /// verdict. The OTHER is AgentLoop's own stop reason (AgentLoop.cs:17,
    /// emitted in the `done` event at :1279, values like llm_error /
    /// max_iterations / stalled) — that is the one infra/scripts read.
    /// Do not port a value from one vocabulary into the other.
    ///
    /// Null on legacy runs written before 2026-08-24 and on team_bench_error
    /// envelopes — absence is NOT "settled".
    /// </summary>
    [JsonPropertyName("stop_reason")]        public string? StopReason { get; set; }
    [JsonPropertyName("session_log_path")]   public string? SessionLogPath { get; set; }
    [JsonPropertyName("assertions")]         public List<TeamBenchJsonAssertion> Assertions { get; set; } = new();

    /// <summary>Tier 1.16 — instance's taxonomy block copied into every
    /// run so calibration consumers can slice without re-joining against
    /// the suite YAML. Null only for legacy team_bench_error envelopes
    /// (where the suite never loaded).</summary>
    [JsonPropertyName("taxonomy")]
    public Dictionary<string, string>? Taxonomy { get; set; }

    /// <summary>Tier 1B Q4 — agent's self-rated confidence + reasoning,
    /// captured by a post-settle follow-up turn. Null when not captured
    /// (older runs, error paths, harness skipped). The pair
    /// (self_assessment.predicted_pass, pass) is the input to the
    /// failure-flagging-recall metric.</summary>
    [JsonPropertyName("self_assessment")]
    public TeamBenchRunSelfAssessment? SelfAssessment { get; set; }
}

public sealed class TeamBenchJsonAssertion
{
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("pass")]        public bool Pass { get; set; }
    [JsonPropertyName("detail")]      public string? Detail { get; set; }
}

public sealed class TeamBenchJsonPerInstance
{
    [JsonPropertyName("instance_id")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("passed")]      public int Passed { get; set; }
    [JsonPropertyName("total")]       public int Total { get; set; }
}
