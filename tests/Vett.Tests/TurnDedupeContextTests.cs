using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// THE DUPLICATE-COLLAPSE AMPLIFIER.
///
/// `TurnDedupeTests` proves `CollapseDuplicateToolCalls` works, and it does —
/// for DISPATCH. It is a pure-function test, so it could never see the half of
/// the behaviour that mattered: what the loop wrote into `state.Messages`.
/// Until 2026-09-01 that was the assistant message UNCHANGED, all N identical
/// calls still in it, plus one replayed tool result per discarded call_id. The
/// collapse saved execution time and made the context strictly worse.
///
/// The model then read its own repetition back on the next turn and imitated
/// it. Measured on EpicForge E3 run 8, leader thread, counting copies of one
/// `cat src/aggregate.js` poll in the OUTGOING request:
///
///     iter 12    1 copy       iter 15    4 copies -> model emits 41
///     iter 16   45 copies     iter 17   74 copies
///     iter 18  103 copies     iter 20  143 copies
///     iter 22  172 copies  -> truncated -> truncated -> exhausted, run dead
///
/// Input tokens 11,519 -> 41,004 on one thread. The run ended
/// `truncated_response_exhausted` with the verifier at 13/20.
///
/// This was NOT the model ignoring its prompt. On four consecutive turns it
/// wrote "I made a mistake by firing many redundant polling calls", "I keep
/// polling unnecessarily", "I've been polling excessively, which is wrong ...
/// let me end the turn and wait" — and then emitted 29 more copies. A hundred
/// identical blocks in context outweigh any instruction, which is why the
/// prompt-side A/B built to fix it (run 7 vs run 8) moved leader waste only
/// 52.1% -> 50.4%: both arms shared this amplifier.
///
/// Every assertion here is on the SECOND request's message list, because that
/// is the only place the defect was ever visible.
/// </summary>
public class TurnDedupeContextTests
{
    // ---- fixtures ---------------------------------------------------------

    private sealed class NoopSandbox : ISandbox
    {
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("ok", 0, "/fake", false));
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileInsertAsync(string s, string p, int l, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SessionCreateAsync(string n, string cwd, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string cwd) => this;
        public ISandbox WithDispatchWorktree(string newCwd, string root) => this;
    }

    /// <summary>
    /// Turn 1 fans out <paramref name="fanout"/> identical calls; every turn
    /// after that is prose so the loop finishes. Records the message list of
    /// each request, which is the thing under test — the defect was invisible
    /// from the response side.
    /// </summary>
    private sealed class FanOutThenStopClient(int fanout) : IChatClient
    {
        private int _n;
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Requests.Add(messages.ToList());
            _n++;

            if (_n == 1)
            {
                var contents = new List<AIContent>
                {
                    new TextContent("I should stop polling and wait."),
                };
                for (var i = 0; i < fanout; i++)
                {
                    contents.Add(new FunctionCallContent(
                        "call-" + i, "terminal",
                        new Dictionary<string, object?> { ["command"] = "cat src/aggregate.js" }));
                }
                return Task.FromResult(new ChatResponse(
                    [new ChatMessage(ChatRole.Assistant, contents)]));
            }

            return Task.FromResult(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, "Nothing further; the task is done.")]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static async Task<(FanOutThenStopClient client, List<Event> events)> RunFanOut(int fanout)
    {
        var events = new ConcurrentQueue<Event>();
        var client = new FanOutThenStopClient(fanout);
        var tools = new Dictionary<string, ToolFn>
        {
            ["terminal"] = (_, _, _, _) => Task.FromResult("not yet"),
        };
        var caps = new AgentCapabilities(tools, new List<JsonElement>(), new List<MiddlewareFn>());
        var env = new AgentEnvironment(new NoopSandbox(), "sess", 6, events.Enqueue);

        await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model"), caps, env, "sys", "go");

        return (client, events.ToList());
    }

    private static int CallsIn(IEnumerable<ChatMessage> msgs) =>
        msgs.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count();

    private static int ResultsIn(IEnumerable<ChatMessage> msgs) =>
        msgs.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count();

    // ---- the defect -------------------------------------------------------

    /// <summary>
    /// THE REGRESSION. 41 identical calls go out; the next request must carry
    /// ONE. Before the fix this saw 41 calls and 41 results.
    ///
    /// 41 is not a round number chosen for effect — it is the exact fan-out
    /// observed at E3 run 8 iteration 15, the turn that started the cascade.
    /// </summary>
    [Fact]
    public async Task FannedOutDuplicates_DoNotReachTheNextRequest()
    {
        var (client, _) = await RunFanOut(41);

        Assert.True(client.Requests.Count >= 2,
            "the loop must make a second request or there is nothing to assert on");

        var second = client.Requests[1];
        Assert.Equal(1, CallsIn(second));
        Assert.Equal(1, ResultsIn(second));
    }

    /// <summary>
    /// The other side of it. A prune that dropped EVERYTHING would satisfy the
    /// test above and silently destroy the turn, so the surviving call must be
    /// the one that ran, its result must be present, and the model's own text
    /// must still be there.
    /// </summary>
    [Fact]
    public async Task ThePrunedTurnKeepsTheCallThatRan_ItsResult_AndTheText()
    {
        var (client, events) = await RunFanOut(41);
        var second = client.Requests[1];

        var call = Assert.Single(second.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("call-0", call.CallId);          // first of the group wins
        Assert.Equal("terminal", call.Name);

        var res = Assert.Single(second.SelectMany(m => m.Contents).OfType<FunctionResultContent>());
        Assert.Equal("call-0", res.CallId);
        Assert.Contains("not yet", res.Result?.ToString() ?? "");

        Assert.Contains(second, m => m.Role == ChatRole.Assistant
            && m.Text.Contains("I should stop polling"));

        // and it really did fan out — otherwise this whole test is vacuous.
        var ev = Assert.Single(events, e => e.Type == "duplicate_tool_calls_collapsed");
        Assert.Equal(40, ev.Data["collapsed"]);
        Assert.Equal(1, ev.Data["executed"]);
    }

    /// <summary>
    /// No dangling call_id. Removing the replay is only safe because the
    /// pruned ids are gone from the assistant message too — every call in the
    /// request must have exactly one matching result, or the provider rejects
    /// the whole conversation.
    /// </summary>
    [Fact]
    public async Task EveryCallIdInHistoryHasExactlyOneResult()
    {
        var (client, _) = await RunFanOut(41);
        var second = client.Requests[1];

        var callIds = second.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Select(c => c.CallId).ToList();
        var resultIds = second.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(r => r.CallId).ToList();

        Assert.Equal(callIds.OrderBy(x => x), resultIds.OrderBy(x => x));
        Assert.Equal(callIds.Count, callIds.Distinct().Count());
    }

    /// <summary>
    /// The event has to name the tool. A bare count cannot be triaged — it was
    /// the missing subject that made run 8 take a raw-log re-parse to diagnose.
    /// </summary>
    [Fact]
    public async Task TheCollapseEventNamesTheToolThatRanAway()
    {
        var (_, events) = await RunFanOut(41);
        var ev = Assert.Single(events, e => e.Type == "duplicate_tool_calls_collapsed");
        Assert.Equal("terminal", ev.Data["tool_name"]);
    }

    /// <summary>
    /// The ordinary case must be untouched. Distinct parallel calls are a
    /// legitimate and desirable thing for a leader to do, and a fix that
    /// quietly serialised them would cost far more than the bug.
    /// </summary>
    [Fact]
    public async Task DistinctParallelCalls_AreAllPreserved()
    {
        var events = new ConcurrentQueue<Event>();
        var client = new DistinctCallsClient();
        var tools = new Dictionary<string, ToolFn>
        {
            ["terminal"] = (_, _, _, _) => Task.FromResult("ok"),
        };
        var caps = new AgentCapabilities(tools, new List<JsonElement>(), new List<MiddlewareFn>());
        var env = new AgentEnvironment(new NoopSandbox(), "sess", 6, events.Enqueue);

        await AgentLoop.RunAsync(new LlmSettings(client, "test-model"), caps, env, "sys", "go");

        var second = client.Requests[1];
        Assert.Equal(3, CallsIn(second));
        Assert.Equal(3, ResultsIn(second));
        Assert.DoesNotContain(events, e => e.Type == "duplicate_tool_calls_collapsed");
    }

    private sealed class DistinctCallsClient : IChatClient
    {
        private int _n;
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Requests.Add(messages.ToList());
            if (_n++ == 0)
            {
                var contents = new List<AIContent>();
                foreach (var f in new[] { "a.js", "b.js", "c.js" })
                {
                    contents.Add(new FunctionCallContent(
                        "call-" + f, "terminal",
                        new Dictionary<string, object?> { ["command"] = "cat " + f }));
                }
                return Task.FromResult(new ChatResponse(
                    [new ChatMessage(ChatRole.Assistant, contents)]));
            }
            return Task.FromResult(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, "done")]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // ---- the helper, directly ---------------------------------------------

    /// <summary>
    /// `PruneCollapsedCalls` must carry the rest of the turn across intact.
    /// The reasoning capture reads `AdditionalProperties` off this message, so
    /// dropping them would blank the reasoning column for exactly the turns
    /// that misbehaved.
    /// </summary>
    [Fact]
    public void Prune_KeepsText_Order_AndAdditionalProperties()
    {
        var msg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent("thinking"),
            new FunctionCallContent("keep", "t", new Dictionary<string, object?>()),
            new FunctionCallContent("drop", "t", new Dictionary<string, object?>()),
            new TextContent("trailing"),
        })
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoning"] = "because" },
        };

        var pruned = AgentLoop.PruneCollapsedCalls(msg, ["drop"]);

        Assert.Equal(3, pruned.Contents.Count);
        Assert.Equal("keep", pruned.Contents.OfType<FunctionCallContent>().Single().CallId);
        Assert.Equal(["thinking", "trailing"],
            pruned.Contents.OfType<TextContent>().Select(t => t.Text).ToArray());
        Assert.Equal("because", pruned.AdditionalProperties?["reasoning"]);
        Assert.Equal(ChatRole.Assistant, pruned.Role);
    }

    /// <summary>
    /// A call with an EMPTY id is never collapsed (CollapseDuplicateToolCalls
    /// takes the `callId.Length == 0` branch and passes it straight through),
    /// so it must never be pruned either. The two rules have to agree, or a
    /// live call loses its result and the provider rejects the conversation.
    ///
    /// Null is deliberately NOT tested: it is unreachable. FunctionCallContent
    /// throws ArgumentNullException on a null callId, so the `is null` arm of
    /// the prune is defensive only -- asserting on it would be asserting on a
    /// state the type system already forbids.
    /// </summary>
    [Fact]
    public void Prune_NeverDropsACallWithAnEmptyId()
    {
        var msg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("", "t", new Dictionary<string, object?>()),
            new FunctionCallContent("drop", "t", new Dictionary<string, object?>()),
        });

        var pruned = AgentLoop.PruneCollapsedCalls(msg, ["drop"]);

        var kept = Assert.Single(pruned.Contents.OfType<FunctionCallContent>());
        Assert.Equal("", kept.CallId);
    }
}
