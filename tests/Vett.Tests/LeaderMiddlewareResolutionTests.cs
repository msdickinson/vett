using Vett.Agent;
using Vett.Bench.Team;
using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// LEADER MIDDLEWARE RESOLUTION.
///
/// THE DEFECT THIS PINS (fixed 2026-08-26). <c>team.leader.middleware</c> is a
/// real, bound field — <see cref="MemberConfig.Middleware"/>, deserialised by the
/// same code path as every other member field — and at depth 0 it had NO READER.
/// Both leader call sites in <see cref="TeamCoordinator"/> passed
/// <c>profile.Middleware</c> literally, so a profile that gave its leader its own
/// chain silently got the profile's instead. Nothing errored and nothing logged:
/// the config parsed, validated, and was then discarded, which reads exactly like
/// config that works.
///
/// ⭐ WHY THESE TESTS ARE SHAPED AS PAIRS. A one-sided test ("the leader's own list
/// is used") would have passed against the BROKEN code in the common case where
/// the two lists happen to be equal — which is every shipped profile, since none
/// of them set team.leader.middleware. Each test below therefore uses a fixture
/// where the leader's list and the profile's list are DIFFERENT AND DISTINGUISHABLE,
/// so the assertion can only be satisfied by reading the right one.
/// </summary>
public class LeaderMiddlewareResolutionTests
{
    private const string Condenser = "llm_summarizing_condenser";
    private const string Milestone = "milestone_checkpoint";

    /// <summary>
    /// A leader that declares its own chain gets its own chain — not the
    /// profile's. The two lists are disjoint on purpose.
    /// </summary>
    [Fact]
    public void Leader_with_its_own_middleware_block_gets_that_block()
    {
        var p = Fixture(profileMiddleware: ["output_truncation", Condenser],
                        leaderMiddleware: ["stuck_detector", Milestone]);

        var resolved = TeamCoordinator.LeaderMiddleware(p.Team!, p);

        Assert.Equal(["stuck_detector", Milestone], resolved);
        Assert.DoesNotContain(Condenser, resolved);
    }

    /// <summary>
    /// The other half of the fall-through: an UNDECLARED leader list still resolves
    /// to the profile's. Every shipped profile relies on this, so a fix that broke
    /// it would be a regression far larger than the defect.
    /// </summary>
    [Fact]
    public void Leader_without_its_own_middleware_block_falls_through_to_the_profile()
    {
        var p = Fixture(profileMiddleware: ["output_truncation", Condenser],
                        leaderMiddleware: null);

        Assert.Equal(["output_truncation", Condenser], TeamCoordinator.LeaderMiddleware(p.Team!, p));
    }

    /// <summary>
    /// Fall-through REPLACES, it does not merge — matching how members and the
    /// nested-team synthesis already behave. Stated as its own test because "does
    /// it merge?" is the first question anyone reading the helper will ask, and an
    /// untested answer invites someone to "fix" it into a union later.
    ///
    /// Dropping submit_detector is safe here: MiddlewareResolver force-prepends it
    /// when unlisted, so naming a list can never lose the completion signal.
    /// </summary>
    [Fact]
    public void Leader_middleware_replaces_rather_than_merges()
    {
        var p = Fixture(profileMiddleware: ["output_truncation", "stuck_detector", Condenser],
                        leaderMiddleware: [Milestone]);

        Assert.Equal([Milestone], TeamCoordinator.LeaderMiddleware(p.Team!, p));
    }

    /// <summary>
    /// ⭐ THE ASYMMETRY THAT MADE THIS A BUG RATHER THAN A CONVENTION.
    ///
    /// RunNestedTeamAsync synthesises a sub-profile with
    /// <c>Middleware = m.Middleware.Count > 0 ? m.Middleware : parentProfile.Middleware</c>,
    /// so a leader at depth ≥ 1 ALREADY honoured its own block. The same YAML
    /// therefore behaved differently depending on how deep the leader sat in the
    /// tree — the sort of split that shows up as "it works in the sub-team but not
    /// at the top" and gets misfiled as a model problem.
    ///
    /// This test pins the two rules to the same answer. It is written against the
    /// nested rule EXPRESSED INDEPENDENTLY rather than by calling the helper twice,
    /// so it fails if either side drifts.
    /// </summary>
    [Fact]
    public void Depth_zero_and_nested_leaders_resolve_middleware_identically()
    {
        foreach (var leaderOwn in new List<string>?[] { null, [Milestone], ["stuck_detector", Milestone] })
        {
            var p = Fixture(profileMiddleware: ["output_truncation", Condenser],
                            leaderMiddleware: leaderOwn);

            var atDepthZero = TeamCoordinator.LeaderMiddleware(p.Team!, p);

            // The nested rule, restated from Coordinator.RunNestedTeamAsync. A
            // member carrying a `team:` block IS the sub-leader, so its own
            // middleware list is what the synthesised sub-profile takes.
            var asNestedMember = p.Team!.Leader;
            var atDepthOne = asNestedMember.Middleware.Count > 0
                ? asNestedMember.Middleware
                : p.Middleware;

            Assert.Equal(atDepthOne, atDepthZero);
        }
    }

    /// <summary>
    /// ⚠ THE LEDGER MUST REPORT THE LIST THE LEADER ACTUALLY RUNS.
    ///
    /// EscalationLedger declares each role's UNINSTRUMENTED middleware — the ones
    /// that call an LLM without the loop's own instrumentation seeing it, so their
    /// token spend is invisible to the counter. That declaration is only true if it
    /// reads the same list the coordinator resolves.
    ///
    /// The fixture makes the two answers observably different: the PROFILE lists
    /// llm_summarizing_condenser (uninstrumented → a declared blind spot), the
    /// LEADER lists milestone_checkpoint (instrumented → no blind spot). Reading
    /// the wrong list produces a ledger that announces a blind spot the leader does
    /// not have — a false declaration is worse than no declaration, because it is
    /// believed.
    /// </summary>
    [Fact]
    public void Escalation_ledger_declares_the_leaders_own_middleware()
    {
        var p = Fixture(profileMiddleware: ["output_truncation", Condenser],
                        leaderMiddleware: ["output_truncation", Milestone]);

        var leader = EscalationLedger
            .FromProfile(p, "http://base:8000/v1", "flash")
            .Roster.Single(r => r.IsLeader);

        Assert.Empty(leader.UninstrumentedMiddleware);
    }

    /// <summary>
    /// The same test the other way round, so the one above cannot pass just because
    /// the blind-spot list is always empty. Here the LEADER carries the
    /// uninstrumented strategy and the profile does not.
    /// </summary>
    [Fact]
    public void Escalation_ledger_reports_a_blind_spot_the_leader_actually_has()
    {
        var p = Fixture(profileMiddleware: ["output_truncation", Milestone],
                        leaderMiddleware: ["output_truncation", Condenser]);

        var leader = EscalationLedger
            .FromProfile(p, "http://base:8000/v1", "flash")
            .Roster.Single(r => r.IsLeader);

        Assert.Equal([Condenser], leader.UninstrumentedMiddleware);
    }

    /// <summary>
    /// Members are untouched by all of this — they always read their own list, and
    /// an empty one means DefaultMiddleware(), not inheritance. Pinned here because
    /// the fix above moved code that sits three lines from the member path.
    /// </summary>
    [Fact]
    public void Member_middleware_resolution_is_unchanged()
    {
        var p = Fixture(profileMiddleware: ["output_truncation", Condenser],
                        leaderMiddleware: [Milestone]);

        var declared = p.Team!.Members.Single(m => m.Name == "declares");
        var bare = p.Team!.Members.Single(m => m.Name == "bare");

        Assert.Equal([Milestone], declared.Middleware);
        Assert.Empty(bare.Middleware);

        var roster = EscalationLedger.FromProfile(p, "http://base:8000/v1", "flash").Roster;
        Assert.All(roster.Where(r => !r.IsLeader), r => Assert.Empty(r.UninstrumentedMiddleware));
    }

    /// <summary>
    /// Round-trips through Yaml.LoadProfile — the loader the runtime actually uses.
    /// Building a Profile object by hand would prove the helper works on data the
    /// deserialiser might never produce.
    /// </summary>
    private static Profile Fixture(List<string> profileMiddleware, List<string>? leaderMiddleware)
    {
        var leaderBlock = leaderMiddleware is null
            ? ""
            : "\n    middleware:\n" + string.Join("\n", leaderMiddleware.Select(m => $"      - {m}"));

        var yaml = $"""
            name: leader-middleware-fixture
            llm:
              provider: local
              endpoint: http://base:8000/v1
              model: flash
            middleware:
            {string.Join("\n", profileMiddleware.Select(m => $"  - {m}"))}
            compaction:
              threshold_tokens: 48000
            max_iterations: 50
            team:
              leader:
                name: lead{leaderBlock}
              members:
                - name: declares
                  middleware: [{Milestone}]
                - name: bare
            """;

        var path = Path.Combine(Path.GetTempPath(), "vett-leader-mw-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, yaml);
            var p = Yaml.LoadProfile(path);

            // ⛔ THE FIXTURE'S OWN LIVENESS CHECK. Every assertion in this file is
            // about which of two lists gets read; if the YAML above stopped binding
            // the leader block, all of them would compare the profile's list to
            // itself and pass while proving nothing.
            Assert.NotNull(p.Team);
            Assert.Equal(profileMiddleware, p.Middleware);
            if (leaderMiddleware is null) Assert.Empty(p.Team!.Leader.Middleware);
            else Assert.Equal(leaderMiddleware, p.Team!.Leader.Middleware);

            return p;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
