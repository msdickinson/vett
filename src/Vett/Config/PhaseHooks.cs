using System.Diagnostics;

namespace Vett.Config;

// Tier 1.10 scaffolding (v2-roadmap.md). Shell-command pre/post hooks
// per phase. Used for things like `git diff > snapshot.patch` after
// Implement, or environment setup before Plan. The contract is "run
// the command in workspaceRoot with a hard timeout, capture stdout +
// exit code, surface to the caller". Coordinator will own when to
// invoke this — Tier 1B work.

public sealed record PhaseHookResult(int ExitCode, string Stdout, string Stderr, TimeSpan Duration, bool TimedOut);

public static class PhaseHookRunner
{
    /// <summary>Default per-hook wall-clock cap. Hooks should be fast
    /// shell snippets; anything longer suggests it should be a phase
    /// of its own, not a hook.</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>How long to keep draining stdout/stderr AFTER the process has
    /// been killed, before giving up and reporting the output as unavailable.
    /// Bounds the one path that could previously exceed
    /// <see cref="DefaultTimeoutSeconds"/> without limit.</summary>
    public const int DrainGraceSeconds = 5;

    /// <summary>Written into Stdout/Stderr when the pipe could not be drained.
    /// Deliberately NOT the empty string: a hook that printed and a hook whose
    /// output we failed to collect must not read identically.</summary>
    public const string OutputUnavailableMarker =
        "(vett: hook output could not be collected — the pipe was still held open "
        + "after the process was killed)";

    public static async Task<PhaseHookResult> RunAsync(
        string command,
        string workspaceRoot,
        int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            ArgumentList = { OperatingSystem.IsWindows() ? "/c" : "-c", command },
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start hook process");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        bool timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { }
        }

        // A CAP THE ENFORCER CAN ITSELF BLOW PAST IS NOT A CAP.
        //
        // Kill(entireProcessTree: true) kills the tree it can SEE. A grandchild
        // that detached itself — `start`, a spawned service, anything outliving
        // cmd.exe — keeps the INHERITED write end of these pipes open, and
        // ReadToEndAsync then never completes. Awaiting the two reads bare (the
        // previous code) let RunAsync exceed its own timeout by an UNBOUNDED
        // amount: a method advertising a 60s cap could hang a run forever, and
        // no caller could tell, because the promise is in the signature and the
        // violation is in a pipe handle.
        //
        // NOT hypothetical in kind: on 2026-08-28 the timeout test failed at
        // 11.38s against a 2s cap — the kill+drain path really does run long
        // under load. That instance did complete; nothing bounded it if it had
        // not.
        //
        // So the drain gets its own bound, and output we could not collect is
        // reported as MISSING rather than as empty — an empty Stdout on a hook
        // that actually printed is the same could-not-measure-laundered-into-
        // measured-zero defect the harness fights everywhere else.
        var drain = Task.WhenAll(stdoutTask, stderrTask);
        await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(DrainGraceSeconds)));

        // Never re-await the reads: a still-blocked one would reintroduce the
        // unbounded wait, and a faulted one (a broken pipe after the kill) would
        // throw out of a method whose verdict — TimedOut, ExitCode — is already
        // known and correct. Read only what has actually landed.
        static string Collected(Task<string> t) =>
            t.IsCompletedSuccessfully ? t.Result : OutputUnavailableMarker;

        var stdout = Collected(stdoutTask);
        var stderr = Collected(stderrTask);
        return new PhaseHookResult(
            ExitCode: timedOut ? -1 : proc.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            Duration: sw.Elapsed,
            TimedOut: timedOut);
    }
}
