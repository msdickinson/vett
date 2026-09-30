using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// ROUND-TRIP ROUTING — the three ask/answer services that pair a
/// request_id with a reply from the chat UI: <see cref="PermissionService"/>,
/// PlanModeUnlockService, and <see cref="UserQuestionService"/>.
///
/// All three were originally the same shape: an unbounded channel that
/// every waiter read from, writing back anything addressed to someone
/// else so a sibling could claim it.
///
/// THE DEFECT (measured 2026-08-26). Re-queuing a mismatched reply into
/// the channel you are about to read again means the very next ReadAsync
/// returns that same item — and on an UNBOUNDED channel both calls
/// complete synchronously, so the `await`s never yield. With no
/// suspension point the loop never reaches one, which means the method
/// NEVER RETURNS ITS TASK: it runs an infinite loop on whichever thread
/// called it. In the product that is the agent loop's own thread.
///
/// One orphaned reply is enough to trigger it — a double-clicked Allow
/// button, or a reply for a tool call cancelled before the answer landed
/// — and because nothing ever drains it, it poisons the session.
///
/// UserQuestionService is the original and carried an `await
/// Task.Yield()` in that loop, with a comment naming the hazard: "so we
/// don't busy-loop on a permanently-stuck mismatched answer". Both
/// copies dropped exactly that line — PlanModeUnlockService's own
/// summary says it "Mirrors PermissionService down to the
/// requeue-on-mismatch loop". The yield is why the original merely span
/// while the copies wedged outright, and the YIELD family below shows
/// that split directly: before the fix it was red for both copies and
/// green for the original.
///
/// THE FIX is to route by id — a dictionary of waiters — rather than
/// share one queue. Parking mismatches was tried first and rejected:
/// it kills the spin but DEADLOCKS siblings, because two waiters each
/// take the other's reply out of the channel and then block holding it.
/// The HANDOFF family caught that.
///
/// TWO FAMILIES, proving different things:
///
///   YIELD — does the call hand its Task back at all when an orphan is
///   queued? The defect at its sharpest, and it needs no timing
///   threshold: a correct implementation returns in microseconds, a
///   spinning one never returns.
///
///   HANDOFF — the behaviour the requeue existed for. These must stay
///   green across any fix, or a "fix" that drops mismatched replies on
///   the floor, or one that deadlocks siblings, would look like success.
///
/// ⛔ A CPU-TIME FAMILY WAS TRIED AND DELIBERATELY REMOVED. It measured
/// each waiter against a paired control window and flagged excess CPU,
/// which is the only way to catch a loop that yields but still spins.
/// It was shipped with a positive control — a deliberate flat-out
/// spinner pushed through the same harness — and that control FAILED on
/// roughly one run in three, once reporting a pinned core as only 109ms
/// of excess. Process-wide TotalProcessorTime is measured across a suite
/// whose other collections run in parallel, so the control term is as
/// noisy as the treatment term and no estimator over it is sound. The
/// budget was not widened to hide this; the instrument was withdrawn,
/// because a ruler that cannot see a flat-out spinner would have made
/// every assertion in that family vacuous. Do not re-add it without a
/// per-thread CPU measurement and a positive control that stays green.
/// </summary>
public class RoundTripRequeueTests
{
    // ---- YIELD: does the call hand its Task back at all? ----

    /// <summary>
    /// Invokes <paramref name="startWaiter"/> on a dedicated thread with
    /// an orphan already queued, and reports whether the call returned a
    /// Task rather than disappearing into a synchronous loop.
    ///
    /// A dedicated thread, not Task.Run: the point is to observe whether
    /// the CALLER gets control back, which a pool thread would hide. The
    /// two-second allowance is not a performance threshold — it only
    /// bounds how long we wait before concluding the call never returns.
    /// </summary>
    private static bool CallReturnsItsTask(Action postOrphan, Func<CancellationToken, Task> startWaiter)
    {
        using var cts = new CancellationTokenSource();
        var returned = new ManualResetEventSlim(false);

        postOrphan();

        var thread = new Thread(() =>
        {
            try
            {
                _ = startWaiter(cts.Token);
                returned.Set();
            }
            catch (OperationCanceledException) { returned.Set(); }
        })
        { IsBackground = true, Name = "requeue-waiter" };
        thread.Start();

        var ok = returned.Wait(TimeSpan.FromSeconds(2));
        cts.Cancel();
        return ok;
    }

    [Fact]
    public void PermissionService_returns_its_task_when_an_orphan_is_queued()
    {
        var svc = new PermissionService();

        Assert.True(
            CallReturnsItsTask(
                () => svc.PostDecision("orphan", new PermissionResponse(PermissionRule.Auto, false)),
                ct => svc.AwaitDecisionAsync("the-live-request", ct)),
            "AwaitDecisionAsync never returned its Task. The loop has no yield point, so it ran an "
            + "infinite read/rewrite loop synchronously on the calling thread — in the product that "
            + "is the agent loop's own thread, wedged on a stale permission reply.");
    }

    [Fact]
    public void PlanModeUnlockService_returns_its_task_when_an_orphan_is_queued()
    {
        var svc = new PlanModeUnlockService();

        Assert.True(
            CallReturnsItsTask(
                () => svc.PostDecision("orphan", true),
                ct => svc.AwaitDecisionAsync("the-live-request", ct)),
            "AwaitDecisionAsync never returned its Task — the loop ran synchronously on the caller.");
    }

    [Fact]
    public void UserQuestionService_returns_its_task_when_an_orphan_is_queued()
    {
        var svc = new UserQuestionService();

        Assert.True(
            CallReturnsItsTask(
                () => svc.PostAnswer("orphan", "stale"),
                ct => svc.AwaitAnswerAsync("the-live-question", ct)),
            "AwaitAnswerAsync never returned its Task.");
    }

    // ---- HANDOFF: what the requeue was FOR. Must stay green. ----

    [Fact]
    public async Task Two_permission_waiters_each_receive_their_own_decision()
    {
        var svc = new PermissionService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var a = svc.AwaitDecisionAsync("call-a", cts.Token);
        var b = svc.AwaitDecisionAsync("call-b", cts.Token);

        // Answer OUT OF ORDER — b first. Neither waiter may consume the
        // other's decision, and neither may block holding it.
        svc.PostDecision("call-b", new PermissionResponse(PermissionRule.Deny, true));
        svc.PostDecision("call-a", new PermissionResponse(PermissionRule.Auto, false));

        var ra = await a;
        var rb = await b;

        Assert.Equal(PermissionRule.Auto, ra.Decision);
        Assert.False(ra.RememberForKind);
        Assert.Equal(PermissionRule.Deny, rb.Decision);
        Assert.True(rb.RememberForKind);
    }

    [Fact]
    public async Task Two_plan_mode_waiters_each_receive_their_own_decision()
    {
        var svc = new PlanModeUnlockService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var a = svc.AwaitDecisionAsync("call-a", cts.Token);
        var b = svc.AwaitDecisionAsync("call-b", cts.Token);

        svc.PostDecision("call-b", false);
        svc.PostDecision("call-a", true);

        Assert.True(await a);
        Assert.False(await b);
    }

    [Fact]
    public async Task Two_question_waiters_each_receive_their_own_answer()
    {
        var svc = new UserQuestionService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var a = svc.AwaitAnswerAsync("q-a", cts.Token);
        var b = svc.AwaitAnswerAsync("q-b", cts.Token);

        svc.PostAnswer("q-b", "answer-b");
        svc.PostAnswer("q-a", "answer-a");

        Assert.Equal("answer-a", await a);
        Assert.Equal("answer-b", await b);
    }

    [Fact]
    public async Task A_cancelled_permission_waiter_leaves_a_parked_decision_for_its_successor()
    {
        var svc = new PermissionService();

        // First waiter is asked about a call it will never be answered
        // about, and a decision addressed to someone else arrives while
        // it waits.
        using var doomed = new CancellationTokenSource();
        var abandoned = svc.AwaitDecisionAsync("never-answered", doomed.Token);
        svc.PostDecision("answered-later", new PermissionResponse(PermissionRule.Deny, false));

        // No wait here, deliberately. `AwaitDecisionAsync` registers its
        // waiter under `_lock` BEFORE its only suspension point
        // (`await tcs.Task`), and `PostDecision` does all of its work inside
        // that same lock — so both facts this test depends on are already
        // established by the time the two statements above return. The
        // `await Task.Delay(100)` that stood here was waiting for something
        // that had already happened; it bought no ordering and cost 100ms
        // on every run.
        doomed.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        // The decision the abandoned waiter was never entitled to must
        // still be claimable — under the old shared-channel design a
        // cancelled sibling could swallow it on its way out.
        using var live = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var claimed = await svc.AwaitDecisionAsync("answered-later", live.Token);
        Assert.Equal(PermissionRule.Deny, claimed.Decision);
    }

    [Fact]
    public async Task A_decision_that_arrives_before_the_await_is_not_lost()
    {
        // The stdio reader can post faster than the agent loop reaches
        // its await. The old channel tolerated that because the reply
        // simply sat in the queue; routing by id has to hold it
        // deliberately.
        var svc = new PermissionService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        svc.PostDecision("early", new PermissionResponse(PermissionRule.Auto, true));

        var claimed = await svc.AwaitDecisionAsync("early", cts.Token);
        Assert.Equal(PermissionRule.Auto, claimed.Decision);
        Assert.True(claimed.RememberForKind);
    }
}
