using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// ⭐⭐ A TRUNCATED RESPONSE IS NOT A FINISHED TURN.
///
/// THE DEFECT (measured 2026-09-01, epic-forge E3 run 6, ef-team-flash with
/// `max_output_tokens: 4096`). The leader's iteration 24 spent its entire
/// output budget in a floating-point arithmetic loop:
///
///     "…3.1? Wait, 1.1 + 2.2 = 3.1? No. 1.1 + 2.2 = 3.1? Actually 1.1 + 2.2
///      = 3.1? Hmm, 1.1 + 2.2 = 3.1? … Let me just compute: 1.1 + 2.2"
///
/// — cut off mid-expression at exactly 4096 output tokens, having emitted no
/// tool call. A tool-call-less turn means "the model is done", so the run
/// finalised.
///
/// ⛔ WHAT THAT COST, WHICH IS THE REASON THIS FILE EXISTS. The run finalised
/// while holding a COMPLETED dispatch it had never reviewed. `implementer-2-2`
/// had returned 8 events earlier having written `src/aggregate.js` and
/// `src/format.js` — the exact two modules the epic's verifier then reported
/// missing. Dispatch bytes live in a worktree and are promoted only by
/// `accept_dispatch`. So the work existed, was correct, and was discarded,
/// because a half-sentence was read as a final answer. vett had even emitted
/// `pending_review_nag` naming that task id; the signal was in the stream and
/// nothing acted on it.
///
/// ⛔ AND IT IS THE FIX FOR THE OPPOSITE DEFECT THAT CREATED IT. With no
/// output bound, generation ran toward `max_model_len - prompt` (~173k tokens
/// on this rig) and every attempt died on its 180s timeout — four consecutive
/// stalled runs. Adding the bound fixed that and opened this one. Measured
/// across the ladder: 0 responses hit the cap in the four unbounded runs; 6
/// did in run 6, three of them the leader. The bound stays; a correction can
/// over-shoot, and the answer is to handle the new mode rather than restore
/// the worse one.
///
/// ⛔ WHY THE EXISTING RECOVERIES COULD NOT CATCH IT. AgentLoop already has a
/// well-developed family of "the model tried to call a tool and the wire
/// format broke" heuristics. Every one of them is keyed on a SHORT reply
/// (&lt;100 tokens, or &lt;30 for the bare-tool-name shape) or on a marker string
/// (`&lt;tool_call&gt;`, `&lt;function=`, DSML's U+FF5C). A 4096-token truncation is
/// long, prose-led and marker-free, so it fell through all of them to the
/// ordinary no-tool-calls exit. `finish_reason` is the structural signal none
/// of those heuristics had: it is the one case where the provider TELLS you
/// the model did not finish.
///
/// PRECONDITION VERIFIED AT THE WIRE BEFORE ANY OF THIS WAS WRITTEN, because a
/// recovery keyed on a field the provider never populates is a confident
/// no-op that looks exactly like a fix. Against the live endpoint
/// (gpu-2:8000, deepseek-v4-flash): a request capped at 24 tokens
/// returned `finish_reason: "length"`; an uncapped one returned
/// `finish_reason: "stop"`. The signal exists and discriminates. vett was
/// dropping it — `finish_reason` appeared NOWHERE in the source.
///
/// These tests drive the REAL AgentLoop through a scripted client, so they
/// exercise the shipped path rather than a copy of it.
/// </summary>
public class TruncatedResponseRecoveryTests
{
    /// <summary>The observed shape, abbreviated: prose that never reaches a
    /// tool call. Long and marker-free, which is precisely why every existing
    /// heuristic ignores it.</summary>
    private const string RamblingReply =
        "The implementer is confused about the expected output. The task I gave them had an "
        + "error in the expected value. Let me work out what the correct total is. "
        + "1.1 + 2.2 = 3.1? Wait, 1.1 + 2.2 = 3.1? No. 1.1 + 2.2 = 3.1? Actually 1.1 + 2.2 "
        + "= 3.1? Hmm, 1.1 + 2.2 = 3.1? No. Let me just compute: 1.1 + 2.2";

    /// <summary>
    /// Replays one scripted assistant message, with a caller-chosen
    /// FinishReason. The finish reason is the whole independent variable here,
    /// so it is settable per client and nothing else differs between arms.
    /// </summary>
    private sealed class ScriptedClient(ChatFinishReason? finishReason, params ChatMessage[] script) : IChatClient
    {
        private int _n;
        public int CallCount => _n;
        /// <summary>Every message list the loop sent, so a test can assert on
        /// what did — and did not — survive into context.</summary>
        public List<List<ChatMessage>> Sent { get; } = [];

        /// <summary>Law 244: when set, every reply carries the streaming
        /// client's prose-loop reading (as MarkProseAbort attaches it).</summary>
        public (int Chars, int Pct)? ProseLoop { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add(messages.ToList());
            var msg = script[Math.Min(_n, script.Length - 1)];
            _n++;
            var resp = new ChatResponse(
                [new ChatMessage(msg.Role, msg.Contents.ToList())])
            {
                FinishReason = finishReason,
            };
            if (ProseLoop is { } pl)
            {
                resp.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                resp.AdditionalProperties[Vett.Llm.SilenceBoundedChatClient.ProseLoopCharsKey] = pl.Chars;
                resp.AdditionalProperties[Vett.Llm.SilenceBoundedChatClient.ProseLoopPctKey] = pl.Pct;
            }
            return Task.FromResult(resp);
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

    // A COMPLETE call: `check_task` requires `task_id`, and this fixture's
    // premise is "the provider emitted complete tool_calls". An EMPTY call to
    // a tool that needs arguments, arriving at the cap, is the other case --
    // the cut tail call of TruncatedToolCallTests -- and is NOT left alone.
    private static ChatMessage Call(string callId, string name) =>
        new(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(callId, name, new Dictionary<string, object?> { ["task_id"] = "T-1" }),
        });

    private static async Task<(ScriptedClient client, ConcurrentQueue<Event> events, AgentResult result)>
        RunAsync(ChatFinishReason? finish, ChatMessage reply, int maxIters = 6)
    {
        var events = new ConcurrentQueue<Event>();
        var client = new ScriptedClient(finish, reply);
        var env = new AgentEnvironment(new InertSandbox(), "sess", maxIters, events.Enqueue);
        var result = await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model"), CapsWithCheckTask(), env, "sys", "go");
        return (client, events, result);
    }

    private static ChatMessage Prose(string s) => new(ChatRole.Assistant, s);

    // ---- the defect --------------------------------------------------------

    [Fact]
    public async Task ATruncatedReplyWithNoToolCall_IsRecovered_NotTreatedAsDone()
    {
        var (_, events, _) = await RunAsync(ChatFinishReason.Length, Prose(RamblingReply));

        Assert.Contains(events, e => e.Type == "truncated_response_recovery");
    }

    /// <summary>
    /// ⭐ THE CONTROL THAT MAKES THE TEST ABOVE MEAN SOMETHING. Identical text,
    /// identical absence of tool calls — only the finish reason differs. A
    /// recovery that fired on every prose reply would satisfy the defect test
    /// completely and would break every legitimate "I'm done" turn in the
    /// product. `length` must be what fires it.
    /// </summary>
    [Fact]
    public async Task TheSameProseThatFinishedNormally_IsNotRecovered()
    {
        var (_, events, _) = await RunAsync(ChatFinishReason.Stop, Prose(RamblingReply));

        Assert.DoesNotContain(events, e => e.Type == "truncated_response_recovery");
    }

    /// <summary>
    /// ⭐ THE SECOND CONTROL, AND IT IS DRAWN FROM THE MEASUREMENT. Run 6's
    /// iterations 16 and 20 ALSO hit exactly 4096 tokens, and both still
    /// carried a `function_call` — the provider emits complete tool_calls, so
    /// a truncated turn that produced one is perfectly usable. Only the turn
    /// where the ramble consumed the whole budget before any call is lost.
    /// Re-rolling a usable turn would throw away real work to fix a problem it
    /// does not have.
    /// </summary>
    [Fact]
    public async Task ATruncatedReplyThatStillCarriesAToolCall_IsLeftAlone()
    {
        var (client, events, _) = await RunAsync(ChatFinishReason.Length, Call("c1", "check_task"));

        Assert.DoesNotContain(events, e => e.Type == "truncated_response_recovery");
        // And the call actually ran — "not recovered" must not mean "dropped".
        Assert.Contains(events, e => e.Type == "tool_call_end");
    }

    /// <summary>
    /// A provider that reports nothing must not be read as reporting
    /// truncation. `finish_reason` is optional on the wire, and a null-means-
    /// truncated bug would fire the recovery on every turn of every provider
    /// that omits it — the false-positive flood that gets a mechanism disabled.
    /// </summary>
    [Fact]
    public async Task ASilentProvider_IsNotTreatedAsTruncated()
    {
        var (_, events, _) = await RunAsync(null, Prose(RamblingReply));

        Assert.DoesNotContain(events, e => e.Type == "truncated_response_recovery");
    }

    // ---- the recovery's own behaviour --------------------------------------

    /// <summary>
    /// The nudge has to reach the model, and it has to be a DIFFERENT
    /// instruction — a model that rambles identically the second time just
    /// burns the retry cap. So the recovery names the mechanism (you were cut
    /// off, lead with the call, compute in the terminal) rather than saying
    /// "try again".
    /// </summary>
    [Fact]
    public async Task TheRecoveryInjectsANudgeThatNamesTheMechanism()
    {
        var (client, _, _) = await RunAsync(ChatFinishReason.Length, Prose(RamblingReply));

        Assert.True(client.Sent.Count >= 2, "the loop must have asked the model again");
        var second = client.Sent[1];
        var nudge = second.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        Assert.Contains("cut off", nudge);
        Assert.Contains("tool call", nudge);
    }

    /// <summary>
    /// ⛔ THE TRUNCATED TEXT MUST NOT SURVIVE INTO CONTEXT. The observed
    /// content is a degenerate repetition loop; re-feeding it invites the model
    /// to continue the loop it was already stuck in, and a half-finished
    /// sentence is not context worth its tokens. Same call as the
    /// empty-response path above it makes.
    /// </summary>
    [Fact]
    public async Task TheTruncatedTextIsDroppedRatherThanReplayedIntoContext()
    {
        var (client, _, _) = await RunAsync(ChatFinishReason.Length, Prose(RamblingReply));

        Assert.True(client.Sent.Count >= 2);
        var second = client.Sent[1];

        Assert.DoesNotContain(second, m =>
            m.Role == ChatRole.Assistant && (m.Text ?? "").Contains("Let me just compute"));
    }

    /// <summary>
    /// A model that truncates every single time must not loop forever — and
    /// must not exit as an ORDINARY completion either. Falling through to the
    /// normal no-tool-calls exit would record a stuck model as a finished one,
    /// which is the exact confusion this whole branch exists to end. The stop
    /// reason has to say what happened.
    /// </summary>
    [Fact]
    public async Task PersistentTruncation_EndsWithItsOwnStopReason_NotAnOrdinaryCompletion()
    {
        var (_, events, result) = await RunAsync(
            ChatFinishReason.Length, Prose(RamblingReply), maxIters: 20);

        Assert.Equal("truncated_response_exhausted", result.StopReason);
        // The cap is 3 retries, so exactly 4 recovery events: three that
        // continued and the fourth that gave up. A cap that never binds and a
        // cap that binds at the wrong count both fail here.
        var recoveries = events.Where(e => e.Type == "truncated_response_recovery").ToList();
        Assert.Equal(4, recoveries.Count);
        Assert.Equal(1, recoveries[0].Data["retry_count"]);
        Assert.Equal(4, recoveries[3].Data["retry_count"]);
    }

    // ---- law 244: the second consecutive prose loop compacts before the retry --

    /// <summary>Batch 14 (2026-09-09, s14c it.41-44, VETT c0a0f4b): the prose
    /// loop is a property of the context. Four consecutive law-240 retries
    /// into the SAME 54k-token history each looped again and the seat died
    /// `truncated_response_exhausted` at 3,570 s. At the second consecutive
    /// prose loop the history is compacted (threshold 0, the profile's keep)
    /// before the nudge; the retry cap is unchanged.</summary>
    [Fact]
    public async Task TheSecondConsecutiveProseLoop_CompactsTheHistoryBeforeTheRetry()
    {
        var events = new ConcurrentQueue<Event>();
        var client = new ScriptedClient(ChatFinishReason.Length, Prose("response response response"))
        {
            ProseLoop = (12290, 1),
        };
        var env = new AgentEnvironment(new InertSandbox(), "sess", 20, events.Enqueue);
        var result = await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model"), CapsWithCheckTask(), env, "sys", "go");

        Assert.Equal("truncated_response_exhausted", result.StopReason);
        var recoveries = events.Where(e => e.Type == "truncated_response_recovery").ToList();
        Assert.Equal(4, recoveries.Count);
        Assert.Equal(new object?[] { 1, 2, 1, 2 }, recoveries.Select(r => r.Data["consecutive_prose_loops"]).ToArray());

        var compactions = events.Where(e => e.Type == "compacted" && (string?)e.Data["reason"] == "prose_loop_retry").ToList();
        // ONE compaction: the second loop compacts; the fourth recovery exhausts
        // the retry cap before the compaction site is reached (the cap is unchanged).
        Assert.Single(compactions);
        Assert.Equal(2, compactions[0].Data["consecutive_prose_loops"]);
        Assert.Equal(2, compactions[0].Data["retry_count"]);

        // The third request sees the digest: a summary took the place of the
        // conversation, and the nudge that follows it says so.
        var third = client.Sent[2];
        Assert.Contains(third, m => m.Role == ChatRole.User && m.Text.StartsWith(AgentLoop.ProseLoopCompactedPreamble, StringComparison.Ordinal));
        Assert.Contains(third, m => m.Text.Contains("compact", StringComparison.OrdinalIgnoreCase) && !m.Text.StartsWith(AgentLoop.ProseLoopCompactedPreamble, StringComparison.Ordinal));
        // The second request, after ONE prose loop, was not compacted.
        Assert.DoesNotContain(client.Sent[1], m => m.Text.StartsWith(AgentLoop.ProseLoopCompactedPreamble, StringComparison.Ordinal));
    }

    /// <summary>A truncation that is NOT a prose loop (the rambling cap case)
    /// never compacts, however many times it repeats: the count is of prose
    /// loops, not of truncations.</summary>
    [Fact]
    public async Task RepeatedCapTruncationsWithoutAProseLoop_NeverCompact()
    {
        var (client, events, result) = await RunAsync(
            ChatFinishReason.Length, Prose(RamblingReply), maxIters: 20);

        Assert.Equal("truncated_response_exhausted", result.StopReason);
        Assert.DoesNotContain(events, e => e.Type == "compacted");
        Assert.All(events.Where(e => e.Type == "truncated_response_recovery"),
            e => Assert.Equal(0, e.Data["consecutive_prose_loops"]));
        Assert.DoesNotContain(client.Sent.SelectMany(s => s),
            m => m.Text.StartsWith(AgentLoop.ProseLoopCompactedPreamble, StringComparison.Ordinal));
    }

    // ---- the instrumentation half ------------------------------------------

    /// <summary>
    /// ⭐ THE ENABLING DEFECT. Until this, `llm_response` recorded tokens but
    /// not why generation stopped, so "the model finished" and "the model was
    /// cut off" were the same event in the log. The only way to tell them
    /// apart was to notice that `output_tokens` happened to equal the
    /// configured bound — an inference, unavailable to anything reading a
    /// single event, and impossible for any run whose bound was not known.
    /// Recording it is what made the defect above findable at all.
    /// </summary>
    [Fact]
    public async Task LlmResponseRecordsWhyGenerationStopped()
    {
        var (_, truncated, _) = await RunAsync(ChatFinishReason.Length, Prose(RamblingReply));
        var (_, finished, _) = await RunAsync(ChatFinishReason.Stop, Prose("All done."));

        var t = truncated.First(e => e.Type == "llm_response");
        var f = finished.First(e => e.Type == "llm_response");

        Assert.Equal("length", t.Data["finish_reason"]);
        Assert.Equal("stop", f.Data["finish_reason"]);
    }

    /// <summary>
    /// The field is emitted even when the provider says nothing, so a reader
    /// can tell "reported nothing" from "reported stop". A key that is present
    /// only sometimes makes its own absence ambiguous.
    /// </summary>
    [Fact]
    public async Task TheFinishReasonKeyIsPresentEvenWhenTheProviderIsSilent()
    {
        var (_, events, _) = await RunAsync(null, Prose("All done."));
        var e = events.First(x => x.Type == "llm_response");

        Assert.True(e.Data.ContainsKey("finish_reason"));
        Assert.Null(e.Data["finish_reason"]);
    }

    // ---- the nudge has to change the reply, and for a FINAL reply it did not ----

    /// <summary>
    /// The nudge after a truncated reply said "answer with the tool call FIRST".
    /// That is the right instruction for a reply that rambled on its way to a
    /// tool call, and no instruction at all for a reply that WAS the answer --
    /// a final plan, a report -- and was simply bigger than the cap. Measured
    /// live 2026-09-05 (EpicForge architect, cap 12288, input context 55k): the
    /// same plan was re-emitted at exactly 12288 tokens THREE times in a row
    /// after three nudges, then the run ended `truncated_response_exhausted`
    /// having cut nothing. A re-roll that is not told to be smaller is a replay.
    ///
    /// So the nudge names the CAP (the number the loop already holds in
    /// <c>llm.MaxOutputTokens</c>) and covers the final-reply case: send a
    /// version that fits, keep the decisions, cut the prose. It is the only
    /// number in the sentence -- the retry allowance stays in code, where it
    /// lives, and is not duplicated into prose that drifts.
    /// </summary>
    [Fact]
    public async Task TheNudgeNamesTheCapAndTellsAFinalReplyToShrink()
    {
        var events = new ConcurrentQueue<Event>();
        var client = new ScriptedClient(ChatFinishReason.Length, Prose(RamblingReply));
        var env = new AgentEnvironment(new InertSandbox(), "sess", 3, events.Enqueue);
        await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model", MaxOutputTokens: 12288), CapsWithCheckTask(), env, "sys", "go");

        Assert.Contains(events, x => x.Type == "truncated_response_recovery");
        Assert.True(client.Sent.Count >= 2, "the loop never re-rolled after the truncation");

        // The nudge is the last user message of the SECOND request.
        var nudge = client.Sent[1].Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("12288", nudge);
        Assert.Contains("tool call FIRST", nudge);
        // The final-reply branch: the same answer will be cut again; shrink it.
        Assert.Matches("(?i)shorter|fits", nudge);
        Assert.Matches("(?i)cut the prose|keep .*decision", nudge);
        // ONE number in the sentence, and it is the cap.
        var numbers = System.Text.RegularExpressions.Regex.Matches(nudge, @"\d+").Select(m => m.Value).Distinct().ToList();
        Assert.Equal(["12288"], numbers);
    }

    [Fact]
    public async Task WithNoCapTheNudgeNamesNoNumber()
    {
        // Unbounded profiles hit the PROVIDER's limit, whose size the loop does
        // not know. Saying a number it does not have would be a false constraint.
        var (client, events, _) = await RunAsync(ChatFinishReason.Length, Prose(RamblingReply));

        Assert.Contains(events, x => x.Type == "truncated_response_recovery");
        var nudge = client.Sent[1].Last(m => m.Role == ChatRole.User).Text;
        Assert.DoesNotMatch(@"\d", nudge);
        Assert.Matches("(?i)shorter|fits", nudge);
    }
}
