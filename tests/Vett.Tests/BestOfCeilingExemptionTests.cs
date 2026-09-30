using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Sandbox;

namespace Vett.Tests;

/// <summary>
/// ⭐ BEST-OF IS EXEMPT FROM THE DISPATCH CEILING — proven AT RUNTIME.
///
/// ⛔ WHY THIS FILE EXISTS. When the width cap shipped (2026-08-27), best-of's
/// exemption was asserted in exactly one place: a `validate` WARNING telling the
/// author the two numbers disagree. That is a check on the CONFIG, not on the
/// behaviour, and the two can part company silently.
///
/// The failure it guards against is nastier than an ordinary cap bug. If some
/// later change routed best-of's candidates through `TryCreate`, a profile
/// asking for best-of-5 under a ceiling of 2 would quietly run **best-of-2** —
/// and keep calling it best-of-5 in every report, every log line and every
/// bench result. The judge would pick from a smaller field than the experiment
/// claims. Nothing would error, and the number that changed is one nobody reads
/// twice.
///
/// So this drives the real <see cref="TeamCoordinator"/> and counts how many
/// candidates are ACTUALLY in flight at once.
///
/// ⛔ BEST-OF REQUIRES DISPATCH WORKTREES — discovered while writing this file.
/// `assign_best_of` is registered INSIDE the `dispatchManager` branch
/// (`Coordinator.cs`, "BEST-OF-N: needs worktrees"), so with
/// `dispatch_worktree: false` the tool does not exist at all. The first draft of
/// these tests ran with worktrees off, the leader called a tool that was never
/// registered, and all three failed with **zero** candidates. Hence the real git
/// repo below: without it this file would be testing nothing.
///
/// ⚠ WHAT THIS DELIBERATELY DOES NOT ASSERT. The candidates make no edits, so
/// there are no diffs and best-of reports "NO candidate produced a diff". That
/// is expected and irrelevant here: the N candidates have already run in
/// parallel by then, which is the property under test. Judging and applying a
/// winner are covered elsewhere.
/// </summary>
public class BestOfCeilingExemptionTests : IDisposable
{
    private readonly string _repo;
    private readonly string _panelId;

    public BestOfCeilingExemptionTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vett-bestof-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);
        _panelId = "testbestof-" + Guid.NewGuid().ToString("N")[..8];

        Git(_repo, "init");
        Git(_repo, "config", "user.email", "test@vett.local");
        Git(_repo, "config", "user.name", "vett-test");
        Git(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");
    }

    public void Dispose()
    {
        try { Git(_repo, "worktree", "prune"); } catch { }
        try { Directory.Delete(Path.Combine(DispatchWorktreeManager.DispatchesRoot, _panelId), true); } catch { }
        try { Directory.Delete(_repo, true); } catch { }
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout;
    }

    private const string CandidateSystem = "CANDIDATE-SYSTEM-BESTOF";
    private const string MemberName = "implementer";
    private const string JudgeName = "reviewer";

    /// <summary>
    /// ⭐ THE HEADLINE. Ceiling of 2, best-of-5 — and all FIVE candidates must
    /// run concurrently. A cap-respecting best-of would peak at 2.
    /// </summary>
    [Fact]
    public async Task Best_of_runs_ALL_N_candidates_even_when_N_exceeds_the_ceiling()
    {
        var outcome = await RunAsync(ceiling: 2, n: 5);

        // The load-bearing assertion: five at once, not two.
        Assert.Equal(5, outcome.PeakConcurrentCandidates);

        // POSITIVE CONJUNCT: a run where the leader never called the tool would
        // otherwise satisfy "peak was not clamped to 2" vacuously.
        Assert.Equal(5, outcome.TotalCandidateCalls);

        // And the candidates were never refused — the ceiling did not reach them.
        Assert.DoesNotContain("dispatch limit reached", outcome.ToolResult);
    }

    /// <summary>
    /// The same profile without a ceiling must behave identically. If these two
    /// differ, the ceiling is reaching best-of by some path after all.
    /// </summary>
    [Fact]
    public async Task An_UNCAPPED_profile_runs_the_same_five_candidates()
    {
        var outcome = await RunAsync(ceiling: 0, n: 5);

        Assert.Equal(5, outcome.PeakConcurrentCandidates);
        Assert.Equal(5, outcome.TotalCandidateCalls);
    }

    /// <summary>
    /// ⭐ THE SPECIFICITY GUARD. The test above would also pass if the harness
    /// simply ignored `n` and always ran five. Asking for THREE under the same
    /// ceiling must produce three — so the count tracks the author's `n`, not a
    /// constant this test happens to expect.
    /// </summary>
    [Fact]
    public async Task The_candidate_count_tracks_the_authors_n_not_a_constant()
    {
        var outcome = await RunAsync(ceiling: 2, n: 3);

        Assert.Equal(3, outcome.PeakConcurrentCandidates);
        Assert.Equal(3, outcome.TotalCandidateCalls);
    }

    private sealed record BestOfOutcome(
        int PeakConcurrentCandidates, int TotalCandidateCalls, int TotalModelCalls, string ToolResult);

    private async Task<BestOfOutcome> RunAsync(int ceiling, int n)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // ⚠ THE GATE IS THE WHOLE MEASUREMENT. Candidates park until all `n`
        // have arrived; only then are they released. If fewer than `n` ever
        // arrive the gate never opens and the run hits its timeout — which is a
        // RED, not a silent pass. A test that let candidates return immediately
        // could see a peak of 1 on a slow machine and prove nothing.
        using var allArrived = new SemaphoreSlim(0);
        var client = new BestOfScriptClient(n, MemberName, JudgeName, allArrived);

        var profile = new Profile
        {
            Llm = new LlmConfig(),
            Team = new TeamConfig
            {
                Leader = new MemberConfig { Name = "leader", SystemPrompt = "LEADER-SYSTEM", MaxIterations = 6 },
                Members =
                {
                    new MemberConfig { Name = MemberName, SystemPrompt = CandidateSystem, MaxIterations = 4, Tools = [] },
                    new MemberConfig { Name = JudgeName, SystemPrompt = "JUDGE-SYSTEM-BESTOF", MaxIterations = 4, Tools = [] },
                },
                // ⛔ MUST be true — `assign_best_of` is only registered inside the
                // dispatchManager branch. With this false the tool does not exist
                // and every assertion below reads 0.
                DispatchWorktree = true,
                // 0 disables the startup auto-prune, which would otherwise walk
                // the SHARED ~/.vett/dispatches tree and reap another run's
                // retained worktrees.
                DispatchMaxAgeDays = 0,
                AutoInjectAsyncResults = false,
                MaxConcurrentDispatches = ceiling,
                BestOf = new BestOfConfig
                {
                    N = n,
                    Member = MemberName,
                    Judge = JudgeName,
                    // `auto: false` — the leader calls `assign_best_of` itself
                    // here, so the measurement is of best-of's own fan-out and
                    // not of a transparent reroute of `assign_task`.
                    Auto = false,
                },
            },
        };

        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync("go");
        chan.Writer.Complete();

        try
        {
            await TeamCoordinator.RunInteractiveAsync(
                profile, client, "test-model", new InertBestOfSandbox(_repo), _panelId,
                chan.Reader,
                onAssistantText: null, onWaitingForInput: null, onEvent: null,
                seedHistory: null, turnInterrupt: null, compactRequest: null,
                cwd: _repo, ct: cts.Token);
        }
        finally
        {
            // A failed assert must never strand a parked candidate.
            allArrived.Release(n + 8);
        }

        return new BestOfOutcome(
            client.PeakConcurrent, client.TotalCandidateCalls, client.TotalModelCalls, client.LastToolResult);
    }

    /// <summary>
    /// Leader issues ONE `assign_best_of` call. Candidates park until all `n`
    /// have arrived, so the peak concurrency reading cannot be an artifact of
    /// scheduling luck.
    /// </summary>
    private sealed class BestOfScriptClient(
        int n, string memberName, string judgeName, SemaphoreSlim allArrived) : IChatClient
    {
        public int PeakConcurrent;
        public int TotalCandidateCalls;
        public int TotalModelCalls;
        public string LastToolResult = "";

        private int _leaderCalls;
        private int _live;
        private int _arrived;
        private readonly object _lock = new();

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var list = messages.ToList();

            // A CANDIDATE.
            if (list.Any(m => (m.Text ?? "").Contains(CandidateSystem)))
            {
                lock (_lock) { TotalModelCalls++; }

                // ⛔ A MODEL CALL IS NOT A CANDIDATE. The first version of this
                // client counted every call and read 15 for n=5 — each candidate
                // agent turns the loop THREE times. Counting calls would have
                // made `n` look like 3n and, worse, would have kept reading
                // "more than the ceiling" no matter what best-of did.
                //
                // A candidate's FIRST call is the one with no assistant message
                // in its history yet. That is the arrival, and it is what the
                // gate and the peak are measured on.
                if (list.Any(m => m.Role == ChatRole.Assistant))
                    return Reply(new ChatMessage(ChatRole.Assistant,
                        "Already done. <self_assessment>done</self_assessment>"));

                var now = Interlocked.Increment(ref _live);
                lock (_lock)
                {
                    if (now > PeakConcurrent) PeakConcurrent = now;
                    TotalCandidateCalls++;
                }

                // Once the n-th candidate arrives, release everyone at once.
                if (Interlocked.Increment(ref _arrived) >= n) allArrived.Release(n + 8);

                try { await allArrived.WaitAsync(ct); }
                finally { Interlocked.Decrement(ref _live); }

                return Reply(new ChatMessage(ChatRole.Assistant,
                    "Made the change. <self_assessment>done</self_assessment>"));
            }

            // THE JUDGE — reached only if candidates produced diffs, which they
            // do not here (they edit nothing). Handled so a future change that
            // does produce diffs fails loudly rather than hanging.
            if (list.Any(m => (m.Text ?? "").Contains("JUDGE-SYSTEM-BESTOF")))
                return Reply(new ChatMessage(ChatRole.Assistant, "NONE"));

            int k;
            lock (_lock) { k = ++_leaderCalls; }

            if (k == 1)
                return Reply(new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("b1", "assign_best_of", new Dictionary<string, object?>
                    {
                        ["member"] = memberName,
                        ["task"] = "make the change",
                        ["n"] = n,
                        ["judge"] = judgeName,
                    }),
                ]));

            foreach (var m in list)
                foreach (var c in m.Contents)
                    if (c is FunctionResultContent fr)
                        LastToolResult = fr.Result?.ToString() ?? "";

            return Reply(new ChatMessage(ChatRole.Assistant, "Done."));
        }

        private static ChatResponse Reply(ChatMessage m) => new([new ChatMessage(m.Role, m.Contents.ToList())]);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class InertBestOfSandbox(string cwd) : ISandbox
    {
        public string Cwd => cwd;
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("", 0, cwd, false));
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default) => Task.FromResult("");
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string nw, CancellationToken ct = default) => Task.FromResult(("", (string?)null));
        public Task<string> FileInsertAsync(string s, string p, int l, string nw, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("");
        public Task SessionCreateAsync(string nm, string c, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string nm, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string newCwd) => new InertBestOfSandbox(newCwd);
        public ISandbox WithDispatchWorktree(string newCwd, string root) => new InertBestOfSandbox(newCwd);
    }
}
