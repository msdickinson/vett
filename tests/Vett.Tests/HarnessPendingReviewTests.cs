using Vett.Agent;
using Vett.Bench.Team;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Failure tests for the team-bench settle gate's PENDING-REVIEW COUNTER.
///
/// WHY THIS FILE EXISTS. The counter tracks implementer dispatches whose
/// worktree has not yet been accepted or rejected, and the settle loop
/// refuses to declare the run finished while it is above zero. Before
/// 2026-08-24 the increment side was guarded (it required
/// <c>pending_review == true</c> on the <c>dispatch_end</c> event) and the
/// DECREMENT side was not: every <c>tool_call_end</c> whose
/// <c>tool_name</c> was <c>accept_dispatch</c> or <c>reject_dispatch</c>
/// retired a pending review, reading neither the <c>success</c> flag nor any
/// task correlation.
///
/// <c>AgentLoop</c> emits <c>tool_call_end</c> with <c>["success"] = false</c>
/// on a tool exception, on a soft failure (a result starting "Error:"), and
/// on both hook-denial paths — so a review that DID NOT HAPPEN decremented
/// exactly like one that did. The disambiguating key was present in the very
/// dictionary being read, and ignored.
///
/// Two consequences, and the tests below cover them separately because they
/// fail through different mechanisms:
///
///  1. THE COUNTER GOES NEGATIVE, and negative is not a smaller number — it
///     is CREDIT AGAINST A FUTURE DISPATCH. -1 plus a genuine new dispatch
///     is 0, and <c>if (pending &gt; 0) continue;</c> then reads "nothing
///     outstanding" over a worktree nobody reviewed. The harness scores a
///     workspace missing that member's work and calls it a finished run.
///  2. THE QUIET WINDOW COLLAPSES. Three lines below the decrement, the same
///     unguarded predicate set <c>anyReviewSettled</c>, which cuts the settle
///     threshold from <c>QuietPeriodSeconds</c> (20) to
///     <c>PostSettlementQuietSeconds</c> (5). A FAILED review call shortened
///     the window 4x as though work had been reviewed.
///
/// FAIL DIRECTION, chosen deliberately and asserted here rather than left to
/// the reader: a review whose success cannot be established DOES NOT COUNT.
/// The gate holds, and the run spends its wall clock and surfaces a visible
/// timeout. The alternative — settling early on a workspace missing a
/// member's work — is a silent wrong verdict, and a false GREEN that accepts
/// wrong work is strictly worse than a false RED that refuses in public.
///
/// The healthy controls are load-bearing. A predicate that answered "not a
/// successful review" to everything would satisfy every failure test in this
/// file and break every run; <c>A_successful_accept_IS_a_review</c>,
/// <c>A_successful_reject_IS_a_review</c> and
/// <c>A_pending_review_IS_retired_by_a_successful_call</c> are what stop that.
///
/// POWER, measured by reverting each half in place rather than reasoned
/// about: dropping the <c>success</c> conjunct turns 6 of these red by name;
/// dropping the zero-clamp turns a DIFFERENT 2 red. The halves are
/// orthogonal, and the overlap is exactly one case —
/// <c>A_FAILED_review_followed_by_a_SUCCESSFUL_retry_retires_exactly_one</c>
/// stayed green under BOTH reverts, because either half alone is enough to
/// survive a retry. Neither half is redundant, and that one test is a
/// coverage bonus rather than a guard: it would not notice a regression that
/// removed only one of them.
/// </summary>
public class HarnessPendingReviewTests
{
    static Event ToolEnd(string toolName, object? success)
    {
        var data = new Dictionary<string, object?> { ["tool_name"] = toolName };
        if (success is not null) data["success"] = success;
        return new Event("tool_call_end", data);
    }

    // ---- section 1: which events count as a review that HAPPENED ----

    [Fact]
    public void A_successful_accept_IS_a_review()
        => Assert.True(Harness.IsSuccessfulReview(ToolEnd("accept_dispatch", true)));

    [Fact]
    public void A_successful_reject_IS_a_review()
        => Assert.True(Harness.IsSuccessfulReview(ToolEnd("reject_dispatch", true)));

    [Fact]
    public void A_FAILED_accept_is_NOT_a_review()
    {
        // The defect, isolated: same event type, same tool name, only
        // `success` differs. Under the old predicate this retired a pending
        // review that was still outstanding.
        Assert.False(Harness.IsSuccessfulReview(ToolEnd("accept_dispatch", false)));
    }

    [Fact]
    public void A_FAILED_reject_is_NOT_a_review()
        => Assert.False(Harness.IsSuccessfulReview(ToolEnd("reject_dispatch", false)));

    [Fact]
    public void A_review_call_with_NO_success_key_is_NOT_a_review()
    {
        // All four `tool_call_end` emit sites in AgentLoop carry `success`,
        // so an absent key means a NEW, UNAUDITED emitter. Holding the gate
        // is the right answer for a source we have not checked — this is the
        // fail-closed direction, pinned so a later "convenience" default of
        // true cannot slip in unnoticed.
        Assert.False(Harness.IsSuccessfulReview(ToolEnd("accept_dispatch", null)));
    }

    [Fact]
    public void A_success_value_that_is_not_a_BOOL_is_NOT_a_review()
    {
        // `ok is true` requires a boxed bool. Events reach the settle gate as
        // in-memory `Event` records straight from AgentLoop, never through a
        // JSON round-trip, so this is a guard against a future emitter
        // stringifying the flag — not a live case.
        Assert.False(Harness.IsSuccessfulReview(ToolEnd("accept_dispatch", "true")));
    }

    [Fact]
    public void A_DIFFERENT_tool_succeeding_is_not_a_review()
    {
        // Guards the other direction: gating on `success` must not have
        // widened the predicate to "any successful tool call".
        Assert.False(Harness.IsSuccessfulReview(ToolEnd("dispatch", true)));
        Assert.False(Harness.IsSuccessfulReview(ToolEnd("declare_done", true)));
    }

    [Fact]
    public void A_review_STARTING_is_not_a_review_HAPPENING()
    {
        var start = new Event("tool_call_start", new Dictionary<string, object?>
        {
            ["tool_name"] = "accept_dispatch",
            ["success"] = true,
        });
        Assert.False(Harness.IsSuccessfulReview(start));
    }

    [Fact]
    public void An_event_with_no_tool_name_is_not_a_review()
        => Assert.False(Harness.IsSuccessfulReview(
            new Event("tool_call_end", new Dictionary<string, object?> { ["success"] = true })));

    // ---- section 2: the counter itself, clamped at zero ----

    [Fact]
    public void A_pending_review_IS_retired_by_a_successful_call()
    {
        var pending = 1;
        Assert.True(Harness.DecrementPendingReviews(ref pending));
        Assert.Equal(0, pending);
    }

    [Fact]
    public void Two_pending_reviews_retire_one_at_a_time()
    {
        var pending = 2;
        Assert.True(Harness.DecrementPendingReviews(ref pending));
        Assert.Equal(1, pending);
        Assert.True(Harness.DecrementPendingReviews(ref pending));
        Assert.Equal(0, pending);
    }

    [Fact]
    public void Retiring_a_review_that_was_never_pending_CANNOT_go_negative()
    {
        // The second half of the fix. Gating on `success` closes the
        // failed-call route to a negative counter; it does not close the
        // duplicate-successful-call route, because the counter carries no
        // task correlation.
        var pending = 0;
        Assert.False(Harness.DecrementPendingReviews(ref pending));
        Assert.Equal(0, pending);
    }

    [Fact]
    public void A_duplicate_successful_review_CANNOT_bank_credit_against_a_future_dispatch()
    {
        // The failure this clamp exists to prevent, written as the sequence
        // that produces it. Without the clamp the second retire leaves -1,
        // the new dispatch brings it back to 0, and the settle gate reads
        // "nothing outstanding" over a worktree nobody reviewed.
        var pending = 1;
        Harness.DecrementPendingReviews(ref pending);   // the real review
        Harness.DecrementPendingReviews(ref pending);   // a retry of the same one

        pending++;                                      // a genuine new dispatch

        Assert.Equal(1, pending);
        Assert.True(pending > 0, "the settle gate must still see one outstanding review");
    }

    // ---- section 3: the two call sites, end to end over an event stream ----

    static (int Pending, bool AnySettled) Replay(params Event[] stream)
    {
        // Mirrors the two consumers in Harness.RunInstanceAsync: the OnEvent
        // decrement, and the settle loop's `anyReviewSettled` flip. Both must
        // ask the same question — the original defect was that they asked the
        // same WRONG question in two places, and a fix to one would have left
        // the other cutting the quiet window.
        var pending = 0;
        foreach (var ev in stream)
        {
            if (ev.Type == "dispatch_end"
                && ev.Data.TryGetValue("pending_review", out var pr) && pr is true)
            {
                pending++;
            }
            else if (Harness.IsSuccessfulReview(ev))
            {
                Harness.DecrementPendingReviews(ref pending);
            }
        }
        return (pending, stream.Any(Harness.IsSuccessfulReview));
    }

    static Event DispatchEnd(bool pendingReview) =>
        new("dispatch_end", new Dictionary<string, object?> { ["pending_review"] = pendingReview });

    [Fact]
    public void A_dispatch_reviewed_SUCCESSFULLY_leaves_the_gate_open()
    {
        var (pending, anySettled) = Replay(DispatchEnd(true), ToolEnd("accept_dispatch", true));

        Assert.Equal(0, pending);
        Assert.True(anySettled, "a real review shortens the quiet window — that is the point of it");
    }

    [Fact]
    public void A_dispatch_whose_review_FAILED_holds_the_gate_shut()
    {
        var (pending, anySettled) = Replay(DispatchEnd(true), ToolEnd("accept_dispatch", false));

        Assert.Equal(1, pending);
        Assert.False(anySettled,
            "a failed review must not cut the quiet window from QuietPeriodSeconds to " +
            "PostSettlementQuietSeconds — nothing was reviewed");
    }

    [Fact]
    public void A_FAILED_review_followed_by_a_SUCCESSFUL_retry_retires_exactly_one()
    {
        // The common real-world shape: the leader passes a stale task_id, the
        // tool throws, the leader retries correctly. Under the old code BOTH
        // calls decremented and the counter finished at -1 for one dispatch.
        var (pending, anySettled) = Replay(
            DispatchEnd(true),
            ToolEnd("accept_dispatch", false),
            ToolEnd("accept_dispatch", true));

        Assert.Equal(0, pending);
        Assert.True(anySettled);
    }

    [Fact]
    public void A_RESEARCHER_dispatch_never_becomes_a_pending_review()
    {
        // Researcher dispatches return pending_review=false — no worktree to
        // apply. Control for the increment side, which was already guarded.
        var (pending, _) = Replay(DispatchEnd(false));
        Assert.Equal(0, pending);
    }

    [Fact]
    public void An_UNREVIEWED_dispatch_cannot_be_cancelled_by_an_unrelated_failed_call()
    {
        // The whole defect in one stream: two implementer dispatches, one
        // reviewed for real and one review that errored. Under the old
        // predicate this landed at 0 and the run settled with a member's
        // work unreviewed and unscored.
        var (pending, _) = Replay(
            DispatchEnd(true),
            DispatchEnd(true),
            ToolEnd("accept_dispatch", true),
            ToolEnd("reject_dispatch", false));

        Assert.Equal(1, pending);
    }
}
