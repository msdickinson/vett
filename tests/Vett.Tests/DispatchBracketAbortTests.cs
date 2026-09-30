using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Sandbox;

namespace Vett.Tests;

/// <summary>
/// F13 — A DISPATCH THAT THROWS LEFT ITS BRACKET OPEN.
///
/// `dispatch_start` increments the harness's `dispatchesInFlight`
/// (Harness.cs:527-530). `dispatch_end` decrements it. Every exit from
/// `TeamCoordinator.RunMemberFull` that did NOT reach the normal `dispatch_end`
/// stranded that counter above zero permanently, and the settle loop's
/// `if (inFlight > 0) continue` (Harness.cs:719) could then never be satisfied:
/// the instance ran to the wall clock and was classified `wall_clock_timeout`.
///
/// ⛔ THAT IS A HARNESS FAULT WEARING A MODEL FAULT'S LABEL. It lands in
/// exactly the place this repo has been burned before — a run whose real cause
/// was infrastructure gets scored as a capability result. One member in flight
/// when the token trips is enough to make the whole instance unsettleable.
///
/// ⚠ THE FINDING WAS UNDER-SCOPED, AND THE CODE IS DELIBERATELY WIDER THAN IT.
/// The register described this as the outer-CANCELLATION path. But both catches
/// that existed were `OperationCanceledException`-specific, so ANY other throw
/// out of the agent loop — an HTTP failure, a tool-layer bug, an OOM inside a
/// nested sub-team — escaped past `dispatch_start` the same way and stranded the
/// same counter. Cancellation was the OBSERVED INSTANCE, not the boundary of the
/// defect. Section 1 pins the case the register named; section 2 pins the wider
/// class it did not.
///
/// ⚠ THE OVER-CORRECTION IS WORSE THAN THE DEFECT, so section 3 exists.
/// Trading a stuck counter for a fabricated COMPLETED dispatch would be a worse
/// bug: the harness would then believe a member that died mid-flight had run and
/// changed nothing. The synthetic event therefore carries
/// `dispatch_aborted: true`, `iterations: null` (COULD NOT MEASURE, never 0),
/// and `pending_review: false` — because emitting `true` there would strand
/// `pendingReviews` instead, which MOVES the deadlock rather than removing it.
///
/// HOW THE FAULTS ARE INDUCED. Both arms drive the REAL `TeamCoordinator`
/// against a scripted offline client, so the production wiring is what is
/// measured.
///   - outer cancellation: the member cancels the run's own CTS mid-turn and
///     still returns a valid tool call, so the iteration completes and the loop
///     comes back to `AgentLoop.cs:306`'s boundary check with the token already
///     tripped — the shape `AgentLoopDispatchTests` already characterises.
///   - non-cancellation fault: the member's sandbox throws from its `Cwd`
///     getter, which `AgentLoop.cs:515` reads once per iteration. ⚠ That
///     injection point is synthetic; it was chosen because the loop's only
///     catches are OCE-filtered, so it is a reliable way to make a NON-OCE
///     exception escape `RunAsync`. What the test pins is the CLASS (any
///     non-OCE throw), not that particular getter.
///
/// `dispatch_worktree` is off in these profiles: the bracket is emitted whether
/// or not a worktree was provisioned, and leaving it off keeps these tests off
/// `git` and out of the SHARED `~/.vett/dispatches` tree.
/// </summary>
public class DispatchBracketAbortTests
{
    private const string LeaderGo = "LEADER-GO-PLEASE-DISPATCH";
    private const string MemberSystem = "MEMBER-SYSTEM-PROMPT-MARKER";
    private const string MemberTask = "MEMBER-WORK-MARKER";
    private const string MemberName = "implementer";

    /// <summary>Public because xUnit only discovers public test methods, and
    /// these arms are driven by [InlineData].</summary>
    public enum Fault { None, OuterCancel, NonCancellationThrow }

    // ---- section 1: the case the register named ---------------------------

    /// <summary>
    /// THE DEFECT, as written. Outer cancellation unwinds the dispatch; the
    /// bracket must still close or the run can never settle.
    /// </summary>
    [Fact]
    public async Task OuterCancellation_StillEmitsDispatchEnd()
    {
        var run = await RunTeamAsync(Fault.OuterCancel);

        AssertDispatchAborted(run, "outer_cancelled");
        AssertBracketBalanced(run);
    }

    // ---- section 2: the wider class the register did NOT name -------------

    /// <summary>
    /// The half the finding missed. A non-cancellation exception out of the
    /// agent loop strands the identical counter, and before the fix nothing
    /// caught it at all.
    /// </summary>
    [Fact]
    public async Task ANonCancellationFault_StillEmitsDispatchEnd()
    {
        var run = await RunTeamAsync(Fault.NonCancellationThrow);

        AssertDispatchAborted(run, "dispatch_faulted");
        AssertBracketBalanced(run);

        // MEASURED, not assumed. The rethrow lands in the leader's tool
        // dispatch (AgentLoop.cs:1063-1064), which turns it into an `error`
        // event and an observation — so ONE faulted member does not take the
        // whole team run down with it.
        //
        // ⚠ This was checked because a Stage-A dump showed an escaping
        // InvalidOperationException and it looked like the leader had died.
        // It had not: the test's own fault injection was still armed (the fix
        // disarms it on dispatch_end, which pre-fix never fired) and had hit
        // the LEADER's next iteration. A harness artifact reading as a
        // production finding — the label on a failure is itself a
        // measurement and can be wrong.
        Assert.Null(run.Thrown);
    }

    /// <summary>
    /// The counter is what actually deadlocked, so assert on the counter's
    /// arithmetic rather than only on the event's presence. This is the
    /// property Harness.cs implements: start increments, end decrements, and
    /// the settle loop waits for zero.
    /// </summary>
    [Theory]
    [InlineData(Fault.OuterCancel)]
    [InlineData(Fault.NonCancellationThrow)]
    [InlineData(Fault.None)]
    public async Task InFlightCounter_ReturnsToZero_OnEveryExitPath(Fault fault)
    {
        var run = await RunTeamAsync(fault);

        var inFlight = 0;
        var peak = 0;
        foreach (var e in run.Events)
        {
            if (e.Type == "dispatch_start") peak = Math.Max(peak, ++inFlight);
            else if (e.Type == "dispatch_end") inFlight--;
        }

        Assert.True(peak > 0, "PREMISE NOT MET: no dispatch was ever started." + run.Dump());
        Assert.True(inFlight == 0,
            $"dispatchesInFlight settled at {inFlight}, not 0 — Harness.cs:719 would spin to the wall clock and the run would be mislabelled `wall_clock_timeout`." + run.Dump());
    }

    // ---- section 3: the synthetic event must not read as a clean run ------

    /// <summary>
    /// ⛔ THE OVER-CORRECTION GUARD. An aborted dispatch must be
    /// DISTINGUISHABLE from one that ran and changed nothing. `iterations: 0`
    /// and `iterations: null` are not the same claim: 0 says the member did
    /// nothing, null says nobody could tell. Reporting 0 here would be the same
    /// class of defect as the capture failure that reported `files_changed: 0`.
    /// </summary>
    [Theory]
    [InlineData(Fault.OuterCancel)]
    [InlineData(Fault.NonCancellationThrow)]
    public async Task TheAbortedEnd_IsNotConfusableWithACleanDispatch(Fault fault)
    {
        var run = await RunTeamAsync(fault);
        var end = AbortedEnd(run);

        Assert.Equal(true, end.Data["dispatch_aborted"]);

        // COULD NOT MEASURE — never a measured zero.
        Assert.Null(end.Data["iterations"]);
        Assert.Null(end.Data["input_tokens"]);
        Assert.Null(end.Data["output_tokens"]);
        Assert.Null(end.Data["files_changed"]);
        Assert.Null(end.Data["diff_summary"]);
        Assert.Null(end.Data["self_assessment"]);
    }

    /// <summary>
    /// ⛔ THE FIX MUST NOT MOVE THE DEADLOCK. Harness.cs:527-543 increments a
    /// SECOND counter, `pendingReviews`, whenever a `dispatch_end` carries
    /// `pending_review: true`. Emitting true here would close the
    /// `dispatchesInFlight` bracket and open a `pendingReviews` one that nothing
    /// will ever close — the same wall-clock hang, one counter to the left.
    /// Nothing was captured, so there is nothing to accept or reject.
    /// </summary>
    [Theory]
    [InlineData(Fault.OuterCancel)]
    [InlineData(Fault.NonCancellationThrow)]
    public async Task TheAbortedEnd_DoesNotOpenAPendingReview(Fault fault)
    {
        var run = await RunTeamAsync(fault);
        var end = AbortedEnd(run);

        Assert.Equal(false, end.Data["pending_review"]);
        Assert.Equal(false, end.Data["worktree_retained"]);
        Assert.Equal(false, end.Data["capture_ran"]);
    }

    /// <summary>
    /// The stop reason has to name WHICH abort. `outer_cancelled` (the operator
    /// stopped the run) and `dispatch_faulted` (something threw) call for
    /// different responses, and a run post-mortem that cannot separate them is
    /// back to guessing — which is the whole complaint F13 starts from.
    /// </summary>
    [Fact]
    public async Task TheTwoAbortPaths_AreLabelledDistinctly()
    {
        var cancelled = AbortedEnd(await RunTeamAsync(Fault.OuterCancel));
        var faulted = AbortedEnd(await RunTeamAsync(Fault.NonCancellationThrow));

        Assert.NotEqual(cancelled.Data["stop_reason"], faulted.Data["stop_reason"]);
    }

    // ---- section 4: negative controls — must stay GREEN in every arm ------

    /// <summary>
    /// ⚠ THIS TEST CANNOT DETECT THE DEFECT. It is here to fail if the fix
    /// over-shoots into "emit an aborted end on every path". A dispatch that
    /// completes normally must produce exactly one `dispatch_end`, and it must
    /// NOT be the synthetic one.
    /// </summary>
    [Fact]
    public async Task ACleanDispatch_EmitsExactlyOneEnd_AndItIsNotTheAbortedOne()
    {
        var run = await RunTeamAsync(Fault.None);

        var end = Assert.Single(run.Events, e => e.Type == "dispatch_end");
        Assert.False(end.Data.ContainsKey("dispatch_aborted"),
            "a dispatch that finished normally was tagged as aborted — the synthetic path is firing where the real one already ran." + run.Dump());

        // The positive half: a real dispatch reports a real, non-null iteration
        // count. Without this, "always emit null" would satisfy section 3.
        Assert.NotNull(end.Data["iterations"]);
    }

    /// <summary>
    /// The double-emit shape: the aborted end fires AND execution falls through
    /// to the normal one, decrementing the counter twice. That drives
    /// `dispatchesInFlight` NEGATIVE, which makes `> 0` false and lets a run
    /// with a genuinely live member settle early — a silent truncation rather
    /// than a hang, and harder to notice.
    /// </summary>
    [Theory]
    [InlineData(Fault.OuterCancel)]
    [InlineData(Fault.NonCancellationThrow)]
    public async Task AnAbortedDispatch_EmitsExactlyOneEnd_NotTwo(Fault fault)
    {
        var run = await RunTeamAsync(fault);

        var ends = run.Events.Where(e => e.Type == "dispatch_end").ToList();
        Assert.True(ends.Count == 1,
            $"expected exactly 1 dispatch_end, saw {ends.Count} — a double decrement drives the in-flight counter negative and lets a live run settle early." + run.Dump());
    }

    // ---- assertions -------------------------------------------------------

    private static Event AbortedEnd(RunOutcome run)
    {
        var end = run.Events.FirstOrDefault(e =>
            e.Type == "dispatch_end" && e.Data.ContainsKey("dispatch_aborted"));
        Assert.True(end is not null,
            "no aborted dispatch_end was emitted — the bracket opened by dispatch_start was never closed." + run.Dump());
        return end!;
    }

    private static void AssertDispatchAborted(RunOutcome run, string expectedStopReason)
    {
        // PREMISE FIRST: a dispatch really started, so there really was a
        // bracket to leave open. Without this a run where assign_task never
        // fired would score as evidence about the abort path.
        Assert.True(run.Events.Any(e => e.Type == "dispatch_start"),
            "PREMISE NOT MET: no dispatch_start — the fault was induced before any bracket opened, so this run says nothing about F13." + run.Dump());

        var end = AbortedEnd(run);
        Assert.Equal(expectedStopReason, end.Data["stop_reason"]);
        Assert.Equal(MemberName, end.Data["member"]);
        Assert.Equal("main", end.Data["thread_id"]);
    }

    private static void AssertBracketBalanced(RunOutcome run)
    {
        var starts = run.Events.Count(e => e.Type == "dispatch_start");
        var ends = run.Events.Count(e => e.Type == "dispatch_end");
        Assert.True(starts == ends,
            $"{starts} dispatch_start vs {ends} dispatch_end — every start MUST be closed or Harness.cs:719 spins forever." + run.Dump());
    }

    // ---- harness ----------------------------------------------------------

    private sealed record RunOutcome(List<Event> Events, Exception? Thrown)
    {
        public string Dump()
        {
            var types = string.Join(", ", Events.Select(e => e.Type));
            var ends = Events.Where(e => e.Type is "dispatch_start" or "dispatch_end")
                .Select(e => e.Type + "{" + string.Join(",", e.Data.Select(kv => kv.Key + "=" + (kv.Value?.ToString() ?? "null"))) + "}");
            return $"\nthrown={Thrown?.GetType().Name ?? "(none)"}: {Thrown?.Message}" +
                   $"\nevents=[{types}]" +
                   $"\nbracket=[{string.Join(" ;; ", ends)}]";
        }
    }

    private async Task<RunOutcome> RunTeamAsync(Fault fault)
    {
        var events = new ConcurrentQueue<Event>();
        var poison = new PoisonCell();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        void OnEvent(Event e)
        {
            events.Enqueue(e);
            if (fault != Fault.NonCancellationThrow) return;

            // Arm the sandbox fault only for the window the member owns, then
            // disarm the moment the bracket closes. A poison left armed would
            // also take down the LEADER's next iteration, and the test would be
            // measuring a dead leader instead of the dispatch bracket.
            if (e.Type == "dispatch_start") poison.Armed = true;
            else if (e.Type == "dispatch_end") poison.Armed = false;
        }

        var profile = new Profile
        {
            Llm = new LlmConfig(),
            Team = new TeamConfig
            {
                // ⭐ ADDED 2026-08-27, when an absent width ceiling became an
                // error rather than a silent "unlimited". EXPLICIT 0 = unlimited,
                // so this fixture behaves exactly as it did before. These tests
                // are about the dispatch_start/dispatch_end BRACKET on abort
                // paths; a ceiling could refuse a dispatch and turn a measured
                // abort into a dispatch that never opened, which is a different
                // event stream than the one being asserted on.
                MaxConcurrentDispatches = 0,
                Leader = new MemberConfig { Name = "leader", SystemPrompt = "LEADER-SYSTEM", MaxIterations = 6 },
                Members =
                {
                    new MemberConfig
                    {
                        Name = MemberName,
                        SystemPrompt = MemberSystem,
                        MaxIterations = 6,
                        // file_editor's schema carries the `{working_dir}`
                        // placeholder, which is what makes AgentLoop.cs:515
                        // read Sandbox.Cwd once per iteration. Without a
                        // schema the read is skipped and the fault cannot be
                        // injected at all.
                        Tools = fault == Fault.NonCancellationThrow ? ["file_editor"] : [],
                    },
                },
                // Off: the bracket does not depend on a worktree, and this keeps
                // the test off `git` and out of the shared dispatches tree.
                DispatchWorktree = false,
                DispatchMaxAgeDays = 0,
                AutoInjectAsyncResults = false,
            },
        };

        var client = new TeamScriptClient(fault, cts);
        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync(LeaderGo);
        chan.Writer.Complete();

        Exception? thrown = null;
        try
        {
            await TeamCoordinator.RunInteractiveAsync(
                profile, client, "test-model", new PoisonableSandbox("/fake", poison), "bracket-test",
                chan.Reader,
                onAssistantText: null, onWaitingForInput: null, onEvent: OnEvent,
                seedHistory: null, turnInterrupt: null, compactRequest: null,
                cwd: "/fake", ct: cts.Token);
        }
        catch (Exception ex)
        {
            // An aborted dispatch RETHROWS by design — the leader's own
            // handling of that is not what F13 is about. What matters is that
            // the bracket closed on the way out, which the event stream carries
            // regardless of where the exception finally lands.
            thrown = ex;
        }

        return new RunOutcome(events.ToList(), thrown);
    }

    /// <summary>Shared mutable arming flag: every sandbox derived by
    /// WithCwd / WithDispatchWorktree sees the same cell, so the fault can be
    /// armed after the member's sandbox has already been handed out.</summary>
    private sealed class PoisonCell { public volatile bool Armed; }

    /// <summary>
    /// Minimal sandbox whose <c>Cwd</c> getter throws while armed. Nothing here
    /// shells out. `Cwd` is the chosen seam because AgentLoop's only catches are
    /// OCE-filtered, so a throw from it escapes `RunAsync` uncaught — which is
    /// precisely the class of exit F13 left unbracketed.
    /// </summary>
    private sealed class PoisonableSandbox(string cwd, PoisonCell poison) : ISandbox
    {
        public string Cwd => poison.Armed
            ? throw new InvalidOperationException("injected non-cancellation fault inside the member's agent loop")
            : cwd;
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("", 0, cwd, false));
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default) => Task.FromResult("");
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string n, CancellationToken ct = default) => Task.FromResult(("", (string?)null));
        public Task<string> FileInsertAsync(string s, string p, int l, string n, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("");
        public Task SessionCreateAsync(string n, string c, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string newCwd) => new PoisonableSandbox(newCwd, poison);
        public ISandbox WithDispatchWorktree(string newCwd, string root) => new PoisonableSandbox(newCwd, poison);
    }

    /// <summary>Drives leader and member off one client, told apart by the
    /// member's system-prompt marker rather than call order.</summary>
    private sealed class TeamScriptClient(Fault fault, CancellationTokenSource cts) : IChatClient
    {
        private int _leaderCalls;
        private int _memberCalls;
        private readonly object _lock = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var list = messages.ToList();
            var isMember = list.Any(m => (m.Text ?? "").Contains(MemberSystem));

            if (isMember)
            {
                int n;
                lock (_lock) { n = ++_memberCalls; }

                if (fault == Fault.OuterCancel && n == 1)
                {
                    // Cancel while this turn is already in flight, then hand
                    // back a well-formed tool call so the iteration COMPLETES.
                    // The loop then returns to AgentLoop.cs:306's boundary
                    // check with the token already tripped and throws there —
                    // past the OCE catch that would otherwise finalise the run
                    // as "cancelled" without propagating.
                    //
                    // The tool is deliberately UNREGISTERED: a registered tool
                    // might observe the cancelled token itself, which would
                    // throw INSIDE the iteration body and be swallowed by
                    // AgentLoop.cs:401. An unregistered name returns a plain
                    // error observation and lets the loop come back round.
                    cts.Cancel();
                    return Reply(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                    {
                        new FunctionCallContent("m1", "no_such_tool_keep_looping", new Dictionary<string, object?>()),
                    }));
                }

                ct.ThrowIfCancellationRequested();
                return Reply(new ChatMessage(ChatRole.Assistant,
                    "I reviewed the task and made no edits. <self_assessment>done</self_assessment>"));
            }

            ct.ThrowIfCancellationRequested();
            int k;
            lock (_lock) { k = ++_leaderCalls; }
            if (k == 1)
            {
                return Reply(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("call-1", "assign_task", new Dictionary<string, object?>
                    {
                        ["member"] = MemberName,
                        ["task"] = MemberTask,
                    }),
                }));
            }
            return Reply(new ChatMessage(ChatRole.Assistant, "Dispatch handled. Stopping here."));
        }

        private static Task<ChatResponse> Reply(ChatMessage m) =>
            Task.FromResult(new ChatResponse([new ChatMessage(m.Role, m.Contents.ToList())]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
