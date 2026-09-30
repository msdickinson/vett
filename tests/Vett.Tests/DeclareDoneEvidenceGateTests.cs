using Vett.Agent;
using Vett.Sandbox;

namespace Vett.Tests;

/// <summary>
/// Evidence gate on declare_done. Shakedown tasks #44-#46 all declared success
/// while the ticket's required file was never modified — at confidence 60, then
/// 95, then 100. On #46 the leader cited the suite's UNTOUCHED baseline counts
/// as proof its implementation worked; it had written no tests, so nothing new
/// ran. Self-report is not evidence.
/// </summary>
[Collection("declare-done-env")]
public class DeclareDoneEvidenceGateTests : IDisposable
{
    private readonly string? _saved = Environment.GetEnvironmentVariable("VETT_REQUIRED_PATHS");

    public void Dispose() => Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", _saved);

    private static void SetRequired(string? v) =>
        Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", v);

    /// <summary>Sandbox whose `git status --porcelain` returns a canned answer,
    /// and which optionally fails any other command (i.e. the verify gate).
    ///
    /// <paramref name="prefix"/> is what `git rev-parse --show-prefix` answers:
    /// the cwd's offset below the repo toplevel. It is a SEPARATE branch from
    /// the verify gate on purpose — several tests fail every other command to
    /// exercise the verify path, and the prefix must not be collateral.</summary>
    private sealed class FakeSandbox(
        string porcelain, int exitCode = 0,
        int verifyExit = 0, string verifyOut = "", bool verifyTimedOut = false,
        string prefix = "") : ISandbox
    {
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
        {
            if (cmd.Contains("git status"))
                return Task.FromResult(new BashResult(porcelain, exitCode, "/fake", false));
            if (cmd.Contains("rev-parse"))
                return Task.FromResult(new BashResult(prefix, 0, "/fake", false));
            return Task.FromResult(new BashResult(verifyOut, verifyExit, "/fake", verifyTimedOut));
        }
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

    [Fact]
    public void RequiredPaths_Unset_IsEmpty_SoBehaviorIsUnchanged()
    {
        SetRequired(null);
        Assert.Empty(LeaderTools.RequiredPaths());
    }

    [Theory]
    [InlineData("a.cs;b.cs")]
    [InlineData("a.cs,b.cs")]
    [InlineData("a.cs b.cs")]
    [InlineData("  a.cs \n b.cs  ")]
    public void RequiredPaths_ParsesAllSeparators(string raw)
    {
        SetRequired(raw);
        Assert.Equal(["a.cs", "b.cs"], LeaderTools.RequiredPaths());
    }

    /// <summary>
    /// ⚠ THIS FIXTURE HAS TWO ROOTS, and that is not a mistake in it — it is
    /// what a real monorepo task looks like. Porcelain paths are relative to
    /// the REPO TOPLEVEL (`apps/static-analysis/src/...`) even though vett ran
    /// inside the project; the ticket's required paths are relative to the
    /// PROJECT (`src/...`). The old gate matched them by testing the required
    /// path as a SUBSTRING of the whole porcelain blob, which bridged the two
    /// roots BY ACCIDENT while also accepting `Money.cs.orig` as evidence for
    /// `Money.cs`. Killing the substring match without resolving the prefix
    /// would have turned every monorepo ticket's paths into false MISSING —
    /// which is exactly how this test caught it, by going red.
    ///
    /// So the fixture now also answers `rev-parse --show-prefix`, as a real
    /// sandbox in that cwd would.
    /// </summary>
    [Fact]
    public async Task Untouched_TheRealTask46_ReportsOnlyPhase1Extractor()
    {
        // Exactly what #46 left behind: Ir.cs modified, fixtures untracked,
        // Phase1Extractor.cs never touched.
        var porcelain =
            " M apps/static-analysis/src/StaticAnalysis.Core/Ir.cs\n" +
            "?? apps/static-analysis/tests/fixtures/24-loops-for-while/\n" +
            "?? apps/static-analysis/tests/fixtures/25-lock-branch-declarators/\n";
        List<string> required =
        [
            "src/StaticAnalysis.CSharp/Phase1Extractor.cs",
            "src/StaticAnalysis.Core/Ir.cs",
        ];

        var untouched = await LeaderTools.UntouchedAsync(
            new FakeSandbox(porcelain, prefix: "apps/static-analysis/"), "s", required, default);

        Assert.Equal(["src/StaticAnalysis.CSharp/Phase1Extractor.cs"], untouched);
    }

    /// <summary>
    /// The control for the test above: with NO prefix resolved, the same
    /// project-relative paths are reported MISSING. That is the direction an
    /// unresolvable prefix fails in — a visible refusal naming both paths,
    /// never a silent acceptance.
    /// </summary>
    [Fact]
    public async Task Untouched_WithNoRepoPrefix_ProjectRelativePathsAreReportedMissing()
    {
        var porcelain = " M apps/static-analysis/src/StaticAnalysis.Core/Ir.cs\n";
        List<string> required =
        [
            "src/StaticAnalysis.CSharp/Phase1Extractor.cs",
            "src/StaticAnalysis.Core/Ir.cs",
        ];

        var untouched = await LeaderTools.UntouchedAsync(
            new FakeSandbox(porcelain), "s", required, default);

        Assert.Equal(required, untouched);
    }

    [Fact]
    public async Task Untouched_UntrackedNewFile_CountsAsWork()
    {
        // A brand-new file never appears in `git diff`; it is still real work.
        var untouched = await LeaderTools.UntouchedAsync(
            new FakeSandbox("?? src/New.cs\n"), "s", ["src/New.cs"], default);
        Assert.Empty(untouched);
    }

    [Fact]
    public async Task Untouched_AllModified_IsEmpty()
    {
        var porcelain = " M src/A.cs\n M src/B.cs\n";
        var untouched = await LeaderTools.UntouchedAsync(
            new FakeSandbox(porcelain), "s", ["src/A.cs", "src/B.cs"], default);
        Assert.Empty(untouched);
    }

    [Fact]
    public async Task Untouched_GitFails_FailsOpen()
    {
        // Non-git workspace must never wedge a run.
        var untouched = await LeaderTools.UntouchedAsync(
            new FakeSandbox("", exitCode: 128), "s", ["src/A.cs"], default);
        Assert.Empty(untouched);
    }

    [Fact]
    public async Task Untouched_NormalizesWindowsSeparators()
    {
        var untouched = await LeaderTools.UntouchedAsync(
            new FakeSandbox(" M src/A.cs\n"), "s", [@"src\A.cs"], default);
        Assert.Empty(untouched);
    }

    [Fact]
    public async Task DeclareDone_RefusesWhileRequiredPathUnchanged_ThenRelentsAfterCap()
    {
        SetRequired("src/StaticAnalysis.CSharp/Phase1Extractor.cs");
        var board = new TaskBoard();
        var sandbox = new FakeSandbox(" M src/StaticAnalysis.Core/Ir.cs\n");
        var tools = LeaderTools.Create(board, (_, _, _, _) => Task.FromResult(""));
        var args = new Dictionary<string, object?> { ["summary"] = "All tests pass (228/228)." };

        // First ten attempts are refused with an actionable message...
        // (10, not 3: on task #50 every refusal drove a real correction but
        // the budget ran out mid-convergence and a red tree failed open.)
        for (var i = 1; i <= 10; i++)
        {
            var r = await tools["declare_done"](args, sandbox, "s", default);
            Assert.StartsWith("Error: cannot declare done", r);
            Assert.Contains("Phase1Extractor.cs", r);
            Assert.Contains($"refusal {i}/10", r);
            Assert.DoesNotContain(Vett.Tools.Builtins.SubmitMarker, r);
        }

        // ...then the cap relents so a stuck run still terminates — but the
        // submission is TAGGED as unverified so the worker (and any human
        // reading the report) can escalate it instead of trusting it. #50
        // landed a 16-error tree as done conf=90 through an untagged path.
        var final = await tools["declare_done"](args, sandbox, "s", default);
        Assert.Contains(Vett.Tools.Builtins.SubmitMarker, final);
        Assert.Contains("gate-failed-open", final);
    }

    [Fact]
    public async Task DeclareDone_AllowsWhenRequiredPathChanged()
    {
        SetRequired("src/StaticAnalysis.CSharp/Phase1Extractor.cs");
        var tools = LeaderTools.Create(new TaskBoard(), (_, _, _, _) => Task.FromResult(""));
        var sandbox = new FakeSandbox(" M src/StaticAnalysis.CSharp/Phase1Extractor.cs\n");

        var r = await tools["declare_done"](
            new Dictionary<string, object?> { ["summary"] = "done" }, sandbox, "s", default);

        Assert.Contains(Vett.Tools.Builtins.SubmitMarker, r);
    }

    [Fact]
    public async Task DeclareDone_TheRealTask47_PathsTouchedButBuildBroken_IsRefused()
    {
        // #47 modified every required path and left 7 compiler errors behind.
        // Touching a file is necessary, not sufficient.
        SetRequired("src/StaticAnalysis.CSharp/Phase1Extractor.cs");
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", "./verify.sh");
        try
        {
            var tools = LeaderTools.Create(new TaskBoard(), (_, _, _, _) => Task.FromResult(""));
            var sandbox = new FakeSandbox(
                " M src/StaticAnalysis.CSharp/Phase1Extractor.cs\n",
                verifyExit: 1,
                verifyOut: "Phase1Extractor.cs(582,51): error CS1061: 'IOperation' does not contain...");

            var r = await tools["declare_done"](
                new Dictionary<string, object?> { ["summary"] = "Successfully completed ACT Task 1." },
                sandbox, "s", default);

            Assert.StartsWith("Error: cannot declare done", r);
            Assert.Contains("./verify.sh", r);
            Assert.Contains("exit 1", r);
            Assert.Contains("CS1061", r);              // the actual error reaches the leader
            Assert.Contains("Do not edit tests", r);
            Assert.DoesNotContain(Vett.Tools.Builtins.SubmitMarker, r);
        }
        finally { Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", null); }
    }

    [Fact]
    public async Task DeclareDone_VerifyGreen_AndPathsTouched_IsAllowed()
    {
        SetRequired("src/A.cs");
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", "./verify.sh");
        try
        {
            var tools = LeaderTools.Create(new TaskBoard(), (_, _, _, _) => Task.FromResult(""));
            var sandbox = new FakeSandbox(" M src/A.cs\n", verifyExit: 0, verifyOut: "[verify] GREEN");
            var r = await tools["declare_done"](
                new Dictionary<string, object?> { ["summary"] = "done" }, sandbox, "s", default);
            Assert.Contains(Vett.Tools.Builtins.SubmitMarker, r);
        }
        finally { Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", null); }
    }

    [Fact]
    public async Task DeclareDone_VerifyTimesOut_IsRefused()
    {
        SetRequired("src/A.cs");
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", "./verify.sh");
        try
        {
            var tools = LeaderTools.Create(new TaskBoard(), (_, _, _, _) => Task.FromResult(""));
            var sandbox = new FakeSandbox(" M src/A.cs\n", verifyExit: 0, verifyTimedOut: true);
            var r = await tools["declare_done"](
                new Dictionary<string, object?> { ["summary"] = "done" }, sandbox, "s", default);
            Assert.Contains("timed out", r);
            Assert.DoesNotContain(Vett.Tools.Builtins.SubmitMarker, r);
        }
        finally { Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", null); }
    }

    [Fact]
    public async Task DeclareDone_NoVerifyCmd_SkipsGate_PathsAloneSuffice()
    {
        SetRequired("src/A.cs");
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", null);
        var tools = LeaderTools.Create(new TaskBoard(), (_, _, _, _) => Task.FromResult(""));
        // verifyExit 1 would refuse if the gate ran; it must not run.
        var sandbox = new FakeSandbox(" M src/A.cs\n", verifyExit: 1);
        var r = await tools["declare_done"](
            new Dictionary<string, object?> { ["summary"] = "done" }, sandbox, "s", default);
        Assert.Contains(Vett.Tools.Builtins.SubmitMarker, r);
    }

    [Fact]
    public void WithRequiredFiles_Unset_LeavesTaskAlone()
    {
        SetRequired(null);
        Assert.Equal("do the thing", LeaderTools.WithRequiredFiles("do the thing"));
    }

    [Fact]
    public void WithRequiredFiles_TheRealTask48_TellsTheMemberWhatTheLeaderNeverDid()
    {
        // #48: 8 dispatches, the implementer only ever edited Ir.cs, and the
        // leader rejected them with "diff showing only Ir.cs changes".
        SetRequired("src/StaticAnalysis.CSharp/Phase1Extractor.cs;src/StaticAnalysis.Core/Ir.cs");
        var dispatched = LeaderTools.WithRequiredFiles("Add the IR records for the loop nodes.");

        Assert.Contains("REQUIRED FILES", dispatched);
        Assert.Contains("src/StaticAnalysis.CSharp/Phase1Extractor.cs", dispatched);
        Assert.Contains("is not a substitute", dispatched);
        Assert.Contains("git status", dispatched);
    }

    [Fact]
    public void WithRequiredFiles_TaskAlreadyNamesEveryPath_IsNotAppended()
    {
        SetRequired("src/A.cs");
        var task = "Edit src/A.cs and add the case.";
        Assert.Equal(task, LeaderTools.WithRequiredFiles(task));
    }

    [Fact]
    public void WithRequiredFiles_PartiallyNamed_StillAppendsTheFullList()
    {
        SetRequired("src/A.cs;src/B.cs");
        var dispatched = LeaderTools.WithRequiredFiles("Edit src/A.cs only.");
        Assert.Contains("REQUIRED FILES", dispatched);
        Assert.Contains("src/B.cs", dispatched);
    }

    [Fact]
    public void WithRequiredFiles_EmptyTask_IsUntouched()
    {
        SetRequired("src/A.cs");
        Assert.Equal("", LeaderTools.WithRequiredFiles(""));
    }

    [Fact]
    public void WithRequiredFiles_MatchesAcrossSeparatorStyles()
    {
        SetRequired(@"src\A.cs");
        Assert.Equal("Edit src/A.cs", LeaderTools.WithRequiredFiles("Edit src/A.cs"));
    }

    [Fact]
    public async Task DeclareDone_Unconfigured_IsUnchanged()
    {
        SetRequired(null);
        var tools = LeaderTools.Create(new TaskBoard(), (_, _, _, _) => Task.FromResult(""));
        // Sandbox would throw if consulted — proving the gate short-circuits.
        var r = await tools["declare_done"](
            new Dictionary<string, object?> { ["summary"] = "done" },
            new FakeSandbox("", exitCode: 128), "s", default);

        Assert.Contains(Vett.Tools.Builtins.SubmitMarker, r);
    }
}
