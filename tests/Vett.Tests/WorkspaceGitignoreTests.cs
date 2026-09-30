using System.Diagnostics;
using Vett.Bench.Team;

namespace Vett.Tests;

/// <summary>
/// ⭐ THE FIXTURE MUST BEHAVE LIKE A REAL REPO, OR IT MEASURES ITSELF.
///
/// Dispatch capture runs `git add -A` (DispatchWorktreeManager.cs:499) and
/// grades on the resulting diff — in VETT, only the diff is evidence. The
/// `empty-git-repo` template shipped no .gitignore, so the moment a member ran
/// `dotnet build` inside its worktree, bin/ and obj/ became part of the graded
/// diff: a task that wrote ONE .cs file was captured as a ~20-file change
/// carrying binary .dll and .pdb blobs.
///
/// MEASURED, not supposed — on dispatch_end.diff_summary across three live
/// manager-width runs (2026-08-26): FLASH w20 52/71 non-empty diffs (73%),
/// PRO w10 51/82 (62%), FLASH w10 20/30 (67%); median files_changed = 20.
/// It was not cosmetic: 19 of the 47 nested sub-dispatches in the Flash w20
/// arm were leads re-dispatching over build artifacts in the diff, so the
/// fixture inflated the fan-out that run existed to measure.
///
/// ⚠ THESE TESTS DRIVE REAL GIT, ON PURPOSE. A mock would assert that the
/// string "bin/" is absent from something we built ourselves — it would pass
/// against a fixture with no .gitignore at all. The claim is about what
/// `git add -A` actually stages, so git has to be the one answering.
///
/// The controls matter as much as the assertions: each test also proves a
/// NON-ignored file DOES land in the same diff. Without that, "no bin/ in the
/// diff" is equally satisfied by a diff that is empty because the capture
/// silently broke.
/// </summary>
public class WorkspaceGitignoreTests
{
    private static (int ExitCode, string StdOut) Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return (p.ExitCode, stdout);
    }

    /// <summary>Write the files a `dotnet build` would leave behind, plus one real source file.</summary>
    private static void SimulateBuild(string ws)
    {
        Directory.CreateDirectory(Path.Combine(ws, "bin", "Debug", "net10.0"));
        Directory.CreateDirectory(Path.Combine(ws, "obj", "Debug", "net10.0"));
        File.WriteAllText(Path.Combine(ws, "bin", "Debug", "net10.0", "Domain.deps.json"), "{}");
        File.WriteAllBytes(Path.Combine(ws, "bin", "Debug", "net10.0", "Domain.dll"), new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        File.WriteAllBytes(Path.Combine(ws, "bin", "Debug", "net10.0", "Domain.pdb"), new byte[] { 0x42, 0x53, 0x4A, 0x42 });
        File.WriteAllText(Path.Combine(ws, "obj", "project.assets.json"), "{}");
        File.WriteAllText(Path.Combine(ws, "obj", "Debug", "net10.0", "Domain.AssemblyInfo.cs"), "// generated");

        // the actual work product — the ONLY thing that should be graded
        File.WriteAllText(Path.Combine(ws, "Money.cs"), "namespace Domain;\npublic readonly record struct Money(decimal Amount);\n");
    }

    [Fact]
    public void The_captured_diff_of_a_built_workspace_contains_the_SOURCE_and_NOT_the_build_output()
    {
        var ws = WorkspaceSetup.Create("empty-git-repo");
        try
        {
            SimulateBuild(ws);

            // Mirror capture exactly: stage everything, then read the stat.
            var (addEc, _) = Git(ws, "add", "-A");
            Assert.Equal(0, addEc);
            var (statEc, stat) = Git(ws, "diff", "--cached", "--stat");
            Assert.Equal(0, statEc);

            // CONTROL FIRST — if the real file is missing, the assertions below
            // are satisfied by a broken capture rather than by the ignore rules.
            Assert.Contains("Money.cs", stat);

            Assert.DoesNotContain("bin/", stat);
            Assert.DoesNotContain("obj/", stat);
            Assert.DoesNotContain("Domain.dll", stat);
            Assert.DoesNotContain("Domain.pdb", stat);
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_one_file_task_is_captured_as_ONE_file_not_twenty()
    {
        // The number is the point. `files_changed` feeds suite assertions
        // (files_changed_min) and the leader's review, and it read ~20 for a
        // single-file task. No files_changed_MAX gate exists anywhere in the
        // suites, so nothing else in the product would have noticed this.
        var ws = WorkspaceSetup.Create("empty-git-repo");
        try
        {
            SimulateBuild(ws);
            Git(ws, "add", "-A");
            var (ec, names) = Git(ws, "diff", "--cached", "--name-only");
            Assert.Equal(0, ec);

            var changed = names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(new[] { "Money.cs" }, changed);
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void The_gitignore_is_COMMITTED_so_every_dispatch_worktree_inherits_it()
    {
        // Worktrees are branched from HEAD (`git worktree add ... HEAD`). An
        // uncommitted .gitignore in the parent would be invisible inside them —
        // which is the whole population this fix has to cover. Assert on the
        // COMMIT, not on the file existing on disk.
        var ws = WorkspaceSetup.Create("empty-git-repo");
        try
        {
            var (ec, tracked) = Git(ws, "ls-tree", "--name-only", "HEAD");
            Assert.Equal(0, ec);
            Assert.Contains(".gitignore", tracked.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            // And it must be clean at HEAD — a modified-but-uncommitted ignore
            // file would show up in every member's captured diff as work.
            var (stEc, status) = Git(ws, "status", "--porcelain");
            Assert.Equal(0, stEc);
            Assert.DoesNotContain(".gitignore", status);
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// ⭐ THE END-TO-END CASE: A REAL WORKTREE, A REAL `dotnet build`, REAL CAPTURE.
    ///
    /// Every other test in this file makes two substitutions that both flatter
    /// the fix, and the audit of 2026-08-26 flagged them:
    ///
    ///   1. They SIMULATE the build by writing a hand-picked set of bin/obj
    ///      files. That asserts the ignore covers the paths I THOUGHT of. A real
    ///      build is the only thing that can name a path I did not.
    ///   2. They assert on the PARENT repo. Capture never runs there — it runs
    ///      inside a `git worktree add ... HEAD`, and "the ignore is committed so
    ///      worktrees inherit it" is exactly the step being assumed.
    ///
    /// This test removes both substitutions and mirrors DispatchWorktreeManager
    /// literally: worktree add -b from HEAD (:277), `git add -A` (:499), then
    /// `git diff --cached --stat &lt;baseSha&gt;` (:563). If the fix works anywhere
    /// that matters, it works here.
    ///
    /// It is deliberately NOT skippable. A test that quietly skips when `dotnet`
    /// is missing would report green having measured nothing, which is the
    /// failure mode this whole file exists to avoid.
    /// </summary>
    [Fact]
    public void REAL_worktree_plus_REAL_dotnet_build_captures_ONLY_the_source_file()
    {
        var ws = WorkspaceSetup.Create("empty-git-repo", new Dictionary<string, string>
        {
            ["Domain.csproj"] =
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
              + "<TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable>"
              + "</PropertyGroup></Project>",
        });
        string? wt = null;
        try
        {
            var (shaEc, baseSha) = Git(ws, "rev-parse", "HEAD");
            Assert.Equal(0, shaEc);
            baseSha = baseSha.Trim();

            wt = Path.Combine(Path.GetTempPath(), "vett-gitignore-e2e-" + Guid.NewGuid().ToString("N")[..8]);
            var (wtEc, _) = Git(ws, "worktree", "add", "-b", "e2e-" + Guid.NewGuid().ToString("N")[..8], wt, "HEAD");
            Assert.Equal(0, wtEc);

            // The work product — one file, exactly as a member would write it.
            File.WriteAllText(Path.Combine(wt, "Money.cs"),
                "namespace Domain;\npublic readonly record struct Money(decimal Amount);\n");

            // ...and a REAL build, which is what actually creates bin/ and obj/.
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = wt,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add("--nologo");
            using (var p = Process.Start(psi)!)
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(180_000);
                Assert.True(p.ExitCode == 0, "the fixture must actually build, or this test proves nothing");
            }

            // POSITIVE CONTROL — the build must really have produced artifacts.
            // Without this, "no bin/ in the diff" is satisfied by a build that
            // silently emitted nothing, and the test would pass on a dead rig.
            Assert.True(Directory.Exists(Path.Combine(wt, "bin")), "no bin/ was produced — nothing was ignored, so nothing was tested");
            Assert.True(Directory.Exists(Path.Combine(wt, "obj")), "no obj/ was produced — nothing was ignored, so nothing was tested");

            // Capture, exactly as DispatchWorktreeManager does it.
            var (addEc, _) = Git(wt, "add", "-A");
            Assert.Equal(0, addEc);
            var (statEc, stat) = Git(wt, "diff", "--cached", "--stat", baseSha);
            Assert.Equal(0, statEc);
            var (namesEc, names) = Git(wt, "diff", "--cached", "--name-only", baseSha);
            Assert.Equal(0, namesEc);

            var changed = names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // CONTROL: the real work is present.
            Assert.Contains("Money.cs", changed);

            // THE CLAIM: and nothing else is. Asserted on the whole set rather
            // than on a few known-bad substrings, so a build artifact at a path
            // I never anticipated still fails the test.
            Assert.Equal(new[] { "Money.cs" }, changed);
            Assert.DoesNotContain("bin/", stat);
            Assert.DoesNotContain("obj/", stat);
        }
        finally
        {
            if (wt is not null) { try { Git(ws, "worktree", "remove", "--force", wt); } catch { } }
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void The_NON_git_empty_template_is_untouched()
    {
        // The fix is scoped to the git template. `empty` has no repo, so there
        // is nothing to ignore and nothing to commit — dropping a stray
        // .gitignore there would be unexplained noise in a different fixture.
        var ws = WorkspaceSetup.Create("empty");
        try
        {
            Assert.False(File.Exists(Path.Combine(ws, ".gitignore")));
            Assert.False(Directory.Exists(Path.Combine(ws, ".git")));
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Seed_files_still_land_in_HEAD_alongside_the_gitignore()
    {
        // The seed path commits AFTER init. Regression guard: the .gitignore
        // commit must not swallow, block, or replace the seed commit — a
        // member checking out a worktree has to see both.
        var ws = WorkspaceSetup.Create("empty-git-repo", new Dictionary<string, string>
        {
            ["Domain.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>",
            ["src/Existing.cs"] = "namespace Domain; public sealed class Existing { }",
        });
        try
        {
            var (ec, tracked) = Git(ws, "ls-tree", "-r", "--name-only", "HEAD");
            Assert.Equal(0, ec);
            var files = tracked.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains(".gitignore", files);
            Assert.Contains("Domain.csproj", files);
            Assert.Contains("src/Existing.cs", files);

            var (stEc, status) = Git(ws, "status", "--porcelain");
            Assert.Equal(0, stEc);
            Assert.True(string.IsNullOrWhiteSpace(status), $"workspace should be clean at HEAD, got: {status}");
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }
}
