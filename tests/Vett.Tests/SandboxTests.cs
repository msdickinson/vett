using Vett.Sandbox;

namespace Vett.Tests;

public class DirectBashTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DirectBash _bash;

    public DirectBashTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vett-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _bash = new DirectBash(_tempDir);
    }

    public void Dispose()
    {
        _bash.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task FileView_ReturnsLineNumbers()
    {
        CreateFile("test.py", "def foo():\n    return 1\n");

        var result = await _bash.FileViewAsync("s", Path.Combine(_tempDir, "test.py"));

        Assert.Contains("1\tdef foo():", result);
        Assert.Contains("2\t    return 1", result);
    }

    [Fact]
    public async Task FileCreate_Succeeds()
    {
        var path = Path.Combine(_tempDir, "new.py");

        await _bash.FileCreateAsync("s", path, "hello\n");

        Assert.True(File.Exists(path));
        Assert.Equal("hello\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task FileCreate_FailsIfExists()
    {
        CreateFile("existing.py", "content");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _bash.FileCreateAsync("s", Path.Combine(_tempDir, "existing.py"), "new"));
    }

    [Fact]
    public async Task StrReplace_SingleMatch()
    {
        CreateFile("test.py", "return 1\n");

        var (content, error) = await _bash.FileStrReplaceAsync("s",
            Path.Combine(_tempDir, "test.py"), "return 1", "return 2");

        Assert.Null(error);
        Assert.Contains("return 2", File.ReadAllText(Path.Combine(_tempDir, "test.py")));
    }

    [Fact]
    public async Task StrReplace_NoMatch()
    {
        CreateFile("test.py", "return 1\n");

        var (_, error) = await _bash.FileStrReplaceAsync("s",
            Path.Combine(_tempDir, "test.py"), "nonexistent", "replacement");

        Assert.NotNull(error);
        Assert.Contains("str_replace_no_match", error);
    }

    // The three tests below pin the recovery payload added 2026-09-11. Measured
    // motivation, across 36 EpicForge probe transcripts: of 20 no-match events
    // 19 had an anchor absent under every whitespace relaxation, and in 11 of
    // the 20 the seat's very next action was to retype the anchor from memory
    // or destroy the file (six `cat > src/...` or `rm src/...`). The coaching
    // sentence alone was followed 9 times in 20, so the file text now comes
    // back with it. A message cannot close a loop the harness still permits.

    [Fact]
    public async Task StrReplace_NoMatch_ReturnsTheRealTextNearTheAnchor()
    {
        CreateFile("calc.py",
            "def add(a, b):\n    return a + b\n\ndef mul(a, b):\n    return a * b\n");

        // The anchor is *close* to a real line but not equal to it -- the
        // common case when a seat retypes from memory.
        var (_, error) = await _bash.FileStrReplaceAsync("s",
            Path.Combine(_tempDir, "calc.py"), "    return a*b", "    return a * b * 1");

        Assert.NotNull(error);
        Assert.Contains("str_replace_no_match", error);
        // It must name where it looked and show the actual bytes there.
        Assert.Contains("closest line to your anchor", error);
        Assert.Contains("return a * b", error);
        Assert.Contains("def mul(a, b):", error);
    }

    [Fact]
    public async Task StrReplace_NoMatch_AnchorNowhereNear_SaysSoAndShowsHead()
    {
        CreateFile("calc.py", "def add(a, b):\n    return a + b\n");

        var (_, error) = await _bash.FileStrReplaceAsync("s",
            Path.Combine(_tempDir, "calc.py"), "zzzz_completely_unrelated_zzzz", "x");

        Assert.NotNull(error);
        Assert.Contains("not there at all", error);
        Assert.Contains("def add(a, b):", error);
    }

    // CONTROL. The recovery must not leak onto the success path, which already
    // returns its own full view -- otherwise this "fix" would double every
    // successful edit's payload and cost context on the one path that is fine.
    [Fact]
    public async Task StrReplace_Success_CarriesNoRecoveryText()
    {
        CreateFile("calc.py", "def add(a, b):\n    return a + b\n");

        var (content, error) = await _bash.FileStrReplaceAsync("s",
            Path.Combine(_tempDir, "calc.py"), "return a + b", "return b + a");

        Assert.Null(error);
        Assert.DoesNotContain("closest line to your anchor", content);
        Assert.DoesNotContain("not there at all", content);
    }

    [Fact]
    public async Task StrReplace_MultipleMatches()
    {
        CreateFile("test.py", "a = 1\nb = 1\n");

        var (_, error) = await _bash.FileStrReplaceAsync("s",
            Path.Combine(_tempDir, "test.py"), "1", "2");

        Assert.NotNull(error);
        Assert.Contains("str_replace_multi_match", error);
    }

    [Fact]
    public async Task Undo_RestoresOriginal()
    {
        CreateFile("test.py", "return 1\n");
        var path = Path.Combine(_tempDir, "test.py");

        await _bash.FileStrReplaceAsync("s", path, "return 1", "return 2");
        Assert.Contains("return 2", File.ReadAllText(path));

        await _bash.FileUndoAsync("s", path);
        Assert.Contains("return 1", File.ReadAllText(path));
    }

    // Line-ending rescue (2026-07-08): on Windows, git worktrees check
    // out with core.autocrlf so files carry \r\n while models send \n —
    // an exact match then never succeeds on multi-line snippets. The
    // sandbox must match modulo line endings and preserve the file's own.

    [Fact]
    public async Task StrReplace_CrlfFile_LfOldStr_MatchesAndPreservesCrlf()
    {
        CreateFile("test.cs", "// TODO: services\r\nvar app = Build();\r\n// TODO: map\r\napp.Run();\r\n");
        var path = Path.Combine(_tempDir, "test.cs");

        var (_, error) = await _bash.FileStrReplaceAsync("s", path,
            "// TODO: services\nvar app = Build();\n// TODO: map",
            "AddControllers();\nvar app = Build();\napp.MapControllers();");

        Assert.Null(error);
        var result = File.ReadAllText(path);
        Assert.Contains("AddControllers();\r\nvar app = Build();\r\napp.MapControllers();\r\napp.Run();", result);
        Assert.DoesNotContain("// TODO", result);
        // no bare-LF lines smuggled into a CRLF file
        Assert.DoesNotContain('\n', result.Replace("\r\n", ""));
    }

    [Fact]
    public async Task StrReplace_LfFile_CrlfOldStr_Matches()
    {
        CreateFile("test.cs", "line one\nline two\nline three\n");
        var path = Path.Combine(_tempDir, "test.cs");

        var (_, error) = await _bash.FileStrReplaceAsync("s", path,
            "line one\r\nline two", "first\r\nsecond");

        Assert.Null(error);
        var result = File.ReadAllText(path);
        Assert.Equal("first\nsecond\nline three\n", result);
    }

    [Fact]
    public async Task StrReplace_CrlfRescue_MultiMatch_Errors()
    {
        CreateFile("test.cs", "a = 1\r\nb = 2\r\na = 1\r\nb = 2\r\n");
        var path = Path.Combine(_tempDir, "test.cs");

        var (_, error) = await _bash.FileStrReplaceAsync("s", path,
            "a = 1\nb = 2", "x");

        Assert.NotNull(error);
        Assert.Contains("str_replace_multi_match", error);
    }

    [Fact]
    public async Task StrReplace_CrlfFile_GenuinelyAbsent_StillNoMatch()
    {
        CreateFile("test.cs", "real content\r\nmore\r\n");
        var path = Path.Combine(_tempDir, "test.cs");

        var (_, error) = await _bash.FileStrReplaceAsync("s", path,
            "not here\nat all", "x");

        Assert.NotNull(error);
        Assert.Contains("str_replace_no_match", error);
    }

    [Fact]
    public async Task StrReplace_CrlfRescue_Undo_RestoresOriginal()
    {
        CreateFile("test.cs", "keep\r\nold line\r\n");
        var path = Path.Combine(_tempDir, "test.cs");

        await _bash.FileStrReplaceAsync("s", path, "keep\nold line", "keep\nnew line");
        Assert.Contains("new line", File.ReadAllText(path));

        await _bash.FileUndoAsync("s", path);
        Assert.Equal("keep\r\nold line\r\n", File.ReadAllText(path));
    }
}

/// <summary>
/// Path-translation policy tests for the dispatch-worktree boundary
/// installed by <see cref="ISandbox.WithDispatchWorktree"/>. Without this
/// boundary an agent given an absolute parent path silently writes
/// straight into the user's workspace, defeating the per-task worktree
/// — the exact bug seen in the May-2026 README.md test session where
/// `file_editor create path:c:\Users\dev\TEST\README.md` from inside
/// a worktree-rooted dispatch landed the file in the parent and made
/// accept_dispatch error with "no pending dispatch".
/// </summary>
public class DirectBashDispatchBoundaryTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly string _worktreeRoot;
    private readonly DirectBash _parent;
    private readonly ISandbox _dispatch;

    public DirectBashDispatchBoundaryTests()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "vett-disp-" + Guid.NewGuid().ToString("N")[..8]);
        _workspaceRoot = Path.Combine(tempBase, "workspace");
        _worktreeRoot = Path.Combine(tempBase, "worktree");
        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(_worktreeRoot);

        _parent = new DirectBash(_workspaceRoot);
        _dispatch = _parent.WithDispatchWorktree(_worktreeRoot, _workspaceRoot);
    }

    public void Dispose()
    {
        _parent.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_workspaceRoot)!, true); } catch { }
    }

    [Fact]
    public async Task RelativePath_LandsInWorktree()
    {
        await _dispatch.FileCreateAsync("s", "README.md", "# inside\n");

        Assert.True(File.Exists(Path.Combine(_worktreeRoot, "README.md")));
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "README.md")));
    }

    [Fact]
    public async Task AbsoluteParentPath_GetsRewrittenToWorktree()
    {
        // The bug: leader's task description embeds an absolute parent path,
        // implementer obeys verbatim, file lands in parent. After fix, the
        // path translates to the worktree-relative equivalent.
        var parentPath = Path.Combine(_workspaceRoot, "README.md");

        await _dispatch.FileCreateAsync("s", parentPath, "# inside\n");

        Assert.True(File.Exists(Path.Combine(_worktreeRoot, "README.md")));
        Assert.False(File.Exists(parentPath));
    }

    [Fact]
    public async Task AbsoluteParentSubdirPath_GetsRewrittenToWorktree()
    {
        var parentPath = Path.Combine(_workspaceRoot, "src", "main.cs");

        await _dispatch.FileCreateAsync("s", parentPath, "// inside\n");

        Assert.True(File.Exists(Path.Combine(_worktreeRoot, "src", "main.cs")));
        Assert.False(File.Exists(parentPath));
    }

    [Fact]
    public async Task AbsoluteWorktreePath_PassesThroughUnchanged()
    {
        var worktreePath = Path.Combine(_worktreeRoot, "x.txt");

        await _dispatch.FileCreateAsync("s", worktreePath, "ok\n");

        Assert.True(File.Exists(worktreePath));
    }

    [Fact]
    public async Task UnrelatedAbsolutePath_PassesThrough()
    {
        // /tmp scratch + system paths aren't isolation's job to police —
        // they pass through. (Use a temp dir outside both roots so the
        // test is hermetic and cross-platform.)
        var elsewhere = Path.Combine(Path.GetTempPath(), "vett-disp-elsewhere-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(elsewhere);
        try
        {
            var elsewherePath = Path.Combine(elsewhere, "scratch.txt");

            await _dispatch.FileCreateAsync("s", elsewherePath, "scratch\n");

            Assert.True(File.Exists(elsewherePath));
        }
        finally
        {
            try { Directory.Delete(elsewhere, true); } catch { }
        }
    }

    [Fact]
    public async Task PlainWithCwd_DoesNotTranslate()
    {
        // Non-team dispatches still use plain WithCwd, which must NOT
        // install the boundary (no behavior change for callers that
        // hadn't opted into the dispatch policy).
        var plain = (DirectBash)_parent.WithCwd(_worktreeRoot);
        Assert.Null(plain.DispatchWorkspaceRoot);

        var parentPath = Path.Combine(_workspaceRoot, "should-go-to-parent.txt");
        await plain.FileCreateAsync("s", parentPath, "hi\n");

        Assert.True(File.Exists(parentPath));
        Assert.False(File.Exists(Path.Combine(_worktreeRoot, "should-go-to-parent.txt")));
    }

    [Fact]
    public async Task ViewAbsoluteParentPath_ShowsWorktreeContents()
    {
        // file_editor view of the workspace root from inside a dispatch
        // should show the WORKTREE's contents, not the parent's. Otherwise
        // the agent reasons about the parent state and may then write
        // based on stale assumptions.
        File.WriteAllText(Path.Combine(_workspaceRoot, "parent-only.txt"), "in parent");
        File.WriteAllText(Path.Combine(_worktreeRoot, "worktree-only.txt"), "in worktree");

        var view = await _dispatch.FileViewAsync("s", _workspaceRoot);

        Assert.Contains("worktree-only.txt", view);
        Assert.DoesNotContain("parent-only.txt", view);
    }
}
