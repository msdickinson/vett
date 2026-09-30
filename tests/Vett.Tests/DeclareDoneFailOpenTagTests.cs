using Vett.Agent;
using Vett.Sandbox;

namespace Vett.Tests;

/// <summary>
/// The two `declare_done` evidence gates fail OPEN — deliberately, so a
/// non-git workspace can never wedge a run — but until this suite they did it
/// SILENTLY. An unmeasured gate and a satisfied gate both returned <c>[]</c>,
/// so the submission came back with no marker at all and a run that proved
/// nothing was filed as evidence-verified.
///
/// Two separate defects are covered here:
///
///  1. NOT TAGGED. The same file's refusal budget already fails open and says
///     so (`[gate-failed-open: ... refusal budget exhausted]`); these two gates
///     took the same exit untagged.
///  2. `TimedOut` CAPTURED BUT NEVER GATED ON. `BashResult.TimedOut` was
///     populated, and consulted for VETT_VERIFY_CMD a few dozen lines away,
///     but neither gate read it. A timeout also sets ExitCode -1, so it landed
///     in the git-failed branch and read as "not a git repo" — and the timeout
///     case fires preferentially on the biggest, slowest, genuinely-hardest
///     worktrees, i.e. exactly where the evidence matters most.
///
/// Every fail-open assertion here is paired with a HEALTHY control asserting
/// the tag is ABSENT. A tag that is always emitted would carry no information;
/// the untagged half is what makes the tagged half mean something.
///
/// Same collection as the other declare_done suites: all three mutate the
/// process env vars the tool reads, so they must not run concurrently.
/// </summary>
[Collection("declare-done-env")]
public class DeclareDoneFailOpenTagTests : IDisposable
{
    private readonly string? _savedPaths = Environment.GetEnvironmentVariable("VETT_REQUIRED_PATHS");
    private readonly string? _savedContent = Environment.GetEnvironmentVariable("VETT_REQUIRED_CONTENT");
    private readonly string? _savedVerify = Environment.GetEnvironmentVariable("VETT_VERIFY_CMD");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", _savedPaths);
        Environment.SetEnvironmentVariable("VETT_REQUIRED_CONTENT", _savedContent);
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", _savedVerify);
    }

    private static void Set(string? paths, string? content, string? verify = null)
    {
        Environment.SetEnvironmentVariable("VETT_REQUIRED_PATHS", paths);
        Environment.SetEnvironmentVariable("VETT_REQUIRED_CONTENT", content);
        Environment.SetEnvironmentVariable("VETT_VERIFY_CMD", verify);
    }

    /// <summary>
    /// Routes `git status` / `git diff` / everything-else to independently
    /// controllable results, so a run can have one blind gate and one healthy
    /// one — which is what actually happens when only the diff is expensive.
    /// </summary>
    private sealed class SickSandbox : ISandbox
    {
        public BashResult Status = new("", 0, "/fake", false);
        public BashResult Diff = new("", 0, "/fake", false);
        public BashResult Verify = new("", 0, "/fake", false);
        /// <summary>When set, thrown instead of answering — the catch path.</summary>
        public Exception? Throw;

        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
        {
            if (Throw is not null) throw Throw;
            return Task.FromResult(
                cmd.Contains("git status") ? Status
                : cmd.Contains("git diff") ? Diff
                : Verify);
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

    private static Dictionary<string, Vett.Tools.ToolFn> Tools() =>
        LeaderTools.Create(new TaskBoard(), (_, _, _, _) => Task.FromResult(""));

    private static readonly Dictionary<string, object?> Args =
        new() { ["summary"] = "All tests pass (228/228)." };

    private const string Tag = "gate-failed-open";

    // ---- unit level: the gate reports WHETHER IT COULD MEASURE ----

    [Fact]
    public async Task Untouched_GitStatusTimesOut_IsBlind_NotSatisfied()
    {
        // ExitCode 0 here on purpose: it isolates the new conjunct. If only
        // ExitCode were read — the pre-fix behaviour — this returns "nothing
        // missing" and the run is filed as verified.
        var sb = new SickSandbox { Status = new BashResult("", 0, "/fake", true) };
        var (missing, blind) = await LeaderTools.UntouchedDetailedAsync(sb, "s", ["src/A.cs"], default);

        Assert.Empty(missing);                       // still fails OPEN, by design
        Assert.NotNull(blind);
        Assert.Equal("required-paths", blind!.Gate);
        Assert.Contains("timed out", blind.Reason);
    }

    [Fact]
    public async Task Untouched_RealTimeout_ReadsAsTIMEOUT_NotAsNotAGitRepo()
    {
        // What the real sandbox produces: a timeout ALSO sets ExitCode -1
        // (DirectBash.cs:167-169). Precedence must put the actionable reading
        // first, or every timeout is misreported as a missing git repo.
        var sb = new SickSandbox { Status = new BashResult("", -1, "/fake", true) };
        var (_, blind) = await LeaderTools.UntouchedDetailedAsync(sb, "s", ["src/A.cs"], default);

        Assert.NotNull(blind);
        Assert.Contains("timed out", blind!.Reason);
        Assert.DoesNotContain("not a git repo", blind.Reason);
    }

    [Fact]
    public async Task Untouched_NonGitWorkspace_IsBlind_AndSaysSo()
    {
        var sb = new SickSandbox { Status = new BashResult("", 128, "/fake", false) };
        var (missing, blind) = await LeaderTools.UntouchedDetailedAsync(sb, "s", ["src/A.cs"], default);

        Assert.Empty(missing);
        Assert.NotNull(blind);
        Assert.Contains("128", blind!.Reason);
    }

    [Fact]
    public async Task Untouched_HealthyGit_IsNOTBlind()
    {
        // The control. Empty-and-measured must be distinguishable from
        // empty-and-blind, or the tag above proves nothing.
        var sb = new SickSandbox { Status = new BashResult(" M src/A.cs\n", 0, "/fake", false) };
        var (missing, blind) = await LeaderTools.UntouchedDetailedAsync(sb, "s", ["src/A.cs"], default);

        Assert.Empty(missing);
        Assert.Null(blind);
    }

    [Fact]
    public async Task Untouched_HealthyGit_ButPathUnchanged_IsNOTBlind_ItIsMISSING()
    {
        // The other side of the control: a real refusal is not a fail-open.
        var sb = new SickSandbox { Status = new BashResult(" M src/B.cs\n", 0, "/fake", false) };
        var (missing, blind) = await LeaderTools.UntouchedDetailedAsync(sb, "s", ["src/A.cs"], default);

        Assert.Equal(["src/A.cs"], missing);
        Assert.Null(blind);
    }

    [Fact]
    public async Task Untouched_SandboxThrows_IsBlind_NotCrashed()
    {
        var sb = new SickSandbox { Throw = new IOException("pipe closed") };
        var (missing, blind) = await LeaderTools.UntouchedDetailedAsync(sb, "s", ["src/A.cs"], default);

        Assert.Empty(missing);
        Assert.NotNull(blind);
        Assert.Contains("IOException", blind!.Reason);
        Assert.Contains("pipe closed", blind.Reason);
    }

    [Fact]
    public async Task MissingContent_GitDiffTimesOut_IsBlind_NotSatisfied()
    {
        var sb = new SickSandbox { Diff = new BashResult("", 0, "/fake", true) };
        var (missing, blind) = await LeaderTools.MissingContentDetailedAsync(sb, "s", ["ILockOperation"], default);

        Assert.Empty(missing);
        Assert.NotNull(blind);
        Assert.Equal("expect-content", blind!.Gate);
        Assert.Contains("timed out", blind.Reason);
    }

    [Fact]
    public async Task MissingContent_GitDiffFails_IsBlind_AndSaysSo()
    {
        var sb = new SickSandbox { Diff = new BashResult("", 128, "/fake", false) };
        var (missing, blind) = await LeaderTools.MissingContentDetailedAsync(sb, "s", ["ILockOperation"], default);

        Assert.Empty(missing);
        Assert.NotNull(blind);
        Assert.Contains("128", blind!.Reason);
    }

    [Fact]
    public async Task MissingContent_HealthyDiff_IsNOTBlind()
    {
        var sb = new SickSandbox { Diff = new BashResult("+case ILockOperation op:\n", 0, "/fake", false) };
        var (missing, blind) = await LeaderTools.MissingContentDetailedAsync(sb, "s", ["ILockOperation"], default);

        Assert.Empty(missing);
        Assert.Null(blind);
    }

    // ---- end to end: what the leader actually receives ----

    [Fact]
    public async Task DeclareDone_PathGateTimedOut_SubmitsTAGGED()
    {
        Set("src/A.cs", null);
        var sb = new SickSandbox { Status = new BashResult("", -1, "/fake", true) };

        var r = await Tools()["declare_done"](Args, sb, "s", default);

        // It still submits — wedging is worse than an unverified pass...
        Assert.Contains(Vett.Tools.Builtins.SubmitMarker, r);
        // ...but the reader can now tell the difference.
        Assert.Contains(Tag, r);
        Assert.Contains("required-paths", r);
        Assert.Contains("timed out", r);
        Assert.Contains("NOT verified", r);
    }

    [Fact]
    public async Task DeclareDone_ContentGateBlind_SubmitsTAGGED()
    {
        Set("src/A.cs", "ILockOperation");
        var sb = new SickSandbox
        {
            Status = new BashResult(" M src/A.cs\n", 0, "/fake", false),   // healthy
            Diff = new BashResult("", 128, "/fake", false),                // blind
        };

        var r = await Tools()["declare_done"](Args, sb, "s", default);

        Assert.Contains(Vett.Tools.Builtins.SubmitMarker, r);
        Assert.Contains(Tag, r);
        Assert.Contains("expect-content", r);
        // The healthy gate must not be slandered as blind.
        Assert.DoesNotContain("required-paths", r);
    }

    [Fact]
    public async Task DeclareDone_BothGatesBlind_TagNamesBOTH()
    {
        // A refusal names the first blocker; a fail-open tag must name every
        // gate that could not measure, or a reader repairs one and re-runs
        // into the other.
        Set("src/A.cs", "ILockOperation");
        var sb = new SickSandbox
        {
            Status = new BashResult("", 128, "/fake", false),
            Diff = new BashResult("", 0, "/fake", true),
        };

        var r = await Tools()["declare_done"](Args, sb, "s", default);

        Assert.Contains(Vett.Tools.Builtins.SubmitMarker, r);
        Assert.Contains("required-paths", r);
        Assert.Contains("expect-content", r);
    }

    [Fact]
    public async Task DeclareDone_BothGatesHEALTHY_SubmitsUNTAGGED()
    {
        // ★ The load-bearing control. An unconditional tag would pass every
        // assertion above and destroy the marker's meaning; this is the test
        // that fails if the tag stops discriminating.
        Set("src/A.cs", "ILockOperation");
        var sb = new SickSandbox
        {
            Status = new BashResult(" M src/A.cs\n", 0, "/fake", false),
            Diff = new BashResult("+case ILockOperation op:\n", 0, "/fake", false),
        };

        var r = await Tools()["declare_done"](Args, sb, "s", default);

        Assert.Contains(Vett.Tools.Builtins.SubmitMarker, r);
        Assert.DoesNotContain(Tag, r);
    }

    [Fact]
    public async Task DeclareDone_GateBlind_ButVerifyCmdRED_StillREFUSES()
    {
        // Precedence: a gate that could not measure must never soften a gate
        // that measured a FAILURE. Tagged-pass is for the unmeasured case
        // only — a red verify is a refusal no matter what else went blind.
        Set("src/A.cs", null, verify: "./verify.sh");
        var sb = new SickSandbox
        {
            Status = new BashResult("", 128, "/fake", false),               // blind
            Verify = new BashResult("error CS1061", 1, "/fake", false),     // measured RED
        };

        var r = await Tools()["declare_done"](Args, sb, "s", default);

        Assert.StartsWith("Error: cannot declare done", r);
        Assert.DoesNotContain(Vett.Tools.Builtins.SubmitMarker, r);
    }

    [Fact]
    public async Task DeclareDone_NothingRequired_IsUNTAGGED_EvenInANonGitWorkspace()
    {
        // No gate was asked for, so no gate went blind. Tagging here would
        // mark every unconfigured run unverified and train readers to ignore
        // the marker.
        Set(null, null);
        var sb = new SickSandbox { Status = new BashResult("", 128, "/fake", false) };

        var r = await Tools()["declare_done"](Args, sb, "s", default);

        Assert.Contains(Vett.Tools.Builtins.SubmitMarker, r);
        Assert.DoesNotContain(Tag, r);
    }
}
