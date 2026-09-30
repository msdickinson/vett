using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// ⭐⭐ A CAP HIT CAN ARRIVE AS `finish_reason: tool_calls` WITH A TOOL CALL
/// WHOSE ARGUMENTS WERE CUT.
///
/// THE DEFECT (measured 2026-09-05, EpicForge run 5 `E-mtntns7a3ap3`,
/// deepseek-v4-flash on vLLM, six replies on two vett builds). At exactly the
/// output cap -- 4096 on `R-mto0ygih334s` replies 18 and 20 and
/// `R-mto0yl506y14` reply 26; 12288 on the law-136 build's `R-mto1wbrod2us`
/// reply 13, `R-mto1wdxw7oyq` reply 18 and `R-mto1wg3b88oh` reply 8 -- the
/// reply's LAST content part is a tool call whose `arguments` is null. The
/// cap cut the generation INSIDE the JSON of the call, the engine's tool
/// parser gave up, and the response came back `finish_reason: tool_calls`,
/// not `length`. The existing recovery is gated on `Length &amp;&amp; no calls`
/// (TruncatedResponseRecoveryTests) on the 09-01 premise that a truncated
/// turn carrying a call is usable; this is the shape that premise did not
/// cover.
///
/// WHAT IT COST. The loop ran the call with `{}`: `terminal` answered
/// "Error: command is required", `file_editor` answered "unknown file_editor
/// command" (0 ms), and the model -- told nothing about the cap -- re-emitted
/// the same ~8k-byte heredoc (identical argument hash in consecutive replies).
/// At ~17 tokens/s per seat a 12288-token cut reply is ~700 s of an 1800 s
/// seat; all three law-136 runs spent one such reply and were cancelled at
/// 1180 s having landed nothing. Reply 8 above generated ~12,250 tokens of
/// arguments for what its own prose called "the first chunk" of a 100-line
/// file -- and nothing recorded how long those arguments were or whether they
/// were repeating, because the loop never looked at the raw bytes.
///
/// THE RULE UNDER TEST: a call whose arguments never arrived is NOT run, the
/// model is told what cut them, complete siblings in the same reply still
/// run, and a reply whose only call was cut is a truncation with nothing
/// usable and takes the recovery path. These tests drive the REAL AgentLoop
/// through a scripted client that reports usage, so they exercise the shipped
/// path, and they read the events and the NEXT request as the model saw it.
/// </summary>
public class TruncatedToolCallTests
{
    /// <summary>Replays scripted assistant messages and, unlike the older
    /// ScriptedClient, reports an output token count -- the signal the fix
    /// keys on when the provider says `tool_calls` instead of `length`.</summary>
    private sealed class CappedClient(ChatFinishReason? finishReason, int outputTokens, params ChatMessage[] script) : IChatClient
    {
        private int _n;
        public List<List<ChatMessage>> Sent { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add(messages.ToList());
            var msg = script[Math.Min(_n, script.Length - 1)];
            _n++;
            return Task.FromResult(new ChatResponse([new ChatMessage(msg.Role, msg.Contents.ToList())])
            {
                FinishReason = finishReason,
                Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = outputTokens },
            });
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

    /// <summary>Three fake tools that count their runs: two with required
    /// parameters (the live shape) and one with none (the control that an
    /// empty call to it is a COMPLETE call).</summary>
    private sealed class Rig
    {
        public int FileEditorRuns;
        public int TerminalRuns;
        public int ListFilesRuns;
        public Dictionary<string, object?>? LastFileEditorArgs;
        /// <summary>Law 236: what file_editor answers -- "ran" by default, or the tool's own refusal.</summary>
        public Func<Dictionary<string, object?>, string> FileEditorReply = _ => "ran";
        public AgentCapabilities Caps { get; }

        public Rig()
        {
            var tools = new Dictionary<string, ToolFn>
            {
                ["file_editor"] = (args, _, _, _) => { Interlocked.Increment(ref FileEditorRuns); LastFileEditorArgs = args; return Task.FromResult(FileEditorReply(args)); },
                ["terminal"] = (_, _, _, _) => { Interlocked.Increment(ref TerminalRuns); return Task.FromResult("ran"); },
                ["list_files"] = (_, _, _, _) => { Interlocked.Increment(ref ListFilesRuns); return Task.FromResult("a.js b.js"); },
            };
            Caps = new AgentCapabilities(tools, Schemas(), []);
        }

        private static List<JsonElement> Schemas()
        {
            var fileEditor = JsonDocument.Parse("""
            {"type":"function","function":{"name":"file_editor","description":"Edit files.",
             "parameters":{"type":"object","properties":{"command":{"type":"string"},"path":{"type":"string"},"file_text":{"type":"string"}},
             "required":["command","path"]}}}
            """).RootElement;
            var terminal = JsonDocument.Parse("""
            {"type":"function","function":{"name":"terminal","description":"Run a command.",
             "parameters":{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}}}
            """).RootElement;
            var listFiles = JsonDocument.Parse("""
            {"type":"function","function":{"name":"list_files","description":"List files.",
             "parameters":{"type":"object","properties":{}}}}
            """).RootElement;
            return [fileEditor, terminal, listFiles];
        }
    }

    /// <summary>Law 236: a sandbox that records creates and refuses a path
    /// that already exists, the way DirectBash does (`file_exists`).</summary>
    private sealed class BankingSandbox : ISandbox
    {
        public readonly List<(string Path, string Text)> Created = [];
        public readonly HashSet<string> Existing = [];
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("", 0, "/fake", false));
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default)
        {
            if (Existing.Contains(p) || Created.Any(c => c.Path == p)) throw new InvalidOperationException("file_exists: " + p);
            Created.Add((p, f));
            return Task.FromResult("created");
        }
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileInsertAsync(string s, string p, int l, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SessionCreateAsync(string n, string cwd, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string cwd) => this;
        public ISandbox WithDispatchWorktree(string newCwd, string root) => this;
    }

    private sealed record Run(CappedClient Client, List<Event> Events, AgentResult Result, Rig Rig);

    private static Task<Run> RunAsync(ChatFinishReason? finish, int outputTokens, int? cap, int maxIters, params ChatMessage[] script)
        => RunAsync(new InertSandbox(), new Rig(), finish, outputTokens, cap, maxIters, script);

    private static async Task<Run> RunAsync(ISandbox sandbox, Rig rig, ChatFinishReason? finish, int outputTokens, int? cap, int maxIters, params ChatMessage[] script)
    {
        var events = new ConcurrentQueue<Event>();
        var client = new CappedClient(finish, outputTokens, script);
        var env = new AgentEnvironment(sandbox, "sess", maxIters, events.Enqueue);
        var result = await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model", MaxOutputTokens: cap), rig.Caps, env, "sys", "go");
        return new Run(client, events.ToList(), result, rig);
    }

    // ---- reply shapes, drawn from the transcripts -------------------------

    /// <summary>The adapter's product for a call the cap cut mid-JSON: the
    /// parser threw, so Arguments is null and the failure sits in .Exception;
    /// RawRepresentation carries what did arrive.</summary>
    private static FunctionCallContent Cut(string callId, string name, object? raw)
    {
        var fc = FunctionCallContent.CreateFromParsedArguments<string>(
            raw?.ToString() ?? "", callId, name,
            static _ => throw new JsonException("Expected end of string, but instead reached end of data."));
        fc.RawRepresentation = raw;
        return fc;
    }

    private static FunctionCallContent Complete(string callId, string name, params (string, object?)[] args)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in args) d[k] = v;
        return new FunctionCallContent(callId, name, d);
    }

    private static ChatMessage Assistant(params AIContent[] parts) => new(ChatRole.Assistant, parts.ToList());

    /// <summary>Reply 8 of R-mto1wg3b88oh, abbreviated: 110 characters of
    /// prose, then the call the cap cut.</summary>
    private const string FirstChunkProse =
        "I'll rewrite the file completely with exactly 100 unique facts. Let me create the file with the first chunk.";

    /// <summary>A degenerate tail: the same line ~700 times. Deflates to a
    /// few percent of its size.</summary>
    private static readonly string RepeatingRaw =
        "{\"command\":\"create\",\"path\":\"src/facts/facts-04.js\",\"file_text\":\"export const facts = [\\n"
        + string.Concat(Enumerable.Repeat("  \\\"Saturn has more than 140 known moons.\\\",\\n", 700));

    /// <summary>Distinct content of the same length class: word salad from a
    /// seeded generator, so the test is deterministic and the tail does NOT
    /// look like a loop.</summary>
    private static readonly string DistinctRaw = BuildDistinctRaw();

    private static string BuildDistinctRaw()
    {
        string[] words =
        [
            "saturn", "jupiter", "comet", "nebula", "orbit", "rover", "probe", "lander", "crater", "asteroid",
            "europa", "titan", "ganymede", "callisto", "ring", "gravity", "launch", "rocket", "booster", "signal",
            "telescope", "mission", "moon", "first", "observed", "measured", "photo", "flyby", "series", "voice",
            "radio", "planet", "sensor", "vehicle", "thruster", "belt", "shield", "panel", "antenna", "capsule",
        ];
        var rng = new Random(7);
        var sb = new StringBuilder("{\"command\":\"create\",\"path\":\"src/facts/facts-04.js\",\"file_text\":\"export const facts = [\\n");
        while (sb.Length < RepeatingRaw.Length)
        {
            sb.Append("  \\\"");
            for (var w = 0; w < 9; w++) sb.Append(words[rng.Next(words.Length)]).Append(rng.Next(1000)).Append(' ');
            sb.Append("\\\",\\n");
        }
        return sb.ToString();
    }

    private static List<Event> Of(Run r, string type) => r.Events.Where(e => e.Type == type).ToList();
    private static int Int(object? o) => Convert.ToInt32(o);
    private static string LastUserText(List<ChatMessage> request) => request.Last(m => m.Role == ChatRole.User).Text;

    // ---- the defect --------------------------------------------------------

    [Fact]
    public async Task ACutTailCallAtTheCap_IsNotRun_AndTheModelIsToldWhatCutIt()
    {
        var cutReply = Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", RepeatingRaw));
        var healthy = Assistant(Complete("c2", "file_editor", ("command", "create"), ("path", "src/facts/facts-04.js"), ("file_text", "export const facts = [];")));

        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 3, cutReply, healthy);

        // The event, and it says what it saw.
        var cut = Assert.Single(Of(run, "truncated_tool_call"));
        Assert.Equal("file_editor", cut.Data["tool_name"]);
        Assert.Equal("c1", cut.Data["call_id"]);
        Assert.Equal(12288, Int(cut.Data["output_tokens"]));
        Assert.Equal(12288, Int(cut.Data["output_cap"]));
        Assert.Equal(0, Int(cut.Data["sibling_calls"]));
        Assert.Equal(RepeatingRaw.Length, Int(cut.Data["raw_argument_chars"]));
        Assert.True(Int(cut.Data["tail_compression_pct"]) < AgentLoop.RepeatingTailCompressionPct,
            "a 700x repeated line must read as repeating: " + cut.Data["tail_compression_pct"] + "%");
        Assert.Contains("end of data", cut.Data["parse_error"]?.ToString());

        // The cut call did NOT run: no tool_call_end for c1 -- an event that
        // said it ran would make every tool count lie.
        Assert.DoesNotContain(run.Events, e => e.Type == "tool_call_end" && e.Data.TryGetValue("call_id", out var id) && (string?)id == "c1");
        // The healthy follow-up did run, so "not run" is about the cut call only.
        Assert.True(run.Rig.FileEditorRuns >= 1);

        // The only call was cut, so the reply was a truncation with nothing
        // usable: the recovery path, with the reply dropped and a nudge that
        // names the tool, the cap and the loop.
        Assert.Contains(run.Events, e => e.Type == "truncated_response_recovery");
        var second = run.Client.Sent[1];
        var nudge = LastUserText(second);
        Assert.Contains("file_editor", nudge);
        Assert.Contains("12288", nudge);
        Assert.Contains("cut off", nudge);
        Assert.Contains(RepeatingRaw.Length.ToString(), nudge);
        Assert.Contains("REPEATING", nudge);
        Assert.DoesNotContain(second, m => m.Role == ChatRole.Assistant && m.Text.Contains("first chunk"));
    }

    // ---- the controls that make it mean something -------------------------

    /// <summary>Same cap, same finish, same tool -- complete arguments. This
    /// is the healthy at-cap reply the 09-01 measurement described, and it
    /// must run untouched: a fix that re-rolled every at-cap call would throw
    /// away real work.</summary>
    [Fact]
    public async Task ACompleteCallAtTheCap_RunsUntouched()
    {
        var reply = Assistant(new TextContent(FirstChunkProse),
            Complete("c1", "file_editor", ("command", "create"), ("path", "src/facts/facts-04.js"), ("file_text", "export const facts = [];")));

        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 2, reply);

        Assert.Empty(Of(run, "truncated_tool_call"));
        Assert.DoesNotContain(run.Events, e => e.Type == "truncated_response_recovery");
        Assert.Contains(run.Events, e => e.Type == "tool_call_end" && (string?)e.Data["call_id"] == "c1");
        Assert.True(run.Rig.FileEditorRuns >= 1);
    }

    /// <summary>An argument-less call BELOW the cap was not cut by anything;
    /// it is the model's own mistake and the tool's error message is the
    /// truthful answer. The cap is the trigger, not the empty arguments.</summary>
    [Fact]
    public async Task AnArgumentlessCallBelowTheCap_IsAnOrdinaryCall()
    {
        var reply = Assistant(new TextContent("Let me check."), Cut("c1", "file_editor", ""));

        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 100, cap: 12288, maxIters: 2, reply);

        Assert.Empty(Of(run, "truncated_tool_call"));
        Assert.Contains(run.Events, e => e.Type == "tool_call_end" && (string?)e.Data["call_id"] == "c1");
    }

    /// <summary>Reply 18 of R-mto1wdxw7oyq: a complete str_replace, a complete
    /// rm, then the cut call. The complete calls are real work and run; the
    /// cut one gets a result that says it did not (every call_id must have
    /// exactly one result -- the API contract) and the reply stays in history
    /// because most of it was usable. The cut tail here is REPEATING, the
    /// shape that is never salvaged (distinct content is: see the salvage
    /// tests below).</summary>
    [Fact]
    public async Task CompleteSiblingsStillRun_AndTheCutCallGetsATruthfulResult()
    {
        var reply = Assistant(
            new TextContent("Delete and recreate with all 100 facts."),
            Complete("c1", "file_editor", ("command", "str_replace"), ("path", "src/facts/facts-03.js"), ("old_str", "a"), ("new_str", "b")),
            Complete("c2", "terminal", ("command", "rm -f src/facts/facts-03.js")),
            Cut("c3", "file_editor", RepeatingRaw));
        var healthy = Assistant(Complete("c4", "terminal", ("command", "ls src/facts")));

        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 3, reply, healthy);

        var cut = Assert.Single(Of(run, "truncated_tool_call"));
        Assert.Equal("c3", cut.Data["call_id"]);
        Assert.Equal(2, Int(cut.Data["sibling_calls"]));
        Assert.Contains(run.Events, e => e.Type == "tool_call_end" && (string?)e.Data["call_id"] == "c1");
        Assert.Contains(run.Events, e => e.Type == "tool_call_end" && (string?)e.Data["call_id"] == "c2");
        Assert.DoesNotContain(run.Events, e => e.Type == "tool_call_end" && (string?)e.Data["call_id"] == "c3");
        Assert.DoesNotContain(run.Events, e => e.Type == "truncated_response_recovery");

        var second = run.Client.Sent[1];
        var c3Result = second.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == "c3").Result?.ToString() ?? "";
        Assert.Contains("NOT RUN", c3Result);
        Assert.Contains("cut off", c3Result);
        Assert.Contains("12288", c3Result);
        Assert.Contains(RepeatingRaw.Length.ToString(), c3Result);
        Assert.Contains("REPEATING", c3Result);
        Assert.Contains(second, m => m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "c3"));
        Assert.Null(cut.Data["salvaged"]);
    }

    /// <summary>A tool with no required parameters, called with none, at the
    /// cap, as the last part: that is a complete call, not a cut one.</summary>
    [Fact]
    public async Task AToolWithNoRequiredParameters_CalledEmptyAtTheCap_IsNotMistakenForACut()
    {
        var reply = Assistant(new TextContent("Listing."), new FunctionCallContent("c1", "list_files", new Dictionary<string, object?>()));

        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 2, reply);

        Assert.Empty(Of(run, "truncated_tool_call"));
        Assert.Contains(run.Events, e => e.Type == "tool_call_end" && (string?)e.Data["call_id"] == "c1");
        Assert.True(run.Rig.ListFilesRuns >= 1);
    }

    /// <summary>The provider CAN say `length` with a cut call attached, and a
    /// profile with no cap has no number to compare against -- the finish
    /// reason alone must carry it, and the notice must not invent a cap.</summary>
    [Fact]
    public async Task ALengthFinishWithACutTailCall_IsACut_EvenWithNoConfiguredCap()
    {
        var reply = Assistant(new TextContent(FirstChunkProse), Cut("c1", "terminal", "{\"command\":\"cat <<'EOF' > src/facts/facts-02.js"));

        var run = await RunAsync(ChatFinishReason.Length, outputTokens: 0, cap: null, maxIters: 2, reply);

        // The scripted model answers the nudge with the SAME cut reply (as the
        // live model did): each cut reply gets exactly one event, no more.
        var cuts = Of(run, "truncated_tool_call");
        Assert.Equal(2, cuts.Count);
        var cut = cuts[0];
        Assert.Equal("terminal", cut.Data["tool_name"]);
        Assert.Null(cut.Data["output_cap"]);
        Assert.Contains(run.Events, e => e.Type == "truncated_response_recovery");
        Assert.Equal(0, run.Rig.TerminalRuns);
        Assert.Contains("output-length limit", LastUserText(run.Client.Sent[1]));
    }

    /// <summary>The live adapter parks the OpenAI tool call in RawRepresentation;
    /// its FunctionArguments are the bytes that did arrive. The length in the
    /// event must be read from there, not guessed.</summary>
    [Fact]
    public async Task TheRawArgumentLength_IsReadFromTheOpenAIToolCall()
    {
        var toolCall = OpenAI.Chat.ChatToolCall.CreateFunctionToolCall("c1", "file_editor", BinaryData.FromString(DistinctRaw));
        var reply = Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", toolCall));

        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 2, reply);

        // Two iterations, the same cut reply both times: one event per cut reply.
        var cuts = Of(run, "truncated_tool_call");
        Assert.Equal(2, cuts.Count);
        var cut = cuts[0];
        Assert.Equal(DistinctRaw.Length, Int(cut.Data["raw_argument_chars"]));
        Assert.True(Int(cut.Data["tail_compression_pct"]) >= AgentLoop.RepeatingTailCompressionPct,
            "distinct content must not read as repeating: " + cut.Data["tail_compression_pct"] + "%");
        // ...and distinct content read from the adapter's bytes is salvaged.
        Assert.NotNull(cut.Data["salvaged"]);
        Assert.Contains(run.Events, e => e.Type == "tool_call_end" && (string?)e.Data["call_id"] == "c1");
    }

    // ---- a cut create banks its prefix (2026-09-09, EpicForge control 5) ----

    /// <summary>The decoded `file_text` that DistinctRaw carries, cut to its
    /// last complete line: what the salvage must write.</summary>
    private static string ExpectedSalvagedText(string raw)
    {
        var open = raw.IndexOf("\"file_text\":\"", StringComparison.Ordinal) + "\"file_text\":\"".Length;
        var (text, complete) = AgentLoop.DecodeJsonStringPrefix(raw, open);
        Assert.False(complete);
        return text[..(text.LastIndexOf('\n') + 1)];
    }

    /// <summary>Control 5's shape: the only call is a `create` whose
    /// `file_text` was cut mid-line after ~40 KB of distinct lines. The
    /// complete lines are written under the call's own id, the reply stays in
    /// history, the recovery path is NOT taken, and the model is told the
    /// file stops at line N and to append, never re-create.</summary>
    [Fact]
    public async Task ACutCreateWithDistinctContent_BanksItsCompleteLines()
    {
        // DistinctRaw ends on a line boundary; the cap falls mid-line, so cut it there.
        var raw = DistinctRaw[..^40];
        var cutReply = Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", raw));
        var healthy = Assistant(Complete("c2", "terminal", ("command", "ls src/facts")));

        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 3, cutReply, healthy);

        var expected = ExpectedSalvagedText(raw);
        var expectedLines = expected.Count(ch => ch == '\n');

        // The event names what was banked.
        var cut = Assert.Single(Of(run, "truncated_tool_call"));
        var salvaged = Assert.IsType<Dictionary<string, object?>>(cut.Data["salvaged"]);
        Assert.Equal("src/facts/facts-04.js", salvaged["path"]);
        Assert.Equal(expected.Length, Int(salvaged["chars"]));
        Assert.Equal(expectedLines, Int(salvaged["lines"]));
        Assert.True(Int(salvaged["dropped_chars"]) > 0, "the cut landed mid-line, so something after the last newline was dropped");
        Assert.False((bool)salvaged["complete"]!);

        // The create RAN, under the cut call's own id, with exactly the
        // complete lines and nothing after them.
        Assert.Contains(run.Events, e => e.Type == "tool_call_end" && (string?)e.Data["call_id"] == "c1");
        var args = run.Rig.LastFileEditorArgs;
        Assert.NotNull(args);
        Assert.Equal("create", args["command"]?.ToString());
        Assert.Equal("src/facts/facts-04.js", args["path"]?.ToString());
        Assert.Equal(expected, args["file_text"]?.ToString());
        Assert.EndsWith("\n", expected);
        Assert.StartsWith("export const facts = [\n", expected);
        Assert.Contains(run.Events, e => e.Type == "truncated_tool_call_salvaged" && Int(e.Data["lines"]) == expectedLines);
        // Law 236: the tool accepted it, and the event says so.
        var salvagedEv = Assert.Single(Of(run, "truncated_tool_call_salvaged"));
        Assert.True((bool)salvagedEv.Data["landed"]!);
        Assert.Null(salvagedEv.Data["side_path"]);

        // Not the recovery path: the reply and its call stay in history and
        // the next request carries the notice, not the discard nudge.
        Assert.DoesNotContain(run.Events, e => e.Type == "truncated_response_recovery");
        var second = run.Client.Sent[1];
        Assert.Contains(second, m => m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "c1"));
        var c1Result = second.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == "c1").Result?.ToString() ?? "";
        Assert.DoesNotContain("NOT RUN", c1Result);
        var notice = LastUserText(second);
        Assert.Contains("SALVAGED", notice);
        Assert.Contains("src/facts/facts-04.js", notice);
        Assert.Contains("first " + expectedLines + " complete lines", notice);
        Assert.Contains("continue from line " + (expectedLines + 1), notice);
        Assert.Contains("Do NOT re-create", notice);
        Assert.Contains("12288", notice);
    }

    /// <summary>A repeating tail is a loop, not content: not salvaged (the
    /// first test above already proves the discard; this pins the field).</summary>
    [Fact]
    public async Task ACutCreateWithARepeatingTail_IsNotSalvaged()
    {
        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 1,
            Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", RepeatingRaw)));

        var cut = Assert.Single(Of(run, "truncated_tool_call"));
        Assert.Null(cut.Data["salvaged"]);
        Assert.Equal(0, run.Rig.FileEditorRuns);
        Assert.Contains(run.Events, e => e.Type == "truncated_response_recovery");
    }

    /// <summary>A cut `terminal` heredoc is never salvaged: a partial command
    /// must not run, whatever its tail looks like.</summary>
    [Fact]
    public async Task ACutTerminalCall_IsNeverSalvaged()
    {
        var raw = "{\"command\":\"cat <<'EOF' > src/facts/facts-02.js\\nexport const facts = [\\n" + DistinctRaw[(DistinctRaw.IndexOf("[\\n", StringComparison.Ordinal) + 3)..];
        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 1,
            Assistant(new TextContent(FirstChunkProse), Cut("c1", "terminal", raw)));

        var cut = Assert.Single(Of(run, "truncated_tool_call"));
        Assert.Null(cut.Data["salvaged"]);
        Assert.Equal(0, run.Rig.TerminalRuns);
    }

    /// <summary>No complete line arrived: nothing to bank, the old path.</summary>
    [Fact]
    public async Task ACutCreateWithNoCompleteLine_IsNotSalvaged()
    {
        var raw = "{\"command\":\"create\",\"path\":\"src/engine/board.js\",\"file_text\":\"export function createBoard(w, h) { return Array.from({ length: h }, () => Array(w).fill(0)); } export function";
        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 1,
            Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", raw)));

        var cut = Assert.Single(Of(run, "truncated_tool_call"));
        Assert.Null(cut.Data["salvaged"]);
        Assert.Equal(0, run.Rig.FileEditorRuns);
    }

    /// <summary>`path` after the cut (or cut itself) is no path: nothing can be
    /// written to a name that never arrived.</summary>
    [Fact]
    public async Task ACutCreateWhosePathNeverArrived_IsNotSalvaged()
    {
        var raw = "{\"command\":\"create\",\"file_text\":\"line one\\nline two\\n\",\"path\":\"src/eng";
        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 1,
            Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", raw)));

        var cut = Assert.Single(Of(run, "truncated_tool_call"));
        Assert.Null(cut.Data["salvaged"]);
        Assert.Equal(0, run.Rig.FileEditorRuns);
    }

    /// <summary>The string closed before the cut (the cap fell in a later
    /// argument): the whole file_text is banked, marked complete.</summary>
    [Fact]
    public async Task ACutCreateWhoseFileTextClosed_IsBankedInFull()
    {
        var raw = "{\"command\":\"create\",\"path\":\"src/a.js\",\"file_text\":\"const a = \\\"x\\\";\\nexport default a;\",\"security_ri";
        var run = await RunAsync(ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 2,
            Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", raw)),
            Assistant(Complete("c2", "terminal", ("command", "ls src"))));

        var cut = Assert.Single(Of(run, "truncated_tool_call"));
        var salvaged = Assert.IsType<Dictionary<string, object?>>(cut.Data["salvaged"]);
        Assert.True((bool)salvaged["complete"]!);
        Assert.Equal(0, Int(salvaged["dropped_chars"]));
        Assert.Equal("const a = \"x\";\nexport default a;", run.Rig.LastFileEditorArgs?["file_text"]?.ToString());
        Assert.Contains("created in full", LastUserText(run.Client.Sent[1]));
    }

    /// <summary>The JSON escapes a model writes inside file_text decode to the
    /// bytes the file must hold; a trailing half-escape is dropped, not kept
    /// as garbage.</summary>
    [Fact]
    public void DecodeJsonStringPrefix_DecodesEscapes_AndDropsATrailingHalfEscape()
    {
        var (text, complete) = AgentLoop.DecodeJsonStringPrefix("a\\\"b\\\\c\\/d\\ne\\tf\\u0041g\\u00", 0);
        Assert.False(complete);
        Assert.Equal("a\"b\\c/d\ne\tfAg", text);

        var (closed, done) = AgentLoop.DecodeJsonStringPrefix("x\\ny\",\"security_risk\":\"LOW\"}", 0);
        Assert.True(done);
        Assert.Equal("x\ny", closed);

        var (half, notDone) = AgentLoop.DecodeJsonStringPrefix("abc\\", 0);
        Assert.False(notDone);
        Assert.Equal("abc", half);
    }

    // ---- LAW 236: the tool can refuse the salvaged call (s10a it.20, 2026-09-09) ----

    private const string ExistsError = "Error: file_editor `create` failed -- `src/facts/facts-04.js` already exists. To MODIFY an existing file, use `str_replace` instead.";

    private static string LastSalvageNotice(Run run)
        => run.Client.Sent[1].Where(m => m.Role == ChatRole.User).Select(m => m.Text ?? "").Last(s => s.Contains("SALVAGE"));

    /// <summary>s10a it.20: the seat re-`create`d a file that already existed,
    /// the cap cut it at 735 honest lines, the salvage replayed the create,
    /// and the sandbox answered `file_exists` -- while the notice said the
    /// lines "were written". Now: the verdict is read, the text is banked
    /// beside the target, and the notice says nothing landed and where the
    /// text is.</summary>
    [Fact]
    public async Task ARefusedSalvage_BanksItsTextAsideAndSaysNothingLanded()
    {
        var raw = DistinctRaw[..^40];
        var cutReply = Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", raw));
        var healthy = Assistant(Complete("c2", "terminal", ("command", "ls src/facts")));
        var sandbox = new BankingSandbox();
        sandbox.Existing.Add("src/facts/facts-04.js");
        var rig = new Rig { FileEditorReply = _ => ExistsError };

        var run = await RunAsync(sandbox, rig, ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 3, cutReply, healthy);

        var expected = ExpectedSalvagedText(raw);
        var ev = Assert.Single(Of(run, "truncated_tool_call_salvaged"));
        Assert.False((bool)ev.Data["landed"]!);
        Assert.Equal("src/facts/facts-04.js.salvaged", ev.Data["side_path"]);
        Assert.Contains("already exists", (string?)ev.Data["tool_error"]);

        // The text survived, byte for byte, beside its target -- and the target was not touched.
        var banked = Assert.Single(sandbox.Created);
        Assert.Equal("src/facts/facts-04.js.salvaged", banked.Path);
        Assert.Equal(expected, banked.Text);

        // The seat is told the truth: nothing landed, here is the tool's word, here is the text.
        var notice = LastSalvageNotice(run);
        Assert.StartsWith("SALVAGE REFUSED", notice);
        Assert.Contains("NOTHING landed in `src/facts/facts-04.js`", notice);
        Assert.Contains("src/facts/facts-04.js.salvaged", notice);
        Assert.Contains("already exists", notice);
        Assert.DoesNotContain("were written to", notice);
        Assert.Contains("Do NOT regenerate", notice);
    }

    /// <summary>The side name is taken too: the second name carries the call
    /// id's tail, and the notice names THAT file.</summary>
    [Fact]
    public async Task ARefusedSalvage_WhoseSideNameIsTaken_UsesTheCallIdName()
    {
        var raw = DistinctRaw[..^40];
        var cutReply = Assistant(new TextContent(FirstChunkProse), Cut("call_abc123", "file_editor", raw));
        var healthy = Assistant(Complete("c2", "terminal", ("command", "ls src/facts")));
        var sandbox = new BankingSandbox();
        sandbox.Existing.Add("src/facts/facts-04.js");
        sandbox.Existing.Add("src/facts/facts-04.js.salvaged");
        var rig = new Rig { FileEditorReply = _ => ExistsError };

        var run = await RunAsync(sandbox, rig, ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 3, cutReply, healthy);

        var ev = Assert.Single(Of(run, "truncated_tool_call_salvaged"));
        Assert.Equal("src/facts/facts-04.js.salvaged-abc123", ev.Data["side_path"]);
        Assert.Single(sandbox.Created);
        Assert.Contains("src/facts/facts-04.js.salvaged-abc123", LastSalvageNotice(run));
    }

    /// <summary>No sandbox can write the side file: the notice says LOST
    /// rather than pointing at a file that does not exist.</summary>
    [Fact]
    public async Task ARefusedSalvage_ThatCannotBeBanked_SaysTheLinesAreLost()
    {
        var raw = DistinctRaw[..^40];
        var cutReply = Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", raw));
        var healthy = Assistant(Complete("c2", "terminal", ("command", "ls src/facts")));
        var rig = new Rig { FileEditorReply = _ => ExistsError };

        var run = await RunAsync(new InertSandbox(), rig, ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 3, cutReply, healthy);

        var ev = Assert.Single(Of(run, "truncated_tool_call_salvaged"));
        Assert.False((bool)ev.Data["landed"]!);
        Assert.Null(ev.Data["side_path"]);
        var notice = LastSalvageNotice(run);
        Assert.Contains("LOST", notice);
        Assert.DoesNotContain(".salvaged", notice);
    }

    /// <summary>2026-09-09 13:26Z, s12a it.19 (batch 12, VETT 09d0873): a
    /// sandbox that can be VIEWED, so the landing check reads bytes. The
    /// target holds the 22 lines a law-233 salvage banked (no closing
    /// bracket) and refuses every write.</summary>
    private sealed class ViewingSandbox : ISandbox
    {
        public readonly Dictionary<string, string> Files = [];
        public readonly List<(string Path, string Text)> Created = [];
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("", 0, "/fake", false));
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default)
            => Files.TryGetValue(p, out var v) ? Task.FromResult(v) : throw new System.IO.FileNotFoundException("file_not_found: " + p);
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default)
        {
            if (Files.ContainsKey(p)) throw new InvalidOperationException("file_exists: " + p);
            Files[p] = f;
            Created.Add((p, f));
            return Task.FromResult("created");
        }
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileInsertAsync(string s, string p, int l, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SessionCreateAsync(string n, string cwd, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string cwd) => this;
        public ISandbox WithDispatchWorktree(string newCwd, string root) => this;
    }

    /// <summary>The wire text of s12a it.19's tool result: DirectBash's
    /// refusal as it was spelled then -- no `Error:` prefix.</summary>
    private const string NoMatchUnprefixed =
        "str_replace_no_match: not found in src/facts/facts-04.js. Do not retype the anchor from memory - view the exact region first";

    /// <summary>DistinctRaw re-shaped as the str_replace the seat sent after a
    /// law-233 salvage: an anchor of `];` (a line the banked file never had)
    /// and a long new_str the cap cuts.</summary>
    private static string DistinctStrReplaceRaw()
    {
        const string createHead = "{\"command\":\"create\",\"path\":\"src/facts/facts-04.js\",\"file_text\":\"export const facts = [\\n";
        Assert.StartsWith(createHead, DistinctRaw);
        return "{\"command\":\"str_replace\",\"path\":\"src/facts/facts-04.js\",\"old_str\":\"];\",\"new_str\":\"" + DistinctRaw[createHead.Length..];
    }

    /// <summary>s12a it.19: the salvaged str_replace was answered
    /// `str_replace_no_match` -- spelled WITHOUT `Error:`, so the loop's
    /// success bit stayed true -- and the event said `landed:true` over a
    /// file whose bytes had not moved (mtime 13:15:34Z, 22 lines). Now the
    /// bytes are read: unchanged means not landed, the text is banked aside,
    /// and the seat is told nothing landed.</summary>
    [Fact]
    public async Task ARefusedStrReplaceSalvage_WithoutAnErrorPrefix_IsNotLanded()
    {
        var raw = DistinctStrReplaceRaw()[..^40];
        var cutReply = Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", raw));
        var healthy = Assistant(Complete("c2", "terminal", ("command", "ls src/facts")));
        var sandbox = new ViewingSandbox();
        const string banked22 = "// Exactly 100 Saturn facts.\nexport const FACTS_01 = [\n  \"Saturn is the world's greatest detective.\",\n";
        sandbox.Files["src/facts/facts-04.js"] = banked22;
        var rig = new Rig { FileEditorReply = _ => NoMatchUnprefixed };

        var run = await RunAsync(sandbox, rig, ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 3, cutReply, healthy);

        var ev = Assert.Single(Of(run, "truncated_tool_call_salvaged"));
        Assert.Equal("str_replace", ev.Data["command"]);
        Assert.True((bool)ev.Data["tool_success"]!, "the tool text carried no Error: prefix, so the success bit is the lie under test");
        Assert.False((bool)ev.Data["bytes_changed"]!);
        Assert.False((bool)ev.Data["landed"]!);
        Assert.Contains("str_replace_no_match", (string?)ev.Data["tool_error"]);
        Assert.Equal("src/facts/facts-04.js.salvaged", ev.Data["side_path"]);
        Assert.Equal(banked22, sandbox.Files["src/facts/facts-04.js"]);
        var side = Assert.Single(sandbox.Created);
        Assert.Equal("src/facts/facts-04.js.salvaged", side.Path);
        Assert.NotEmpty(side.Text);

        var notice = LastSalvageNotice(run);
        Assert.StartsWith("SALVAGE REFUSED", notice);
        Assert.Contains("NOTHING landed in `src/facts/facts-04.js`", notice);
        Assert.Contains("str_replace_no_match", notice);
        Assert.DoesNotContain("were written into", notice);
    }

    /// <summary>The control: the same salvage, and this time the tool moves
    /// the bytes. Landed, bytes_changed true, nothing banked aside.</summary>
    [Fact]
    public async Task AStrReplaceSalvage_ThatMovesTheBytes_IsLanded()
    {
        var raw = DistinctStrReplaceRaw()[..^40];
        var cutReply = Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", raw));
        var healthy = Assistant(Complete("c2", "terminal", ("command", "ls src/facts")));
        var sandbox = new ViewingSandbox();
        sandbox.Files["src/facts/facts-04.js"] = "export const facts = [\n  \"one\",\n];\n";
        var rig = new Rig();
        rig.FileEditorReply = args =>
        {
            var path = (string)args["path"]!;
            sandbox.Files[path] = sandbox.Files[path].Replace("];", (string)args["new_str"]!);
            return "ran";
        };

        var run = await RunAsync(sandbox, rig, ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 3, cutReply, healthy);

        var ev = Assert.Single(Of(run, "truncated_tool_call_salvaged"));
        Assert.True((bool)ev.Data["tool_success"]!);
        Assert.True((bool)ev.Data["bytes_changed"]!);
        Assert.True((bool)ev.Data["landed"]!);
        Assert.Null(ev.Data["side_path"]);
        Assert.Empty(sandbox.Created);
        Assert.StartsWith("SALVAGED", LastSalvageNotice(run));
    }

    /// <summary>A cut create's notice says the file ends with NOTHING after
    /// the banked line, so the seat views before anchoring a str_replace
    /// (s12a's anchor included a `];` that was never written).</summary>
    [Fact]
    public async Task ASalvagedCreateNotice_SaysTheFileHasNoClosingLine()
    {
        var raw = DistinctRaw[..^40];
        var cutReply = Assistant(new TextContent(FirstChunkProse), Cut("c1", "file_editor", raw));
        var healthy = Assistant(Complete("c2", "terminal", ("command", "ls src/facts")));

        var run = await RunAsync(new InertSandbox(), new Rig(), ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 3, cutReply, healthy);

        var ev = Assert.Single(Of(run, "truncated_tool_call_salvaged"));
        Assert.True((bool)ev.Data["landed"]!);
        Assert.Null(ev.Data["bytes_changed"]);
        var notice = LastSalvageNotice(run);
        Assert.Contains("NOTHING after it, no closing bracket", notice);
        Assert.Contains("str_replace_no_match", notice);
    }
    /// <summary>2026-09-09 14:15Z, s13g it.8 (batch 13, VETT da4e8fa): the
    /// seat's reply carried a complete `create src/facts/facts-01.js` (20
    /// facts) AND, cut at the cap, a second create of the same path -- it had
    /// changed its mind mid-reply. The salvaged call was dispatched in the
    /// same parallel batch as its sibling, so the two RACED for the path
    /// (the sibling won; the salvage was refused `file_exists`), and the
    /// landing check's before-view, taken at parse time, saw no file, so the
    /// event said `bytes_changed:true` over a refusal: the SIBLING's bytes,
    /// attributed to the salvage. Now the siblings run first, the target is
    /// viewed, and the salvaged call runs alone: the sibling always wins,
    /// the salvage's own landing reads unchanged, and its text is banked
    /// aside.</summary>
    [Fact]
    public async Task ASalvage_RunsAfterItsSiblings_AndItsLandingCheckMeasuresOnlyItself()
    {
        var raw = DistinctRaw[..^40];
        const string siblingText = "export const facts = [\n  \"the sibling's twenty facts\",\n];\n";
        var cutReply = Assistant(
            new TextContent("Create facts-01 with block 1. Wait, let me redo it with distinct facts."),
            Complete("c1", "file_editor", ("command", "create"), ("path", "src/facts/facts-04.js"), ("file_text", siblingText)),
            Complete("c2", "terminal", ("command", "ls src/facts")),
            Cut("c3", "file_editor", raw));
        var healthy = Assistant(Complete("c4", "terminal", ("command", "ls src/facts")));
        var sandbox = new ViewingSandbox();
        var rig = new Rig();
        rig.FileEditorReply = args =>
        {
            var path = (string)args["path"]!;
            if (sandbox.Files.ContainsKey(path)) return "Error: file_exists: " + path;
            sandbox.Files[path] = (string)args["file_text"]!;
            return "created";
        };

        var run = await RunAsync(sandbox, rig, ChatFinishReason.ToolCalls, outputTokens: 12288, cap: 12288, maxIters: 3, cutReply, healthy);

        // The sibling's complete call landed, untouched by the salvage.
        Assert.Equal(siblingText, sandbox.Files["src/facts/facts-04.js"]);
        var ev = Assert.Single(Of(run, "truncated_tool_call_salvaged"));
        Assert.False((bool)ev.Data["tool_success"]!);
        Assert.Contains("file_exists", (string?)ev.Data["tool_error"]);
        // The salvage's own landing: its bytes did not move (the sibling's write is not its write).
        Assert.False((bool)ev.Data["bytes_changed"]!);
        Assert.False((bool)ev.Data["landed"]!);
        Assert.Equal("src/facts/facts-04.js.salvaged", ev.Data["side_path"]);
        var side = Assert.Single(sandbox.Created);
        Assert.Equal("src/facts/facts-04.js.salvaged", side.Path);
        // Order on the wire: every sibling's tool_call_end precedes the salvaged call's.
        var ends = run.Events.Where(e => e.Type == "tool_call_end").Select(e => (string?)e.Data["call_id"]).Take(3).ToList();
        Assert.Equal(3, ends.Count);
        Assert.Contains("c1", ends.Take(2));
        Assert.Contains("c2", ends.Take(2));
        Assert.Equal("c3", ends[2]);
        Assert.StartsWith("SALVAGE REFUSED", LastSalvageNotice(run));
    }
}
