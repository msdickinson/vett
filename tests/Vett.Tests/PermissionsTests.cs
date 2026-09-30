using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// Covers the permission classifier (pure) + the gate's auto/ask/deny
/// behavior. The async ask path is exercised against the real
/// PermissionService channel.
/// </summary>
public class PermissionClassifierTests
{
    [Fact]
    public void Classify_TerminalWithSafeCommand_ReturnsTerminalSafe()
    {
        var args = new Dictionary<string, object?> { ["command"] = "ls -la" };
        Assert.Equal(PermissionKind.TerminalSafe, PermissionClassifier.Classify("terminal", args));
    }

    [Fact]
    public void Classify_TerminalWithUnsafeCommand_ReturnsTerminalUnsafe()
    {
        var args = new Dictionary<string, object?> { ["command"] = "rm -rf /tmp/foo" };
        Assert.Equal(PermissionKind.TerminalUnsafe, PermissionClassifier.Classify("terminal", args));
    }

    [Fact]
    public void Classify_FileEditorView_ReturnsRead()
    {
        var args = new Dictionary<string, object?> { ["command"] = "view", ["path"] = "/foo" };
        Assert.Equal(PermissionKind.Read, PermissionClassifier.Classify("file_editor", args));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("str_replace")]
    [InlineData("insert")]
    [InlineData("undo_edit")]
    public void Classify_FileEditorWriteOps_ReturnsEdit(string op)
    {
        var args = new Dictionary<string, object?> { ["command"] = op, ["path"] = "/foo" };
        Assert.Equal(PermissionKind.Edit, PermissionClassifier.Classify("file_editor", args));
    }

    [Fact]
    public void Classify_UpdateMemory_ReturnsEdit()
    {
        Assert.Equal(PermissionKind.Edit,
            PermissionClassifier.Classify("update_memory", new Dictionary<string, object?>()));
    }

    [Theory]
    [InlineData("think")]
    [InlineData("finish")]
    [InlineData("task_tracker")]
    [InlineData("ask_user_question")]
    [InlineData("assign_task")]
    public void Classify_ControlPlaneTools_ReturnOther(string tool)
    {
        Assert.Equal(PermissionKind.Other,
            PermissionClassifier.Classify(tool, new Dictionary<string, object?>()));
    }

    [Fact]
    public void Classify_McpPrefixedTool_ReturnsMcp()
    {
        Assert.Equal(PermissionKind.Mcp,
            PermissionClassifier.Classify("mcp_filesystem_read", new Dictionary<string, object?>()));
    }

    [Fact]
    public void Classify_BashBackgroundWithSafeCommand_ReturnsTerminalSafe()
    {
        var args = new Dictionary<string, object?> { ["command"] = "git status" };
        Assert.Equal(PermissionKind.TerminalSafe,
            PermissionClassifier.Classify("bash_background", args));
    }

    [Fact]
    public void Classify_BashBackgroundWithUnsafeCommand_ReturnsTerminalUnsafe()
    {
        var args = new Dictionary<string, object?> { ["command"] = "npm run dev" };
        Assert.Equal(PermissionKind.TerminalUnsafe,
            PermissionClassifier.Classify("bash_background", args));
    }

    [Fact]
    public void Classify_MonitorAndBashJobs_ReturnRead()
    {
        var args = new Dictionary<string, object?> { ["job_id"] = "bg-12345678" };
        Assert.Equal(PermissionKind.Read, PermissionClassifier.Classify("monitor", args));
        Assert.Equal(PermissionKind.Read, PermissionClassifier.Classify("bash_jobs", new Dictionary<string, object?>()));
    }

    [Fact]
    public void Classify_BashKill_ReturnsOther()
    {
        // Killing a previously-allowed background job — the user already
        // consented to the work, ending it doesn't need a fresh prompt.
        Assert.Equal(PermissionKind.Other,
            PermissionClassifier.Classify("bash_kill", new Dictionary<string, object?>()));
    }

    [Theory]
    [InlineData("ls")]
    [InlineData("ls -la")]
    [InlineData("cat README.md")]
    [InlineData("git status")]
    [InlineData("git log --oneline")]
    [InlineData("git diff HEAD~1")]
    [InlineData("pwd")]
    [InlineData("which python")]
    [InlineData("npm list --depth=0")]
    [InlineData("python --version")]
    [InlineData("dotnet --info")]
    [InlineData("docker ps")]
    [InlineData("rg 'foo'")]
    [InlineData("find . -name '*.cs'")]
    [InlineData("grep -r foo src/")]
    public void IsSafeTerminalCommand_AllowsKnownReadOnlyOperations(string cmd)
    {
        Assert.True(PermissionClassifier.IsSafeTerminalCommand(cmd), $"Expected safe: {cmd}");
    }

    [Theory]
    [InlineData("rm -rf /tmp")]
    [InlineData("git push origin main")]
    [InlineData("git checkout -b feature")]
    [InlineData("git reset --hard HEAD")]
    [InlineData("npm install")]
    [InlineData("npm run build")]
    [InlineData("python script.py")]
    [InlineData("dotnet build")]
    [InlineData("cargo build")]
    [InlineData("docker run busybox")]
    [InlineData("curl https://example.com")]
    [InlineData("wget foo")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSafeTerminalCommand_BlocksMutatingOrUnknownCommands(string cmd)
    {
        Assert.False(PermissionClassifier.IsSafeTerminalCommand(cmd), $"Expected unsafe: {cmd}");
    }

    [Theory]
    [InlineData("ls; rm -rf /")]      // command chaining
    [InlineData("ls && rm foo")]      // conditional chain
    [InlineData("ls | xargs rm")]     // pipe to mutator
    [InlineData("cat foo > bar")]     // redirect
    [InlineData("cat < foo")]         // input redirect
    [InlineData("ls `which rm`")]     // backtick substitution
    [InlineData("ls $(which rm)")]    // command substitution
    [InlineData("ls &")]              // background
    public void IsSafeTerminalCommand_BlocksShellMetacharacters(string cmd)
    {
        Assert.False(PermissionClassifier.IsSafeTerminalCommand(cmd), $"Compound expected unsafe: {cmd}");
    }

    [Theory]
    [InlineData("find . -exec rm {} ;")]
    [InlineData("find . -delete")]
    [InlineData("grep -r foo . -exec cat {} \\;")]
    public void IsSafeTerminalCommand_BlocksFindGrepWithExecOrDelete(string cmd)
    {
        Assert.False(PermissionClassifier.IsSafeTerminalCommand(cmd), $"-exec/-delete expected unsafe: {cmd}");
    }

    [Theory]
    [InlineData("cat foo.txt")]
    [InlineData("cat README.md")]
    [InlineData("head -n 50 build.log")]
    [InlineData("tail -f service.log")]
    [InlineData("less src/main.cs")]
    [InlineData("more notes.md")]
    public void Classify_TerminalFileDump_ReturnsRead(string cmd)
    {
        // File-content dumpers route to Read so a "Read: ask" rule
        // covers "any tool call that exposes file contents to the
        // model" — file_editor view AND shell cat/head/tail/etc.
        var args = new Dictionary<string, object?> { ["command"] = cmd };
        Assert.Equal(PermissionKind.Read, PermissionClassifier.Classify("terminal", args));
        Assert.Equal(PermissionKind.Read, PermissionClassifier.Classify("bash_background", args));
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("pwd")]
    [InlineData("git status")]
    [InlineData("git log --oneline")]
    [InlineData("npm list --depth=0")]
    [InlineData("docker ps")]
    [InlineData("which python")]
    public void Classify_TerminalNonDumpSafeCommands_StayTerminalSafe(string cmd)
    {
        // Read state, not file bodies — these stay as TerminalSafe
        // so flipping Read to ask doesn't paper over every benign
        // shell call.
        var args = new Dictionary<string, object?> { ["command"] = cmd };
        Assert.Equal(PermissionKind.TerminalSafe, PermissionClassifier.Classify("terminal", args));
    }

    [Theory]
    [InlineData("cat foo > bar")]    // metachar — not safe → unsafe regardless of head
    [InlineData("cat foo | grep x")] // pipe — same
    public void Classify_TerminalFileDumpWithMetachars_ReturnsTerminalUnsafe(string cmd)
    {
        // Even though the head token is a file dumper, the metachar
        // gate runs first and unsafe wins. Otherwise a redirect of
        // `cat foo > evil` would slip through under Read-only gating.
        var args = new Dictionary<string, object?> { ["command"] = cmd };
        Assert.Equal(PermissionKind.TerminalUnsafe, PermissionClassifier.Classify("terminal", args));
    }
}

public class PermissionRulesTests
{
    [Theory]
    [InlineData("auto", PermissionRule.Auto)]
    [InlineData("ALLOW", PermissionRule.Auto)]
    [InlineData("yes", PermissionRule.Auto)]
    [InlineData("ask", PermissionRule.Ask)]
    [InlineData("PROMPT", PermissionRule.Ask)]
    [InlineData("deny", PermissionRule.Deny)]
    [InlineData("Block", PermissionRule.Deny)]
    [InlineData("no", PermissionRule.Deny)]
    public void Parse_RecognizedTokens_ReturnsRightRule(string raw, PermissionRule expected)
    {
        Assert.Equal(expected, Permissions.Parse(raw, PermissionRule.Ask));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    public void Parse_UnknownTokens_FallsBackToProvidedDefault(string? raw)
    {
        Assert.Equal(PermissionRule.Ask, Permissions.Parse(raw, PermissionRule.Ask));
        Assert.Equal(PermissionRule.Auto, Permissions.Parse(raw, PermissionRule.Auto));
    }

    [Fact]
    public void For_ReturnsCorrectRulePerKind()
    {
        var p = new Permissions
        {
            Read = PermissionRule.Auto,
            Edit = PermissionRule.Ask,
            TerminalSafe = PermissionRule.Auto,
            TerminalUnsafe = PermissionRule.Deny,
            Mcp = PermissionRule.Ask,
            Other = PermissionRule.Auto,
        };
        Assert.Equal(PermissionRule.Auto, p.For(PermissionKind.Read));
        Assert.Equal(PermissionRule.Ask, p.For(PermissionKind.Edit));
        Assert.Equal(PermissionRule.Auto, p.For(PermissionKind.TerminalSafe));
        Assert.Equal(PermissionRule.Deny, p.For(PermissionKind.TerminalUnsafe));
        Assert.Equal(PermissionRule.Ask, p.For(PermissionKind.Mcp));
        Assert.Equal(PermissionRule.Auto, p.For(PermissionKind.Other));
    }

    [Fact]
    public void Set_FlipsRuleForKind()
    {
        var p = new Permissions();
        p.Set(PermissionKind.Edit, PermissionRule.Auto);
        Assert.Equal(PermissionRule.Auto, p.Edit);
    }
}

public class PermissionGateTests
{
    [Fact]
    public async Task CheckAsync_AutoRule_ReturnsAutoWithoutEmittingEvent()
    {
        var rules = new Permissions { Edit = PermissionRule.Auto };
        var service = new PermissionService();
        var emitted = new List<string>();
        var gate = new PermissionGate(rules, service, (type, _) => emitted.Add(type));

        var result = await gate.CheckAsync(
            "file_editor",
            new Dictionary<string, object?> { ["command"] = "create", ["path"] = "/x" },
            default);

        Assert.Equal(PermissionRule.Auto, result);
        Assert.Empty(emitted);
    }

    [Fact]
    public async Task CheckAsync_DenyRule_ReturnsDenyWithoutEmittingEvent()
    {
        var rules = new Permissions { Edit = PermissionRule.Deny };
        var service = new PermissionService();
        var emitted = new List<string>();
        var gate = new PermissionGate(rules, service, (type, _) => emitted.Add(type));

        var result = await gate.CheckAsync(
            "file_editor",
            new Dictionary<string, object?> { ["command"] = "create", ["path"] = "/x" },
            default);

        Assert.Equal(PermissionRule.Deny, result);
        Assert.Empty(emitted);
    }

    [Fact]
    public async Task CheckAsync_AskRule_EmitsRequestAndAwaitsResponse()
    {
        var rules = new Permissions { Edit = PermissionRule.Ask };
        var service = new PermissionService();
        var emitted = new List<(string Type, Dictionary<string, object?> Data)>();
        var gate = new PermissionGate(rules, service, (type, data) => emitted.Add((type, data)));

        var checkTask = gate.CheckAsync(
            "file_editor",
            new Dictionary<string, object?> { ["command"] = "create", ["path"] = "/x" },
            default);

        // Wait for the gate to emit + park. Today `_emit` runs before
        // CheckAsync's first suspension point, so this is already true on
        // the line above — but that is a property of the current code, not
        // of the test's subject. A condition-wait stays correct if an await
        // is ever introduced ahead of the emit; the `await Task.Delay(20)`
        // this replaces would have started failing under load instead.
        await TestWait.UntilCountAsync(emitted, 1, "the gate to emit permission_request");
        Assert.Single(emitted);
        Assert.Equal("permission_request", emitted[0].Type);
        var requestId = (string)emitted[0].Data["request_id"]!;
        Assert.False(string.IsNullOrEmpty(requestId));

        // Simulate the user clicking Allow.
        service.PostDecision(requestId, new PermissionResponse(PermissionRule.Auto, false));

        var result = await checkTask;
        Assert.Equal(PermissionRule.Auto, result);
        // Single-call rule shouldn't have flipped the per-session rule.
        Assert.Equal(PermissionRule.Ask, rules.Edit);
    }

    [Fact]
    public async Task CheckAsync_AskWithRememberForKind_FlipsRuleForSession()
    {
        // 2026-08-27: this test used to build a FIRST gate with a no-op emit,
        // sleep 20ms, discover it could not recover the request id from it,
        // and then build a second gate that does the actual work. The first
        // gate asserted nothing, and its "cleanup" posted a decision under an
        // id no waiter held, then swallowed EVERY exception from a 50ms
        // WaitAsync — including a real failure. It also left a Task parked
        // forever on a TaskCompletionSource that nothing would ever complete.
        // Deleting it removes dead weight and two sleeps; it removes no
        // assertion, because it made none.
        var emitted = new List<(string Type, Dictionary<string, object?> Data)>();
        var rules = new Permissions { Edit = PermissionRule.Ask };
        var service = new PermissionService();
        var gate = new PermissionGate(rules, service, (t, d) => emitted.Add((t, d)));

        var checkTask = gate.CheckAsync(
            "file_editor",
            new Dictionary<string, object?> { ["command"] = "create", ["path"] = "/x" },
            default);

        await TestWait.UntilCountAsync(emitted, 1, "the gate to emit permission_request");
        var requestId = (string)emitted[0].Data["request_id"]!;
        service.PostDecision(requestId, new PermissionResponse(PermissionRule.Auto, true));
        var result = await checkTask;

        Assert.Equal(PermissionRule.Auto, result);
        Assert.Equal(PermissionRule.Auto, rules.Edit); // flipped for the session
    }

    [Fact]
    public async Task CheckAsync_CancellationDuringAsk_ReturnsDeny()
    {
        var emitted = new List<(string Type, Dictionary<string, object?> Data)>();
        var rules = new Permissions { Edit = PermissionRule.Ask };
        var service = new PermissionService();
        var gate = new PermissionGate(rules, service, (t, d) => emitted.Add((t, d)));

        using var cts = new CancellationTokenSource();
        var checkTask = gate.CheckAsync(
            "file_editor",
            new Dictionary<string, object?> { ["command"] = "create" },
            cts.Token);

        // Cancel only once the gate is genuinely parked on the ask. The emit
        // is the observable proof it got that far — the previous
        // `await Task.Delay(20)` was a guess at the same fact, and the gate
        // was given a no-op emit so there was nothing to check against.
        await TestWait.UntilCountAsync(emitted, 1, "the gate to park on the ask (emit observed)");
        cts.Cancel();

        var result = await checkTask;
        // Cancellation during ask is treated as a deny so the agent
        // gets a synthesized error rather than hanging.
        Assert.Equal(PermissionRule.Deny, result);
    }
}
