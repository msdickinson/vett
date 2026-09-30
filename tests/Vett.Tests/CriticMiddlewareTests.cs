using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Llm;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// Covers the LLM-backed middleware: <see cref="CriticMiddleware.AgentFinishedCritic"/>
/// and <see cref="CompactionMiddleware.LLMSummarizingCondenser"/>.
///
/// Both rely on an <see cref="IChatClient"/> at construction time. We
/// stub it via <see cref="StubChatClient"/> below, which returns canned
/// responses and counts how many calls were made — enough to verify
/// the LGTM / disagree / max-retry / fallback paths without a live
/// LLM endpoint.
/// </summary>
public class AgentFinishedCriticTests
{
    [Fact]
    public async Task DoesNothing_WhenLoopNotStopped()
    {
        var stub = new StubChatClient(_ => "DISAGREE: not done");
        var llm = new LlmSettings(stub, "test", 0.0);
        var mw = CriticMiddleware.AgentFinishedCritic(llm, maxRetries: 1);

        var state = NewState(stopLoop: false);
        await mw(state, default);

        Assert.False(state.StopLoop);
        Assert.Equal(0, stub.CallCount); // critic shouldn't fire
    }

    [Fact]
    public async Task DoesNothing_WhenStoppedForOtherReason()
    {
        // Critic only triggers on finish_tool. Other reasons (max_iterations,
        // stuck:*, llm_error) are out of scope.
        var stub = new StubChatClient(_ => "DISAGREE: not done");
        var llm = new LlmSettings(stub, "test", 0.0);
        var mw = CriticMiddleware.AgentFinishedCritic(llm);

        var state = NewState(stopLoop: true, stopReason: "stuck:monologue");
        await mw(state, default);

        Assert.True(state.StopLoop);
        Assert.Equal("stuck:monologue", state.StopReason);
        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task LeavesLoopStopped_WhenCriticAgrees()
    {
        var stub = new StubChatClient(_ => "LGTM");
        var llm = new LlmSettings(stub, "test", 0.0);
        var mw = CriticMiddleware.AgentFinishedCritic(llm);

        var state = NewState(stopLoop: true, stopReason: "finish_tool");
        var msgsBefore = state.Messages.Count;
        await mw(state, default);

        Assert.True(state.StopLoop);
        Assert.Equal("finish_tool", state.StopReason);
        Assert.Equal(msgsBefore, state.Messages.Count); // no injection
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task UnstopsLoop_AndInjectsCritique_WhenCriticDisagrees()
    {
        var stub = new StubChatClient(_ => "You only handled the create case; the update path still throws on null.");
        var llm = new LlmSettings(stub, "test", 0.0);
        var mw = CriticMiddleware.AgentFinishedCritic(llm, maxRetries: 1);

        var state = NewState(stopLoop: true, stopReason: "finish_tool");
        await mw(state, default);

        Assert.False(state.StopLoop);
        Assert.Equal("", state.StopReason);
        // Critique injected as a user-style turn with the <critic_feedback> envelope.
        var injected = state.Messages.Last();
        Assert.Equal(ChatRole.User, injected.Role);
        Assert.Contains("<critic_feedback>", injected.GetText());
        Assert.Contains("update path still throws", injected.GetText());
    }

    [Fact]
    public async Task RespectsMaxRetries_AcceptsFinishOnceCapped()
    {
        // First disagree → un-stops, injects critique.
        // Second disagree would un-stop again; with maxRetries=1 it shouldn't.
        var stub = new StubChatClient(_ => "Still not done; missing tests.");
        var llm = new LlmSettings(stub, "test", 0.0);
        var mw = CriticMiddleware.AgentFinishedCritic(llm, maxRetries: 1);

        var state = NewState(stopLoop: true, stopReason: "finish_tool");
        await mw(state, default);
        Assert.False(state.StopLoop); // first call: un-stopped

        // Agent calls finish again; SubmitDetector re-flips StopLoop.
        state.StopLoop = true;
        state.StopReason = "finish_tool";
        await mw(state, default);
        // Second call: maxRetries reached → critic doesn't fire, finish stands.
        Assert.True(state.StopLoop);
        Assert.Equal("finish_tool", state.StopReason);
        Assert.Equal(1, stub.CallCount); // only the first attempt called the LLM
    }

    [Fact]
    public async Task SoftFails_WhenCriticThrows()
    {
        // LLM error inside the critic should NOT crash the loop or
        // override the agent's finish. Trust the agent on critic failure.
        var stub = new StubChatClient(_ => throw new InvalidOperationException("simulated LLM 500"));
        var llm = new LlmSettings(stub, "test", 0.0);
        var mw = CriticMiddleware.AgentFinishedCritic(llm);

        var state = NewState(stopLoop: true, stopReason: "finish_tool");
        await mw(state, default);

        Assert.True(state.StopLoop); // loop stays stopped
        Assert.Equal("finish_tool", state.StopReason);
    }

    private static AgentState NewState(bool stopLoop, string stopReason = "finish_tool") =>
        new()
        {
            Messages = new List<ChatMessage>
            {
                Chat.System("You are an assistant."),
                Chat.User("Fix the null-pointer bug in user_service.py"),
                Chat.Assistant("Done — applied a null guard."),
            },
            StopLoop = stopLoop,
            StopReason = stopLoop ? stopReason : "",
        };
}

/// <summary>
/// ⚠ Any test here that crosses the threshold must pass an explicit
/// <c>sessionLogDir</c>. The condenser snapshots history before clearing it,
/// and an unset dir resolves to the real <c>~/.vett/chat-sessions/compaction/</c>
/// — so the unit suite would write into the user's actual session store on
/// every run. Same reason ReplanCheckpointTests has always used a temp dir.
/// </summary>
public class LLMSummarizingCondenserTests : IDisposable
{
    private readonly string _dir;

    public LLMSummarizingCondenserTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-condense-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task DoesNothing_BelowTokenThreshold()
    {
        var stub = new StubChatClient(_ => "summary text");
        var llm = new LlmSettings(stub, "test", 0.0);
        // Threshold higher than a small conversation will produce.
        var mw = CompactionMiddleware.LLMSummarizingCondenser(llm, tokenThreshold: 100_000);

        var state = new AgentState
        {
            Messages = new List<ChatMessage>
            {
                Chat.System("system"),
                Chat.User("hi"),
                Chat.Assistant("hello"),
            },
        };
        var msgsBefore = state.Messages.Count;
        await mw(state, default);

        Assert.Equal(msgsBefore, state.Messages.Count);
        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task CompactsHistory_AboveThreshold()
    {
        var stub = new StubChatClient(_ => "Earlier turns: user asked for X, agent did A and B.");
        var llm = new LlmSettings(stub, "test", 0.0);
        var mw = CompactionMiddleware.LLMSummarizingCondenser(
            llm, tokenThreshold: 0, keepLastMessages: 2, sessionLogDir: _dir);

        var state = new AgentState
        {
            Messages = new List<ChatMessage>
            {
                Chat.System("system prompt"),
                Chat.User("first request"),
                Chat.Assistant("first reply"),
                Chat.User("second request"),
                Chat.Assistant("second reply"),
                Chat.User("third request"),
                Chat.Assistant("third reply"),
            },
        };
        await mw(state, default);

        // Layout after compact: [system] + [user-summary] + [assistant-ack] + lastN
        Assert.True(state.Messages.Count <= 5, $"expected ≤5 messages, got {state.Messages.Count}");
        Assert.Equal(ChatRole.System, state.Messages[0].Role);
        Assert.Contains("Earlier turns", state.Messages[1].GetText());
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task FallsBackToStaticSummary_WhenLLMErrors()
    {
        var stub = new StubChatClient(_ => throw new InvalidOperationException("LLM unavailable"));
        var llm = new LlmSettings(stub, "test", 0.0);
        var mw = CompactionMiddleware.LLMSummarizingCondenser(
            llm, tokenThreshold: 0, keepLastMessages: 1, sessionLogDir: _dir);

        var state = new AgentState
        {
            Messages = new List<ChatMessage>
            {
                Chat.System("system"),
                Chat.User("a"),
                Chat.Assistant("b"),
                Chat.User("c"),
            },
        };
        await mw(state, default);

        // Even with the LLM erroring, the condenser should still compact
        // — using the static fallback so the loop keeps running.
        var summaryMsg = state.Messages.FirstOrDefault(m => m.IsRole(ChatRole.User) && m.GetText().Contains("Milestone checkpoint"));
        Assert.NotNull(summaryMsg);
    }
}

/// <summary>
/// Minimal IChatClient stub for testing LLM-backed middleware.
/// Returns whatever <c>responseFn</c> produces, and counts how many
/// times GetResponseAsync was invoked.
/// </summary>
internal sealed class StubChatClient : IChatClient
{
    private readonly Func<IEnumerable<ChatMessage>, string> _responseFn;
    public int CallCount { get; private set; }

    public StubChatClient(Func<IEnumerable<ChatMessage>, string> responseFn)
    {
        _responseFn = responseFn;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        var text = _responseFn(messages); // may throw to test the error path
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        return Task.FromResult(resp);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("streaming not used by these tests");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
