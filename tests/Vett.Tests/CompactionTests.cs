using Microsoft.Extensions.AI;
using Vett.Llm;
using Vett.Tools;

namespace Vett.Tests;

public class ObservationElisionTests
{
    [Fact]
    public async Task ElidesOldToolOutputs()
    {
        var mw = CompactionMiddleware.ObservationElision(keepLastN: 2, maxResultChars: 20);
        var state = new AgentState
        {
            Messages =
            [
                Chat.User("start"),
                Chat.ToolResult("c1", new string('x', 500)),  // old — should be elided
                Chat.ToolResult("c2", new string('y', 500)),  // old — should be elided
                Chat.ToolResult("c3", "recent short"),         // recent — kept
                Chat.ToolResult("c4", "also recent"),          // recent — kept
            ]
        };

        await mw(state, default);

        // Old tool outputs should be truncated.
        Assert.Contains("elided", state.Messages[1].GetText());
        Assert.Contains("elided", state.Messages[2].GetText());

        // Recent ones untouched.
        Assert.Equal("recent short", state.Messages[3].GetText());
        Assert.Equal("also recent", state.Messages[4].GetText());
    }

    [Fact]
    public async Task DoesNotElideWhenFewMessages()
    {
        var mw = CompactionMiddleware.ObservationElision(keepLastN: 5, maxResultChars: 20);
        var state = new AgentState
        {
            Messages =
            [
                Chat.ToolResult("c1", "short"),
                Chat.ToolResult("c2", "also short"),
            ]
        };

        await mw(state, default);

        Assert.Equal("short", state.Messages[0].GetText());
        Assert.Equal("also short", state.Messages[1].GetText());
    }
}

/// <summary>
/// ⚠ EVERY test here must pass an explicit <c>sessionLogDir</c>.
///
/// MilestoneCheckpoint now snapshots history before clearing it, and an
/// unset sessionLogDir resolves to the REAL <c>~/.vett/chat-sessions/</c>
/// (under <c>compaction/</c>). Constructing the middleware without one and
/// then crossing the threshold makes the unit suite write into the user's
/// actual session store on every run. The temp dir + Dispose below is the
/// same pattern ReplanCheckpointTests already used, for the same reason.
/// </summary>
public class MilestoneCheckpointTests : IDisposable
{
    private readonly string _dir;

    public MilestoneCheckpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-milestone-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task ResetsContextWhenOverThreshold()
    {
        var mw = CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 50, keepLastMessages: 2, model: null,
            sessionLogDir: _dir, sessionId: "run1");

        // Create a state with lots of text to exceed threshold.
        var state = new AgentState
        {
            Iteration = 10,
            Messages =
            [
                Chat.System("You are helpful."),
                Chat.User(new string('a', 500)),
                Chat.Assistant(new string('b', 500)),
                Chat.ToolResult("c1", new string('c', 500)),
                Chat.User("recent 1"),
                Chat.Assistant("recent 2"),
            ]
        };

        await mw(state, default);

        // EXACT shape: system + digest(user) + ack(assistant) + the last 2.
        //
        // This assertion used to be `Count <= 6` on a state that STARTS with 6
        // messages — satisfied by the middleware doing nothing at all, along
        // with the two that followed it. The only falsifiable content in the
        // old test was "a checkpoint block got inserted." Pinning the exact
        // count and the exact roles is what makes it a test of the reset.
        Assert.Equal(5, state.Messages.Count);
        Assert.True(state.Messages[0].IsRole(ChatRole.System));
        Assert.True(state.Messages[1].IsRole(ChatRole.User));
        Assert.True(state.Messages[2].IsRole(ChatRole.Assistant));
        Assert.Equal("recent 1", state.Messages[3].GetText());
        Assert.Equal("recent 2", state.Messages[4].GetText());

        // The digest is the milestone block, and it carries real recovered
        // content rather than just a token count.
        var digest = state.Messages[1].GetText();
        Assert.StartsWith("[Milestone checkpoint at iteration", digest);
        Assert.Contains("ORIGINAL TASK:", digest);
        Assert.Contains("FULL PRIOR TRANSCRIPT:", digest);
    }

    [Fact]
    public async Task DoesNothingUnderThreshold()
    {
        var mw = CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 100_000, sessionLogDir: _dir);
        var state = new AgentState
        {
            Messages =
            [
                Chat.System("short"),
                Chat.User("hello"),
                Chat.Assistant("hi"),
            ]
        };

        await mw(state, default);

        Assert.Equal(3, state.Messages.Count);
        // Under threshold means NOTHING happened — including no snapshot. A
        // strategy that writes a file it then doesn't act on would still pass a
        // message-count check.
        Assert.False(Directory.Exists(Path.Combine(_dir, "compaction")));
    }

    [Fact]
    public async Task DropsLeadingOrphanToolResultAfterTrim()
    {
        // Regression: previously, when the kept window started with a Tool
        // result whose matching Assistant tool_call was just dropped, the LLM
        // API would 400 on the orphaned tool_call_id. The middleware should
        // strip leading orphans.
        var mw = CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 50, keepLastMessages: 3, model: null, sessionLogDir: _dir);

        var state = new AgentState
        {
            Iteration = 5,
            Messages =
            [
                Chat.System("system"),
                Chat.User(new string('a', 500)),
                Chat.AssistantWithCalls(null, [new Microsoft.Extensions.AI.FunctionCallContent("c1", "tool")]),
                Chat.ToolResult("c1", new string('b', 500)),
                Chat.Assistant("recent reply"),
                Chat.User("follow-up"),
            ]
        };

        await mw(state, default);

        // The kept window of 3 would have been [tool_result, assistant, user].
        // The leading orphan tool_result must be dropped.
        Assert.DoesNotContain(state.Messages.Skip(3),
            m => m.IsRole(Microsoft.Extensions.AI.ChatRole.Tool));

        // ...and state the real invariant, which the role check above only
        // approximates. "No Tool role in the tail" is both too strong (a
        // properly PAIRED call+result inside the kept window is legal) and too
        // weak (it cannot see an orphan that isn't leading). What the provider
        // actually 400s on is a tool result whose call_id has no matching
        // tool_call, so assert exactly that.
        AssertNoOrphanedToolResults(state.Messages);
    }

    [Fact]
    public async Task DropsLeadingOrphanAssistantWithToolCallsAfterTrim()
    {
        // The OTHER branch of the same trim, which had no test at all: the kept
        // window begins with an Assistant carrying tool_calls whose matching
        // Tool results were truncated off the end. Providers 400 on this too —
        // a tool_call with no result is as invalid as a result with no call.
        var mw = CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 50, keepLastMessages: 2, model: null, sessionLogDir: _dir);

        var state = new AgentState
        {
            Iteration = 5,
            Messages =
            [
                Chat.System("system"),
                Chat.User(new string('a', 800)),
                Chat.Assistant(new string('b', 800)),
                Chat.AssistantWithCalls(null, [new Microsoft.Extensions.AI.FunctionCallContent("c9", "tool")]),
                Chat.User("follow-up"),
            ]
        };

        await mw(state, default);

        // Kept window of 2 = [assistant-with-calls, user]. The leading
        // assistant-with-calls is an orphan and must be dropped, leaving
        // system + digest + ack + "follow-up".
        Assert.Equal(4, state.Messages.Count);
        Assert.Equal("follow-up", state.Messages.Last().GetText());
        Assert.DoesNotContain(state.Messages.Skip(3), m => m.HasToolCalls());
        AssertNoOrphanedToolResults(state.Messages);
    }

    /// <summary>
    /// The invariant the orphan trim exists to protect: every surviving tool
    /// RESULT must have a matching tool CALL earlier in the list, and every
    /// surviving tool CALL must have its result. Either half missing is a 400
    /// from the provider, which on a long run reads as a mysterious mid-flight
    /// death rather than as a compaction bug.
    /// </summary>
    internal static void AssertNoOrphanedToolResults(List<ChatMessage> messages)
    {
        var callIds = messages
            .SelectMany(m => m.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>())
            .Select(c => c.CallId)
            .ToHashSet();
        var resultIds = messages
            .SelectMany(m => m.Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>())
            .Select(r => r.CallId)
            .ToHashSet();

        var orphanResults = resultIds.Except(callIds).ToList();
        Assert.True(orphanResults.Count == 0,
            $"tool results with no matching tool_call: {string.Join(", ", orphanResults)}");

        var unansweredCalls = callIds.Except(resultIds).ToList();
        Assert.True(unansweredCalls.Count == 0,
            $"tool_calls with no matching result: {string.Join(", ", unansweredCalls)}");
    }
}
