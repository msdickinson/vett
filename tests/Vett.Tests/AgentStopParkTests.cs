using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Sandbox;
using Vett.Tools;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// What happens when the AGENT decides it is done.
///
/// THE DEFECT, as it was actually hit (2026-08-29): a vett-chat session
/// finished its work and the panel printed
/// <c>--- Session ended (finish_tool) | 130 iterations ---</c>. Mark:
/// "i dont love it ended conv before i had a chance to review it". The agent
/// calling <c>finish</c> set <c>StopLoop</c>, and the loop answered that by
/// calling Finalize() — ending the whole conversation, at the exact moment a
/// person most wants to read it and say "good, now also do X".
///
/// This is the same shape as the max_iterations defect pinned in
/// <see cref="RunawayCapTests"/>, in the same file, forty lines apart: a
/// condition that means "a human should look at this" answered with the most
/// destructive response available. Both are now parks.
///
/// THE SPLIT IS MARK'S OWN: "maybe when its vett chat it never does maybe reg
/// vett can. as its fully automatoous". So:
///   - chat, person present  -> the turn ends, the session lives
///   - `vett run` / bench    -> unchanged, terminates on the stop reason
///   - --finish-ends-session -> the old behaviour, for a script driving --stdio
///
/// The autonomous arm is not a leftover, it is load-bearing twice over: Runner
/// and the coordinator reconcile runs on these stop reasons, and parking with
/// nobody at the keyboard would hang forever on input that is never coming.
/// </summary>
public class AgentStopParkTests
{
    // ---- fixtures ---------------------------------------------------------

    private sealed class NoSandbox : ISandbox
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

    /// <summary>
    /// Calls <c>finish</c> on the FIRST response of every turn, then answers in
    /// prose. Two turns therefore cost two LLM calls, and the call count is what
    /// separates "the session carried on" from "the session died and the second
    /// question was never asked".
    /// </summary>
    private sealed class FinishingClient : IChatClient
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var n = Interlocked.Increment(ref _calls);
            if (n > 50)
                throw new InvalidOperationException("the loop never stopped calling the model");
            // Odd calls finish; even calls answer. In a park, the user's next
            // message starts a turn that ends the ordinary way, so the loop
            // reaches the normal input wait rather than re-parking forever.
            return Task.FromResult(n % 2 == 1
                ? new ChatResponse([new ChatMessage(ChatRole.Assistant,
                    new List<AIContent> { new FunctionCallContent($"c{n}", "finish",
                        new Dictionary<string, object?> { ["message"] = "all done" }) })])
                : new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type t, object? key = null) => null;
        public void Dispose() { }
    }

    /// <summary>The real `finish` tool plus the real detector that reads its
    /// marker. Using the shipped pair rather than a stub keeps the test honest
    /// about WHAT sets StopLoop.</summary>
    private static AgentCapabilities FinishTool() => new(
        new Dictionary<string, ToolFn>
        {
            ["finish"] = Builtins.All()["finish"],
        }, [], [BuiltinMiddleware.SubmitDetector]);

    /// <summary>The same, plus a middleware that stops the loop with a reason
    /// nothing in AgentLoop knows about — standing in for a plugin (Pool.cs
    /// supplies arbitrary stop reasons the same way).</summary>
    private static AgentCapabilities PluginStopTool(string reason) => new(
        new Dictionary<string, ToolFn>
        {
            ["finish"] = Builtins.All()["finish"],
        }, [],
        [
            (state, _) =>
            {
                foreach (var obs in state.LastObservations)
                {
                    if (obs.Result.StartsWith(Builtins.SubmitMarker))
                    {
                        state.StopLoop = true;
                        state.StopReason = reason;
                    }
                }
                return Task.CompletedTask;
            },
        ]);

    private sealed class Recorder
    {
        public readonly List<Event> Events = [];
        public readonly List<string> AssistantText = [];
        public int Count(string type) => Events.Count(e => e.Type == type);
    }

    /// <summary>A chat: a person is reading and can type.</summary>
    private static AgentEnvironment Env(Recorder rec, bool endsSession = false, ChannelReader<bool>? wake = null) =>
        new(new NoSandbox(), "stop-park-test", MaxIterations: 50,
            OnEvent: e => { lock (rec.Events) rec.Events.Add(e); },
            WakeSignal: wake, HumanAtTheKeyboard: true, AgentStopEndsSession: endsSession);

    /// <summary>A userInput channel with NOBODY on the other end: a team leader
    /// under the bench harness. Same entry point, same channel, no human.</summary>
    private static AgentEnvironment HeadlessEnv(Recorder rec) =>
        new(new NoSandbox(), "stop-park-test", MaxIterations: 50,
            OnEvent: e => { lock (rec.Events) rec.Events.Add(e); });

    private static async Task WaitUntil(Func<bool> cond, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(failure);
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Never `await run` bare. If the escape hatch under test is broken the
    /// agent parks forever, and a bare await turns that into a wedged test
    /// host: CI hangs instead of going red, which is a strictly worse signal
    /// than a failure. Mutation M3 did exactly this -- it was a kill, but it
    /// only announced itself by burning the job's whole time budget.
    /// </summary>
    private static async Task<AgentResult> Finished(Task<AgentResult> run, string what)
    {
        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))) != (Task)run)
            throw new TimeoutException("the agent loop never returned: " + what);
        return await run;
    }

    // ---- 1. the regression that was actually hit --------------------------

    /// <summary>
    /// THE BUG. The agent finishes, and the conversation is still there.
    ///
    /// Two questions. The agent calls `finish` on the first. Under the old
    /// behaviour the loop finalized right there and the second question was
    /// never asked — which is exactly what Mark saw, with 130 iterations of
    /// context going with it.
    ///
    /// The second LLM call is the whole assertion. "It emitted a park event"
    /// would also pass for a loop that announced a park and then returned
    /// anyway.
    /// </summary>
    [Fact]
    public async Task FinishingHandsControlBackInsteadOfEndingTheChat()
    {
        var rec = new Recorder();
        var client = new FinishingClient();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("do the thing");

        var run = AgentLoop.RunInteractiveAsync(
            new LlmSettings(client, "test-model"), FinishTool(), Env(rec),
            "SYSTEM", ch.Reader,
            onAssistantText: t => { lock (rec.AssistantText) rec.AssistantText.Add(t); },
            onWaitingForInput: null, CancellationToken.None);

        await WaitUntil(() => rec.Count("agent_stop_parked") == 1,
            "the agent finished and the session ended instead of parking");
        Assert.Equal(1, client.CallCount);

        // The human reviews and replies — the thing the old code made impossible.
        ch.Writer.TryWrite("good, now also do X");
        await WaitUntil(() => client.CallCount == 2, "the follow-up was never answered");

        ch.Writer.Complete();
        var result = await Finished(run, "the loop did not return after the channel closed");

        Assert.Equal(2, client.CallCount);
        Assert.Equal(1, rec.Count("agent_stop_parked"));
        Assert.Equal(1, rec.Count("resumed"));

        // It ended when the person closed the panel, not when the agent decided
        // it was done.
        Assert.Equal("user_closed", result.StopReason);
    }

    /// <summary>
    /// A stop the user can read. It has to say the work is done, that nothing is
    /// running, and that the session survived — a person looking at a quiet
    /// panel has nothing else to go on.
    /// </summary>
    [Fact]
    public async Task TheParkNoticeSaysTheWorkIsDoneAndTheSessionSurvived()
    {
        var rec = new Recorder();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("go");

        var run = AgentLoop.RunInteractiveAsync(
            new LlmSettings(new FinishingClient(), "test-model"), FinishTool(), Env(rec),
            "SYSTEM", ch.Reader,
            onAssistantText: t => { lock (rec.AssistantText) rec.AssistantText.Add(t); },
            onWaitingForInput: null, CancellationToken.None);

        await WaitUntil(() => rec.Count("agent_stop_parked") == 1, "never parked");
        ch.Writer.Complete();
        await Finished(run, "the loop did not return after the channel closed");

        string notice;
        lock (rec.AssistantText) notice = Assert.Single(rec.AssistantText);

        Assert.Contains("Finished", notice);
        Assert.Contains("Nothing is running", notice);
        Assert.Contains("still here", notice);          // the session was NOT lost
    }

    // ---- 2. the autonomous path is untouched ------------------------------

    /// <summary>
    /// `vett run`: no channel at all. The agent finishing IS the end of the run,
    /// and the stop reason is what Runner reads to decide the run was clean.
    /// </summary>
    [Fact]
    public async Task AnAutonomousRunStillEndsWhenTheAgentFinishes()
    {
        var rec = new Recorder();
        var client = new FinishingClient();

        var result = await Finished(AgentLoop.RunAsync(
            new LlmSettings(client, "test-model"), FinishTool(), HeadlessEnv(rec),
            "SYSTEM", "do the thing", CancellationToken.None),
            "`vett run` parked instead of ending -- an autonomous run would hang forever");

        Assert.Equal("finish_tool", result.StopReason);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, rec.Count("agent_stop_parked"));
    }

    /// <summary>
    /// The case the guard exists for, and the one a naive "if interactive" check
    /// would break: a team leader under the bench harness. It HAS a userInput
    /// channel, so "interactive" is true; nobody types into it, so parking would
    /// hang the run forever instead of terminating with the stop_reason the
    /// harness reconciles on.
    ///
    /// The test would not merely fail if this regressed — it would hang, which
    /// is why the assertion is wrapped in a timeout.
    /// </summary>
    [Fact]
    public async Task ALeaderWithAChannelButNoHumanStillEndsWhenItFinishes()
    {
        var rec = new Recorder();
        var client = new FinishingClient();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("do the thing");
        // Deliberately NOT completed: if the loop parks, there is nothing to
        // wake it and the wait below is what reports that.

        var run = AgentLoop.RunInteractiveAsync(
            new LlmSettings(client, "test-model"), FinishTool(), HeadlessEnv(rec),
            "SYSTEM", ch.Reader, onAssistantText: null, onWaitingForInput: null,
            CancellationToken.None);

        var result = await Finished(run,
            "a leader with no human parked instead of ending — the bench run would hang here");
        Assert.Equal("finish_tool", result.StopReason);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, rec.Count("agent_stop_parked"));
    }

    // ---- 3. the escape hatch ----------------------------------------------

    /// <summary>
    /// "there may be times its good it can" — Mark, same conversation. A script
    /// driving `vett chat --stdio` wants the process to exit when the agent
    /// declares done, and `--finish-ends-session` restores exactly that.
    ///
    /// Asserting the call count as well as the stop reason is the point: the
    /// second question is sitting in the channel, and a session that really
    /// ended never asks it.
    /// </summary>
    [Fact]
    public async Task TheEscapeHatchRestoresTheOldEnding()
    {
        var rec = new Recorder();
        var client = new FinishingClient();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("do the thing");
        ch.Writer.TryWrite("and another thing");

        var result = await Finished(AgentLoop.RunInteractiveAsync(
            new LlmSettings(client, "test-model"), FinishTool(), Env(rec, endsSession: true),
            "SYSTEM", ch.Reader, onAssistantText: null, onWaitingForInput: null,
            CancellationToken.None),
            "--finish-ends-session was ignored and the agent parked");

        Assert.Equal("finish_tool", result.StopReason);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, rec.Count("agent_stop_parked"));
    }

    // ---- 4. what the park reports -----------------------------------------

    /// <summary>
    /// Closing the panel while parked finalizes with the reason the agent
    /// actually stopped for, NOT "user_closed".
    ///
    /// The agent did finish. Reporting that session as abandoned would be a
    /// clean run recorded as an incomplete one, and StoppedCleanly reads this
    /// field.
    /// </summary>
    [Fact]
    public async Task ClosingThePanelWhileParkedKeepsTheReasonTheAgentStoppedFor()
    {
        var rec = new Recorder();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("go");

        var run = AgentLoop.RunInteractiveAsync(
            new LlmSettings(new FinishingClient(), "test-model"), FinishTool(), Env(rec),
            "SYSTEM", ch.Reader, onAssistantText: null, onWaitingForInput: null,
            CancellationToken.None);

        await WaitUntil(() => rec.Count("agent_stop_parked") == 1, "never parked");
        ch.Writer.Complete();
        var result = await Finished(run, "the loop did not return after the channel closed");

        Assert.Equal("finish_tool", result.StopReason);
    }

    /// <summary>
    /// A wake signal is not a reply. Background deliveries (a member finishing,
    /// an async result landing) arrive on their own schedule; resuming a
    /// finished agent on one would restart work nobody asked for.
    ///
    /// Same rule as the runaway park, and it has to be stated separately because
    /// the two parks wait on the same primitive and could easily diverge.
    /// </summary>
    [Fact]
    public async Task AWakeSignalDoesNotResumeAFinishedAgent()
    {
        var rec = new Recorder();
        var client = new FinishingClient();
        var wake = Channel.CreateUnbounded<bool>();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("go");

        var run = AgentLoop.RunInteractiveAsync(
            new LlmSettings(client, "test-model"), FinishTool(), Env(rec, wake: wake.Reader),
            "SYSTEM", ch.Reader, onAssistantText: null, onWaitingForInput: null,
            CancellationToken.None);

        await WaitUntil(() => rec.Count("agent_stop_parked") == 1, "never parked");
        Assert.Equal(1, client.CallCount);

        for (var i = 0; i < 5; i++) wake.Writer.TryWrite(true);
        await Task.Delay(300);

        // Still exactly one call: the wakes were absorbed, not obeyed.
        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, rec.Count("resumed"));

        ch.Writer.Complete();
        var result = await Finished(run, "the loop did not return after the channel closed");
        Assert.Equal("finish_tool", result.StopReason);
    }

    // ---- 5. no exclusion list ---------------------------------------------

    /// <summary>
    /// EVERY agent-side stop parks, not just `finish`.
    ///
    /// This is checked rather than assumed. Every assignment of StopLoop = true
    /// in the codebase is agent-side: the finish_tool detector, the two stuck:*
    /// detectors, and a plugin-supplied reason from Pool. The user-initiated
    /// endings (user_closed, user_interrupted, cancelled) never pass through
    /// that block at all — they call Finalize() directly. So there is no reason
    /// arriving there that a person should be denied the chance to see.
    ///
    /// A plugin reason is the strongest version of the claim: AgentLoop has
    /// never heard of "policy:halt" and must still park on it.
    /// </summary>
    [Fact]
    public async Task AnUnknownPluginStopReasonParksToo()
    {
        var rec = new Recorder();
        var client = new FinishingClient();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("go");

        var run = AgentLoop.RunInteractiveAsync(
            new LlmSettings(client, "test-model"), PluginStopTool("policy:halt"), Env(rec),
            "SYSTEM", ch.Reader,
            onAssistantText: t => { lock (rec.AssistantText) rec.AssistantText.Add(t); },
            onWaitingForInput: null, CancellationToken.None);

        await WaitUntil(() => rec.Count("agent_stop_parked") == 1,
            "a plugin stop reason ended the session instead of parking");

        string notice;
        lock (rec.AssistantText) notice = Assert.Single(rec.AssistantText);
        // The fallback notice names the reason, because a stop nobody planned
        // for is exactly the one the reader needs identified.
        Assert.Contains("policy:halt", notice);
        Assert.Contains("still open", notice);

        ch.Writer.TryWrite("carry on");
        await WaitUntil(() => client.CallCount == 2, "the follow-up was never answered");

        ch.Writer.Complete();
        var result = await Finished(run, "the loop did not return after the channel closed");
        Assert.Equal("user_closed", result.StopReason);
    }
}
