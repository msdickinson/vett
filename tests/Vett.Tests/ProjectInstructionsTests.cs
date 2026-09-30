using Vett.Config;

namespace Vett.Tests;

/// <summary>
/// Covers the v1 hierarchical AGENTS.md / VETT.md loader.
///
/// Each test runs against a fresh temp directory tree so we don't
/// accidentally pick up a real VETT.md from the dev's workspace. We also
/// override <c>HOME</c> / <c>USERPROFILE</c> per-test because Load
/// appends user-global rules from <c>~/.vett/</c>.
///
/// Joins the "declare-done-env" collection — the repo's bucket for classes
/// that write process-global env vars — because HOME/USERPROFILE are written
/// here AND in ConfigResolutionAuditTests. Two classes clobbering each
/// other's HOME concurrently is a flake, not a gate.
/// </summary>
[Collection("declare-done-env")]
public class ProjectInstructionsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _origHome;
    private readonly string? _origUserProfile;
    private readonly string _fakeHome;

    public ProjectInstructionsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "vett-pi-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _fakeHome = Path.Combine(_tempRoot, "fake-home");
        Directory.CreateDirectory(_fakeHome);

        // Snapshot + override env. Restored in Dispose so other tests
        // don't see our fake home leak.
        _origHome = Environment.GetEnvironmentVariable("HOME");
        _origUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        Environment.SetEnvironmentVariable("HOME", _fakeHome);
        Environment.SetEnvironmentVariable("USERPROFILE", _fakeHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _origHome);
        Environment.SetEnvironmentVariable("USERPROFILE", _origUserProfile);
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    /// <summary>
    /// Helper: build a fake workspace with .git so WalkWorkspace treats
    /// it as a workspace root rather than a random ancestor directory.
    /// </summary>
    private string MakeWorkspace(string name = "ws")
    {
        var ws = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(Path.Combine(ws, ".git"));
        return ws;
    }

    [Fact]
    public void Load_NoFiles_ReturnsEmpty()
    {
        var ws = MakeWorkspace();
        Assert.Equal("", ProjectInstructions.Load(ws));
    }

    [Fact]
    public void Load_VettMd_AtCwd_BackwardCompat()
    {
        var ws = MakeWorkspace();
        File.WriteAllText(Path.Combine(ws, "VETT.md"), "Be terse.");
        var loaded = ProjectInstructions.Load(ws);
        Assert.Contains("Be terse.", loaded);
        Assert.Contains("VETT.md", loaded);
    }

    [Fact]
    public void Load_AgentsMd_AtCwd_AlsoLoaded()
    {
        var ws = MakeWorkspace();
        File.WriteAllText(Path.Combine(ws, "AGENTS.md"), "Do not delete files.");
        var loaded = ProjectInstructions.Load(ws);
        Assert.Contains("Do not delete files.", loaded);
        Assert.Contains("AGENTS.md", loaded);
    }

    [Fact]
    public void Load_BothAgentsAndVett_BothIncluded()
    {
        var ws = MakeWorkspace();
        File.WriteAllText(Path.Combine(ws, "AGENTS.md"), "from-agents");
        File.WriteAllText(Path.Combine(ws, "VETT.md"), "from-vett");
        var loaded = ProjectInstructions.Load(ws);
        Assert.Contains("from-agents", loaded);
        Assert.Contains("from-vett", loaded);
    }

    [Fact]
    public void Load_HierarchicalWalk_ParentBeforeChild()
    {
        var ws = MakeWorkspace();
        var nested = Path.Combine(ws, "src", "deep");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(ws, "AGENTS.md"), "ROOT-RULE");
        File.WriteAllText(Path.Combine(nested, "AGENTS.md"), "DEEP-RULE");

        var loaded = ProjectInstructions.Load(nested);

        Assert.Contains("ROOT-RULE", loaded);
        Assert.Contains("DEEP-RULE", loaded);
        // Closer-to-cwd appears later in the prompt (slightly higher weight).
        var rootIdx = loaded.IndexOf("ROOT-RULE", StringComparison.Ordinal);
        var deepIdx = loaded.IndexOf("DEEP-RULE", StringComparison.Ordinal);
        Assert.True(rootIdx < deepIdx, $"expected ROOT-RULE before DEEP-RULE; got {rootIdx} vs {deepIdx}");
    }

    [Fact]
    public void Load_NoGitRoot_OnlyCwdConsidered()
    {
        // Workspace WITHOUT a .git marker. Hierarchical walk should
        // collapse to "just cwd" so we don't accidentally load AGENTS.md
        // from a random parent directory.
        var dir = Path.Combine(_tempRoot, "no-git", "child");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(_tempRoot, "no-git", "AGENTS.md"), "PARENT-RULE");
        File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "CHILD-RULE");

        var loaded = ProjectInstructions.Load(dir);

        Assert.Contains("CHILD-RULE", loaded);
        Assert.DoesNotContain("PARENT-RULE", loaded);
    }

    [Fact]
    public void Load_AlwaysApplyFalse_IsSkipped()
    {
        var ws = MakeWorkspace();
        File.WriteAllText(Path.Combine(ws, "AGENTS.md"),
            "---\n" +
            "alwaysApply: false\n" +
            "description: agent-requested only\n" +
            "---\n" +
            "\n" +
            "SHOULD-NOT-APPEAR");
        var loaded = ProjectInstructions.Load(ws);
        Assert.DoesNotContain("SHOULD-NOT-APPEAR", loaded);
    }

    [Fact]
    public void Load_AlwaysApplyTrue_Included_FrontmatterStripped()
    {
        var ws = MakeWorkspace();
        File.WriteAllText(Path.Combine(ws, "AGENTS.md"),
            "---\n" +
            "alwaysApply: true\n" +
            "description: terse style\n" +
            "---\n" +
            "\n" +
            "BODY-CONTENT");
        var loaded = ProjectInstructions.Load(ws);
        Assert.Contains("BODY-CONTENT", loaded);
        // Frontmatter shouldn't leak through verbatim.
        Assert.DoesNotContain("alwaysApply: true", loaded);
        // Description should appear in the source header.
        Assert.Contains("terse style", loaded);
    }

    [Fact]
    public void Load_MissingClosingFence_FallsBackToFullBody()
    {
        var ws = MakeWorkspace();
        // Malformed frontmatter (no closing ---) — preserve the file
        // verbatim rather than swallowing it silently.
        File.WriteAllText(Path.Combine(ws, "AGENTS.md"),
            "---\n" +
            "description: oops\n" +
            "(no closing fence)\n" +
            "REAL-CONTENT");
        var loaded = ProjectInstructions.Load(ws);
        Assert.Contains("REAL-CONTENT", loaded);
    }

    [Fact]
    public void Load_DeDupesUserGlobalUnderHome()
    {
        // When cwd is under fake-home, the ancestor walk would include
        // ~/.vett/AGENTS.md naturally. We then explicitly tack
        // ~/.vett/AGENTS.md on at the end too. The dedup prevents the
        // same content from appearing twice.
        var globalDir = Path.Combine(_fakeHome, ".vett");
        Directory.CreateDirectory(globalDir);
        File.WriteAllText(Path.Combine(globalDir, "AGENTS.md"), "ONCE-ONLY");

        var ws = Path.Combine(_fakeHome, "ws");
        Directory.CreateDirectory(Path.Combine(ws, ".git"));

        var loaded = ProjectInstructions.Load(ws);
        // Count occurrences of ONCE-ONLY — should be exactly one.
        var first = loaded.IndexOf("ONCE-ONLY", StringComparison.Ordinal);
        var second = first >= 0 ? loaded.IndexOf("ONCE-ONLY", first + 1, StringComparison.Ordinal) : -1;
        Assert.True(first >= 0, "user-global rule should be loaded");
        Assert.True(second < 0, "user-global rule should not be loaded twice");
    }

    [Fact]
    public void Apply_WrapsInProjectInstructionsBlock()
    {
        var ws = MakeWorkspace();
        File.WriteAllText(Path.Combine(ws, "AGENTS.md"), "RULES");
        var result = ProjectInstructions.Apply("BASE-PROMPT", ws);
        Assert.StartsWith("<project_instructions>", result);
        Assert.Contains("RULES", result);
        Assert.Contains("</project_instructions>", result);
        Assert.EndsWith("BASE-PROMPT", result);
    }

    [Fact]
    public void Apply_NoFiles_ReturnsBaseUnchanged()
    {
        var ws = MakeWorkspace();
        Assert.Equal("BASE-PROMPT", ProjectInstructions.Apply("BASE-PROMPT", ws));
    }

    [Fact]
    public void ParseFrontmatter_NoFrontmatter_ReturnsEmptyMetaAndOriginal()
    {
        var (meta, body) = ProjectInstructions.ParseFrontmatter("just some markdown\n\nblah");
        Assert.Empty(meta);
        Assert.Equal("just some markdown\n\nblah", body);
    }

    [Fact]
    public void ParseFrontmatter_StripsQuotes()
    {
        var (meta, _) = ProjectInstructions.ParseFrontmatter(
            "---\n" +
            "description: \"a quoted string\"\n" +
            "name: 'single-quoted'\n" +
            "---\n" +
            "body");
        Assert.Equal("a quoted string", meta["description"]);
        Assert.Equal("single-quoted", meta["name"]);
    }

    [Fact]
    public void Load_DotVettDirVariant_AlsoLoaded()
    {
        var ws = MakeWorkspace();
        Directory.CreateDirectory(Path.Combine(ws, ".vett"));
        File.WriteAllText(Path.Combine(ws, ".vett", "AGENTS.md"), "DOT-VETT-RULE");
        var loaded = ProjectInstructions.Load(ws);
        Assert.Contains("DOT-VETT-RULE", loaded);
    }
}
