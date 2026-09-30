using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// Agent-loop dispatch + measurement-integrity tests.
///
/// Three separate defects, all in the "the harness lied about what happened"
/// family that this repo cares about:
///
///   1. declare_done's refusal budget was a PROCESS-GLOBAL static, so a second
///      LeaderTools.Create (nested team, or a second bench instance in the same
///      process) reset or consumed another live team's budget. Consuming it is
///      the dangerous direction: the budget running out makes declare_done FAIL
///      OPEN and submit an unverified result.
///   2. A tool call naming an unregistered tool emitted NO events at all, so
///      the event stream — the thing assertions and post-mortems read — was
///      silent about a call that really happened.
///   3. The two "nudge" recovery paths re-added the assistant message that the
///      loop had already appended, double-counting it in message history.
///
/// This class sets VETT_REQUIRED_PATHS, so it joins the declare-done-env
/// collection to stay serialised with the other env-mutating classes.
/// </summary>
[Collection("declare-done-env")]
public class AgentLoopDispatchTests : IDisposable
{
    private readonly string? _savedPaths = Environment.GetEnvironmentVariable("VETT_REQUIRED_PATHS");
    private readonly string? _savedContent = Environment.GetEnvironmentVariable("VETT_REQUIRED_CONTENT");
    private readonly string? _savedVerify = Environment.GetEnvironmentVariable("VETT_VERIFY_CMD");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", _savedPaths);
        Environment.SetEnvironmentVariable("VETT_REQUIRED_CONTENT", _savedContent);
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", _savedVerify);
    }

    // ---- fixtures ---------------------------------------------------------

    /// <summary>Sandbox that reports a fixed `git status --porcelain`, so
    /// declare_done's "required path untouched" check is deterministic.
    /// <paramref name="yieldFirst"/> forces a real await so concurrent
    /// declare_done calls actually interleave on the thread pool.</summary>
    private sealed class PorcelainSandbox(string porcelain, bool yieldFirst = false) : ISandbox
    {
        public string Cwd => "/fake";
        public async Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
        {
            if (yieldFirst) await Task.Yield();
            return cmd.Contains("git status")
                ? new BashResult(porcelain, 0, "/fake", false)
                : new BashResult("", 0, "/fake", false);
        }
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

    /// <summary>Replays a scripted list of assistant messages, then repeats the
    /// last one forever. Lets a test drive the real AgentLoop with no network.</summary>
    private sealed class ScriptedClient(params ChatMessage[] script) : IChatClient
    {
        private int _n;
        public int CallCount => _n;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var msg = script[Math.Min(_n, script.Length - 1)];
            _n++;
            // Fresh instance per call. A real provider never hands back the
            // same ChatMessage object twice, and one of the tests below
            // asserts on reference identity in the history.
            return Task.FromResult(new ChatResponse(
                [new ChatMessage(msg.Role, msg.Contents.ToList())]));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatMessage Text(string s) => new(ChatRole.Assistant, s);

    private static ChatMessage Call(string callId, string name) =>
        new(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(callId, name, new Dictionary<string, object?>()),
        });

    private static Dictionary<string, object?> Summary(string s) => new() { ["summary"] = s };

    private static Dictionary<string, ToolFn> NewTeam() =>
        LeaderTools.Create(new TaskBoard(), (_, _, _, _) => Task.FromResult(""));

    // ---- 1. declare_done refusal budget is PER TEAM ------------------------

    /// <summary>
    /// TIER-3 shape: an outer leader is mid-refusal when a nested team builds
    /// its own leader toolkit. With the budget held in a static, the nested
    /// Create() zeroed it and handed the outer leader a fresh 10 refusals —
    /// the cap silently stopped capping. Refusal N is the observable.
    /// </summary>
    [Fact]
    public async Task DeclareDoneBudget_ASecondTeamsCreate_DoesNotResetTheFirstTeamsBudget()
    {
        Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", "src/A.cs");
        Environment.SetEnvironmentVariable("VETT_REQUIRED_CONTENT", null);
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", null);

        var teamA = NewTeam();
        var sandbox = new PorcelainSandbox(" M src/B.cs\n");   // A.cs untouched

        for (var i = 1; i <= 3; i++)
        {
            var r = await teamA["declare_done"](Summary("done"), sandbox, "s", default);
            Assert.Contains($"refusal {i}/10", r);
        }

        // A nested team (or a second bench instance in this process) builds its
        // own leader tools. This must not touch team A's counter.
        _ = NewTeam();

        var after = await teamA["declare_done"](Summary("done"), sandbox, "s", default);
        Assert.Contains("refusal 4/10", after);
        Assert.DoesNotContain("refusal 1/10", after);
    }

    /// <summary>
    /// The dangerous direction. Two teams alive in one process (Runner fans out
    /// with SemaphoreSlim + Task.WhenAll): team B burning its whole budget must
    /// not push team A over the cap. Over the cap, declare_done FAILS OPEN and
    /// submits — so a shared counter can hand one run an unverified pass that
    /// another run paid for.
    /// </summary>
    [Fact]
    public async Task DeclareDoneBudget_OneTeamsExhaustion_DoesNotFailAnotherTeamOpen()
    {
        Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", "src/A.cs");
        Environment.SetEnvironmentVariable("VETT_REQUIRED_CONTENT", null);
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", null);

        var teamA = NewTeam();
        var teamB = NewTeam();
        var sandbox = new PorcelainSandbox(" M src/B.cs\n");   // A.cs untouched

        // Team B burns all 10 of ITS refusals.
        for (var i = 1; i <= 10; i++)
        {
            var r = await teamB["declare_done"](Summary("b"), sandbox, "s", default);
            Assert.Contains($"refusal {i}/10", r);
        }
        var bFailedOpen = await teamB["declare_done"](Summary("b"), sandbox, "s", default);
        Assert.Contains("gate-failed-open", bFailedOpen);      // B's own cap still works

        // Team A has spent nothing. It must still be refused, not submitted.
        var aFirst = await teamA["declare_done"](Summary("a"), sandbox, "s", default);
        Assert.StartsWith("Error: cannot declare done", aFirst);
        Assert.Contains("refusal 1/10", aFirst);
        Assert.DoesNotContain("gate-failed-open", aFirst);
        Assert.DoesNotContain(Builtins.SubmitMarker, aFirst);
    }

    /// <summary>
    /// Regression guard for the counter itself: ten concurrent refusals on ONE
    /// team must consume exactly ten budget slots and report the numbers 1..10
    /// with no repeats. A plain `refusals++` can lose an update under a real
    /// race — that under-counts the budget and delays the cap. (This asserts
    /// the post-fix invariant; the pre-fix race is real but not reliably
    /// reproducible, so it is not part of the two-sided proof.)
    /// </summary>
    [Fact]
    public async Task DeclareDoneBudget_ConcurrentRefusals_AreNumberedExactlyOnce()
    {
        Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", "src/A.cs");
        Environment.SetEnvironmentVariable("VETT_REQUIRED_CONTENT", null);
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", null);

        var team = NewTeam();
        var sandbox = new PorcelainSandbox(" M src/B.cs\n", yieldFirst: true);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => team["declare_done"](Summary("x"), sandbox, "s", default))));

        var seen = results
            .Select(r => System.Text.RegularExpressions.Regex.Match(r, @"refusal (\d+)/10"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(Enumerable.Range(1, 10).ToList(), seen);
    }

    // ---- 2. an unregistered tool call must leave a trace --------------------

    /// <summary>
    /// Premise: DispatchAsync returned an "Error: tool not registered"
    /// observation but emitted nothing, so the JSONL session log showed no
    /// evidence the call was ever made. That silence is exactly what made the
    /// `mcp__` schema-without-handler mismatch expensive to diagnose.
    ///
    /// The fix must NOT be a synthesised tool_call_end: AssertionEngine matches
    /// `tool_call: X` on tool_call_end by name, with no success filter
    /// required, so an end event here would turn "called a tool that does not
    /// exist" from a FAIL into a PASS. Both halves are asserted.
    /// </summary>
    [Fact]
    public async Task UnregisteredTool_EmitsATrace_ButNoToolCallEnd()
    {
        var events = new ConcurrentQueue<Event>();
        var client = new ScriptedClient(
            Call("call-1", "mcp__missing"),
            Text("I could not use that tool, so I am stopping."));

        var caps = new AgentCapabilities(
            new Dictionary<string, ToolFn>(),   // deliberately empty registry
            new List<JsonElement>(),
            new List<MiddlewareFn>());
        var env = new AgentEnvironment(new PorcelainSandbox(""), "sess", 5, events.Enqueue);

        var result = await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model"), caps, env, "sys", "do the thing");

        var all = events.ToList();

        // The attempt is recorded, and names the tool + the registry it missed.
        var trace = Assert.Single(all, e => e.Type == "tool_not_registered");
        Assert.Equal("mcp__missing", trace.Data["tool_name"]);
        Assert.Equal("call-1", trace.Data["call_id"]);
        Assert.True(trace.Data.ContainsKey("registered_tools"));

        // ...and it is NOT recorded as a completed tool call, which would
        // credit the model for a tool that does not exist.
        Assert.DoesNotContain(all, e =>
            e.Type == "tool_call_end"
            && e.Data.TryGetValue("tool_name", out var n) && (n as string) == "mcp__missing");

        // The loop still answered the model rather than hanging on it.
        Assert.Contains(result.Messages, m =>
            m.Contents.Any(c => c is FunctionResultContent fr
                && (fr.Result?.ToString() ?? "").Contains("not registered")));
    }

    // ---- 3. the nudge paths must not double-count the assistant turn -------

    /// <summary>
    /// The loop appends the assistant message unconditionally ("Add assistant
    /// message to history"), and the no-tool-engagement nudge appended it a
    /// SECOND time — the same ChatMessage instance twice. Every later request
    /// then re-sent the duplicated turn, inflating message count and
    /// TotalInputTokens, both of which are reported as run evidence.
    ///
    /// Script: three prose-only replies. Iterations 1 and 2 fire the nudge
    /// (cap 2); iteration 3 is past the `Iteration &lt;= 2` window and finishes.
    /// Three LLM calls therefore mean exactly three assistant messages.
    /// </summary>
    [Fact]
    public async Task NoToolEngagementNudge_DoesNotDuplicateTheAssistantMessage()
    {
        var client = new ScriptedClient(Text("I will look into the task now."));
        var caps = new AgentCapabilities(
            new Dictionary<string, ToolFn>(), new List<JsonElement>(), new List<MiddlewareFn>());
        var env = new AgentEnvironment(new PorcelainSandbox(""), "sess", 10);

        var result = await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model"), caps, env, "sys", "do the thing");

        Assert.Equal("completed", result.StopReason);
        Assert.Equal(3, client.CallCount);

        var assistantMessages = result.Messages.Where(m => m.Role == ChatRole.Assistant).ToList();
        Assert.Equal(3, assistantMessages.Count);

        // No message object appears in history more than once.
        var repeated = result.Messages
            .GroupBy(m => m, ReferenceEqualityComparer.Instance)
            .Count(g => g.Count() > 1);
        Assert.Equal(0, repeated);
    }

    /// <summary>The nudge itself must still fire — the fix removes a duplicate
    /// append, not the recovery behaviour it belonged to.</summary>
    [Fact]
    public async Task NoToolEngagementNudge_StillFires_AndStillCapsAtTwo()
    {
        var events = new ConcurrentQueue<Event>();
        var client = new ScriptedClient(Text("I will look into the task now."));
        var caps = new AgentCapabilities(
            new Dictionary<string, ToolFn>(), new List<JsonElement>(), new List<MiddlewareFn>());
        var env = new AgentEnvironment(new PorcelainSandbox(""), "sess", 10, events.Enqueue);

        await AgentLoop.RunAsync(new LlmSettings(client, "test-model"), caps, env, "sys", "go");

        var nudges = events.Where(e => e.Type == "no_tool_engagement_nudge").ToList();
        Assert.Equal(2, nudges.Count);
        Assert.Equal(1, nudges[0].Data["retry_count"]);
        Assert.Equal(2, nudges[1].Data["retry_count"]);
    }

    // ---- premise check, kept as a regression guard --------------------------

    /// <summary>
    /// CHARACTERISATION OF A KNOWN, UNFIXED GAP — this test asserts today's
    /// behaviour, NOT the behaviour anyone wants.
    ///
    /// AgentLoop.cs:401-413 catches OperationCanceledException and returns
    /// Finalize(state, "cancelled"), and its comment says why: "Preserve
    /// partial state by returning a 'cancelled' result instead of propagating
    /// — that lets the coordinator cache the messages so continue_task can
    /// resume the member with whatever it had so far."
    ///
    /// That catch only covers the iteration BODY. The boundary check at
    /// AgentLoop.cs:306 (`ct.ThrowIfCancellationRequested()`) sits above the
    /// try, so when cancellation lands while a turn is in flight and the turn
    /// finishes anyway — the ordinary shape when the member watchdog fires —
    /// the next loop pass throws instead, the partial state is lost, and
    /// Coordinator.cs:541-558 substitutes a synthetic result carrying
    /// Iterations = 0 / InputTokens = 0 / OutputTokens = 0. Real iterations
    /// and real tokens are reported as zero.
    ///
    /// Not fixed here: making the boundary return instead of throw stops
    /// Coordinator.cs:528 from firing, which is where a cancelled dispatch's
    /// worktree gets discarded, and changes what the leader is told when a
    /// member stalls. That needs the coordinator's owner.
    ///
    /// IF THIS TEST GOES RED because the loop now returns StopReason
    /// "cancelled" with a non-zero Iterations, the gap was fixed — delete the
    /// test, don't restore the throw.
    /// </summary>
    [Fact]
    public async Task Cancellation_AtTheIterationBoundary_PropagatesInsteadOfPreservingPartialState()
    {
        using var cts = new CancellationTokenSource();
        // Cancels while the turn is already in flight, then still returns a
        // valid tool call — so the iteration completes and the loop comes
        // back round to the boundary check with the token already cancelled.
        var client = new CancelDuringTurnClient(cts);
        var tools = new Dictionary<string, ToolFn> { ["noop"] = (_, _, _, _) => Task.FromResult("ok") };
        var caps = new AgentCapabilities(tools, new List<JsonElement>(), new List<MiddlewareFn>());
        var env = new AgentEnvironment(new PorcelainSandbox(""), "sess", 10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AgentLoop.RunAsync(new LlmSettings(client, "test-model"), caps, env, "sys", "go", cts.Token));

        // One real iteration's worth of work was done and then discarded.
        Assert.Equal(1, client.Calls);
    }

    private sealed class CancelDuringTurnClient(CancellationTokenSource cts) : IChatClient
    {
        public int Calls;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
        {
            Calls++;
            var msg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c" + Calls, "noop", new Dictionary<string, object?>()),
            });
            cts.Cancel();
            return Task.FromResult(new ChatResponse([msg]));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Recorded because it REFUTES a suspected defect: the brief asked whether
    /// a caller can tell "hit the iteration cap" from "finished normally". It
    /// can — Finalize stamps StopReason, and the cap path stamps
    /// "max_iterations" rather than "completed".
    /// </summary>
    [Fact]
    public async Task HittingTheIterationCap_IsDistinguishableFromCompleting()
    {
        // A model that always calls a tool never reaches the "no tool call =
        // done" exit, so the only way out is the cap.
        var client = new ScriptedClient(Call("c", "noop"));
        var tools = new Dictionary<string, ToolFn>
        {
            ["noop"] = (_, _, _, _) => Task.FromResult("ok"),
        };
        var caps = new AgentCapabilities(tools, new List<JsonElement>(), new List<MiddlewareFn>());
        var env = new AgentEnvironment(new PorcelainSandbox(""), "sess", 3);

        var result = await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model"), caps, env, "sys", "go");

        Assert.Equal("max_iterations", result.StopReason);
        Assert.Equal(3, result.Iterations);
    }

    // ---- 4. THE OUTPUT BOUND ----------------------------------------------
    //
    // THE DEFECT THESE ENCODE. Until 2026-09-01 vett sent no max_tokens at all
    // -- the string did not appear anywhere in src/, profiles/ or defaults/.
    // Null max_tokens is not a modest default: vLLM then permits
    // (max_model_len - prompt_tokens), so on a 204,800-token model with a 31k
    // prompt one call may generate ~173,000 tokens.
    //
    // MEASURED during EpicForge rung E3, while the stall was still live:
    // /metrics reported num_requests_running=2, num_requests_waiting=0, and
    // generation_tokens_total rising ~72 tok/s across the two requests while
    // prompt_tokens_total stayed FLAT -- the same two calls, still writing,
    // 800+ seconds after the run log had gone quiet. At ~36 tok/s per request
    // an unbounded call is ~80 MINUTES against request_timeout_seconds: 180,
    // so the timeout could never win: kill at 180s, retry, generate again.
    //
    // I had previously written this up as a client-side network stall. It was
    // not. The replay that "proved" the endpoint healthy returned in 16.1s
    // because it happened to generate a SHORT answer -- it reproduced the
    // prompt, never the output length, so it could not have caught this.

    private sealed class CapturingClient : IChatClient
    {
        public ChatOptions? Seen { get; private set; }
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Seen ??= options;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")]));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static async Task<(ChatOptions? seen, List<Event> events)> RunCapturing(int? bound)
    {
        var events = new ConcurrentQueue<Event>();
        var client = new CapturingClient();
        var caps = new AgentCapabilities(
            new Dictionary<string, ToolFn>(), new List<JsonElement>(), new List<MiddlewareFn>());
        var env = new AgentEnvironment(new PorcelainSandbox(""), "sess", 1, events.Enqueue);
        await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model", 1.0, null, bound), caps, env, "sys", "go");
        return (client.Seen, events.ToList());
    }

    [Fact]
    public async Task TheOutputBoundReachesTheWireAndTheLogSaysSo()
    {
        var (seen, events) = await RunCapturing(4096);

        // (a) it reaches the provider
        Assert.NotNull(seen);
        Assert.Equal(4096, seen!.MaxOutputTokens);

        // (b) and it is VISIBLE. A bound you cannot see in the log is a bound
        // you cannot prove was applied -- which is exactly how the unbounded
        // case hid: `llm_request` recorded temperature and top_p but had no
        // field for the one setting that was pathological.
        var req = events.First(e => e.Type == "llm_request");
        Assert.Equal(4096, req.Data["max_output_tokens"]);
    }

    [Fact]
    public async Task TheDefaultIsUnboundedAndTheLogPRINTSThatRatherThanOmittingIt()
    {
        // CHARACTERISATION, not an endorsement. The code default stays null so
        // existing profiles are untouched; the profiles that matter set it.
        // What must never regress is the VISIBILITY: "unbounded" has to be
        // readable in the log, because "no max_output_tokens line" and
        // "max_output_tokens: null" are different facts and only one is
        // evidence.
        var (seen, events) = await RunCapturing(null);

        Assert.NotNull(seen);
        Assert.Null(seen!.MaxOutputTokens);

        var req = events.First(e => e.Type == "llm_request");
        Assert.True(req.Data.ContainsKey("max_output_tokens"), "the field was OMITTED, not printed as null");
        Assert.Null(req.Data["max_output_tokens"]);
    }

    [Fact]
    public void TheYamlAliasActuallyBinds()
    {
        // The alias string is the kind of thing that silently fails to bind and
        // then reads as "the setting had no effect".
        var p = Yaml.ParseProfile("name: t\nllm:\n  model: m\n  max_output_tokens: 4096\n");
        Assert.Equal(4096, p.Llm.MaxOutputTokens);

        var unset = Yaml.ParseProfile("name: t\nllm:\n  model: m\n");
        Assert.Null(unset.Llm.MaxOutputTokens);
    }

}
