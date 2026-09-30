using Vett.Agent;
using Vett.Bench.Team;

namespace Vett.Tests;

/// <summary>
/// Coverage for the `min_count` assertion knob.
///
/// WHY IT EXISTS. A sweep of 61 team-bench runs across 8 suites (2026-08-26,
/// ds-team-flash) measured a maximum leader fan-out of ONE member; 40 of the
/// 61 runs dispatched no members at all. The suites still passed, because
/// fan-out was only ever RECORDED (in `member_iterations`) and never GATED.
/// The assertion vocabulary had `max_count`, which applies to `no_event` and
/// bounds things from above — there was no way at all to say "the leader must
/// dispatch at least five members".
///
/// `min_count` closes that. It defaults to 1, which is exactly what both
/// evaluators did before (return on the first satisfying match), so every
/// pre-existing assertion in every shipped suite is unaffected — the
/// Default_* tests below are what pins that down.
/// </summary>
public class AssertionMinCountTests
{
    private readonly string _workspace;

    public AssertionMinCountTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "vett-mincount-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspace);
    }

    private AssertionResult Eval(TeamBenchAssertion a, IReadOnlyList<Event> events) =>
        AssertionEngine.Evaluate(
            new List<TeamBenchAssertion> { a },
            events,
            _workspace,
            leaderIterations: 0,
            memberIterations: new Dictionary<string, int>(),
            countsComplete: true,
            assistantTexts: Array.Empty<string>()
        ).Single();

    /// <summary>N successful `dispatch` calls, as a fanned-out leader turn produces.</summary>
    private static List<Event> DispatchCalls(int n, bool success = true)
    {
        var evs = new List<Event>();
        for (var i = 1; i <= n; i++)
        {
            evs.Add(new Event("tool_call_start", new Dictionary<string, object?>
            {
                ["tool_name"] = "dispatch",
                ["call_id"] = $"c{i}",
                ["arguments"] = new Dictionary<string, object?> { ["member"] = $"implementer-{i}" },
            }));
            evs.Add(new Event("tool_call_end", new Dictionary<string, object?>
            {
                ["tool_name"] = "dispatch",
                ["call_id"] = $"c{i}",
                ["success"] = success,
            }));
        }
        return evs;
    }

    private static List<Event> DispatchEndEvents(int n, int filesChanged)
    {
        var evs = new List<Event>();
        for (var i = 1; i <= n; i++)
        {
            evs.Add(new Event("dispatch_end", new Dictionary<string, object?>
            {
                ["member"] = $"implementer-{i}",
                ["files_changed"] = filesChanged,
            }));
        }
        return evs;
    }

    // ---------- tool_call: the fan-out gate ----------------------------

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public void ToolCall_MinCount_PassesWhenLeaderFansOutFarEnough(int n)
    {
        var r = Eval(new TeamBenchAssertion { ToolCall = "dispatch", MinCount = n }, DispatchCalls(n));
        Assert.True(r.Pass, r.Detail);
    }

    [Fact]
    public void ToolCall_MinCount_FailsWhenLeaderDispatchesOnlyOne()
    {
        // The exact shape the whole 61-run corpus produced.
        var r = Eval(new TeamBenchAssertion { ToolCall = "dispatch", MinCount = 5 }, DispatchCalls(1));
        Assert.False(r.Pass);
        Assert.Contains("only 1", r.Detail);
        Assert.Contains("need 5", r.Detail);
    }

    [Fact]
    public void ToolCall_MinCount_FailsWhenLeaderDispatchesNothing()
    {
        // 40 of the 61 runs looked like this.
        var r = Eval(new TeamBenchAssertion { ToolCall = "dispatch", MinCount = 5 }, Array.Empty<Event>());
        Assert.False(r.Pass);
        Assert.Contains("no tool_call_end", r.Detail);
    }

    [Fact]
    public void ToolCall_MinCount_CountsOnlyCallsMeetingTheOtherConstraints()
    {
        // 5 dispatches, but they FAILED. A fan-out gate that counted these
        // would certify a leader whose members all died.
        var r = Eval(
            new TeamBenchAssertion { ToolCall = "dispatch", Success = true, MinCount = 5 },
            DispatchCalls(5, success: false));
        Assert.False(r.Pass);
    }

    [Fact]
    public void ToolCall_MinCount_MixedSuccess_CountsOnlyTheSuccessful()
    {
        var evs = DispatchCalls(3);                    // c1..c3 succeed
        foreach (var e in DispatchCalls(2, success: false)) evs.Add(e);
        var r = Eval(new TeamBenchAssertion { ToolCall = "dispatch", Success = true, MinCount = 5 }, evs);
        Assert.False(r.Pass);
        Assert.Contains("need 5", r.Detail);
    }

    // ---------- event: same knob, same semantics -----------------------

    [Fact]
    public void Event_MinCount_PassesWhenEnoughMembersReportedWork()
    {
        var r = Eval(
            new TeamBenchAssertion { Event = "dispatch_end", FilesChangedMin = 1, MinCount = 5 },
            DispatchEndEvents(5, filesChanged: 2));
        Assert.True(r.Pass, r.Detail);
    }

    [Fact]
    public void Event_MinCount_FailsWhenTooFewSatisfyTheFieldConstraints()
    {
        // Ten members ran, but only the empty-diff kind — the silent
        // "dispatched but did nothing" failure.
        var r = Eval(
            new TeamBenchAssertion { Event = "dispatch_end", FilesChangedMin = 1, MinCount = 5 },
            DispatchEndEvents(10, filesChanged: 0));
        Assert.False(r.Pass);
        Assert.Contains("none satisfied", r.Detail);
    }

    // ---------- the compatibility pin ----------------------------------
    //
    // Every shipped suite omits min_count. If the default were anything but
    // 1, this change would silently re-score the entire existing corpus.

    [Fact]
    public void Default_ToolCall_OneMatchStillPasses()
    {
        var r = Eval(new TeamBenchAssertion { ToolCall = "dispatch" }, DispatchCalls(1));
        Assert.True(r.Pass, r.Detail);
    }

    [Fact]
    public void Default_Event_OneMatchStillPasses()
    {
        var r = Eval(
            new TeamBenchAssertion { Event = "dispatch_end", FilesChangedMin = 1 },
            DispatchEndEvents(1, filesChanged: 3));
        Assert.True(r.Pass, r.Detail);
    }

    [Fact]
    public void Default_Description_DoesNotMentionMinCount()
    {
        // The description string is what lands in the JSON artifact and in
        // every downstream report. Adding an unconditional " min_count=1"
        // would change the text of thousands of historical assertion rows.
        var r = Eval(new TeamBenchAssertion { ToolCall = "dispatch" }, DispatchCalls(1));
        Assert.DoesNotContain("min_count", r.Description);
    }

    [Fact]
    public void MinCount_AppearsInDescriptionWhenSet()
    {
        var r = Eval(new TeamBenchAssertion { ToolCall = "dispatch", MinCount = 5 }, DispatchCalls(5));
        Assert.Contains("min_count=5", r.Description);
    }
}
