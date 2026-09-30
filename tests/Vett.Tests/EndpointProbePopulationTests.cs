using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// ⛔ THE POPULATION OF `vett validate`, NOT ITS VERDICT.
///
/// These tests do not ask whether the probe reports the RIGHT thing about a
/// seat. They ask whether it LOOKS AT the seat at all. That distinction is the
/// whole point: until 2026-08-26 <see cref="EndpointProbe"/> walked
/// <c>profile.Llm</c> plus <c>profile.Team.Members</c> and nothing else, so
/// <c>profile.Team.Leader</c> and every nested <c>member.Team</c> seat were
/// invisible — and it still printed a green tick.
///
/// ⚠ WHY THE OLD BEHAVIOUR LOOKED FINE. Every profile shipping today gives its
/// leader a block containing only temperature/top_p, so the leader's effective
/// config is field-identical to <c>profile.Llm</c> — which IS collected. The
/// leader was covered by COINCIDENCE. A gate that passes because of an accident
/// of the current data is a gate that fails OPEN as soon as the data moves, and
/// it moves the instant a leader gets its own endpoint.
///
/// Every count assertion below is TWO-SIDED — it compares against the same
/// profile with the seat removed. A test that merely asserted "the leader is
/// present" would pass just as happily on an implementation that returned every
/// seat twice.
/// </summary>
public class EndpointProbePopulationTests
{
    private const string TopHost = "http://top.invalid:8000/v1";
    private const string LeadHost = "http://lead.invalid:8000/v1";
    private const string NestHost = "http://nested.invalid:8000/v1";

    private static Profile WithLeader(LlmConfig? leaderLlm) => new()
    {
        Llm = new LlmConfig { Endpoint = TopHost, Model = "top-model" },
        Team = new TeamConfig
        {
            Leader = new MemberConfig { Name = "lead", Llm = leaderLlm },
            Members = [new MemberConfig { Name = "impl", Llm = new LlmConfig { Temperature = 0.3 } }],
        },
    };

    /// <summary>
    /// The leader seat is collected, and its OWN endpoint is what gets
    /// reported — not the profile's.
    /// </summary>
    [Fact]
    public void A_leader_with_its_own_endpoint_is_probed_at_that_endpoint()
    {
        var plain = EndpointProbe.TargetsOf(WithLeader(new LlmConfig { Temperature = 0.3 }));
        var repointed = EndpointProbe.TargetsOf(
            WithLeader(new LlmConfig { Endpoint = LeadHost, Model = "lead-model" }));

        // Two-sided: same seat count either way (the leader always exists), but
        // the DISTINCT endpoint set must grow by exactly the leader's host.
        Assert.Equal(plain.Count, repointed.Count);

        var leader = Assert.Single(repointed, t => t.Path.Contains(".leader[", StringComparison.Ordinal));
        Assert.Equal(LeadHost, leader.Endpoint);
        Assert.Equal("lead-model", leader.Model);

        // ...and with no override it inherits, which is what makes the old
        // "covered by coincidence" claim true rather than merely asserted.
        var inherited = Assert.Single(plain, t => t.Path.Contains(".leader[", StringComparison.Ordinal));
        Assert.Equal(TopHost, inherited.Endpoint);

        Assert.Contains(LeadHost, repointed.Select(t => t.Endpoint));
        Assert.DoesNotContain(LeadHost, plain.Select(t => t.Endpoint));
    }

    /// <summary>
    /// ⛔ THE ASSERTION THAT CATCHES MIRRORING THE WRONG INHERITANCE RULE.
    ///
    /// A nested seat inherits from its PARENT SEAT, not from the top-level
    /// profile — <c>Coordinator.cs:95-108</c> resolves a sub-team member as
    /// <c>Merge(parentProfile.Llm, m.Llm)</c> where the parent profile has
    /// already been rebuilt around the feature-lead. An implementation that
    /// recursed but passed the ORIGINAL <c>profile.Llm</c> down would still
    /// make the seat APPEAR — it would just report the wrong host for it, which
    /// is worse than not reporting it, because it looks like coverage.
    /// </summary>
    [Fact]
    public void A_nested_subteam_seat_inherits_from_its_feature_lead_not_the_profile()
    {
        var profile = new Profile
        {
            Llm = new LlmConfig { Endpoint = TopHost, Model = "top-model" },
            Team = new TeamConfig
            {
                Leader = new MemberConfig { Name = "lead" },
                Members =
                [
                    new MemberConfig
                    {
                        Name = "feature-lead",
                        // The feature-lead is repointed; its sub-team must follow IT.
                        Llm = new LlmConfig { Endpoint = NestHost, Model = "nested-model" },
                        Team = new TeamConfig
                        {
                            Leader = new MemberConfig { Name = "sub-lead" },
                            Members =
                            [
                                new MemberConfig
                                {
                                    Name = "sub-impl",
                                    Llm = new LlmConfig { Temperature = 0.1 },
                                },
                            ],
                        },
                    },
                ],
            },
        };

        var targets = EndpointProbe.TargetsOf(profile);

        var sub = Assert.Single(targets, t => t.Path.Contains("sub-impl", StringComparison.Ordinal));
        Assert.Equal(NestHost, sub.Endpoint);      // <-- the feature-lead's host
        Assert.NotEqual(TopHost, sub.Endpoint);    // <-- explicitly NOT the profile's
        Assert.Equal("nested-model", sub.Model);

        var subLead = Assert.Single(targets, t => t.Path.Contains("sub-lead", StringComparison.Ordinal));
        Assert.Equal(NestHost, subLead.Endpoint);

        // Two-sided: flattening the sub-team must lose exactly those two seats.
        var flattened = new Profile
        {
            Llm = profile.Llm,
            Team = new TeamConfig
            {
                Leader = new MemberConfig { Name = "lead" },
                Members = [new MemberConfig
                {
                    Name = "feature-lead",
                    Llm = new LlmConfig { Endpoint = NestHost, Model = "nested-model" },
                }],
            },
        };
        Assert.Equal(targets.Count - 2, EndpointProbe.TargetsOf(flattened).Count);
    }

    /// <summary>
    /// The same hole in <see cref="EndpointProbe.IncompleteSeatsOf"/>, which is
    /// what a PLAIN <c>vett validate</c> reads (no network). A leader that
    /// cannot start a session must be named.
    /// </summary>
    [Fact]
    public void An_unstartable_leader_is_reported_by_plain_validate()
    {
        var profile = new Profile
        {
            // No endpoint anywhere: provider `local` needs one.
            Llm = new LlmConfig { Provider = "local", Model = "m" },
            Team = new TeamConfig
            {
                Leader = new MemberConfig { Name = "lead" },
                Members = [new MemberConfig { Name = "impl" }],
            },
        };

        var gaps = EndpointProbe.IncompleteSeatsOf(profile);
        var leaderGap = Assert.Single(gaps, g => g.Path.Contains(".leader[", StringComparison.Ordinal));
        Assert.Contains("endpoint", leaderGap.Missing, StringComparison.Ordinal);

        // Two-sided: giving the profile an endpoint clears the leader gap, so
        // this test cannot pass by reporting every seat unconditionally.
        var fixedProfile = new Profile
        {
            Llm = new LlmConfig { Provider = "local", Endpoint = TopHost, Model = "m" },
            Team = profile.Team,
        };
        Assert.DoesNotContain(
            EndpointProbe.IncompleteSeatsOf(fixedProfile),
            g => g.Path.Contains(".leader[", StringComparison.Ordinal));
    }

    /// <summary>
    /// Over the SHIPPED profiles: the probe must now see at least as many seats
    /// as the old walk did, and strictly more wherever a team exists — because
    /// every team has a leader the old walk skipped.
    ///
    /// The ledger is printed on failure so this cannot pass VACUOUSLY on an
    /// empty directory: it asserts a non-zero team-profile count first.
    /// </summary>
    [Fact]
    public void Every_shipped_team_profile_gains_its_leader_seat()
    {
        var dir = ProfilesDir();
        int teamProfiles = 0, gained = 0;
        var ledger = new List<string>();
        var unparseable = new List<string>();

        foreach (var file in Directory.GetFiles(dir, "*.yaml"))
        {
            Profile p;
            try { p = Yaml.LoadProfile(file); }
            catch (Exception ex) { unparseable.Add($"{Path.GetFileName(file)}: {ex.GetType().Name}"); continue; }
            if (p.Team is null || p.Llm is null) continue;

            teamProfiles++;
            var targets = EndpointProbe.TargetsOf(p);

            // What the OLD walk would have produced: profile.Llm + members only.
            var oldCount = 1 + (p.Team.Members?.Count ?? 0);
            var name = Path.GetFileName(file);
            ledger.Add($"{name}: old={oldCount} new={targets.Count}");

            Assert.True(targets.Count >= oldCount,
                $"{name}: the probe LOST seats ({oldCount} -> {targets.Count})\n" +
                string.Join("\n", ledger));

            if (targets.Count > oldCount) gained++;

            // ⛔ ONE LEADER PER PROFILE WAS AN ARITY ASSUMPTION, NOT THE INVARIANT.
            // Assert.Single held only while every shipped profile had exactly one
            // `team:` block. A member carrying `member.team:` brings its OWN leader
            // (first such profile: ds-manager-flash-cloud, 2026-08-26), so a
            // three-level tree has three leader seats and Single fails on a
            // CORRECT probe.
            //
            // Relaxing to ">= 1" would restore the green and quietly drop the
            // claim: a probe that visited the top leader and skipped both nested
            // ones would pass. So count the leaders the profile ACTUALLY declares
            // and demand exactly that many. For a flat profile this reduces to the
            // original Single; for a nested one it is strictly stronger.
            var expectedLeaders = CountLeaders(p.Team);
            var leaderSeats = targets.Count(t => t.Path.Contains(".leader[", StringComparison.Ordinal));
            Assert.True(leaderSeats == expectedLeaders,
                $"{name}: profile declares {expectedLeaders} leader(s) across its team tree but the "
              + $"probe produced {leaderSeats} leader seat(s) — a leader that is never probed is a "
              + "seat whose endpoint can be dead while `vett validate` reports green.\n"
              + string.Join("\n", targets.Select(t => "  " + t.Path)));
        }

        Assert.True(teamProfiles > 0,
            $"no team profiles found under {dir} — this guard would have passed vacuously. " +
            $"unparseable: {string.Join(", ", unparseable)}");

        // Every team profile has exactly one leader the old walk never visited.
        Assert.True(gained == teamProfiles,
            $"expected all {teamProfiles} team profiles to gain a leader seat, got {gained}\n" +
            string.Join("\n", ledger));
    }

    /// <summary>
    /// How many `.leader[...]` seats the profile DECLARES — derived from the
    /// YAML shape, not from the probe's output.
    ///
    /// ⛔ WHY IT MUST NOT CALL THE PROBE. An expectation computed by running
    /// the thing under test is not an expectation; it is a tautology that
    /// passes on every implementation, including one that emits no leaders at
    /// all. This walks <see cref="TeamConfig"/> directly and independently.
    ///
    /// The rule mirrors <c>EndpointProbe.WalkTeam</c> (EndpointProbe.cs:136-137):
    /// it visits the leader ONCE PER TeamConfig NODE, unconditionally —
    /// <c>TeamConfig.Leader</c> is non-nullable (<c>= new()</c>, Profile.cs:348),
    /// so a team whose YAML omits the `leader:` block still has one seat that
    /// inherits wholesale. Hence: one per node, no emptiness test. If that
    /// runtime rule ever changes, this helper is wrong and the gate will say so
    /// loudly rather than quietly agreeing.
    /// </summary>
    private static int CountLeaders(TeamConfig? team)
    {
        if (team is null) return 0;
        var n = 1;                                   // this node's own leader
        foreach (var m in team.Members ?? [])
            n += CountLeaders(m.Team);               // + one per nested sub-team
        return n;
    }

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
