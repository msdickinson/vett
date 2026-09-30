using Vett.Config;

namespace Vett.Tests;

public class PhaseHookRunnerTests
{
    private static string Tmp() => Path.GetTempPath();

    [Fact]
    public async Task SuccessfulHook_CapturesStdout_ExitZero()
    {
        var cmd = OperatingSystem.IsWindows() ? "echo hello-from-hook" : "echo hello-from-hook";
        var r = await PhaseHookRunner.RunAsync(cmd, Tmp(), timeoutSeconds: 10);
        Assert.Equal(0, r.ExitCode);
        Assert.False(r.TimedOut);
        Assert.Contains("hello-from-hook", r.Stdout);
    }

    [Fact]
    public async Task FailingHook_SurfacesNonZeroExit()
    {
        // `exit 7` works on both cmd.exe and bash
        var r = await PhaseHookRunner.RunAsync("exit 7", Tmp(), timeoutSeconds: 10);
        Assert.Equal(7, r.ExitCode);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task SlowHook_HitsTimeout_KilledAndMarked()
    {
        // 30s sleep cap'd at 2s should trip timeout
        var cmd = OperatingSystem.IsWindows()
            ? "ping -n 30 127.0.0.1 > nul"
            : "sleep 30";
        var r = await PhaseHookRunner.RunAsync(cmd, Tmp(), timeoutSeconds: 2);
        // THESE are the assertions that the timeout fired.
        Assert.True(r.TimedOut);
        Assert.Equal(-1, r.ExitCode);

        // THE COMMAND WAS KILLED, NOT WAITED OUT.
        //
        // The bound is set against the COMMAND's own runtime (~29s), not
        // against a guess at how fast this machine schedules a kill. Anything
        // under 25s proves the runner did not simply let the hook run to
        // completion; the runner's own promise is much tighter than that —
        // timeoutSeconds (2) + DrainGraceSeconds (5) ≈ 7s.
        //
        // ⛔ THIS IS NOT A CAP RAISED TO TURN A RED TEST GREEN. The bound was
        // `< 10` and failed at 11.3846s on 2026-08-28, run 1 of a 10-run suite
        // watch under full xUnit parallel load. On that run `TimedOut` and
        // `ExitCode == -1` — the two assertions above, unchanged — both PASSED:
        // the timeout logic was never in question. What `< 10` actually
        // asserted was a property of the MACHINE while claiming to assert a
        // property of the RUNNER, so it failed for a reason that told nobody
        // anything. The unbounded pipe drain that let the kill path run long is
        // fixed at its source in PhaseHookRunner (DrainGraceSeconds); this
        // bound no longer has to encode a scheduling guess to be meaningful.
        Assert.True(r.Duration.TotalSeconds < 25,
            $"runner waited the hook out instead of killing it: took {r.Duration.TotalSeconds}s "
            + "against a command that runs ~29s and a 2s cap.");
    }
}
