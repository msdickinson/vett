using System.Diagnostics;
using Vett.Agent;

namespace Vett.Tests;

/// <summary>
/// Evidence-preservation coverage for the dispatch worktree lifecycle.
///
/// THE ONLY EVIDENCE OF WHAT A DISPATCHED MEMBER DID IS ITS WORKTREE AND
/// THE DIFF CAPTURED FROM IT. Two defects in that path destroy the
/// measurement silently, months before anyone looks:
///
///  1. CROSS-RUN PATH COLLISION. The dispatch path is
///     ~/.vett/dispatches/&lt;panelId&gt;/&lt;taskId&gt; — a process-wide
///     constant that contains NOTHING identifying the run. panelId is the
///     coordinator's sessionId, which for team-bench is
///     "team-bench-&lt;instanceId&gt;" (Harness.cs:363), so two vett
///     processes running the SAME suite instance concurrently (the campaign
///     runs arms in parallel) resolve to the SAME directory even though
///     their parent repos are different temp workspaces. CreateAsync's
///     pre-clean then reclaims it with an unconditional
///     Directory.Delete(path, recursive: true) — deleting the OTHER run's
///     LIVE worktree, mid-dispatch, with its member's uncaptured work in it.
///
///  2. COULD-NOT-MEASURE REPORTED AS MEASURED-ZERO. CaptureAsync ran four
///     git commands and discarded every exit code, so a git FAILURE and a
///     member that genuinely changed nothing produced byte-identical
///     results (HasChanges=false, FilesChanged=0). Coordinator.cs:623-628
///     acts on that destructively — "No changes — clean up the worktree
///     right away" — so the failure path deletes the very worktree whose
///     diff could not be read. The codebase already applied this exact law
///     one level up (Coordinator.cs:643-654 emits files_changed as null,
///     NOT 0, when no capture ran); it was never applied inside the capture.
///
/// These drive the real DispatchWorktreeManager against throwaway git
/// repos, so they need `git` on PATH (same as the manager itself).
/// Panel ids are GUID-unique and only those panel dirs are removed, so the
/// tests never touch another run's state under ~/.vett/dispatches.
/// </summary>
public class WorkspaceEvidenceLifecycleTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly List<string> _panelIds = new();

    public void Dispose()
    {
        foreach (var p in _panelIds)
        {
            try { Directory.Delete(Path.Combine(DispatchWorktreeManager.DispatchesRoot, p), true); } catch { }
        }
        foreach (var d in _tempDirs)
        {
            try { ForceDelete(d); } catch { }
        }
    }

    /// <summary>
    /// Recursive delete that also works on a git repo ON WINDOWS. git marks
    /// loose objects and packs READ-ONLY, and Directory.Delete(recursive)
    /// throws UnauthorizedAccessException on a read-only file rather than
    /// clearing the attribute — measured here, see the report. Linux has no
    /// such rule, which is why the harness's own
    /// `Directory.Delete(workspace, recursive: true)` behaves differently on
    /// the two platforms. Only ever called on directories this test created.
    /// </summary>
    private static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(dir, recursive: true);
    }

    private string NewRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), "vett-wel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        _tempDirs.Add(repo);
        Git(repo, "init");
        Git(repo, "config", "user.email", "test@vett.local");
        Git(repo, "config", "user.name", "vett-test");
        Git(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "init");
        return repo;
    }

    private string NewPanelId()
    {
        var p = "wel-" + Guid.NewGuid().ToString("N")[..8];
        _panelIds.Add(p);
        return p;
    }

    private static string CanonicalPath(string panelId, string taskId) =>
        Path.Combine(DispatchWorktreeManager.DispatchesRoot, panelId, taskId);

    // ---------------------------------------------------------------
    // 1. Cross-run collision
    // ---------------------------------------------------------------

    /// <summary>
    /// POSITIVE CASE. Two vett runs, two DIFFERENT parent repos, the SAME
    /// panelId + taskId (what the campaign produces when two arms run the
    /// same suite instance concurrently). Run A's worktree is live and holds
    /// the member's work. Run B must not be able to delete it.
    ///
    /// Before the fix: B's pre-clean Directory.Delete succeeded — A's
    /// worktree, and the only copy of its member's edits, was gone.
    /// </summary>
    [Fact]
    public async Task ConcurrentRun_SamePanelId_DoesNotDestroyAnotherRepoLiveWorktree()
    {
        const string taskId = "implementer-1";
        var panelId = NewPanelId();
        var repoA = NewRepo();
        var repoB = NewRepo();

        var mgrA = new DispatchWorktreeManager(repoA, panelId);
        var mgrB = new DispatchWorktreeManager(repoB, panelId);

        var wtA = await mgrA.CreateAsync(taskId);

        // SANITY-CHECK THE PREMISE, don't assume it: the whole finding rests
        // on both runs resolving to the SAME directory. If they didn't, this
        // test would prove nothing.
        Assert.Equal(CanonicalPath(panelId, taskId), wtA.Path);

        // The member's work — the evidence this test is about.
        var evidence = Path.Combine(wtA.Path, "member-work.txt");
        File.WriteAllText(evidence, "the only copy of what the member did\n");
        Assert.True(File.Exists(evidence));

        var diagnostics = new List<string>();
        var wtB = await mgrB.CreateAsync(taskId, default, d => diagnostics.Add(d));

        // THE CLAIM: run A's evidence survived run B's pre-clean.
        Assert.True(File.Exists(evidence),
            "run B's pre-clean deleted run A's LIVE dispatch worktree — the member's work is gone");
        Assert.True(File.Exists(Path.Combine(wtA.Path, ".git")),
            "run A's worktree is no longer a git worktree — B partially destroyed it");

        // Run B still gets real isolation, just at a different name.
        Assert.NotEqual(wtA.Path, wtB.Path);
        Assert.True(File.Exists(Path.Combine(wtB.Path, "seed.txt")));

        // And it was ANNOUNCED. A silent path change is how the last one of
        // these went unnoticed for a whole campaign.
        Assert.Single(diagnostics);
        Assert.Contains("live git worktree", diagnostics[0]);
    }

    /// <summary>
    /// NEGATIVE CONTROL for the same guard, on the OTHER destructive path.
    /// PreCleanByTaskIdAsync is the cancellation-cleanup route: it runs the
    /// same ops without a DispatchWorktree handle, so it had the same
    /// unconditional delete. A cancelled dispatch in run B must not reap run
    /// A's live worktree either.
    /// </summary>
    [Fact]
    public async Task PreCleanByTaskId_DoesNotDestroyAnotherRepoLiveWorktree()
    {
        const string taskId = "implementer-7";
        var panelId = NewPanelId();
        var repoA = NewRepo();
        var repoB = NewRepo();

        var wtA = await new DispatchWorktreeManager(repoA, panelId).CreateAsync(taskId);
        Assert.Equal(CanonicalPath(panelId, taskId), wtA.Path);
        var evidence = Path.Combine(wtA.Path, "member-work.txt");
        File.WriteAllText(evidence, "still the only copy\n");

        await new DispatchWorktreeManager(repoB, panelId).PreCleanByTaskIdAsync(taskId);

        Assert.True(File.Exists(evidence),
            "PreCleanByTaskIdAsync deleted another repo's live dispatch worktree");
    }

    /// <summary>
    /// NEGATIVE CONTROL — proves the guard is not unconditional, and that it
    /// does not turn ~/.vett/dispatches into a landfill. A worktree whose
    /// backing repo is GONE (exactly what a PASSing bench run leaves behind
    /// after Harness deletes its workspace) is an orphan: nothing can ever
    /// read it as a worktree again, so the canonical path is reclaimed and
    /// NO diagnostic fires.
    ///
    /// Without this control, a manager that always took a unique sibling
    /// would pass the positive case while quietly changing every dispatch.
    /// </summary>
    [Fact]
    public async Task OrphanedWorktree_WhoseRepoIsGone_IsReclaimed_AtCanonicalPath()
    {
        const string taskId = "implementer-2";
        var panelId = NewPanelId();
        var repoA = NewRepo();
        var repoB = NewRepo();

        var wtA = await new DispatchWorktreeManager(repoA, panelId).CreateAsync(taskId);
        Assert.Equal(CanonicalPath(panelId, taskId), wtA.Path);

        // Kill the parent repo the way Harness.cs does on PASS. Only a
        // directory this test created is removed. (ForceDelete, not plain
        // Directory.Delete: on Windows git's read-only object files make the
        // plain call throw — the harness's own delete is best-effort and
        // swallows exactly that, which is a platform difference worth
        // knowing about but is not what this test is measuring.)
        ForceDelete(repoA);
        // SANITY-CHECK: the worktree's gitdir really is unreachable now, so
        // "orphan" is a measured fact, not an assumption.
        Assert.False(Directory.Exists(repoA));

        var diagnostics = new List<string>();
        var wtB = await new DispatchWorktreeManager(repoB, panelId)
            .CreateAsync(taskId, default, d => diagnostics.Add(d));

        Assert.Equal(CanonicalPath(panelId, taskId), wtB.Path);
        Assert.Empty(diagnostics);
    }

    // ---------------------------------------------------------------
    // 2. Capture: could-not-measure vs measured-zero
    // ---------------------------------------------------------------

    /// <summary>
    /// POSITIVE CASE. A worktree that DOES contain the member's work, but
    /// whose git diff cannot be computed (here: the recorded base object is
    /// not resolvable — the same observable as an index.lock collision, a
    /// full disk, or a pruned object store: git exits non-zero and prints
    /// nothing to stdout).
    ///
    /// Before the fix CaptureAsync discarded every exit code and returned
    /// HasChanges=false / FilesChanged=0 — byte-identical to a member that
    /// did nothing — and Coordinator.cs:627 then discarded the worktree.
    /// </summary>
    [Fact]
    public async Task Capture_WhenGitFails_DoesNotReportItAsNoChanges()
    {
        var panelId = NewPanelId();
        var repo = NewRepo();
        var mgr = new DispatchWorktreeManager(repo, panelId);
        var wt = await mgr.CreateAsync("implementer-3");

        File.WriteAllText(Path.Combine(wt.Path, "member-work.txt"), "real work\n");

        // SANITY-CHECK THE PREMISE: with the real base SHA this very worktree
        // reports the change. So anything that reports "no changes" below is
        // reporting a MEASUREMENT FAILURE, not an empty diff.
        var honest = await mgr.CaptureAsync(wt);
        Assert.True(honest.HasChanges);
        Assert.Equal(1, honest.FilesChanged);

        // Same worktree, unresolvable base — git exits non-zero on every diff.
        var broken = wt with { BaseSha = new string('0', 40) };
        var ex = await Assert.ThrowsAsync<DispatchCaptureFailedException>(
            () => mgr.CaptureAsync(broken));
        Assert.Contains("git", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// NEGATIVE CONTROL — a genuinely empty dispatch must still report
    /// "no changes" and must NOT throw. Otherwise the fix above would just
    /// convert every clean no-op dispatch into a fault.
    /// </summary>
    [Fact]
    public async Task Capture_WithGenuinelyNoChanges_ReportsZero_WithoutThrowing()
    {
        var panelId = NewPanelId();
        var repo = NewRepo();
        var mgr = new DispatchWorktreeManager(repo, panelId);
        var wt = await mgr.CreateAsync("implementer-4");

        var cap = await mgr.CaptureAsync(wt);

        Assert.False(cap.HasChanges);
        Assert.Equal(0, cap.FilesChanged);
        Assert.Null(cap.Diff);
    }

    private static void Git(string cwd, params string[] args)
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
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }
}
