using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// The malformed-tool-call heuristic against SHORT JSON ANSWERS.
///
/// AgentLoop treats a short reply (under 100 output tokens) that opens with
/// `&lt;`, `[`, `{` or a fence as a tool call the wire parser failed to decode,
/// nudges the model and re-rolls the turn, and after three of them ends the run
/// `malformed_tool_call`. That is right for the shapes it was written against
/// (`&lt;tool_call&gt;file_editor:::`, `&lt;file_editor&gt;`, `[view]`, a fenced
/// `{"command": ...}`). It is wrong for a model that was ASKED for a short JSON
/// answer and gave one.
///
/// Measured live, 2026-09-05, EpicForge's law-44 reader (prompted "Answer with
/// ONE fenced JSON object and nothing after it"): 32 of 84 readback runs had
/// their `{ "claim_holds": ... }` verdict flagged here at 52-79 output tokens,
/// nudged, re-emitted, flagged again, and 17 of them ended `runaway_paused`
/// with the answer never delivered. The one reader that escaped did so because
/// its prose pushed the reply past 100 tokens -- the LENGTH was the
/// discriminator, not the shape, which is the tell that the heuristic was
/// reading the wrong feature.
///
/// The two shapes differ in VOCABULARY, not in their first character: a botched
/// tool call names a registered tool, or one of its parameters as a JSON key; an
/// answer names neither. So the `{` and fence openers are gated on that
/// vocabulary. `&lt;` and `[` keep their unconditional standing -- no answer to a
/// question opens with a tag.
///
/// Every test here drives the REAL AgentLoop with a client that sets
/// `Usage.OutputTokenCount`, because the branch under test is unreachable when
/// usage is absent (outputTokens reads 0 and the `&gt; 0` guard skips it) -- a
/// test with a usage-less client would pass vacuously in both directions.
/// </summary>
public class MalformedToolCallHeuristicTests
{
    /// <summary>Replies with one fixed text forever, stamping the output-token
    /// count the heuristic reads. The count is the independent variable of the
    /// `&lt; 100` gate, so it is set explicitly on every response.</summary>
    private sealed class UsageClient(string text, int outputTokens) : IChatClient
    {
        private int _n;
        public int CallCount => _n;
        public List<List<ChatMessage>> Sent { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add(messages.ToList());
            _n++;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)])
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { OutputTokenCount = outputTokens },
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

    /// <summary>The two tools the live shapes were recorded against, in the
    /// OpenAI schema form the loop's vocabulary builder reads (`function` wrapper,
    /// `name`, `parameters.properties`). Hand-built so the test owns its
    /// vocabulary: `terminal` with `command`; `file_editor` with `command`,
    /// `path`, `file_text`, `old_str`, `new_str`.</summary>
    private static List<JsonElement> Schemas() =>
    [
        JsonSerializer.Deserialize<JsonElement>(
            """
            {"type":"function","function":{"name":"terminal","parameters":{"type":"object",
             "properties":{"command":{"type":"string"}}}}}
            """),
        JsonSerializer.Deserialize<JsonElement>(
            """
            {"type":"function","function":{"name":"file_editor","parameters":{"type":"object",
             "properties":{"command":{"type":"string"},"path":{"type":"string"},"file_text":{"type":"string"},
                           "old_str":{"type":"string"},"new_str":{"type":"string"}}}}}
            """),
    ];

    private static async Task<(UsageClient client, List<Event> events, AgentResult result)> Run(string text, int outputTokens)
    {
        var events = new ConcurrentQueue<Event>();
        var client = new UsageClient(text, outputTokens);
        var caps = new AgentCapabilities(new Dictionary<string, ToolFn>(), Schemas(), new List<MiddlewareFn>());
        var env = new AgentEnvironment(new InertSandbox(), "sess", 4, events.Enqueue);
        var result = await AgentLoop.RunAsync(new LlmSettings(client, "test-model"), caps, env, "sys", "go");
        return (client, events.ToList(), result);
    }

    private static long OutputTokensSeen(List<Event> events) =>
        Convert.ToInt64(events.First(e => e.Type == "llm_response").Data["output_tokens"]);

    /// <summary>The recovery paths a run took, in order. RunAsync has its own
    /// rule for a text-only reply -- `no_tool_engagement_nudge`, twice, then it
    /// accepts the text -- so "one request" is NOT the healthy null here; the
    /// healthy null is "the same path the same answer takes past the length
    /// gate", and that is what the defect tests compare against.</summary>
    private static string Recoveries(List<Event> events) =>
        string.Join(",", events
            .Where(e => e.Type is "malformed_tool_call" or "no_tool_engagement_nudge" or "truncated_response_recovery")
            .Select(e => e.Type));

    /// <summary>The malformed path's SECOND symptom: the next request is pinned
    /// to the tool the loop thinks the model meant (`forced_tool_choice`,
    /// reason `previous_iteration_emitted_malformed_tool_call`).</summary>
    private static bool PinnedTheNextCall(List<Event> events) =>
        events.Any(e => e.Type == "forced_tool_choice");

    /// <summary>The reader's reply, verbatim in shape from the live readback
    /// logs: a fence, a JSON object with the keys the reader was told to use,
    /// none of them a tool name or a tool parameter.</summary>
    private const string ReaderReply =
        "```json\n{ \"claim_holds\": true, \"counted\": \"7 numbered requirements, 7 hold\", \"correction\": null }\n```";

    // ---- the defect -----------------------------------------------------------

    [Fact]
    public async Task AShortFencedJsonAnswerThatNamesNoToolIsNotAToolCall()
    {
        var (client, events, result) = await Run(ReaderReply, 52);
        // The control is the live escape: the SAME answer, past the length gate.
        var (ctlClient, ctlEvents, ctlResult) = await Run(ReaderReply, 140);

        // Positive conjunct: the count the heuristic gates on really reached the
        // loop. Without this the "no event" assertion below would also hold for
        // a client whose usage never arrived.
        Assert.Equal(52, OutputTokensSeen(events));

        Assert.DoesNotContain(events, e => e.Type == "malformed_tool_call");
        Assert.False(PinnedTheNextCall(events), "the next request was pinned to a tool the answer never named");
        // And it is treated EXACTLY as the answer is treated at 140 tokens: same
        // recovery path, same number of requests, same stop reason. The live
        // failure was the same answer requested three times and discarded three
        // times while its 118-token sibling went straight through.
        Assert.Equal(Recoveries(ctlEvents), Recoveries(events));
        Assert.Equal(ctlClient.CallCount, client.CallCount);
        Assert.Equal(ctlResult.StopReason, result.StopReason);
    }

    [Fact]
    public async Task AShortBareJsonAnswerThatNamesNoToolIsNotAToolCall()
    {
        var (client, events, result) = await Run(
            "{ \"claim_holds\": false, \"counted\": \"3 requirements, 2 hold\", \"correction\": \"requirement 3 is unmet\" }", 40);
        var (ctlClient, ctlEvents, ctlResult) = await Run(ReaderReply, 140);

        Assert.Equal(40, OutputTokensSeen(events));
        Assert.DoesNotContain(events, e => e.Type == "malformed_tool_call");
        Assert.False(PinnedTheNextCall(events));
        Assert.Equal(Recoveries(ctlEvents), Recoveries(events));
        Assert.Equal(ctlClient.CallCount, client.CallCount);
        Assert.Equal(ctlResult.StopReason, result.StopReason);
    }

    // ---- the shapes the heuristic exists for, unchanged -----------------------

    [Fact]
    public async Task AFencedJsonCarryingAToolParameterStillCounts()
    {
        // The recorded drift shape: the model emitted its tool call as fenced
        // JSON instead of a structured call. `command` is a registered parameter.
        var (_, events, _) = await Run("```json\n{\"command\": \"ls -la\"}\n```", 14);

        var hit = Assert.Single(events, e => e.Type == "malformed_tool_call" && Convert.ToInt32(e.Data["iteration"]) == 1);
        Assert.StartsWith("```json", (string)hit.Data["content_preview"]!);
    }

    [Fact]
    public async Task AFencedJsonNamingAToolStillCounts()
    {
        var (_, events, _) = await Run("```json\n{\"name\": \"file_editor\", \"arguments\": {\"path\": \"a.txt\"}}\n```", 22);

        Assert.Contains(events, e => e.Type == "malformed_tool_call");
    }

    [Fact]
    public async Task ABareJsonToolCallStillCounts()
    {
        var (_, events, _) = await Run("{\"command\": \"view\", \"path\": \"src/index.js\"}", 16);

        Assert.Contains(events, e => e.Type == "malformed_tool_call");
    }

    [Fact]
    public async Task ABracketedShorthandStillCounts()
    {
        var (_, events, _) = await Run("[view] src/index.js", 7);

        Assert.Contains(events, e => e.Type == "malformed_tool_call");
    }

    [Fact]
    public async Task ABareToolNameTagStillCounts()
    {
        var (_, events, _) = await Run("<file_editor>", 4);

        Assert.Contains(events, e => e.Type == "malformed_tool_call");
    }

    // ---- the tell ---------------------------------------------------------------

    /// <summary>
    /// CHARACTERISATION of the live escape, not a behaviour anyone wants: the
    /// readers that got their verdict through did so by writing enough prose to
    /// pass 100 tokens (run 5 pass 2's reader: 118 tokens, `holds`, straight
    /// through). This pins the `&lt; 100` gate as measured, so that if it ever
    /// moves, the change is visible here rather than inferred from a log -- and
    /// it pins RunAsync's OWN treatment of a text-only reply (two
    /// `no_tool_engagement_nudge`s, then accepted as `completed`), which is the
    /// healthy null the defect tests compare against. Mis-specifying that null
    /// as "one request" is how this test first went red.
    /// </summary>
    [Fact]
    public async Task PastAHundredTokensTheShapeNeverMatteredAndStillDoesNot()
    {
        var (client, events, result) = await Run(ReaderReply, 140);

        Assert.Equal(140, OutputTokensSeen(events));
        Assert.DoesNotContain(events, e => e.Type == "malformed_tool_call");
        Assert.False(PinnedTheNextCall(events));
        Assert.Equal("no_tool_engagement_nudge,no_tool_engagement_nudge", Recoveries(events));
        Assert.Equal("completed", result.StopReason);
        Assert.Equal(3, client.CallCount);
    }
}
