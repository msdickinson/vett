using Vett.Agent;
using Vett.Bench.Team;

namespace Vett.Tests;

/// <summary>
/// Verifies workspace-containment enforcement in the bench assertion engine.
///
/// The bug this guards against: agent's cwd drifts (it cd's somewhere odd,
/// or emits an absolute path / path with .. segments in a file_editor call),
/// the workspace itself is empty / partial, but a `file:` assertion still
/// resolves to a file that exists OUTSIDE the workspace, producing a
/// false-positive PASS.
///
/// Fix: ResolveInside canonicalizes the resolved path and verifies it
/// is the workspace itself or a descendant.
/// </summary>
public class AssertionEngineContainmentTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _outside;

    public AssertionEngineContainmentTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "vet-assert-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _workspace = Path.Combine(root, "workspace");
        _outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_workspace)!, recursive: true); } catch { }
    }

    private static AssertionResult Eval(TeamBenchAssertion a, string workspace) =>
        AssertionEngine.Evaluate(
            new List<TeamBenchAssertion> { a },
            Array.Empty<Event>(),
            workspace,
            leaderIterations: 0,
            memberIterations: new Dictionary<string, int>(),
            // These are path-containment tests; the run they model completed.
            countsComplete: true,
            assistantTexts: Array.Empty<string>()
        ).Single();

    // ---------- happy paths --------------------------------------------

    [Fact]
    public void File_InsideWorkspace_Exists_Passes()
    {
        File.WriteAllText(Path.Combine(_workspace, "hello.txt"), "hi");
        var r = Eval(new TeamBenchAssertion { File = "hello.txt", Exists = true }, _workspace);
        Assert.True(r.Pass, r.Detail);
    }

    [Fact]
    public void File_InsideSubdir_Exists_Passes()
    {
        var sub = Path.Combine(_workspace, "src", "Foo");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "Bar.cs"), "// bar");
        var r = Eval(new TeamBenchAssertion { File = "src/Foo/Bar.cs", Exists = true }, _workspace);
        Assert.True(r.Pass, r.Detail);
    }

    [Fact]
    public void File_RelativeMissing_FailsCleanly_NotEscape()
    {
        // Missing file inside the workspace should fail with the
        // file-not-found message, NOT with the escape message.
        var r = Eval(new TeamBenchAssertion { File = "absent.txt", Exists = true }, _workspace);
        Assert.False(r.Pass);
        Assert.Contains("file not found", r.Detail!);
        Assert.DoesNotContain("escapes workspace", r.Detail);
    }

    // ---------- escape: absolute paths ---------------------------------

    [Fact]
    public void File_AbsolutePath_OutsideWorkspace_FailsAsEscape()
    {
        // Plant a file outside the workspace; an absolute-path assertion
        // pointing to it must NOT pass.
        var outsideFile = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(outsideFile, "ha");

        var r = Eval(new TeamBenchAssertion { File = outsideFile, Exists = true }, _workspace);
        Assert.False(r.Pass);
        Assert.Contains("escapes workspace", r.Detail!);
    }

    // ---------- escape: .. traversal -----------------------------------

    [Fact]
    public void File_DotDotEscape_FailsAsEscape()
    {
        // Plant a file outside; reach it via ../outside/secret.txt.
        var outsideFile = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(outsideFile, "ha");

        var r = Eval(new TeamBenchAssertion
        {
            File = Path.Combine("..", "outside", "secret.txt"),
            Exists = true
        }, _workspace);
        Assert.False(r.Pass);
        Assert.Contains("escapes workspace", r.Detail!);
    }

    [Fact]
    public void File_DotDotInside_StillResolvesInside_Passes()
    {
        // src/../top.txt resolves to <workspace>/top.txt — that's inside,
        // so the escape check must NOT fire even though .. is present.
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        File.WriteAllText(Path.Combine(_workspace, "top.txt"), "ok");

        var r = Eval(new TeamBenchAssertion
        {
            File = Path.Combine("src", "..", "top.txt"),
            Exists = true
        }, _workspace);
        Assert.True(r.Pass, r.Detail);
    }

    // ---------- the regression that motivated the fix ------------------

    [Fact]
    public void File_AbsolutePath_ToFileThatActuallyExistsOutsideWorkspace_DoesNotFalsePassPriorBug()
    {
        // Direct simulation of the 2026-05-11 panel-match incident: agent
        // writes PanelMatch.slnx into the system temp directory (absolute
        // path used in the file_editor call); the workspace itself ends
        // up nearly empty. Before the fix, the bench's file: assertion
        // with the absolute path would have PASSed because Path.Combine
        // silently returns the absolute arg. After the fix, it fails as
        // an escape — which is the correct outcome since the file is not
        // actually in the workspace.
        var slnx = Path.Combine(_outside, "PanelMatch.slnx");
        File.WriteAllText(slnx, "<solution>...</solution>");

        var r = Eval(new TeamBenchAssertion { File = slnx, Exists = true }, _workspace);
        Assert.False(r.Pass);
        Assert.Contains("escapes workspace", r.Detail!);
    }
}
