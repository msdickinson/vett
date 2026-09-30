using Vett.Agent;
using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// LEADER COMPACTION RESOLUTION — <c>team.leader.compaction</c>.
///
/// ⭐ THIS FILE PINS A RULE THAT WAS ALREADY CORRECT. Unlike its sibling
/// <see cref="LeaderMiddlewareResolutionTests"/>, there was no defect here:
/// both leader call sites already read <c>team.Leader.Compaction ??
/// profile.Compaction</c>. What was missing was any test at all, and the rule
/// existed as TWO COPIES of the same expression ~350 lines apart in
/// Coordinator.cs — the shape that lets one site get "simplified" while the
/// other keeps working. The expression is now the single seam
/// <see cref="TeamCoordinator.LeaderCompaction"/>, and these tests are its
/// falsifier.
///
/// ⛔ WHY AN UNTESTED-BUT-CORRECT RULE WAS WORTH CLOSING. This field is the
/// lever for giving a team lead a bigger context window than its members. If it
/// silently stopped being read, the symptom would be: we set 240000, the lead
/// still compacts at 48000, and NOTHING ERRORS — the block parses, validates,
/// and is discarded. No exception, no log line, and no run artifact records
/// which threshold was actually used. The conclusion a person draws from the
/// outside is "a bigger window didn't help", and a working option gets
/// abandoned for the wrong reason. There is no error string to search for, so
/// reading the resolution directly is the ONLY way to catch it.
///
/// ⭐ WHY THESE ARE PAIR-SHAPED. Every fixture below makes the leader's
/// threshold and the profile's threshold DIFFERENT AND DISTINGUISHABLE (240000
/// vs 48000, with members on a third value, 96000). A one-sided test — "the
/// leader's threshold is 240000" against a fixture where both blocks say
/// 240000 — passes against code that reads the wrong one. Every shipped profile
/// today omits <c>team.leader.compaction</c>, so the equal-values case is the
/// only case real config exercises, which is exactly why the test must not use
/// it.
/// </summary>
public class LeaderCompactionResolutionTests
{
    private const int ProfileThreshold = 48_000;
    private const int LeaderThreshold = 240_000;
    private const int MemberThreshold = 96_000;

    /// <summary>Profile keep_last_messages, set away from CompactionConfig's
    /// default of 5 so a per-field merge is observably different from a
    /// whole-block replace. See the "replaces rather than merges" test.</summary>
    private const int ProfileKeepLast = 12;

    // ---------------------------------------------------------------
    // THE RULE
    // ---------------------------------------------------------------

    /// <summary>
    /// A leader that declares its own compaction block gets that block — not
    /// the profile's. The two thresholds are far apart on purpose.
    /// </summary>
    [Fact]
    public void Leader_with_its_own_compaction_block_gets_that_block()
    {
        var p = Fixture(leaderThreshold: LeaderThreshold);

        var resolved = TeamCoordinator.LeaderCompaction(p.Team!, p);

        Assert.NotNull(resolved);
        Assert.Equal(LeaderThreshold, resolved!.ThresholdTokens);
        Assert.NotEqual(ProfileThreshold, resolved.ThresholdTokens);
    }

    /// <summary>
    /// The other half of the fall-through: an UNDECLARED leader block still
    /// resolves to the profile's. Every shipped profile relies on this, so a
    /// change that broke it would be a regression far wider than the gap this
    /// file closes.
    /// </summary>
    [Fact]
    public void Leader_without_its_own_compaction_block_falls_through_to_the_profile()
    {
        var p = Fixture(leaderThreshold: null);

        var resolved = TeamCoordinator.LeaderCompaction(p.Team!, p);

        Assert.NotNull(resolved);
        Assert.Equal(ProfileThreshold, resolved!.ThresholdTokens);
        Assert.Equal(ProfileKeepLast, resolved.KeepLastMessages);
    }

    /// <summary>
    /// ⚠ FALL-THROUGH IS WHOLE-BLOCK, NOT PER-FIELD.
    ///
    /// The leader's block below sets ONLY threshold_tokens. The profile sets
    /// threshold_tokens AND keep_last_messages: 12. A leader that declares the
    /// block therefore gets keep_last_messages = 5 — <see cref="CompactionConfig"/>'s
    /// OWN default — and NOT the profile's 12.
    ///
    /// This is stated as its own test because "does it merge?" is the first
    /// question anyone reading the seam will ask, and an untested answer invites
    /// someone to "fix" it into a field-wise merge later. It also matches how
    /// members resolve (<c>m.Compaction ?? profile.Compaction</c>), so the two
    /// cannot drift into different semantics.
    ///
    /// ⛔ The practical trap this pins: raising a lead's threshold_tokens
    /// SILENTLY RESETS every other compaction field to its default. Anyone
    /// tuning keep_last_messages at the profile level and then giving the lead
    /// a bigger window loses the tuning without a word.
    /// </summary>
    [Fact]
    public void Leader_compaction_replaces_rather_than_merges()
    {
        var p = Fixture(leaderThreshold: LeaderThreshold);

        var resolved = TeamCoordinator.LeaderCompaction(p.Team!, p)!;

        Assert.Equal(LeaderThreshold, resolved.ThresholdTokens);

        // The profile's 12 must NOT leak in; the class default must.
        Assert.Equal(new CompactionConfig().KeepLastMessages, resolved.KeepLastMessages);
        Assert.NotEqual(ProfileKeepLast, resolved.KeepLastMessages);
    }

    /// <summary>
    /// ⭐ THE ASYMMETRY THIS RULES OUT, by analogy with the middleware file.
    ///
    /// RunNestedTeamAsync synthesises a sub-profile with
    /// <c>Compaction = m.Compaction ?? parentProfile.Compaction</c>
    /// (Coordinator.cs:365), so a leader at depth ≥ 1 resolves through a
    /// DIFFERENT line of code than a leader at depth 0. Identical YAML behaving
    /// differently by nesting depth is the sort of split that surfaces as "it
    /// works in the sub-team but not at the top" and gets misfiled as a model
    /// problem.
    ///
    /// Written against the nested rule EXPRESSED INDEPENDENTLY rather than by
    /// calling the seam twice, so it fails if either side drifts.
    /// </summary>
    [Fact]
    public void Depth_zero_and_nested_leaders_resolve_compaction_identically()
    {
        foreach (var leaderOwn in new int?[] { null, LeaderThreshold })
        {
            var p = Fixture(leaderThreshold: leaderOwn);

            var atDepthZero = TeamCoordinator.LeaderCompaction(p.Team!, p);

            // The nested rule, restated from Coordinator.RunNestedTeamAsync. A
            // member carrying a `team:` block IS the sub-leader, so its own
            // compaction block is what the synthesised sub-profile takes.
            var asNestedMember = p.Team!.Leader;
            var atDepthOne = asNestedMember.Compaction ?? p.Compaction;

            Assert.Same(atDepthOne, atDepthZero);
        }
    }

    // ---------------------------------------------------------------
    // CROSS-READER AGREEMENT
    // ---------------------------------------------------------------

    /// <summary>
    /// ⚠ THE PROBE AND THE RUNTIME MUST NAME THE SAME THRESHOLD.
    ///
    /// <see cref="EndpointProbe"/> is a SECOND, INDEPENDENT reader of this exact
    /// field: WalkTeam visits the leader seat with <c>team.Leader.Compaction</c>
    /// (EndpointProbe.cs:137) and Collect applies the same fall-through
    /// (:240). It uses the answer to decide whether to warn that a compaction
    /// trigger CAN NEVER FIRE — "serves N tokens, but threshold_tokens is M"
    /// (:352-356).
    ///
    /// If the probe read the profile's block while the runtime ran the leader's,
    /// the warning would describe a seat that does not exist: silent about a
    /// leader genuinely configured above its window, and noisy about one that is
    /// fine. A validator that is confidently wrong is worse than no validator,
    /// because it is believed.
    ///
    /// This asserts the two readers agree on a fixture where reading the wrong
    /// block gives a DIFFERENT number, so agreement cannot be an accident.
    /// </summary>
    [Fact]
    public void Endpoint_probe_and_the_coordinator_agree_on_the_leaders_threshold()
    {
        var p = Fixture(leaderThreshold: LeaderThreshold);

        var fromCoordinator = TeamCoordinator.LeaderCompaction(p.Team!, p)!.ThresholdTokens;
        var fromProbe = LeaderTarget(p).ThresholdTokens;

        Assert.Equal(LeaderThreshold, fromCoordinator);
        Assert.Equal(fromCoordinator, fromProbe);
    }

    /// <summary>
    /// The same agreement on the fall-through path, so the test above cannot
    /// pass merely because both readers happen to return the leader's block
    /// whenever one exists. Here there is no leader block and BOTH must land on
    /// the profile's.
    /// </summary>
    [Fact]
    public void Endpoint_probe_and_the_coordinator_agree_when_the_leader_declares_nothing()
    {
        var p = Fixture(leaderThreshold: null);

        var fromCoordinator = TeamCoordinator.LeaderCompaction(p.Team!, p)!.ThresholdTokens;
        var fromProbe = LeaderTarget(p).ThresholdTokens;

        Assert.Equal(ProfileThreshold, fromCoordinator);
        Assert.Equal(fromCoordinator, fromProbe);
    }

    // ---------------------------------------------------------------
    // MEMBERS ARE UNTOUCHED
    // ---------------------------------------------------------------

    /// <summary>
    /// Members always read their OWN block, falling through to the profile —
    /// never to the leader's. Pinned here because the seam extraction moved
    /// code that sits within a few lines of the member path, and because a
    /// leader on 240000 whose members silently inherited it would be a much
    /// more expensive mistake than the one this file is about.
    /// </summary>
    [Fact]
    public void Member_compaction_resolution_is_unchanged()
    {
        var p = Fixture(leaderThreshold: LeaderThreshold);

        var declares = p.Team!.Members.Single(m => m.Name == "declares");
        var bare = p.Team!.Members.Single(m => m.Name == "bare");

        Assert.Equal(MemberThreshold, (declares.Compaction ?? p.Compaction)!.ThresholdTokens);
        Assert.Equal(ProfileThreshold, (bare.Compaction ?? p.Compaction)!.ThresholdTokens);

        // And neither one picked up the leader's.
        Assert.NotEqual(LeaderThreshold, (declares.Compaction ?? p.Compaction)!.ThresholdTokens);
        Assert.NotEqual(LeaderThreshold, (bare.Compaction ?? p.Compaction)!.ThresholdTokens);
    }

    // ---------------------------------------------------------------

    /// <summary>The leader's row from the probe's seat walk.</summary>
    private static EndpointTarget LeaderTarget(Profile p) =>
        EndpointProbe.TargetsOf(p).Single(t => t.Path.Contains(".leader["));

    /// <summary>
    /// Round-trips through Yaml.LoadProfile — the loader the runtime actually
    /// uses. Building a Profile by hand would prove the seam works on data the
    /// deserialiser might never produce, which is the whole risk with a field
    /// whose failure mode is "it parsed and was then discarded".
    /// </summary>
    private static Profile Fixture(int? leaderThreshold)
    {
        var leaderBlock = leaderThreshold is null
            ? ""
            : $"\n    compaction:\n      threshold_tokens: {leaderThreshold}";

        var yaml = $"""
            name: leader-compaction-fixture
            llm:
              provider: local
              endpoint: http://base:8000/v1
              model: flash
            middleware:
              - output_truncation
            compaction:
              threshold_tokens: {ProfileThreshold}
              keep_last_messages: {ProfileKeepLast}
            max_iterations: 50
            team:
              leader:
                name: lead{leaderBlock}
              members:
                - name: declares
                  compaction:
                    threshold_tokens: {MemberThreshold}
                - name: bare
            """;

        var path = Path.Combine(Path.GetTempPath(), "vett-leader-comp-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, yaml);
            var p = Yaml.LoadProfile(path);

            // ⛔ THE FIXTURE'S OWN LIVENESS CHECK. Every assertion in this file
            // is about which of two blocks gets read. If the YAML above stopped
            // binding team.leader.compaction, all of them would compare the
            // profile's block to itself and PASS while proving nothing — the
            // exact failure this file exists to make impossible.
            Assert.NotNull(p.Team);
            Assert.NotNull(p.Compaction);
            Assert.Equal(ProfileThreshold, p.Compaction!.ThresholdTokens);
            Assert.Equal(ProfileKeepLast, p.Compaction.KeepLastMessages);

            if (leaderThreshold is null)
            {
                Assert.Null(p.Team!.Leader.Compaction);
            }
            else
            {
                Assert.NotNull(p.Team!.Leader.Compaction);
                Assert.Equal(leaderThreshold.Value, p.Team.Leader.Compaction!.ThresholdTokens);
            }

            return p;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
