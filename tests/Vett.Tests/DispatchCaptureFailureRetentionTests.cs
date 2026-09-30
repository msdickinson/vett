using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Sandbox;

namespace Vett.Tests;

/// <summary>
/// COULD-NOT-MEASURE IS NOT MEASURED-ZERO — the destructive half.
///
/// DispatchWorktreeManager.CaptureAsync was fixed (2026-08-24) to THROW when a
/// git call exits non-zero, instead of letting empty stdout collapse into
/// FilesChanged=0 / HasChanges=false. Its own comment names the site that was
/// left unfixed:
///
///     "Coordinator.cs:623-628 then acts on that destructively ('No changes —
///      clean up the worktree right away'), so the ONE case where the worktree
///      is the last surviving copy of the member's work is the case that
///      deletes it."
///
/// Coordinator caught the exception, emitted `dispatch_capture_failed`, and
/// synthesised `new DispatchCapture(false, null, "(capture failed: ...)", 0)`.
/// HasChanges=false then fell straight into the else-branch and called
/// DiscardAsync — destroying the evidence precisely when it could not be read.
/// The synthetic capture's own comment claimed the opposite ("so the leader
/// sees that the worktree path exists for manual cleanup"); the next statement
/// deleted the path.
///
/// These drive the REAL TeamCoordinator against a throwaway git repo and a
/// scripted (offline) chat client, so they exercise the actual production
/// wiring rather than a re-implementation of the decision. They need `git` on
/// PATH, same as DispatchWorktreeCollisionTests.
///
/// Capture failure is induced honestly: the worktree's `.git` gitfile is
/// rewritten to point at a directory that does not exist, which is what a
/// yanked/pruned object store looks like to `git add -A`. The tests assert the
/// premise (a `dispatch_capture_failed` event really fired) before asserting
/// the consequence, so a run where capture quietly SUCCEEDED cannot pass as
/// proof.
/// </summary>
public class DispatchCaptureFailureRetentionTests : IDisposable
{
    private const string LeaderGo = "LEADER-GO-PLEASE-DISPATCH";
    private const string MemberSystem = "MEMBER-SYSTEM-PROMPT-MARKER";
    private const string MemberTask = "MEMBER-WORK-MARKER";
    private const string MemberName = "implementer";

    private readonly string _repo;
    private readonly string _panelId;

    public DispatchCaptureFailureRetentionTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vett-cfr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);
        _panelId = "cfr-" + Guid.NewGuid().ToString("N")[..8];

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
        // Clean up whatever the run left behind — including, in the fixed
        // arm, the deliberately RETAINED worktree. Never let a test leak
        // directories into the shared ~/.vett/dispatches tree.
        try { Git(_repo, "worktree", "prune"); } catch { }
        try { Directory.Delete(Path.Combine(DispatchWorktreeManager.DispatchesRoot, _panelId), true); } catch { }
        try { Directory.Delete(_repo, true); } catch { }
    }

    // ---- the two arms -----------------------------------------------------

    /// <summary>
    /// THE DEFECT. Capture could not be performed; the worktree is the only
    /// surviving copy of what the member did. It must still be on disk.
    /// </summary>
    [Fact]
    public async Task CaptureFailure_RetainsTheWorktreeAsEvidence()
    {
        var run = await RunTeamAsync(sabotageCapture: true);

        // PREMISE: we really did induce a measurement failure. Without this
        // the test could "pass" on a run where capture worked fine.
        AssertCaptureFailed(run);

        Assert.False(string.IsNullOrEmpty(run.WorktreePath));
        Assert.True(Directory.Exists(run.WorktreePath),
            $"capture FAILED, so the worktree is the only evidence of the dispatch — it must not be discarded. Missing: {run.WorktreePath}");
    }

    /// <summary>
    /// The retention has to be READABLE, not just true. A later reader must be
    /// able to tell "retained because capture failed" from "retained because
    /// the retention policy keeps everything" without inferring it from a
    /// diff-stat string.
    /// </summary>
    [Fact]
    public async Task CaptureFailure_IsLabelledOnTheEventStream_WithItsReason()
    {
        var run = await RunTeamAsync(sabotageCapture: true);

        AssertCaptureFailed(run);

        var retained = Assert.Single(run.Events, e => e.Type == "dispatch_worktree_retained");
        Assert.Equal("capture_failed", retained.Data["reason"]);
        Assert.Equal(run.WorktreePath, retained.Data["worktree_path"] as string);

        var end = Assert.Single(run.Events, e => e.Type == "dispatch_end" && e.Data.ContainsKey("capture_ran"));
        Assert.Equal(true, end.Data["capture_failed"]);
        Assert.Equal(true, end.Data["worktree_retained"]);
        Assert.Equal("capture_failed", end.Data["worktree_retained_reason"]);
        // files_changed stays 0 from the synthetic capture, which is exactly
        // why the boolean above has to exist: 0 alone cannot carry the
        // difference between "measured zero" and "could not measure".
        Assert.Equal(0, end.Data["files_changed"]);
    }

    /// <summary>
    /// The leader is the other consumer that was being lied to. On capture
    /// failure it used to be told "DISPATCH COMPLETED — NO CHANGES ... Just
    /// relay the member's result to the user and move on."
    /// </summary>
    [Fact]
    public async Task CaptureFailure_TellsTheLeaderMeasurementFailed_NotNoChanges()
    {
        var run = await RunTeamAsync(sabotageCapture: true);

        AssertCaptureFailed(run);

        var assign = Assert.Single(run.ToolResults);
        Assert.Contains("CAPTURE FAILED", assign);
        Assert.DoesNotContain("NO CHANGES", assign);
        Assert.Contains(run.WorktreePath!, assign);
    }

    // ---- negative controls: these must stay GREEN in BOTH arms ------------

    /// <summary>
    /// OVER-CORRECTION GUARD. "Retain on capture failure" must not become
    /// "retain everything": a member that genuinely changed nothing, under the
    /// default retention policy, still gets its worktree cleaned up. On the
    /// DickinsonBros monorepo a retained full worktree is ~15 GB, so blanket
    /// retention is not a harmless direction to be wrong in.
    ///
    /// This test CANNOT detect the defect — that is the point. It is here to
    /// fail if the fix over-shoots.
    /// </summary>
    [Fact]
    public async Task GenuineNoChanges_StillDiscardsTheWorktree()
    {
        var run = await RunTeamAsync(sabotageCapture: false);

        // PREMISE: capture actually ran and measured a real zero.
        Assert.DoesNotContain(run.Events, e => e.Type == "dispatch_capture_failed");

        Assert.False(string.IsNullOrEmpty(run.WorktreePath));
        Assert.False(Directory.Exists(run.WorktreePath),
            $"nothing changed and capture succeeded — the worktree carries no evidence and must be reclaimed: {run.WorktreePath}");
    }

    /// <summary>
    /// The other half of the same guard, on the reporting surface: a clean
    /// no-op must not be dressed up as a retained failure, or the retention
    /// signal becomes noise and stops meaning anything.
    /// </summary>
    [Fact]
    public async Task GenuineNoChanges_EmitsNoFailureOrRetentionEvent()
    {
        var run = await RunTeamAsync(sabotageCapture: false);

        Assert.DoesNotContain(run.Events, e => e.Type == "dispatch_capture_failed");
        Assert.DoesNotContain(run.Events, e => e.Type == "dispatch_worktree_retained");

        var assign = Assert.Single(run.ToolResults);
        Assert.DoesNotContain("CAPTURE FAILED", assign);
        Assert.Contains("NO CHANGES", assign);
    }

    // ---- harness ----------------------------------------------------------

    private sealed record RunOutcome(
        List<Event> Events, string? WorktreePath, List<string> ToolResults)
    {
        /// <summary>Diagnostics for premise failures. A premise assert that
        /// fires without saying WHY leaves you unable to tell "the treatment
        /// did not apply" from "the treatment applied and the fix worked".</summary>
        public string Dump()
        {
            var types = string.Join(", ", Events.Select(e => e.Type));
            var fb = Events.Where(e => e.Type is "dispatch_worktree_fallback" or "dispatch_capture_failed")
                           .Select(e => e.Type + "=" + string.Join("|", e.Data.Select(kv => kv.Key + ":" + kv.Value)));
            return $"\nworktree_path={WorktreePath ?? "(null)"}" +
                   $"\nexists={(WorktreePath is not null && Directory.Exists(WorktreePath))}" +
                   $"\nevents=[{types}]" +
                   $"\ndetail=[{string.Join(" ;; ", fb)}]" +
                   $"\ntool_results=[{string.Join(" ;; ", ToolResults)}]";
        }
    }

    /// <summary>Premise guard: assert the TREATMENT actually applied before
    /// asserting its consequence. Without this a run where capture quietly
    /// SUCCEEDED would score as evidence about the retention branch.</summary>
    private static void AssertCaptureFailed(RunOutcome run) =>
        Assert.True(run.Events.Any(e => e.Type == "dispatch_capture_failed"),
            "PREMISE NOT MET: no dispatch_capture_failed event — the sabotage did not induce a capture failure, so this run says nothing about the retention branch." + run.Dump());

    private async Task<RunOutcome> RunTeamAsync(bool sabotageCapture)
    {
        var events = new ConcurrentQueue<Event>();
        string? worktreePath = null;

        void OnEvent(Event e)
        {
            events.Enqueue(e);
            if (e.Type != "dispatch_start") return;
            worktreePath = e.Data.TryGetValue("worktree_path", out var p) ? p as string : null;
            if (!sabotageCapture || worktreePath is null) return;

            // Break the worktree's link to its object store. `git worktree add`
            // writes `.git` as a FILE containing `gitdir: <repo>/.git/worktrees/<n>`;
            // repointing it at a path that does not exist makes every git call
            // inside the worktree exit non-zero — the same shape as a pruned
            // store or a yanked repo. Done here (dispatch_start fires before
            // the member runs) so the failure lands on the real capture step.
            var gitFile = Path.Combine(worktreePath, ".git");
            // git marks the worktree gitfile HIDDEN on Windows, and
            // File.WriteAllText onto a hidden file throws UnauthorizedAccess.
            // Clear attributes first — a sabotage that throws inside this
            // synchronous event callback would abort the dispatch before the
            // capture step and silently measure nothing.
            File.SetAttributes(gitFile, FileAttributes.Normal);
            File.WriteAllText(gitFile,
                "gitdir: " + Path.Combine(Path.GetTempPath(), "vett-cfr-no-such-gitdir-" + Guid.NewGuid().ToString("N")) + "\n");
        }

        var profile = new Profile
        {
            Llm = new LlmConfig(),
            Team = new TeamConfig
            {
                // ⭐ ADDED 2026-08-27, when an absent width ceiling became an
                // error rather than a silent "unlimited". EXPLICIT 0 = unlimited,
                // preserving this fixture's pre-requirement behaviour. These
                // tests measure worktree RETENTION after a capture failure; a
                // refused dispatch never creates a worktree, so a ceiling could
                // make the retention assertions pass without a worktree ever
                // existing.
                MaxConcurrentDispatches = 0,
                Leader = new MemberConfig { Name = "leader", SystemPrompt = "LEADER-SYSTEM", MaxIterations = 6 },
                Members =
                {
                    new MemberConfig { Name = MemberName, SystemPrompt = MemberSystem, MaxIterations = 6 },
                },
                DispatchWorktree = true,
                // 0 disables the startup auto-prune. It walks the SHARED
                // ~/.vett/dispatches tree and would reap another run's
                // retained worktrees; a unit test must never do that.
                DispatchMaxAgeDays = 0,
                DispatchRetention = "keep-on-failure",
                AutoInjectAsyncResults = false,
            },
        };

        var client = new TeamScriptClient();
        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync(LeaderGo);
        chan.Writer.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await TeamCoordinator.RunInteractiveAsync(
            profile, client, "test-model", new FakeSandbox(_repo), _panelId,
            chan.Reader,
            onAssistantText: null, onWaitingForInput: null, onEvent: OnEvent,
            seedHistory: null, turnInterrupt: null, compactRequest: null,
            cwd: _repo, ct: cts.Token);

        return new RunOutcome(events.ToList(), worktreePath, client.AssignTaskResults);
    }

    /// <summary>Drives leader and member off one client. They are told apart by
    /// the member's system-prompt marker, not by call order, so the interleaving
    /// cannot silently mis-script the run.</summary>
    private sealed class TeamScriptClient : IChatClient
    {
        private int _leaderCalls;
        private readonly object _lock = new();
        public readonly List<string> AssignTaskResults = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var list = messages.ToList();
            var isMember = list.Any(m => (m.Text ?? "").Contains(MemberSystem));

            if (isMember)
            {
                // Prose only: the member calls no tools and touches no files,
                // so a SUCCESSFUL capture legitimately measures zero.
                return Reply(new ChatMessage(ChatRole.Assistant,
                    "I reviewed the task and made no edits. <self_assessment>done</self_assessment>"));
            }

            // Harvest anything the leader was told about its dispatch.
            foreach (var m in list)
            {
                foreach (var c in m.Contents)
                {
                    if (c is FunctionResultContent fr && !string.IsNullOrEmpty(fr.Result?.ToString()))
                    {
                        lock (_lock)
                        {
                            var text = fr.Result!.ToString()!;
                            if (!AssignTaskResults.Contains(text)) AssignTaskResults.Add(text);
                        }
                    }
                }
            }

            int n;
            lock (_lock) { n = ++_leaderCalls; }
            if (n == 1)
            {
                return Reply(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("call-1", "assign_task", new Dictionary<string, object?>
                    {
                        ["member"] = MemberName,
                        ["task"] = MemberTask,
                    }),
                }));
            }
            return Reply(new ChatMessage(ChatRole.Assistant, "Dispatch handled. Stopping here."));
        }

        private static Task<ChatResponse> Reply(ChatMessage m) =>
            Task.FromResult(new ChatResponse([new ChatMessage(m.Role, m.Contents.ToList())]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Minimal sandbox. WithDispatchWorktree must return a sandbox
    /// rooted at the worktree (the coordinator relies on it not throwing);
    /// nothing in these tests actually shells out.</summary>
    private sealed class FakeSandbox(string cwd) : ISandbox
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
        public ISandbox WithCwd(string newCwd) => new FakeSandbox(newCwd);
        public ISandbox WithDispatchWorktree(string newCwd, string root) => new FakeSandbox(newCwd);
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout;
    }
}
