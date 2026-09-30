using System.Diagnostics;

namespace Vett.Bench.Team;

/// <summary>
/// Provisions clean workspaces for each benchmark instance. Each
/// invocation returns a fresh tempdir so concurrent runs don't trample
/// each other and so a failed instance leaves its workspace behind for
/// post-mortem (the harness only deletes on full pass).
///
/// Supported templates:
///   <list type="bullet">
///   <item><c>empty</c> — empty directory.</item>
///   <item><c>empty-git-repo</c> — directory with <c>git init</c> +
///   one empty initial commit. Required when the profile has
///   <c>team.dispatch_worktree=true</c> since worktrees need a
///   committed parent.</item>
///   </list>
/// </summary>
public static class WorkspaceSetup
{
    /// <summary>
    /// Adopt an existing workspace dir instead of creating a fresh one.
    /// Used by `vett team-bench --workspace-dir` for Q3 decomposition
    /// chains where successive sub-tasks build on the previous step's
    /// workspace state.
    ///
    /// The provided dir must already exist; seed files are NOT dropped
    /// (the caller is responsible for the workspace state). Returns the
    /// dir as-is. Caller signals "don't delete this on PASS" by passing
    /// the same path back to the harness with the adopt flag set.
    /// </summary>
    public static string Adopt(string existingDir)
    {
        if (string.IsNullOrWhiteSpace(existingDir))
            throw new ArgumentException("workspace dir cannot be empty", nameof(existingDir));
        if (!Directory.Exists(existingDir))
            throw new DirectoryNotFoundException($"--workspace-dir does not exist: {existingDir}");
        return existingDir;
    }

    public static string Create(string template, IReadOnlyDictionary<string, string>? seedFiles = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "vett-team-bench");
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, $"{template}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(workspace);

        var isGit = false;
        switch (template.ToLowerInvariant())
        {
            case "empty":
                break;
            case "empty-git-repo":
                Git(workspace, "init", "-q");
                // Configure local user so commit succeeds without
                // depending on global git config (CI runners often
                // don't have one).
                Git(workspace, "config", "user.email", "bench@vett.local");
                Git(workspace, "config", "user.name", "vett-bench");
                // ⛔ THE FIXTURE HAS TO BEHAVE LIKE A REAL REPO, OR IT
                // MEASURES ITSELF. Dispatch capture runs `git add -A`
                // (DispatchWorktreeManager.cs:499) and grades on the
                // resulting diff — "only the diff is evidence". Without a
                // .gitignore, every `dotnet build` inside a worktree put
                // bin/ and obj/ into that diff, so a one-file task was
                // captured as a ~20-file change carrying binary .dll and
                // .pdb blobs.
                //
                // MEASURED 2026-08-26 across three live manager-width runs,
                // on dispatch_end.diff_summary (NOT on the leads' prose —
                // that would be reading the narration, not the artifact):
                //     FLASH w20   52 of 71 non-empty diffs (73%)
                //     PRO   w10   51 of 82 (62%)
                //     FLASH w10   20 of 30 (67%)
                //   median files_changed = 20 for tasks writing ONE .cs file.
                // 19 of 47 nested sub-dispatches in the Flash w20 arm were
                // leads re-dispatching over artifacts in the diff, so the
                // fixture was inflating the very fan-out numbers that run
                // was measuring.
                //
                // Real .NET repos ship this ignore; the bench fixture did
                // not. Fixed HERE rather than in capture on purpose —
                // `git add -A` must keep honouring the target repo's own
                // ignore rules, and silently filtering paths at capture
                // time would hide real work in repos that DO track build
                // output. The fixture was wrong, not the capture.
                File.WriteAllText(
                    Path.Combine(workspace, ".gitignore"),
                    "# See WorkspaceSetup.cs — keeps build output out of the graded diff.\nbin/\nobj/\n");
                Git(workspace, "add", ".gitignore");
                Git(workspace, "commit", "--allow-empty", "-m", "init", "-q");
                isGit = true;
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown workspace template '{template}'. Supported: empty, empty-git-repo");
        }

        // Drop seed files. For git templates they need to be committed so
        // dispatch worktrees check them out — otherwise the implementer
        // would see an empty workspace.
        if (seedFiles is not null && seedFiles.Count > 0)
        {
            foreach (var (relPath, content) in seedFiles)
            {
                var full = Path.Combine(workspace, relPath);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(full, content);
            }
            if (isGit)
            {
                Git(workspace, "add", "-A");
                Git(workspace, "commit", "-m", "seed", "-q");
            }
        }

        return workspace;
    }

    private static void Git(string cwd, params string[] args)
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
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("git not on PATH (required for workspace template)");
        p.WaitForExit(15_000);
        if (p.ExitCode != 0)
        {
            var err = p.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err}");
        }
    }
}
