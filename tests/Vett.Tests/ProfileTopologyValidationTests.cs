using Vett.Agent;
using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// ⭐ CAN THIS PROFILE'S TEAM SHAPE ACTUALLY RUN? — answered offline.
///
/// The dispatch cap is enforced at RUNTIME (TeamCoordinator.EnforceDispatchDepth),
/// which means an over-deep profile discovers its own rejection only after
/// paying for every token spent getting down to the level that gets refused.
/// Nesting, though, is declared STATICALLY: MemberConfig.Team is a recursive
/// type, so the depth a profile reaches is a property of the FILE.
///
/// ⛔ WHY THIS FILE EXISTS. Measured 2026-08-26, before the check was written:
/// `vett validate --profile <five-agent-levels>` printed a clean ✓ and
/// "0 error(s), 0 warning(s)" and exited 0. Every single thing validate looks
/// at — tools, schemas, prompts, ranges, unknown keys, seat completeness,
/// endpoint liveness — was genuinely fine. The topology was simply un-runnable
/// and NOTHING LOOKED. That is the same false-green class validate already
/// documents for the 31 suites, one axis over, and it is exactly why Mark said
/// not to take validate's word for it.
///
/// These tests are two-sided on purpose. A check that refused every nested
/// profile would satisfy the "too deep is rejected" half perfectly while
/// breaking the tier-3 topology Mark explicitly kept.
/// </summary>
public class ProfileTopologyValidationTests
{
    /// <summary>Builds a chain of nested sub-teams `levels` deep.</summary>
    private static TeamConfig Nested(int levels)
    {
        var team = new TeamConfig
        {
            Leader = new MemberConfig { Name = "leader-" + levels },
            Members = [new MemberConfig { Name = "member-" + levels }],
        };
        if (levels > 0) team.Members[0].Team = Nested(levels - 1);
        return team;
    }

    /// <summary>
    /// The arithmetic, in the units the cap is written in. A flat team is ZERO
    /// nesting levels — it is already two agents (leader + members), which is
    /// the +2 conversion the cap's doc comment spells out.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]   // leader, member                    — 2 agents
    [InlineData(1, 1)]   // manager, lead, worker             — 3 agents
    [InlineData(2, 2)]   // manager, lead, worker, sub-worker — 4 agents, the cap
    public void Each_nested_member_team_adds_exactly_ONE_level(int built, int expected)
    {
        Assert.Equal(expected, TeamCoordinator.DeclaredNestingDepth(Nested(built)));
    }

    /// <summary>
    /// A solo profile has no team at all. Null must read as "flat", not throw —
    /// validate runs this over every profile in the store, most of which are solo.
    /// </summary>
    [Fact]
    public void A_profile_with_NO_team_is_depth_zero()
    {
        Assert.Equal(0, TeamCoordinator.DeclaredNestingDepth(null));
    }

    /// <summary>
    /// ⭐ THE REFUSAL, AND THE PERMISSION. Both halves, because either alone is
    /// satisfied by a broken check.
    /// </summary>
    [Fact]
    public void A_topology_AT_the_cap_is_ACCEPTED_and_one_past_it_is_REFUSED()
    {
        var atCap = TeamCoordinator.DeclaredNestingDepth(Nested(TeamCoordinator.MaxDispatchDepth));
        Assert.True(atCap <= TeamCoordinator.MaxDispatchDepth,
            "the sub-worker level Mark explicitly kept must remain runnable");

        var past = TeamCoordinator.DeclaredNestingDepth(Nested(TeamCoordinator.MaxDispatchDepth + 1));
        Assert.True(past > TeamCoordinator.MaxDispatchDepth,
            "a profile one level past the cap must be caught BEFORE it spends a token");
    }

    /// <summary>
    /// ⛔ MEMBERS ONLY — AND THIS IS THE TEST THAT DEFENDS IT.
    ///
    /// The runtime nests on `m.Team` inside the MEMBERS loop; a `team:` written
    /// under a LEADER is never dispatched by anything. SimpleCommands.CollectTeam
    /// DOES walk leaders, and is correct to — over-inclusion there only
    /// over-reports which endpoints a profile references, which is harmless.
    ///
    /// Here the same over-inclusion would REFUSE a profile that runs perfectly
    /// well. Someone tidying the two walks into agreement would introduce
    /// exactly that bug, and this is the assertion that stops them.
    /// </summary>
    [Fact]
    public void A_sub_team_under_a_LEADER_does_not_count_because_nothing_dispatches_it()
    {
        var team = new TeamConfig
        {
            Leader = new MemberConfig { Name = "manager", Team = Nested(5) },
            Members = [new MemberConfig { Name = "worker" }],
        };

        Assert.Equal(0, TeamCoordinator.DeclaredNestingDepth(team));
    }

    /// <summary>
    /// ⛔ A CYCLIC PROFILE MUST TERMINATE, NOT HANG.
    ///
    /// YamlDotNet resolves anchors/aliases, so a hand-written `&amp;a`/`*a` can
    /// produce a genuinely cyclic object graph — and validate is precisely the
    /// command you run WHEN a profile is malformed. An unbounded walk would
    /// hang the validator on the one input it exists to diagnose.
    ///
    /// The bound is not a visited-set: the walk returns as soon as the cap is
    /// exceeded, and a cycle always exceeds the cap. This asserts that property
    /// directly rather than trusting the reasoning.
    /// </summary>
    [Fact]
    public async Task A_CYCLIC_team_graph_terminates_and_reports_too_deep()
    {
        var team = new TeamConfig
        {
            Leader = new MemberConfig { Name = "leader" },
            Members = [new MemberConfig { Name = "member" }],
        };
        team.Members[0].Team = team;   // the alias case, built directly

        var walk = Task.Run(() => TeamCoordinator.DeclaredNestingDepth(team));
        var finished = await Task.WhenAny(walk, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(ReferenceEquals(finished, walk),
            "the walk did not terminate on a cyclic team graph — validate would hang");
        Assert.True(await walk > TeamCoordinator.MaxDispatchDepth,
            "a cycle is infinitely deep, so it must be reported as past the cap");
    }

    /// <summary>
    /// A null/empty members list must not throw. `members:` written with
    /// nothing under it deserializes to an empty list — or, depending on the
    /// YAML, to null — and validate walks EVERY profile in the store, so one
    /// malformed file must not take down the sweep for all the others.
    /// </summary>
    [Fact]
    public void An_empty_or_null_members_list_is_walked_without_throwing()
    {
        Assert.Equal(0, TeamCoordinator.DeclaredNestingDepth(new TeamConfig()));
        Assert.Equal(0, TeamCoordinator.DeclaredNestingDepth(new TeamConfig { Members = null! }));
    }

    /// <summary>
    /// ⭐ THE POPULATION GATE. Every profile that actually ships must be
    /// runnable, not merely parseable.
    ///
    /// This is what catches the reverse defect: the cap was LOWERED from 5 to 2
    /// on 2026-08-26, and if any shipped profile had declared 3+ levels, that
    /// change would have broken it silently — validate said ✓ either way.
    ///
    /// Measured at the time of writing: 18 profiles, 5 of which declare
    /// nesting (the four manager-width profiles that were run live, plus
    /// ds-manager-flash-cloud), all at depth 1. So this gate spans real nested
    /// topologies rather than passing vacuously over a population of flat
    /// profiles — and depth 1 is independently corroborated: the four live runs
    /// measured a maximum observed dispatch depth of exactly 1.
    /// </summary>
    [Fact]
    public void EVERY_shipped_profile_declares_a_topology_that_can_actually_run()
    {
        var dir = ProfilesDir();
        var nested = 0;
        var checkedCount = 0;

        foreach (var f in Directory.GetFiles(dir, "*.yaml"))
        {
            Profile p;
            try { p = Yaml.LoadProfile(f); }
            catch { continue; }   // unparseable is a DIFFERENT check's job

            checkedCount++;
            var depth = TeamCoordinator.DeclaredNestingDepth(p.Team);
            if (depth > 0) nested++;

            Assert.True(depth <= TeamCoordinator.MaxDispatchDepth,
                $"{Path.GetFileName(f)} declares {depth} nesting level(s) but the cap is "
              + $"{TeamCoordinator.MaxDispatchDepth} — it would throw mid-run");
        }

        Assert.True(checkedCount > 0, "no profiles were read — this gate would pass vacuously");

        // ⛔ THE GATE MUST SPAN A NESTED PROFILE. If every shipped profile were
        // flat, the loop above would be green while saying nothing whatsoever
        // about nesting. Asserting the population contains the case makes the
        // coverage claim falsifiable instead of assumed.
        Assert.True(nested > 0,
            "no shipped profile declares any nesting, so the depth assertion above "
          + "passed vacuously — it proved nothing about the tier-3 topology");
    }

    /// <summary>Locates the repo's profiles/ directory. Throws rather than
    /// skipping: a gate that quietly finds nothing to check is a false green.</summary>
    private static string ProfilesDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var c = Path.Combine(d.FullName, "profiles");
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "ds-team-flash.yaml"))) return c;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("could not locate the repo's profiles/ directory");
    }
}
