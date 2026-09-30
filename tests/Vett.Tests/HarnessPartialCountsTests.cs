using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Vett.Bench.Team;
using Vett.Config;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Failure tests for the iteration counters the team-bench harness
/// PUBLISHES on the exception path.
///
/// WHY THIS FILE EXISTS: before 2026-08-24
/// <c>result.LeaderIterations</c> / <c>result.MemberIterations</c> were the
/// last two statements of the try block in
/// <see cref="Harness.RunInstanceAsync"/>. Every throw on the way there —
/// WorkspaceSetup, the StreamWriter, ChatClientFactory, the
/// <c>ct</c>-bearing <c>Task.Delay</c> in either settle loop,
/// AssertionEngine.Evaluate — discarded them, and the run went out with
/// <c>leader_iterations: 0</c>. A run that did 40 real iterations and then
/// threw contributed 0 to every total, and NOTHING in the published output
/// separated it from a run that genuinely did nothing: a COULD-NOT-MEASURE
/// laundered into a MEASURED-ZERO, shipped as a silent lower bound wearing
/// the face of an exact count.
///
/// Two halves, and the tests below cover them separately because fixing
/// only one still ships a lie:
///
///  1. SURVIVAL — the counts must reach the result even when the body
///     throws. (<c>Counters_survive_a_mid_run_abort</c>)
///  2. HONESTY — a count published from a <c>finally</c> after an abort is
///     a LOWER BOUND, and the output has to say so. A finally that writes 0
///     is no more truthful than one that loses the 40.
///     (<c>Partial_zero_is_distinguishable_from_a_measured_zero</c> is the
///     regression guard; if those two results ever compare equal again, the
///     collapse is back.)
///
/// KNOWN LIMIT, recorded here rather than hidden: the discriminator rides
/// <see cref="TeamBenchResult.Error"/> because the count fields CANNOT
/// express it. <c>TeamBenchResult.LeaderIterations</c> and
/// <c>TeamBenchJsonRun.leader_iterations</c> are both plain <c>int</c> and
/// <c>MemberIterations</c> is a plain dictionary — there is no value in
/// either domain meaning "not measured". Widening them to <c>int?</c> or
/// adding an explicit partial flag is a Models.cs + JsonWire.cs change.
/// </summary>
public class HarnessPartialCountsTests
{
    // ---------------------------------------------------------------
    // 1. The mapping itself. Pure, so it can be pinned exhaustively.
    // ---------------------------------------------------------------

    [Fact]
    public void Complete_counts_are_published_bare_with_no_marker()
    {
        var r = new TeamBenchResult { InstanceId = "x" };
        Harness.PublishIterationCounts(r, 40,
            new Dictionary<string, int> { ["task-1"] = 7 }, complete: true);

        Assert.Equal(40, r.LeaderIterations);
        Assert.Equal(7, r.MemberIterations["task-1"]);
        // A completed run must not be tainted with a "lower bound" caveat —
        // a caveat that fires on everything discriminates nothing.
        Assert.True(string.IsNullOrEmpty(r.Error));
    }

    [Fact]
    public void Partial_counts_survive_and_are_labelled_as_lower_bounds()
    {
        var r = new TeamBenchResult { InstanceId = "x" };
        Harness.PublishIterationCounts(r, 40,
            new Dictionary<string, int> { ["task-1"] = 7 }, complete: false);

        // SURVIVAL: the 40 is still there.
        Assert.Equal(40, r.LeaderIterations);
        Assert.Equal(7, r.MemberIterations["task-1"]);

        // HONESTY: and it is published as a bound, not a measurement.
        Assert.NotNull(r.Error);
        Assert.Contains(Harness.PartialCountsMarker, r.Error);
        Assert.Contains("leader_iterations>=40", r.Error);
        Assert.Contains("task-1>=7", r.Error);
    }

    /// <summary>
    /// THE discrimination this change exists to create. Both results carry
    /// leader_iterations = 0. One measured zero; the other could not
    /// measure. Under the old code they were byte-identical, and the
    /// aborted one was pooled into campaign totals as a genuine zero.
    /// </summary>
    [Fact]
    public void Partial_zero_is_distinguishable_from_a_measured_zero()
    {
        var measuredZero = new TeamBenchResult { InstanceId = "x" };
        Harness.PublishIterationCounts(measuredZero, 0, null, complete: true);

        var couldNotMeasure = new TeamBenchResult { InstanceId = "x" };
        Harness.PublishIterationCounts(couldNotMeasure, 0, null, complete: false);

        Assert.Equal(0, measuredZero.LeaderIterations);
        Assert.Equal(0, couldNotMeasure.LeaderIterations);

        // The numbers agree, so the published record MUST NOT.
        Assert.NotEqual(measuredZero.Error ?? "", couldNotMeasure.Error ?? "");
        Assert.DoesNotContain(Harness.PartialCountsMarker, measuredZero.Error ?? "");
        Assert.Contains(Harness.PartialCountsMarker, couldNotMeasure.Error!);
    }

    /// <summary>
    /// The caveat must ADD to whatever the harness already recorded, never
    /// replace it. On the abort path <c>Error</c> already holds
    /// `harness error: ...` — clobbering it would trade one lost fact for
    /// another.
    /// </summary>
    [Fact]
    public void Partial_note_appends_to_an_existing_error_instead_of_clobbering_it()
    {
        var r = new TeamBenchResult { InstanceId = "x", Error = "harness error: boom" };
        Harness.PublishIterationCounts(r, 3, null, complete: false);

        Assert.Contains("harness error: boom", r.Error);
        Assert.Contains(Harness.PartialCountsMarker, r.Error);
    }

    /// <summary>Null member map is "none observed", not a crash — the
    /// finally that calls this runs on paths where nothing was set up.</summary>
    [Fact]
    public void Null_member_map_is_reported_as_none_observed()
    {
        var r = new TeamBenchResult { InstanceId = "x" };
        Harness.PublishIterationCounts(r, 0, null, complete: false);

        Assert.Empty(r.MemberIterations);
        Assert.Contains("none observed", r.Error!);
    }

    // ---------------------------------------------------------------
    // 2. The WIRING. A correct mapping that the finally never calls is
    //    a fix that does not ship — so drive the real entry point.
    // ---------------------------------------------------------------

    /// <summary>
    /// Deterministic abort with ZERO iterations: WorkspaceSetup.Adopt is the
    /// first statement in the try and throws on a missing dir. Proves the
    /// finally publishes at all, and that the 0 it publishes is LABELLED.
    /// No LLM, no network, no workspace.
    /// </summary>
    [Fact]
    public async Task An_abort_before_the_loop_publishes_a_LABELLED_zero()
    {
        var missing = Path.Combine(Path.GetTempPath(), "vett-no-such-ws-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));

        var result = await Harness.RunInstanceAsync(
            new TeamBenchInstance { Id = "adopt-throws", Prompt = "unused", TimeoutSeconds = 5 },
            new Profile { Name = "adopt-throws" },
            endpoint: "http://127.0.0.1:1/v1",
            model: "unused",
            apiKey: null,
            logger: NullLogger.Instance,
            ct: CancellationToken.None,
            workspaceOverride: missing);

        Assert.NotNull(result.Error);
        Assert.Contains("harness error", result.Error);
        // The count is genuinely 0 here — but it is 0 because we never got
        // to count, and that is what the marker says.
        Assert.Equal(0, result.LeaderIterations);
        Assert.Contains(Harness.PartialCountsMarker, result.Error);
        Assert.Contains("leader_iterations>=0", result.Error);
    }

    /// <summary>
    /// The scenario from the field: real iterations happened, THEN the run
    /// was cancelled. Under the old code this published 0.
    ///
    /// Rig: solo profile (no team block) pointed at a CLOSED loopback port —
    /// no paid API, no egress. AgentLoop emits `iteration_start` BEFORE its
    /// first LLM call (AgentLoop.cs:489), so the counter moves even though
    /// the call then fails. We wait on a POSITIVE event in the run's OWN log
    /// (`_bench-session.jsonl`, AutoFlush) rather than on a sleep — a
    /// blocked-on-I/O probe has no liveness content — and only then cancel.
    ///
    /// ⛔ 2026-08-26: THE ABORT MECHANISM THIS TEST ONCE NAMED WAS ITSELF THE
    /// BUG, AND IT IS GONE. This doc used to end "...and only then cancel,
    /// which throws out of the settle loop's `await Task.Delay(500, ct)`" —
    /// and the assertions below pinned that throw's consequences. That throw
    /// jumped from the settle loop straight to the catch-all at :1420,
    /// vaulting the whole tail of the try (Harness.cs:1390-1415):
    ///
    ///   :1390 inputChan.Writer.TryComplete()  — never ran
    ///   :1391 runCts.CancelAfter(...)         — never ran, so THE AGENT LOOP
    ///         WAS NEVER SIGNALLED TO STOP and outlived this method's return
    ///   :1396 AwaitLoopUnwindAsync            — never ran
    ///   :1404 countersComplete = loopUnwound  — never ran; the field kept its
    ///         `false` initializer from :699
    ///   :1413 AssertionEngine.Evaluate        — never ran, so a run whose work
    ///         was sitting complete on disk went out with ZERO assertion
    ///         results, and Pass (=> Count > 0 && All) scored it FAIL
    ///
    /// So the marker this test used to assert fired because countersComplete
    /// was never ASSIGNED — not because the loop was genuinely abandoned. That
    /// matters for what this test can be credited with proving: it never
    /// exercised `countersComplete = loopUnwound` at all, which is precisely
    /// what AwaitLoopUnwindAsync's own doc records at Harness.cs:612-624
    /// ("reverting the caller to the old unconditional true produced ZERO
    /// red"). Nothing is lost by retiring the marker assertion here, and
    /// WIRING-LEVEL marker coverage is NOT lost either: the zero-iteration
    /// sibling above still throws in WorkspaceSetup.Adopt — before the loop,
    /// so before any of the above — and still asserts the marker at :169.
    ///
    /// The settle loop now BREAKS on cancellation instead of throwing, so the
    /// tail above runs: the loop is signalled, it unwinds (connection-refused
    /// is fast), and the counters are therefore FINAL rather than a bound.
    /// Publishing them UNMARKED is the more precise statement, not a weaker
    /// one — which is why the assertion flipped rather than being deleted.
    /// The survival half is untouched and is still the point of the test.
    /// </summary>
    [Fact]
    public async Task Counters_survive_a_mid_run_abort()
    {
        var ws = Path.Combine(Path.GetTempPath(), "vett-partial-counts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        try
        {
            var profile = new Profile
            {
                Name = "partial-counts-probe",
                SystemPrompt = "test fixture; never actually reached",
                MaxIterations = 100,
                // No Team block => the solo path => no dispatches, no worktrees.
                Llm = new LlmConfig
                {
                    Provider = "local",
                    Endpoint = $"http://127.0.0.1:{ClosedLoopbackPort()}/v1",
                    Model = "no-such-model",
                    NumRetries = 0,
                    RequestTimeoutSeconds = 5,
                },
            };

            // Long enough that the wall clock is NOT what ends this run —
            // the cancel is, and the cancel is what we are measuring.
            var instance = new TeamBenchInstance
            {
                Id = "partial-counts-probe",
                Prompt = "hello",
                TimeoutSeconds = 600,
            };

            using var cts = new CancellationTokenSource();
            var run = Harness.RunInstanceAsync(
                instance, profile,
                endpoint: profile.Llm.Endpoint,
                model: profile.Llm.Model,
                apiKey: null,
                logger: NullLogger.Instance,
                ct: cts.Token,
                workspaceOverride: ws);

            var counted = await WaitForIterationStartAsync(
                Path.Combine(ws, "_bench-session.jsonl"), TimeSpan.FromSeconds(60));

            // LIVENESS CONJUNCT. If the fixture never iterated, this test
            // measured the harness rig, not the fix — fail loudly rather
            // than let a vacuous green through.
            Assert.True(counted,
                "fixture never emitted iteration_start; the rig did not exercise the counter, " +
                "so a pass here would prove nothing.");

            cts.Cancel();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(120));

            // SURVIVAL — the whole point. Old code published 0 here.
            Assert.True(result.LeaderIterations >= 1,
                $"iterations were counted in the run's own log but published as " +
                $"{result.LeaderIterations}; error was: {result.Error ?? "(null)"}");

            // HONESTY — asserted as a COUPLING, not as one branch.
            //
            // ⛔ 2026-08-27, WHAT THIS LINE USED TO BE AND WHY IT WAS WRONG.
            // It was a bare `Assert.DoesNotContain(PartialCountsMarker, Error)`,
            // resting on "the cleanup tail runs, so the loop unwinds, so
            // countersComplete is genuinely true". That is a claim about
            // SCHEDULING, and it lost: measured red on 1 of 20 consecutive
            // full-suite runs, with the failing run taking exactly 10s —
            // CleanupTimeoutSeconds+2, i.e. AwaitLoopUnwindAsync's timeout
            // branch and nothing else. Under a loaded thread pool the loop can
            // fail to be SCHEDULED to observe a token it does honour.
            //
            // ⭐ THE MARKER WAS RIGHT AND THE ASSERTION WAS WRONG. On that run
            // the harness genuinely could not certify the counters, said so, and
            // the test called it a failure. Deleting or loosening the assertion
            // to make the red go away would delete the instrument — so instead
            // it now pins the INVARIANT that actually has to hold, which neither
            // branch of the race can violate:
            //
            //     the loop was abandoned  ⟺  the counts are labelled bounds
            //
            // This is strictly STRONGER than what it replaces. The old form said
            // nothing at all about the abandoned path — a regression that
            // stopped emitting the marker there would have passed silently. This
            // form goes red on that, and still goes red if a run that unwound
            // cleanly gets labelled (the original direction, unchanged).
            //
            // The witness is only available because Harness.cs now APPENDS the
            // unwind warning instead of `??=`-dropping it; before that fix the
            // marker was the sole survivor and this coupling was unwritable.
            Assert.NotNull(result.Error);
            var loopWasAbandoned = result.Error!.Contains("did not unwind");
            if (loopWasAbandoned)
            {
                Assert.Contains(Harness.PartialCountsMarker, result.Error);
                Assert.Contains($"leader_iterations>={result.LeaderIterations}", result.Error);
            }
            else
            {
                Assert.DoesNotContain(Harness.PartialCountsMarker, result.Error);
            }

            // NOT the catch-all. The old code surfaced this run as the generic
            // "harness error: The operation was canceled." — a harness fault
            // wearing the face of a model failure. Pinning its absence is what
            // keeps the classification from silently regressing to that.
            Assert.DoesNotContain("harness error", result.Error);

            // THE DOC BUG FROM 2026-08-24, NOW FIXED AND THEREFORE PINNED.
            // The old comment here recorded what this exact path produced —
            //   stop_reason=null  self_assessment=null  assertions=0
            // — and deliberately left it un-asserted, because pinning it would
            // have frozen a doc bug in place. All three had a single cause: they
            // are assigned in the tail of the try that the settle-loop throw
            // vaulted. The throw is gone, so all three now carry real values and
            // pinning them is no longer freezing a bug — it is guarding a fix.
            Assert.Equal("harness_cancelled", result.StopReason);
            Assert.Contains("CANCELLED BY THE HARNESS", result.Error);
            Assert.Contains("VOID", result.Error);   // admissibility, not a score

            Assert.NotNull(result.SelfAssessment);
            Assert.False(result.SelfAssessment!.Captured);
            Assert.Equal("harness_cancelled", result.SelfAssessment.CaptureOutcome);

            // The wall clock is NOT what ended this run, and the published
            // error must not imply it was: TimeoutSeconds is 600 and we
            // cancelled seconds in. A message naming a 600s timeout here would
            // invite raising a cap that was never reached.
            Assert.DoesNotContain($"timeout after {instance.TimeoutSeconds}s", result.Error);
            Assert.True(result.WallClock.TotalSeconds < 120,
                $"run should have ended on the cancel, not the 600s budget; " +
                $"took {result.WallClock.TotalSeconds:F1}s");
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------
    // 3. WHERE `complete` COMES FROM.
    //
    //    The mapping above was correct from the day it landed and the
    //    finally really did call it — and ITERATION_COUNTS_PARTIAL still
    //    could not fire on the abandoned-loop path, because the caller set
    //    `countersComplete = true` unconditionally after waiting for the
    //    agent loop. The comment on that very line named the abandoned case
    //    ("or abandoned with a cleanup warning already recorded above") and
    //    then treated it as final anyway.
    //
    //    "The task finished" and "we stopped waiting for the task" are
    //    different events. Only the first one stops the counters.
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_loop_that_finished_is_UNWOUND_with_nothing_to_report()
    {
        var (unwound, error) = await Harness.AwaitLoopUnwindAsync(Task.CompletedTask, cleanupSeconds: 0);

        Assert.True(unwound);
        Assert.Null(error);
    }

    [Fact]
    public async Task A_loop_that_OBSERVED_cancellation_is_UNWOUND()
    {
        // Cancellation observed means the task REACHED AN END, so its
        // counters are final. This is the ordinary end of a timed-out run
        // and must NOT be marked partial — a caveat that fires on the
        // common case discriminates nothing.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = Task.FromCanceled(cts.Token);

        var (unwound, error) = await Harness.AwaitLoopUnwindAsync(cancelled, cleanupSeconds: 0);

        Assert.True(unwound);
        Assert.Null(error);
    }

    [Fact]
    public async Task A_loop_that_FAULTED_is_UNWOUND_and_the_fault_is_reported()
    {
        var faulted = Task.FromException(new InvalidOperationException("boom"));

        var (unwound, error) = await Harness.AwaitLoopUnwindAsync(faulted, cleanupSeconds: 0);

        // It ran to an end, badly. The error is about the RUN's outcome,
        // not about our ability to measure it.
        Assert.True(unwound);
        Assert.NotNull(error);
        Assert.Contains("boom", error);
    }

    /// <summary>
    /// THE DEFECT. A loop that never finishes is abandoned, not unwound —
    /// and every count read after this point is a lower bound.
    /// </summary>
    [Fact]
    public async Task A_loop_still_running_is_ABANDONED_not_unwound()
    {
        var neverFinishes = new TaskCompletionSource().Task;

        var (unwound, error) = await Harness.AwaitLoopUnwindAsync(neverFinishes, cleanupSeconds: 0);

        Assert.False(unwound);
        Assert.NotNull(error);
        Assert.Contains("did not unwind", error);

        // ★ THE FACT THE WHOLE FIX RESTS ON, asserted rather than assumed:
        // giving up on the wait does not stop the task. It is STILL RUNNING
        // right now, which is why its counters are still moving and why
        // publishing them as measurements was a lie.
        Assert.False(neverFinishes.IsCompleted,
            "the abandoned task completed after all; if WaitAsync could stop it, " +
            "the counters would be final and this fix would be unnecessary.");
    }

    /// <summary>
    /// DISCRIMINATION GUARD. Two outcomes both produce an Error string, and
    /// exactly one of them means "could not measure". A fix that keyed
    /// completeness off "did anything go wrong" instead of "did the task
    /// end" would mark a faulted run partial and pass every test above.
    /// </summary>
    [Fact]
    public async Task Abandoned_and_faulted_both_report_errors_but_only_ONE_is_unmeasured()
    {
        var (faultedUnwound, faultedError) = await Harness.AwaitLoopUnwindAsync(
            Task.FromException(new InvalidOperationException("boom")), cleanupSeconds: 0);
        var (abandonedUnwound, abandonedError) = await Harness.AwaitLoopUnwindAsync(
            new TaskCompletionSource().Task, cleanupSeconds: 0);

        Assert.NotNull(faultedError);
        Assert.NotNull(abandonedError);
        Assert.NotEqual(faultedUnwound, abandonedUnwound);
        Assert.True(faultedUnwound);
        Assert.False(abandonedUnwound);
    }

    /// <summary>
    /// End to end through the mapping: the abandoned verdict must actually
    /// reach the published record as ITERATION_COUNTS_PARTIAL. Composed
    /// exactly as <see cref="Harness.RunInstanceAsync"/> composes them.
    /// </summary>
    [Fact]
    public async Task An_abandoned_loops_counts_are_published_as_LOWER_BOUNDS()
    {
        var (unwound, unwindError) = await Harness.AwaitLoopUnwindAsync(
            new TaskCompletionSource().Task, cleanupSeconds: 0);

        var r = new TeamBenchResult { InstanceId = "x" };
        if (unwindError is not null) r.Error ??= unwindError;
        Harness.PublishIterationCounts(r, 40, new Dictionary<string, int> { ["task-1"] = 7 }, unwound);

        Assert.NotNull(r.Error);
        Assert.Contains("did not unwind", r.Error);          // the cleanup warning survives...
        Assert.Contains(Harness.PartialCountsMarker, r.Error); // ...and the counts are labelled
        Assert.Contains("leader_iterations>=40", r.Error);
    }

    // ---------------------------------------------------------------

    /// <summary>Bind port 0 to get one the OS considers free, then release
    /// it. Connecting there yields connection-refused immediately — a dead
    /// endpoint with no network egress and no paid API.</summary>
    private static int ClosedLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Poll the harness's own per-run JSONL log for a real
    /// `iteration_start` record. FileShare.ReadWrite because the harness
    /// holds the file open for writing.</summary>
    private static async Task<bool> WaitForIterationStartAsync(string logPath, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(logPath))
            {
                try
                {
                    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    var text = await sr.ReadToEndAsync();
                    if (text.Contains("\"iteration_start\"")) return true;
                }
                catch (IOException) { /* mid-write; retry */ }
            }
            await Task.Delay(100);
        }
        return false;
    }
}
