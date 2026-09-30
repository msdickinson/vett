using System.Diagnostics;
using Vett.Agent;

namespace Vett.Tests;

/// <summary>
/// Regression coverage for the subdirectory-cwd dispatch bug.
///
/// `git worktree add` always checks out the WHOLE repo, so the worktree
/// root corresponds to the repo toplevel. When vett runs from a
/// subdirectory of that repo (cwd = &lt;repo&gt;/apps/static-analysis, the
/// real shakedown layout), dropping a dispatched member at the worktree
/// ROOT shifts every relative path by the subdirectory offset. The member
/// is told to edit `src/Foo.cs`, looks there, finds nothing, and honestly
/// reports the file missing — while creating any new files at the wrong
/// level. Observed live on queue task #43.
///
/// The fix: DispatchWorktree.Cwd = worktree root + the parent's
/// `git rev-parse --show-prefix` offset.
/// </summary>
public class DispatchWorktreeSubdirCwdTests : IDisposable
{
    private readonly string _repo;
    private readonly string _subdir;
    private readonly string _panelId;
    private readonly List<(DispatchWorktreeManager Mgr, DispatchWorktree Wt)> _created = new();

    public DispatchWorktreeSubdirCwdTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vett-sub-" + Guid.NewGuid().ToString("N")[..8]);
        _subdir = Path.Combine(_repo, "apps", "proj");
        Directory.CreateDirectory(Path.Combine(_subdir, "src"));
        _panelId = "subtest-" + Guid.NewGuid().ToString("N")[..8];

        Git(_repo, "init");
        Git(_repo, "config", "user.email", "test@vett.local");
        Git(_repo, "config", "user.name", "vett-test");
        Git(_repo, "config", "commit.gpgsign", "false");
        // A file that only exists under the subdirectory offset.
        File.WriteAllText(Path.Combine(_subdir, "src", "Target.cs"), "// original\n");
        File.WriteAllText(Path.Combine(_repo, "root.txt"), "root\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");
    }

    public void Dispose()
    {
        foreach (var (mgr, wt) in _created)
        {
            try { mgr.DiscardAsync(wt).GetAwaiter().GetResult(); } catch { }
        }
        // Discarding the worktrees does not remove the panel dir that holds
        // them (DispatchWorktreeManager.cs:150 creates it). See the longer note
        // in DispatchWorktreeSparseTests.Dispose.
        try { Git(_repo, "worktree", "prune"); } catch { }
        try { Directory.Delete(Path.Combine(DispatchWorktreeManager.DispatchesRoot, _panelId), true); } catch { }
        try { Directory.Delete(_repo, true); } catch { }
    }

    private async Task<(DispatchWorktreeManager, DispatchWorktree)> CreateFrom(string parentCwd, string taskId)
    {
        var mgr = new DispatchWorktreeManager(parentCwd, _panelId);
        var wt = await mgr.CreateAsync(taskId);
        _created.Add((mgr, wt));
        return (mgr, wt);
    }

    [Fact]
    public async Task SubdirCwd_MemberCwd_IsOffsetIntoWorktree_NotRoot()
    {
        var (_, wt) = await CreateFrom(_subdir, "implementer-1");

        // The worktree root is the repo toplevel: root.txt lives there.
        Assert.True(File.Exists(Path.Combine(wt.Path, "root.txt")));

        // The member must be dropped at <worktree>/apps/proj, not at the root.
        Assert.NotEqual(wt.Path, wt.Cwd);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(wt.Path, "apps", "proj")),
            Path.GetFullPath(wt.Cwd));

        // ...so that the path the leader would use resolves for the member too.
        // This is the exact assertion that fails pre-fix.
        Assert.True(File.Exists(Path.Combine(wt.Cwd, "src", "Target.cs")));
        Assert.False(File.Exists(Path.Combine(wt.Path, "src", "Target.cs")));
    }

    [Fact]
    public async Task ToplevelCwd_MemberCwd_IsWorktreeRoot_Unchanged()
    {
        // The bench case: cwd IS the repo toplevel, prefix is empty.
        // Behavior must be identical to before the fix.
        var (_, wt) = await CreateFrom(_repo, "implementer-toplevel");

        Assert.Equal(Path.GetFullPath(wt.Path), Path.GetFullPath(wt.Cwd));
        Assert.True(File.Exists(Path.Combine(wt.Cwd, "root.txt")));
    }

    [Fact]
    public async Task SubdirCwd_EditAtMemberCwd_AppliesBackToCorrectParentPath()
    {
        var (mgr, wt) = await CreateFrom(_subdir, "implementer-apply");

        // Member edits the file using a path relative to ITS cwd.
        var memberFile = Path.Combine(wt.Cwd, "src", "Target.cs");
        File.WriteAllText(memberFile, "// edited by member\n");

        var cap = await mgr.CaptureAsync(wt);
        Assert.True(cap.HasChanges);
        await mgr.ApplyAsync(cap.Diff!);

        // The change must land at <repo>/apps/proj/src/Target.cs — the
        // subdirectory path — and NOT at <repo>/src/Target.cs.
        Assert.Equal("// edited by member\n",
            File.ReadAllText(Path.Combine(_subdir, "src", "Target.cs")).Replace("\r\n", "\n"));
        Assert.False(File.Exists(Path.Combine(_repo, "src", "Target.cs")));
    }

    [Fact]
    public void MemberCwd_EmptyPrefix_ReturnsRoot()
    {
        Assert.Equal(_repo, DispatchWorktreeManager.MemberCwd(_repo, ""));
        Assert.Equal(_repo, DispatchWorktreeManager.MemberCwd(_repo, "   "));
    }

    [Fact]
    public void MemberCwd_NonexistentPrefix_FallsBackToRoot()
    {
        // Defensive: a prefix that doesn't exist in the worktree must not
        // strand the member in a missing directory.
        Assert.Equal(_repo, DispatchWorktreeManager.MemberCwd(_repo, "no/such/dir/"));
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout;
    }
}
