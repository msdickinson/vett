using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Vett.Runner;

/// <summary>Result of grading one candidate patch against an <see cref="EvalSpec"/>.</summary>
public sealed class EvalOutcome
{
    public string InstanceId { get; set; } = "";
    /// <summary>True iff every FAIL_TO_PASS and every PASS_TO_PASS test PASSED (and F2P is non-empty).</summary>
    public bool Resolved { get; set; }
    public int F2pPass { get; set; }
    public int F2pTotal { get; set; }
    public int P2pPass { get; set; }
    public int P2pTotal { get; set; }
    /// <summary>Did the candidate patch apply? Null when there was no candidate (gold/none check).</summary>
    public bool? PatchApplied { get; set; }
    /// <summary>Non-null on a mechanical failure (patch didn't apply, tests didn't run, container died).</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Runs an instance's own test command inside a fresh container off the pre-pulled
/// bench-daemon image and reports per-test PASS/FAIL. Uses the same Docker path as
/// the rest of VETT (shells `docker`, so it honors DOCKER_HOST — point it at the
/// bench daemon socket). No sidecar: grading only needs `docker exec`.
///
/// Trust model: the verdict is the test runner's OWN structured summary
/// (pytest `-rA` emits `PASSED &lt;nodeid&gt;` / `FAILED &lt;nodeid&gt;`), not a freeform-log
/// heuristic. Validate the whole pipeline per-instance with --prove-gold: the gold
/// patch MUST resolve; if it doesn't, the instance isn't gradable and the model
/// verdict is untrusted.
/// </summary>
public static class Evaluator
{
    // pytest -rA (and most xunit-style summaries) print one line per test:
    //   PASSED tests/foo.py::test_bar
    //   FAILED tests/foo.py::test_baz
    private static readonly Regex StatusLine =
        new(@"^(PASSED|FAILED|ERROR|XFAIL|XPASS)\s+(\S+)", RegexOptions.Multiline | RegexOptions.Compiled);

    public static async Task<EvalOutcome> EvaluateAsync(EvalSpec spec, string candidatePatch, CancellationToken ct)
    {
        var res = new EvalOutcome
        {
            InstanceId = spec.InstanceId,
            F2pTotal = spec.FailToPass.Count,
            P2pTotal = spec.PassToPass.Count,
        };

        var cid = (await Docker(["run", "-d", "--rm", "--entrypoint", "/bin/bash",
                                 "-e", "HOME=/tmp", spec.Image, "-c", "sleep infinity"], ct)).Trim();
        if (string.IsNullOrEmpty(cid))
        {
            res.Error = "container_start_failed";
            return res;
        }

        try
        {
            // Reset to a clean base tree (image is already at base_commit).
            await ExecCapture(cid, $"cd {spec.RepoDir} && git checkout -- . 2>/dev/null; git clean -fdq 2>/dev/null; true", ct);

            // Candidate solution first, then the gold tests.
            if (!string.IsNullOrWhiteSpace(candidatePatch))
            {
                var applied = await ApplyPatch(cid, spec.RepoDir, candidatePatch, "cand", ct);
                res.PatchApplied = applied;
                if (!applied) { res.Error = "model_patch_apply_failed"; return res; }
            }
            else
            {
                res.PatchApplied = true;
            }

            if (!await ApplyPatch(cid, spec.RepoDir, spec.TestPatch, "test", ct))
            {
                res.Error = "test_patch_apply_failed";
                return res;
            }

            var (log, _) = await ExecCapture(cid, $"cd {spec.RepoDir} && {spec.TestCmd}", ct, timeoutSec: 2400);

            var status = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in StatusLine.Matches(log))
                status[m.Groups[2].Value] = m.Groups[1].Value;

            res.F2pPass = spec.FailToPass.Count(n => status.GetValueOrDefault(n) == "PASSED");
            res.P2pPass = spec.PassToPass.Count(n => status.GetValueOrDefault(n) == "PASSED");
            res.Resolved = res.F2pTotal > 0
                           && res.F2pPass == res.F2pTotal
                           && res.P2pPass == res.P2pTotal;

            if (status.Count == 0)
                res.Error = "no_test_summary (collection/exec error)";
        }
        finally
        {
            try { await Docker(["kill", cid], CancellationToken.None); } catch { /* best-effort cleanup */ }
        }

        return res;
    }

    /// <summary>Write the patch into the container then try git-apply variants, then patch -p1.</summary>
    private static async Task<bool> ApplyPatch(string cid, string repoDir, string patch, string label, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patch)) return true;
        await ExecStdin(cid, $"cat > /tmp/{label}.patch", patch, ct);
        string[] tries =
        [
            $"cd {repoDir} && git apply --whitespace=nowarn /tmp/{label}.patch",
            $"cd {repoDir} && git apply --3way /tmp/{label}.patch",
            $"cd {repoDir} && patch -p1 -i /tmp/{label}.patch",
        ];
        foreach (var cmd in tries)
        {
            var (_, code) = await ExecCapture(cid, cmd, ct);
            if (code == 0) return true;
        }
        return false;
    }

    // --- docker plumbing (mirrors DockerSandbox; honors DOCKER_HOST) ---

    private static async Task<string> Docker(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("docker failed to start");
        var outp = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return outp;
    }

    /// <summary>Run a bash command in the container; return (stdout+stderr, exitCode).</summary>
    private static async Task<(string Output, int Code)> ExecCapture(string cid, string command, CancellationToken ct, int timeoutSec = 600)
    {
        var psi = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var a in new[] { "exec", cid, "bash", "-lc", command }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("docker exec failed to start");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var outTask = p.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var errTask = p.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { p.Kill(true); } catch { }
            return ("__timeout__", 124);
        }
        var stdout = await outTask;
        var stderr = await errTask;
        return (stdout + "\n" + stderr, p.ExitCode);
    }

    /// <summary>Run a bash command in the container feeding <paramref name="stdin"/> to it.</summary>
    private static async Task ExecStdin(string cid, string command, string stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var a in new[] { "exec", "-i", cid, "bash", "-c", command }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("docker exec -i failed to start");
        await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        _ = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
    }
}
