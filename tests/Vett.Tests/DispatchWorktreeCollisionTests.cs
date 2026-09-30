using System.Diagnostics;
using Vett.Agent;

namespace Vett.Tests;

/// <summary>
/// Regression coverage for the STALE-WORKTREE COLLISION LATCH.
///
/// Failure this pins down (measured 2026-08-24, campaign run armD-run4):
/// CreateAsync pre-cleans the canonical dispatch path, but every step of that
/// pre-clean is best-effort — three git calls with ignoreErrors:true and a
/// `Directory.Delete` wrapped in `catch { }`. When the directory survived
/// anyway (a live handle, a process cwd inside it, or a worktree registered
/// to a DIFFERENT repo so `git worktree remove` here knows nothing about it),
/// the code walked into `git worktree add`, which failed with
/// "'&lt;path&gt;' already exists" and threw WorktreeNotSupportedException.
///
/// Coordinator then latched worktrees off for the WHOLE session, so 12 of 14
/// dispatches ran with no isolation, their diffs were never captured, the
/// leader saw nothing to accept, and the run surfaced only as a 7200s
/// timeout — indistinguishable from a model/cap result.
///
/// These drive the real DispatchWorktreeManager against a throwaway git repo,
/// so they need `git` on PATH (same as the manager itself).
/// </summary>
public class DispatchWorktreeCollisionTests : IDisposable
{
    private readonly string _repo;
    private readonly string _panelId;
    private readonly DispatchWorktreeManager _mgr;
    private readonly List<DispatchWorktree> _created = new();

    public DispatchWorktreeCollisionTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vett-wtc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);
        _panelId = "testc-" + Guid.NewGuid().ToString("N")[..8];

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

    private string CanonicalPath(string taskId) =>
        Path.Combine(DispatchWorktreeManager.DispatchesRoot, _panelId, taskId);

    /// <summary>
    /// POSITIVE CASE — the actual bug. An UNDELETABLE directory sits at the
    /// canonical path. We reproduce "undeletable" the way it happens in the
    /// wild: hold an open handle with FileShare.None on a file inside it, so
    /// Directory.Delete genuinely throws rather than us faking a failure.
    /// </summary>
    [WindowsOnlyFact("an open handle blocks deletion only on Windows.")]
    public async Task StaleUndeletableDirectory_RecoversToUniquePath_AndKeepsIsolation()
    {
        const string taskId = "implementer-2";
        var canonical = CanonicalPath(taskId);
        Directory.CreateDirectory(canonical);
        var blocker = Path.Combine(canonical, "held-open.bin");
        File.WriteAllText(blocker, "a prior run left this behind\n");

        // Hold the handle for the duration of CreateAsync. With this open,
        // Directory.Delete(canonical, recursive: true) throws — which is
        // exactly the silent failure the pre-clean used to swallow.
        using (var _ = new FileStream(blocker, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Sanity-check the premise rather than assuming it: if this
            // delete were to SUCCEED, the test would be proving nothing.
            Assert.ThrowsAny<Exception>(() => Directory.Delete(canonical, recursive: true));

            var diagnostics = new List<string>();
            var wt = await _mgr.CreateAsync(taskId, default, d => diagnostics.Add(d));
            _created.Add(wt);

            // 1. It did NOT throw — before the fix this threw
            //    WorktreeNotSupportedException("... already exists").
            // 2. It moved to a different path rather than reusing the
            //    occupied one.
            Assert.NotEqual(canonical, wt.Path);
            Assert.StartsWith(canonical, wt.Path, StringComparison.OrdinalIgnoreCase);

            // 3. The dispatch really is isolated: a real checkout landed.
            Assert.True(Directory.Exists(wt.Path));
            Assert.True(File.Exists(Path.Combine(wt.Path, "seed.txt")));

            // 4. The recovery was ANNOUNCED, not silent. The whole failure
            //    mode was that this class of fault made no noise.
            Assert.Single(diagnostics);
            Assert.Contains("could not be removed", diagnostics[0]);

            // 5. The stale directory is left intact for inspection.
            Assert.True(File.Exists(blocker));
        }
    }

    /// <summary>
    /// NEGATIVE CONTROL — proves the test above can actually fail, and that
    /// recovery is not firing unconditionally. With no collision, the
    /// canonical path must still be used and NO diagnostic emitted. Without
    /// this, a manager that always took a unique path would pass the
    /// positive case while silently changing behavior for every dispatch.
    /// </summary>
    [Fact]
    public async Task NoCollision_UsesCanonicalPath_AndEmitsNoDiagnostic()
    {
        const string taskId = "implementer-1";
        var diagnostics = new List<string>();

        var wt = await _mgr.CreateAsync(taskId, default, d => diagnostics.Add(d));
        _created.Add(wt);

        Assert.Equal(CanonicalPath(taskId), wt.Path);
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// A DELETABLE stale directory must still be cleaned in place and reuse
    /// the canonical path — the pre-clean is not replaced by the recovery,
    /// only backstopped by it.
    /// </summary>
    [Fact]
    public async Task StaleDeletableDirectory_IsCleanedInPlace_AndReusesCanonicalPath()
    {
        const string taskId = "implementer-3";
        var canonical = CanonicalPath(taskId);
        Directory.CreateDirectory(canonical);
        File.WriteAllText(Path.Combine(canonical, "leftover.txt"), "deletable\n");

        var diagnostics = new List<string>();
        var wt = await _mgr.CreateAsync(taskId, default, d => diagnostics.Add(d));
        _created.Add(wt);

        Assert.Equal(canonical, wt.Path);
        Assert.Empty(diagnostics);
        Assert.False(File.Exists(Path.Combine(canonical, "leftover.txt")));
    }

    /// <summary>
    /// Fix B's classification. A non-git parent is PERMANENT (latching is
    /// correct — it cannot change mid-session). The default for every other
    /// cause must be NON-permanent, because latching on a recoverable fault
    /// is what voided armD-run4.
    /// </summary>
    [Fact]
    public async Task NonGitParent_ThrowsPermanent_WhileDefaultIsRecoverable()
    {
        var notARepo = Path.Combine(Path.GetTempPath(), "vett-notrepo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(notARepo);
        try
        {
            var mgr = new DispatchWorktreeManager(notARepo, "panel-x");
            var ex = await Assert.ThrowsAsync<WorktreeNotSupportedException>(
                () => mgr.CreateAsync("implementer-1"));

            Assert.True(ex.IsPermanent, "a non-git parent cannot change mid-session, so it may latch");

            // The DEFAULT must be recoverable — this is the safe direction:
            // being wrong costs a few extra git calls, versus silently
            // voiding a two-hour run.
            Assert.False(new WorktreeNotSupportedException("transient git failure").IsPermanent);
        }
        finally
        {
            try { Directory.Delete(notARepo, true); } catch { }
        }
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
