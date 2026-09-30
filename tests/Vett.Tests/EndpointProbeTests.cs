using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// ENDPOINT PROBE — SHARING GATES.
///
/// `vett validate --check-endpoints` exists to answer a question the schema
/// validator cannot: can this profile's seats actually reach a server that
/// serves the model they name? That answer is only worth having if the
/// instrument is honest, and its first version was not.
///
/// THE DEFECT THESE PIN. The connection and the catalogue cache were locals
/// inside the probe call, and the caller probes ONE PROFILE AT A TIME. So both
/// were rebuilt per profile: deduplication only ever worked within a single
/// profile, and a 32-profile store meant 32 clients and 32 downloads of the
/// same catalogue. The symptom was not slowness but a LYING INSTRUMENT — one
/// sweep reported openrouter.ai unreachable for three profiles and live for
/// six others in the same run, all nine naming the byte-identical
/// (endpoint, credential) pair, each racing its own cold ~688KB fetch against
/// an 8s deadline. A direct curl answered HTTP 200 in 0.5s.
///
/// ⭐ A LIVENESS PROBE WHOSE FALSE ANSWER IS "DEAD" IS WORSE THAN NO PROBE,
/// because it condemns working config. So the property under test is not
/// "the probe is fast" but "two seats naming the same pair CANNOT disagree" —
/// which is exactly what a shared cache buys and what a per-profile cache
/// silently gave up.
///
/// ⚠ These are hermetic. Every target points at a closed port on the loopback
/// interface, so the connection is REFUSED immediately — no network, no
/// waiting out a timeout, and no dependency on any host being up. A refusal is
/// a perfectly good probe result for these purposes: failures are cached
/// exactly like successes, so the sharing is observable either way.
/// </summary>
public class EndpointProbeTests
{
    // Port 1 is reserved and never listening, so connect() is refused rather
    // than hanging. That keeps the assertion about CACHING rather than about
    // how long a timeout takes to elapse.
    private const string Dead = "http://127.0.0.1:1/v1";
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(2);

    private static EndpointTarget Target(string path, string model, string endpoint = Dead) =>
        new(path, endpoint, model, ApiKeyEnv: "", ThresholdTokens: 0);

    /// <summary>
    /// THE REGRESSION GATE. One session probed across SEPARATE calls — the way
    /// the CLI actually drives it, one call per profile — must fetch a given
    /// (endpoint, credential) exactly once. Before the fix this counted one
    /// fetch per call, which is precisely how a 32-profile sweep produced 32
    /// downloads and a contradictory verdict.
    /// </summary>
    [Fact]
    public async Task Session_shared_across_calls_fetches_each_endpoint_once()
    {
        using var session = new EndpointProbe.ProbeSession();

        // Five separate ProbeAsync calls, standing in for five profiles.
        for (int i = 0; i < 5; i++)
            await EndpointProbe.ProbeAsync(session, [Target($"p{i}", "m")], Fast);

        Assert.Equal(1, session.Fetches);
    }

    /// <summary>
    /// THE OTHER SIDE OF THAT GATE — without this, the assertion above proves
    /// nothing. `Assert.Equal(1, Fetches)` is only meaningful if the broken
    /// arrangement would have produced something else, so this reconstructs
    /// the defect exactly: a session per call, which is what a local inside
    /// ProbeAsync amounted to. Five calls, five fetches of one endpoint.
    ///
    /// Read together, the two tests say the counter can tell the fixed world
    /// from the broken one. Read alone, either could be measuring the harness.
    /// </summary>
    [Fact]
    public async Task A_session_per_call_does_not_share_which_is_the_defect()
    {
        var fetches = 0;

        for (int i = 0; i < 5; i++)
        {
            using var perCallSession = new EndpointProbe.ProbeSession();
            await EndpointProbe.ProbeAsync(perCallSession, [Target($"p{i}", "m")], Fast);
            fetches += perCallSession.Fetches;
        }

        // 5, not 1 — the same five probes the shared-session test resolves in
        // one fetch. This is the count a 32-profile sweep was really paying.
        Assert.Equal(5, fetches);
    }

    /// <summary>
    /// The property the count is a proxy for, asserted directly: seats naming
    /// the identical pair must not disagree. If this ever fails, the cache is
    /// not shared and no verdict from the sweep can be trusted.
    /// </summary>
    [Fact]
    public async Task Seats_naming_the_same_endpoint_cannot_disagree()
    {
        using var session = new EndpointProbe.ProbeSession();

        var a = await EndpointProbe.ProbeAsync(session, [Target("first", "m")], Fast);
        var b = await EndpointProbe.ProbeAsync(session, [Target("second", "m")], Fast);

        Assert.Equal(a[0].Level, b[0].Level);
        Assert.Equal(a[0].Message, b[0].Message);
    }

    /// <summary>
    /// Distinct hosts must still be probed separately — the fix must not have
    /// over-collapsed the key into "one fetch, ever". This is the other side of
    /// the gate above: shared, but not indiscriminately shared.
    /// </summary>
    [Fact]
    public async Task Distinct_endpoints_are_each_fetched()
    {
        using var session = new EndpointProbe.ProbeSession();

        await EndpointProbe.ProbeAsync(session, [
            Target("a", "m", "http://127.0.0.1:1/v1"),
            Target("b", "m", "http://127.0.0.1:2/v1"),
            Target("c", "m", "http://127.0.0.1:3/v1"),
        ], Fast);

        Assert.Equal(3, session.Fetches);
    }

    /// <summary>
    /// A trailing slash is a spelling difference, not a different host. Without
    /// normalisation the same server splits into two cache entries and can
    /// return two verdicts — the same disagreement, arriving by a different
    /// route.
    /// </summary>
    [Fact]
    public async Task Trailing_slash_does_not_split_the_cache()
    {
        using var session = new EndpointProbe.ProbeSession();

        await EndpointProbe.ProbeAsync(session, [
            Target("a", "m", "http://127.0.0.1:1/v1"),
            Target("b", "m", "http://127.0.0.1:1/v1/"),
        ], Fast);

        Assert.Equal(1, session.Fetches);
    }

    /// <summary>
    /// A missing credential must be reported as a CONFIG fault naming the
    /// variable, never as an unreachable host — those call for opposite
    /// repairs, and mislabelling one as the other sends the reader to the
    /// wrong place. Note this resolves without any network at all.
    /// </summary>
    [Fact]
    public async Task Unset_api_key_env_is_reported_as_config_not_as_a_dead_host()
    {
        using var session = new EndpointProbe.ProbeSession();

        var v = await EndpointProbe.ProbeAsync(session,
            [new EndpointTarget("llm", Dead, "m", "VETT_TEST_KEY_THAT_IS_NOT_SET", 0)], Fast);

        Assert.Equal(ProbeLevel.Error, v[0].Level);
        Assert.Contains("VETT_TEST_KEY_THAT_IS_NOT_SET", v[0].Message);
        Assert.Contains("not set", v[0].Message);
    }

    /// <summary>
    /// A member with no `llm:` block inherits the profile's at run time, so the
    /// probe must resolve it the same way. An unresolved view would report the
    /// seat as "no endpoint" when it demonstrably has one — a false clean, and
    /// the reason TargetsOf does inheritance rather than reading the raw YAML.
    /// </summary>
    [Fact]
    public void TargetsOf_resolves_member_inheritance_from_the_profile()
    {
        var profile = new Profile
        {
            Llm = new LlmConfig { Endpoint = Dead, Model = "inherited-model" },
            Team = new TeamConfig
            {
                Members =
                [
                    new MemberConfig { Name = "inheritor" },
                    new MemberConfig
                    {
                        Name = "override",
                        Llm = new LlmConfig { Endpoint = Dead, Model = "own-model" },
                    },
                ],
            },
        };

        var targets = EndpointProbe.TargetsOf(profile);

        // ⚠ THIS COMMENT USED TO READ "Leader seat plus both members" AND SAID
        // 3. It was wrong on both counts, and wrong in the direction that hides
        // a hole: the third target was the PROFILE-LEVEL "llm" seat, and the
        // leader was not collected at ALL (2026-08-26 —
        // `grep -n "Leader" EndpointProbe.cs` returned nothing). A test comment
        // that names a seat the code never visits is how a fail-open gate keeps
        // looking covered. Now genuinely 4: profile "llm", the leader, and the
        // two members.
        Assert.Equal(4, targets.Count);
        Assert.Single(targets, t => t.Path.Contains(".leader[", StringComparison.Ordinal));
        Assert.Equal("inherited-model", Assert.Single(targets, t => t.Path.Contains("inheritor")).Model);
        Assert.Equal("own-model", Assert.Single(targets, t => t.Path.Contains("override")).Model);

        // The seat must be nameable in a failure message, or a red line in a
        // six-seat profile does not say which seat is broken.
        Assert.All(targets, t => Assert.False(string.IsNullOrWhiteSpace(t.Path)));
    }

    /// <summary>
    /// ⛔ THE CASE EVERY REAL PROFILE ACTUALLY USES, and the one the test above
    /// does not construct: a member whose <c>llm:</c> block exists but is
    /// PARTIAL — it sets <c>temperature</c> and nothing else.
    ///
    /// The two cases above are the extremes (no block at all / a complete
    /// block), and the old <c>llm ?? inherited</c> rule happens to get both
    /// right. It gets the middle wrong: a non-null-but-empty block wins
    /// outright, the seat resolves to a blank endpoint, and Collect drops it
    /// silently. The runtime disagrees — Coordinator resolves members through
    /// <see cref="Vett.Llm.ChatClientFactory.Merge"/>, which is FIELD-LEVEL:
    /// temperature overrides, endpoint and model inherit.
    ///
    /// So --check-endpoints was skipping member seats in nearly every team
    /// profile in the store and still printing "(endpoints live)". Measured
    /// 2026-08-26: ds-team-flash, ds-team-pro, ds-team-lead-pro,
    /// ds-team-flash-escalate and every coding-team*/dsv4-team* profile give
    /// their members an `llm:` block containing only `temperature: 0.3`.
    ///
    /// A gate that does not span its population passes vacuously. This is that
    /// gate, and this is the seat it was not asking about.
    /// </summary>
    [Fact]
    public void TargetsOf_resolves_a_PARTIAL_member_llm_block_the_way_the_runtime_does()
    {
        var profile = new Profile
        {
            Llm = new LlmConfig { Endpoint = Dead, Model = "inherited-model" },
            Team = new TeamConfig
            {
                Members =
                [
                    // Exactly what ds-team-flash writes for all three members.
                    new MemberConfig
                    {
                        Name = "temperature-only",
                        Llm = new LlmConfig { Temperature = 0.3 },
                    },
                ],
            },
        };

        var targets = EndpointProbe.TargetsOf(profile);

        // Profile "llm" + the leader + the member = 3. Under the old
        // null-coalesce rule this was 1: the member vanished, and nothing on
        // screen said a seat had gone unprobed. (The previous "Leader + the
        // member. 2" mislabelled the profile-level target as the leader — the
        // leader seat was not collected until 2026-08-26.)
        Assert.Equal(3, targets.Count);

        var member = Assert.Single(targets, t => t.Path.Contains("temperature-only", StringComparison.Ordinal));
        Assert.Equal(Dead, member.Endpoint);
        Assert.Equal("inherited-model", member.Model);
    }

    /// <summary>
    /// The same partial-block rule, asserted against the real merge helper so
    /// the probe and the runtime cannot drift apart silently. If someone
    /// changes Merge's inheritance, this fails rather than the probe quietly
    /// going back to measuring something else.
    /// </summary>
    [Fact]
    public void The_probe_and_the_runtime_agree_on_what_a_partial_member_inherits()
    {
        var baseLlm = new LlmConfig { Endpoint = Dead, Model = "inherited-model" };
        var partial = new LlmConfig { Temperature = 0.3 };

        var runtime = Vett.Llm.ChatClientFactory.Merge(baseLlm, partial);

        var profile = new Profile
        {
            Llm = baseLlm,
            Team = new TeamConfig
            {
                Members = [new MemberConfig { Name = "partial-seat", Llm = partial }],
            },
        };
        // Anchored on the MEMBER path, not a substring. A `Contains("m")`
        // predicate here matched the LEADER's path — "llm" contains an "m" —
        // so this assertion passed against the broken code by reading the
        // wrong seat. Caught 2026-08-26 when the sibling test went red and
        // this one did not.
        var probed = Assert.Single(
            EndpointProbe.TargetsOf(profile),
            t => t.Path.StartsWith("team.members", StringComparison.Ordinal));

        Assert.Equal(runtime.Endpoint, probed.Endpoint);
        Assert.Equal(runtime.Model, probed.Model);
    }

    /// <summary>
    /// A profile that declares no endpoint yields NO targets. The caller relies
    /// on this to tell "probed and fine" from "nothing was asked" — collapsing
    /// those two is the false-green this whole feature exists to remove.
    /// </summary>
    [Fact]
    public void TargetsOf_yields_nothing_when_no_endpoint_is_declared()
    {
        var profile = new Profile { Llm = new LlmConfig { Endpoint = "", Model = "m" } };

        Assert.Empty(EndpointProbe.TargetsOf(profile));
    }
}
