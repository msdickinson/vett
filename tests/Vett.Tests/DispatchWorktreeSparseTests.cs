using System.Diagnostics;
using Vett.Agent;

namespace Vett.Tests;

/// <summary>
/// Coverage for sparse dispatch worktrees (`VETT_DISPATCH_SPARSE`).
///
/// On the DickinsonBros monorepo a full `git worktree add` writes 15.6 GB
/// and takes ~83 s per member (measured warm, 2026-07-09). Narrowed to the
/// ticket's directories (git cone mode) the same worktree is 0.5 GB in
/// 3.5 s and passes the identical verify.sh gate (228/228 + 90/90 + 68/68,
/// proven live 2026-07-09).
///
/// Contract:
///   - no cones   -> full checkout, byte-identical to prior behavior
///   - cones set  -> only the named cones (+ the parent's own subdir,
///                   auto-added so the member cwd always exists) plus
///                   root-level files are materialized
///   - capture/apply round-trip from a sparse worktree lands edits at
///     the correct parent paths
///
/// Tests inject cones via the manager's constructor instead of mutating
/// VETT_DISPATCH_SPARSE — xUnit runs test classes in parallel, and process
/// env is shared, so an env write here would bleed into every concurrent
/// CreateAsync in the seeding/subdir-cwd suites.
/// </summary>
public class DispatchWorktreeSparseTests : IDisposable
{
    private readonly string _repo;
    private readonly string _subdir;
    private readonly string _panelId;
    private readonly List<(DispatchWorktreeManager Mgr, DispatchWorktree Wt)> _created = new();

    public DispatchWorktreeSparseTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vett-sparse-" + Guid.NewGuid().ToString("N")[..8]);
        _subdir = Path.Combine(_repo, "apps", "proj");
        Directory.CreateDirectory(Path.Combine(_subdir, "src"));
        Directory.CreateDirectory(Path.Combine(_repo, "docs", "proj"));
        Directory.CreateDirectory(Path.Combine(_repo, "bulky", "assets"));
        _panelId = "sparsetest-" + Guid.NewGuid().ToString("N")[..8];

        Git(_repo, "init");
        Git(_repo, "config", "user.email", "test@vett.local");
        Git(_repo, "config", "user.name", "vett-test");
        Git(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_subdir, "src", "Target.cs"), "// original\n");
        File.WriteAllText(Path.Combine(_repo, "docs", "proj", "ticket.md"), "the ticket\n");
        File.WriteAllText(Path.Combine(_repo, "bulky", "assets", "huge.bin"), "pretend I'm 8 GB\n");
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
        // ⛔ DISCARDING THE WORKTREES DOES NOT REMOVE THE PANEL DIRECTORY.
        // DiscardAsync only takes back what it handed out; the panel dir itself
        // is created by DispatchWorktreeManager.cs:150 and outlives every
        // worktree inside it. Without this line each test METHOD strands one
        // `sparsetest-*` dir in the SHARED ~/.vett/dispatches tree — measured
        // at 11 leaked dirs from a single run of this file's three sibling
        // classes. Never let a test leak into a tree the real harness uses.
        try { Git(_repo, "worktree", "prune"); } catch { }
        try { Directory.Delete(Path.Combine(DispatchWorktreeManager.DispatchesRoot, _panelId), true); } catch { }
        try { Directory.Delete(_repo, true); } catch { }
    }

    private async Task<(DispatchWorktreeManager, DispatchWorktree)> CreateFrom(
        string parentCwd, string taskId, IReadOnlyList<string>? sparse)
    {
        var mgr = new DispatchWorktreeManager(parentCwd, _panelId, sparse);
        var wt = await mgr.CreateAsync(taskId);
        _created.Add((mgr, wt));
        return (mgr, wt);
    }

    [Fact]
    public void SparseDirs_EmptyRaw_IsEmpty()
    {
        Assert.Empty(DispatchWorktreeManager.SparseDirs(""));
    }

    [Fact]
    public void SparseDirs_ParsesSeparatorsSlashesAndDedupes()
    {
        var dirs = DispatchWorktreeManager.SparseDirs("apps/proj;docs\\proj/,APPS/PROJ, ,");
        Assert.Equal(["apps/proj", "docs/proj"], dirs);
    }

    [Fact]
    public async Task NoCones_FullCheckout_Unchanged()
    {
        var (_, wt) = await CreateFrom(_subdir, "impl-full", sparse: []);

        // Everything materializes, including the bulky dir this ticket
        // never mentions — exactly the pre-feature behavior.
        Assert.True(File.Exists(Path.Combine(wt.Path, "bulky", "assets", "huge.bin")));
        Assert.True(File.Exists(Path.Combine(wt.Cwd, "src", "Target.cs")));
    }

    [Fact]
    public async Task Cones_OnlyConesAndRootFilesMaterialize()
    {
        var (_, wt) = await CreateFrom(_subdir, "impl-sparse", sparse: ["docs/proj"]);

        // Named cone + root files are present…
        Assert.True(File.Exists(Path.Combine(wt.Path, "docs", "proj", "ticket.md")));
        Assert.True(File.Exists(Path.Combine(wt.Path, "root.txt")));
        // …the parent's own subdir is auto-added even though the cones never
        // named it, so the member cwd exists and resolves relative paths…
        Assert.True(File.Exists(Path.Combine(wt.Cwd, "src", "Target.cs")));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(wt.Path, "apps", "proj")),
            Path.GetFullPath(wt.Cwd));
        // …and the unmentioned bulk stays on the shelf.
        Assert.False(Directory.Exists(Path.Combine(wt.Path, "bulky")));
    }

    [Fact]
    public async Task Cones_CaptureApply_LandsAtCorrectParentPath()
    {
        var (mgr, wt) = await CreateFrom(_subdir, "impl-sparse-apply", sparse: ["docs/proj"]);

        File.WriteAllText(Path.Combine(wt.Cwd, "src", "Target.cs"), "// edited by member\n");
        File.WriteAllText(Path.Combine(wt.Cwd, "src", "New.cs"), "// brand new\n");

        var cap = await mgr.CaptureAsync(wt);
        Assert.True(cap.HasChanges);
        Assert.Equal(2, cap.FilesChanged);
        await mgr.ApplyAsync(cap.Diff!);

        Assert.Equal("// edited by member\n",
            File.ReadAllText(Path.Combine(_subdir, "src", "Target.cs")).Replace("\r\n", "\n"));
        Assert.True(File.Exists(Path.Combine(_subdir, "src", "New.cs")));
    }

    [Fact]
    public async Task Cones_NonexistentCone_MemberStillGetsItsSubdir()
    {
        // A typo'd cone must never strand the member: git accepts unknown
        // cone dirs (they simply materialize nothing), and the auto-added
        // parent prefix keeps the member cwd real.
        var (_, wt) = await CreateFrom(_subdir, "impl-sparse-typo", sparse: ["no/such/dir"]);

        Assert.True(File.Exists(Path.Combine(wt.Cwd, "src", "Target.cs")));
        Assert.True(File.Exists(Path.Combine(wt.Path, "root.txt")));
    }

    [Fact]
    public async Task Cones_ToplevelParent_NoPrefixToAdd_StillWorks()
    {
        // Parent cwd IS the repo toplevel (the bench case): no prefix to
        // auto-add, cones alone define the checkout.
        var (_, wt) = await CreateFrom(_repo, "impl-sparse-toplevel", sparse: ["docs/proj"]);

        Assert.True(File.Exists(Path.Combine(wt.Path, "docs", "proj", "ticket.md")));
        Assert.False(Directory.Exists(Path.Combine(wt.Path, "bulky")));
        Assert.Equal(Path.GetFullPath(wt.Path), Path.GetFullPath(wt.Cwd));
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
