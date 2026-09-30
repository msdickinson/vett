using System.Diagnostics;
using System.Runtime.InteropServices;
using Vett.Agent;

namespace Vett.Tests;

/// <summary>
/// A fact that reports as SKIPPED (not PASSED) off Windows. The behaviour under
/// test is a Win32 device-namespace quirk; a test that silently early-returns
/// would read exactly like one that ran and passed.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string reason = "reserved device names are not special elsewhere.")
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = "Windows-only: " + reason;
    }
}

/// <summary>
/// ONE UNREADABLE ARTIFACT MUST NOT DESTROY A WHOLE DISPATCH.
///
/// THE DEFECT (measured 2026-08-26, team-fanout-tier2/fan5, ds-team-lead-pro).
/// The Pro leader fanned out five members correctly — the `assign_async`
/// min_count=5 gate passed — and all five lost their work. Each capture died
/// with the same stderr:
///
///     error: short read while indexing nul
///     error: nul: failed to insert into database
///     error: unable to index file 'nul'
///     fatal: adding files failed
///
/// A member had run a build with a DOS-style `> nul` redirect. Under the bash
/// sandbox that does NOT discard output — it creates a real directory entry
/// named `nul`, which Win32 resolves to the NUL device, so git's read comes
/// back short. `git add -A` is all-or-nothing, so that one unreadable entry
/// took five members' genuine edits with it.
///
/// WHY EXCLUDE AND NOT DELETE. The obvious fix — remove the offending file —
/// does not work: `rm` on a file named `nul` fails with "Device or resource
/// busy" because the path resolves to the device, not the entry. (Deleting it
/// at all needs the `\\?\` extended-path prefix, which is how the fixture
/// below both creates and removes one.) Deleting is also destructive in a
/// place where the worktree may be the last copy of a member's work. The fix
/// leaves the file alone and excludes it from the stage.
///
/// These tests drive the REAL DispatchWorktreeManager against a throwaway git
/// repo, and the fixture creates a REAL unreadable entry — not a mock. The
/// rig is proven honest by ReservedName_GenuinelyBreaksPlainGitAdd below: if
/// that test ever goes green-by-passing-add, the other tests are measuring
/// nothing.
/// </summary>
public class DispatchCaptureReservedNameTests : IDisposable
{
    private readonly string _repo;
    private readonly string _panelId;
    private readonly DispatchWorktreeManager _mgr;
    private readonly List<DispatchWorktree> _created = new();
    private readonly List<string> _devicePaths = new();

    public DispatchCaptureReservedNameTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vett-nul-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);
        _panelId = "test-" + Guid.NewGuid().ToString("N")[..8];

        Git(_repo, "init");
        Git(_repo, "config", "user.email", "test@vett.local");
        Git(_repo, "config", "user.name", "vett-test");
        Git(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "Alpha.cs"), "public const int SchemaVersion = 1;\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");

        _mgr = new DispatchWorktreeManager(_repo, _panelId);
    }

    public void Dispose()
    {
        // Device-named entries FIRST: they are what makes the directory
        // undeletable by every ordinary path, including Directory.Delete.
        foreach (var p in _devicePaths)
        {
            try { File.Delete(@"\\?\" + p); } catch { }
        }
        foreach (var wt in _created)
        {
            try { _mgr.DiscardAsync(wt).GetAwaiter().GetResult(); } catch { }
        }
        try { Git(_repo, "worktree", "prune"); } catch { }
        try { Directory.Delete(Path.Combine(DispatchWorktreeManager.DispatchesRoot, _panelId), true); } catch { }
        try { Directory.Delete(_repo, true); } catch { }
    }

    /// <summary>
    /// Create a genuinely unreadable Win32 device entry. Plain
    /// File.WriteAllText("...\\nul") writes to the DEVICE and leaves no
    /// directory entry at all — it would produce a fixture that quietly tests
    /// nothing. The `\\?\` prefix bypasses device resolution and creates the
    /// real entry the sandbox's `> nul` redirect produces.
    /// </summary>
    private void MakeDeviceFile(string dir, string name)
    {
        var full = Path.Combine(dir, name);
        File.WriteAllText(@"\\?\" + full, "junk");
        _devicePaths.Add(full);
        Assert.Contains(name, Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName));
    }

    private async Task<DispatchWorktree> CreateAsync(string taskId)
    {
        var wt = await _mgr.CreateAsync(taskId);
        _created.Add(wt);
        return wt;
    }

    // ---- the rig is honest ---------------------------------------------------

    /// <summary>
    /// THE RIG CHECK. Everything else here is only meaningful if a bare
    /// `git add -A` genuinely fails on this fixture. A green control proves
    /// only the case it constructs — so construct the failing case and prove
    /// it fails, on this machine, with this git.
    /// </summary>
    [WindowsOnlyFact]
    public async Task ReservedName_GenuinelyBreaksPlainGitAdd()
    {
        var wt = await CreateAsync("implementer-1");
        MakeDeviceFile(wt.Path, "nul");

        var (ec, _, stderr) = RawGit(wt.Path, "add", "-A");

        Assert.NotEqual(0, ec);
        Assert.Contains("nul", stderr, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the defect ----------------------------------------------------------

    /// <summary>
    /// The member's real edit must survive an unreadable sibling. This is the
    /// whole point: before the fix this capture threw and the edit was lost.
    /// </summary>
    [WindowsOnlyFact]
    public async Task Capture_WithUnreadableNul_StillCapturesTheRealEdit()
    {
        var wt = await CreateAsync("implementer-1");
        File.WriteAllText(Path.Combine(wt.Path, "Alpha.cs"), "public const int SchemaVersion = 2;\n");
        MakeDeviceFile(wt.Path, "nul");

        var cap = await _mgr.CaptureAsync(wt);

        Assert.True(cap.HasChanges, "the real edit was lost to the unreadable sibling");
        Assert.Equal(1, cap.FilesChanged);
        Assert.Contains("SchemaVersion = 2", cap.Diff);
    }

    /// <summary>
    /// PUBLISH THE DISCARD COUNT. A capture that silently drops an entry is a
    /// different failure from the one being fixed, not a fix. The skip has to
    /// reach both the programmatic field and DiffStat — DiffStat is what the
    /// leader reads back from review_dispatch and what rides out as the
    /// `diff_summary` event field.
    /// </summary>
    [WindowsOnlyFact]
    public async Task Capture_WithUnreadableNul_ReportsWhatItSkipped()
    {
        var wt = await CreateAsync("implementer-1");
        File.WriteAllText(Path.Combine(wt.Path, "Alpha.cs"), "public const int SchemaVersion = 2;\n");
        MakeDeviceFile(wt.Path, "nul");

        var cap = await _mgr.CaptureAsync(wt);

        Assert.Equal(["nul"], cap.SkippedPaths);
        Assert.Contains("skipped 1", cap.DiffStat);
        Assert.Contains("nul", cap.DiffStat);
    }

    /// <summary>
    /// End to end: the captured diff must actually APPLY to the parent. A diff
    /// that captures cleanly but cannot be replayed would still lose the work,
    /// just one step later.
    /// </summary>
    [WindowsOnlyFact]
    public async Task Capture_WithUnreadableNul_DiffAppliesToParent()
    {
        var wt = await CreateAsync("implementer-1");
        File.WriteAllText(Path.Combine(wt.Path, "Alpha.cs"), "public const int SchemaVersion = 2;\n");
        MakeDeviceFile(wt.Path, "nul");

        var cap = await _mgr.CaptureAsync(wt);
        await _mgr.ApplyAsync(cap.Diff!);

        Assert.Contains("SchemaVersion = 2", File.ReadAllText(Path.Combine(_repo, "Alpha.cs")));
        // The unreadable entry must NOT be replayed into the parent — it could
        // not be staged, so it cannot be in the diff.
        Assert.False(File.Exists(Path.Combine(_repo, "nul")));
    }

    /// <summary>
    /// A reserved name in a SUBDIRECTORY breaks the stage exactly the same way
    /// (the fan5 members ran builds in nested output dirs), so the walk has to
    /// find it there and the exclude pathspec has to be repo-relative and
    /// forward-slashed for git to honour it.
    /// </summary>
    [WindowsOnlyFact]
    public async Task Capture_WithNestedUnreadableName_StillCapturesTheRealEdit()
    {
        var wt = await CreateAsync("implementer-1");
        File.WriteAllText(Path.Combine(wt.Path, "Alpha.cs"), "public const int SchemaVersion = 2;\n");
        var sub = Path.Combine(wt.Path, "obj", "Debug");
        Directory.CreateDirectory(sub);
        MakeDeviceFile(sub, "nul");

        var cap = await _mgr.CaptureAsync(wt);

        Assert.True(cap.HasChanges);
        Assert.Contains("SchemaVersion = 2", cap.Diff);
        Assert.Equal(["obj/Debug/nul"], cap.SkippedPaths);
    }

    // ---- the controls --------------------------------------------------------

    /// <summary>
    /// THE CONTROL FOR THE HEALTHY PATH. Without it, a capture that reported a
    /// skip on every run would pass every test above. A clean worktree must
    /// report NOTHING skipped and carry no note in DiffStat.
    /// </summary>
    [Fact]
    public async Task Capture_WithNoReservedNames_ReportsNoSkips()
    {
        var wt = await CreateAsync("implementer-1");
        File.WriteAllText(Path.Combine(wt.Path, "Alpha.cs"), "public const int SchemaVersion = 2;\n");

        var cap = await _mgr.CaptureAsync(wt);

        Assert.True(cap.HasChanges);
        Assert.Empty(cap.SkippedPaths);
        Assert.DoesNotContain("skipped", cap.DiffStat);
    }

    /// <summary>
    /// COULD-NOT-MEASURE IS NOT MEASURED-ZERO, still. The retry is gated on the
    /// walk finding something; when it finds nothing the original add failure
    /// must be rethrown rather than swallowed into a false empty capture. This
    /// asserts the gate condition directly — on a clean tree the walk returns
    /// empty, so the rethrow branch is the one taken.
    /// </summary>
    [Fact]
    public async Task CleanWorktree_FindsNothingToSkip_SoAnAddFailureWouldStillThrow()
    {
        var wt = await CreateAsync("implementer-1");
        File.WriteAllText(Path.Combine(wt.Path, "Alpha.cs"), "public const int SchemaVersion = 2;\n");

        Assert.Empty(DispatchWorktreeManager.FindReservedDeviceNames(wt.Path));
    }

    /// <summary>
    /// The walk must not report anything inside `.git`. Git's own object store
    /// is not the member's work, and excluding a path in there would be
    /// meaningless at best.
    /// </summary>
    [Fact]
    public async Task Walk_IgnoresTheGitDirectory()
    {
        var wt = await CreateAsync("implementer-1");
        var found = DispatchWorktreeManager.FindReservedDeviceNames(wt.Path);

        Assert.DoesNotContain(found, p => p.StartsWith(".git", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the classifier, two-sided ------------------------------------------

    [Theory]
    [InlineData("nul")]
    [InlineData("NUL")]
    [InlineData("Nul")]
    [InlineData("nul.txt")]     // Win32 resolves the DEVICE for these too
    [InlineData("NUL.log")]
    [InlineData("con")]
    [InlineData("prn")]
    [InlineData("aux")]
    [InlineData("clock$")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    public void ReservedNames_AreClassifiedReserved(string name)
        => Assert.True(DispatchWorktreeManager.IsReservedDeviceName(name), name);

    /// <summary>
    /// THE CONTROL FOR THE CLASSIFIER, and the one that matters most — these
    /// are real source-file names. A classifier matching on `Contains("nul")`
    /// or on a prefix would silently exclude `nullable.cs` or `console.log`
    /// from a member's diff, which is the very work-loss this fix exists to
    /// prevent, inflicted by the fix itself.
    /// </summary>
    [Theory]
    [InlineData("nullable.cs")]
    [InlineData("null.txt")]
    [InlineData("nuls")]
    [InlineData("console.log")]
    [InlineData("Console.cs")]
    [InlineData("communication.md")]
    [InlineData("auxiliary.json")]
    [InlineData("printer.cs")]
    [InlineData("com10")]       // only COM1-COM9 are devices
    [InlineData("lpt0")]
    [InlineData("Alpha.cs")]
    [InlineData("")]
    public void OrdinaryNames_AreNotClassifiedReserved(string name)
        => Assert.False(DispatchWorktreeManager.IsReservedDeviceName(name), name);

    // ---- helpers -------------------------------------------------------------

    private static (int Ec, string Stdout, string Stderr) RawGit(string cwd, params string[] args)
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
        return (p.ExitCode, stdout, stderr);
    }

    private static string Git(string cwd, params string[] args)
    {
        var (ec, stdout, stderr) = RawGit(cwd, args);
        if (ec != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({ec}): {stderr}");
        return stdout;
    }
}
