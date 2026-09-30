using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// Law 251 (EpicForge batch 18, s18a it.18, VETT 6fe9e6b; the same shape on
/// s17b it.52, s17d it.12/22, s17e it.31, s17f it.26, s18h it.8/17): a reply
/// carrying `rm -f src/facts/facts-01.js` and, beside it, `create
/// src/facts/facts-01.js` ran both through Task.WhenAll. The create finished
/// FIRST (0 ms, `Error: file_exists`), the rm landed 52 ms later, and the
/// seat's banked facts file was gone from the tree. The seat wrote the
/// calls in the only order that works; the harness ran them in the order
/// the thread pool happened to pick.
///
/// Sibling calls that touch the workspace (`terminal`, `file_editor`, or any
/// tool outside the leader's fan-out set) now run one at a time in wire
/// order. A turn whose siblings are ALL leader tools (assign_*, check_*,
/// wait_task, *_dispatch, declare_done) still fans out, because that
/// concurrency is what "10-20 members under a manager" is made of.
/// </summary>
public class SiblingWireOrderTests
{
    /// <summary>Records the order the sandbox was reached in. `terminal` takes
    /// longer than `create` on purpose: under Task.WhenAll the create reaches
    /// the sandbox first even though the seat sent it second.</summary>
    private sealed class OrderSandbox : ISandbox
    {
        public readonly ConcurrentQueue<string> Order = new();
        public string Cwd => "/fake";
        public async Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
        {
            await Task.Delay(150, ct);
            Order.Enqueue("terminal:" + cmd);
            return new BashResult("", 0, "/fake", false);
        }
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("1\tx\n");
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default)
        {
            Order.Enqueue("create:" + p);
            return Task.FromResult("File created successfully");
        }
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string n, CancellationToken ct = default) => Task.FromResult(("edited", (string?)null));
        public Task<string> FileInsertAsync(string s, string p, int l, string n, CancellationToken ct = default) => Task.FromResult("edited");
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("undone");
        public Task SessionCreateAsync(string n, string cwd, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string cwd) => this;
        public ISandbox WithDispatchWorktree(string newCwd, string root) => this;
    }

    /// <summary>Replays scripted assistant messages, then repeats the last one.</summary>
    private sealed class ScriptedClient(params ChatMessage[] script) : IChatClient
    {
        private int _n;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var msg = script[Math.Min(_n, script.Length - 1)];
            _n++;
            return Task.FromResult(new ChatResponse([new ChatMessage(msg.Role, msg.Contents.ToList())]));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatMessage Text(string s) => new(ChatRole.Assistant, s);

    private static ChatMessage Calls(params FunctionCallContent[] calls) =>
        new(ChatRole.Assistant, calls.Cast<AIContent>().ToList());

    private static FunctionCallContent Fc(string id, string name, Dictionary<string, object?> args) => new(id, name, args);

    private static AgentCapabilities Caps(Dictionary<string, ToolFn> tools) =>
        new(tools, new List<JsonElement>(), new List<MiddlewareFn>());

    [Fact]
    public async Task Siblings_ThatTouchTheWorkspace_RunOneAtATimeInWireOrder()
    {
        var events = new ConcurrentQueue<Event>();
        var sb = new OrderSandbox();
        var client = new ScriptedClient(
            Calls(
                Fc("call-1", "terminal", new() { ["command"] = "rm -f src/facts/facts-01.js" }),
                Fc("call-2", "file_editor", new() { ["command_name"] = "create", ["path"] = "src/facts/facts-01.js", ["file_text"] = "export const FACTS_01 = [];\n" })),
            Text("done"));
        var env = new AgentEnvironment(sb, "sess", 5, events.Enqueue);

        await AgentLoop.RunAsync(new LlmSettings(client, "test-model"), Caps(Builtins.All()), env, "sys", "go");

        // The sandbox saw the calls in the order the seat wrote them.
        Assert.Equal(
            new[] { "terminal:rm -f src/facts/facts-01.js", "create:src/facts/facts-01.js" },
            sb.Order.ToArray());

        // ...and the wire says so too: call-1 ended before call-2 started.
        var seq = events.Where(e => e.Type is "tool_call_start" or "tool_call_end")
            .Select(e => e.Type + ":" + e.Data["call_id"]).ToList();
        Assert.Equal(
            new[] { "tool_call_start:call-1", "tool_call_end:call-1", "tool_call_start:call-2", "tool_call_end:call-2" },
            seq);
    }

    [Fact]
    public async Task Siblings_ThatAreAllLeaderTools_StillFanOut()
    {
        var entered = new[] { new TaskCompletionSource(), new TaskCompletionSource() };
        var overlapped = new bool[2];
        var tools = Builtins.All();
        for (var i = 0; i < 2; i++)
        {
            var me = i;
            tools[me == 0 ? "check_task" : "check_tasks"] = async (_, _, _, ct) =>
            {
                entered[me].SetResult();
                // Both siblings in flight at once ⇒ this completes at once;
                // one at a time ⇒ the other never enters until we return.
                var both = Task.WhenAll(entered[0].Task, entered[1].Task);
                overlapped[me] = ReferenceEquals(await Task.WhenAny(both, Task.Delay(3000, ct)), both);
                return "ok";
            };
        }
        var client = new ScriptedClient(
            Calls(
                Fc("call-1", "check_task", new() { ["task_id"] = "a" }),
                Fc("call-2", "check_tasks", new())),
            Text("done"));
        var env = new AgentEnvironment(new OrderSandbox(), "sess", 5);

        await AgentLoop.RunAsync(new LlmSettings(client, "test-model"), Caps(tools), env, "sys", "go");

        Assert.True(overlapped[0] && overlapped[1], "leader siblings must still run concurrently");
    }
}
