using Vett.Cli;

namespace Vett.Tests;

/// <summary>
/// PICKER-SUMMARY FIDELITY GATE.
///
/// THE DEFECT THIS PINS. `vett profiles --json` reported only the TOP-LEVEL
/// llm block, so a TEAM profile was summarized by its leader alone. Measured
/// against the real corpus on 2026-08-26: 12 of 49 profiles had a headline
/// endpoint that hid at least one other — 24% of what a picker lists.
///
/// ⭐ WHY THAT IS A CORRECTNESS BUG AND NOT A COSMETIC ONE. The two halves can
/// disagree about whether the profile works at all. `dsv4-tier3` headlines
/// `https://openrouter.ai/api/v1` — healthy, reachable, unremarkable — while its
/// members bind to a host vacated by a DHCP re-lease, 251 lines further down the
/// file. Rendered in a dropdown, a profile that cannot run reads as perfectly
/// fine, and the failure only appears once someone spends a run on it.
///
/// ⛔ THIS GATE DELIBERATELY KNOWS NOTHING ABOUT WHICH BINDINGS ARE HEALTHY.
/// No reachability probe (that would be a network test, flaky by construction)
/// and no list of known-dead addresses (those are one operator's LAN, and
/// compiling them into a shipped tool is overfitting to a single environment —
/// they live in SuiteProfileBindingTests, which is scoped to THIS repo). What is
/// asserted here is only that the summary REPORTS WHAT THE PROFILE REFERENCES.
/// Judging those references is somebody else's job; being able to see them at
/// all is the precondition for anyone doing it.
/// </summary>
public class ProfileSummaryBindingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "vett-summary-" + Guid.NewGuid().ToString("N"));

    public ProfileSummaryBindingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteProfile(string name, string yaml)
    {
        var path = Path.Combine(_dir, name + ".yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    /// <summary>
    /// ⭐ THE CORE PROOF: the walk must reach the leader, EVERY member, and a
    /// nested sub-team — the tier-3 topology, where a member is itself a team
    /// leader. A refactor that keeps the `endpoints` field but quietly stops
    /// recursing would still emit a plausible-looking array containing only the
    /// headline; that is precisely the regression this catches.
    /// </summary>
    [Fact]
    public void Summary_enumerates_nested_team_bindings_not_just_the_headline()
    {
        var path = WriteProfile("nested", """
            llm:
              model: top-model
              endpoint: http://top.example/v1
            team:
              leader:
                llm:
                  model: leader-model
                  endpoint: http://leader.example/v1
              members:
                - name: implementer-1
                  llm:
                    model: member1-model
                    endpoint: http://member1.example/v1
                - name: feature-lead
                  llm:
                    model: sublead-model
                    endpoint: http://sublead.example/v1
                  team:
                    members:
                      - name: deep-implementer
                        llm:
                          model: deep-model
                          endpoint: http://deep.example/v1
            """);

        var s = SimpleCommands.BuildSummary("nested", path);

        Assert.False(s.parseError);

        // The headline fields keep their old meaning — existing consumers of
        // `model`/`endpoint` must not silently start receiving something else.
        Assert.Equal("top-model", s.model);
        Assert.Equal("http://top.example/v1", s.endpoint);

        // Top-level first, so a picker showing endpoints[0] shows the headline.
        Assert.Equal("http://top.example/v1", s.endpoints[0]);

        foreach (var expected in new[]
                 {
                     "http://leader.example/v1",
                     "http://member1.example/v1",
                     "http://sublead.example/v1",
                     "http://deep.example/v1",   // ← only reachable by recursing
                 })
            Assert.Contains(expected, s.endpoints);

        Assert.Contains("deep-model", s.models);

        // Exact counts, not just "contains": an over-broad walk that scooped up
        // extra strings would pass every assertion above.
        Assert.Equal(5, s.endpoints.Count);
        Assert.Equal(5, s.models.Count);
    }

    /// <summary>
    /// The common healthy case — a team where everyone shares one endpoint —
    /// must NOT render as "+4 more". Without dedupe the feature would cry wolf
    /// on exactly the profiles that are fine.
    /// </summary>
    [Fact]
    public void Summary_dedupes_bindings_shared_across_members()
    {
        var path = WriteProfile("shared", """
            llm:
              model: one-model
              endpoint: http://one.example/v1
            team:
              leader:
                llm:
                  model: one-model
                  endpoint: http://one.example/v1
              members:
                - name: a
                  llm:
                    model: one-model
                    endpoint: http://one.example/v1
                - name: b
                  llm:
                    model: one-model
                    endpoint: HTTP://ONE.EXAMPLE/v1
            """);

        var s = SimpleCommands.BuildSummary("shared", path);

        Assert.Single(s.endpoints);
        Assert.Single(s.models);
    }

    /// <summary>
    /// ⛔ COULD-NOT-MEASURE vs MEASURED-EMPTY, asserted in BOTH directions.
    /// A one-sided check ("malformed sets the flag") would still pass if the
    /// flag were hard-coded true, which would make every profile look broken.
    /// </summary>
    [Fact]
    public void Unparseable_profile_is_distinguishable_from_a_profile_that_declares_nothing()
    {
        // Malformed: unclosed bracket, cannot be YAML.
        var bad = WriteProfile("bad", "llm: [ this is not: valid: yaml\n  - nope");
        var badSummary = SimpleCommands.BuildSummary("bad", bad);
        Assert.True(badSummary.parseError,
            "a profile that could not be read must say so — otherwise it is indistinguishable "
          + "from one that parsed fine and declares no bindings.");

        // Valid YAML, genuinely declares no llm bindings.
        var empty = WriteProfile("empty", "description: nothing bound here\n");
        var emptySummary = SimpleCommands.BuildSummary("empty", empty);
        Assert.False(emptySummary.parseError);
        Assert.Empty(emptySummary.endpoints);

        // The two states must not be represented identically on the wire.
        Assert.NotEqual(badSummary.parseError, emptySummary.parseError);
    }

    /// <summary>
    /// The depth cap terminates. `vett profiles` is the command you reach for
    /// WHEN a profile is malformed, so it must not be the thing that hangs on
    /// one — a picker that never renders is worse than a wrong row.
    /// </summary>
    [Fact]
    public void Deeply_nested_teams_terminate_rather_than_recursing_forever()
    {
        // 20 levels — well past both the walk's cap (8) and MaxDispatchDepth
        // (whatever it currently is; it was lowered 5 -> 2 on 2026-08-26, and
        // 20 is chosen to be absurd rather than to sit just past either bound).
        var sb = new System.Text.StringBuilder("llm:\n  model: m0\n  endpoint: http://e0.example/v1\n");
        var indent = "";
        for (var i = 1; i <= 20; i++)
        {
            sb.Append(indent).Append("team:\n");
            indent += "  ";
            sb.Append(indent).Append("members:\n");
            sb.Append(indent).Append("  - name: n").Append(i).Append('\n');
            indent += "    ";
            sb.Append(indent).Append("llm:\n");
            sb.Append(indent).Append("  model: m").Append(i).Append('\n');
            sb.Append(indent).Append("  endpoint: http://e").Append(i).Append(".example/v1\n");
        }

        var path = WriteProfile("deep", sb.ToString());

        // The assertion is that this RETURNS AT ALL. A runaway walk would hang
        // the test host, and a stack overflow would kill the process outright.
        var s = SimpleCommands.BuildSummary("deep", path);

        Assert.False(s.parseError);
        Assert.Equal("http://e0.example/v1", s.endpoints[0]);

        // Bounded by the cap, and not by accident: it must have walked deeper
        // than the headline but stopped well short of all 20.
        Assert.True(s.endpoints.Count > 1, "the walk did not descend at all");
        Assert.True(s.endpoints.Count < 20,
            $"the depth cap did not bound the walk — got {s.endpoints.Count} endpoints from a 20-deep profile.");
    }
}
