using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vett.Bench.Team;
using Vett.Config;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// FORCED-EMISSION tests for <c>stop_reason</c>.
///
/// WHY THIS FILE EXISTS AND WHY <see cref="HarnessStopReasonTests"/> IS NOT
/// ENOUGH: that file proves <see cref="Harness.ClassifyStop"/> — a pure
/// three-boolean mapping — is correct for all eight inputs. It proves
/// NOTHING about where those three booleans come from, whether the settle
/// loop can ever produce them, or whether the string reaches anything a
/// consumer reads. The commit that introduced the field
/// (9769d78) says so in its own words: "no live team-bench run has yet
/// produced a non-'settled' stop_reason end-to-end. The classification is
/// unit-proven; its wiring into a real timeout is not."
///
/// AN INVARIANT IS ONLY AS REAL AS ITS FAILURE TEST, and A DISPATCHED FIX
/// IS NOT A BUILT FIX. So these drive the REAL entry point,
/// <see cref="Harness.RunInstanceAsync"/>, once per value, with the leader
/// pushed into the state that value names:
///
///   settled               a stub LLM answers, the leader signals
///                         user_input_needed, the quiet period elapses.
///   leader_faulted        the leader TASK throws (duplicate member names
///                         make TeamCoordinator's members.ToDictionary
///                         throw before any LLM call).
///   leader_loop_exhausted the leader's loop ENDS at max_iterations=1.
///   wall_clock_timeout    the stub accepts the request and NEVER answers,
///                         so the leader is genuinely still running when
///                         the per-instance deadline fires.
///   harness_cancelled     the same never-answering stub, but the OUTER
///                         token is cancelled once the leader is provably
///                         mid-call — the harness stops the run, well
///                         inside a budget that never expires.
///
/// NO PAID API AND NO NETWORK EGRESS. The stub is a loopback TcpListener
/// speaking just enough HTTP/1.1 + OpenAI chat-completions to satisfy the
/// client <c>ChatClientFactory</c> builds for provider "local"; the fault
/// case never opens a socket at all.
///
/// PRECEDENCE. Every non-settled case here has the wall clock ALSO expired
/// — the settle loop cannot exit early when the leader never signals
/// user_input_needed, so it always runs to the deadline. That means the
/// fault case has fault AND ended AND clock all true simultaneously, and
/// the exhaustion case has ended AND clock both true. These are not
/// hypothetical two-conditions-at-once rows fed to a pure function; they
/// are the real harness with two real conditions satisfied, which is the
/// only place a "whichever check ran first" bug could hide.
///
/// KNOWN LIMIT, recorded rather than hidden — see
/// <see cref="A_leader_that_ENDED_for_a_non_exhaustion_reason_is_still_labelled_exhausted"/>:
/// <c>leader_loop_exhausted</c> is derived from <c>runTask.IsCompleted</c>
/// alone, so it covers EVERY way AgentLoop's Loop can return, not just the
/// iteration cap. AgentLoop already carries the discriminator
/// (<c>AgentResult.StopReason</c>, ten distinct values including
/// <c>llm_error</c>, <c>empty_response</c>, <c>malformed_tool_call</c> and
/// <c>cancelled</c>) and the harness does not read it.
/// </summary>
public class HarnessStopReasonWiringTests
{
    // Wall-clock budget knobs. Deliberately short: the settle loop runs to
    // the deadline on every non-settled path, so the deadline IS the cost.
    private const int ShortDeadlineSeconds = 4;
    private const int LlmErrorDeadlineSeconds = 20;   // AgentLoop retries 3x with 2s+6s backoff
    private const int SettleDeadlineSeconds = 90;     // must exceed Harness.QuietPeriodSeconds (20)
    // Generous ON PURPOSE. The finish probe asserts the run comes in UNDER
    // this, so a wide deadline makes the assertion stronger, not weaker: the
    // pre-fix behaviour was to consume the whole thing whatever its size.
    private const int FinishDeadlineSeconds = 60;
    // The ceiling a finish-on-turn-1 run must come in under. Sized from the
    // harness's own floor — PostSettlementQuietSeconds (5) of drain plus one
    // stub round-trip — not from the deadline. Below the 20s settle window and
    // far below the 60s self-assessment window, so either dead wait returning
    // turns this red on its own.
    private const int FinishFloorBudgetSeconds = 15;
    // Deliberately WIDE. The cancellation probe asserts the run comes in well
    // under this, so a generous budget makes that assertion stronger: the
    // whole point is that the clock was nowhere near expiring when the run
    // stopped, which is precisely what `wall_clock_timeout` used to claim.
    private const int CancelProbeDeadlineSeconds = 60;

    // ===============================================================
    // 1. settled
    // ===============================================================

    /// <summary>
    /// The ordinary exit. A stub LLM answers with plain text (no tool
    /// calls), so AgentLoop finishes the turn and signals
    /// user_input_needed a second time; the settle loop then waits out its
    /// quiet period and takes its normal exit.
    ///
    /// The liveness conjunct matters here more than anywhere: "settled" is
    /// the value a rig that never ran would also produce if the harness
    /// defaulted to it, so this asserts the leader really did iterate.
    /// </summary>
    [Fact]
    public async Task Settled_run_publishes_stop_reason_settled()
    {
        using var stub = FakeOpenAI.AlwaysText("done.");
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        var logger = new StopReasonWatchLogger(cts);

        var result = await Harness.RunInstanceAsync(
            Instance("settled-probe", SettleDeadlineSeconds),
            SoloProfile(stub.Endpoint, maxIterations: 100),
            endpoint: stub.Endpoint, model: "fake-model", apiKey: null,
            logger: logger, ct: cts.Token, workspaceOverride: ws.Path);

        // LIVENESS: the leader really ran a turn against the stub.
        Assert.True(stub.RequestCount >= 1,
            "the stub LLM was never called; the rig did not exercise the settle path.");
        Assert.True(result.LeaderIterations >= 1,
            $"leader_iterations={result.LeaderIterations}; the loop never iterated.");

        Assert.Equal("settled", result.StopReason);
        // A settled run must not carry a non-settled warning.
        Assert.Empty(logger.Warnings);

        // WIRING for the iteration-counter completeness flag (see
        // Harness.AwaitLoopUnwindAsync). A run whose loop genuinely unwound
        // publishes its counts BARE. This catches an inverted or
        // hard-coded-false flag.
        //
        // ⛔ 2026-08-27: this comment used to end "...the ABANDONED half is
        // unit-proven in HarnessPartialCountsTests and is not reachable from
        // here, because every blocking path in the real loop honours
        // cancellation." RETRACTED. Honouring the token is not sufficient —
        // under a loaded thread pool the loop can fail to be SCHEDULED to
        // observe it within the grace period, and the abandoned branch was
        // measured firing through RunInstanceAsync on 1 of 20 full-suite runs.
        // This run settles rather than being cancelled, so the loop is normally
        // already complete when the unwind wait begins and the risk here is
        // much lower — but "lower" is not "unreachable", and the assertion is
        // written as a coupling so contention cannot turn a correct, honest
        // marker into a red. Same shape as
        // HarnessPartialCountsTests.Counters_survive_a_mid_run_abort.
        if ((result.Error ?? "").Contains("did not unwind"))
            Assert.Contains(Harness.PartialCountsMarker, result.Error!);
        else
            Assert.DoesNotContain(Harness.PartialCountsMarker, result.Error ?? "");
    }

    // ===============================================================
    // 2. leader_faulted  (and precedence over BOTH other non-settled values)
    // ===============================================================

    /// <summary>
    /// The leader TASK throws. Induced honestly, with no LLM involved at
    /// all: a team profile with two members sharing a name makes
    /// <c>TeamCoordinator</c>'s <c>team.Members.ToDictionary(m =&gt; m.Name)</c>
    /// throw. That call lives inside <c>RunInteractiveImpl</c>, which is
    /// <c>async</c>, so the throw is captured into the returned Task rather
    /// than propagating synchronously — i.e. the harness's <c>runTask</c> is
    /// FAULTED, which is exactly the state the field exists to name.
    ///
    /// PRECEDENCE, both rules at once. A faulted Task is also a COMPLETED
    /// Task, so <c>leaderEnded</c> is true here too; and the settle loop
    /// cannot exit early (the leader never signals user_input_needed), so it
    /// runs to the deadline and the wall clock expires too. All three
    /// conditions hold. Under the OLD code this run reported
    /// `timeout after 4s without settle` — a CRASH wearing a clock label.
    /// </summary>
    [Fact]
    public async Task Faulted_leader_publishes_leader_faulted_outranking_exhaustion_and_the_clock()
    {
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        var logger = new StopReasonWatchLogger(cts);

        var result = await Harness.RunInstanceAsync(
            Instance("fault-probe", ShortDeadlineSeconds),
            DuplicateMemberTeamProfile(),
            // Closed loopback port: the client is built but never used.
            endpoint: $"http://127.0.0.1:{ClosedLoopbackPort()}/v1",
            model: "fake-model", apiKey: null,
            logger: logger, ct: cts.Token, workspaceOverride: ws.Path);

        // PREMISE: a real fault, not some other abort path. If the harness
        // threw instead (StopReason would be null) this names it.
        Assert.NotNull(result.Error);
        Assert.Contains("leader loop FAULTED", result.Error);
        Assert.Contains("ArgumentException", result.Error);
        Assert.Contains("CRASH, not a timeout", result.Error);

        // THE VALUE.
        Assert.Equal("leader_faulted", result.StopReason);

        // PRECEDENCE 1 — fault outranks exhaustion. A faulted task IS a
        // completed task, so a naive `if (leaderEnded)` first would have
        // scored this crash as a CONFIG finding.
        Assert.NotEqual("leader_loop_exhausted", result.StopReason);

        // PRECEDENCE 2 — fault outranks the clock. The clock genuinely
        // expired: the settle loop ran its full deadline.
        Assert.NotEqual("wall_clock_timeout", result.StopReason);
        Assert.True(result.WallClock.TotalSeconds >= ShortDeadlineSeconds,
            $"the wall clock did NOT expire (elapsed {result.WallClock.TotalSeconds:F1}s < " +
            $"{ShortDeadlineSeconds}s), so this run does not test precedence over the clock at all.");
    }

    /// <summary>
    /// The premise the fault-over-exhaustion rule rests on, pinned
    /// separately so it cannot rot silently: in .NET a faulted Task reports
    /// <c>IsCompleted == true</c>. That is why the harness's
    /// <c>leaderEnded</c> is ALSO true on the fault path, and therefore why
    /// the ordering in ClassifyStop is load-bearing rather than decorative.
    /// </summary>
    [Fact]
    public async Task A_faulted_task_is_also_a_completed_task()
    {
        var faulted = Task.Run(() => throw new InvalidOperationException("boom"));
        try { await faulted; } catch (InvalidOperationException) { }

        Assert.True(faulted.IsCompleted);   // => harness sets leaderEnded = true
        Assert.True(faulted.IsFaulted);     // => and leaderFaulted = true
        // So the two conditions are NOT mutually exclusive, and the if-order
        // in ClassifyStop decides which one is reported.
        Assert.Equal("leader_faulted", Harness.ClassifyStop(false, true, true));
    }

    // ===============================================================
    // 3. leader_loop_exhausted  (and precedence over the clock)
    // ===============================================================

    /// <summary>
    /// GENUINE iteration exhaustion — the state campaign run armA-run4 was
    /// suspected of and could not be proven to be, because the instrument
    /// could not tell it from a timeout.
    ///
    /// Rig: <c>max_iterations = 1</c> and a stub that answers with a tool
    /// call, so iteration 1 completes without finishing the turn and
    /// AgentLoop's <c>while (state.Iteration &lt; env.MaxIterations)</c> falls
    /// out to <c>Finalize(state, "max_iterations")</c>. The leader task
    /// completes normally — no fault — and never signals user_input_needed,
    /// so the settle loop spins to the deadline exactly as it did in the
    /// field.
    ///
    /// PRECEDENCE: the wall clock expired here too. Under the old code this
    /// was byte-identical to a leader that was still working.
    /// </summary>
    [Fact]
    public async Task Exhausted_leader_publishes_leader_loop_exhausted_outranking_the_clock()
    {
        using var stub = FakeOpenAI.AlwaysToolCall();
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        var logger = new StopReasonWatchLogger(cts);

        var result = await Harness.RunInstanceAsync(
            Instance("exhaustion-probe", ShortDeadlineSeconds),
            SoloProfile(stub.Endpoint, maxIterations: 1),
            endpoint: stub.Endpoint, model: "fake-model", apiKey: null,
            logger: logger, ct: cts.Token, workspaceOverride: ws.Path);

        // LIVENESS: the loop really ran and really hit its cap.
        Assert.True(stub.RequestCount >= 1,
            "the stub LLM was never called; the loop cannot have exhausted anything.");
        Assert.Equal(1, result.LeaderIterations);

        // PREMISE — EXHAUSTION AND THE CLOCK WERE BOTH TRUE.
        //
        // The claim under test is "when BOTH hold, exhaustion outranks the
        // clock." If the leader had not finished its single iteration by the
        // deadline then exhaustion was never true, only the clock was, and
        // `wall_clock_timeout` is the CORRECT answer to a question this run
        // did not ask. Such a run measures the MACHINE, not the wiring.
        //
        // OBSERVED 2026-08-28, run 7 of a 10-run suite watch under full xUnit
        // parallel load: this test failed Expected leader_loop_exhausted /
        // Actual wall_clock_timeout while LeaderIterations was 1 against a cap
        // of 1 — the leader HAD exhausted; its Task had merely not been
        // observed complete at the instant the deadline fired.
        //
        // THE DEADLINE CANNOT SIMPLY BE RAISED. It is 4s deliberately: BELOW
        // PostSettlementQuietSeconds (5s), so the clock is guaranteed to be the
        // settle loop's exit and the `WallClock >= deadline` assertion below is
        // guaranteed to hold. Push it past 5s and the loop exits on quiet
        // instead, and that assertion breaks. So the premise is ASSERTED, not
        // tuned — and a run that misses it is VOID: skipped, never silently
        // passed, never reported as a precedence failure.
        // Asserted rather than SKIPPED only because dynamic skip
        // (Assert.Skip/SkipUnless) landed in xUnit v3 and this suite is on
        // 2.9.3. A red here therefore means VOID — "this run could not ask the
        // question" — and must NOT be read as "precedence is broken". The two
        // are told apart by this message versus the one below it.
        Assert.True(
            result.StopEvidence?.Contains("leader_ended=true") == true,
            "VOID, not failing: the leader task had not completed when the 4s clock "
            + "expired, so exhaustion and the clock were never both true and this run "
            + "cannot test precedence between them. Re-run on a less loaded box before "
            + $"reading anything into it. stop_evidence: {result.StopEvidence}");

        // THE VALUE.
        Assert.Equal("leader_loop_exhausted", result.StopReason);
        Assert.NotNull(result.Error);
        Assert.Contains("leader loop ENDED after 1 iterations", result.Error);

        // PRECEDENCE — exhaustion outranks the clock, and the clock really
        // did expire.
        Assert.NotEqual("wall_clock_timeout", result.StopReason);
        Assert.True(result.WallClock.TotalSeconds >= ShortDeadlineSeconds,
            $"the wall clock did NOT expire (elapsed {result.WallClock.TotalSeconds:F1}s < " +
            $"{ShortDeadlineSeconds}s), so this run does not test precedence over the clock at all.");
    }

    /// <summary>
    /// THE OVER-COLLAPSE THIS FIELD DID NOT FIX, pinned so it is visible
    /// rather than inferred.
    ///
    /// <c>leaderEnded</c> is <c>runTask.IsCompleted</c> and nothing else, so
    /// EVERY way AgentLoop's Loop can return lands on
    /// <c>leader_loop_exhausted</c> — a value whose name, whose XML doc and
    /// whose accompanying Error string all say ITERATION CAP. Here the
    /// endpoint answers HTTP 500, so the leader dies at
    /// <c>Finalize(state, "llm_error")</c> on iteration 1 having exhausted
    /// nothing at all, and is still reported as exhausted.
    ///
    /// AgentLoop already knows the difference: <c>AgentResult.StopReason</c>
    /// carries ten distinct values (max_iterations, llm_error,
    /// empty_response, empty_response_exhausted, malformed_tool_call,
    /// cancelled, user_interrupted, user_closed, completed). The harness has
    /// the finished Task in hand and never reads it.
    ///
    /// IF THIS TEST GOES RED because the harness started distinguishing
    /// them, that is the FIX, not a regression — update this test.
    /// </summary>
    [Fact]
    public async Task A_leader_that_ENDED_for_a_non_exhaustion_reason_is_still_labelled_exhausted()
    {
        using var stub = FakeOpenAI.AlwaysHttp500();
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        var logger = new StopReasonWatchLogger(cts);

        var result = await Harness.RunInstanceAsync(
            Instance("llm-error-probe", LlmErrorDeadlineSeconds),
            SoloProfile(stub.Endpoint, maxIterations: 100),
            endpoint: stub.Endpoint, model: "fake-model", apiKey: null,
            logger: logger, ct: cts.Token, workspaceOverride: ws.Path);

        Assert.True(stub.RequestCount >= 1, "the stub LLM was never called.");

        // The leader ENDED on iteration 1 of a cap of 100 — it exhausted
        // nothing — and the published label says otherwise.
        Assert.Equal("leader_loop_exhausted", result.StopReason);
        Assert.NotNull(result.Error);
        Assert.Contains("leader loop ENDED after 1 iterations", result.Error);

        // The label is IDENTICAL to the genuine-cap case. Two states, one
        // string: the same collapse the field was built to end, one level
        // down.
        Assert.Equal(Harness.ClassifyStop(settled: false, leaderEnded: true, leaderFaulted: false),
                     result.StopReason);
    }

    // ===============================================================
    // 4. wall_clock_timeout
    // ===============================================================

    /// <summary>
    /// The only one of the three the OLD single string described correctly:
    /// the leader is STILL RUNNING and simply did not finish in time.
    ///
    /// Rig: the stub accepts the connection, reads the request, and never
    /// answers. The leader is genuinely blocked inside its LLM call when the
    /// deadline fires — not dead, not ended.
    ///
    /// THE DISCRIMINATION THIS WHOLE FIELD EXISTS TO CREATE: this run and
    /// the exhaustion run above are both "the settle loop did not take its
    /// normal exit", both spun to the same deadline, and under the old code
    /// both produced the identical `timeout after Ns without settle` string.
    /// </summary>
    [Fact]
    public async Task Blocked_leader_publishes_wall_clock_timeout()
    {
        using var stub = FakeOpenAI.NeverAnswers();
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        var logger = new StopReasonWatchLogger(cts);

        var result = await Harness.RunInstanceAsync(
            Instance("clock-probe", ShortDeadlineSeconds),
            SoloProfile(stub.Endpoint, maxIterations: 100),
            endpoint: stub.Endpoint, model: "fake-model", apiKey: null,
            logger: logger, ct: cts.Token, workspaceOverride: ws.Path);

        // LIVENESS: the leader really was mid-call. A stub that was never
        // reached would leave the leader blocked for a different reason and
        // this test would be measuring the rig.
        Assert.True(stub.RequestCount >= 1,
            "the stub LLM was never called; the leader was not blocked in an LLM call.");

        Assert.Equal("wall_clock_timeout", result.StopReason);
        Assert.NotNull(result.Error);
        Assert.Contains($"timeout after {ShortDeadlineSeconds}s without settle", result.Error);

        // And it is DISTINGUISHABLE from a dead leader — the regression
        // guard for the original collapse.
        Assert.NotEqual("leader_loop_exhausted", result.StopReason);
        Assert.NotEqual("leader_faulted", result.StopReason);
    }

    // ===============================================================
    // 4b. leader_finished — the deliberate exit (added 2026-08-25)
    // ===============================================================

    /// <summary>
    /// ⭐ THE DEAD WAIT. The defect this test exists for is not a wrong label,
    /// it is TIME: a run whose agent had already declared done sat in the
    /// settle loop until the wall clock expired.
    ///
    /// MECHANISM. The settle loop's only exit was <c>sawInputNeeded</c>, which
    /// AgentLoop raises when a turn ends with NO tool call
    /// (AgentLoop.cs:1333). But <c>finish</c> sets <c>StopLoop</c>, and the
    /// StopLoop check (AgentLoop.cs:1197) returns BEFORE that path is reached.
    /// So the stronger, explicit completion signal produced no settle at all,
    /// and the harness waited for a callback that could never arrive.
    ///
    /// Measured on the real thing (e1-bump-csproj-version, ds-team-flash,
    /// 2026-08-25): PASS in 7 leader iterations against a cap of 200, 421s
    /// wall clock of which ~360s was this dead wait, reported as
    /// `leader_loop_exhausted`.
    ///
    /// THE ASSERTION THAT CARRIES THE WEIGHT is the elapsed-time one. A test
    /// that only checked the string would stay green against a harness that
    /// still burned the full deadline and merely renamed the result.
    /// </summary>
    [Fact]
    public async Task A_leader_that_calls_finish_settles_immediately_and_is_not_called_exhausted()
    {
        using var stub = FakeOpenAI.AlwaysFinishCall();
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        var logger = new StopReasonWatchLogger(cts);

        var result = await Harness.RunInstanceAsync(
            Instance("finish-probe", FinishDeadlineSeconds),
            SoloProfile(stub.Endpoint, maxIterations: 100),
            endpoint: stub.Endpoint, model: "fake-model", apiKey: null,
            logger: logger, ct: cts.Token, workspaceOverride: ws.Path);

        // LIVENESS: the leader really called the stub. Without this, a rig
        // that never connected would produce a fast, quiet, green run.
        Assert.True(stub.RequestCount >= 1,
            "the stub LLM was never called; nothing exercised the finish path.");
        Assert.True(result.LeaderIterations >= 1,
            $"leader_iterations={result.LeaderIterations}; the loop never iterated.");

        // ⭐ THE MARKER WAS ACTED ON. The stub answers `finish` to EVERY
        // request, so an ignored marker means the loop keeps calling it until
        // max_iterations (100). One request is the wired behaviour; a large
        // count is the defect, in the one number that can tell them apart.
        Assert.True(stub.RequestCount <= 3,
            $"the LLM was called {stub.RequestCount} times after calling `finish` on the first "
          + "turn — the submit marker was not acted on, so the agent kept working past its own "
          + "submission. This is the __VETT_SUBMIT__-has-no-reader defect.");

        // THE VALUE: ended on purpose, NOT out of road.
        Assert.Equal("leader_finished", result.StopReason);
        Assert.NotEqual("leader_loop_exhausted", result.StopReason);
        Assert.NotEqual("wall_clock_timeout", result.StopReason);

        // ⭐ THE DEAD WAIT ITSELF. Waiting out the deadline is what this fix
        // removes; a rename alone would leave this assertion red.
        //
        // ⛔ THE BOUND IS NOT THE DEADLINE. Written as `< FinishDeadlineSeconds`
        // it was too loose to see the actual defect: the first run of this test
        // measured 80.5s and failed, and the 80.5 was 20s of settle quiet plus
        // 60s of a SECOND dead wait in the self-assessment block. A bound at 60
        // would have gone green the moment either half was fixed while the other
        // still burned. So bound it just above the real floor instead — the
        // post-settlement drain (5s) plus the one follow-up call — where BOTH
        // dead waits are individually fatal to it.
        Assert.True(result.WallClock.TotalSeconds < FinishFloorBudgetSeconds,
            $"the run took {result.WallClock.TotalSeconds:F1}s; a leader that finished on its "
          + $"first turn should land near the {FinishFloorBudgetSeconds}s drain-plus-follow-up "
          + "floor. Anything approaching 20s or 60s is one of the two dead waits back.");

        // A deliberate finish is not a failure: no error, no warning.
        Assert.True(string.IsNullOrEmpty(result.Error),
            $"a cleanly-finished run carries an error string: {result.Error}");
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// ⭐ THE MEASUREMENT THE DEAD-WAIT FIX NEARLY DELETED.
    ///
    /// The Tier 1B Q4 self-assessment is collected by pushing a follow-up
    /// question onto the leader's INPUT CHANNEL and waiting for a fresh
    /// user_input_needed. Both halves assume the leader's loop is still
    /// running. Once `finish` became a real terminator, the normal healthy run
    /// reaches that block with the loop already returned: the write lands in a
    /// buffer nobody reads, and the wait cannot end.
    ///
    /// The trap is that the failure is INVISIBLE IN THE OUTPUT. Captured=false
    /// is also what "we asked and the model refused" looks like, so every
    /// healthy run would have quietly started publishing the value that means
    /// something else entirely — and the analyzer computes failure-flagging
    /// recall over exactly that column. COULD NOT MEASURE IS NOT MEASURED ZERO.
    ///
    /// So the fix had to keep the measurement, not just delete the wait. The
    /// stub answers `finish` on request 0 and plain text after, which makes
    /// RequestCount the readout: 1 means the question was never asked, 2 means
    /// it was asked and answered.
    /// </summary>
    [Fact]
    public async Task A_cleanly_finished_leader_is_still_asked_for_its_self_assessment()
    {
        using var stub = FakeOpenAI.FinishThenText(
            "CONFIDENCE: 73\nREASON: The change is small and the existing tests cover it.");
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        var logger = new StopReasonWatchLogger(cts);

        var result = await Harness.RunInstanceAsync(
            Instance("finish-selfassess-probe", FinishDeadlineSeconds),
            SoloProfile(stub.Endpoint, maxIterations: 100),
            endpoint: stub.Endpoint, model: "fake-model", apiKey: null,
            logger: logger, ct: cts.Token, workspaceOverride: ws.Path);

        // PREMISE: this is the finished-leader path, not some other one. If the
        // leader were still alive the ORIGINAL channel push would handle the
        // capture and this test would be measuring the old code path.
        Assert.Equal("leader_finished", result.StopReason);

        // ⭐ THE QUESTION WAS ACTUALLY ASKED. Request 0 is the leader's single
        // turn; there is no other source of a request 1. Under the defect this
        // is exactly 1 — and the assertions below would all still be reachable
        // from a harness that fabricated a default, so this conjunct is what
        // makes them mean anything.
        Assert.Equal(2, stub.RequestCount);

        Assert.NotNull(result.SelfAssessment);
        Assert.True(result.SelfAssessment!.Captured,
            "captured=false on a leader that finished cleanly and had a model answering — "
          + "the question was either not asked or its answer was dropped.");
        Assert.Equal(73, result.SelfAssessment.Confidence);
        Assert.Equal(true, result.SelfAssessment.PredictedPass);
        Assert.Contains("existing tests cover it", result.SelfAssessment.Reasoning ?? "");

        // And it stayed cheap: the capture must not reintroduce a long wait.
        Assert.True(result.WallClock.TotalSeconds < FinishFloorBudgetSeconds,
            $"capturing the self-assessment cost {result.WallClock.TotalSeconds:F1}s — the "
          + "direct follow-up call is one round-trip, so this is a wait creeping back.");

        // A cleanly-finished run is not a failure, even one that answered.
        Assert.Empty(logger.Warnings);
    }

    // ===============================================================
    // 4c. harness_cancelled — the run we stopped ourselves (added 2026-08-26)
    // ===============================================================

    /// <summary>
    /// ⭐ TWO DEFECTS, ONE ROOT — and the second is the expensive one.
    ///
    /// FIELD EVIDENCE (team-fanout-tier2/fan5, ds-team-lead-pro, 2026-08-26):
    /// a run stopped at 340.2s against a 1200s budget. The settle loop's quiet
    /// was ≈0.66s, under BOTH thresholds, so its normal `break` was not the
    /// exit taken; 8.67s later — CleanupTimeoutSeconds is 8 — the leader
    /// emitted `cancelled reason=external` at iteration 14.
    ///
    /// (1) THE LABEL. Nothing set `settled`, the leader had not ended yet, so
    ///     the classifier fell through to `wall_clock_timeout` and the run
    ///     published "timeout after 1200s without settle" — a duration that
    ///     demonstrably did not elapse, wearing the one label whose obvious
    ///     remedy (raise the cap) is barred.
    ///
    /// (2) THE GRADING. The loop's `Task.Delay(500, ct)` THREW, and the catch-
    ///     all that caught it sits PAST AssertionEngine.Evaluate. So the run
    ///     came back with an EMPTY AssertionResults list — and
    ///     <c>TeamBenchResult.Pass</c> requires <c>Count > 0</c>, so it scored
    ///     FAIL. The members' work was still sitting in the workspace,
    ///     gradeable; the cancellation removed only our willingness to look at
    ///     it. A COULD-NOT-MEASURE laundered into a MEASURED-FAIL, charged to
    ///     the model.
    ///
    /// THE LOAD-BEARING ASSERTION HERE IS THE ASSERTION-RESULTS ONE. A fix that
    /// only renamed the label would leave this test red, which is the point:
    /// the label was the symptom and the deleted grading was the damage.
    /// </summary>
    [Fact]
    public async Task A_cancelled_settle_loop_is_labelled_harness_cancelled_and_the_workspace_is_still_graded()
    {
        using var stub = FakeOpenAI.NeverAnswers();
        using var ws = new TempWorkspace();

        // Stand in for the work the team actually delivered. Written before
        // the run because the override path ADOPTS the directory as-is
        // (Harness: WorkspaceSetup.Adopt), so this is on disk and gradeable
        // for the whole run — exactly like the real fan5 workspace was.
        File.WriteAllText(Path.Combine(ws.Path, "delivered.txt"), "the work is still on disk");

        using var cts = new CancellationTokenSource();
        var logger = new StopReasonWatchLogger(cts);

        // ⭐ CANCEL ON A MEASURED STATE, NOT ON A TIMER. A fixed delay that
        // fired before the leader reached the stub would be cancelling a
        // DIFFERENT state (startup, not a spinning settle loop) and this test
        // would silently stop testing what it names. Waiting for the request
        // to land makes "the leader is blocked mid-LLM-call and the settle
        // loop is spinning" a premise rather than a hope. The bound stops a
        // never-arriving request from hanging the suite; the liveness
        // assertion below is what catches that case if it happens.
        var cancelWhenBlocked = Task.Run(async () =>
        {
            for (var i = 0; i < 200 && stub.RequestCount < 1; i++)
                await Task.Delay(50);
            cts.Cancel();
        });

        var result = await Harness.RunInstanceAsync(
            CancelProbeInstance("cancel-probe"),
            SoloProfile(stub.Endpoint, maxIterations: 100),
            endpoint: stub.Endpoint, model: "fake-model", apiKey: null,
            logger: logger, ct: cts.Token, workspaceOverride: ws.Path);

        await cancelWhenBlocked;

        // LIVENESS: the leader really was mid-call when we pulled the plug.
        // Without this the run could have been cancelled during startup, which
        // exercises a different path and would make every assertion below pass
        // for the wrong reason.
        Assert.True(stub.RequestCount >= 1,
            "the stub LLM was never called; the run was cancelled before the leader was "
          + "blocked in an LLM call, so this does not test the settle-loop path at all.");

        // (1) THE LABEL.
        Assert.Equal("harness_cancelled", result.StopReason);
        Assert.NotEqual("wall_clock_timeout", result.StopReason);
        Assert.NotEqual("leader_faulted", result.StopReason);

        // The error says what happened, and — the part that matters — does NOT
        // claim a budget elapsed that never did.
        Assert.NotNull(result.Error);
        Assert.Contains("CANCELLED BY THE HARNESS", result.Error);
        Assert.Contains("VOID", result.Error);
        Assert.DoesNotContain($"timeout after {CancelProbeDeadlineSeconds}s", result.Error);

        // ⭐ (2) THE GRADING SURVIVED. This is the regression guard. Under the
        // old code AssertionResults was EMPTY here and Pass was therefore
        // false — a perfectly good workspace scored as a model failure.
        Assert.NotEmpty(result.AssertionResults);
        Assert.Single(result.AssertionResults);
        Assert.True(result.AssertionResults[0].Pass,
            $"the seeded file was not graded: {result.AssertionResults[0].Detail}");

        // …and the run therefore reads as PASS. That is deliberate and is NOT
        // a claim the run is usable: `harness_cancelled` is what marks it VOID.
        // Grading and admissibility are separate columns on purpose — collapsing
        // them is what produced the false FAIL in the first place.
        Assert.True(result.Pass);

        // The budget genuinely never expired — so "timeout" was never available
        // as an honest description of this run.
        Assert.True(result.WallClock.TotalSeconds < CancelProbeDeadlineSeconds,
            $"the run took {result.WallClock.TotalSeconds:F1}s of a {CancelProbeDeadlineSeconds}s "
          + "budget; if it reached the deadline this is a wall-clock timeout and the test is "
          + "measuring the wrong state.");

        // The optional diagnostic recorded its own non-attempt rather than
        // leaving null — null already means "this build doesn't capture".
        Assert.NotNull(result.SelfAssessment);
        Assert.False(result.SelfAssessment!.Captured);
        Assert.Equal("harness_cancelled", result.SelfAssessment.CaptureOutcome);
    }

    // ===============================================================
    // 5. NULL IS NOT "settled", and the field actually reaches --json
    // ===============================================================

    /// <summary>
    /// A run JSON written before 2026-08-24 has no <c>stop_reason</c> key at
    /// all. Deserialising it must yield NULL — "we do not know how this run
    /// ended" — and must NOT be coerced to "settled", which would turn a
    /// COULD-NOT-MEASURE into a MEASURED-FINE.
    /// </summary>
    [Fact]
    public void A_legacy_run_without_the_key_deserialises_to_null_not_settled()
    {
        const string legacy = """
        {"instance_id":"old","run_index":0,"pass":true,"wall_clock_seconds":12.5,
         "leader_iterations":7,"member_iterations":{},"error":null}
        """;

        var run = JsonSerializer.Deserialize<TeamBenchJsonRun>(legacy);

        Assert.NotNull(run);
        Assert.Null(run.StopReason);
        Assert.NotEqual("settled", run.StopReason);
    }

    /// <summary>
    /// An explicit <c>"stop_reason": null</c> must also stay null. A
    /// consumer slicing on this field is running a predicate over a
    /// vocabulary; silently mapping null into the vocabulary is how a
    /// confident zero gets manufactured.
    /// </summary>
    [Fact]
    public void An_explicit_null_stop_reason_stays_null()
    {
        var run = JsonSerializer.Deserialize<TeamBenchJsonRun>(
            """{"instance_id":"x","stop_reason":null}""");

        Assert.NotNull(run);
        Assert.Null(run.StopReason);
    }

    /// <summary>
    /// The error envelope the CLI emits when a suite never runs
    /// (TeamBenchCommand.cs:124, <c>new TeamBenchJsonRun { Error = message }</c>)
    /// must carry a null stop_reason, not a defaulted one. That envelope is
    /// a run that never happened; calling it settled would be a lie about a
    /// run with no leader at all.
    /// </summary>
    [Fact]
    public void The_error_envelope_run_carries_a_null_stop_reason()
    {
        var envelope = new TeamBenchJsonRun { Error = "No instances to run" };

        Assert.Null(envelope.StopReason);
        var json = JsonSerializer.Serialize(envelope, CliJsonOptions);
        Assert.DoesNotContain("settled", json);
    }

    /// <summary>
    /// A FIELD NOTHING PUBLISHES IS NOT EVIDENCE. Serialise through the same
    /// options the CLI uses (<c>new JsonSerializerOptions { WriteIndented = true }</c>,
    /// TeamBenchCommand.cs:323 — note there is no
    /// <c>DefaultIgnoreCondition</c>, so nulls are WRITTEN, not omitted) and
    /// assert both the snake_case wire name and the value a scorer would
    /// read.
    ///
    /// MEASURED POWER, not assumed. A falsifier probe that gave
    /// <c>StopReason</c> the initialiser <c>= "settled"</c> — the actual
    /// null-coercion defect — left THIS test GREEN, because both halves
    /// ASSIGN the property explicitly and an explicit assignment overwrites
    /// the initialiser. So this is a wire-NAME test and nothing more. The
    /// only two tests that hold power over null-coercion are
    /// <see cref="A_legacy_run_without_the_key_deserialises_to_null_not_settled"/>
    /// and <see cref="The_error_envelope_run_carries_a_null_stop_reason"/>,
    /// both of which went red under that probe. Do not delete either one
    /// believing this test covers them.
    /// </summary>
    [Fact]
    public void stop_reason_reaches_the_published_json_under_its_wire_name()
    {
        var populated = JsonSerializer.Serialize(
            new TeamBenchJsonRun { InstanceId = "x", StopReason = "wall_clock_timeout" },
            CliJsonOptions);

        Assert.Contains("\"stop_reason\": \"wall_clock_timeout\"", populated);

        // And on a legacy/unknown run the key is present with an explicit
        // null rather than silently absent — absence and null read the same
        // to a careful consumer, but only one of them is greppable.
        var unknown = JsonSerializer.Serialize(
            new TeamBenchJsonRun { InstanceId = "x", StopReason = null },
            CliJsonOptions);

        Assert.Contains("\"stop_reason\": null", unknown);
        Assert.DoesNotContain("settled", unknown);
    }

    /// <summary>
    /// The one link in the chain that cannot be executed from a test: the
    /// CLI's result-to-wire projection is an object initialiser inside a
    /// System.CommandLine handler lambda. ADOPTING A LAW IS NOT INSTALLING
    /// IT — so check it at the point of use. Without this line every test
    /// above passes while <c>--json</c> ships <c>stop_reason: null</c> for
    /// every run.
    /// </summary>
    [Fact]
    public void The_cli_projection_actually_copies_StopReason_onto_the_wire_object()
    {
        var path = FindRepoFile(Path.Combine("src", "Vett", "Cli", "TeamBenchCommand.cs"));
        Assert.True(path is not null,
            "could not locate src/Vett/Cli/TeamBenchCommand.cs from " + AppContext.BaseDirectory +
            "; this guard cannot pass vacuously.");

        var text = File.ReadAllText(path!);
        Assert.Contains("StopReason = t.Result.StopReason", text);
    }

    // ===============================================================
    // 6. THE SELF-ASSESSMENT ANSWERED BY SUBMITTING  (site 5)
    // ===============================================================

    /// <summary>
    /// ⭐ THE POSITIVE CONTROL, and it is load-bearing.
    ///
    /// Drives the SAME channel-push branch as the falsifier below with a leader
    /// that answers in PROSE, and requires the harness to record "answered"
    /// with the confidence parsed. Without this, a red below would be
    /// ambiguous between "the answer was discarded" (the claim) and "the rig
    /// never reached the self-assessment at all" (a broken instrument) — and
    /// the second reads exactly like the first. A falsifier that a
    /// never-executed treatment also satisfies measured the harness: VOID, not
    /// FAILED.
    /// </summary>
    [Fact]
    public async Task A_leader_that_answers_the_self_assessment_in_prose_is_recorded_answered()
    {
        using var stub = FakeOpenAI.AlwaysText("CONFIDENCE: 80\nREASON: the change is minimal.");
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        var logger = new StopReasonWatchLogger(cts);

        var result = await Harness.RunInstanceAsync(
            Instance("selfassess-prose", SettleDeadlineSeconds),
            SoloProfile(stub.Endpoint, maxIterations: 100),
            endpoint: stub.Endpoint, model: "fake-model", apiKey: null,
            logger: logger, ct: cts.Token, workspaceOverride: ws.Path);

        // LIVENESS: request 0 is the leader's own turn; a SECOND request is the
        // only thing the follow-up question can produce. Without this the
        // assertions below could pass on a run that was never asked.
        Assert.True(stub.RequestCount >= 2,
            $"stub saw {stub.RequestCount} request(s); the self-assessment follow-up was "
          + "never asked, so this does not exercise the capture path at all.");

        Assert.NotNull(result.SelfAssessment);
        Assert.Equal("answered", result.SelfAssessment!.CaptureOutcome);
        Assert.True(result.SelfAssessment.Captured);
        Assert.Equal(80, result.SelfAssessment.Confidence);
    }

    /// <summary>
    /// ⛔ A LEADER THAT ANSWERS THE SELF-ASSESSMENT BY SUBMITTING MUST NOT BE
    /// RECORDED AS HAVING FAILED TO ANSWER.
    ///
    /// Site 5 of one concept — *an agent that answered only by SUBMITTING loses
    /// its answer*. Sites 1-4 are in DISPATCH-REPORTING-SWEEP-2026-08-28.md.
    ///
    /// THE CONTRADICTION THIS PINS: the harness follow-up (Harness.cs:66) says
    /// "Reply with EXACTLY these two lines and nothing else", while every one of
    /// the 10 shipping team profiles says "Never write CONFIDENCE/REASON as a
    /// plain chat message — they go INSIDE the declare_done summary argument".
    /// A leader obeying its SYSTEM prompt answers with a tool call. The sink it
    /// is graded from, `assistantTexts`, collects (its own comment,
    /// Harness.cs:872) "final assistant turns with NO TOOL CALLS".
    ///
    /// So the better-behaved the leader, the more reliably its answer is lost —
    /// and `CaptureOutcome` is the column failure-flagging recall is computed
    /// over. The existing SelfAssessmentCaptureOutcomeTests exist to stop
    /// COULD-NOT-MEASURE being read as MEASURED-ZERO; this is the third state
    /// they do not yet cover: MEASURED AND DISCARDED.
    ///
    /// ⚠ Deadline is 240s, not SettleDeadlineSeconds (90): the capture alone is
    /// allowed 60s (SelfAssessmentTimeoutSeconds) on top of the 20s quiet
    /// period. This is a fixture budget chosen so the run can COMPLETE, not a
    /// prereg cap being raised to rescue a result — the prediction under test
    /// is about the OUTCOME LABEL, not about elapsed time.
    /// </summary>
    [Fact]
    public async Task A_leader_that_answers_the_self_assessment_by_submitting_keeps_its_answer()
    {
        using var stub = FakeOpenAI.TextThenSubmitCall(
            "Starting on the task now.",
            "CONFIDENCE: 80\nREASON: the change is minimal and covered.");
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        var logger = new StopReasonWatchLogger(cts);

        var result = await Harness.RunInstanceAsync(
            Instance("selfassess-submitted", 240),
            SoloProfile(stub.Endpoint, maxIterations: 100),
            endpoint: stub.Endpoint, model: "fake-model", apiKey: null,
            logger: logger, ct: cts.Token, workspaceOverride: ws.Path);

        // LIVENESS, same conjunct as the control: the follow-up really was asked.
        Assert.True(stub.RequestCount >= 2,
            $"stub saw {stub.RequestCount} request(s); the self-assessment follow-up was "
          + "never asked, so this does not exercise the capture path at all.");

        Assert.NotNull(result.SelfAssessment);

        // THE CLAIM: the leader answered. It answered in the format its own
        // system prompt mandates. That answer must reach the record.
        Assert.Equal("answered", result.SelfAssessment!.CaptureOutcome);
        Assert.True(result.SelfAssessment.Captured);

        // ⭐ ...AND THE ANSWER ITSELF SURVIVES, NOT JUST THE LABEL. The harm is
        // that a calibrated leader's confidence is DROPPED from the column
        // failure-flagging recall is computed over. A fix that recorded
        // "answered" while still losing the number would satisfy every
        // assertion above and none of the point.
        //
        // Assert.True, not Assert.Equal, so a failure NAMES THE TEXT IT SAW.
        // "expected 80, got null" cannot distinguish "the answer never arrived"
        // from "the answer arrived and the parse missed it", and those two have
        // different owners.
        Assert.True(result.SelfAssessment.Confidence == 80,
            "confidence not recovered from the submitted answer. capture_outcome="
            + (result.SelfAssessment.CaptureOutcome ?? "<null>")
            + " confidence=" + (result.SelfAssessment.Confidence?.ToString() ?? "<null>")
            + " raw_text=[" + (result.SelfAssessment.RawText ?? "<null>") + "]"
            + " requests=" + stub.RequestCount
            // The ONE line that settles this diagnosis rather than every line
            // the run emitted: it carries leader_stop, which separates "the
            // answer never arrived" from "it arrived and the recovery skipped it".
            + " || " + string.Join(" || ",
                  logger.All.Where(l => l.Contains("self_assessment_capture"))));
    }

    /// <summary>
    /// ⛔ A LEADER WHOSE LOOP DIED DURING THE CAPTURE MUST NOT HAVE ITS
    /// PRE-PUSH PROSE REPORTED AS ITS ANSWER.
    ///
    /// This pins the guard, not the feature — and it exists because the first
    /// version of the site-5 fix FAILED it. That version set
    /// `selfSettled = runTask.IsCompletedSuccessfully`, which is TRUE for a loop
    /// that gave up: `llm_error` is a RETURN, not a fault. The recovery then
    /// pulled FinalTurnText() — the only assistant turn there was, the one
    /// written BEFORE the question — and recorded `answered`.
    ///
    /// That is strictly worse than the bug being fixed. The original defect
    /// LOST a real confidence number; this would MANUFACTURE one from an
    /// unrelated turn, and nothing downstream could tell it from a real reading.
    ///
    /// It was caught by a log line, not an assertion. Without this test the
    /// tightening to <see cref="Harness.LeaderFinishedCleanly"/> is an untested
    /// change that a later edit could revert with the whole suite green.
    ///
    /// THE HONEST LABEL for this run is `leader_ended_unanswered`: the leader
    /// really did end, and it really did not answer. Not `settle_timeout` —
    /// that would score a harness/transport fault as model behaviour, the exact
    /// pooling `capture_outcome` was added to prevent (JsonWire.cs:54).
    /// </summary>
    [Fact]
    public async Task A_leader_that_dies_during_the_self_assessment_is_not_recorded_answered()
    {
        // ⚠ THE PRE-PUSH TURN CARRIES A CONFIDENCE LINE ON PURPOSE. Without
        // one, the `Confidence is null` conjunct below passes in the broken
        // state too  14 it would assert nothing. With one, reverting the guards
        // makes the harness report a REAL NUMBER (42) parsed out of a turn the
        // leader wrote before it was ever asked, which is the actual harm:
        // not a missing reading, a FABRICATED one that nothing downstream can
        // distinguish from a genuine self-assessment. Verified by mutation.
        const string prePush = "Starting on the task now.\nCONFIDENCE: 42\nREASON: early guess.";
        using var stub = FakeOpenAI.TextThenGarbage(prePush);
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        var logger = new StopReasonWatchLogger(cts);

        var result = await Harness.RunInstanceAsync(
            Instance("selfassess-died", 240),
            SoloProfile(stub.Endpoint, maxIterations: 100),
            endpoint: stub.Endpoint, model: "fake-model", apiKey: null,
            logger: logger, ct: cts.Token, workspaceOverride: ws.Path);

        // LIVENESS, same conjunct as the other two: the follow-up really was
        // asked. Without it this passes vacuously on a run that never got there.
        Assert.True(stub.RequestCount >= 2,
            $"stub saw {stub.RequestCount} request(s); the self-assessment follow-up was "
          + "never asked, so this does not exercise the capture path at all.");

        Assert.NotNull(result.SelfAssessment);

        // THE CLAIM, stated as what must NOT be true. Asserted on the VALUE and
        // on the TEXT, not only the label: a regression that relabelled while
        // still copying the pre-push turn into RawText would be the same defect.
        Assert.True(result.SelfAssessment!.CaptureOutcome != "answered"
                 && result.SelfAssessment.Confidence is null
                 && !(result.SelfAssessment.RawText ?? "").Contains(prePush),
            "a leader whose loop died was credited with an answer it never gave. "
          + "capture_outcome=" + (result.SelfAssessment.CaptureOutcome ?? "<null>")
          + " confidence=" + (result.SelfAssessment.Confidence?.ToString() ?? "<null>")
          + " raw_text=[" + (result.SelfAssessment.RawText ?? "<null>") + "]");

        // ...and the label is the SPECIFIC honest one, not merely not-"answered".
        // Kept as a separate assertion so a failure says which half moved.
        Assert.Equal("leader_ended_unanswered", result.SelfAssessment.CaptureOutcome);
        Assert.False(result.SelfAssessment.Captured);
    }

    // ===============================================================
    // Rig
    // ===============================================================

    private static readonly JsonSerializerOptions CliJsonOptions = new() { WriteIndented = true };

    private static TeamBenchInstance Instance(string id, int timeoutSeconds) => new()
    {
        Id = id,
        Prompt = "probe",
        TimeoutSeconds = timeoutSeconds,
    };

    /// <summary>Instance carrying ONE cheap, deterministic, offline assertion.
    /// The other probes in this file assert nothing, so their AssertionResults
    /// are empty either way — which means none of them could ever have caught
    /// the grading being skipped. This one exists to make that list
    /// non-empty-when-correct, i.e. to give the check power.</summary>
    private static TeamBenchInstance CancelProbeInstance(string id) => new()
    {
        Id = id,
        Prompt = "probe",
        TimeoutSeconds = CancelProbeDeadlineSeconds,
        Assertions = [new TeamBenchAssertion { File = "delivered.txt", Exists = true }],
    };

    /// <summary>Solo profile (no team block) — no dispatches, no worktrees,
    /// no git. Points at whatever endpoint the caller supplies.</summary>
    private static Profile SoloProfile(string endpoint, int maxIterations) => new()
    {
        Name = "stop-reason-probe",
        SystemPrompt = "probe fixture",
        MaxIterations = maxIterations,
        Llm = new LlmConfig
        {
            Provider = "local",
            Endpoint = endpoint,
            Model = "fake-model",
            NumRetries = 0,
            RequestTimeoutSeconds = 120,
        },
    };

    /// <summary>Team profile whose member list has a DUPLICATE name, so
    /// <c>team.Members.ToDictionary(m =&gt; m.Name)</c> throws inside the
    /// coordinator's async body and faults the leader task before any
    /// socket is opened.</summary>
    private static Profile DuplicateMemberTeamProfile() => new()
    {
        Name = "stop-reason-fault-probe",
        SystemPrompt = "probe fixture",
        MaxIterations = 100,
        Llm = new LlmConfig { Provider = "local", Endpoint = "http://127.0.0.1:1/v1", Model = "fake-model", NumRetries = 0, RequestTimeoutSeconds = 5 },
        Team = new TeamConfig
        {
            // ⭐ ADDED 2026-08-27, when an absent width ceiling became an error
            // rather than a silent "unlimited". EXPLICIT 0 = unlimited, so the
            // stop-reason precedence this class measures is unchanged.
            MaxConcurrentDispatches = 0,
            Leader = new MemberConfig { Name = "leader", SystemPrompt = "lead" },
            Members =
            [
                new MemberConfig { Name = "implementer", SystemPrompt = "a" },
                new MemberConfig { Name = "implementer", SystemPrompt = "b" },
            ],
        },
    };

    /// <summary>Bind port 0, note it, release it. Connecting there is
    /// refused immediately — a dead endpoint with no egress.</summary>
    private static int ClosedLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Walk up from the test binary to find a repo-relative file.
    /// Returns null when not found; the caller must fail rather than skip.</summary>
    private static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Path { get; }
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "vett-stopreason-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Records the harness's non-settled warning and — the moment it fires —
    /// cancels the run's CancellationToken.
    ///
    /// WHY: <c>Harness.RunInstanceAsync</c> logs that warning at the
    /// statement immediately after it assigns <c>result.StopReason</c>, and
    /// the very next statement is <c>if (!ct.IsCancellationRequested)</c>
    /// guarding a 60-second self-assessment turn that a dead or blocked
    /// leader can never answer. Cancelling from inside the log call is
    /// therefore ordered, not raced: StopReason is already written, and the
    /// unanswerable 60s wait is skipped. Nothing after that point reads
    /// <c>ct</c>, so the published result is unaffected.
    /// </summary>
    private sealed class StopReasonWatchLogger(CancellationTokenSource cts) : ILogger
    {
        private readonly List<string> _warnings = [];
        private readonly List<string> _all = [];

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return _warnings.ToList(); }
        }

        /// <summary>EVERY log line, not just warnings. A probe that can only
        /// see warnings can tell you a run went wrong but not what it did, and
        /// "expected 80, got null" is exactly the failure that needs the trace.</summary>
        public IReadOnlyList<string> All
        {
            get { lock (_all) return _all.ToList(); }
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            lock (_all) _all.Add(logLevel + ": " + msg);
            if (logLevel >= LogLevel.Warning) { lock (_warnings) _warnings.Add(msg); }
            if (msg.Contains("leader_faulted")
                || msg.Contains("leader_loop_exhausted")
                || msg.Contains("wall_clock_timeout"))
            {
                cts.Cancel();
            }
        }
    }

    /// <summary>
    /// Loopback-only stub speaking the minimum HTTP/1.1 + OpenAI
    /// chat-completions the client built by
    /// <c>ChatClientFactory</c> (provider "local") needs. Keep-alive so the
    /// SDK can reuse the connection; a null response body means "accept the
    /// request and never answer", which is how the wall-clock case is
    /// forced.
    /// </summary>
    private sealed class FakeOpenAI : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<int, string?> _body;   // null => never answer
        private readonly int _status;
        private int _requests;

        private FakeOpenAI(Func<int, string?> body, int status)
        {
            _body = body;
            _status = status;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }
        public string Endpoint => $"http://127.0.0.1:{Port}/v1";
        public int RequestCount => Volatile.Read(ref _requests);

        public static FakeOpenAI AlwaysText(string content)
            => new(_ => TextBody(content), 200);

        public static FakeOpenAI AlwaysToolCall()
            => new(i => ToolCallBody("call_" + i), 200);

        /// <summary>Calls `finish` on the FIRST request, then would keep
        /// calling it. A correctly wired chain stops after the first, so a
        /// RequestCount above 1 is itself evidence the marker was ignored.</summary>
        public static FakeOpenAI AlwaysFinishCall()
            => new(i => FinishCallBody("call_" + i), 200);

        /// <summary>`finish` on request 0, plain text on every request after.
        /// Models the real shape of a healthy run: the leader terminates, and
        /// the ONLY thing that can produce a second request is the harness
        /// asking its post-run self-assessment question. That makes
        /// RequestCount a direct, unambiguous readout of whether the question
        /// was asked at all.</summary>
        public static FakeOpenAI FinishThenText(string content)
            => new(i => i == 0 ? FinishCallBody("call_0") : TextBody(content), 200);

        /// <summary>Plain text on request 0 — the leader completes a turn and
        /// PARKS, which is precisely what puts the harness on the channel-push
        /// self-assessment path (leaderEnded == false). Then a SUBMIT-MARKER
        /// TOOL CALL on every request after, with the confidence inside the
        /// tool argument and NO prose — which is how all 10 shipping team
        /// profiles instruct a leader to answer ("Never write CONFIDENCE/REASON
        /// as a plain chat message — they go INSIDE the declare_done summary").
        ///
        /// `finish` stands in for `declare_done`, which is a LEADER tool
        /// (LeaderTools.cs:710) and is not registered for a solo profile. They
        /// are the same object to the loop: both return
        /// Builtins.SubmitMarker + summary (BuiltinTools.cs:180,
        /// LeaderTools.cs:716) and both therefore trip SubmitDetector into
        /// StopLoop. Named as a substitution in the prereg.</summary>
        public static FakeOpenAI TextThenSubmitCall(string content, string submitMessage)
            => new(i => i == 0 ? TextBody(content) : FinishCallBody("call_" + i, submitMessage), 200);

        /// <summary>Plain text on request 0 — same park as
        /// <see cref="TextThenSubmitCall"/>, so the harness takes the same
        /// channel-push branch — then a body that is not a chat completion at
        /// all on every request after. Status stays 200 on purpose: this models
        /// the loop DYING DURING THE FOLLOW-UP, not the transport refusing, and
        /// it reaches the same place from the model side rather than the socket.
        ///
        /// The loop exhausts its retries and returns with StopReason
        /// "llm_error" — a RETURN, so `IsCompletedSuccessfully` is true, which
        /// is exactly the trap this fixture exists to hold shut.</summary>
        public static FakeOpenAI TextThenGarbage(string content)
            => new(i => i == 0 ? TextBody(content) : "{\"not\":\"a chat completion\"", 200);

        public static FakeOpenAI AlwaysHttp500()
            => new(_ => "{\"error\":{\"message\":\"probe: forced 500\",\"type\":\"server_error\"}}", 500);

        public static FakeOpenAI NeverAnswers()
            => new(_ => null, 200);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buf = new byte[16384];
                    var acc = new List<byte>();

                    while (!_cts.IsCancellationRequested)
                    {
                        // --- request head ---
                        int headEnd;
                        while ((headEnd = FindHeadEnd(acc)) < 0)
                        {
                            var n = await stream.ReadAsync(buf, _cts.Token);
                            if (n == 0) return;
                            for (var i = 0; i < n; i++) acc.Add(buf[i]);
                        }
                        var head = Encoding.ASCII.GetString(acc.ToArray(), 0, headEnd);
                        var contentLength = ContentLengthOf(head);

                        // --- request body ---
                        var bodyStart = headEnd + 4;
                        while (acc.Count - bodyStart < contentLength)
                        {
                            var n = await stream.ReadAsync(buf, _cts.Token);
                            if (n == 0) return;
                            for (var i = 0; i < n; i++) acc.Add(buf[i]);
                        }
                        var requestBody = Encoding.UTF8.GetString(acc.ToArray(), bodyStart, contentLength);
                        acc.RemoveRange(0, bodyStart + contentLength);

                        var index = Interlocked.Increment(ref _requests) - 1;
                        var body = _body(index);
                        if (body is null)
                        {
                            // Hold the connection open, unanswered, until the
                            // stub is disposed. THIS is the wall-clock case.
                            try { await Task.Delay(Timeout.Infinite, _cts.Token); } catch { }
                            return;
                        }

                        // Law 135: the real client now streams by default, so
                        // a 200 to a `stream:true` request is answered the way
                        // vLLM answers it -- the same completion, as SSE. The
                        // scripted BODIES are unchanged; only the wire shape
                        // follows what the request asked for. A non-200, or a
                        // body that is not a completion at all, is sent as-is:
                        // the SDK reports both on the first read, streaming or
                        // not, which keeps the "loop dies mid-answer" fixture
                        // honest.
                        var wantsStream = WantsStream(requestBody);
                        var sse = _status == 200 && wantsStream ? AsSse(body) : null;
                        var payload = Encoding.UTF8.GetBytes(sse ?? body);
                        var reason = _status == 200 ? "OK" : "Internal Server Error";
                        var contentType = sse is null ? "application/json" : "text/event-stream";
                        var responseHead = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 {_status} {reason}\r\n" +
                            $"Content-Type: {contentType}\r\n" +
                            $"Content-Length: {payload.Length}\r\n" +
                            "Connection: keep-alive\r\n\r\n");
                        await stream.WriteAsync(responseHead, _cts.Token);
                        await stream.WriteAsync(payload, _cts.Token);
                        await stream.FlushAsync(_cts.Token);
                    }
                }
            }
            catch { /* client hung up, or the stub is shutting down */ }
        }

        private static bool WantsStream(string requestBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(requestBody);
                return doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
            }
            catch (JsonException) { return false; }
        }

        /// <summary>
        /// Re-shape one buffered chat completion as the SSE a streaming
        /// request receives: one chunk carrying the whole message as a
        /// delta (tool calls gain the `index` the streaming schema keys on),
        /// one chunk with the finish_reason, one usage-only chunk, then
        /// `[DONE]`. A body that does not parse as a completion is returned
        /// unchanged so the SDK fails on it exactly as it did buffered.
        /// </summary>
        private static string? AsSse(string completion)
        {
            System.Text.Json.Nodes.JsonNode? root;
            try { root = System.Text.Json.Nodes.JsonNode.Parse(completion); }
            catch (JsonException) { return null; }
            if (root?["choices"] is not System.Text.Json.Nodes.JsonArray choices || choices.Count == 0)
                return null;
            var choice = choices[0]!;
            var message = choice["message"]?.DeepClone() ?? new System.Text.Json.Nodes.JsonObject();
            if (message["tool_calls"] is System.Text.Json.Nodes.JsonArray calls)
                for (var i = 0; i < calls.Count; i++)
                    calls[i]!["index"] = i;
            var finish = choice["finish_reason"]?.DeepClone();

            System.Text.Json.Nodes.JsonObject Envelope() => new()
            {
                ["id"] = root["id"]?.DeepClone() ?? "chatcmpl-probe",
                ["object"] = "chat.completion.chunk",
                ["created"] = root["created"]?.DeepClone() ?? 1700000000,
                ["model"] = root["model"]?.DeepClone() ?? "fake-model",
            };

            var first = Envelope();
            first["choices"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["index"] = 0, ["delta"] = message, ["finish_reason"] = null,
            });
            var last = Envelope();
            last["choices"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["index"] = 0, ["delta"] = new System.Text.Json.Nodes.JsonObject(), ["finish_reason"] = finish,
            });
            var sb = new StringBuilder();
            sb.Append("data: ").Append(first.ToJsonString()).Append("\n\n");
            sb.Append("data: ").Append(last.ToJsonString()).Append("\n\n");
            if (root["usage"] is { } usage)
            {
                var u = Envelope();
                u["choices"] = new System.Text.Json.Nodes.JsonArray();
                u["usage"] = usage.DeepClone();
                sb.Append("data: ").Append(u.ToJsonString()).Append("\n\n");
            }
            sb.Append("data: [DONE]\n\n");
            return sb.ToString();
        }

        private static int FindHeadEnd(List<byte> acc)
        {
            for (var i = 0; i + 3 < acc.Count; i++)
                if (acc[i] == 13 && acc[i + 1] == 10 && acc[i + 2] == 13 && acc[i + 3] == 10)
                    return i;
            return -1;
        }

        private static int ContentLengthOf(string head)
        {
            foreach (var line in head.Split("\r\n"))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                if (!line[..idx].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(line[(idx + 1)..].Trim(), out var n)) return n;
            }
            return 0;
        }

        private static string TextBody(string content) =>
            "{\"id\":\"chatcmpl-probe\",\"object\":\"chat.completion\",\"created\":1700000000," +
            "\"model\":\"fake-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"," +
            "\"content\":" + JsonSerializer.Serialize(content) + "},\"finish_reason\":\"stop\"}]," +
            "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

        // `think` is a builtin with no side effects (BuiltinTools.cs:101), so
        // the loop advances an iteration without touching the filesystem.
        private static string ToolCallBody(string callId) =>
            "{\"id\":\"chatcmpl-probe\",\"object\":\"chat.completion\",\"created\":1700000000," +
            "\"model\":\"fake-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"," +
            "\"content\":null,\"tool_calls\":[{\"id\":\"" + callId + "\",\"type\":\"function\"," +
            "\"function\":{\"name\":\"think\",\"arguments\":\"{\\\"thought\\\":\\\"probe\\\"}\"}}]}," +
            "\"finish_reason\":\"tool_calls\"}]," +
            "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

        // `finish` (BuiltinTools.cs:102) returns Builtins.SubmitMarker + message.
        // SubmitDetector is the ONLY reader of that marker in the codebase.
        /// <summary>`finish` carrying an ARBITRARY message, so a probe can put
        /// real content (a CONFIDENCE line) inside the tool argument — the place
        /// the shipping profiles tell a leader to put it. Built through
        /// JsonSerializer twice on purpose: `arguments` is a JSON STRING whose
        /// content is itself JSON, so a message containing a newline has to
        /// survive two rounds of escaping. Hand-rolling that is how a probe ends
        /// up testing its own quoting bug.</summary>
        private static string FinishCallBody(string callId, string message)
        {
            // String overload only — no reflection-based Serialize in a probe.
            var inner = "{\"message\":" + JsonSerializer.Serialize(message) + "}";
            return "{\"id\":\"chatcmpl-probe\",\"object\":\"chat.completion\",\"created\":1700000000," +
                "\"model\":\"fake-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"," +
                "\"content\":null,\"tool_calls\":[{\"id\":" + JsonSerializer.Serialize(callId) + ",\"type\":\"function\"," +
                "\"function\":{\"name\":\"finish\",\"arguments\":" + JsonSerializer.Serialize(inner) + "}}]}," +
                "\"finish_reason\":\"tool_calls\"}]," +
                "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";
        }

        private static string FinishCallBody(string callId) =>
            "{\"id\":\"chatcmpl-probe\",\"object\":\"chat.completion\",\"created\":1700000000," +
            "\"model\":\"fake-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"," +
            "\"content\":null,\"tool_calls\":[{\"id\":\"" + callId + "\",\"type\":\"function\"," +
            "\"function\":{\"name\":\"finish\",\"arguments\":\"{\\\"message\\\":\\\"probe done\\\"}\"}}]}," +
            "\"finish_reason\":\"tool_calls\"}]," +
            "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}
