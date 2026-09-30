using Vett.Bench.Team;
using Vett.Live;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vett.Tests;

/// <summary>
/// SCOPE guard for the tier-6 ruler — the companion to
/// <see cref="Tier6RulerTests"/>, added 2026-08-24 by the gate-scope audit
/// (internal note GATE-SCOPE-AUDIT-2026-08-24.md).
///
/// ⛔ THE LAW: A GATE MUST SPAN THE RUN'S POPULATION OR IT PASSES
/// VACUOUSLY. The corollary — THE GATE'S LOOP MUST BE THE RUN'S LOOP.
///
/// WHY A SECOND FILE. Tier6RulerTests binds four things: the
/// `dotnet build` run_command, the `dotnet_test` spec's min_passed /
/// max_failed, the npm client run_command, and the presence of at least
/// one `file:` assertion. It does NOT look at the other two assertion
/// kinds the suite carries — `budget:` and `no_event:` — and it never
/// counts anything.
///
/// That asymmetry is backwards with respect to demonstrated power. Of the
/// eight assertions on each instance, exactly ONE has ever produced a red
/// on a real paid run of this suite: `budget: leader_iters_max: 40`, which
/// armD-run4 crossed with 77 leader iterations (internal note 
/// PROVEN-STATE-2026-08-24.md §1 cell B). The suite's shared deserializer
/// sets IgnoreUnmatchedProperties(), so misspelling `leader_iters_max`
/// deletes that gate SILENTLY — the assertion still deserializes, still
/// evaluates, and still reports PASS, just with nothing left to check
/// (AssertionEngine.EvalBudget fails only when a count EXCEEDS a non-null
/// cap). Nothing in the repo would have caught that. This file is that
/// catch.
///
/// WHY IT DOES NOT HARD-CODE 40 OR 80. Whether the ruler's leader cap
/// should stay 5x tighter than the profiles' `max_iterations: 200`
/// (profiles/ds-team-flash.yaml:246,
/// profiles/dsv4-team-local-architect.yaml:302) is an OWNER decision, not
/// an audit decision — raising the assertion to match the config would
/// retroactively convert armD-run4's real red into a green. So these tests
/// pin STRUCTURE (the cap exists, and the three instances agree with each
/// other), never the VALUE. An owner who changes 40 deliberately changes
/// it in three places and this file stays green; an owner who loses it to
/// a typo in one place gets a red.
/// </summary>
public class Tier6GateScopeTests
{
    private static readonly IDeserializer D = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Same walk-up as Tier6RulerTests: bin/<cfg>/net10.0 -> the repo root.
    private static string SuitePath()
    {
        var asmDir = Path.GetDirectoryName(typeof(Tier6GateScopeTests).Assembly.Location)!;
        var vett = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        return Path.Combine(vett, "suites", "team-games-tier6.yaml");
    }

    private static TeamBenchSuite RealSuite() => D.Deserialize<TeamBenchSuite>(File.ReadAllText(SuitePath()));

    /// <summary>
    /// The KIND of an assertion, as AssertionEngine.EvaluateOne dispatches
    /// on it (AssertionEngine.cs:38-46, first-non-null wins). Reduced to a
    /// sorted signature per instance so "do g2 and g3 carry the same
    /// coverage as g1?" becomes a mechanical comparison instead of an
    /// eyeball diff.
    /// </summary>
    private static string Kind(TeamBenchAssertion a) =>
        a.Event is not null ? "event"
        : a.NoEvent is not null ? "no_event"
        : a.ToolCall is not null ? "tool_call"
        : a.File is not null ? "file"
        : a.Budget is not null ? "budget"
        : a.AssistantTextContains is not null ? "assistant_text_contains"
        : a.RunCommand is not null ? "run_command"
        : a.DotnetTest is not null ? "dotnet_test"
        : "(none)";

    private static string KindSignature(TeamBenchInstance i) =>
        string.Join(",", i.Assertions.Select(Kind).OrderBy(s => s, StringComparer.Ordinal));

    // ----------------------------------------------------------------
    // 1. THE COUNT. "Passed" is not evidence; a count is.
    // ----------------------------------------------------------------

    /// <summary>
    /// Every instance must carry a NON-ZERO assertion set, and the three
    /// must agree on how many. TeamBenchResult.Pass (Models.cs:245) already
    /// refuses to call an empty set a pass — but it cannot tell 8 from 1,
    /// and neither can the console summary, which prints the per-assertion
    /// rows only inside `if (!result.Pass)` (TeamBenchCommand.cs:241-249).
    /// A PASS line therefore carries ZERO visible evidence of how many
    /// assertions were evaluated. This test is where that evidence lives.
    /// </summary>
    [Fact]
    public void Every_tier6_instance_carries_a_nonzero_and_equal_assertion_count()
    {
        var suite = RealSuite();
        Assert.Equal(3, suite.Instances.Count);

        foreach (var inst in suite.Instances)
            Assert.True(inst.Assertions.Count > 0,
                $"{inst.Id}: ZERO assertions. A suite with no assertions is not a lenient gate, " +
                $"it is no gate — and the run's own verdict is the only thing standing between " +
                $"that and a green summary.");

        var counts = suite.Instances.ToDictionary(i => i.Id, i => i.Assertions.Count);
        var distinct = counts.Values.Distinct().ToList();
        Assert.True(distinct.Count == 1,
            $"tier-6 instances disagree on assertion count: " +
            $"{string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"))}. " +
            $"g1-blocks-2p is the only instance that has been run repeatedly; if g2/g3 " +
            $"carry fewer assertions then a future \"we passed 3 instances\" claim is not " +
            $"what it looks like.");
    }

    /// <summary>
    /// COVERAGE PARITY, field by field. Equal counts are not equal
    /// coverage — three `file:` assertions and no compiler gate also
    /// counts as three. Compare the sorted KIND signature so a swap
    /// (dropping `budget`, adding a fourth `file`) cannot hide behind a
    /// stable total.
    /// </summary>
    [Fact]
    public void All_three_tier6_instances_have_the_same_assertion_kind_coverage()
    {
        var suite = RealSuite();
        var sigs = suite.Instances.ToDictionary(i => i.Id, KindSignature);
        var distinct = sigs.Values.Distinct(StringComparer.Ordinal).ToList();

        Assert.True(distinct.Count == 1,
            "tier-6 instances do not carry the same assertion kinds:\n  " +
            string.Join("\n  ", sigs.Select(kv => $"{kv.Key}: {kv.Value}")));

        // A kind that never resolves is the "(none)" bucket — an assertion
        // object where every kind field is null, which AssertionEngine
        // scores as "(empty assertion)" FAIL. Fail-closed, but it means a
        // real check was lost to a typo, so name it here rather than
        // discovering it mid-campaign.
        Assert.DoesNotContain("(none)", distinct[0]);
    }

    // ----------------------------------------------------------------
    // 2. THE BUDGET GATE — the only assertion with a demonstrated red.
    // ----------------------------------------------------------------

    /// <summary>
    /// `budget:` must be present on all three instances with BOTH caps
    /// bound, and the three must agree. A null cap is not a loose cap: it
    /// is NO cap, and EvalBudget's `is not null` guards make the whole
    /// half disappear without changing the assertion's verdict.
    /// </summary>
    [Fact]
    public void Every_tier6_instance_binds_both_budget_caps_and_the_three_agree()
    {
        var suite = RealSuite();
        var leaderCaps = new List<int>();
        var memberCaps = new List<int>();

        foreach (var inst in suite.Instances)
        {
            var budget = inst.Assertions.FirstOrDefault(a => a.Budget is not null)?.Budget;
            Assert.True(budget is not null,
                $"{inst.Id}: no `budget:` assertion. leader_iters_max is the ONLY tier-6 " +
                $"assertion that has produced a red on a real run (armD-run4, 77 iters).");

            Assert.True(budget!.LeaderItersMax is not null,
                $"{inst.Id}: budget present but leader_iters_max is NULL. " +
                $"IgnoreUnmatchedProperties() turns a misspelled key into exactly this, and " +
                $"AssertionEngine.EvalBudget (AssertionEngine.cs:382) only fails when the count " +
                $"EXCEEDS a NON-NULL cap — so the assertion keeps reporting PASS with nothing left " +
                $"to check.");
            Assert.True(budget.MemberItersMax is not null,
                $"{inst.Id}: budget present but member_iters_max is NULL — same silent-deletion " +
                $"mode as leader_iters_max.");

            leaderCaps.Add(budget.LeaderItersMax!.Value);
            memberCaps.Add(budget.MemberItersMax!.Value);
        }

        // Structure, not value: whatever the owner decides the cap should
        // be, all three instances must decide it the same way.
        Assert.True(leaderCaps.Distinct().Count() == 1,
            $"leader_iters_max differs across tier-6 instances: [{string.Join(", ", leaderCaps)}]");
        Assert.True(memberCaps.Distinct().Count() == 1,
            $"member_iters_max differs across tier-6 instances: [{string.Join(", ", memberCaps)}]");
    }

    // ----------------------------------------------------------------
    // 3. THE no_event GATE — a predicate over a vocabulary.
    // ----------------------------------------------------------------

    /// <summary>
    /// A PREDICATE OVER A GUESSED VOCABULARY RETURNS A CONFIDENT ZERO.
    /// `no_event:` counts events whose Type string-equals the yaml value
    /// (AssertionEngine.cs:234, StringComparison.Ordinal). If the emitter
    /// ever renames the event, the suite keeps counting a string nothing
    /// emits, gets 0, and reports a confident green forever. Bind the yaml
    /// literal to the constant the codebase declares for it.
    /// </summary>
    [Fact]
    public void Every_tier6_no_event_names_an_event_type_the_codebase_actually_emits()
    {
        var suite = RealSuite();
        foreach (var inst in suite.Instances)
        {
            var ne = inst.Assertions.FirstOrDefault(a => a.NoEvent is not null);
            Assert.True(ne is not null, $"{inst.Id}: no `no_event:` assertion.");
            Assert.Equal(EventTypes.MalformedToolCall, ne!.NoEvent);
            Assert.True(ne.MaxCount is not null,
                $"{inst.Id}: no_event present but max_count is NULL. Null means the DEFAULT " +
                $"(strict, 0 tolerated), which is not what the suite text says — so a dropped " +
                $"key changes the ruler without changing the yaml's apparent meaning.");
        }
    }

    // ----------------------------------------------------------------
    // 4. CROSS-FIELD: the gate must point at THIS instance's artifact.
    // ----------------------------------------------------------------

    /// <summary>
    /// The three instances are copy-paste siblings differing only in
    /// solution name and web dir. A copy that forgot to rename would leave
    /// g2 asserting `dotnet build Blocks.slnx` — which fails closed on a
    /// good g2 artifact (false red), and, worse, would leave the `file:`
    /// existence assertion and the build assertion pointing at DIFFERENT
    /// files, so neither names the thing the other checked. Require the
    /// .slnx in the build command to be one the instance also asserts the
    /// existence of.
    /// </summary>
    [Fact]
    public void Each_instances_build_command_targets_a_slnx_that_instance_also_asserts_exists()
    {
        var suite = RealSuite();
        foreach (var inst in suite.Instances)
        {
            var build = inst.Assertions.FirstOrDefault(a =>
                a.RunCommand is not null &&
                a.RunCommand.Command.Contains("dotnet build", StringComparison.OrdinalIgnoreCase));
            Assert.True(build is not null, $"{inst.Id}: no `dotnet build` run_command.");

            var declaredSlnx = inst.Assertions
                .Where(a => a.File is not null && a.File.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.File!)
                .ToList();
            Assert.True(declaredSlnx.Count > 0, $"{inst.Id}: no `file:` assertion naming a .slnx.");

            Assert.True(
                declaredSlnx.Any(s => build!.RunCommand!.Command.Contains(s, StringComparison.OrdinalIgnoreCase)),
                $"{inst.Id}: build command `{build!.RunCommand!.Command}` does not name any .slnx " +
                $"this instance asserts the existence of ({string.Join(", ", declaredSlnx)}). " +
                $"One of the two was copy-pasted from a sibling instance.");
        }
    }

    // ----------------------------------------------------------------
    // 5. THE RUN-TIME COUNT GATE — verify the TOOL, not the artifact.
    // ----------------------------------------------------------------

    /// <summary>
    /// AN INVARIANT IS ONLY AS REAL AS ITS FAILURE TEST. The run-time
    /// guard against "green summary, zero assertions evaluated" is the
    /// `AssertionResults.Count > 0` conjunct in TeamBenchResult.Pass
    /// (Models.cs:245). Prove it FAILS on the empty set rather than
    /// trusting that `All(...)` on an empty list — which returns TRUE —
    /// is being defended somewhere. Without the conjunct, a harness
    /// exception that wipes the result list would publish PASS.
    /// </summary>
    [Fact]
    public void A_result_with_zero_evaluated_assertions_is_not_a_pass()
    {
        var empty = new TeamBenchResult { InstanceId = "probe" };
        Assert.Empty(empty.AssertionResults);
        Assert.False(empty.Pass,
            "TeamBenchResult.Pass returned TRUE for a result with zero evaluated assertions. " +
            "Enumerable.All is vacuously true on an empty sequence, so the `Count > 0` conjunct " +
            "in Models.cs is the ONLY thing preventing a green verdict from a run where the " +
            "assertion engine never evaluated anything.");

        // Two-sided: the same property must still say PASS when a real,
        // non-empty, all-green set is present — otherwise the guard above
        // would be indistinguishable from a Pass property hard-wired false.
        var green = new TeamBenchResult
        {
            InstanceId = "probe",
            AssertionResults = { new AssertionResult { Description = "file: x", Pass = true } },
        };
        Assert.True(green.Pass);
    }

    // ----------------------------------------------------------------
    // 6. FAILURE TESTS for the checkers above. VERIFY THE TOOL.
    // ----------------------------------------------------------------

    /// <summary>
    /// Feed the SAME reductions a deliberately weakened suite and require
    /// them to reject it. If these ever go green, every test above proves
    /// nothing. The weakened yaml lives here, not on disk — the real suite
    /// is never touched.
    /// </summary>
    [Fact]
    public void The_scope_checks_reject_a_weakened_suite()
    {
        // g-weak-1 keeps all four gate kinds. g-weak-2 has lost `budget`
        // to a one-character typo, has a null max_count, and builds a
        // sibling's solution file — three independent regressions, each of
        // which the real suite must never carry.
        const string yaml = """
            name: probe-weak
            instances:
              - id: g-weak-1
                description: probe
                workspace: empty-git-repo
                prompt: probe
                assertions:
                  - file: Alpha.slnx
                    exists: true
                  - run_command:
                      command: dotnet build Alpha.slnx
                      exit_code: 0
                  - dotnet_test:
                      min_passed: 10
                  - no_event: malformed_tool_call
                    max_count: 10
                  - budget:
                      member_iters_max: 80
                      leader_iters_max: 40
              - id: g-weak-2
                description: probe
                workspace: empty-git-repo
                prompt: probe
                assertions:
                  - file: Beta.slnx
                    exists: true
                  - run_command:
                      command: dotnet build Alpha.slnx
                      exit_code: 0
                  - dotnet_test:
                      min_passed: 10
                  - no_event: malformed_tool_call
                  - budget:
                      member_iters_max: 80
                      leader_iterz_max: 40
            """;

        var weak = D.Deserialize<TeamBenchSuite>(yaml);
        var g1 = weak.Instances[0];
        var g2 = weak.Instances[1];

        // (a) The typo does NOT throw and does NOT change the count — this
        //     is precisely why a count-only gate is insufficient and the
        //     binding checks above exist.
        Assert.Equal(g1.Assertions.Count, g2.Assertions.Count);
        Assert.Equal(KindSignature(g1), KindSignature(g2));

        // (b) leader_iters_max silently became null on g-weak-2 while the
        //     `budget:` assertion itself survives and still reports PASS.
        var b1 = g1.Assertions.First(a => a.Budget is not null).Budget!;
        var b2 = g2.Assertions.First(a => a.Budget is not null).Budget!;
        Assert.NotNull(b1.LeaderItersMax);
        Assert.Null(b2.LeaderItersMax);

        // (c) max_count dropped -> null -> EvalNoEvent falls back to the
        //     strict default. Different ruler, identical-looking yaml.
        Assert.NotNull(g1.Assertions.First(a => a.NoEvent is not null).MaxCount);
        Assert.Null(g2.Assertions.First(a => a.NoEvent is not null).MaxCount);

        // (d) the cross-field check catches the sibling's solution name.
        var declared2 = g2.Assertions.Where(a => a.File is not null).Select(a => a.File!).ToList();
        var build2 = g2.Assertions.First(a =>
            a.RunCommand is not null &&
            a.RunCommand.Command.Contains("dotnet build", StringComparison.OrdinalIgnoreCase));
        Assert.False(
            declared2.Any(s => build2.RunCommand!.Command.Contains(s, StringComparison.OrdinalIgnoreCase)),
            "the weakened fixture was supposed to build a SIBLING's .slnx; if this passes, the " +
            "cross-field check in Each_instances_build_command_targets_a_slnx_that_instance_also_asserts_exists " +
            "has no failure mode and proves nothing.");
    }
}
