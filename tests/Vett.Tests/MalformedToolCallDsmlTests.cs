using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// DEEPSEEK DSML CONTROL-TOKEN DRIFT — detection, attribution, and the
/// controls that prove the detector isn't just firing on everything.
///
/// THE DEFECT (measured 2026-08-26, team-fanout-tier2/fan5, ds-team-flash).
/// The leader wanted to poll five in-flight dispatches. Instead of five
/// structured calls it emitted them as TEXT:
///
///     I need to wait for the background tasks to complete. Let me check again.
///
///     &lt;｜DSML｜tool_calls&gt;
///     &lt;｜DSML｜invoke name="check_task"&gt;
///     &lt;｜DSML｜parameter name="task_id" string="true"&gt;implementer-1…
///
/// Every existing heuristic missed it, each for its own reason: the reply
/// STARTS WITH PROSE (so the structural-opener test sees 'I', not '&lt;'), the
/// marker vocabulary only knew qwen3_coder's &lt;tool_call&gt;/&lt;function=, and the
/// body is far past both the 100-token and 30-token caps.
///
/// The consequence is the part that matters: no event was emitted, so the
/// suite's `no_event: malformed_tool_call` assertion PASSED VACUOUSLY. The
/// gate named for this failure was blind to the only drift shape the
/// project's primary model family actually produces — an unmeasured failure
/// mode is indistinguishable from a healthy run.
///
/// These tests drive the REAL AgentLoop through a scripted client, so they
/// exercise the shipped detection path rather than a copy of it.
/// </summary>
public class MalformedToolCallDsmlTests
{
    // The exact text from the run, transcribed from the artifact's raw_text
    // field. `｜` is U+FF5C FULLWIDTH VERTICAL LINE, not an ASCII pipe — the
    // whole point is that this is a DeepSeek control-token spelling that
    // leaked into the content channel.
    private const string ObservedDsmlReply =
        "I need to wait for the background tasks to complete. Let me check again.\n\n" +
        "<｜DSML｜tool_calls>\n" +
        "<｜DSML｜invoke name=\"check_task\">\n" +
        "<｜DSML｜parameter name=\"task_id\" string=\"true\">implementer-1</｜DSML｜parameter>\n" +
        "</｜DSML｜invoke>\n" +
        "<｜DSML｜invoke name=\"check_task\">\n" +
        "<｜DSML｜parameter name=\"task_id\" string=\"true\">implementer-2</｜DSML｜parameter>\n" +
        "</｜DSML｜invoke>\n" +
        "</｜DSML｜tool_calls>";

    /// <summary>The same reply with the DSML block removed. This is the
    /// control: identical prose, identical length class, no marker.</summary>
    private const string ProseOnlyReply =
        "I need to wait for the background tasks to complete. Let me check again.";

    private sealed class ScriptedClient(params ChatMessage[] script) : IChatClient
    {
        private int _n;
        public int CallCount => _n;
        public List<ChatToolMode?> ToolModes { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ToolModes.Add(options?.ToolMode);
            var msg = script[Math.Min(_n, script.Length - 1)];
            _n++;
            return Task.FromResult(new ChatResponse(
                [new ChatMessage(msg.Role, msg.Contents.ToList())]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class InertSandbox : ISandbox
    {
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("", 0, "/fake", false));
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

    /// <summary>A registered `check_task` tool, so the detector's
    /// "only pin tool_choice to a REAL tool" guard has something to match.</summary>
    private static AgentCapabilities CapsWithCheckTask()
    {
        var schema = JsonDocument.Parse("""
        {
          "type": "function",
          "function": {
            "name": "check_task",
            "description": "Poll a dispatched task.",
            "parameters": {
              "type": "object",
              "properties": { "task_id": { "type": "string" } },
              "required": ["task_id"]
            }
          }
        }
        """).RootElement;

        var tools = new Dictionary<string, ToolFn>
        {
            ["check_task"] = (_, _, _, _) => Task.FromResult("running"),
        };
        return new AgentCapabilities(tools, [schema], []);
    }

    private static async Task<(ScriptedClient client, ConcurrentQueue<Event> events, AgentResult result)>
        RunAsync(string reply, int maxIters = 4)
    {
        var events = new ConcurrentQueue<Event>();
        var client = new ScriptedClient(new ChatMessage(ChatRole.Assistant, reply));
        var env = new AgentEnvironment(new InertSandbox(), "sess", maxIters, events.Enqueue);
        var result = await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model"), CapsWithCheckTask(), env, "sys", "go");
        return (client, events, result);
    }

    // ---- the defect --------------------------------------------------------

    [Fact]
    public async Task DsmlDrift_EmitsMalformedToolCall()
    {
        var (_, events, _) = await RunAsync(ObservedDsmlReply);

        Assert.Contains(events, e => e.Type == "malformed_tool_call");
    }

    /// <summary>
    /// THE CONTROL. Without this, a detector that fired on every prose reply
    /// would pass the test above and look like a fix. The marker must be what
    /// fires it — not the prose, not the length, not the absence of tool calls.
    /// </summary>
    [Fact]
    public async Task ProseWithoutDsml_DoesNotEmitMalformedToolCall()
    {
        var (_, events, _) = await RunAsync(ProseOnlyReply);

        Assert.DoesNotContain(events, e => e.Type == "malformed_tool_call");
    }

    /// <summary>
    /// Attribution: DSML names the intended tool inline, so recovery pins
    /// tool_choice to that specific function rather than guessing. A wrong pin
    /// would FORCE the wrong call, which is worse than no pin — so this
    /// asserts the name actually came through, not merely that something was
    /// forced.
    /// </summary>
    [Fact]
    public async Task DsmlDrift_PinsToolChoiceToTheNamedTool()
    {
        var (client, events, _) = await RunAsync(ObservedDsmlReply);

        var forced = events.Where(e => e.Type == "forced_tool_choice").ToList();
        Assert.NotEmpty(forced);
        Assert.Equal("check_task", forced[0].Data["tool_name"]);

        // And it reached the request the client actually received.
        Assert.Contains(client.ToolModes, m => m is RequiredChatToolMode r
            && r.RequiredFunctionName == "check_task");
    }

    /// <summary>
    /// The retry cap still bounds a model that never recovers. Four identical
    /// DSML replies exceed MalformedToolCallRetryCap (3), so the run must end
    /// on that reason rather than spinning to the iteration limit.
    /// </summary>
    [Fact]
    public async Task DsmlDrift_ThatNeverRecovers_StopsOnTheRetryCap()
    {
        var (client, _, result) = await RunAsync(ObservedDsmlReply, maxIters: 20);

        Assert.Equal("malformed_tool_call", result.StopReason);
        Assert.True(client.CallCount < 20,
            $"should stop on the retry cap, not the iteration limit (calls={client.CallCount})");
    }

    /// <summary>
    /// An ASCII pipe is NOT the DeepSeek control token. Prose that happens to
    /// discuss the marker — a bug report, a prompt telling the model not to
    /// emit it — must not be mistaken for the drift itself.
    /// </summary>
    [Fact]
    public async Task AsciiPipeLookalike_DoesNotTrigger()
    {
        var (_, events, _) = await RunAsync(
            "Do not write <|DSML|tool_calls> in your reply; use the tool channel.");

        Assert.DoesNotContain(events, e => e.Type == "malformed_tool_call");
    }
}
