using System.Diagnostics;
using Vett.Agent;

namespace Vett.Tests;

/// <summary>
/// Coverage for LEADER FAN-OUT — one leader dispatching MANY members at once.
///
/// WHY THIS EXISTS. A sweep of 61 team-bench runs across 8 suites (2026-08-26,
/// ds-team-flash) was measured for actual fan-out:
///
///     40 runs dispatched ZERO members (the leader did the work itself)
///     21 runs dispatched exactly ONE (always "implementer-1")
///     max fan-out observed across the entire corpus: 1
///
/// So the multi-member path was shipping completely unexercised. That matters
/// because AgentLoop.cs:1021-1031 runs multiple tool calls in ONE leader turn
/// through `Task.WhenAll` with NO semaphore and NO cap — N dispatches means N
/// genuinely concurrent `git worktree add` calls against the SAME parent repo.
/// Git does not serialize that for you, and DispatchWorktreeManager.cs:368
/// already names "index.lock held by a concurrent process" as a live failure
/// path. Nothing in that class takes a lock, mutex, or semaphore.
///
/// The blast radius if it breaks is not a clean error. Per
/// DispatchWorktreeCollisionTests, a CreateAsync throw makes Coordinator latch
/// worktrees OFF for the whole session, after which dispatches run with no
/// isolation, their diffs are never captured, and the run surfaces only as a
/// timeout — indistinguishable from a model or cap result.
///
/// These drive the real DispatchWorktreeManager against a throwaway git repo,
/// so they need `git` on PATH (same as the manager itself).
/// </summary>
public class DispatchFanOutConcurrencyTests : IDisposable
{
    private readonly string _repo;
    private readonly string _panelId;
    private readonly DispatchWorktreeManager _mgr;
    private readonly List<DispatchWorktree> _created = new();

    public DispatchFanOutConcurrencyTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vett-fan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);
        _panelId = "testfan-" + Guid.NewGuid().ToString("N")[..8];

        Git(_repo, "init");
        Git(_repo, "config", "user.email", "test@vett.local");
        Git(_repo, "config", "user.name", "vett-test");
        Git(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");

        _mgr = new DispatchWorktreeManager(_repo, _panelId);
    }

    public void Dispose()
    {
        foreach (var wt in _created)
        {
            try { _mgr.DiscardAsync(wt).GetAwaiter().GetResult(); } catch { }
        }
        try { Directory.Delete(Path.Combine(DispatchWorktreeManager.DispatchesRoot, _panelId), true); } catch { }
        try { Directory.Delete(_repo, true); } catch { }
    }

    /// <summary>
    /// The shape AgentLoop actually produces: every dispatch starts at the
    /// same moment via Task.WhenAll, not staggered. A loop of sequential
    /// awaits would pass while the real concurrent path still failed, so the
    /// tasks are all created before any is awaited.
    ///
    /// ⭐ WHY THIS DOESN'T JUST `await Task.WhenAll` (2026-08-26). It used to.
    /// This test went red exactly once — full-suite run 2 of 2026-08-26 — and
    /// the run produced NO usable evidence: WhenAll rethrows only the FIRST
    /// faulted task, so nine of the ten outcomes were discarded before anyone
    /// could look at them, and there was nothing to say whether one dispatch
    /// failed or all ten did. It has not reproduced since (8 full-suite runs,
    /// plus 24 parallel-process runs of this class = 72 green executions of
    /// these three tests), so the ONE observation that exists is the only one
    /// there is, and it was thrown away.
    ///
    /// A flake you cannot diagnose is worse than a flake you can: the standing
    /// temptation is to "fix" it by guessing — here, by serializing
    /// CreateAsync behind a per-repo lock — and a guess that serializes ten
    /// full monorepo checkouts costs ~830 s of a dispatch budget where the
    /// concurrent path costs a fraction of that. Fixing an unproven cause at
    /// that price is a worse bug than the flake.
    ///
    /// So this collects EVERY task's outcome and reports all of them together,
    /// with git's own view of the repo alongside. The next occurrence names
    /// the failure instead of hiding it.
    /// </summary>
    private async Task<DispatchWorktree[]> FanOutAsync(int n)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(1, n).Select(async i =>
        {
            // Every task parks on the same gate, so releasing it puts them
            // into `git worktree add` together rather than in arrival order.
            await gate.Task;
            try { return (Index: i, Wt: await _mgr.CreateAsync($"implementer-{i}"), Error: (Exception?)null); }
            catch (Exception ex) { return (Index: i, Wt: (DispatchWorktree?)null, Error: (Exception?)ex); }
        }).ToArray();

        gate.SetResult();
        // No exception escapes the selector above, so WhenAll cannot discard
        // an outcome — every one of the n results is present either way.
        var outcomes = await Task.WhenAll(tasks);

        var failed = outcomes.Where(o => o.Error is not null).ToList();
        if (failed.Count > 0)
            Assert.Fail(Diagnose(n, outcomes, failed));

        return outcomes.Select(o => o.Wt!).ToArray();
    }

    /// <summary>
    /// Everything a reader needs to classify the next occurrence without
    /// having to reproduce it: how many of the n dispatches failed, each
    /// failure's exception TYPE and message (the git stderr rides inside
    /// <see cref="WorktreeNotSupportedException"/>'s message), and git's own
    /// account of the repo — because the manager's return value is not
    /// evidence about git's metadata.
    /// </summary>
    private string Diagnose(
        int n,
        (int Index, DispatchWorktree? Wt, Exception? Error)[] outcomes,
        List<(int Index, DispatchWorktree? Wt, Exception? Error)> failed)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{failed.Count} of {n} concurrent dispatches failed to get a worktree.");
        sb.AppendLine();
        foreach (var (index, _, error) in failed)
        {
            sb.AppendLine($"  implementer-{index}: {error!.GetType().Name}");
            foreach (var line in (error.Message ?? "").Split('\n'))
                sb.AppendLine($"      {line.TrimEnd()}");
        }

        var ok = outcomes.Where(o => o.Error is null).Select(o => o.Index).OrderBy(i => i);
        sb.AppendLine();
        sb.AppendLine($"  succeeded: [{string.Join(", ", ok)}]");

        // git's view, not the manager's. Wrapped because a repo wedged badly
        // enough to fail the adds may also fail these — and a diagnostic that
        // throws while diagnosing replaces the real message with its own.
        try { sb.AppendLine().AppendLine("git worktree list:").AppendLine(Git(_repo, "worktree", "list")); }
        catch (Exception ex) { sb.AppendLine($"(git worktree list itself failed: {ex.Message})"); }
        try
        {
            var panel = Path.Combine(DispatchWorktreeManager.DispatchesRoot, _panelId);
            sb.AppendLine($"contents of {panel}:");
            if (Directory.Exists(panel))
                foreach (var d in Directory.GetDirectories(panel))
                    sb.AppendLine($"  {Path.GetFileName(d)}  (seed.txt: {File.Exists(Path.Combine(d, "seed.txt"))})");
            else
                sb.AppendLine("  <the panel directory does not exist>");
        }
        catch (Exception ex) { sb.AppendLine($"(listing the panel dir failed: {ex.Message})"); }

        return sb.ToString();
    }

    private void Record(IEnumerable<DispatchWorktree> wts)
    {
        lock (_created) _created.AddRange(wts);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public async Task ConcurrentFanOut_AllDispatchesGetTheirOwnIsolatedWorktree(int n)
    {
        var wts = await FanOutAsync(n);
        Record(wts);

        // 1. Every dispatch got a worktree. A partial result here is the
        //    exact fault that latches isolation off for the whole session.
        Assert.Equal(n, wts.Length);

        // 2. Paths are distinct. Two members sharing a checkout would let
        //    one member's edits land in another's captured diff.
        var paths = wts.Select(w => w.Path).ToList();
        Assert.Equal(n, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // 3. Each one is a REAL checkout, not an empty directory that git
        //    registered and then failed to populate under contention.
        foreach (var wt in wts)
        {
            Assert.True(Directory.Exists(wt.Path), $"missing worktree dir: {wt.Path}");
            Assert.True(File.Exists(Path.Combine(wt.Path, "seed.txt")),
                $"worktree has no checkout: {wt.Path}");
        }

        // 4. git itself agrees they are all registered — the manager's own
        //    return value is not evidence that git's metadata is consistent.
        var listed = Git(_repo, "worktree", "list");
        foreach (var wt in wts)
        {
            var leaf = Path.GetFileName(wt.Path.TrimEnd(Path.DirectorySeparatorChar));
            Assert.Contains(leaf, listed, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Fan out, then have every member write and have each diff captured.
    /// Creation contends on .git/index.lock; capture contends again, and a
    /// diff that comes back empty or carrying ANOTHER member's file is the
    /// silent-corruption case — the leader would accept work nobody did.
    /// </summary>
    [Fact]
    public async Task ConcurrentFanOut_EachMemberDiffContainsOnlyItsOwnWork()
    {
        const int n = 10;
        var wts = await FanOutAsync(n);
        Record(wts);
        Assert.Equal(n, wts.Length);

        for (var i = 0; i < n; i++)
        {
            var cwd = DispatchWorktreeManager.MemberCwd(wts[i].Path, "");
            File.WriteAllText(Path.Combine(cwd, $"member-{i + 1}.txt"), $"work by member {i + 1}\n");
        }

        var captures = await Task.WhenAll(wts.Select(w => _mgr.CaptureAsync(w)));

        for (var i = 0; i < n; i++)
        {
            var diff = captures[i].Diff ?? "";
            Assert.Contains($"member-{i + 1}.txt", diff);

            // No other member's file may appear in this diff.
            for (var j = 0; j < n; j++)
            {
                if (j == i) continue;
                Assert.DoesNotContain($"member-{j + 1}.txt", diff);
            }
        }
    }

    /// <summary>
    /// THE DIAGNOSTIC'S OWN FAILURE TEST.
    ///
    /// The reporting above only earns its keep on the day this class goes red,
    /// and a message that has never been rendered is a message nobody has read.
    /// Worse, <see cref="Diagnose"/> shells out to git and walks the filesystem
    /// while explaining a failure — if either throws, the real error is
    /// replaced by the diagnostic's own, which is precisely the outcome it
    /// exists to prevent.
    ///
    /// So drive it for real: a manager whose parent is NOT a git repo makes
    /// every CreateAsync throw, and the assertion message must name the
    /// count, the exception type, and the empty success list.
    /// </summary>
    [Fact]
    public async Task WhenDispatchesFail_TheFailureMessageNamesEveryOne()
    {
        var notARepo = Path.Combine(Path.GetTempPath(), "vett-fan-norepo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(notARepo);
        try
        {
            var mgr = new DispatchWorktreeManager(notARepo, "testfan-norepo-" + Guid.NewGuid().ToString("N")[..8]);
            var gate = new TaskCompletionSource();
            var tasks = Enumerable.Range(1, 3).Select(async i =>
            {
                await gate.Task;
                try { return (Index: i, Wt: await mgr.CreateAsync($"implementer-{i}"), Error: (Exception?)null); }
                catch (Exception ex) { return (Index: i, Wt: (DispatchWorktree?)null, Error: (Exception?)ex); }
            }).ToArray();
            gate.SetResult();
            var outcomes = await Task.WhenAll(tasks);

            var failed = outcomes.Where(o => o.Error is not null).ToList();
            Assert.Equal(3, failed.Count);   // the premise: this really did fail

            var msg = Diagnose(3, outcomes, failed);

            Assert.Contains("3 of 3 concurrent dispatches failed", msg);
            Assert.Contains(nameof(WorktreeNotSupportedException), msg);
            foreach (var i in new[] { 1, 2, 3 })
                Assert.Contains($"implementer-{i}:", msg);
            Assert.Contains("succeeded: []", msg);
            // The git stderr / cause must survive into the message — a report
            // that says "3 failed" without saying WHY is the state this
            // replaced.
            Assert.Contains("not a git repository", msg);
        }
        finally
        {
            try { Directory.Delete(notARepo, true); } catch { }
        }
    }

    // ---- the retry's classifier, tested TWO-SIDED --------------------------

    /// <summary>
    /// THE OBSERVED RACE MUST CLASSIFY AS TRANSIENT.
    ///
    /// This is the assertion that actually protects the fix. The retry added
    /// 2026-08-26 absorbs anything NOT on the permanent list, so the only way
    /// to silently disable it is for someone to widen that list until it
    /// swallows the race — and "failed to read" or "no error" both look like
    /// plausible things to add.
    ///
    /// The string is the REAL one, copied verbatim from the full-suite run
    /// that produced it, not a paraphrase:
    ///
    ///   fatal: failed to read .git/worktrees/implementer-4/commondir: No error
    ///
    /// If this goes red, dispatches stop retrying and fan-out silently loses
    /// isolation again — which surfaces as a timeout, not as an error.
    /// </summary>
    [Theory]
    [InlineData("fatal: failed to read .git/worktrees/implementer-4/commondir: no error")]
    [InlineData("fatal: '.git/index.lock' already exists")]
    [InlineData("fatal: could not create leading directories")]
    [InlineData("fatal: unable to write new index file")]
    [InlineData("")]
    public void Transient_git_failures_are_retried_not_reported(string gitOutput)
    {
        Assert.False(DispatchWorktreeManager.IsPermanentAddFailure(gitOutput),
            $"'{gitOutput}' was classified PERMANENT, so the dispatch will not be retried. " +
            "Only causes that cannot change for the life of the session belong on that list.");
    }

    /// <summary>
    /// The other side. Without this, "classify everything as transient" would
    /// pass the test above — and a repo with no commits would then burn the
    /// full attempt budget on every single dispatch before reporting the
    /// actionable `git commit --allow-empty` message.
    /// </summary>
    [Theory]
    [InlineData("fatal: invalid reference: head")]
    [InlineData("fatal: not a valid object name: 'head'")]
    [InlineData("fatal: no such ref: head")]
    public void Permanent_git_failures_are_reported_not_retried(string gitOutput)
    {
        Assert.True(DispatchWorktreeManager.IsPermanentAddFailure(gitOutput),
            $"'{gitOutput}' cannot succeed on a retry and must be reported immediately.");
    }

    /// <summary>
    /// A retry budget of 1 is "no retry at all" — the exact pre-fix behaviour
    /// this file exists to prevent regressing to. Pinning the floor rather
    /// than the value leaves the number tunable without making the test a
    /// change-detector.
    /// </summary>
    [Fact]
    public void The_add_retry_budget_is_actually_greater_than_one()
        => Assert.True(DispatchWorktreeManager.WorktreeAddAttempts > 1,
            "WorktreeAddAttempts <= 1 disables the concurrent-fan-out retry entirely.");

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
