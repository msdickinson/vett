using Vett.Agent;
using Vett.Sandbox;

namespace Vett.Tests;

/// <summary>
/// Content-level evidence gate (`VETT_REQUIRED_CONTENT` / `expect-content:`).
///
/// Split-ticket runs #53 and #54 both landed `done` conf=95 with every
/// required FILE modified and the gate suite green — but the asked-for
/// LowerStatement switch cases absent from the diff. The model reliably
/// edits the small file (records, registrations) and skips the hard
/// mid-file cases; a path check cannot tell the difference. When the
/// ticket names content, the diff against HEAD must actually contain it.
///
/// Same collection as DeclareDoneEvidenceGateTests: both mutate process
/// env vars that declare_done reads, so they must not run concurrently.
/// </summary>
[Collection("declare-done-env")]
public class DeclareDoneContentGateTests : IDisposable
{
    private readonly string? _savedPaths = Environment.GetEnvironmentVariable("VETT_REQUIRED_PATHS");
    private readonly string? _savedContent = Environment.GetEnvironmentVariable("VETT_REQUIRED_CONTENT");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", _savedPaths);
        Environment.SetEnvironmentVariable("VETT_REQUIRED_CONTENT", _savedContent);
    }

    private static void Set(string? paths, string? content)
    {
        Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", paths);
        Environment.SetEnvironmentVariable("VETT_REQUIRED_CONTENT", content);
    }

    /// <summary>Routes git status / git diff / everything-else (verify)
    /// to separate canned results.</summary>
    private sealed class RoutedSandbox(
        string porcelain = "", string diff = "", int verifyExit = 0) : ISandbox
    {
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(
                cmd.Contains("git status") ? new BashResult(porcelain, 0, "/fake", false)
                : cmd.Contains("git diff") ? new BashResult(diff, 0, "/fake", false)
                : new BashResult("", verifyExit, "/fake", false));
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

    private static Dictionary<string, Vett.Tools.ToolFn> Tools() =>
        LeaderTools.Create(new TaskBoard(), (_, _, _, _) => Task.FromResult(""));

    private static readonly Dictionary<string, object?> Args =
        new() { ["summary"] = "All done." };

    [Fact]
    public void RequiredContent_Unset_IsEmpty()
    {
        Set(null, null);
        Assert.Empty(LeaderTools.RequiredContent());
    }

    [Fact]
    public void RequiredContent_ParsesSemicolons_CaseSensitive_Deduped()
    {
        Set(null, "IForLoopOperation; ILockOperation ;IForLoopOperation;");
        Assert.Equal(["IForLoopOperation", "ILockOperation"], LeaderTools.RequiredContent());
    }

    [Fact]
    public async Task MissingContent_TheRealTask54_CasesAbsent_RecordsPresent()
    {
        // What #54 actually shipped: registrations + records + a helper,
        // none of the four switch cases.
        var diff =
            "+[JsonDerivedType(typeof(LockStatementIr), \"lock\")]\n" +
            "+public sealed record LockStatementIr(\n" +
            "+    private StatementIr LowerBlock(IBlockOperation block)\n";
        var missing = await LeaderTools.MissingContentAsync(
            new RoutedSandbox(diff: diff), "s",
            ["ILockOperation", "IBranchOperation", "LockStatementIr"], default);

        Assert.Equal(["ILockOperation", "IBranchOperation"], missing);
    }

    [Fact]
    public async Task DeclareDone_RefusesWhileContentAbsent_NamesTheMissingPieces()
    {
        Set(null, "IForLoopOperation;ILockOperation");
        var tools = Tools();
        var r = await tools["declare_done"](Args,
            new RoutedSandbox(diff: "+case IForLoopOperation forLoop:\n"), "s", default);

        Assert.StartsWith("Error: cannot declare done", r);
        Assert.Contains("ILockOperation", r);
        Assert.DoesNotContain("IForLoopOperation,", r);   // present piece not re-demanded
        Assert.DoesNotContain(Vett.Tools.Builtins.SubmitMarker, r);
    }

    [Fact]
    public async Task DeclareDone_ContentPresent_Submits()
    {
        Set(null, "IForLoopOperation");
        var tools = Tools();
        var r = await tools["declare_done"](Args,
            new RoutedSandbox(diff: "+                case IForLoopOperation forLoop:\n"), "s", default);

        Assert.Contains(Vett.Tools.Builtins.SubmitMarker, r);
    }

    [Fact]
    public async Task DeclareDone_ContentOnly_NoPaths_GateStillArms()
    {
        // A ticket may pin content without pinning paths; the early-return
        // must not skip the content check.
        Set(null, "IBranchOperation");
        var tools = Tools();
        var r = await tools["declare_done"](Args, new RoutedSandbox(diff: ""), "s", default);

        Assert.StartsWith("Error: cannot declare done", r);
        Assert.Contains("IBranchOperation", r);
    }

    [Fact]
    public async Task DeclareDone_PathsAndContent_PathCheckedFirst()
    {
        Set("src/A.cs", "IBranchOperation");
        var tools = Tools();
        // Nothing modified at all: path refusal wins (its message names files).
        var r = await tools["declare_done"](Args, new RoutedSandbox(porcelain: ""), "s", default);

        Assert.Contains("unchanged: src/A.cs", r);
    }

    [Fact]
    public async Task DeclareDone_GitDiffFails_FailsOpen()
    {
        Set(null, "IBranchOperation");
        var missing = await LeaderTools.MissingContentAsync(
            new FailingSandbox(), "s", ["IBranchOperation"], default);
        Assert.Empty(missing);
    }

    private sealed class FailingSandbox : ISandbox
    {
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("fatal: not a git repository", 128, "/fake", false));
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

    // ── ONLY ADDED LINES COUNT ────────────────────────────────────────────
    // Every fixture above hands the gate a diff of '+' lines, so all of them
    // passed both before and after the fix — the suite had ZERO POWER over the
    // defect it was supposed to cover. A green control proves only the case it
    // constructs. These construct the cases it never did.

    [Fact]
    public async Task MissingContent_RequiredCodeWasDELETED_IsMissing_NotSatisfied()
    {
        // The substring is in the diff — on a '-' line. A raw
        // `diff.Contains(...)` scored this PASS, so a ticket saying "use
        // banker's rounding" was satisfied by an agent DELETING it. The
        // gate certified the exact opposite of what was asked.
        var diff =
            "--- a/src/Money.cs\n" +
            "+++ b/src/Money.cs\n" +
            "@@ -12,7 +12,7 @@\n" +
            "-        return Math.Round(v, 2, MidpointRounding.ToEven);\n" +
            "+        return Math.Round(v, 2);\n";
        var missing = await LeaderTools.MissingContentAsync(
            new RoutedSandbox(diff: diff), "s", ["MidpointRounding.ToEven"], default);

        Assert.Equal(["MidpointRounding.ToEven"], missing);
    }

    [Fact]
    public async Task MissingContent_RequiredCodeIsOnlyUNCHANGEDCONTEXT_IsMissing()
    {
        // Pre-existing code that merely sits in the context window near an
        // unrelated edit. Nothing was done to it; the ticket is not satisfied.
        var diff =
            "--- a/src/Money.cs\n" +
            "+++ b/src/Money.cs\n" +
            "@@ -10,6 +10,7 @@\n" +
            "     public decimal Rate { get; init; }\n" +
            "         return Math.Round(v, 2, MidpointRounding.ToEven);\n" +
            "+    public string Note { get; init; } = \"\";\n";
        var missing = await LeaderTools.MissingContentAsync(
            new RoutedSandbox(diff: diff), "s", ["MidpointRounding.ToEven"], default);

        Assert.Equal(["MidpointRounding.ToEven"], missing);
    }

    [Fact]
    public async Task MissingContent_SubstringOnlyInFileHeaderPath_IsMissing()
    {
        // `+++ b/src/ILockOperation.cs` starts with '+' but is git's header,
        // not content. Naming the type in the FILENAME must not satisfy a
        // ticket that asked for the type to be USED.
        var diff =
            "--- a/src/ILockOperation.cs\n" +
            "+++ b/src/ILockOperation.cs\n" +
            "@@ -1,2 +1,3 @@\n" +
            "+// unrelated comment\n";
        var missing = await LeaderTools.MissingContentAsync(
            new RoutedSandbox(diff: diff), "s", ["ILockOperation"], default);

        Assert.Equal(["ILockOperation"], missing);
    }

    [Fact]
    public async Task MissingContent_AddedLineStartingWithPlusPlus_StillCounts()
    {
        // Guards the fix against over-correcting: source text `++counter;` at
        // column 0 becomes the diff line "+++counter;". It is a real addition,
        // and filtering on a bare "+++" prefix would silently refuse it.
        var missing = await LeaderTools.MissingContentAsync(
            new RoutedSandbox(diff: "+++ b/src/C.cs\n+++counter;\n"), "s",
            ["++counter;"], default);

        Assert.Empty(missing);
    }

    [Fact]
    public async Task DeclareDone_RefusesWhenRequiredContentWasDeleted()
    {
        // End-to-end through the tool the leader actually calls, not just the
        // helper: the refusal must reach the model, naming the piece.
        Set(null, "MidpointRounding.ToEven");
        var tools = Tools();
        var r = await tools["declare_done"](Args,
            new RoutedSandbox(diff: "-  Math.Round(v, 2, MidpointRounding.ToEven);\n"), "s", default);

        Assert.StartsWith("Error: cannot declare done", r);
        Assert.Contains("MidpointRounding.ToEven", r);
        Assert.DoesNotContain(Vett.Tools.Builtins.SubmitMarker, r);
    }

    [Fact]
    public void WithRequiredFiles_InjectsContentBlock_WhenTaskOmitsIt()
    {
        Set(null, "IForLoopOperation;ILockOperation");
        var injected = LeaderTools.WithRequiredFiles("Add the missing switch cases.");

        Assert.Contains("REQUIRED CONTENT", injected);
        Assert.Contains("IForLoopOperation", injected);
        Assert.Contains("ILockOperation", injected);
    }

    [Fact]
    public void WithRequiredFiles_SkipsContentBlock_WhenTaskAlreadyNamesIt()
    {
        Set(null, "IForLoopOperation");
        var task = "Add the IForLoopOperation case to LowerStatement.";
        Assert.Equal(task, LeaderTools.WithRequiredFiles(task));
    }
}
