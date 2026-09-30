using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Sandbox;

namespace Vett.Tests;

/// <summary>
/// ⭐⭐ THE CEILING REACHES A RUNNING LEADER — not just a config object.
///
/// ⛔ WHY THIS FILE EXISTS SEPARATELY FROM <see cref="DispatchWidthCapTests"/>.
/// Those tests prove two things that are NOT the same as the claim on the tin:
/// that `TaskBoard` refuses past its ceiling, and that
/// `max_concurrent_dispatches:` deserialises off YAML. Both can be true while
/// `Coordinator` never copies the value onto the board it builds — the setting
/// would be **captured but not gated on**, the profile author would get exactly
/// the ceiling they asked for in the config object and none of it at dispatch,
/// and every test above would still be green.
///
/// So this drives the REAL `TeamCoordinator` with a scripted client and a fake
/// sandbox — no network, no model, no worktree, no `git` — and asserts on the
/// refusal string the leader actually receives back from its own tool call.
/// The path under test is the whole one: YAML shape → <see cref="TeamConfig"/>
/// → `Coordinator.cs:465` → `TaskBoard` → `assign_async`'s return value.
///
/// ⚠ DETERMINISM. Members BLOCK on a gate instead of returning promptly. If
/// they returned, a slot could free between the leader's tool calls and the
/// refusal would depend on thread timing — the test would pass or fail on
/// machine speed and prove nothing either way. The gate is released only after
/// the leader has seen its results, and in a `finally` so a failing assert can
/// never strand a background member.
/// </summary>
public class DispatchWidthCapEndToEndTests
{
    private const string MemberSystem = "MEMBER-SYSTEM-WIDTH";
    private const string MemberName = "worker";

    /// <summary>
    /// Ceiling of 2, leader asks for 4 at once: two land, two are refused, and
    /// the refusal text names both numbers so the leader can act on it.
    /// </summary>
    [Fact]
    public async Task A_leader_that_over_dispatches_gets_REFUSALS_naming_the_limit()
    {
        var outcome = await RunAsync(ceiling: 2, requested: 4);

        Assert.Equal(2, outcome.Accepted.Count);
        Assert.Equal(2, outcome.Refused.Count);

        // ⭐ THE POSITIVE CONJUNCT: a run where the leader never got to call the
        // tool at all would otherwise satisfy "no more than 2 accepted".
        Assert.All(outcome.Accepted, a => Assert.Contains("assigned to worker", a));

        foreach (var r in outcome.Refused)
        {
            Assert.Contains("dispatch limit reached", r);
            Assert.Contains("the limit is 2", r);       // the ceiling it hit
            Assert.Contains("2 task(s) already in flight", r);   // and why
            Assert.Contains("wait_task", r);            // and what to do instead
        }

        // The board never exceeded the ceiling on the way through.
        Assert.True(outcome.PeakInFlight <= 2,
            $"peak in-flight was {outcome.PeakInFlight}, ceiling was 2");
    }

    /// <summary>
    /// ⛔ THE REGRESSION GUARD FOR EVERY MEASURED ARM. With no ceiling set — the
    /// default, and the state every campaign number was measured under — all
    /// four dispatches land and the success string is the legacy one, with no
    /// budget suffix appended.
    /// </summary>
    [Fact]
    public async Task With_NO_ceiling_every_dispatch_lands_and_the_string_is_unchanged()
    {
        var outcome = await RunAsync(ceiling: 0, requested: 4);

        Assert.Equal(4, outcome.Accepted.Count);
        Assert.Empty(outcome.Refused);

        // The budget suffix is what an uncapped run must NOT grow.
        Assert.All(outcome.Accepted, a =>
        {
            Assert.DoesNotContain("dispatches in flight]", a);
            Assert.EndsWith("(running in background; results auto-deliver, or use cancel_task to stop).", a);
        });
    }

    /// <summary>
    /// When a ceiling IS set, an accepted dispatch tells the leader where it
    /// stands. Nothing else in VETT has ever told a manager its budget, which is
    /// the gap Mark named as *"so it never over does it and trashes"*.
    /// </summary>
    [Fact]
    public async Task An_accepted_dispatch_under_a_ceiling_reports_the_remaining_budget()
    {
        var outcome = await RunAsync(ceiling: 3, requested: 2);

        Assert.Equal(2, outcome.Accepted.Count);
        Assert.Empty(outcome.Refused);
        Assert.EndsWith("[1/3 dispatches in flight]", outcome.Accepted[0]);
        Assert.EndsWith("[2/3 dispatches in flight]", outcome.Accepted[1]);
    }

    /// <summary>
    /// ⛔ THE SYNCHRONOUS DOOR. Found by auditing the fix itself, not the
    /// diagnosis: `assign_task` called `board.Create` directly and so was waved
    /// through regardless of the ceiling.
    ///
    /// It can only ever add ONE concurrent member, because it blocks the
    /// leader — but "off by one, silently" is exactly the class where the number
    /// stops meaning what it says. A leader at its ceiling of 2 with async work
    /// in flight could open a third worker through this door and nothing would
    /// report it.
    ///
    /// ⭐ CLOSING EITHER DOOR ALONE LEAVES THE OTHER OPEN, so this asserts on the
    /// sync path specifically, with the async slots already taken.
    /// </summary>
    [Fact]
    public async Task The_SYNCHRONOUS_assign_task_is_capped_too_not_just_assign_async()
    {
        var outcome = await RunAsync(ceiling: 1, requested: 1, thenSyncAssign: true);

        Assert.Single(outcome.Accepted);                  // the async one took the only slot
        Assert.Single(outcome.Refused);                   // and the sync one was turned away
        Assert.Contains("dispatch limit reached", outcome.Refused[0]);
        Assert.Contains("BLOCKING assignment", outcome.Refused[0]);
        Assert.Contains("the limit is 1", outcome.Refused[0]);
    }

    /// <summary>
    /// The same sync path must be untouched when no ceiling is set — otherwise
    /// this fix would change every profile that has ever used `assign_task`,
    /// which is most of them.
    /// </summary>
    [Fact]
    public async Task With_NO_ceiling_the_synchronous_path_is_unchanged()
    {
        var outcome = await RunAsync(ceiling: 0, requested: 1, thenSyncAssign: true);

        Assert.Empty(outcome.Refused);
        Assert.Equal(2, outcome.Accepted.Count);   // the async dispatch AND the sync one
    }

    /// <summary>
    /// ⚠ THE OTHER ENTRY POINT. `TeamCoordinator.RunAsync` — the one-shot,
    /// non-interactive path — built its board bare while `RunInteractiveAsync`
    /// built one carrying the ceiling. A profile's `max_concurrent_dispatches`
    /// would have been honoured on one public entry point and silently ignored
    /// on the other.
    ///
    /// ⚠ LATENT, NOT LIVE: nothing in the repo calls it today (the bench harness
    /// and chat both use the interactive path). Tested anyway, because "wired but
    /// never proven" is the same class as "captured but never gated on".
    /// </summary>
    [Fact]
    public async Task The_ONE_SHOT_entry_point_honours_the_ceiling_as_well()
    {
        var outcome = await RunAsync(ceiling: 2, requested: 4, oneShot: true);

        Assert.Equal(2, outcome.Accepted.Count);
        Assert.Equal(2, outcome.Refused.Count);
        Assert.All(outcome.Refused, r => Assert.Contains("the limit is 2", r));
    }

    private sealed record RunOutcome(
        List<string> Accepted, List<string> Refused, int PeakInFlight);

    private static async Task<RunOutcome> RunAsync(
        int ceiling, int requested, bool thenSyncAssign = false, bool oneShot = false)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var memberGate = new SemaphoreSlim(0);

        // ⚠ MEMBERS PARK ONLY WHEN A CEILING IS SET. That is the only case where
        // a slot freeing early could change the answer, so it is the only case
        // that needs the determinism. With no ceiling the gate must stay OPEN:
        // `assign_task` is synchronous and awaits its member, so a parked member
        // would hang the leader's own tool call rather than test anything.
        if (ceiling <= 0) memberGate.Release(requested + 8);

        var profile = new Profile
        {
            Llm = new LlmConfig(),
            Team = new TeamConfig
            {
                Leader = new MemberConfig { Name = "leader", SystemPrompt = "LEADER-SYSTEM", MaxIterations = 6 },
                Members =
                {
                    new MemberConfig { Name = MemberName, SystemPrompt = MemberSystem, MaxIterations = 6, Tools = [] },
                },
                // Off: this test is about the ceiling, not about worktrees. It
                // also keeps the run off `git` and out of the shared dispatch tree.
                DispatchWorktree = false,
                DispatchMaxAgeDays = 0,
                AutoInjectAsyncResults = false,
                MaxConcurrentDispatches = ceiling,
            },
        };

        var client = new WidthScriptClient(requested, MemberName, memberGate, thenSyncAssign);
        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync("go");
        chan.Writer.Complete();

        try
        {
            if (oneShot)
                await TeamCoordinator.RunAsync(
                    profile, client, "test-model", new InertSandbox("/fake"), "width-test",
                    "go", onEvent: null, ct: cts.Token);
            else
                await TeamCoordinator.RunInteractiveAsync(
                    profile, client, "test-model", new InertSandbox("/fake"), "width-test",
                    chan.Reader,
                    onAssistantText: null, onWaitingForInput: null, onEvent: null,
                    seedHistory: null, turnInterrupt: null, compactRequest: null,
                    cwd: "/fake", ct: cts.Token);
        }
        finally
        {
            // Release generously and unconditionally: a failed assert above must
            // not leave a member parked on the gate for the rest of the suite.
            memberGate.Release(requested + 8);
        }

        return new RunOutcome(client.Accepted.ToList(), client.Refused.ToList(), client.PeakInFlight);
    }

    /// <summary>
    /// Leader emits <paramref name="requested"/> `assign_async` calls in ONE
    /// assistant message — the exact shape the tool's own description invites
    /// ("Multiple parallel calls ... are supported"). Members park on a gate so
    /// no slot can free mid-iteration.
    /// </summary>
    private sealed class WidthScriptClient(
        int requested, string memberName, SemaphoreSlim gate, bool thenSyncAssign = false) : IChatClient
    {
        public readonly ConcurrentQueue<string> Accepted = new();
        public readonly ConcurrentQueue<string> Refused = new();
        public int PeakInFlight;

        private int _leaderCalls;
        private int _live;
        private readonly object _lock = new();

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var list = messages.ToList();

            if (list.Any(m => (m.Text ?? "").Contains(MemberSystem)))
            {
                var now = Interlocked.Increment(ref _live);
                lock (_lock) if (now > PeakInFlight) PeakInFlight = now;
                try { await gate.WaitAsync(ct); }
                finally { Interlocked.Decrement(ref _live); }
                return Reply(new ChatMessage(ChatRole.Assistant,
                    "Reviewed, no edits. <self_assessment>done</self_assessment>"));
            }

            int k;
            lock (_lock) { k = ++_leaderCalls; }

            if (k == 1)
            {
                var calls = Enumerable.Range(0, requested).Select(i => (AIContent)
                    new FunctionCallContent($"c{i}", "assign_async", new Dictionary<string, object?>
                    {
                        ["member"] = memberName,
                        ["task"] = $"subtask {i}",
                    })).ToList();

                // The sync call rides in the SAME message, after the async ones.
                // Tool calls are processed in order, so by the time it runs the
                // async slots are already reserved — no timing assumption needed.
                if (thenSyncAssign)
                    calls.Add(new FunctionCallContent("sync", "assign_task", new Dictionary<string, object?>
                    {
                        ["member"] = memberName,
                        ["task"] = "the blocking one",
                    }));

                return Reply(new ChatMessage(ChatRole.Assistant, calls));
            }

            // Iteration 2: the leader's history now carries every tool result.
            // Sort them here rather than at the assert site so the test reads
            // the leader's OWN view of what happened, not the board's.
            foreach (var m in list)
                foreach (var c in m.Contents)
                    if (c is FunctionResultContent fr)
                    {
                        var text = fr.Result?.ToString() ?? "";
                        if (text.Contains("dispatch limit reached")) Refused.Enqueue(text);
                        // `assign_async` acknowledges with "assigned to <member>";
                        // the SYNCHRONOUS `assign_task` returns its member's
                        // finished work as "[<id> — <member> done]". Both are a
                        // dispatch that got a slot, so both count as accepted.
                        else if (text.Contains("assigned to") || text.Contains($"{memberName} done"))
                            Accepted.Enqueue(text);
                    }

            return Reply(new ChatMessage(ChatRole.Assistant, "Understood. Stopping here."));
        }

        private static ChatResponse Reply(ChatMessage m) => new([new ChatMessage(m.Role, m.Contents.ToList())]);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Sandbox that does nothing and shells out to nothing.</summary>
    private sealed class InertSandbox(string cwd) : ISandbox
    {
        public string Cwd => cwd;
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("", 0, cwd, false));
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default) => Task.FromResult("");
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string n, CancellationToken ct = default) => Task.FromResult(("", (string?)null));
        public Task<string> FileInsertAsync(string s, string p, int l, string n, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("");
        public Task SessionCreateAsync(string n, string c, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string newCwd) => new InertSandbox(newCwd);
        public ISandbox WithDispatchWorktree(string newCwd, string root) => new InertSandbox(newCwd);
    }
}
