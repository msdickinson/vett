using Vett.Agent;
using Vett.Bench.Team;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Failure test for <see cref="Harness.ClassifyStop"/>.
///
/// WHY THIS EXISTS: before 2026-08-24 the harness reported all three
/// not-settled states as the single string `timeout after {N}s without
/// settle`. Campaign run armA-run4 entered the cohort under that label
/// with a leader that had recorded EXACTLY 200 iterations against a
/// configured `team.leader.max_iterations` of EXACTLY 200 — i.e. almost
/// certainly iteration exhaustion wearing a wall-clock label, where the
/// clock was a bystander. A label on a failure is itself a measurement
/// and can be wrong; this test is what keeps the new one honest.
///
/// The boolean domain is 8 combinations, so this enumerates ALL of them
/// rather than sampling. Three of the eight are physically impossible
/// (faulted implies ended) but the function is total, so its behaviour there
/// is pinned too — an unreachable input that silently changes meaning is how
/// a refactor reintroduces the collapse.
///
/// ⭐ 2026-08-25: the same collapse recurred one level down. `leaderEnded`
/// conflated "ended on purpose" with "ran out of road", which was harmless
/// only while the leader COULDN'T end on purpose — the completion marker was
/// inert because no profile's resolved middleware chain contained
/// `submit_detector`. Wiring it made deliberate termination the normal exit
/// and turned healthy runs into `leader_loop_exhausted`. Hence the fourth
/// parameter, the `leader_finished` value, and the widened vocabulary gate.
/// </summary>
public class HarnessStopReasonTests
{
    [Theory]
    // settled wins outright, whatever the leader task is doing during the
    // cleanup handshake. All four settled rows must agree.
    [InlineData(true,  false, false, "settled")]
    [InlineData(true,  true,  false, "settled")]
    [InlineData(true,  true,  true,  "settled")]
    [InlineData(true,  false, true,  "settled")]  // impossible, pinned anyway
    // not settled: fault outranks exhaustion outranks the clock.
    [InlineData(false, true,  true,  "leader_faulted")]
    [InlineData(false, false, true,  "leader_faulted")]  // impossible, pinned anyway
    [InlineData(false, true,  false, "leader_loop_exhausted")]
    // the ONLY row the old single string described correctly: the leader
    // is still running and simply ran out of wall clock.
    [InlineData(false, false, false, "wall_clock_timeout")]
    public void ClassifyStop_covers_every_combination(
        bool settled, bool leaderEnded, bool leaderFaulted, string expected)
    {
        Assert.Equal(expected, Harness.ClassifyStop(settled, leaderEnded, leaderFaulted));
    }

    /// <summary>
    /// The discrimination this whole change exists to create. Both rows
    /// are "the settle loop did not take its normal exit" and under the
    /// OLD code both produced the identical `timeout after Ns` string.
    /// If these two ever compare equal again, the collapse is back —
    /// this assertion, not the table above, is the regression guard.
    /// </summary>
    [Fact]
    public void Exhausted_leader_is_distinguishable_from_wall_clock_timeout()
    {
        var exhausted = Harness.ClassifyStop(settled: false, leaderEnded: true,  leaderFaulted: false);
        var onTheClock = Harness.ClassifyStop(settled: false, leaderEnded: false, leaderFaulted: false);

        Assert.NotEqual(exhausted, onTheClock);
        Assert.Equal("leader_loop_exhausted", exhausted);
        Assert.Equal("wall_clock_timeout", onTheClock);
    }

    /// <summary>
    /// A crash must not be laundered into either of the other two. A
    /// faulted leader IS an ended leader, so a naive `if (leaderEnded)`
    /// written before the fault check would silently score a crash as
    /// exhaustion — a code fault reported as a config finding.
    /// </summary>
    [Fact]
    public void Faulted_leader_outranks_ended_leader()
    {
        Assert.Equal("leader_faulted",
            Harness.ClassifyStop(settled: false, leaderEnded: true, leaderFaulted: true));
    }

    // =====================================================================
    // leader_finished — the state added 2026-08-25
    // =====================================================================

    /// <summary>
    /// ⭐ THE SECOND COLLAPSE, ONE LEVEL DOWN FROM THE FIRST.
    ///
    /// `leaderEnded` says the task object completed; it says NOTHING about
    /// why. That was harmless only while the leader had no way to end
    /// deliberately — the completion marker was inert, because no profile's
    /// resolved middleware chain contained `submit_detector`, so the loop
    /// really could only stop by running out of road.
    ///
    /// Wiring submit_detector made deliberate termination the NORMAL exit,
    /// and this line began reporting healthy runs as exhausted. Measured on
    /// e1-bump-csproj-version (2026-08-25): a PASS in 7 leader iterations
    /// against a cap of 200, labelled `leader_loop_exhausted`.
    /// </summary>
    [Theory]
    [InlineData("finish_tool")]          // declare_done / finish
    [InlineData("sequential_complete")]  // sequential team mode ran every member
    [InlineData("completed")]            // model stopped calling tools
    public void A_leader_that_ended_on_purpose_is_not_exhausted(string reason)
    {
        var actual = Harness.ClassifyStop(
            settled: false, leaderEnded: true, leaderFaulted: false, leaderStopReason: reason);

        Assert.Equal("leader_finished", actual);
        Assert.NotEqual("leader_loop_exhausted", actual);
    }

    /// <summary>
    /// The other side of that ruler. Ending for a reason the run should be
    /// JUDGED on keeps its old label — otherwise the fix would launder every
    /// cap hit and every stuck-detector kill into a healthy string, which is
    /// a far worse defect than the one it replaces.
    /// </summary>
    [Theory]
    [InlineData("max_iterations")]            // the cap — armA-run4's real cause
    [InlineData("stuck:monologue")]           // stuck detector overruled the agent
    [InlineData("stuck:action_error_loop")]
    [InlineData("llm_error")]
    [InlineData("empty_response_exhausted")]
    [InlineData("malformed_tool_call")]
    [InlineData("cancelled")]
    [InlineData("user_closed")]
    [InlineData("stalled")]
    [InlineData("")]                          // AgentResult's default
    [InlineData(null)]                        // faulted / still running
    public void A_leader_that_ran_out_of_road_is_still_exhausted(string? reason)
    {
        Assert.Equal("leader_loop_exhausted", Harness.ClassifyStop(
            settled: false, leaderEnded: true, leaderFaulted: false, leaderStopReason: reason));
    }

    /// <summary>
    /// A crash outranks a clean reason too. `IsCompletedSuccessfully` should
    /// stop a faulted task ever carrying one, but the classifier is total and
    /// an unreachable input that silently changes meaning is how a refactor
    /// reintroduces the collapse.
    /// </summary>
    [Fact]
    public void Faulted_outranks_a_clean_stop_reason()
    {
        Assert.Equal("leader_faulted", Harness.ClassifyStop(
            settled: false, leaderEnded: true, leaderFaulted: true, leaderStopReason: "finish_tool"));
    }

    /// <summary>
    /// A clean reason on a leader that has NOT ended cannot conjure a finish.
    /// The reason is only meaningful once the task completed.
    /// </summary>
    [Fact]
    public void A_clean_reason_without_an_ended_leader_is_still_the_clock()
    {
        Assert.Equal("wall_clock_timeout", Harness.ClassifyStop(
            settled: false, leaderEnded: false, leaderFaulted: false, leaderStopReason: "finish_tool"));
    }

    /// <summary>
    /// The clean-reason set must be enumerated from the real
    /// <c>Finalize(state, …)</c> call sites, not guessed. Pinned so that
    /// adding a reason to the set is a deliberate act with a visible diff.
    /// </summary>
    [Fact]
    public void Clean_stop_reasons_are_exactly_the_three_deliberate_exits()
    {
        Assert.Equal(
            new HashSet<string> { "finish_tool", "sequential_complete", "completed" },
            Harness.CleanLeaderStopReasons.ToHashSet());
    }

    /// <summary>
    /// Guards the vocabulary itself. A consumer that slices runs by
    /// stop_reason is running a predicate over this exact set of strings;
    /// a predicate over a GUESSED vocabulary returns a confident zero.
    /// Renaming a value is allowed — doing it without updating this list
    /// (and the downstream scorers) is not.
    ///
    /// ⛔ THE REASON DIMENSION IS ENUMERATED HERE ON PURPOSE. `leaderStopReason`
    /// defaults to null, so a sweep over the three booleans alone would never
    /// reach the `leader_finished` branch and would keep passing against the
    /// OLD four-value set — a gate that no longer spans its own population,
    /// passing vacuously while looking green.
    /// </summary>
    [Fact]
    public void Vocabulary_is_exactly_the_six_documented_values()
    {
        var observed = new HashSet<string>();
        string?[] reasons = [null, "", "finish_tool", "max_iterations", "stuck:monologue"];
        foreach (var s in new[] { true, false })
        foreach (var e in new[] { true, false })
        foreach (var f in new[] { true, false })
        foreach (var r in reasons)
        // ⛔ THE CANCELLATION DIMENSION IS SWEPT HERE FOR THE SAME REASON THE
        // REASON DIMENSION IS. `harnessCancelled` was added as a TRAILING
        // OPTIONAL parameter so the existing call sites and the tables above
        // kept compiling — which is convenient and is exactly the trap the
        // comment above describes. Defaulted to false, a sweep over the other
        // three booleans can never reach the new branch, so this test would
        // have gone on asserting the OLD five-value set and passing: a gate
        // that stopped spanning its own population the moment the population
        // grew, still green.
        foreach (var c in new[] { true, false })
            observed.Add(Harness.ClassifyStop(s, e, f, r, c));

        Assert.Equal(
            new HashSet<string>
            {
                "settled", "leader_finished", "leader_faulted",
                "leader_loop_exhausted", "wall_clock_timeout",
                "harness_cancelled",
            },
            observed);
    }

    // =====================================================================
    // harness_cancelled — the state added 2026-08-26
    // =====================================================================

    /// <summary>
    /// ⭐ THE SAME COLLAPSE, ONE LEVEL FURTHER OUT — and the one with teeth,
    /// because the label it used to wear points at a FORBIDDEN REMEDY.
    ///
    /// MEASURED (team-fanout-tier2/fan5, ds-team-lead-pro): the run stopped at
    /// 340.2s against a 1200s budget. The settle loop's quiet was ≈0.66s —
    /// under BOTH thresholds, so its normal `break` was not the exit taken.
    /// 8.67s later (CleanupTimeoutSeconds is 8) the leader emitted `cancelled`
    /// with reason=external at iteration 14.
    ///
    /// Under the old classifier that run came back `wall_clock_timeout` with
    /// the error "timeout after 1200s without settle" — a sentence naming a
    /// duration that demonstrably did not elapse, attached to the one label
    /// whose obvious remedy is raising the cap. Raising a cap to re-score a
    /// run is barred, so the wrong label did not merely misinform: it pointed
    /// at the barred action.
    /// </summary>
    [Fact]
    public void A_harness_cancellation_is_not_a_wall_clock_timeout()
    {
        // The leader task HAS completed by the time we classify — our own
        // cleanup cancelled runCts and the loop unwound with reason=external.
        var actual = Harness.ClassifyStop(
            settled: false, leaderEnded: true, leaderFaulted: false,
            leaderStopReason: "cancelled", harnessCancelled: true);

        Assert.Equal("harness_cancelled", actual);
        Assert.NotEqual("wall_clock_timeout", actual);
        Assert.NotEqual("leader_loop_exhausted", actual);
    }

    /// <summary>
    /// The ordering that carries the blame. Cleanup cancels <c>runCts</c>,
    /// which kills a leader that was healthy and mid-iteration; the leader
    /// then faults or reports `cancelled`. Read from outside, that is
    /// indistinguishable from a leader that died on its own — so a fault
    /// observed AFTER we pulled the plug is a CONSEQUENCE of our exit, not
    /// evidence about the model.
    /// </summary>
    [Fact]
    public void Harness_cancellation_outranks_a_leader_fault()
    {
        Assert.Equal("harness_cancelled", Harness.ClassifyStop(
            settled: false, leaderEnded: true, leaderFaulted: true, leaderStopReason: null,
            harnessCancelled: true));
    }

    /// <summary>
    /// The OTHER side of that ordering, and the reason it is not simply
    /// "cancellation wins". A CANCEL CANNOT MANUFACTURE A CLEAN STOP REASON:
    /// cancelling a leader yields `cancelled`, never finish_tool /
    /// sequential_complete / completed. One of those on the record is positive
    /// evidence the leader finished its work BEFORE the token fired, and
    /// voiding that run would discard a completed one.
    ///
    /// So a fault yields to the cancel (a cancel produces faults) and a clean
    /// finish does not (a cancel cannot produce one). If this ever flips, a
    /// Ctrl+C landing in the cleanup handshake starts voiding good runs.
    /// </summary>
    [Theory]
    [InlineData("finish_tool")]
    [InlineData("sequential_complete")]
    [InlineData("completed")]
    public void A_clean_finish_outranks_harness_cancellation(string reason)
    {
        Assert.Equal("leader_finished", Harness.ClassifyStop(
            settled: false, leaderEnded: true, leaderFaulted: false, leaderStopReason: reason,
            harnessCancelled: true));
    }

    /// <summary>
    /// Settled still wins outright. The settle loop takes its normal exit and
    /// only THEN does cleanup cancel things — a token firing during the
    /// unwind of a run that already reached its exit says nothing about it.
    /// </summary>
    [Fact]
    public void Settled_outranks_harness_cancellation()
    {
        Assert.Equal("settled", Harness.ClassifyStop(
            settled: true, leaderEnded: true, leaderFaulted: true, leaderStopReason: null,
            harnessCancelled: true));
    }

    /// <summary>
    /// ⛔ THE THREE PRECEDENCE RULES ARE CIRCULAR ON PAPER, so they are pinned
    /// TOGETHER — a future reorder has to confront all three at once instead of
    /// satisfying whichever one it happens to be looking at.
    ///
    ///   clean finish &gt; cancel   — a cancel yields `cancelled`, never
    ///                              finish_tool; a clean reason is positive
    ///                              evidence the work was done first.
    ///   cancel &gt; fault          — a cancel DOES produce faults, so a fault
    ///                              observed after we pulled the plug is a
    ///                              consequence, not evidence.
    ///   fault &gt; clean finish    — pinned 2026-08-25: a crash must never wear
    ///                              the healthiest string in the vocabulary.
    ///
    /// The cycle closes only on faulted-AND-clean-reason, which the harness
    /// cannot produce (leaderStopReason is read only from a successfully
    /// completed task). The classifier is total, so the `!leaderFaulted` guard
    /// excludes that unreachable input explicitly. THIS TEST IS HOW THAT
    /// REASONING GETS CHECKED rather than believed: moving the finish branch up
    /// WITHOUT the guard turns the third line red, which is exactly how the
    /// defect was caught.
    /// </summary>
    [Fact]
    public void The_three_precedence_rules_hold_simultaneously()
    {
        Assert.Equal("leader_finished", Harness.ClassifyStop(
            settled: false, leaderEnded: true, leaderFaulted: false,
            leaderStopReason: "finish_tool", harnessCancelled: true));

        Assert.Equal("harness_cancelled", Harness.ClassifyStop(
            settled: false, leaderEnded: true, leaderFaulted: true,
            leaderStopReason: null, harnessCancelled: true));

        Assert.Equal("leader_faulted", Harness.ClassifyStop(
            settled: false, leaderEnded: true, leaderFaulted: true,
            leaderStopReason: "finish_tool", harnessCancelled: false));
    }

    /// <summary>
    /// ⛔ THE SECOND SIDE OF THE RULER. Everything above shows the new branch
    /// FIRING; this shows it STAYING OUT OF THE WAY. A green ruler you wrote
    /// yourself is unverified until it has been shown to answer differently on
    /// the two sides — a classifier that returned "harness_cancelled" a bit too
    /// eagerly would pass every assertion above while voiding real results,
    /// which is the more expensive failure of the two: it turns model findings
    /// into non-data silently.
    ///
    /// Sweeps the FULL prior domain and asserts the new parameter is inert at
    /// false, i.e. every pre-existing verdict is byte-identical.
    /// </summary>
    [Fact]
    public void With_no_cancellation_every_prior_verdict_is_unchanged()
    {
        string?[] reasons = [null, "", "finish_tool", "sequential_complete", "completed",
                             "max_iterations", "stuck:monologue", "cancelled"];
        var sawCancelled = false;
        foreach (var s in new[] { true, false })
        foreach (var e in new[] { true, false })
        foreach (var f in new[] { true, false })
        foreach (var r in reasons)
        {
            var withFlag = Harness.ClassifyStop(s, e, f, r, harnessCancelled: false);
            var withoutFlag = Harness.ClassifyStop(s, e, f, r);
            Assert.Equal(withoutFlag, withFlag);
            Assert.NotEqual("harness_cancelled", withFlag);
            if (Harness.ClassifyStop(s, e, f, r, harnessCancelled: true) == "harness_cancelled")
                sawCancelled = true;
        }

        // Positive conjunct: the loop above is only meaningful if the same
        // input domain CAN reach the new label when the flag is true. Without
        // this, an accidentally dead branch passes the whole test.
        Assert.True(sawCancelled, "no input in the swept domain ever produced harness_cancelled");
    }

    // =====================================================================
    // LeaderFinishedCleanly — the task-shape half
    // =====================================================================

    /// <summary>
    /// A faulted or cancelled task must never read as a clean finish, and
    /// reading <c>.Result</c> off one would throw rather than answer. This
    /// pins the guard that keeps a crash out of the healthy branch.
    /// </summary>
    [Fact]
    public async Task Only_a_successfully_completed_task_can_finish_cleanly()
    {
        Assert.False(Harness.LeaderFinishedCleanly(null));

        var running = new TaskCompletionSource<AgentResult>();
        Assert.False(Harness.LeaderFinishedCleanly(running.Task));

        var faulted = Task.FromException<AgentResult>(new InvalidOperationException("boom"));
        try { await faulted; } catch (InvalidOperationException) { }
        Assert.False(Harness.LeaderFinishedCleanly(faulted));

        var cancelled = Task.FromCanceled<AgentResult>(new CancellationToken(canceled: true));
        try { await cancelled; } catch (OperationCanceledException) { }
        Assert.False(Harness.LeaderFinishedCleanly(cancelled));

        // Positive conjunct: the predicate must be capable of returning true,
        // or every assertion above passes for the wrong reason.
        var finished = Task.FromResult(new AgentResult { StopReason = "finish_tool" });
        Assert.True(Harness.LeaderFinishedCleanly(finished));

        // …and capable of returning false on a completed-but-capped task.
        var capped = Task.FromResult(new AgentResult { StopReason = "max_iterations" });
        Assert.False(Harness.LeaderFinishedCleanly(capped));
    }
}
