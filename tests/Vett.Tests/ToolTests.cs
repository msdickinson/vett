using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

public class FakeSandbox : ISandbox
{
    public string Cwd { get; set; } = "/testbed";
    public BashResult BashResult { get; set; } = new("hello\n", 0, "/testbed", false);
    public string FileViewResult { get; set; } = "file content";
    public string FileCreateResult { get; set; } = "File created successfully";
    /// <summary>Law 252: DirectBash THROWS on an existing path; set this to model it.</summary>
    public Exception? FileCreateException { get; set; }
    /// <summary>Law 255: when set, models a file system -- `create` throws
    /// file_exists on a present path and records a created one; a test
    /// removes a path to model the seat's `rm`.</summary>
    public HashSet<string>? Files { get; set; }
    public (string, string?) StrReplaceResult { get; set; } = ("has been edited", null);
    public string InsertResult { get; set; } = "has been edited";
    public string UndoResult { get; set; } = "undone successfully";
    public List<(string Cmd, int Timeout)> BashCalls { get; } = [];

    public Task<BashResult> BashExecAsync(string sessionId, string command, int timeoutSec = 60, CancellationToken ct = default)
    {
        BashCalls.Add((command, timeoutSec));
        return Task.FromResult(BashResult);
    }

    public Task<string> FileViewAsync(string sessionId, string path, CancellationToken ct = default)
        => Task.FromResult(FileViewResult);

    public Task<string> FileCreateAsync(string sessionId, string path, string fileText, CancellationToken ct = default)
    {
        if (FileCreateException is not null) return Task.FromException<string>(FileCreateException);
        if (Files is not null && !Files.Add(path))
            return Task.FromException<string>(new InvalidOperationException($"file_exists: {path}"));
        return Task.FromResult(FileCreateResult);
    }

    public Task<(string Content, string? Error)> FileStrReplaceAsync(string sessionId, string path, string oldStr, string newStr, CancellationToken ct = default)
        => Task.FromResult(StrReplaceResult);

    public Task<string> FileInsertAsync(string sessionId, string path, int insertLine, string newStr, CancellationToken ct = default)
        => Task.FromResult(InsertResult);

    public Task<string> FileUndoAsync(string sessionId, string path, CancellationToken ct = default)
        => Task.FromResult(UndoResult);

    public Task SessionCreateAsync(string name, string cwd, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SessionDestroyAsync(string name, CancellationToken ct = default)
        => Task.CompletedTask;

    // Tool-level tests don't exercise dispatch isolation; the cwd-rooted
    // and dispatch-bound variants throw so any test that grows into that
    // territory fails loudly instead of silently using the same FakeSandbox.
    public ISandbox WithCwd(string newCwd)
        => throw new NotSupportedException("FakeSandbox does not implement WithCwd; use DirectBash for dispatch tests.");
    public ISandbox WithDispatchWorktree(string newCwd, string workspaceRoot)
        => throw new NotSupportedException("FakeSandbox does not implement WithDispatchWorktree; use DirectBash for dispatch tests.");
}

public class ToolTests
{
    private readonly FakeSandbox _sb = new();
    private readonly Dictionary<string, ToolFn> _tools = Builtins.All();

    private Task<string> Call(string toolName, Dictionary<string, object?> args)
        => _tools[toolName](args, _sb, "test", default);

    [Fact]
    public async Task Terminal_SuccessEnvelope()
    {
        var result = await Call("terminal", new() { ["command"] = "echo hello" });

        // Envelope mirrors openhands-tools/terminal/definition.py
        // TerminalObservation.to_llm_content: stdout, then [Current working
        // directory: ...], then [Command finished with exit code N].
        Assert.Contains("hello", result);
        Assert.Contains("[Command finished with exit code 0]", result);
        Assert.Contains("[Current working directory: /testbed]", result);
        Assert.Single(_sb.BashCalls);
    }

    [Fact]
    public async Task Terminal_TimeoutEnvelope()
    {
        _sb.BashResult = new BashResult("partial", -1, "/testbed", true);

        var result = await Call("terminal", new() { ["command"] = "sleep 100", ["timeout"] = 2 });

        Assert.StartsWith(Builtins.TimeoutPrefix, result);
        Assert.Contains("partial", result);
    }

    [Fact]
    public async Task Terminal_MissingCommand_ReturnsError()
    {
        var result = await Call("terminal", new());

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task Finish_EmitsSubmitMarker()
    {
        var result = await Call("finish", new() { ["message"] = "all tests pass" });

        Assert.StartsWith(Builtins.SubmitMarker, result);
        Assert.Contains("all tests pass", result);
    }

    [Fact]
    public async Task Think_ReturnsAck()
    {
        var result = await Call("think", new() { ["thought"] = "I think" });

        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task FileEditor_Create()
    {
        var result = await Call("file_editor", new() { ["command_name"] = "create", ["path"] = "/test.py", ["file_text"] = "hello\n" });

        Assert.Contains("File created", result);
    }

    [Fact]
    public async Task FileEditor_StrReplace_ErrorPassesThrough()
    {
        _sb.StrReplaceResult = ("", "did not appear verbatim");

        var result = await Call("file_editor", new() { ["command_name"] = "str_replace", ["path"] = "/test.py", ["old_str"] = "x", ["new_str"] = "y" });

        Assert.Contains("did not appear verbatim", result);
    }

    /// <summary>2026-09-09 (s12a it.19): DirectBash answers a missed anchor
    /// with `str_replace_no_match: ...` and no `Error:` prefix, so the loop's
    /// soft-fail marker read it as success and a loop salvage reported
    /// `landed:true` over an unchanged file. A refusal is spelled as one.</summary>
    [Fact]
    public async Task FileEditor_StrReplace_RefusalIsAnError()
    {
        _sb.StrReplaceResult = ("", "str_replace_no_match: not found in /test.py. Do not retype the anchor from memory");

        var result = await Call("file_editor", new() { ["command_name"] = "str_replace", ["path"] = "/test.py", ["old_str"] = "x", ["new_str"] = "y" });

        Assert.StartsWith("Error: str_replace_no_match", result);
    }

    /// <summary>2026-09-09 (s14a it.19-26, batch 14, VETT c0a0f4b): after the
    /// no-op str_replace was refused, the seat followed the file_exists hint
    /// (rm, then create) and re-created facts-01.js with the same 1,401 bytes,
    /// three replies per cycle, invisible to the identical-call breaker. The
    /// first identical re-create is warned on, the second refused, and a
    /// different text is accepted again.</summary>
    [Fact]
    public async Task FileEditor_Create_SameBytesAgain_WarnsThenRefuses()
    {
        _sb.FileCreateResult = "File created successfully";
        const string same = "export const FACTS_01 = [\n  \"one\",\n];\n";
        Dictionary<string, object?> Args(string text) => new() { ["command_name"] = "create", ["path"] = "/law243/facts-01.js", ["file_text"] = text };

        var first = await Call("file_editor", Args(same));
        var second = await Call("file_editor", Args(same));
        var third = await Call("file_editor", Args(same));
        var different = await Call("file_editor", Args(same + "// more\n"));

        Assert.Equal("File created successfully", first);
        Assert.StartsWith("Warning: create_repeats_last_create", second);
        Assert.EndsWith("File created successfully", second);
        Assert.StartsWith("Error: create_repeats_last_create", third);
        Assert.Equal("File created successfully", different);
    }

    /// <summary>Law 255 (batch 22, VETT 455f523: s22a it.24-28, s22b it.19-20/41,
    /// s22d it.19-21, s22h it.27-32): every law-243 refusal landed on a path the
    /// seat had JUST `rm`'d, so the refusal left the file GONE -- s22b and s22h
    /// ended their hour with src/facts/ empty. A refusal that fires when the
    /// path is absent restores the banked bytes and says so; when the path is
    /// present it stays the plain refusal.</summary>
    [Fact]
    public async Task FileEditor_Create_ThirdIdenticalOnAnAbsentPath_RestoresTheBytes()
    {
        _sb.Files = new HashSet<string>();
        _sb.FileCreateResult = "1\tone\n2\ttwo\n3\tthree";
        const string same = "one\ntwo\nthree";
        const string path = "/law255/facts-01.js";
        Dictionary<string, object?> Args(string text) => new() { ["command_name"] = "create", ["path"] = path, ["file_text"] = text };

        var first = await Call("file_editor", Args(same));
        _sb.Files.Remove(path);                                   // the seat's `rm`
        var second = await Call("file_editor", Args(same));
        _sb.Files.Remove(path);                                   // `rm` again
        var third = await Call("file_editor", Args(same));

        Assert.StartsWith("1\tone", first);
        Assert.StartsWith("Warning: create_repeats_last_create", second);
        Assert.StartsWith("Error: create_repeats_last_create", third);
        Assert.Contains("RESTORED:", third);
        Assert.Contains("`insert_line` = 2", third);
        Assert.Contains(path, _sb.Files);                         // the bytes are back on disk

        var fourth = await Call("file_editor", Args(same));       // present now: plain refusal, nothing rewritten
        Assert.StartsWith("Error: create_repeats_last_create", fourth);
        Assert.DoesNotContain("RESTORED:", fourth);
    }

    /// <summary>Law 252 (batch 18 s18h it.7/8/9/17, s18a it.18; every
    /// `file_exists` on batches 13-18): DirectBash THROWS
    /// InvalidOperationException("file_exists: path") instead of returning
    /// the string the rewrite below matches, so the seat has only ever seen
    /// the raw "Error: file_exists: path" -- never the rm-then-create hint.
    /// The test above proved the rewrite against a sandbox that RETURNS the
    /// string; this one models the sandbox that runs.</summary>
    [Fact]
    public async Task FileEditor_Create_WhenTheSandboxThrowsFileExists_StillGetsTheRewrite()
    {
        _sb.FileCreateException = new InvalidOperationException("file_exists: /law252/a.js");
        Dictionary<string, object?> Args() => new() { ["command_name"] = "create", ["path"] = "/law252/a.js", ["file_text"] = "x\n" };

        var a = await Call("file_editor", Args());
        var b = await Call("file_editor", Args());

        Assert.StartsWith("Error: file_editor `create` failed", a);
        Assert.Contains("rm", a);
        Assert.Contains("`str_replace`", a);
        Assert.StartsWith("Error: file_editor `create` failed", b);   // a refused create is not a repeat
    }

    /// <summary>Law 256 (batch 22, VETT 455f523, RAN on the wire): SIX of six
    /// law-252 `already exists` hints were followed by `rm` of that same path
    /// and ZERO by `str_replace` -- s22a it.18, s22b it.15 and it.32, s22d
    /// it.13, s22e it.25, s22f it.7; s23a it.20 repeated it on the law-255
    /// binary and then burned it.21-34 deleting and regenerating facts-01.js.
    /// The hint's rm-then-create branch is the seed of the livelock that laws
    /// 243 and 255 exist to contain. Offer it only on the FIRST collision;
    /// once this session has successfully created the path, the seat has been
    /// here before and gets the non-destructive recipe with no `rm` in it.</summary>
    /// <summary>Law 257 (batch 23, VETT da053df, RAN on the wire): law 255
    /// restored `src/facts/facts-02.js` for s23c three times -- it.28, it.31,
    /// it.34, 6,557 chars each -- and each restore was followed by `rm -f` of
    /// that same path at it.30, it.33 and it.35. The refusal text it ignored
    /// three times contains the literal sentence "Do NOT `rm` it and do NOT
    /// `create` it". Eight of its 36 iterations went into that cycle. A seat
    /// that has stopped reading cannot be reached by better prose, so the
    /// restored path gets a DOOR: `rm` of it is refused until the seat edits
    /// the file.</summary>
    [Fact]
    public async Task Terminal_Rm_OfAPathLaw255Restored_IsRefusedAndDoesNotRun()
    {
        var body = await Law257RestoreAsync("/law257/facts-01.js");
        Assert.Contains("RESTORED:", body);
        var before = _sb.BashCalls.Count;

        var refused = await Call("terminal", new() { ["command"] = "rm -f /law257/facts-01.js && echo removed" });

        Assert.StartsWith("Error: rm_of_restored_file", refused);
        Assert.Contains("/law257/facts-01.js", refused);
        Assert.Contains("`str_replace`", refused);
        Assert.Equal(before, _sb.BashCalls.Count);          // the command never reached the shell
    }

    /// <summary>Law 257, other side: an ordinary `rm` still runs. The door is
    /// scoped to paths this session's harness has restored, nothing else.</summary>
    [Fact]
    public async Task Terminal_Rm_OfAPathNothingRestored_StillRuns()
    {
        var before = _sb.BashCalls.Count;

        var result = await Call("terminal", new() { ["command"] = "rm -f /law257/never-restored.js" });

        Assert.DoesNotContain("rm_of_restored_file", result);
        Assert.Equal(before + 1, _sb.BashCalls.Count);
    }

    /// <summary>Law 257, the way out: the door opens as soon as the seat edits
    /// the restored file. The point is to stop the seat throwing away bytes it
    /// has not looked at, not to make a path undeletable for the whole hour.</summary>
    [Fact]
    public async Task Terminal_Rm_OfARestoredPath_RunsOnceTheSeatHasEditedIt()
    {
        await Law257RestoreAsync("/law257/facts-02.js");
        await Call("file_editor", new() { ["command_name"] = "str_replace", ["path"] = "/law257/facts-02.js", ["old_str"] = "one", ["new_str"] = "ONE" });
        var before = _sb.BashCalls.Count;

        var result = await Call("terminal", new() { ["command"] = "rm /law257/facts-02.js" });

        Assert.DoesNotContain("rm_of_restored_file", result);
        Assert.Equal(before + 1, _sb.BashCalls.Count);
    }

    /// <summary>Law 258 (batch 24, s24d, RAN on the wire). Law 257's door
    /// matches the command word `rm`; s24d walked around it with a shell
    /// redirect. Law 255 restored `src/facts/facts-01.js` at it.39 and said
    /// "Do NOT `rm` it and do NOT `create` it"; the seat's `str_replace` at
    /// it.41 FAILED (`str_replace_no_match`, `old_str` retyped from memory --
    /// the arm never called `view` once all hour), and a failed edit does not
    /// clear the door, so it was still armed when the seat sent four
    /// `cat > src/facts/facts-01.js <<'EOF'` calls at it.42/43 that truncated
    /// the restored file and rewrote it with fourteen copies of one sentence.
    /// A truncating redirect destroys banked work exactly as `rm` does.</summary>
    [Fact]
    public async Task Terminal_TruncatingRedirect_OfAPathLaw255Restored_IsRefusedAndDoesNotRun()
    {
        var body = await Law257RestoreAsync("/law258/facts-01.js");
        Assert.Contains("RESTORED:", body);
        var before = _sb.BashCalls.Count;

        var refused = await Call("terminal", new() { ["command"] = "cat > /law258/facts-01.js <<'EOF'\nrewritten\nEOF" });

        Assert.StartsWith("Error: overwrite_of_restored_file", refused);
        Assert.Contains("/law258/facts-01.js", refused);
        Assert.Contains("`str_replace`", refused);
        Assert.Equal(before, _sb.BashCalls.Count);          // the command never reached the shell
    }

    /// <summary>Law 258, the line it draws: APPEND is not destruction. `>>`
    /// adds to the banked bytes instead of throwing them away, so it runs.
    /// A door that refused every write to the path would just move the seat's
    /// loop somewhere else.</summary>
    [Fact]
    public async Task Terminal_AppendRedirect_OfARestoredPath_StillRuns()
    {
        await Law257RestoreAsync("/law258/facts-02.js");
        var before = _sb.BashCalls.Count;

        var result = await Call("terminal", new() { ["command"] = "echo more >> /law258/facts-02.js" });

        Assert.DoesNotContain("restored_file", result);
        Assert.Equal(before + 1, _sb.BashCalls.Count);
    }

    /// <summary>Law 258 covers `tee` on the same rule: plain `tee` truncates
    /// and is refused, `tee -a` appends and runs.</summary>
    [Fact]
    public async Task Terminal_Tee_OfARestoredPath_IsRefused_AndTeeAppendRuns()
    {
        await Law257RestoreAsync("/law258/facts-03.js");
        await Law257RestoreAsync("/law258/facts-04.js");
        var before = _sb.BashCalls.Count;

        var refused = await Call("terminal", new() { ["command"] = "echo x | tee /law258/facts-03.js" });
        Assert.StartsWith("Error: overwrite_of_restored_file", refused);
        Assert.Equal(before, _sb.BashCalls.Count);

        var ran = await Call("terminal", new() { ["command"] = "echo x | tee -a /law258/facts-04.js" });
        Assert.DoesNotContain("restored_file", ran);
        Assert.Equal(before + 1, _sb.BashCalls.Count);
    }

    /// <summary>Law 258, other side: a truncating redirect to any path the
    /// harness has NOT restored still runs. The door is scoped to banked
    /// bytes, not to redirects.</summary>
    [Fact]
    public async Task Terminal_TruncatingRedirect_OfAPathNothingRestored_StillRuns()
    {
        var before = _sb.BashCalls.Count;

        var result = await Call("terminal", new() { ["command"] = "cat > /law258/never-restored.js <<'EOF'\nx\nEOF" });

        Assert.DoesNotContain("restored_file", result);
        Assert.Equal(before + 1, _sb.BashCalls.Count);
    }

    /// <summary>Drives one path all the way to a law-255 restore: create, the
    /// seat deletes, identical create (warned), deletes again, identical create
    /// (refused -- and restored). Returns the refusal text.</summary>
    private async Task<string> Law257RestoreAsync(string path)
    {
        _sb.Files ??= new HashSet<string>();
        _sb.FileCreateResult = "1\tone\n2\ttwo";
        Dictionary<string, object?> Args() => new() { ["command_name"] = "create", ["path"] = path, ["file_text"] = "one\ntwo\n" };

        await Call("file_editor", Args());                  // lands, banked
        _sb.Files.Remove(path);                             // the seat's rm
        await Call("file_editor", Args());                  // repeat 1 -> warning
        _sb.Files.Remove(path);                             // the seat's rm again
        return await Call("file_editor", Args());           // repeat 2 -> refused + RESTORED
    }

    [Fact]
    public async Task FileEditor_Create_OnAPathThisSessionCreated_DoesNotOfferRmThenCreate()
    {
        _sb.Files = new HashSet<string>();
        _sb.FileCreateResult = "1\tfirst";
        const string path = "/law256/facts-01.js";
        Dictionary<string, object?> Args(string text) => new() { ["command_name"] = "create", ["path"] = path, ["file_text"] = text };

        var made = await Call("file_editor", Args("first\n"));          // lands; the harness banks the create
        var collide = await Call("file_editor", Args("second\n"));      // different bytes, path present

        Assert.StartsWith("1\tfirst", made);
        Assert.StartsWith("Error: file_editor `create` failed", collide);
        Assert.Contains("`insert`", collide);
        Assert.Contains("`str_replace`", collide);
        Assert.DoesNotContain("rm", collide);                          // the destructive recipe is GONE
    }

    /// <summary>Law 256, other side: a path this session did NOT create still
    /// gets the full hint, `rm` branch included -- the seat may legitimately
    /// need to replace a file it inherited.</summary>
    [Fact]
    public async Task FileEditor_Create_OnAPathThisSessionDidNotCreate_StillOffersRm()
    {
        _sb.FileCreateException = new InvalidOperationException("file_exists: /law256/inherited.js");
        var hint = await Call("file_editor", new() { ["command_name"] = "create", ["path"] = "/law256/inherited.js", ["file_text"] = "x\n" });

        Assert.StartsWith("Error: file_editor `create` failed", hint);
        Assert.Contains("rm", hint);
        Assert.Contains("`str_replace`", hint);
    }

    [Fact]
    public async Task FileEditor_Create_RefusedBySandbox_DoesNotCountAsARepeat()
    {
        _sb.FileCreateResult = "Error: file_exists: /law243/other.js";
        const string same = "x\n";
        Dictionary<string, object?> Args() => new() { ["command_name"] = "create", ["path"] = "/law243/other.js", ["file_text"] = same };

        var a = await Call("file_editor", Args());
        var b = await Call("file_editor", Args());
        var c = await Call("file_editor", Args());

        Assert.StartsWith("Error: file_editor `create` failed", a);
        Assert.StartsWith("Error: file_editor `create` failed", b);
        Assert.StartsWith("Error: file_editor `create` failed", c);
    }

    /// <summary>2026-09-09 (s13c it.23-29, batch 13, VETT da4e8fa): a
    /// str_replace whose new_str equalled its old_str (1,267 chars) was applied
    /// and answered with the numbered view as success, six times; the seat,
    /// told "edited" and seeing nothing edited, re-sent it until the
    /// identical-call breaker ended the arm at 1,591 s. A no-op is refused
    /// on the first call, spelled as an error, before the sandbox is asked.</summary>
    [Fact]
    public async Task FileEditor_StrReplace_IdenticalOldAndNew_IsRefusedBeforeTheSandbox()
    {
        _sb.StrReplaceResult = ("1\tsame\n", null);
        const string same = "export const FACTS_01 = [\n  \"one\",\n];\n";

        var result = await Call("file_editor", new() { ["command_name"] = "str_replace", ["path"] = "/test.js", ["old_str"] = same, ["new_str"] = same });

        Assert.StartsWith("Error: str_replace_noop", result);
        Assert.DoesNotContain("1\tsame", result);
    }

    /// <summary>Law 250 (batch 17, 29 refusals on five arms): the seat re-sends
    /// the refused pair because its rewrite of a long block comes out as a
    /// copy. The second consecutive no-op on a path names the file's line
    /// count and the three calls that cannot copy; an edit that lands
    /// clears the count so the next no-op is the plain refusal again.</summary>
    [Fact]
    public async Task FileEditor_StrReplace_SecondNoopInARow_NamesTheLineCountAndTheWayOut()
    {
        const string same = "export const FACTS_01 = [\n  \"one\",\n];\n";
        _sb.FileViewResult = "1\texport const FACTS_01 = [\n2\t  \"one\",\n3\t];\n";
        Dictionary<string, object?> Noop() => new() { ["command_name"] = "str_replace", ["path"] = "/law250.js", ["old_str"] = same, ["new_str"] = same };

        var first = await Call("file_editor", Noop());
        var second = await Call("file_editor", Noop());
        var third = await Call("file_editor", Noop());

        Assert.StartsWith("Error: str_replace_noop:", first);
        Assert.StartsWith("Error: str_replace_noop_repeated:", second);
        Assert.Contains("2 times in a row", second);
        Assert.Contains("`/law250.js` has 3 lines", second);
        Assert.Contains("`insert_line`: 3", second);
        Assert.Contains("`create`", second);
        Assert.Contains("rm -f", second);   // law 252: create alone is refused on an existing path
        Assert.Contains("3 times in a row", third);

        _sb.StrReplaceResult = ("1\tnew\n", null);
        var landed = await Call("file_editor", new() { ["command_name"] = "str_replace", ["path"] = "/law250.js", ["old_str"] = same, ["new_str"] = "changed" });
        Assert.Equal("1\tnew\n", landed);
        var again = await Call("file_editor", Noop());
        Assert.StartsWith("Error: str_replace_noop:", again);
    }

    [Fact]
    public void NumberedLineCount_ReadsTheLastNumberedLine()
    {
        Assert.Equal(3, Builtins.NumberedLineCount("1\ta\n2\tb\n3\tc\n"));
        Assert.Equal(-1, Builtins.NumberedLineCount("file content"));
        Assert.Equal(-1, Builtins.NumberedLineCount(""));
    }

    [Fact]
    public async Task FileEditor_StrReplace_DifferentNew_StillReachesTheSandbox()
    {
        _sb.StrReplaceResult = ("1\tnew\n", null);

        var result = await Call("file_editor", new() { ["command_name"] = "str_replace", ["path"] = "/test.js", ["old_str"] = "old", ["new_str"] = "new" });

        Assert.Equal("1\tnew\n", result);
    }

    [Fact]
    public async Task FileEditor_UnknownCommand()
    {
        var result = await Call("file_editor", new() { ["command_name"] = "foobar", ["path"] = "/test.py" });

        Assert.Contains("unknown file_editor command", result);
    }

    [Fact]
    public void AllBuiltinToolsRegistered()
    {
        var tools = Builtins.All();
        var expected = new[] { "terminal", "file_editor", "think", "finish", "task_tracker" };

        foreach (var name in expected)
            Assert.True(tools.ContainsKey(name), $"Missing tool: {name}");
    }

    [Fact]
    public async Task TaskTracker_PlanThenView_RoundTrips()
    {
        var sessionId = $"tt-{Guid.NewGuid()}";
        TaskTracker.Reset(sessionId);

        var taskListJson = System.Text.Json.JsonDocument.Parse(
            "[{\"title\":\"Reproduce bug\",\"status\":\"in_progress\",\"notes\":\"see issue #42\"}," +
            "{\"title\":\"Write fix\",\"status\":\"todo\"}]").RootElement;

        var planResult = await _tools["task_tracker"](
            new() { ["command"] = "plan", ["task_list"] = taskListJson },
            _sb, sessionId, default);
        Assert.Contains("updated with 2 item(s)", planResult);

        var viewResult = await _tools["task_tracker"](
            new() { ["command"] = "view" }, _sb, sessionId, default);
        Assert.Contains("# Task List", viewResult);
        Assert.Contains("Reproduce bug", viewResult);
        Assert.Contains("see issue #42", viewResult);
        Assert.Contains("Write fix", viewResult);
    }

    [Fact]
    public async Task TaskTracker_ViewEmpty_ReturnsHint()
    {
        var sessionId = $"tt-empty-{Guid.NewGuid()}";
        TaskTracker.Reset(sessionId);

        var result = await _tools["task_tracker"](
            new() { ["command"] = "view" }, _sb, sessionId, default);
        Assert.Contains("No task list found", result);
    }

    [Fact]
    public async Task TaskTracker_PerSession_Isolated()
    {
        var sA = $"a-{Guid.NewGuid()}";
        var sB = $"b-{Guid.NewGuid()}";
        TaskTracker.Reset(sA);
        TaskTracker.Reset(sB);

        var listA = System.Text.Json.JsonDocument.Parse(
            "[{\"title\":\"A1\",\"status\":\"todo\"}]").RootElement;
        await _tools["task_tracker"](new() { ["command"] = "plan", ["task_list"] = listA }, _sb, sA, default);

        var viewB = await _tools["task_tracker"](
            new() { ["command"] = "view" }, _sb, sB, default);
        Assert.Contains("No task list found", viewB);
    }

    [Fact]
    public async Task Terminal_AcceptsJsonElementArgs()
    {
        // Regression: Microsoft.Extensions.AI.OpenAI hands FunctionCallContent
        // arguments through as JsonElement, not raw strings/ints. Without
        // JsonElement support in the Str/Int extractors, every real LLM call
        // would silently see empty args and the agent would loop on errors.
        var commandJson = System.Text.Json.JsonDocument.Parse("\"echo hi\"").RootElement;
        var timeoutJson = System.Text.Json.JsonDocument.Parse("30").RootElement;

        var result = await Call("terminal", new()
        {
            ["command"] = commandJson,
            ["timeout"] = timeoutJson,
        });

        Assert.Contains("hello", result); // FakeSandbox returns "hello\n"
        Assert.Single(_sb.BashCalls);
        Assert.Equal("echo hi", _sb.BashCalls[0].Cmd);
        Assert.Equal(30, _sb.BashCalls[0].Timeout);
    }
}
