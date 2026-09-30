using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// The terminal tool used to report the timeout the caller ASKED FOR rather
/// than the one the sandbox ENFORCED.
///
/// The two numbers are not the same on the local path: BuiltinTools'
/// terminal tool defaults to 600s ("SWE-bench commands ... routinely exceed
/// 120s") while <see cref="DirectBash"/> clamps every request to 120s, and
/// every team-bench run goes through DirectBash (Bench/Team/Harness.cs
/// constructs one unconditionally — it never consults profile.Sandbox.Type).
/// So a command killed at 120s was announced to the model as "timed out
/// after 600 seconds", contradicting DirectBash's own 120s trailer in the
/// same message.
///
/// These tests pin the REPORT only. They do not assert any new ceiling and
/// they must not: the 120s value is the owner's to move, not this fix's.
/// </summary>
public class BashTimeoutReportTests
{
    // ---- The clamp itself. These pin the EXISTING ceiling so that a later
    // change to it is visible rather than silent. They are deliberately
    // insensitive to the reporting fix.

    [Fact]
    public void ClampTimeoutSec_CapsARequestAboveTheCeiling()
    {
        // The terminal tool's own default is 600. This is what happens to it.
        Assert.Equal(120, DirectBash.ClampTimeoutSec(600));
    }

    [Fact]
    public void ClampTimeoutSec_LeavesAnInRangeRequestAlone()
    {
        Assert.Equal(60, DirectBash.ClampTimeoutSec(60));
        Assert.Equal(120, DirectBash.ClampTimeoutSec(120));
    }

    [Fact]
    public void ClampTimeoutSec_FloorsNonPositiveRequestsAtOne()
    {
        Assert.Equal(1, DirectBash.ClampTimeoutSec(0));
        Assert.Equal(1, DirectBash.ClampTimeoutSec(-5));
    }

    // ---- The defect under repair.

    [Fact]
    public async Task TerminalTool_ReportsTheEnforcedTimeout_NotTheRequestedOne()
    {
        // Sandbox asked for 600 (the tool default), enforced 120.
        var sandbox = new TimingOutSandbox(enforcedTimeoutSec: 120);
        var result = await RunTerminal(sandbox, requestedTimeout: 600);

        Assert.Contains("120 seconds", result);
        Assert.DoesNotContain("600 seconds", result);
    }

    [Fact]
    public async Task TerminalTool_DoesNotContradictTheSandboxTrailer()
    {
        // DirectBash appends its own "[bash timed out after Ns...]" trailer.
        // Before the fix the headline said 600 and the trailer said 120 in
        // one message; whatever number the headline carries, the message must
        // now name exactly one deadline.
        var sandbox = new TimingOutSandbox(
            enforcedTimeoutSec: 120,
            stdout: "partial output\n[bash timed out after 120s, process killed]");
        var result = await RunTerminal(sandbox, requestedTimeout: 600);

        Assert.Contains("[bash timed out after 120s, process killed]", result);
        Assert.DoesNotContain("600", result);
    }

    // ---- Guarding the opposite over-correction. If the fix were written as
    // "always report DirectBash's ceiling", the sidecar path would start
    // under-reporting: the Go sidecar imposes NO upper bound (it only floors
    // a non-positive timeout at 60s), so there the requested number is the
    // true one. RpcClient leaves EffectiveTimeoutSec null to say so.

    [Fact]
    public async Task TerminalTool_ReportsTheRequestedTimeout_WhenTheSandboxAppliedNoClamp()
    {
        var sandbox = new TimingOutSandbox(enforcedTimeoutSec: null);
        var result = await RunTerminal(sandbox, requestedTimeout: 600);

        Assert.Contains("600 seconds", result);
    }

    // ---- Verdict invariance. The fix changes a number inside the message
    // and nothing else. Every consumer keys off the PREFIX
    // (AgentLoop's sawTimeout / softFailed / Observation.Success), so a
    // timeout must still read as a timeout in both shapes. If this ever goes
    // red, the fix has leaked into scoring and must be reverted.

    [Theory]
    [InlineData(120)]
    [InlineData(null)]
    public async Task TerminalTool_StillEmitsTheTimeoutPrefix(int? enforced)
    {
        var sandbox = new TimingOutSandbox(enforcedTimeoutSec: enforced);
        var result = await RunTerminal(sandbox, requestedTimeout: 600);

        Assert.StartsWith(Builtins.TimeoutPrefix, result);
    }

    [Fact]
    public async Task TerminalTool_LeavesNonTimeoutResultsUntouched()
    {
        var sandbox = new CompletingSandbox();
        var result = await RunTerminal(sandbox, requestedTimeout: 600);

        Assert.DoesNotContain(Builtins.TimeoutPrefix, result);
        Assert.Contains("Command finished with exit code 0", result);
    }

    private static Task<string> RunTerminal(ISandbox sandbox, int requestedTimeout)
    {
        var terminal = Builtins.All()["terminal"];
        var args = new Dictionary<string, object?>
        {
            ["command"] = "dotnet test",
            ["timeout"] = requestedTimeout,
        };
        return terminal(args, sandbox, "session", CancellationToken.None);
    }

    /// <summary>A sandbox whose bash always times out, reporting whatever
    /// deadline it claims to have enforced.</summary>
    private sealed class TimingOutSandbox(int? enforcedTimeoutSec, string stdout = "") : StubSandbox
    {
        public override Task<BashResult> BashExecAsync(
            string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult(stdout, -1, Cwd, true, enforcedTimeoutSec));
    }

    /// <summary>A sandbox whose bash succeeds — the control for the
    /// timeout-only tests.</summary>
    private sealed class CompletingSandbox : StubSandbox
    {
        public override Task<BashResult> BashExecAsync(
            string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("ok", 0, Cwd, false));
    }

    /// <summary>Nothing here shells out; only BashExecAsync matters.</summary>
    private abstract class StubSandbox : ISandbox
    {
        public string Cwd => "/workspace";
        public abstract Task<BashResult> BashExecAsync(
            string s, string cmd, int t = 60, CancellationToken ct = default);
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default) => Task.FromResult("");
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string n, CancellationToken ct = default) => Task.FromResult(("", (string?)null));
        public Task<string> FileInsertAsync(string s, string p, int l, string n, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("");
        public Task SessionCreateAsync(string n, string c, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string newCwd) => this;
        public ISandbox WithDispatchWorktree(string newCwd, string root) => this;
    }
}
