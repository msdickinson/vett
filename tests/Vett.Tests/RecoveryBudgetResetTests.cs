using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// ⭐⭐ A RECOVERY BUDGET COUNTS CONSECUTIVE FAILURES, AND A SEAT THAT RE-ISSUES
/// THE SAME CALL FOR THE SAME ANSWER IS STOPPED BEFORE IT BURNS THE SEAT.
///
/// THE MEASUREMENT (EpicForge E8 `E-pg-e8-flat`, run `R-mtnoneab2it5`,
/// 2026-09-05; EpicForge HANDOFF law 129). One team run took 1104 s of wall
/// clock -- 50.4% of the whole epic -- and all of it was one seat.
/// `implementer-1` made 13 tool calls, 7 of them the IDENTICAL 217-byte
/// `terminal` payload ("Write hasBom.js with correct method name"), narrated
/// each attempt at length, was truncated four times doing so, and ended
/// `truncated_response_exhausted` with `files=1`. `implementer-2` then did the
/// whole job in a fraction of the time. The module in question was one line:
/// `src.charCodeAt(0) === 0xFEFF`.
///
/// TWO DEFECTS, READ FROM THE SOURCE, NOT GUESSED:
///
/// (1) `TruncatedResponseRetries`, `EmptyResponseRetries` and
///     `MalformedToolCallRetries` are incremented on every failure of their
///     kind and NEVER reset -- no `= 0` anywhere in the tree -- so a cap that
///     every comment around it describes as "consecutive" ("Cap retries on
///     consecutive empty responses"; "a model that truncates every single time
///     must not loop forever") is in fact a LIFETIME allowance. Four
///     truncations spread across 32 iterations, never two in a row, killed the
///     seat. EpicForge law 118 recorded the same mechanism ending an architect
///     at iteration 26 with its verdict half-written.
///
/// (2) Nothing interrupts a seat that issues the same call, with the same
///     arguments, and gets the same bytes back, again and again. vett's
///     StuckDetector counts error TURNS and text-only monologues; a successful
///     identical write seven times over is neither. The epic-forge runner's
///     `stuck_seats` metric SAW the streak (tool, argument, byte count) after
///     the fact, and nothing acted on it.
///
/// THE FIX, in AgentLoop: (1) a turn that is a real reply -- not empty, not
/// truncated-without-a-call, not a malformed call -- zeroes the three streak
/// counters, so each cap binds on consecutive failures as written; the
/// exhaustion count on an unbroken streak is unchanged (the persistent-
/// truncation test in TruncatedResponseRecoveryTests pins it at cap+1).
/// (2) When a sole tool call repeats the previous sole call byte-for-byte AND
/// the previous two runs of it returned byte-identical output, the third issue
/// is NOT run: the model gets its prior output back with a notice that says
/// so, a `repeated_call_break` event records it, and the fifth identical issue
/// ends the run `repeated_call_exhausted` -- a seat that ignores two explicit
/// notices is stuck by any definition, and freeing it is what lets a second
/// seat do the work. A poll that is still waiting (`check_task`, `wait_task`,
/// or any result carrying Builtins.StillRunningMarker) is never a repeat, and
/// a result that CHANGES between identical calls is a progressing poll, also
/// never a repeat.
///
/// Every test drives the REAL AgentLoop with a scripted client so the shipped
/// path is what is measured. No middleware is registered, so no other detector
/// can end a run and take credit for these assertions.
/// </summary>
public class RecoveryBudgetResetTests
{
    /// <summary>One scripted reply: what the model said and how the provider
    /// said it stopped. Built per request so call ids can differ per turn.</summary>
    private sealed record Step(Func<int, ChatMessage> Reply, ChatFinishReason? Finish);

    /// <summary>Plays the steps in order; the LAST step repeats forever.</summary>
    private sealed class SequenceClient(params Step[] steps) : IChatClient
    {
        private int _n;
        public int CallCount => _n;
        public List<List<ChatMessage>> Sent { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add(messages.ToList());
            var step = steps[Math.Min(_n, steps.Length - 1)];
            var msg = step.Reply(_n);
            _n++;
            return Task.FromResult(new ChatResponse([new ChatMessage(msg.Role, msg.Contents.ToList())])
            {
                FinishReason = step.Finish,
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

    private static JsonElement Schema(string name, string param) => JsonSerializer.Deserialize<JsonElement>(
        "{\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"parameters\":{\"type\":\"object\","
        + "\"properties\":{\"" + param + "\":{\"type\":\"string\"}}}}}");

    /// <summary>The E8 payload's shape: the same write, every time.</summary>
    private const string WriteCmd = "cat > hasBom.js <<'EOF'\nmodule.exports = (src) => src.charCodeAt(0) === 0xFEFF;\nEOF";

    private static ChatMessage Call(int n, string tool, string param, string value) =>
        new(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("c" + (n + 1), tool, new Dictionary<string, object?> { [param] = value }),
        });

    private static ChatMessage Prose(string s) => new(ChatRole.Assistant, s);
    private static ChatMessage Empty() => new(ChatRole.Assistant, new List<AIContent>());

    private static Step Truncated() => new(_ => Prose("Let me think about this carefully. 1.1 + 2.2 = 3.1? No. 1.1 + 2.2"), ChatFinishReason.Length);
    private static Step EmptyReply() => new(_ => Empty(), ChatFinishReason.Stop);
    private static Step Malformed() => new(_ => Prose("<tool_call>terminal:::command>"), ChatFinishReason.Stop);
    private static Step Write() => new(n => Call(n, "terminal", "command", WriteCmd), ChatFinishReason.ToolCalls);
    private static Step Done() => new(_ => Prose("All done."), ChatFinishReason.Stop);

    private sealed class Rig
    {
        public readonly ConcurrentQueue<Event> Events = new();
        public int TerminalRuns;
        public int CheckTaskRuns;
        public Func<int, string> TerminalResult = _ => "";

        public async Task<(SequenceClient client, AgentResult result)> Run(int maxIters, params Step[] steps)
        {
            var tools = new Dictionary<string, ToolFn>
            {
                ["terminal"] = (_, _, _, _) => Task.FromResult(TerminalResult(Interlocked.Increment(ref TerminalRuns))),
                ["check_task"] = (_, _, _, _) => { Interlocked.Increment(ref CheckTaskRuns); return Task.FromResult("Error: task t1 " + Builtins.StillRunningMarker + " (12s)"); },
            };
            var caps = new AgentCapabilities(tools, [Schema("terminal", "command"), Schema("check_task", "task_id")], new List<MiddlewareFn>());
            var env = new AgentEnvironment(new InertSandbox(), "sess", maxIters, Events.Enqueue);
            var client = new SequenceClient(steps);
            var result = await AgentLoop.RunAsync(new LlmSettings(client, "test-model"), caps, env, "sys", "go");
            return (client, result);
        }

        public List<Event> Of(string type) => Events.Where(e => e.Type == type).ToList();
        public int MaxRetryCount(string type) => Of(type).Select(e => Convert.ToInt32(e.Data["retry_count"])).DefaultIfEmpty(0).Max();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (1) THE COUNTERS ARE CONSECUTIVE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>The E8 arithmetic, reduced: eight truncations in a 12-turn
    /// session, never more than two in a row, a real tool turn between each
    /// pair. A lifetime cap of 3 kills this seat at the fourth truncation
    /// (turn 5). A consecutive cap lets it finish.</summary>
    [Fact]
    public async Task TruncationsSpreadAcrossASession_DoNotExhaustTheSeat()
    {
        var rig = new Rig { TerminalResult = n => "wrote " + n };
        var (_, result) = await rig.Run(40,
            Truncated(), Truncated(), new Step(n => Call(n, "terminal", "command", "echo 1"), ChatFinishReason.ToolCalls),
            Truncated(), Truncated(), new Step(n => Call(n, "terminal", "command", "echo 2"), ChatFinishReason.ToolCalls),
            Truncated(), Truncated(), new Step(n => Call(n, "terminal", "command", "echo 3"), ChatFinishReason.ToolCalls),
            Truncated(), Truncated(), Done());

        // Positive conjuncts: the truncations really happened and really were
        // recovered -- eight of them -- and the tool turns really ran.
        Assert.Equal(8, rig.Of("truncated_response_recovery").Count);
        Assert.Equal(3, rig.TerminalRuns);

        Assert.Equal("completed", result.StopReason);
        // The streak never got past two, because every third turn was real.
        Assert.Equal(2, rig.MaxRetryCount("truncated_response_recovery"));
    }

    /// <summary>⭐ THE CONTROL: the reset must not weaken the cap. A seat that
    /// truncates on every turn after one healthy call still exhausts at
    /// exactly cap+1 = 4 recoveries, with its own stop reason.</summary>
    [Fact]
    public async Task AnUnbrokenTruncationStreak_StillExhaustsAtCapPlusOne()
    {
        var rig = new Rig();
        var (_, result) = await rig.Run(40, Write(), Truncated());

        Assert.Equal("truncated_response_exhausted", result.StopReason);
        var rec = rig.Of("truncated_response_recovery");
        Assert.Equal(4, rec.Count);
        Assert.Equal([1, 2, 3, 4], rec.Select(e => Convert.ToInt32(e.Data["retry_count"])).ToArray());
    }

    [Fact]
    public async Task EmptyRepliesSpreadAcrossASession_DoNotExhaustTheSeat()
    {
        var rig = new Rig { TerminalResult = n => "wrote " + n };
        var (_, result) = await rig.Run(40,
            EmptyReply(), EmptyReply(), new Step(n => Call(n, "terminal", "command", "echo 1"), ChatFinishReason.ToolCalls),
            EmptyReply(), EmptyReply(), new Step(n => Call(n, "terminal", "command", "echo 2"), ChatFinishReason.ToolCalls),
            EmptyReply(), EmptyReply(), new Step(n => Call(n, "terminal", "command", "echo 3"), ChatFinishReason.ToolCalls),
            Done());

        Assert.Equal(6, rig.Of("empty_response_recovery").Count);
        Assert.Equal(3, rig.TerminalRuns);
        Assert.Equal("completed", result.StopReason);
        Assert.Equal(2, rig.MaxRetryCount("empty_response_recovery"));
    }

    [Fact]
    public async Task MalformedCallsSpreadAcrossASession_DoNotExhaustTheSeat()
    {
        var rig = new Rig { TerminalResult = n => "wrote " + n };
        var (_, result) = await rig.Run(40,
            Malformed(), Malformed(), new Step(n => Call(n, "terminal", "command", "echo 1"), ChatFinishReason.ToolCalls),
            Malformed(), Malformed(), new Step(n => Call(n, "terminal", "command", "echo 2"), ChatFinishReason.ToolCalls),
            Malformed(), Malformed(), new Step(n => Call(n, "terminal", "command", "echo 3"), ChatFinishReason.ToolCalls),
            Done());

        Assert.Equal(6, rig.Of("malformed_tool_call").Count);
        Assert.Equal(3, rig.TerminalRuns);
        Assert.Equal("completed", result.StopReason);
        Assert.Equal(2, rig.MaxRetryCount("malformed_tool_call"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) THE SAME CALL FOR THE SAME ANSWER IS NOT RUN A THIRD TIME
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>E8's seat, exactly: the same write, over and over, returning
    /// the same nothing. It runs twice. The third issue comes back unexecuted
    /// with a notice; the fifth ends the run with its own stop reason.</summary>
    [Fact]
    public async Task TheThirdIdenticalCallWithIdenticalResults_IsNotRun_AndTheFifthEndsTheRun()
    {
        var rig = new Rig { TerminalResult = _ => "" };
        var (client, result) = await rig.Run(40, Write());

        Assert.Equal(2, rig.TerminalRuns);
        Assert.Equal("repeated_call_exhausted", result.StopReason);
        // Issues 3, 4 and 5 were refused; the fifth also ended the run.
        var breaks = rig.Of("repeated_call_break");
        Assert.Equal([3, 4, 5], breaks.Select(e => Convert.ToInt32(e.Data["times_issued"])).ToArray());
        Assert.All(breaks, e => Assert.Equal("terminal", (string)e.Data["tool_name"]!));
        // Exactly five requests were made -- nothing looped past the exhaustion.
        Assert.Equal(5, client.CallCount);
    }

    /// <summary>Law 248 (EpicForge s16b/s16h): the first refusal compacts the
    /// history, so the retry is not a replay of the context that produced the
    /// repeat. The 4th request carries the digest, ONE copy of the call (the
    /// refused one, kept so its notice has a home) and the notice that says
    /// the history was compacted; the 3rd request carried two full copies.
    /// The exhaustion count is unchanged.</summary>
    [Fact]
    public async Task TheFirstRefusal_CompactsTheHistory_SoTheRetryIsNotAReplay()
    {
        var rig = new Rig { TerminalResult = _ => "" };
        var (client, result) = await rig.Run(40, Write());

        Assert.Equal("repeated_call_exhausted", result.StopReason);
        var compacted = rig.Of("compacted");
        var one = Assert.Single(compacted);
        Assert.Equal("repeated_call_retry", (string)one.Data["reason"]!);
        Assert.Equal(3, Convert.ToInt32(one.Data["times_issued"]));
        Assert.True(Convert.ToInt32(one.Data["after"]) < Convert.ToInt32(one.Data["before"]), "did not shrink");
        var breaks = rig.Of("repeated_call_break");
        Assert.Equal([true, false, false], breaks.Select(e => (bool)e.Data["compacted"]!).ToArray());

        static int Copies(List<ChatMessage> req) => req.Count(m =>
            m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Any(fc =>
                fc.Arguments is { } a && a.TryGetValue("command", out var v) && v?.ToString() == WriteCmd));
        Assert.Equal(2, Copies(client.Sent[2]));
        // Law 254 (EpicForge s21c, 2026-09-09): the compacted request carries
        // the refused call so its notice has a home, but NOT its body -- s21c
        // re-sent a 4,409-byte `create` a 4th and 5th time straight out of a
        // 5-message tail that held three verbatim copies of it. Every kept
        // call with the refused signature has its string arguments replaced
        // by an `<omitted: N chars ...>` marker.
        Assert.Equal(0, Copies(client.Sent[3]));
        static int Redacted(List<ChatMessage> req) => req.Count(m =>
            m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Any(fc =>
                fc.Arguments is { } a && a.TryGetValue("command", out var v)
                && v?.ToString() is { } s && s.StartsWith("<omitted: ", StringComparison.Ordinal) && s.Contains(WriteCmd.Length.ToString())));
        Assert.Equal(1, Redacted(client.Sent[3]));
        Assert.Equal(1, Convert.ToInt32(one.Data["redacted_bodies"]));
        // The refused call's own notice is in the compacted request, answering call c3.
        var notice = client.Sent[3].SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single(r => r.CallId == "c3");
        Assert.Contains("REPEATED CALL -- NOT RUN", notice.Result?.ToString());
        Assert.Contains("has been compacted", notice.Result?.ToString());
        // The 5th request (after the 4th issue, refused without compaction) says nothing about compaction.
        var notice4 = client.Sent[4].SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single(r => r.CallId == "c4");
        Assert.DoesNotContain("has been compacted", notice4.Result?.ToString());
        Assert.Equal(5, client.CallCount);
    }

    /// <summary>Law 248's power arm: a call whose result changes is never
    /// refused, and nothing compacts.</summary>
    [Fact]
    public async Task ACallWhoseResultChanges_NeverCompacts()
    {
        var rig = new Rig { TerminalResult = n => "wrote " + n };
        await rig.Run(6, Write());

        Assert.Empty(rig.Of("repeated_call_break"));
        Assert.Empty(rig.Of("compacted"));
    }

    /// <summary>⛔ THE LOG STAYS HONEST: a refused call has no tool_call_start
    /// and no tool_call_end. Crediting an unexecuted call with an end event
    /// would make every tool-count and every `tool_call:` assertion lie.</summary>
    [Fact]
    public async Task ARefusedCall_LeavesNoToolCallEvents()
    {
        var rig = new Rig { TerminalResult = _ => "" };
        await rig.Run(40, Write());

        Assert.Equal(2, rig.Of("tool_call_start").Count);
        Assert.Equal(2, rig.Of("tool_call_end").Count);
        Assert.Equal(3, rig.Of("repeated_call_break").Count);
    }

    /// <summary>The notice has to give the model what it needs to do
    /// something else: that the call was NOT run, how many times it has now
    /// asked, and the output it already has.</summary>
    [Fact]
    public async Task TheNoticeSaysNotRun_NamesTheCount_AndCarriesThePriorOutput()
    {
        var rig = new Rig { TerminalResult = _ => "hasBom.js written (217 bytes)" };
        var (client, _) = await rig.Run(40, Write());

        // The fourth request carries the result of the third (refused) call.
        Assert.True(client.Sent.Count >= 4);
        var toolMsg = client.Sent[3].Last(m => m.Role == ChatRole.Tool);
        var text = toolMsg.Contents.OfType<FunctionResultContent>().Single().Result?.ToString() ?? "";

        Assert.Contains("NOT RUN", text);
        Assert.Contains("3", text);
        Assert.Contains("hasBom.js written (217 bytes)", text);
        Assert.Contains("terminal", text);
    }

    /// <summary>⭐ CONTROL: identical arguments whose RESULT changes is a poll
    /// on something that is progressing (`tail` on a growing log, a test that
    /// is being fixed). It must run every time.</summary>
    [Fact]
    public async Task IdenticalCallsWithChangingResults_AreAProgressingPoll_AndAllRun()
    {
        var rig = new Rig { TerminalResult = n => "line " + n };
        var (_, result) = await rig.Run(6, Write());

        Assert.Equal(6, rig.TerminalRuns);
        Assert.Empty(rig.Of("repeated_call_break"));
        Assert.NotEqual("repeated_call_exhausted", result.StopReason);
    }

    /// <summary>⭐ CONTROL: a constant result to DIFFERENT calls is not a
    /// repeat -- `echo 1`, `echo 2`, ... each returning "ok" is ordinary work.</summary>
    [Fact]
    public async Task DifferentCallsWithTheSameResult_AreNotARepeat()
    {
        var rig = new Rig { TerminalResult = _ => "ok" };
        var (_, result) = await rig.Run(6, new Step(n => Call(n, "terminal", "command", "echo " + n), ChatFinishReason.ToolCalls));

        Assert.Equal(6, rig.TerminalRuns);
        Assert.Empty(rig.Of("repeated_call_break"));
    }

    /// <summary>⭐ CONTROL: a poll on a task that is still running returns the
    /// same benign status every time BY DESIGN. Breaking it would kill every
    /// leader that waits on a slow member -- the fan-out killer StuckDetector
    /// already had to be cured of (see its comment). Both routes are covered:
    /// the tool's name, and the marker in the result.</summary>
    [Fact]
    public async Task ABenignStillRunningPoll_IsNeverARepeat()
    {
        var byName = new Rig();
        await byName.Run(6, new Step(n => Call(n, "check_task", "task_id", "t1"), ChatFinishReason.ToolCalls));
        Assert.Equal(6, byName.CheckTaskRuns);
        Assert.Empty(byName.Of("repeated_call_break"));

        var byMarker = new Rig { TerminalResult = _ => "Error: job 7 " + Builtins.StillRunningMarker };
        await byMarker.Run(6, Write());
        Assert.Equal(6, byMarker.TerminalRuns);
        Assert.Empty(byMarker.Of("repeated_call_break"));
    }

    /// <summary>A turn that fans out (two calls at once) is not a sole call;
    /// it resets the streak, and the identical sole call after it runs.</summary>
    [Fact]
    public async Task AMultiCallTurnBetweenIdenticalCalls_ResetsTheStreak()
    {
        var rig = new Rig { TerminalResult = _ => "" };
        ChatMessage Two(int n) => new(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("a" + n, "terminal", new Dictionary<string, object?> { ["command"] = "ls" }),
            new FunctionCallContent("b" + n, "terminal", new Dictionary<string, object?> { ["command"] = "pwd" }),
        });
        var (_, result) = await rig.Run(40,
            Write(), Write(), new Step(Two, ChatFinishReason.ToolCalls), Write(), Write(), Done());

        // 2 writes + 2 (the fan-out) + 2 writes = 6 executions, no refusal.
        Assert.Equal(6, rig.TerminalRuns);
        Assert.Empty(rig.Of("repeated_call_break"));
        Assert.Equal("completed", result.StopReason);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // THE TWO HALVES TOGETHER: THE SHAPE E8 ACTUALLY HAD
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Write, narrate until truncated, write again, narrate until
    /// truncated ... Under the old code this died `truncated_response_exhausted`
    /// at the fourth truncation, after an unbounded number of identical writes
    /// had already run. Under the fix the truncation streak never exceeds 1
    /// (each write resets it), the write runs exactly twice, and the seat is
    /// released by the repeat break at the fifth identical issue -- for the
    /// reason that is actually true of it.</summary>
    [Fact]
    public async Task TheE8Shape_EndsAsARepeatedCall_NotAsATruncation()
    {
        var rig = new Rig { TerminalResult = _ => "" };
        var (_, result) = await rig.Run(60,
            Write(), Truncated(), Write(), Truncated(), Write(), Truncated(), Write(), Truncated(), Write());

        Assert.Equal("repeated_call_exhausted", result.StopReason);
        Assert.Equal(2, rig.TerminalRuns);
        Assert.Equal(1, rig.MaxRetryCount("truncated_response_recovery"));
        Assert.Equal(4, rig.Of("truncated_response_recovery").Count);
    }
}
