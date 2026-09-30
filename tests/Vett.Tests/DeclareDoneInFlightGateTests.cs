using Vett.Agent;
using Vett.Sandbox;

namespace Vett.Tests;

/// <summary>
/// In-flight gate on declare_done. EpicForge run 33 (2026-09-16): two of six
/// leads called `assign_async` twice and declared done on the next turn with
/// "Dispatched both tickets in parallel". The submit marker ended the run and
/// killed both implementers at iteration 1-2; the task returned
/// files_changed=0. Delegating is not finishing.
/// </summary>
[Collection("declare-done-env")]
public class DeclareDoneInFlightGateTests : IDisposable
{
    private readonly string? _savedPaths = Environment.GetEnvironmentVariable("VETT_REQUIRED_PATHS");
    private readonly string? _savedContent = Environment.GetEnvironmentVariable("VETT_REQUIRED_CONTENT");
    private readonly string? _savedVerify = Environment.GetEnvironmentVariable("VETT_VERIFY_CMD");

    public DeclareDoneInFlightGateTests()
    {
        Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", null);
        Environment.SetEnvironmentVariable("VETT_REQUIRED_CONTENT", null);
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", _savedPaths);
        Environment.SetEnvironmentVariable("VETT_REQUIRED_CONTENT", _savedContent);
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", _savedVerify);
    }

    private sealed class NullSandbox : ISandbox
    {
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("", 0, "/fake", false));
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileInsertAsync(string s, string p, int l, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SessionCreateAsync(string n, string cwd, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string cwd) => this;
        public ISandbox WithDispatchWorktree(string newCwd, string root) => this;
    }

    private static async Task WaitUntilAsync(Func<bool> cond, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(cond(), "condition not met in time");
    }

    [Fact]
    public async Task DeclareDone_RefusesWhileAsyncDispatchInFlight_ThenAllowsOnceItSettles()
    {
        var board = new TaskBoard { MaxConcurrentDispatches = 6 };
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = LeaderTools.Create(board, (_, _, _, _) => gate.Task);
        var sandbox = new NullSandbox();

        var a = await tools["assign_async"](
            new Dictionary<string, object?> { ["member"] = "implementer-1", ["task"] = "fix it" },
            sandbox, "s", default);
        Assert.DoesNotContain("Error", a);
        await WaitUntilAsync(() => board.InFlight() == 1);

        var refused = await tools["declare_done"](
            new Dictionary<string, object?> { ["summary"] = "Dispatched both tickets in parallel." },
            sandbox, "s", default);
        Assert.StartsWith("Error: cannot declare done", refused);
        Assert.Contains("1 dispatch(es) are still in flight", refused);
        Assert.Contains("implementer-1", refused);
        Assert.Contains("wait_task", refused);
        Assert.Contains("refusal 1/10", refused);
        Assert.DoesNotContain(Vett.Tools.Builtins.SubmitMarker, refused);
        Assert.Equal(1, board.InFlight()); // the refusal did not touch the dispatch

        gate.SetResult("done: wrote the fix");
        await WaitUntilAsync(() => board.InFlight() == 0);

        var ok = await tools["declare_done"](
            new Dictionary<string, object?> { ["summary"] = "Reviewed and landed." },
            sandbox, "s", default);
        Assert.StartsWith(Vett.Tools.Builtins.SubmitMarker, ok);
        Assert.DoesNotContain("gate-failed-open", ok);
    }

    [Fact]
    public async Task DeclareDone_WithNothingInFlight_IsUnchanged()
    {
        var tools = LeaderTools.Create(new TaskBoard(), (_, _, _, _) => Task.FromResult(""));
        var r = await tools["declare_done"](
            new Dictionary<string, object?> { ["summary"] = "Done." }, new NullSandbox(), "s", default);
        Assert.Equal($"{Vett.Tools.Builtins.SubmitMarker}Done.", r);
    }

    [Fact]
    public async Task DeclareDone_InFlightRefusalBudgetRelents_ButTagsTheSubmission()
    {
        var board = new TaskBoard { MaxConcurrentDispatches = 6 };
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = LeaderTools.Create(board, (_, _, _, _) => gate.Task);
        var sandbox = new NullSandbox();
        await tools["assign_async"](
            new Dictionary<string, object?> { ["member"] = "implementer-1", ["task"] = "fix it" },
            sandbox, "s", default);
        await WaitUntilAsync(() => board.InFlight() == 1);
        var args = new Dictionary<string, object?> { ["summary"] = "Done." };

        for (var i = 1; i <= 10; i++)
        {
            var r = await tools["declare_done"](args, sandbox, "s", default);
            Assert.StartsWith("Error: cannot declare done", r);
            Assert.Contains($"refusal {i}/10", r);
        }
        var final = await tools["declare_done"](args, sandbox, "s", default);
        Assert.StartsWith(Vett.Tools.Builtins.SubmitMarker, final);
        Assert.Contains("gate-failed-open", final);
        Assert.Contains("still in flight", final);
        gate.SetResult("");
    }
}
