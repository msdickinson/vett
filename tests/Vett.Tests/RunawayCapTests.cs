using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Sandbox;
using Vett.Tools;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// <c>max_iterations</c> is a RUNAWAY DETECTOR. These pin what it is allowed to
/// do to an interactive session, and what it must still do to a batch run.
///
/// THE DEFECT, as it was actually hit: a vett-chat session on `ds-solo-flash`
/// (max_iterations: 200) died mid-work with
/// <c>--- Session ended (max_iterations) | 200 iterations ---</c>. Nothing had
/// run away. <c>state.Iteration</c> is a single counter that increments once per
/// LLM call and is NEVER reset, and the interactive loop parks at the input wait
/// and then CONTINUES THE SAME while-loop when the next message arrives. So
/// every question spends permanently from one session-wide budget: ask enough
/// ordinary questions and the session dies of old age, with the stop reason
/// blaming a runaway that never happened.
///
/// Two things were wrong and both are pinned below:
///   1. THE DENOMINATOR. A cap measured from zero over a cumulative counter
///      answers "has this conversation been going a while?", not "will this
///      request ever stop?".
///   2. THE VERDICT. It called Finalize(), which ends the session and takes the
///      conversation with it — the most destructive available response to a
///      condition whose whole point is that a human should look at it.
///
/// The non-interactive path is deliberately untouched: `vett run` and the team
/// bench reconcile runs on <c>stop_reason: "max_iterations"</c>, and there the
/// whole run IS one turn, so the cumulative count is the right denominator.
/// </summary>
public class RunawayCapTests
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

    /// <summary>Answers every request with plain prose and no tool call, which
    /// is how a turn ENDS: the interactive loop hands control back to the user.
    /// One iteration per question, exactly like a chat that behaves.</summary>
    private sealed class AnsweringClient : IChatClient
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type t, object? key = null) => null;
        public void Dispose() { }
    }

    /// <summary>A genuine runaway: every response is another tool call, so the
    /// turn never ends on its own. This is the thing the cap exists for.
    ///
    /// The ceiling is not decoration. Every test here asserts that SOMETHING
    /// stops this client, so the failure mode of a broken cap is an unbounded
    /// loop — which as a test outcome is a hung suite, the one result that
    /// reports nothing. The ceiling converts that into a named failure.</summary>
    private sealed class RunawayClient : IChatClient
    {
        // 1,000 is ~140x the largest cap any test here installs (7), so it can
        // never fire on a working cap — but it fires in SECONDS rather than the
        // 36 CPU-minutes 50,000 took when a mutation arm deleted the bound. A
        // safety valve that takes half an hour to trip makes the positive-control
        // sweep unaffordable, which is the same as not having one.
        private const int Ceiling = 1_000;
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var n = Interlocked.Increment(ref _calls);
            if (n > Ceiling)
                throw new InvalidOperationException(
                    $"the runaway was never stopped — {Ceiling} LLM calls in one run");
            // Each spin carries a DIFFERENT argument on purpose. Since 2026-09-05
            // (EpicForge law 129) the loop refuses a sole call that repeats the
            // previous one byte-for-byte after two identical results, and ends
            // the run `repeated_call_exhausted` at the fifth identical issue --
            // so an argless `spin` forever would be stopped by THAT detector at
            // call 5 and never reach the iteration cap this rig measures. A
            // runaway that keeps changing what it asks for is still a runaway,
            // and only the iteration cap can stop it. The stimulus changed; no
            // assertion below did.
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant,
                new List<AIContent> { new FunctionCallContent($"c{n}", "spin", new Dictionary<string, object?> { ["n"] = n }) })]));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type t, object? key = null) => null;
        public void Dispose() { }
    }

    private static AgentCapabilities NoTools() => new([], [], []);

    /// <summary>One tool that does nothing, so a runaway client's calls resolve
    /// and the loop keeps going rather than erroring out on an unknown tool.</summary>
    private static AgentCapabilities SpinTool() => new(
        new Dictionary<string, ToolFn>
        {
            ["spin"] = (_, _, _, _) => Task.FromResult("spun"),
        }, [], []);

    private sealed class Recorder
    {
        public readonly List<Event> Events = [];
        public readonly List<string> AssistantText = [];
        public int Count(string type) => Events.Count(e => e.Type == type);
    }

    /// <summary>The chat environment: a person is reading and can type.</summary>
    private static AgentEnvironment Env(Recorder rec, int max, ChannelReader<bool>? wake = null) =>
        new(new NoSandbox(), "runaway-cap-test", MaxIterations: max,
            OnEvent: e => { lock (rec.Events) rec.Events.Add(e); },
            WakeSignal: wake, HumanAtTheKeyboard: true);

    /// <summary>A userInput channel with NOBODY on the other end — a team leader
    /// under the coordinator, or the bench harness. Same entry point, same
    /// channel, no human. This is the environment the park must NOT apply
    /// to.</summary>
    private static AgentEnvironment HeadlessEnv(Recorder rec, int max) =>
        new(new NoSandbox(), "runaway-cap-test", MaxIterations: max,
            OnEvent: e => { lock (rec.Events) rec.Events.Add(e); });

    // ---- 1. the regression that was actually hit --------------------------

    /// <summary>
    /// THE BUG. A well-behaved conversation, longer than the cap, must not die.
    ///
    /// Twelve ordinary questions against a cap of five. Nothing runs away —
    /// every turn costs exactly one iteration and ends by handing control back.
    /// Against the old cumulative condition the loop fell out after the fifth,
    /// leaving seven questions unanswered and reporting `max_iterations` as
    /// though the agent were at fault.
    ///
    /// Asserting the reply count and not just the stop reason is deliberate: a
    /// session can end for several reasons, but only one number says the work
    /// actually got done.
    /// </summary>
    [Fact]
    public async Task ALongHealthyConversationOutlivesTheIterationCap()
    {
        const int Cap = 5;
        const int Questions = 12;

        var rec = new Recorder();
        var client = new AnsweringClient();
        var ch = Channel.CreateUnbounded<string>();
        for (var i = 0; i < Questions; i++) ch.Writer.TryWrite($"question {i}");
        ch.Writer.Complete();

        var result = await AgentLoop.RunInteractiveAsync(
            new LlmSettings(client, "test-model"), NoTools(), Env(rec, Cap),
            "SYSTEM", ch.Reader,
            onAssistantText: t => { lock (rec.AssistantText) rec.AssistantText.Add(t); },
            onWaitingForInput: null, CancellationToken.None);

        Assert.Equal(Questions, client.CallCount);
        Assert.Equal(Questions, rec.AssistantText.Count(t => t == "answer"));

        // No runaway happened, so the detector must not have fired once.
        Assert.Equal(0, rec.Count("runaway_paused"));

        // It ended because the user's channel closed, which is the only honest
        // reason available here.
        Assert.Equal("user_closed", result.StopReason);
    }

    // ---- 2. the cap still caps ---------------------------------------------

    /// <summary>
    /// The other side of the same coin, and the one that keeps this from being
    /// a fix that just deletes the protection: a single turn that will never
    /// stop must still be stopped, at exactly the cap.
    ///
    /// The LLM call count is the assertion that matters. "It emitted an event"
    /// would also pass for a loop that emitted the event and then carried on
    /// spending money.
    /// </summary>
    [Fact]
    public async Task AGenuineRunawayInsideOneTurnStillStopsAtTheCap()
    {
        const int Cap = 6;

        var rec = new Recorder();
        var client = new RunawayClient();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("go");

        var run = AgentLoop.RunInteractiveAsync(
            new LlmSettings(client, "test-model"), SpinTool(), Env(rec, Cap),
            "SYSTEM", ch.Reader,
            onAssistantText: t => { lock (rec.AssistantText) rec.AssistantText.Add(t); },
            onWaitingForInput: null, CancellationToken.None);

        // The loop is now parked at the cap waiting for a human. Closing the
        // channel is that human saying nothing further.
        await WaitUntil(() => rec.Count("runaway_paused") == 1,
            "the runaway was never stopped — the loop is still spending");
        var callsAtPause = client.CallCount;
        ch.Writer.Complete();
        var result = await run;

        Assert.Equal(Cap, callsAtPause);
        Assert.Equal(Cap, client.CallCount);
        Assert.Equal(1, rec.Count("runaway_paused"));

        // Parked, then closed — NOT terminated by the cap.
        Assert.Equal("user_closed", result.StopReason);
    }

    /// <summary>
    /// A stop the user can read. The notice has to say what happened, that the
    /// session survived it, and what to do — an operator staring at a stalled
    /// chat has nothing else to go on.
    /// </summary>
    [Fact]
    public async Task TheRunawayStopExplainsItselfAndSaysTheSessionSurvived()
    {
        var rec = new Recorder();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("go");

        var run = AgentLoop.RunInteractiveAsync(
            new LlmSettings(new RunawayClient(), "test-model"), SpinTool(), Env(rec, 4),
            "SYSTEM", ch.Reader,
            onAssistantText: t => { lock (rec.AssistantText) rec.AssistantText.Add(t); },
            onWaitingForInput: null, CancellationToken.None);

        await WaitUntil(() => rec.Count("runaway_paused") == 1, "never paused");
        ch.Writer.Complete();
        await run;

        string notice;
        lock (rec.AssistantText) notice = Assert.Single(rec.AssistantText);

        Assert.Contains("4 steps", notice);
        Assert.Contains("max_iterations", notice);
        Assert.Contains("still here", notice);      // the session was NOT lost
        Assert.Contains("Send another message", notice);
    }

    // ---- 3. parking is not dying -------------------------------------------

    /// <summary>
    /// After the stop, one message carries on — with a whole fresh budget, not
    /// a single grudging iteration. This is the difference between a pause and
    /// a death, and the second batch of calls is what proves it.
    /// </summary>
    [Fact]
    public async Task AfterTheStopTheUserCanCarryOnWithAFullBudget()
    {
        const int Cap = 5;

        var rec = new Recorder();
        var client = new RunawayClient();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("go");

        var run = AgentLoop.RunInteractiveAsync(
            new LlmSettings(client, "test-model"), SpinTool(), Env(rec, Cap),
            "SYSTEM", ch.Reader, onAssistantText: null, onWaitingForInput: null,
            CancellationToken.None);

        await WaitUntil(() => rec.Count("runaway_paused") == 1, "never paused");
        Assert.Equal(Cap, client.CallCount);

        ch.Writer.TryWrite("keep going");

        await WaitUntil(() => rec.Count("runaway_paused") == 2,
            "the second turn never got its own budget");
        Assert.Equal(Cap * 2, client.CallCount);

        ch.Writer.Complete();
        var result = await run;
        Assert.Equal("user_closed", result.StopReason);
    }

    // ---- 4. what must NOT reset the budget ---------------------------------

    /// <summary>
    /// A wake signal is not consent.
    ///
    /// Background task completions wake the loop on their own schedule. If a
    /// wake cleared the stop, a chat with a periodic background task would hand
    /// the runaway a fresh budget forever and the cap would not exist at all —
    /// a fix for "it stops too eagerly" that quietly deletes the protection.
    /// Only a human's message resumes.
    /// </summary>
    [Fact]
    public async Task AWakeSignalAloneDoesNotHandBackAFreshBudget()
    {
        const int Cap = 4;

        var rec = new Recorder();
        var client = new RunawayClient();
        var ch = Channel.CreateUnbounded<string>();
        var wake = Channel.CreateUnbounded<bool>();
        ch.Writer.TryWrite("go");

        var run = AgentLoop.RunInteractiveAsync(
            new LlmSettings(client, "test-model"), SpinTool(), Env(rec, Cap, wake.Reader),
            "SYSTEM", ch.Reader, onAssistantText: null, onWaitingForInput: null,
            CancellationToken.None);

        await WaitUntil(() => rec.Count("runaway_paused") == 1, "never paused");
        Assert.Equal(Cap, client.CallCount);

        // Poke it the way a finishing background task would, repeatedly.
        for (var i = 0; i < 5; i++)
        {
            wake.Writer.TryWrite(true);
            await Task.Delay(20);
        }

        Assert.Equal(Cap, client.CallCount);
        Assert.Equal(1, rec.Count("runaway_paused"));

        // And a real message still works, so this is a closed door and not a
        // wedged one.
        ch.Writer.TryWrite("carry on");
        await WaitUntil(() => client.CallCount == Cap * 2, "a real message did not resume it");

        ch.Writer.Complete();
        await run;
    }

    // ---- 5. the batch path is untouched ------------------------------------

    /// <summary>
    /// Non-interactive runs must be bit-for-bit unchanged. `vett run` and the
    /// team bench reconcile results on <c>stop_reason: "max_iterations"</c>, and
    /// there the whole run really is one turn, so the cumulative count is the
    /// correct denominator. A repair that silently made batch runs unbounded
    /// would be a far worse bug than the one being fixed.
    /// </summary>
    [Fact]
    public async Task ANonInteractiveRunStillTerminatesAtTheCapWithTheSameStopReason()
    {
        const int Cap = 7;

        var rec = new Recorder();
        var client = new RunawayClient();

        var result = await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model"), SpinTool(), Env(rec, Cap),
            "SYSTEM", "do the thing", CancellationToken.None);

        Assert.Equal("max_iterations", result.StopReason);
        Assert.Equal(Cap, result.Iterations);
        Assert.Equal(Cap, client.CallCount);

        // The interactive-only park must not leak into batch runs.
        Assert.Equal(0, rec.Count("runaway_paused"));
    }

    // ---- 6. the over-correction the full suite actually caught -------------

    /// <summary>
    /// The first version of this repair asked "is there a userInput channel?"
    /// and parked whenever the answer was yes. That is the wrong question. A
    /// team leader and the bench harness are BOTH driven through
    /// RunInteractiveAsync with a real channel, and nobody types into either
    /// one — so they parked forever on input that was never coming. Two
    /// existing tests went red on it
    /// (ConfigResolutionAuditTests.TeamLeader_OmittingItsOwnCap_Gets50…, which
    /// saw "user_closed" where "max_iterations" belonged, and
    /// HarnessStopReasonWiringTests.Exhausted_leader_publishes… , whose leader
    /// simply never ended). Parking is only ever better than terminating when
    /// somebody is there to unpark it; with no human it is strictly worse,
    /// because a hang reports nothing at all.
    ///
    /// The right question is whether a PERSON is present, which is what
    /// HumanAtTheKeyboard answers, and it defaults to false so that a caller
    /// who never considered it keeps the old terminate semantics.
    /// </summary>
    [Fact]
    public async Task AnInteractiveChannelWithNoHumanOnItStillTerminatesRatherThanParkingForever()
    {
        const int Cap = 6;

        var rec = new Recorder();
        var client = new RunawayClient();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("go");   // the coordinator's opening instruction
        // Deliberately NOT completed: the channel stays open exactly as a real
        // leader's does, so "it terminated" cannot be an artifact of a closed
        // channel. If the loop parks here it waits on this channel forever and
        // the test times out instead of passing.
        var run = AgentLoop.RunInteractiveAsync(
            new LlmSettings(client, "test-model"), SpinTool(), HeadlessEnv(rec, Cap),
            "SYSTEM", ch.Reader, null, null, CancellationToken.None);

        var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(done == run,
            "the headless leader never terminated — it parked on a channel no human will ever write to");

        var result = await run;
        Assert.Equal("max_iterations", result.StopReason);
        Assert.Equal(0, rec.Count("runaway_paused"));
    }

    // ---- helper ------------------------------------------------------------

    /// <summary>
    /// Poll for a condition with a hard ceiling. A bare Task.Delay would make
    /// every test above a race that passes on a fast machine; a hang would make
    /// the suite unrunnable. This fails loudly instead of doing either.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"timed out after 10s: {failure}");
    }
}
