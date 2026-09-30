using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Sandbox;

namespace Vett.Tests;

/// <summary>
/// ⭐⭐ THE ADVICE IN A REFUSAL MUST NAME A TOOL THE LEADER ACTUALLY HAS.
///
/// ⛔ THE DEFECT. Both dispatch-limit refusals in <c>LeaderTools</c> told the
/// leader to call <c>wait_task</c> / <c>check_tasks</c>. In chat mode those
/// tools are not registered: <c>Coordinator</c> strips <c>check_task</c>,
/// <c>check_tasks</c> and <c>wait_task</c> from the leader's tool map AND its
/// schemas whenever <c>auto_inject_async_results</c> is on, so that a leader
/// cannot freeze the chat by polling. The engine was therefore instructing the
/// leader, at the exact moment it was blocked, to call a tool the engine had
/// just taken away.
///
/// ⛔ WHY IT IS NOT A COSMETIC BUG. The leader cannot comply. Best case it
/// burns an iteration on an unknown-tool error. Worst case — recorded in
/// <c>AgentLoop.cs</c> as "leader emitted FIVE check_task calls as plain
/// text" — it writes the call as PROSE: no tool runs, no error is raised, and
/// the turn reads as the model rambling rather than as the harness misleading
/// it. That is a defect that gets attributed to the model.
///
/// ⭐ WHY THIS ASSERTS A RELATION AND NOT A STRING. The bug was two sites
/// DISAGREEING, so pinning the new wording with <c>Assert.Contains</c> would
/// re-create exactly the coupling that broke: the pair could drift apart again
/// and both tests would stay green. Instead the test extracts every
/// backtick-quoted tool name from the refusal the leader actually received and
/// requires it to be present in the tool table the leader was actually handed,
/// in the SAME run. Add a tool, rename one, strip a different one — the
/// invariant still holds without anyone editing this file.
///
/// ⚠ THE VACUITY TRAP. In auto-inject mode the corrected advice names no tool
/// at all, so the relation above is satisfied by an empty set — it would pass
/// against a refusal that said nothing useful, and it would have passed against
/// a build where the strip never happened. Both arms therefore carry positive
/// conjuncts: the non-auto arm must yield at least one backticked name (proving
/// the extractor can see one), and the auto arm must show the poll tools really
/// absent from the table (proving there was something to get wrong).
/// </summary>
public class DispatchLimitAdviceMatchesToolTableTests
{
    private const string MemberSystem = "MEMBER-SYSTEM-ADVICE";
    private const string MemberName = "worker";

    /// <summary>Tools Coordinator strips when auto-inject is on.</summary>
    private static readonly string[] PollTools = ["wait_task", "check_task", "check_tasks"];

    /// <summary>
    /// THE INVARIANT, over both modes: a refusal may only recommend tools that
    /// the leader receiving it can actually call.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refusal_never_recommends_a_tool_the_leader_was_not_given(bool autoInject)
    {
        var run = await RunAsync(autoInject);

        Assert.NotEmpty(run.Refused);            // the ceiling was actually hit
        Assert.NotEmpty(run.LeaderTools);        // and we really observed a tool table

        foreach (var refusal in run.Refused)
        {
            foreach (var named in ToolNamesIn(refusal))
            {
                Assert.True(run.LeaderTools.Contains(named),
                    $"the refusal tells the leader to call `{named}`, which is NOT in its tool " +
                    $"table (auto_inject={autoInject}). Table: {string.Join(", ", run.LeaderTools.OrderBy(x => x))}" +
                    $"\nRefusal was:\n{refusal}");
            }
        }
    }

    /// <summary>
    /// ⭐ THE ARM THAT WAS BROKEN. Chat mode: the poll tools are gone from the
    /// table, and the advice must not send the leader after them.
    /// </summary>
    [Fact]
    public async Task In_chat_mode_the_poll_tools_are_stripped_AND_the_refusal_stops_naming_them()
    {
        var run = await RunAsync(autoInject: true);

        // POSITIVE CONJUNCT — without this the assertion below passes on a build
        // where the strip silently stopped happening, i.e. where there was
        // nothing to get wrong in the first place.
        foreach (var t in PollTools)
            Assert.False(run.LeaderTools.Contains(t),
                $"`{t}` is still registered in chat mode — Coordinator's strip did not run, so " +
                "this test no longer covers the case it was written for.");

        Assert.NotEmpty(run.Refused);
        foreach (var refusal in run.Refused)
        {
            Assert.Contains("dispatch limit reached", refusal);
            foreach (var t in PollTools)
                Assert.DoesNotContain(t, refusal);

            // And it must still say what to do instead, or the fix merely
            // deleted the advice: ending the turn IS the correct move here,
            // because completions are auto-delivered into a later turn.
            Assert.Contains("End your turn", refusal);
        }
    }

    /// <summary>
    /// ⛔ THE CONTROL. The fix must be MODE-AWARE, not a blanket deletion. With
    /// auto-inject off the poll tools exist, and the refusal must still point at
    /// them — otherwise a leader that CAN wait is left with no way to.
    /// </summary>
    [Fact]
    public async Task Outside_chat_mode_the_poll_tools_exist_and_the_refusal_still_recommends_them()
    {
        var run = await RunAsync(autoInject: false);

        Assert.Contains("wait_task", run.LeaderTools);
        Assert.Contains("check_tasks", run.LeaderTools);

        Assert.NotEmpty(run.Refused);
        foreach (var refusal in run.Refused)
        {
            Assert.Contains("wait_task", refusal);

            // The extractor must be able to SEE a name here. If this ever finds
            // none, the Theory above is passing vacuously in both arms.
            Assert.NotEmpty(ToolNamesIn(refusal));
        }
    }

    /// <summary>
    /// Backtick-quoted identifiers in a refusal. Tool names are the only thing
    /// VETT backticks in these strings; the pattern is deliberately narrow so a
    /// backticked flag or path cannot be mistaken for a tool.
    /// </summary>
    private static HashSet<string> ToolNamesIn(string text) =>
        Regex.Matches(text, "`([a-z][a-z0-9_]*)`")
             .Select(m => m.Groups[1].Value)
             .ToHashSet(StringComparer.Ordinal);

    private sealed record AdviceRun(
        List<string> Refused, List<string> Accepted, HashSet<string> LeaderTools);

    private static async Task<AdviceRun> RunAsync(bool autoInject)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var memberGate = new SemaphoreSlim(0);

        var profile = new Profile
        {
            Llm = new LlmConfig(),
            Team = new TeamConfig
            {
                Leader = new MemberConfig { Name = "leader", SystemPrompt = "LEADER-SYSTEM-ADVICE", MaxIterations = 6 },
                Members =
                {
                    new MemberConfig { Name = MemberName, SystemPrompt = MemberSystem, MaxIterations = 6, Tools = [] },
                },
                DispatchWorktree = false,
                DispatchMaxAgeDays = 0,
                AutoInjectAsyncResults = autoInject,
                MaxConcurrentDispatches = 2,
            },
        };

        var client = new AdviceScriptClient(requested: 4, memberName: MemberName, gate: memberGate);
        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync("go");
        chan.Writer.Complete();

        try
        {
            await TeamCoordinator.RunInteractiveAsync(
                profile, client, "test-model", new AdviceInertSandbox("/fake"), "advice-test",
                chan.Reader,
                onAssistantText: null, onWaitingForInput: null, onEvent: null,
                seedHistory: null, turnInterrupt: null, compactRequest: null,
                cwd: "/fake", ct: cts.Token);
        }
        finally
        {
            // Unconditional, generous: a failing assert must never strand a
            // parked member for the rest of the suite.
            memberGate.Release(16);
        }

        return new AdviceRun(client.Refused.ToList(), client.Accepted.ToList(), client.LeaderTools);
    }

    /// <summary>
    /// Same shape as the width-cap harness: four `assign_async` calls in ONE
    /// assistant message against a ceiling of 2, with members parked on a gate
    /// so no slot can free mid-iteration and make the refusal timing-dependent.
    /// The addition here is that it records the leader's TOOL TABLE, which is
    /// the other half of the invariant under test.
    /// </summary>
    private sealed class AdviceScriptClient(int requested, string memberName, SemaphoreSlim gate) : IChatClient
    {
        public readonly ConcurrentQueue<string> Accepted = new();
        public readonly ConcurrentQueue<string> Refused = new();
        public readonly HashSet<string> LeaderTools = new(StringComparer.Ordinal);

        private int _leaderCalls;
        private readonly object _lock = new();

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var list = messages.ToList();

            if (list.Any(m => (m.Text ?? "").Contains(MemberSystem)))
            {
                await gate.WaitAsync(ct);
                return Reply(new ChatMessage(ChatRole.Assistant,
                    "Reviewed, no edits. <self_assessment>done</self_assessment>"));
            }

            // The leader's own tool table, as handed to the model on this turn.
            // Recorded on EVERY leader turn, not just the first: the strip runs
            // where the tools are assembled, and reading only one turn could
            // miss a table that differs later.
            lock (_lock)
                foreach (var t in options?.Tools ?? [])
                    if (!string.IsNullOrEmpty(t.Name)) LeaderTools.Add(t.Name);

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
                return Reply(new ChatMessage(ChatRole.Assistant, calls));
            }

            foreach (var m in list)
                foreach (var c in m.Contents)
                    if (c is FunctionResultContent fr)
                    {
                        var text = fr.Result?.ToString() ?? "";
                        if (text.Contains("dispatch limit reached")) Refused.Enqueue(text);
                        else if (text.Contains("assigned to")) Accepted.Enqueue(text);
                    }

            return Reply(new ChatMessage(ChatRole.Assistant, "Understood. Stopping here."));
        }

        private static ChatResponse Reply(ChatMessage m) => new([new ChatMessage(m.Role, m.Contents.ToList())]);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Sandbox that does nothing and shells out to nothing.</summary>
    private sealed class AdviceInertSandbox(string cwd) : ISandbox
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
        public ISandbox WithCwd(string newCwd) => new AdviceInertSandbox(newCwd);
        public ISandbox WithDispatchWorktree(string newCwd, string root) => new AdviceInertSandbox(newCwd);
    }
}
