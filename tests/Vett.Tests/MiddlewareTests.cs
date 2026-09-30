using Microsoft.Extensions.AI;
using Vett.Llm;
using Vett.Tools;

namespace Vett.Tests;

public class SubmitDetectorTests
{
    [Fact]
    public async Task TriggersOnMarker()
    {
        var state = new AgentState
        {
            LastObservations =
            [
                new() { ToolCallId = "1", ToolName = "finish", Result = Builtins.SubmitMarker + "done", Success = true }
            ]
        };

        await BuiltinMiddleware.SubmitDetector(state, default);

        Assert.True(state.StopLoop);
        Assert.Equal("finish_tool", state.StopReason);
    }

    [Fact]
    public async Task IgnoresOtherResults()
    {
        var state = new AgentState
        {
            LastObservations =
            [
                new() { ToolCallId = "1", ToolName = "terminal", Result = "[exit code: 0]", Success = true }
            ]
        };

        await BuiltinMiddleware.SubmitDetector(state, default);

        Assert.False(state.StopLoop);
    }
}

public class OutputTruncationTests
{
    [Fact]
    public async Task ClipsLongResult()
    {
        var longResult = new string('x', 20_000);
        var state = new AgentState
        {
            Messages = [Chat.ToolResult("c1", longResult)],
            LastObservations =
            [
                new() { ToolCallId = "c1", ToolName = "terminal", Result = longResult, Success = true }
            ]
        };

        await BuiltinMiddleware.OutputTruncation(state, default);

        Assert.True(state.LastObservations[0].Result.Length < longResult.Length);
        Assert.Contains("truncated", state.LastObservations[0].Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeavesShortResultAlone()
    {
        var state = new AgentState
        {
            Messages = [Chat.ToolResult("c1", "hi")],
            LastObservations =
            [
                new() { ToolCallId = "c1", ToolName = "terminal", Result = "hi", Success = true }
            ]
        };

        await BuiltinMiddleware.OutputTruncation(state, default);

        Assert.Equal("hi", state.LastObservations[0].Result);
    }
}

public class StuckDetectorTests
{
    [Fact]
    public async Task Monologue_TriggersAtThreshold()
    {
        var state = new AgentState
        {
            Messages =
            [
                Chat.User("start"),
                Chat.Assistant("thinking..."),
                Chat.Assistant("still thinking..."),
                Chat.Assistant("more thinking..."),
                Chat.Assistant("even more thinking..."),
            ]
        };

        await BuiltinMiddleware.StuckDetector(state, default);

        Assert.True(state.StopLoop);
        Assert.Contains("monologue", state.StopReason);
    }

    [Fact]
    public async Task Monologue_BelowThreshold_DoesNotTrigger()
    {
        var state = new AgentState
        {
            Messages =
            [
                Chat.User("start"),
                Chat.Assistant("thinking..."),
                Chat.Assistant("still thinking..."),
                Chat.Assistant("more thinking..."),
            ]
        };

        await BuiltinMiddleware.StuckDetector(state, default);

        Assert.False(state.StopLoop);
    }

    /// <summary>
    /// One turn: an assistant message carrying `n` tool calls, followed by the
    /// `n` tool results it produced. This is the shape a real transcript has —
    /// tool results never appear without an assistant turn in front of them.
    /// </summary>
    private static IEnumerable<ChatMessage> Turn(int turnIndex, params string[] results)
    {
        var calls = results
            .Select((_, i) => new FunctionCallContent($"t{turnIndex}c{i}", "terminal", null))
            .ToList();
        yield return Chat.AssistantWithCalls(null, calls);
        for (var i = 0; i < results.Length; i++)
            yield return Chat.ToolResult($"t{turnIndex}c{i}", results[i]);
    }

    [Fact]
    public async Task ActionErrorLoop_Triggers()
    {
        // Four turns that each failed — a genuine loop. This test used to
        // pass four bare ToolResults with no assistant messages between
        // them, which now reads as ONE turn making four parallel calls (see
        // FourErrorsInOneParallelTurn_DoesNotTrigger). The intent it was
        // written to protect — a repeatedly-failing agent gets stopped — is
        // what's asserted here, in the shape a real transcript has.
        var state = new AgentState
        {
            Messages =
            [
                Chat.User("start"),
                .. Turn(1, "Error: failed"),
                .. Turn(2, "Error: failed"),
                .. Turn(3, "Error: failed"),
                .. Turn(4, "Error: failed"),
            ]
        };

        await BuiltinMiddleware.StuckDetector(state, default);

        Assert.True(state.StopLoop);
        Assert.Contains("action_error_loop", state.StopReason);
    }

    [Fact]
    public async Task ActionErrorLoop_ThreeFailedTurns_DoesNotTrigger()
    {
        var state = new AgentState
        {
            Messages =
            [
                Chat.User("start"),
                .. Turn(1, "Error: failed"),
                .. Turn(2, "Error: failed"),
                .. Turn(3, "Error: failed"),
            ]
        };

        await BuiltinMiddleware.StuckDetector(state, default);

        Assert.False(state.StopLoop);
    }

    [Fact]
    public async Task ActionErrorLoop_SucceedingTurnResetsTheStreak()
    {
        var state = new AgentState
        {
            Messages =
            [
                Chat.User("start"),
                .. Turn(1, "Error: failed"),
                .. Turn(2, "Error: failed"),
                .. Turn(3, "ok"),
                .. Turn(4, "Error: failed"),
                .. Turn(5, "Error: failed"),
            ]
        };

        await BuiltinMiddleware.StuckDetector(state, default);

        Assert.False(state.StopLoop);
    }

    // ---- fan-out: one bad turn is not a loop -------------------------------

    /// <summary>
    /// THE OLD BEHAVIOUR, NOW ASSERTED IN REVERSE. This is byte-for-byte the
    /// message list the previous ActionErrorLoop_Triggers used, and it used to
    /// stop the loop. A leader fanning out issues N tool calls in ONE turn, so
    /// N failures arrive together; killing it there stops the agent before it
    /// has had a single chance to react. Four failures in one turn is one
    /// failed turn.
    /// </summary>
    [Fact]
    public async Task FourErrorsInOneParallelTurn_DoesNotTrigger()
    {
        var state = new AgentState
        {
            Messages =
            [
                Chat.User("start"),
                .. Turn(1, "Error: failed", "Error: failed", "Error: failed", "Error: failed"),
            ]
        };

        await BuiltinMiddleware.StuckDetector(state, default);

        Assert.False(state.StopLoop);
    }

    /// <summary>
    /// THE MEASURED FAN-OUT KILL (2026-08-26, team-fanout-tier2/fan5).
    ///
    /// The leader dispatched five members with assign_async, then polled all
    /// five in its next turn. Every member was still working, so every poll
    /// returned the benign "still RUNNING" status — which keeps an "Error:"
    /// prefix because tool_call_end.success is derived from it. The detector
    /// killed the leader at iteration 3; all five members finished correct
    /// edits that were then discarded, because the leader that had to accept
    /// them was gone.
    /// </summary>
    [Fact]
    public async Task FiveStillRunningPolls_InOneTurn_DoesNotTrigger()
    {
        var poll = $"Error: task_id 'implementer-1' {Builtins.StillRunningMarker} — there's nothing to review yet.";
        var state = new AgentState
        {
            Messages =
            [
                Chat.User("start"),
                .. Turn(1, poll, poll, poll, poll, poll),
            ]
        };

        await BuiltinMiddleware.StuckDetector(state, default);

        Assert.False(state.StopLoop);
    }

    /// <summary>
    /// Turn-counting alone would not save a leader whose members are simply
    /// slow: four polling turns while five dispatches are legitimately in
    /// flight is correct behaviour, not a stuck agent.
    /// </summary>
    [Fact]
    public async Task RepeatedStillRunningPolls_AcrossManyTurns_DoNotTrigger()
    {
        var poll = $"Error: task_id 'implementer-1' {Builtins.StillRunningMarker} — nothing to review yet.";
        var state = new AgentState
        {
            Messages =
            [
                Chat.User("start"),
                .. Turn(1, poll, poll),
                .. Turn(2, poll, poll),
                .. Turn(3, poll, poll),
                .. Turn(4, poll, poll),
                .. Turn(5, poll, poll),
                .. Turn(6, poll, poll),
            ]
        };

        await BuiltinMiddleware.StuckDetector(state, default);

        Assert.False(state.StopLoop);
    }

    /// <summary>
    /// The benign exemption must not become a blanket amnesty: a turn that
    /// mixes a real failure in with waits is still a failed turn.
    /// </summary>
    [Fact]
    public async Task RealErrorsAlongsideBenignWaits_StillTrigger()
    {
        var poll = $"Error: task_id 'implementer-1' {Builtins.StillRunningMarker} — nothing to review yet.";
        var state = new AgentState
        {
            Messages =
            [
                Chat.User("start"),
                .. Turn(1, poll, "Error: no such file"),
                .. Turn(2, poll, "Error: no such file"),
                .. Turn(3, poll, "Error: no such file"),
                .. Turn(4, poll, "Error: no such file"),
            ]
        };

        await BuiltinMiddleware.StuckDetector(state, default);

        Assert.True(state.StopLoop);
        Assert.Contains("action_error_loop", state.StopReason);
    }

    [Fact]
    public async Task ResetsOnUserMessage()
    {
        var state = new AgentState
        {
            Messages =
            [
                Chat.User("start"),
                Chat.Assistant("a"),
                Chat.Assistant("b"),
                Chat.Assistant("c"),
                Chat.User("try again"),
                Chat.Assistant("d"),
            ]
        };

        await BuiltinMiddleware.StuckDetector(state, default);

        Assert.False(state.StopLoop);
    }
}
