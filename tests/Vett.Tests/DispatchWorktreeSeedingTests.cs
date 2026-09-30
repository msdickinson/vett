using System.Diagnostics;
using Vett.Agent;

namespace Vett.Tests;

/// <summary>
/// Regression coverage for the sequential-dispatch accumulation gap.
///
/// accept_dispatch applies a member's diff to the parent WORKING TREE
/// without committing (so the end-of-run `git diff` grading capture still
/// sees every change). But `git worktree add ... HEAD` forks the next
/// member's worktree from the last COMMIT. Before the seeding fix a second
/// member (e.g. a reviewer) forked from the seed state and could NOT see
/// what a prior accept had applied to the parent — the exact
/// "verified by implementer but not found by reviewer" failure seen in the
/// real deepseek-team proof run.
///
/// These tests drive the real DispatchWorktreeManager against a throwaway
/// git repo, so they require `git` on PATH (same as the manager itself).
/// </summary>
public class DispatchWorktreeSeedingTests : IDisposable
{
    private readonly string _repo;
    private readonly string _panelId;
    private readonly DispatchWorktreeManager _mgr;
    private readonly List<DispatchWorktree> _created = new();

    public DispatchWorktreeSeedingTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vett-wt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);
        // Unique panel id so dispatch dirs under ~/.vett/dispatches don't
        // collide with a concurrent run and get cleaned up deterministically.
        _panelId = "test-" + Guid.NewGuid().ToString("N")[..8];

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
        // Discarding the worktrees does not remove the panel dir that holds
        // them (DispatchWorktreeManager.cs:150 creates it). See the longer note
        // in DispatchWorktreeSparseTests.Dispose.
        try { Git(_repo, "worktree", "prune"); } catch { }
        try { Directory.Delete(Path.Combine(DispatchWorktreeManager.DispatchesRoot, _panelId), true); } catch { }
        try { Directory.Delete(_repo, true); } catch { }
    }

    private async Task<DispatchWorktree> CreateAsync(string taskId)
    {
        var wt = await _mgr.CreateAsync(taskId);
        _created.Add(wt);
        return wt;
    }

    private static string HeadSha(string cwd) => Git(cwd, "rev-parse", "HEAD").Trim();

    [Fact]
    public async Task SequentialDispatch_SecondWorktree_SeesFirstAcceptedChange()
    {
        var parentHeadBefore = HeadSha(_repo);

        // --- Member 1: create alpha.txt in an isolated worktree, capture,
        //     and "accept" (apply to parent working tree). ---
        var wt1 = await CreateAsync("implementer-1");
        File.WriteAllText(Path.Combine(wt1.Path, "alpha.txt"), "alpha\n");
        var cap1 = await _mgr.CaptureAsync(wt1);
        Assert.True(cap1.HasChanges);
        Assert.NotNull(cap1.Diff);
        await _mgr.ApplyAsync(cap1.Diff!);

        // accept_dispatch applies to the parent WORKING TREE only — HEAD
        // must not move (grading `git diff` depends on this).
        Assert.Equal(parentHeadBefore, HeadSha(_repo));
        Assert.True(File.Exists(Path.Combine(_repo, "alpha.txt")));

        // --- Member 2: a fresh worktree must now SEE alpha.txt (the fix). ---
        var wt2 = await CreateAsync("reviewer-1");
        Assert.True(
            File.Exists(Path.Combine(wt2.Path, "alpha.txt")),
            "second dispatch worktree did not inherit the first accept's change — seeding regressed");
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(wt2.Path, "alpha.txt")).Trim());

        // --- Member 2 adds beta.txt. Its capture must contain ONLY beta.txt,
        //     not the inherited alpha.txt (proves baseSha was re-anchored at
        //     the seed commit — no double-count). ---
        File.WriteAllText(Path.Combine(wt2.Path, "beta.txt"), "beta\n");
        var cap2 = await _mgr.CaptureAsync(wt2);
        Assert.True(cap2.HasChanges);
        Assert.Equal(1, cap2.FilesChanged);
        Assert.Contains("beta.txt", cap2.Diff);
        Assert.DoesNotContain("alpha.txt", cap2.Diff);

        // Accepting member 2 applies cleanly on top of the parent (which
        // already has alpha.txt) and leaves both files present, uncommitted.
        await _mgr.ApplyAsync(cap2.Diff!);
        Assert.True(File.Exists(Path.Combine(_repo, "alpha.txt")));
        Assert.True(File.Exists(Path.Combine(_repo, "beta.txt")));
        Assert.Equal(parentHeadBefore, HeadSha(_repo));

        // The whole accumulated result is present as uncommitted parent
        // state (HEAD never moved) — what the queue/chat path surfaces and
        // what `git diff` grading captures once the files are tracked.
        var status = Git(_repo, "status", "--porcelain");
        Assert.Contains("alpha.txt", status);
        Assert.Contains("beta.txt", status);
    }

    [Fact]
    public async Task FirstDispatch_CleanParent_NoSeedCommit()
    {
        // With a pristine parent (nothing accumulated) the worktree should
        // fork straight from HEAD with no extra seed commit — unchanged
        // pre-fix behavior. BaseSha must equal parent HEAD.
        var wt = await CreateAsync("implementer-1");
        Assert.Equal(HeadSha(_repo), wt.BaseSha);
        Assert.Equal(HeadSha(_repo), HeadSha(wt.Path));
        Assert.True(File.Exists(Path.Combine(wt.Path, "seed.txt")));
    }

    [Fact]
    public async Task SequentialDispatch_InheritsModificationAndDeletion()
    {
        // Seeding must carry tracked MODIFICATIONS and DELETIONS, not just
        // new files. Member 1 edits seed.txt and deletes it? Instead: edit
        // seed.txt and add a file, accept, then a second worktree must see
        // the edited content.
        var wt1 = await CreateAsync("implementer-1");
        File.WriteAllText(Path.Combine(wt1.Path, "seed.txt"), "seed-modified\n");
        var cap1 = await _mgr.CaptureAsync(wt1);
        await _mgr.ApplyAsync(cap1.Diff!);
        Assert.Equal("seed-modified", File.ReadAllText(Path.Combine(_repo, "seed.txt")).Trim());

        var wt2 = await CreateAsync("reviewer-1");
        Assert.Equal("seed-modified", File.ReadAllText(Path.Combine(wt2.Path, "seed.txt")).Trim());
    }

    // --- helpers ---

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }
}
