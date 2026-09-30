using Vett.Agent;
using Vett.Cli;

namespace Vett.Tests;

/// <summary>
/// THE TOPOLOGY CONTRACT: how deep a team may nest.
///
/// This exists because the constant it guards had NO test coverage at all
/// while being referenced — by name, in prose — from four other files. It
/// could have been changed to 0 (breaking every nested team) or to 20
/// (defeating an explicit product decision) and the entire suite would have
/// stayed green.
///
/// ⛔ THE CONSTANT IS NOT IN THE UNITS ANYONE THINKS IN, which is the whole
/// reason it needs pinning in a test that spells the conversion out.
/// MaxDispatchDepth counts NESTED TEAM LEVELS with the top-level team at
/// ZERO, and a team contributes TWO agents to the chain (a leader and its
/// members). So the agent chain is always the constant PLUS TWO:
///
///     MaxDispatchDepth = 0  ->  leader, member                    (2 deep)
///     MaxDispatchDepth = 1  ->  manager, lead, worker             (3 deep)
///     MaxDispatchDepth = 2  ->  manager, lead, worker, sub-worker (4 deep)
///
/// Mark set the ceiling in AGENT units on 2026-08-26: "I dont really want
/// Manger, Team lead, worker... to go DEEPR though. so 3 still maybe... a
/// 4th. worker and sub worker task if really needed ... but that would max
/// out." Four agent levels is the cap; the constant is 2 because of the
/// +2 conversion, not because anyone asked for "2".
///
/// These are deliberately CHANGE DETECTORS. Moving the ceiling is a product
/// decision, not a refactor, and it should require deleting an assertion that
/// says so out loud.
/// </summary>
public class DispatchDepthCapTests
{
    /// <summary>
    /// The ceiling, stated in the units it was decided in.
    /// </summary>
    [Fact]
    public void Agent_chain_is_capped_at_four_levels()
    {
        const int maxAgentLevels = TeamCoordinator.MaxDispatchDepth + 2;

        Assert.Equal(4, maxAgentLevels);

        // Stated the other way too, so a reader who reaches for the constant
        // directly still sees the intended value rather than deriving it.
        Assert.Equal(2, TeamCoordinator.MaxDispatchDepth);
    }

    /// <summary>
    /// ⭐ THE CAP IS A CLAIM ABOUT WHAT GETS REFUSED — SO TEST THE REFUSAL.
    ///
    /// Everything else in this file is a change detector on the NUMBER. Until
    /// 2026-08-26 that was all there was: the constant was pinned to 2, and
    /// nothing anywhere exercised the guard that enforces it. Delete the `if`
    /// at either enforcement site, leave the constant alone, and the whole
    /// suite stayed green while teams nested without limit — which is the one
    /// failure Mark actually asked to be protected from.
    ///
    /// A CAP IS NOT THE CONSTANT, IT IS THE THROW.
    /// </summary>
    [Fact]
    public void A_dispatch_ONE_LEVEL_PAST_the_cap_is_REFUSED()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => TeamCoordinator.EnforceDispatchDepth(TeamCoordinator.MaxDispatchDepth + 1));

        // The operator reading this needs the ceiling in AGENT units — the
        // constant is in nesting levels, which is not what anyone thinks in.
        Assert.Contains("at most 4 long", ex.Message);
        Assert.Contains("manager, lead, worker, sub-worker", ex.Message);
    }

    /// <summary>
    /// The other side of the guard. Without this, a guard that threw on
    /// EVERYTHING would satisfy the test above — and would break every nested
    /// team while looking correctly strict.
    /// </summary>
    [Fact]
    public void A_dispatch_EXACTLY_AT_the_cap_is_ALLOWED()
    {
        // The cap is a MAX, not an exclusive bound: depth == 2 is the
        // sub-worker level Mark explicitly kept ("a 4th ... but that would
        // max out"), so it must run, not throw.
        TeamCoordinator.EnforceDispatchDepth(TeamCoordinator.MaxDispatchDepth);

        // ...and so must every level below it, including the flat team.
        TeamCoordinator.EnforceDispatchDepth(0);
        TeamCoordinator.EnforceDispatchDepth(1);
    }

    /// <summary>
    /// ⭐ THE COUNTER MUST ACTUALLY COUNT.
    ///
    /// The two tests above prove the guard refuses a number. They say nothing
    /// about whether the number ever grows. Replace the increment with a
    /// constant — `var depth = 1;` — and nesting becomes unbounded while every
    /// other test in this file, and all ~1,159 in the suite, stay green. The
    /// guard and the counter are two claims and need two tests.
    ///
    /// This walks the real production bookkeeping: the depth a dispatch would
    /// run at, entered as a scope, exactly as RunNestedTeamAsync does it.
    /// </summary>
    [Fact]
    public void Depth_ACCUMULATES_across_nested_dispatches_and_the_third_is_refused()
    {
        // Top level: nothing has been entered, so the first dispatch is depth 1.
        Assert.Equal(1, TeamCoordinator.NextDispatchDepth());

        using (TeamCoordinator.EnterDispatchDepth(TeamCoordinator.NextDispatchDepth()))
        {
            // manager -> lead. A lead's own dispatch is the worker, depth 2.
            Assert.Equal(2, TeamCoordinator.NextDispatchDepth());

            using (TeamCoordinator.EnterDispatchDepth(TeamCoordinator.NextDispatchDepth()))
            {
                // worker -> sub-worker is depth 3, the fifth agent level, and
                // is exactly what Mark's ceiling forbids.
                var wouldBe = TeamCoordinator.NextDispatchDepth();
                Assert.Equal(3, wouldBe);
                Assert.Throws<InvalidOperationException>(
                    () => TeamCoordinator.EnforceDispatchDepth(wouldBe));
            }

            // ...and unwinding one level makes room again.
            Assert.Equal(2, TeamCoordinator.NextDispatchDepth());
        }

        Assert.Equal(1, TeamCoordinator.NextDispatchDepth());
    }

    /// <summary>
    /// The scope must restore the depth it found, not merely decrement. Both
    /// forms agree while NextDispatchDepth is the only producer; only this one
    /// stays correct if that ever stops being true.
    /// </summary>
    [Fact]
    public void Leaving_a_dispatch_scope_restores_the_depth_it_FOUND()
    {
        var before = TeamCoordinator.NextDispatchDepth();

        var scope = TeamCoordinator.EnterDispatchDepth(7);
        Assert.Equal(8, TeamCoordinator.NextDispatchDepth());
        scope.Dispose();

        Assert.Equal(before, TeamCoordinator.NextDispatchDepth());

        // Disposing twice must not double-unwind — dispatch teardown paths can
        // dispose defensively, and a second decrement would hand a later
        // sibling MORE headroom than the cap allows.
        scope.Dispose();
        Assert.Equal(before, TeamCoordinator.NextDispatchDepth());
    }

    /// <summary>
    /// ⛔ THE CONCURRENT CASE IS THE ONE THAT ACTUALLY SHIPS. `assign_async`
    /// fires sibling dispatches through Task.Run, and a manager at width 20 has
    /// twenty of them in flight. If depth lived in a plain static, one sibling
    /// entering depth 2 would raise the ceiling for every other sibling — and
    /// the leak would be invisible under any single-threaded test.
    ///
    /// Measured 2026-08-26 across the four manager-width arms: the widest single
    /// lead fanned out to **32** sub-workers (Pro w20; 25 in Pro w10), so this is
    /// not a hypothetical width. The 25 below is this test's own fan-out, chosen
    /// before that maximum was known — it is a lower bound on the observed
    /// width, not a claim that 25 is the maximum.
    /// </summary>
    [Fact]
    public async Task Sibling_dispatches_do_NOT_see_each_others_depth()
    {
        var top = TeamCoordinator.NextDispatchDepth();

        var siblings = Enumerable.Range(0, 25).Select(_ => Task.Run(() =>
        {
            // Each sibling descends to the cap independently.
            using var a = TeamCoordinator.EnterDispatchDepth(TeamCoordinator.NextDispatchDepth());
            using var b = TeamCoordinator.EnterDispatchDepth(TeamCoordinator.NextDispatchDepth());
            return TeamCoordinator.NextDispatchDepth();
        })).ToArray();

        var observed = await Task.WhenAll(siblings);

        // Every sibling must independently arrive at the SAME would-be depth.
        // A leak shows up as some sibling reporting 4, 5, 6... instead of 3.
        Assert.All(observed, d => Assert.Equal(3, d));

        // ...and none of it escaped back into this context.
        Assert.Equal(top, TeamCoordinator.NextDispatchDepth());
    }

    /// <summary>
    /// The floor. A cap of 0 still permits a flat team (leader + members) but
    /// forbids ALL nesting — which would silently disable tier-3 topologies
    /// rather than failing loudly. Nesting must remain possible.
    /// </summary>
    [Fact]
    public void Nesting_remains_possible_at_all()
    {
        Assert.True(TeamCoordinator.MaxDispatchDepth >= 1,
            "a cap below 1 disables nested teams entirely — tier-3 profiles would "
          + "fail at dispatch time instead of being rejected at validation time");
    }

    /// <summary>
    /// ⭐ THE INVARIANT THAT WAS ONLY A COMMENT. SimpleCommands.CollectTeam
    /// bounds its walk so `vett profiles` cannot hang on a cyclic profile
    /// (YamlDotNet resolves anchors/aliases, so a hand-written `&amp;a`/`*a`
    /// can produce a cyclic graph — and listing is exactly the command you
    /// run WHEN a profile is malformed).
    ///
    /// That bound must stay strictly ABOVE the dispatch cap. If it ever sat at
    /// or below, the picker would truncate a topology that actually runs, and
    /// silently under-report the endpoints of the deepest sub-team — the exact
    /// class of "summary hides a binding" bug ProfileSummaryBindingTests was
    /// written for, reintroduced from the other end.
    /// </summary>
    [Fact]
    public void Profile_walk_cap_stays_strictly_above_the_dispatch_cap()
    {
        Assert.True(SimpleCommands.ProfileWalkDepthCap > TeamCoordinator.MaxDispatchDepth,
            $"the profile summary walk (cap {SimpleCommands.ProfileWalkDepthCap}) must be able to "
          + $"reach every level a dispatch can create (cap {TeamCoordinator.MaxDispatchDepth}), or "
          + "the picker will truncate runnable topologies and hide their bindings");
    }
}
