using YamlDotNet.Serialization;

namespace Vett.Bench.Team;

/// <summary>
/// YAML model for a team-mode benchmark suite. Loaded from
/// <c>suites/team-*.yaml</c>. Each suite is a tier of sub-agent
/// dispatch scenarios with deterministic assertions — no LLM judge.
/// </summary>
public sealed class TeamBenchSuite
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Profile name (e.g. "coding-team"). Resolved against the
    /// usual profile search path: ~/.vett/profiles, then bundled defaults.</summary>
    public string Profile { get; set; } = "";

    /// <summary>1, 2, or 3. Tiers are policy: tier 1 is "must always pass",
    /// tier 2 "should pass", tier 3 "want it to pass." The runner doesn't
    /// gate on this — it's metadata for reporting.</summary>
    public int Tier { get; set; } = 1;

    public List<TeamBenchInstance> Instances { get; set; } = new();
}

/// <summary>
/// A single benchmark scenario: prompt + workspace setup + assertions.
/// </summary>
public sealed class TeamBenchInstance
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Workspace template name. Currently supported:
    /// <list type="bullet">
    /// <item><c>empty</c> — a fresh empty directory.</item>
    /// <item><c>empty-git-repo</c> — fresh dir + <c>git init</c> + an
    /// empty initial commit so dispatch worktrees can be created
    /// (worktrees require at least one commit on the parent).</item>
    /// </list>
    /// </summary>
    public string Workspace { get; set; } = "empty-git-repo";

    /// <summary>The user message sent to the leader. Multiline supported
    /// via YAML <c>|</c>. The runner sends this once and waits for the
    /// chat loop to settle.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Optional files to drop into the workspace before the run.
    /// Keys are paths relative to the workspace root, values are full
    /// file contents. For <c>empty-git-repo</c> templates, seed files
    /// are committed so dispatch worktrees see them.</summary>
    [YamlMember(Alias = "seed_files")]
    public Dictionary<string, string>? SeedFiles { get; set; }

    /// <summary>Hard wall-clock cap for the whole run, including dispatch
    /// time. Default 180s — enough for a 3-iter dispatch on aeon-mtp at
    /// typical ~5s per LLM call. Bump for scenarios that involve build
    /// or test runs.</summary>
    [YamlMember(Alias = "timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 180;

    public List<TeamBenchAssertion> Assertions { get; set; } = new();

    /// <summary>Tier 1.14 task taxonomy — dimension-name → value pairs.
    /// Validated against <see cref="Vett.Bench.Team.TaxonomySchema"/>
    /// (bundled at <c>defaults/taxonomy.yaml</c>). Required on every
    /// instance so calibration can slice results by task class from
    /// day one. Values are strings (YAML may emit ints; they coerce).</summary>
    public Dictionary<string, string>? Taxonomy { get; set; }
}

/// <summary>
/// One assertion to evaluate after the run finishes. Exactly one of the
/// four kinds (<see cref="Event"/>, <see cref="ToolCall"/>, <see cref="File"/>,
/// <see cref="Budget"/>, <see cref="NoEvent"/>) must be set.
/// </summary>
public sealed class TeamBenchAssertion
{
    /// <summary>Match an emitted event by type + optional field constraints.</summary>
    public string? Event { get; set; }

    /// <summary>Assert NO event of this type was emitted across the run.
    /// Pair with <see cref="MaxCount"/> to tolerate up to N firings — useful
    /// for soft signals like <c>malformed_tool_call</c> where one parser-caught
    /// retry is recovery-working-as-designed, not a real model failure.</summary>
    [YamlMember(Alias = "no_event")]
    public string? NoEvent { get; set; }

    /// <summary>For <see cref="NoEvent"/>: maximum tolerated count of the
    /// event before failing. Default 0 (strict).</summary>
    [YamlMember(Alias = "max_count")]
    public int? MaxCount { get; set; }

    /// <summary>For <see cref="Event"/> and <see cref="ToolCall"/>: the
    /// minimum number of matches that must satisfy EVERY other constraint on
    /// the assertion. Defaults to 1, which is the historical behaviour (both
    /// evaluators returned on the first satisfying match), so omitting this
    /// changes nothing.
    ///
    /// Added 2026-08-26 for LEADER FAN-OUT. A sweep of 61 team-bench runs
    /// showed a maximum fan-out of ONE member across the whole corpus, and
    /// there was no way to express "the leader must dispatch at least 5" —
    /// only <c>max_count</c> existed, and it applies to <c>no_event</c>. Fan-out
    /// was recorded in <c>member_iterations</c> but nothing could GATE on it,
    /// so a run that quietly stopped fanning out still passed.</summary>
    [YamlMember(Alias = "min_count")]
    public int? MinCount { get; set; }

    /// <summary>Match a tool_call_end event by tool name + optional success
    /// + optional argument constraints.</summary>
    [YamlMember(Alias = "tool_call")]
    public string? ToolCall { get; set; }

    /// <summary>Path relative to the workspace to inspect after the run.</summary>
    public string? File { get; set; }

    /// <summary>Iteration-count budget assertion; see <see cref="BudgetSpec"/>.</summary>
    public BudgetSpec? Budget { get; set; }

    // -- shared sub-fields ---------------------------------------------

    /// <summary>For <see cref="Event"/>: only consider events with this
    /// member field. Useful for narrowing dispatch_end to a specific
    /// implementer/researcher.</summary>
    public string? Member { get; set; }

    /// <summary>For <see cref="Event"/>: <c>files_changed</c> field must be at least this.</summary>
    [YamlMember(Alias = "files_changed_min")]
    public int? FilesChangedMin { get; set; }

    /// <summary>For <see cref="Event"/>: <c>pending_review</c> field must equal this.</summary>
    [YamlMember(Alias = "pending_review")]
    public bool? PendingReview { get; set; }

    /// <summary>For <see cref="ToolCall"/>: required success status.</summary>
    public bool? Success { get; set; }

    /// <summary>For <see cref="ToolCall"/>: minimum length of specific
    /// string args. Validates the LLM gave a substantive response (e.g.
    /// the <c>review</c> field on <c>accept_dispatch</c> should be a
    /// real review, not "ok").</summary>
    [YamlMember(Alias = "args_min_length")]
    public Dictionary<string, int>? ArgsMinLength { get; set; }

    /// <summary>For <see cref="ToolCall"/>: exact string-equals match on
    /// specific args. Used to verify a multi-command tool was invoked
    /// with a particular sub-command (e.g. file_editor with command="insert"
    /// vs "str_replace") rather than just "tool was called at all".</summary>
    [YamlMember(Alias = "args_equals")]
    public Dictionary<string, string>? ArgsEquals { get; set; }

    /// <summary>For <see cref="File"/>: file must exist (true) or must
    /// not exist (false). Defaults to true when omitted.</summary>
    public bool? Exists { get; set; }

    /// <summary>For <see cref="File"/>: regex (single-line, IgnoreCase,
    /// Multiline) the file content must match.</summary>
    [YamlMember(Alias = "content_matches")]
    public string? ContentMatches { get; set; }

    /// <summary>Assert that some piece of leader-emitted assistant text
    /// (final-turn output to the user, no tool calls) contains this
    /// substring, case-insensitive. Use to verify the leader actually
    /// relayed an answer it got from a member.</summary>
    [YamlMember(Alias = "assistant_text_contains")]
    public string? AssistantTextContains { get; set; }

    /// <summary>V2: Run a shell command in the workspace AFTER the run
    /// finishes; assert exit_code + stdout regex. Built-in timeout +
    /// kill so a hung child can't wedge the bench.</summary>
    [YamlMember(Alias = "run_command")]
    public RunCommandSpec? RunCommand { get; set; }

    /// <summary>V4: Run <c>dotnet test</c> in the workspace and parse
    /// the pass/fail summary; assert min_passed and max_failed.
    /// Higher-level wrapper over RunCommand for the .NET test path.</summary>
    [YamlMember(Alias = "dotnet_test")]
    public DotnetTestSpec? DotnetTest { get; set; }
}

/// <summary>Parameters for the run_command assertion.</summary>
public sealed class RunCommandSpec
{
    /// <summary>Shell command to run. On Windows this goes through
    /// <c>cmd /c</c>, elsewhere through <c>bash -c</c>, so pipes,
    /// redirects, and globs work as expected.</summary>
    public string Command { get; set; } = "";

    /// <summary>Working directory relative to the workspace root.
    /// Defaults to "." (workspace root).</summary>
    public string Cwd { get; set; } = ".";

    /// <summary>Required exit code. Default 0.</summary>
    [YamlMember(Alias = "exit_code")]
    public int? ExitCode { get; set; } = 0;

    /// <summary>Optional regex applied to combined stdout+stderr.</summary>
    [YamlMember(Alias = "stdout_matches")]
    public string? StdoutMatches { get; set; }

    /// <summary>Hard cap; child is killed if it overruns. Default 60s.</summary>
    [YamlMember(Alias = "timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Parameters for the dotnet_test assertion.</summary>
public sealed class DotnetTestSpec
{
    /// <summary>Working directory relative to the workspace root —
    /// where the .slnx / .csproj lives. Default ".".</summary>
    public string Cwd { get; set; } = ".";

    /// <summary>Hard cap on the entire `dotnet test` invocation. Cold
    /// builds can take 60-90s on a fresh nuget cache; default 240s.</summary>
    [YamlMember(Alias = "timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 240;

    /// <summary>Minimum count of `Passed` tests. Use to reject the
    /// trivial-stub failure mode where 0 tests are collected.</summary>
    [YamlMember(Alias = "min_passed")]
    public int? MinPassed { get; set; }

    /// <summary>Maximum allowed `Failed` count. Default 0 — any failure
    /// fails the assertion.</summary>
    [YamlMember(Alias = "max_failed")]
    public int? MaxFailed { get; set; } = 0;
}

/// <summary>
/// Iteration-count budget. Each member dispatch and the leader thread
/// have their own iteration counters; assert max bounds.
/// </summary>
public sealed class BudgetSpec
{
    [YamlMember(Alias = "member_iters_max")]
    public int? MemberItersMax { get; set; }

    [YamlMember(Alias = "leader_iters_max")]
    public int? LeaderItersMax { get; set; }
}

/// <summary>
/// Result of evaluating a single instance.
/// </summary>
public sealed class TeamBenchResult
{
    public string InstanceId { get; set; } = "";
    /// <summary>
    /// PASS when every configured assertion passed. The <see cref="Error"/>
    /// string is diagnostic only — settle-timeout / cleanup-timeout do NOT
    /// override a passing assertion set, because the harness's job is to
    /// score the *work* (dotnet test, file content, event invariants), not
    /// whether the agent said "done" before the politeness window closed.
    /// Observed 2026-05-12: A2 method-tier1 had 2 runs where dotnet test
    /// passed cleanly, every assertion was green, and the only "fault" was
    /// settle-timeout — scoring those FAIL undercounted real capability.
    /// </summary>
    public bool Pass => AssertionResults.Count > 0 && AssertionResults.All(r => r.Pass);
    public List<AssertionResult> AssertionResults { get; set; } = new();
    public int LeaderIterations { get; set; }
    public Dictionary<string, int> MemberIterations { get; set; } = new();
    public TimeSpan WallClock { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Why the settle loop stopped. Exactly one of SIX values — the
    /// vocabulary is pinned two-sided by
    /// <c>HarnessStopReasonTests.Vocabulary_is_exactly_the_six_documented_values</c>,
    /// so adding a value here without adding it there fails the build's
    /// intent, not just its text:
    ///
    ///   <c>settled</c>               the leader parked and the stream went quiet
    ///   <c>leader_finished</c>       the leader's loop RETURNED cleanly
    ///   <c>harness_cancelled</c>     the OUTER token fired — see below
    ///   <c>leader_faulted</c>        the leader's loop threw
    ///   <c>leader_loop_exhausted</c> the loop ended without signalling
    ///   <c>wall_clock_timeout</c>    the budget genuinely elapsed
    ///
    /// Added 2026-08-24 because the three non-settled cases were previously
    /// indistinguishable — all three surfaced as the same
    /// `timeout after Ns without settle` string, so a leader that ended at
    /// its iteration cap and a leader that was still working when the clock
    /// ran out produced byte-identical verdicts. That ambiguity mis-labelled
    /// campaign run armA-run4 (leader recorded exactly 200 against
    /// <c>team.leader.max_iterations: 200</c>) as a wall-clock timeout.
    ///
    /// ⛔ <c>harness_cancelled</c> (added 2026-08-26) IS AN ADMISSIBILITY
    /// VERDICT, NOT A SCORE. It means the run was stopped from OUTSIDE —
    /// Ctrl+C, or System.CommandLine cancelling mid-action — rather than
    /// finishing, timing out, or failing. Such a run is VOID: it must not be
    /// pooled with model results, and it must NOT be read as evidence for
    /// raising a timeout, because the budget was never reached. It is
    /// deliberately ranked ABOVE <c>leader_faulted</c> and BELOW the clean
    /// finishes: a leader that already finished cleanly is reported on its own
    /// merits, but a fault observed while the harness was tearing the run down
    /// is not trustworthy evidence about the model. Note this label says only
    /// THAT the token fired, never WHY — diagnosing the cause is a separate
    /// question the harness cannot answer from the token alone.
    ///
    /// Null on the error envelope paths where the loop never started.
    /// </summary>
    public string? StopReason { get; set; }

    /// <summary>
    /// THE INPUTS THE LABEL WAS COMPUTED FROM, so the label can be audited
    /// instead of trusted.
    /// </summary>
    /// <remarks>
    /// <see cref="StopReason"/> is a four-way collapse of the boolean triple
    /// (settled, leaderEnded, leaderFaulted) plus harnessCancelled and the
    /// leader's own stop reason. Two genuinely different worlds print the
    /// SAME label and cannot be told apart afterwards:
    ///
    ///   - the leader was still running when the clock expired  → a real timeout
    ///   - the leader had already stopped, but its Task object had not been
    ///     OBSERVED complete at the instant we classified → a mislabel
    ///
    /// Both emit <c>wall_clock_timeout</c>. That is the same
    /// could-not-measure-read-as-measured collapse this class documents
    /// everywhere else, one level further out, and it bit for real: on
    /// 2026-08-28 a suite run under full parallel load reported
    /// <c>wall_clock_timeout</c> for a leader that had provably exhausted its
    /// iteration cap (leader_iterations was 1 of a max of 1).
    ///
    /// This string is DIAGNOSTIC, never a score. Nothing branches on it, and
    /// nothing may: it exists so a human — or a premise assertion in a test —
    /// can ask "was the leader actually still running?" and get an answer
    /// rather than an inference. Written on every path that sets
    /// <see cref="StopReason"/> from the classifier; null when the loop never
    /// started and no classification happened.
    /// </remarks>
    public string? StopEvidence { get; set; }

    /// <summary>Path to the per-instance JSONL session log (every event
    /// the loop emitted). Preserved on FAIL for post-mortem; deleted on
    /// PASS along with the workspace dir.</summary>
    public string? SessionLogPath { get; set; }

    /// <summary>Tier 1B Q4 self-assessment — after the main work settles
    /// the harness asks the agent to rate its own confidence + reasoning.
    /// Null when capture fails or is skipped (e.g. timeout). The actual
    /// pass/fail from the harness is in <see cref="Pass"/>; comparing
    /// <see cref="TeamBenchSelfAssessment.PredictedPass"/> vs that is
    /// what the failure-flagging-recall metric is built on.</summary>
    public TeamBenchSelfAssessment? SelfAssessment { get; set; }
}

/// <summary>
/// Captured agent self-assessment for Q4 calibration. Populated by the
/// harness's post-settle follow-up turn that asks the agent to report
/// `CONFIDENCE: N` and `REASON: <one sentence>`. Null fields = parse
/// failure; raw text always preserved for post-mortem.
/// </summary>
public sealed class TeamBenchSelfAssessment
{
    /// <summary>0–100. Null when the agent's reply couldn't be parsed
    /// for a CONFIDENCE: N token.</summary>
    public int? Confidence { get; set; }

    /// <summary>One-sentence reasoning captured from the REASON: line.
    /// Null when not present.</summary>
    public string? Reasoning { get; set; }

    /// <summary>Derived from Confidence: true when Confidence ≥ 50, false
    /// when &lt; 50, null when Confidence is null. Used to compute
    /// failure-flagging recall against <see cref="TeamBenchResult.Pass"/>.</summary>
    public bool? PredictedPass { get; set; }

    /// <summary>The raw text of the agent's follow-up turn, capped at
    /// 2000 chars. Useful for diagnosing parse failures and seeing the
    /// model's actual phrasing.</summary>
    public string? RawText { get; set; }

    /// <summary>Whether the harness captured a follow-up response at all
    /// (true) or had to skip the prompt (e.g. main run timed out, agent
    /// loop refused, or settle never re-fired). Lets the analyzer
    /// distinguish "agent said low confidence" from "we couldn't ask".</summary>
    public bool Captured { get; set; }

    /// <summary>
    /// WHICH of the not-captured states this was. `Captured` alone is a
    /// single bool covering causes that are not interchangeable:
    ///   answered       — a reply came back (Captured=true).
    ///   refused_empty  — we asked, the model returned nothing. A real
    ///                    measurement: this model, on this run, declined.
    ///   ask_failed     — the follow-up CALL threw (context overflow on a
    ///                    long history, transport error). We never got to ask.
    ///   no_leader      — the leader faulted/cancelled; no history to ask
    ///                    against and no agent left to ask.
    ///   channel_closed — the live-leader path could not even push the prompt.
    ///   settle_timeout — prompt pushed, no fresh user_input_needed in the
    ///                    window, AND the leader was still running when the
    ///                    window closed. A genuine we-waited-and-nothing-came.
    ///   leader_ended_unanswered
    ///                  — prompt pushed, and the leader’s loop then ENDED on a
    ///                    non-clean stop (llm_error, empty_response,
    ///                    malformed_tool_call, cancelled...). It never had the
    ///                    chance to answer. Split out of `settle_timeout`,
    ///                    which used to absorb it: a transport or provider
    ///                    fault scored there reads as “the model went quiet”,
    ///                    which is the same pooling this whole field exists to
    ///                    prevent — and it biases the same direction, because
    ///                    a loop is likelier to die the longer it has run.
    ///
    /// COULD NOT MEASURE IS NOT MEASURED ZERO. Failure-flagging recall is
    /// computed over exactly this column, so pooling `ask_failed` (an
    /// infrastructure fault, and one that gets MORE likely the longer the run)
    /// with `refused_empty` (a genuine model behaviour) would let harness
    /// breakage masquerade as model calibration — and would bias hardest on
    /// precisely the long runs that matter most. The warning was already
    /// logged; a log line is not a column the analyzer reads.
    ///
    /// `Captured` is left untouched so existing summaries and scripts keep
    /// their meaning; this only adds the discriminator beside it.
    /// </summary>
    public string? CaptureOutcome { get; set; }
}

public sealed class AssertionResult
{
    public string Description { get; set; } = "";
    public bool Pass { get; set; }
    public string? Detail { get; set; }
}
