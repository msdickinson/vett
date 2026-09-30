using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// The leader write-guard turns "You do NOT write code yourself" from a prompt
/// request into a tool boundary. These cases are not hypothetical: the two
/// BLOCKED_* commands below are VERBATIM from bench session logs where a Flash
/// leader bypassed the dispatch protocol (2026-07-13, team-smoke-tier2).
/// </summary>
public class LeaderWriteGuardTests
{
    // ---- must be BLOCKED: real observed leader bypasses -------------------

    [Fact]
    public void Blocks_HandMerge_Of_DispatchWorktree()
    {
        // dsv4-team-local-sizing / t2-2-sequential-pipeline: the leader copied
        // a file straight out of the dispatch worktree instead of calling
        // accept_dispatch. The run PASSED because it cheated.
        Assert.True(LeaderWriteGuard.IsFileWrite(
            @"cp ""C:\Users\dev\.vett\dispatches\team-bench-t2-2\impl-1\src\a.ts"" src/a.ts"));
    }

    [Fact]
    public void Blocks_InPlaceSed()
    {
        // dsv4-team-local-architect / t2-5-long-file-edit: leader edited the
        // file directly rather than dispatching. That run FAILED.
        Assert.True(LeaderWriteGuard.IsFileWrite(
            "sed -i 's/max_connections = 100/max_connections = 250/' app.conf"));
    }

    [Theory]
    [InlineData("cat > f.txt << 'EOF'")]
    [InlineData("echo hello >> log.txt")]
    [InlineData("printf 'x' > src/Money.cs")]
    [InlineData("tee out.txt")]
    [InlineData("rm -rf src/")]
    [InlineData("mv old.cs new.cs")]
    [InlineData("git apply patch.diff")]
    [InlineData("git checkout -- src/Order.cs")]
    public void Blocks_FileWrites(string cmd) =>
        Assert.True(LeaderWriteGuard.IsFileWrite(cmd), cmd);

    // ---- must be ALLOWED: the leader still needs its verify gate ----------

    [Theory]
    [InlineData("dotnet build src/App.csproj")]
    [InlineData("dotnet test tests/App.Tests")]
    [InlineData("dotnet build src/App.csproj 2>&1")]
    [InlineData("cat app.conf 2>/dev/null || echo NOT_FOUND")]
    [InlineData("grep -rn \"TODO\" src/ 2>/dev/null")]
    [InlineData("find src/ -type f -name \"*.cs\" -o -name \"*.ts\"")]
    [InlineData("git ls-files")]
    [InlineData("git ls-files -- '*.csproj'")]
    [InlineData("git status")]
    [InlineData("git diff")]
    [InlineData("ls -la schema.json types.ts 2>&1")]
    public void Allows_ReadsAndBuildGate(string cmd) =>
        Assert.False(LeaderWriteGuard.IsFileWrite(cmd), cmd);

    [Theory]
    // VERBATIM from t2-3-researcher-then-impl (2026-07-13): the leader used the
    // WINDOWS null device (2>nul), the guard read it as "redirect into a file
    // named nul", and blocked a pure read. Leaders run on Windows here — both
    // spellings of "discard this" must pass.
    [InlineData("git ls-files -- '*.csproj' '*.fsproj' '*.vbproj' 2>nul; ls src/ 2>/dev/null || echo \"no src dir\"")]
    [InlineData("dir /b 2>NUL")]
    [InlineData("type app.conf 2>nul")]
    public void Allows_WindowsNullDevice(string cmd) =>
        Assert.False(LeaderWriteGuard.IsFileWrite(cmd), cmd);

    [Theory]
    // ...but a redirect into a real file is still a write, even next to a nul.
    [InlineData("git ls-files 2>nul > out.txt")]
    [InlineData("echo x 2>nul >> log.txt")]
    public void StillBlocks_RealWrite_AlongsideNullDevice(string cmd) =>
        Assert.True(LeaderWriteGuard.IsFileWrite(cmd), cmd);

    // ---- the wrap itself ---------------------------------------------------

    [Fact]
    public async Task Apply_RefusesWrite_AndNeverReachesSandbox()
    {
        var reached = false;
        var tools = new Dictionary<string, ToolFn>
        {
            ["terminal"] = (a, s, id, ct) => { reached = true; return Task.FromResult("ran"); },
        };
        LeaderWriteGuard.Apply(tools);

        var result = await tools["terminal"](
            new Dictionary<string, object?> { ["command"] = "sed -i 's/a/b/' f.cs" },
            null!, "s", default);

        Assert.False(reached);                     // the sandbox never saw it
        Assert.Contains("REJECTED", result);
        Assert.Contains("accept_dispatch", result); // tells it what to do instead
    }

    [Fact]
    public async Task Apply_LetsBuildGateThrough()
    {
        var reached = false;
        var tools = new Dictionary<string, ToolFn>
        {
            ["terminal"] = (a, s, id, ct) => { reached = true; return Task.FromResult("Build succeeded"); },
        };
        LeaderWriteGuard.Apply(tools);

        var result = await tools["terminal"](
            new Dictionary<string, object?> { ["command"] = "dotnet build src/App.csproj" },
            null!, "s", default);

        Assert.True(reached);
        Assert.Equal("Build succeeded", result);
    }
}
