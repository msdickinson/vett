using System.Text.Json;
using Vett.Cli;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// Pin the plan-mode gate-based behavior. Plan mode now uses
/// <see cref="PlanModeUnlockGate"/>: each write tool surfaces an
/// "approve & switch" prompt on first attempt; on approve the gate
/// unlocks for the rest of the session AND emits
/// <c>chat_mode_changed:execute</c> so the UI flips Plan → Exec
/// without a respawn (no context loss). On reject the wrap returns
/// an error to the agent.
///
/// Read-only file_editor.view always passes through. Tools not in the
/// dispatch dict are silently skipped (e.g. update_memory may not be
/// registered when the profile didn't list it). Only the chat layer
/// passes <c>--mode plan</c>; <c>vett run</c> never invokes this.
/// </summary>
public class PlanModeHelperTests
{
    private static Dictionary<string, ToolFn> SeedTools() =>
        new Dictionary<string, ToolFn>
        {
            ["terminal"]         = (_, _, _, _) => Task.FromResult("terminal-ran"),
            ["bash_background"]  = (_, _, _, _) => Task.FromResult("bg-ran"),
            ["bash_kill"]        = (_, _, _, _) => Task.FromResult("kill-ran"),
            ["update_memory"]    = (_, _, _, _) => Task.FromResult("memory-ran"),
            ["file_editor"]      = (args, _, _, _) =>
            {
                var op = args.TryGetValue("command", out var v) ? v?.ToString() ?? "" : "";
                return Task.FromResult($"file_editor-ran:{op}");
            },
            ["think"]            = (_, _, _, _) => Task.FromResult("think-ran"),
        };

    private static (PlanModeUnlockGate gate, PlanModeUnlockService service, List<(string Type, Dictionary<string, object?> Data)> emitted) NewGate()
    {
        var emitted = new List<(string, Dictionary<string, object?>)>();
        var service = new PlanModeUnlockService();
        var gate = new PlanModeUnlockGate(service, (type, data) => emitted.Add((type, data)));
        return (gate, service, emitted);
    }

    [Fact]
    public async Task FileEditorView_PassesThroughWithoutPrompt()
    {
        var tools = SeedTools();
        var (gate, _, emitted) = NewGate();
        Helpers.ApplyPlanModeToolRestrictions(tools, gate);

        var result = await tools["file_editor"](
            new Dictionary<string, object?> { ["command"] = "view", ["path"] = "/x" },
            null!, "session", default);

        Assert.Equal("file_editor-ran:view", result);
        Assert.Empty(emitted); // no approval request emitted for view
    }

    [Fact]
    public async Task FileEditorWriteOps_PromptForApproval_AndDispatchOnApprove()
    {
        var tools = SeedTools();
        var (gate, service, emitted) = NewGate();
        Helpers.ApplyPlanModeToolRestrictions(tools, gate);

        // Approve the request as soon as it's emitted, mirroring the
        // chat UI's response flow.
        var callTask = tools["file_editor"](
            new Dictionary<string, object?> { ["command"] = "create", ["path"] = "/x" },
            null!, "session", default);

        // Loop briefly until the gate emits the request, then approve.
        var requestId = await WaitForRequestAsync(emitted);
        service.PostDecision(requestId, approve: true);

        var result = await callTask;
        Assert.Equal("file_editor-ran:create", result);
        Assert.True(gate.IsUnlocked);
        // After approve, gate also emits chat_mode_changed:execute.
        Assert.Contains(emitted, e => e.Type == "chat_mode_changed");
    }

    [Fact]
    public async Task FileEditorWriteOps_OnReject_ReturnsErrorAndStaysLocked()
    {
        var tools = SeedTools();
        var (gate, service, emitted) = NewGate();
        Helpers.ApplyPlanModeToolRestrictions(tools, gate);

        var callTask = tools["file_editor"](
            new Dictionary<string, object?> { ["command"] = "create", ["path"] = "/x" },
            null!, "session", default);
        var requestId = await WaitForRequestAsync(emitted);
        service.PostDecision(requestId, approve: false);

        var result = await callTask;
        Assert.Contains("rejected", result);
        Assert.Contains("file_editor", result);
        Assert.False(gate.IsUnlocked);
        // No chat_mode_changed event on reject.
        Assert.DoesNotContain(emitted, e => e.Type == "chat_mode_changed");
    }

    [Fact]
    public async Task GateUnlocks_SubsequentCallsBypassPrompt()
    {
        var tools = SeedTools();
        var (gate, service, emitted) = NewGate();
        Helpers.ApplyPlanModeToolRestrictions(tools, gate);

        // First call → approve.
        var first = tools["file_editor"](
            new Dictionary<string, object?> { ["command"] = "create", ["path"] = "/a" },
            null!, "session", default);
        service.PostDecision(await WaitForRequestAsync(emitted), approve: true);
        await first;

        var emittedBefore = emitted.Count;

        // Second call → no prompt, dispatches directly.
        var result = await tools["terminal"](
            new Dictionary<string, object?> { ["command"] = "rm -rf /tmp/x" },
            null!, "session", default);

        Assert.Equal("terminal-ran", result);
        Assert.Equal(emittedBefore, emitted.Count); // no new emission
    }

    [Fact]
    public async Task ChatModeChangedEvent_FiresOnceOnFirstUnlock()
    {
        var tools = SeedTools();
        var (gate, service, emitted) = NewGate();
        Helpers.ApplyPlanModeToolRestrictions(tools, gate);

        // Approve twice in a row — chat_mode_changed should fire only once.
        var first = tools["file_editor"](
            new Dictionary<string, object?> { ["command"] = "create", ["path"] = "/a" },
            null!, "session", default);
        service.PostDecision(await WaitForRequestAsync(emitted), approve: true);
        await first;

        var modeChangeCount = emitted.Count(e => e.Type == "chat_mode_changed");
        Assert.Equal(1, modeChangeCount);

        // Subsequent calls don't re-emit.
        await tools["terminal"](
            new Dictionary<string, object?> { ["command"] = "ls" },
            null!, "session", default);
        Assert.Equal(1, emitted.Count(e => e.Type == "chat_mode_changed"));
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("bash_background")]
    [InlineData("update_memory")]
    public async Task WriteToolsAreGated(string toolName)
    {
        var tools = SeedTools();
        var (gate, service, emitted) = NewGate();
        Helpers.ApplyPlanModeToolRestrictions(tools, gate);

        var callTask = tools[toolName](
            new Dictionary<string, object?> { ["command"] = "echo hi" },
            null!, "session", default);
        var requestId = await WaitForRequestAsync(emitted);
        service.PostDecision(requestId, approve: true);

        var result = await callTask;
        Assert.DoesNotContain("rejected", result);
    }

    [Fact]
    public async Task GateIsUnlocked_ShortCircuitsCheckAsync()
    {
        // Direct gate test: once IsUnlocked, CheckAsync returns true
        // immediately without emitting an approval request.
        var (gate, _, emitted) = NewGate();
        // Manually unlock by approving one call.
        var first = gate.CheckAsync("terminal", new Dictionary<string, object?>(), default);
        var rid = await WaitForRequestAsync(emitted);
        gate.Service.PostDecision(rid, approve: true);
        Assert.True(await first);
        Assert.True(gate.IsUnlocked);

        var emittedBefore = emitted.Count;
        var result = await gate.CheckAsync("terminal", new Dictionary<string, object?>(), default);
        Assert.True(result);
        Assert.Equal(emittedBefore, emitted.Count);
    }

    /// <summary>Spin briefly until the gate has emitted its
    ///  plan_mode_action_request, returning its request_id. Avoids a
    ///  race between Task starts and the channel receive loop.</summary>
    private static async Task<string> WaitForRequestAsync(
        List<(string Type, Dictionary<string, object?> Data)> emitted,
        int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var match = emitted.FirstOrDefault(e => e.Type == "plan_mode_action_request");
            if (match.Type is not null)
            {
                return match.Data.TryGetValue("request_id", out var v) ? v?.ToString() ?? "" : "";
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("Gate did not emit plan_mode_action_request within timeout.");
    }
}
