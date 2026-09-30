using Vett.Agent;
using Vett.Bench.Team;

namespace Vett.Tests;

/// <summary>
/// F11 — a budget assertion evaluated against counters that were still moving
/// when we read them.
///
/// `Harness.PublishIterationCounts` already stamps aborted runs with
/// `ITERATION_COUNTS_PARTIAL`, and the note it publishes ends with the words
/// "...and do not evaluate a budget assertion against them." That sentence was
/// TRUE, PUBLISHED, and UNENFORCED: `EvalBudget` read the lower bounds as
/// measurements and returned PASS. Adopting a law is not installing it — it
/// needs a check at the point of use, and a check needs a failure test.
///
/// THE ASYMMETRY THESE TESTS PIN DOWN. Over the cap is sound on a lower bound
/// (the true count is only higher, so the violation is real). Under the cap is
/// worth nothing on a lower bound. So the two directions must NOT get the same
/// treatment, and the over-cap answer must survive an incomplete run rather
/// than being swallowed by a blanket "could not measure".
///
/// POWER, MEASURED — three separate reverts, each run and counted. I had
/// PREDICTED 6 red for stage A and measured 4; the arithmetic below is the
/// measurement, not the prediction.
///
///   A. `countsComplete` reverted to an ignored parameter ⇒ 4 red, all in
///      section 1 (`..._under_the_leader_cap`, `..._under_the_member_cap`,
///      `The_REFUSAL_names_...`, `..._with_NO_counts_at_all`). Section 2 stays
///      green because a cap violation is reported either way — which is the
///      point: stage A has NO power over the asymmetry, only over the refusal.
///   B. the check restored but moved ABOVE the over-cap tests (i.e. the
///      ordering reverted) ⇒ 4 red. Only TWO of those measure the ordering:
///      both `OVER_the_..._reports_the_VIOLATION_not_the_blindness`. The other
///      two are artifacts of the placeholder message the revert returned, and
///      are NOT evidence about ordering. So the ordering's real power is 2.
///   C. the `[|members| = 0]` disclosure dropped from the Pass description ⇒
///      exactly 1 red (`..._CARRIES_THE_EMPTY_POPULATION`), with its control
///      `..._does_NOT_carry_the_empty_marker` still green — the disclosure is
///      pinned in both directions, so it cannot be satisfied by stamping every
///      row.
///
/// The four completed-run controls in section 3 stay green through all three
/// stages, which is what stops a predicate that simply fails everything from
/// satisfying this file.
/// </summary>
public class BudgetAssertionCompletenessTests
{
    private static AssertionResult Eval(
        BudgetSpec budget,
        int leaderIterations,
        Dictionary<string, int>? memberIterations,
        bool countsComplete)
        => AssertionEngine.Evaluate(
            [new TeamBenchAssertion { Budget = budget }],
            Array.Empty<Event>(),
            Path.GetTempPath(),
            leaderIterations,
            memberIterations ?? new Dictionary<string, int>(),
            countsComplete)[0];

    // ---- section 1: incomplete counters cannot certify UNDER budget ----

    [Fact]
    public void An_ABORTED_run_under_the_leader_cap_is_NOT_a_satisfied_budget()
    {
        var r = Eval(new BudgetSpec { LeaderItersMax = 40 }, 12, null, countsComplete: false);

        Assert.False(r.Pass);
        Assert.Contains(Harness.PartialCountsMarker, r.Detail);
    }

    [Fact]
    public void An_ABORTED_run_under_the_member_cap_is_NOT_a_satisfied_budget()
    {
        var r = Eval(
            new BudgetSpec { MemberItersMax = 22 },
            leaderIterations: 3,
            new Dictionary<string, int> { ["m1"] = 5 },
            countsComplete: false);

        Assert.False(r.Pass);
        Assert.Contains(Harness.PartialCountsMarker, r.Detail);
    }

    [Fact]
    public void The_REFUSAL_names_the_counts_it_could_not_trust()
    {
        // The failure has to be actionable: a reader must see WHICH numbers
        // are lower bounds, not just that something was unmeasurable.
        var r = Eval(
            new BudgetSpec { LeaderItersMax = 40, MemberItersMax = 22 },
            leaderIterations: 12,
            new Dictionary<string, int> { ["impl"] = 5, ["arch"] = 2 },
            countsComplete: false);

        Assert.False(r.Pass);
        Assert.Contains("leader_iterations>=12", r.Detail);
        Assert.Contains("arch>=2", r.Detail);
        Assert.Contains("impl>=5", r.Detail);
    }

    [Fact]
    public void An_ABORTED_run_with_NO_counts_at_all_is_NOT_a_satisfied_budget()
    {
        // The headline F11 shape: nothing observed, everything passes.
        var r = Eval(new BudgetSpec { MemberItersMax = 22 }, 0, null, countsComplete: false);

        Assert.False(r.Pass);
        Assert.Contains("none observed", r.Detail);
    }

    // ---- section 2: OVER the cap stays sound on a lower bound ----

    [Fact]
    public void OVER_the_leader_cap_on_an_ABORTED_run_reports_the_VIOLATION_not_the_blindness()
    {
        // A lower bound of 57 against a cap of 40 is a real violation — the
        // true count can only be higher. Reporting "could not measure" here
        // would DISCARD a sound finding, so ordering matters, not just the
        // pass/fail bit.
        var r = Eval(new BudgetSpec { LeaderItersMax = 40 }, 57, null, countsComplete: false);

        Assert.False(r.Pass);
        Assert.Contains("leader ran 57 iters (cap 40)", r.Detail);
        Assert.DoesNotContain(Harness.PartialCountsMarker, r.Detail);
    }

    [Fact]
    public void OVER_the_member_cap_on_an_ABORTED_run_reports_the_VIOLATION_not_the_blindness()
    {
        var r = Eval(
            new BudgetSpec { MemberItersMax = 22 },
            leaderIterations: 3,
            new Dictionary<string, int> { ["impl"] = 31 },
            countsComplete: false);

        Assert.False(r.Pass);
        Assert.Contains("member impl ran 31 iters (cap 22)", r.Detail);
        Assert.DoesNotContain(Harness.PartialCountsMarker, r.Detail);
    }

    /// <summary>
    /// ⭐ THE MEMBER COUNT IS A SUBTREE TOTAL, AND THE FAILURE SENTENCE MUST SAY SO.
    ///
    /// A LABEL IS NOT A DIAGNOSIS. memberIterations is keyed on the iteration_start
    /// thread_id (Harness.IterationThreadId), and TaggedEmit rewrites a nested
    /// child's thread_id to its PARENT's task id — so a member that dispatched a
    /// sub-team is credited with its own iterations PLUS everything underneath it.
    ///
    /// This is not hypothetical. In the live ds-manager-pro-w10 run (2026-08-26)
    /// the ONLY failing assertion read "member feature-lead-d-2 ran 255 iters
    /// (cap 120)". Read as one agent, that says a leader loop would not terminate,
    /// and the next hour goes into AgentLoop. The truth was 60 own iterations plus
    /// 25 serially-dispatched sub-workers contributing 195 — a fan-out problem, in
    /// a completely different file. The bare number sent the reader to the wrong
    /// place, so the number ships with its unit attached.
    ///
    /// ⚠ ASSERTED ON THE WORD "SUBTREE", not on the whole sentence: this guards the
    /// CLAIM against a well-meaning message cleanup, without pinning the wording so
    /// hard that any rephrase is a test failure. The byte-identical leading clause
    /// is covered by the test above, which is what downstream greps match on.
    /// </summary>
    [Fact]
    public void The_member_cap_violation_says_the_count_is_a_SUBTREE_total()
    {
        var r = Eval(
            new BudgetSpec { MemberItersMax = 120 },
            leaderIterations: 23,
            new Dictionary<string, int> { ["feature-lead-d-2"] = 255 },
            countsComplete: true);

        Assert.False(r.Pass);
        Assert.Contains("member feature-lead-d-2 ran 255 iters (cap 120)", r.Detail);
        Assert.Contains("SUBTREE", r.Detail);
        // ...and it must point at the counter that DOES report the seat alone,
        // otherwise "it's a subtree total" is a caveat with no next step.
        Assert.Contains("dispatch_end.iterations", r.Detail);
    }

    // ---- section 3: completed runs still behave (the controls) ----
    //
    // Without these, "return Fail(...) always" would satisfy every test above.

    [Fact]
    public void A_COMPLETED_run_under_both_caps_PASSES()
    {
        var r = Eval(
            new BudgetSpec { LeaderItersMax = 40, MemberItersMax = 22 },
            leaderIterations: 12,
            new Dictionary<string, int> { ["impl"] = 9 },
            countsComplete: true);

        Assert.True(r.Pass);
    }

    [Fact]
    public void A_COMPLETED_run_OVER_the_leader_cap_FAILS()
    {
        var r = Eval(new BudgetSpec { LeaderItersMax = 40 }, 41, null, countsComplete: true);

        Assert.False(r.Pass);
        Assert.Contains("leader ran 41 iters (cap 40)", r.Detail);
    }

    [Fact]
    public void A_COMPLETED_run_EXACTLY_AT_the_cap_PASSES()
    {
        // The cap is a MAX, not an exclusive bound. Pinned because F11's fix
        // touches this comparison's neighbourhood and an off-by-one here would
        // read as a model finding.
        var r = Eval(new BudgetSpec { LeaderItersMax = 40 }, 40, null, countsComplete: true);

        Assert.True(r.Pass);
    }

    [Fact]
    public void A_run_declaring_NO_budget_at_all_is_unaffected_even_when_ABORTED()
    {
        // countsComplete must not fail assertions that never asked about counts.
        var r = Eval(new BudgetSpec(), 999, null, countsComplete: false);

        Assert.True(r.Pass);
    }

    // ---- section 4: an EMPTY member set is a measurement, but must say so ----

    [Fact]
    public void A_COMPLETED_run_with_NO_MEMBERS_passes_but_CARRIES_THE_EMPTY_POPULATION()
    {
        // Final counters + no members = a real measured zero, so PASS is
        // correct. But "budget: member<=22 ✅" alone reads as "the members
        // stayed under 22" when there were none. An empty set is not a
        // falsified set; the row has to carry |X|.
        var r = Eval(new BudgetSpec { MemberItersMax = 22 }, 8, null, countsComplete: true);

        Assert.True(r.Pass);
        Assert.Contains("|members| = 0", r.Description);
    }

    [Fact]
    public void A_COMPLETED_run_WITH_members_does_NOT_carry_the_empty_marker()
    {
        // The control for the row above: the disclosure must be specific to
        // the empty case, or it is noise on every run.
        var r = Eval(
            new BudgetSpec { MemberItersMax = 22 },
            leaderIterations: 8,
            new Dictionary<string, int> { ["impl"] = 4 },
            countsComplete: true);

        Assert.True(r.Pass);
        Assert.DoesNotContain("|members| = 0", r.Description);
    }
}
