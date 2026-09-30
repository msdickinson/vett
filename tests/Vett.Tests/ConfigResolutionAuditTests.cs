using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// CONFIG RESOLUTION GATES (audit 2026-08-24).
///
/// Three config-resolution defects were "confirmed but unresolved" by a prior
/// audit and lived only in prose. ADOPTING A LAW IS NOT INSTALLING IT — a
/// resolution rule that exists only in a doc has zero power over the next
/// edit. These are the checks at the point of use.
///
///   (1) max_iterations has THREE different fallbacks depending on role.
///       A team LEADER that omits team.leader.max_iterations does NOT
///       inherit the profile's top-level max_iterations — it silently gets a
///       hard-coded 50. dsv4-relay-local.yaml's own comment records the same
///       thing being discovered the expensive way ("Task #49 proved that
///       empirically: with 200 here, the run still died at exactly 25").
///       These tests measure the EFFECTIVE cap by running the real loop to
///       exhaustion against an offline scripted client, so the number is
///       observed, not read off the source.
///
///   (2) AgentEnvironment.Profile is null at 8 of 10 src construction sites.
///       Pinned here as a COUNT so the ratio cannot drift silently in either
///       direction — a new un-threaded site, or a fix, both trip it.
///
///   (3) Profile-path resolution follows HOME ?? USERPROFILE, while the
///       dispatch/session stores use GetFolderPath(UserProfile), which does
///       NOT follow either env var. Redirecting HOME therefore moves the
///       RULER without moving the worktrees. These tests resolve a real
///       profile through the real resolver under both env shapes.
///
/// The env-mutating tests join the "declare-done-env" collection — the
/// repo's existing bucket for classes that write process-global env vars.
/// ProjectInstructionsTests writes the SAME two variables (HOME /
/// USERPROFILE) and was added to that collection by this audit, because two
/// classes clobbering each other's HOME concurrently is a flake, not a gate.
/// </summary>
[Collection("declare-done-env")]
public class ConfigResolutionAuditTests : IDisposable
{
    private readonly string? _origHome = Environment.GetEnvironmentVariable("HOME");
    private readonly string? _origUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
    private readonly string _temp;

    public ConfigResolutionAuditTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "vett-cfgaudit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _origHome);
        Environment.SetEnvironmentVariable("USERPROFILE", _origUserProfile);
        // Only the throwaway tree this class created. Nothing under the real
        // ~/.vett is ever touched by these tests — that store is the owner's.
        try { Directory.Delete(_temp, recursive: true); } catch { }
    }

    // =====================================================================
    // (1) max_iterations — which default actually wins, per role
    // =====================================================================

    /// <summary>
    /// The two YAML-side defaults disagree on purpose: Profile.MaxIterations
    /// defaults to 100 (a real budget), MemberConfig.MaxIterations defaults
    /// to 0 (a "not set" sentinel that each call site replaces with its own
    /// literal). Deserialised through the real YAML pipeline, not read off
    /// the field initialisers.
    /// </summary>
    [Fact]
    public void Defaults_TopLevelIs100_ButMemberAndLeaderSentinelIsZero()
    {
        var p = Yaml.ParseProfile("""
            name: defaults-probe
            team:
              leader:
                name: lead
              members:
                - name: implementer
            """);

        Assert.Equal(100, p.MaxIterations);
        Assert.Equal(0, p.Team!.Leader.MaxIterations);
        Assert.Equal(0, p.Team.Members[0].MaxIterations);
    }

    /// <summary>
    /// A TEAM LEADER that omits team.leader.max_iterations gets 50 — NOT the
    /// profile's top-level max_iterations, which is set to a distinctive 7
    /// here precisely so inheritance would be visible if it happened.
    ///
    /// Measured, not read: the scripted client calls a tool every turn, so
    /// the "no tool call = done" exit is never reached and the ONLY way out
    /// is the cap. StopReason is asserted first — a run that ended for any
    /// other reason must not be scored as a cap measurement.
    /// </summary>
    [Fact]
    public async Task TeamLeader_OmittingItsOwnCap_Gets50_NotTheProfileTopLevel()
    {
        var result = await RunLeaderToExhaustion(topLevelMaxIterations: 7, leaderMaxIterations: 0);

        Assert.Equal("max_iterations", result.StopReason);
        // Falsification-probed 2026-08-24: asserting 7 here fails with
        // "Expected: 7, Actual: 50", so this number is measured, not assumed.
        Assert.Equal(50, result.Iterations);
        Assert.NotEqual(7, result.Iterations);
    }

    /// <summary>
    /// The other side of the same gate: an EXPLICIT team.leader.max_iterations
    /// is honoured verbatim. Without this, a leader stuck at 50 for an
    /// unrelated reason would satisfy the test above vacuously.
    /// </summary>
    [Fact]
    public async Task TeamLeader_ExplicitCap_IsHonouredVerbatim()
    {
        var result = await RunLeaderToExhaustion(topLevelMaxIterations: 7, leaderMaxIterations: 4);

        Assert.Equal("max_iterations", result.StopReason);
        Assert.Equal(4, result.Iterations);
    }

    /// <summary>
    /// A MEMBER dispatch that omits max_iterations gets 100 — a THIRD value,
    /// disagreeing with both the leader's 50 and the top-level 7. The member
    /// runs inside a real assign_task from a real leader.
    /// </summary>
    [Fact]
    public async Task MemberDispatch_OmittingItsOwnCap_Gets100_NotTheLeaders50()
    {
        var (memberCalls, _) = await RunTeamCountingMemberIterations(
            topLevelMaxIterations: 7, leaderMaxIterations: 2, memberMaxIterations: 0);

        Assert.Equal(100, memberCalls);
    }

    /// <summary>Explicit member cap is honoured — the two-sided half of the above.</summary>
    [Fact]
    public async Task MemberDispatch_ExplicitCap_IsHonouredVerbatim()
    {
        var (memberCalls, _) = await RunTeamCountingMemberIterations(
            topLevelMaxIterations: 7, leaderMaxIterations: 2, memberMaxIterations: 6);

        Assert.Equal(6, memberCalls);
    }

    /// <summary>
    /// The SOLO path (Runner.cs / ChatCommand.cs / ReplayCommand.cs) passes
    /// profile.MaxIterations to AgentEnvironment with NO `> 0` guard, unlike
    /// Bench/Team/Harness.cs which applies `profile.MaxIterations > 0 ? … : 100`.
    ///
    /// So `max_iterations: 0` is not "unset" on the solo path — it is a real
    /// budget of zero. The loop makes ZERO LLM calls and stops immediately,
    /// while the identical YAML under team-bench's solo arm would run 100.
    /// Same key, same file, two different meanings depending on entry point.
    /// </summary>
    [Fact]
    public async Task SoloPath_MaxIterationsZero_RunsZeroIterations_AndNeverCallsTheModel()
    {
        var p = Yaml.ParseProfile("name: zero\nmax_iterations: 0\n");
        Assert.Equal(0, p.MaxIterations);

        var client = new AlwaysCallsTool();
        var caps = new AgentCapabilities(
            new Dictionary<string, ToolFn> { ["noop"] = (_, _, _, _) => Task.FromResult("ok") },
            new List<JsonElement>(), new List<MiddlewareFn>());

        // Constructed exactly as Runner.cs does it: the profile value verbatim.
        var env = new AgentEnvironment(new NullSandbox(), "solo", p.MaxIterations);

        var result = await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model"), caps, env, "sys", "go");

        Assert.Equal("max_iterations", result.StopReason);
        Assert.Equal(0, result.Iterations);
        Assert.Equal(0, client.Calls);

        // ...whereas Harness.cs's guard would have turned the same 0 into 100.
        Assert.Equal(100, p.MaxIterations > 0 ? p.MaxIterations : 100);
    }

    // =====================================================================
    // (2) AgentEnvironment.Profile null-site count
    // =====================================================================

    /// <summary>
    /// COUNT GATE. `Profile` is the last optional parameter of
    /// AgentEnvironment; a site that forgets it gets null and every
    /// profile-driven feature the loop reads through it (pre_tool_use /
    /// post_tool_use / stop hooks, relay context nudges, turn_dedupe,
    /// leader_nudge) silently no-ops. There is no warning event — a hook
    /// that was configured to DENY behaves exactly like a hook that allowed.
    ///
    /// Counted from source because the sites are constructor calls, not
    /// runtime state. Both directions are failures: a NEW un-threaded site,
    /// or a fix that lands without updating this number.
    /// </summary>
    [Fact]
    public void AgentEnvironment_ProfileIsThreadedAt_2_Of_10_SrcSites()
    {
        var srcRoot = FindSrcRoot();
        var files = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        int ctors = 0, threaded = 0;
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            ctors += CountOccurrences(text, "new AgentEnvironment(");
            threaded += CountOccurrences(text, "Profile: profile");
        }

        Assert.Equal(10, ctors);
        Assert.Equal(2, threaded);
    }

    // =====================================================================
    // (3) HOME vs USERPROFILE — profile resolution
    // =====================================================================

    /// <summary>
    /// THE RULER SWAP, demonstrated end to end. One profile NAME, two user
    /// stores whose copies disagree on max_iterations, and nothing changing
    /// between the two resolutions except an environment variable.
    ///
    /// HOME strictly precedes USERPROFILE (Profile.cs ResolveSearchDirs), so
    /// a launcher that exports HOME steers which ruler the run is scored
    /// against — silently, with the same profile name in the run artifact.
    /// </summary>
    [Fact]
    public void ProfileResolution_HomeBeatsUserProfile_AndTheCapChangesWithIt()
    {
        var name = "vett-audit-ruler-" + Guid.NewGuid().ToString("N")[..8];
        var homeStore = MakeUserStore("home-store", name, maxIterations: 11);
        var upStore = MakeUserStore("up-store", name, maxIterations: 22);

        Environment.SetEnvironmentVariable("HOME", homeStore);
        Environment.SetEnvironmentVariable("USERPROFILE", upStore);
        var (viaHome, pathViaHome) = Yaml.ResolveWithPath(name, "profiles", Yaml.LoadProfile);

        Assert.NotNull(viaHome);
        Assert.Equal(11, viaHome!.MaxIterations);
        Assert.StartsWith(homeStore, pathViaHome!, StringComparison.OrdinalIgnoreCase);

        // Same name, same process, same resolver. Only HOME went away.
        Environment.SetEnvironmentVariable("HOME", null);
        var (viaUserProfile, pathViaUp) = Yaml.ResolveWithPath(name, "profiles", Yaml.LoadProfile);

        Assert.NotNull(viaUserProfile);
        Assert.Equal(22, viaUserProfile!.MaxIterations);
        Assert.StartsWith(upStore, pathViaUp!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// cwd/profiles WINS over both user stores. This is the property the
    /// team-bench queue depends on: its exec line cd's to the repo before
    /// launching, and that cd is the only reason the repo's copy of a tier6
    /// profile is the one that scores the run.
    ///
    /// Proven by writing a uniquely-named file into the REAL current
    /// directory rather than by mutating cwd, so no other concurrently
    /// running test can observe a changed working directory.
    /// </summary>
    [Fact]
    public void ProfileResolution_CwdProfiles_BeatsHomeStore()
    {
        var name = "vett-audit-cwd-" + Guid.NewGuid().ToString("N")[..8];
        var homeStore = MakeUserStore("home-store", name, maxIterations: 11);
        Environment.SetEnvironmentVariable("HOME", homeStore);
        Environment.SetEnvironmentVariable("USERPROFILE", homeStore);

        var cwdProfiles = Path.Combine(Directory.GetCurrentDirectory(), "profiles");
        Directory.CreateDirectory(cwdProfiles);
        var cwdFile = Path.Combine(cwdProfiles, name + ".yaml");
        File.WriteAllText(cwdFile, $"name: {name}\nmax_iterations: 33\n");
        try
        {
            var (p, path) = Yaml.ResolveWithPath(name, "profiles", Yaml.LoadProfile);

            Assert.NotNull(p);
            Assert.Equal(33, p!.MaxIterations);
            Assert.Equal(Path.GetFullPath(cwdFile), Path.GetFullPath(path!));
        }
        finally
        {
            // Removes only the GUID-named file this test just created.
            File.Delete(cwdFile);
        }
    }

    /// <summary>
    /// THE POLICY SPLIT, pinned. Profile/suite resolution and the AGENTS.md
    /// loader read HOME ?? USERPROFILE. The dispatch-worktree root
    /// (DispatchWorktreeManager), the chat-session log dir
    /// (CompactionMiddleware) and the tool-bin probe (Cli/Helpers) all use
    /// Environment.GetFolderPath(SpecialFolder.UserProfile), which ignores
    /// BOTH env vars and returns the real Windows profile.
    ///
    /// So "~/.vett" names two different directories in the same process the
    /// moment HOME is redirected: the RULER moves, the worktrees do not.
    /// This test fails if either policy is changed without the other.
    /// </summary>
    [WindowsOnlyFact("the real profile folder ignores HOME only on Windows.")]
    public void UserStorePolicies_Disagree_WhenHomeIsRedirected()
    {
        var redirected = Path.Combine(_temp, "redirected-home");
        Directory.CreateDirectory(redirected);
        Environment.SetEnvironmentVariable("HOME", redirected);
        Environment.SetEnvironmentVariable("USERPROFILE", redirected);

        // Policy A — profile resolution follows the env.
        var dirs = Yaml.ResolveSearchDirs("profiles").ToList();
        Assert.Equal(3, dirs.Count);
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "profiles"), dirs[0]);
        Assert.Equal(Path.Combine(redirected, ".vett", "profiles"), dirs[1]);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "profiles"), dirs[2]);

        // Policy B — GetFolderPath does NOT follow the env. It is the real
        // profile directory, so it cannot equal our temp redirect.
        var folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotEqual(redirected, folderPath);

        // Which is exactly the divergence: same "~", two directories.
        Assert.NotEqual(
            Path.Combine(redirected, ".vett", "dispatches"),
            Path.Combine(folderPath, ".vett", "dispatches"));
    }

    /// <summary>
    /// A POSIX-shaped HOME (what a shell exports natively; Git-Bash happens
    /// to convert it to Windows form when it spawns a native child, but a
    /// launcher that does not — a service, a container exec, an inherited
    /// env — will not) does NOT throw and does NOT resolve to the real user
    /// store. It silently becomes a DRIVE-RELATIVE path, so ~/.vett/profiles
    /// quietly stops existing and resolution falls through to the install
    /// dir. Recorded as a characterisation: the failure is silent, which is
    /// the whole problem.
    /// </summary>
    [WindowsOnlyFact("drive-relative paths exist only on Windows.")]
    public void ProfileResolution_PosixShapedHome_SilentlyBecomesADriveRelativePath()
    {
        Environment.SetEnvironmentVariable("HOME", "/c/Users/nobody-vett-audit");

        var dirs = Yaml.ResolveSearchDirs("profiles").ToList();
        var userDir = dirs[1];

        // No exception, no warning — just a path that is not what the shell meant.
        Assert.False(Path.IsPathFullyQualified(userDir),
            $"expected a drive-relative path, got '{userDir}'");
        Assert.False(Directory.Exists(userDir));
    }

    // =====================================================================
    // helpers
    // =====================================================================

    private string MakeUserStore(string label, string profileName, int maxIterations)
    {
        var root = Path.Combine(_temp, label);
        var store = Path.Combine(root, ".vett", "profiles");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, profileName + ".yaml"),
            $"name: {profileName}\nmax_iterations: {maxIterations}\n");
        return root;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>Walks up from the test binary to the repo's src/Vett.</summary>
    private static string FindSrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var candidate = Path.Combine(d.FullName, "src", "Vett");
            if (Directory.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("could not locate src/Vett above " + AppContext.BaseDirectory);
    }

    private const string LeaderMarker = "AUDIT-LEADER-SYSTEM-PROMPT";
    private const string MemberMarker = "AUDIT-MEMBER-SYSTEM-PROMPT";
    private const string MemberName = "implementer";

    private static Profile TeamProfile(int topLevel, int leaderIters, int memberIters) => new()
    {
        Llm = new LlmConfig(),
        MaxIterations = topLevel,
        Tools = ["terminal"],
        Team = new TeamConfig
        {
            // ⭐ ADDED 2026-08-27, when an absent width ceiling became an error
            // rather than a silent "unlimited". EXPLICIT 0 — not a number —
            // because 0 IS unlimited, so this fixture keeps the exact behaviour
            // it had before the requirement landed. This class measures how
            // ITERATION caps resolve across leader/member/profile; a real width
            // ceiling here would be a second, uncontrolled variable in tests that
            // were never about width.
            MaxConcurrentDispatches = 0,
            Leader = new MemberConfig
            {
                Name = "lead",
                SystemPrompt = LeaderMarker,
                MaxIterations = leaderIters,
                Tools = ["terminal"],
            },
            Members =
            {
                new MemberConfig
                {
                    Name = MemberName,
                    SystemPrompt = MemberMarker,
                    MaxIterations = memberIters,
                    Tools = ["terminal"],
                },
            },
            // No worktrees: this audit must not create anything under the
            // shared ~/.vett/dispatches tree. 0 also disables the startup
            // prune, which walks that same shared tree.
            DispatchWorktree = false,
            DispatchMaxAgeDays = 0,
            AutoInjectAsyncResults = false,
        },
    };

    /// <summary>Runs a real team leader with a client that calls a tool every
    /// turn, so the only exit is the iteration cap.</summary>
    private async Task<AgentResult> RunLeaderToExhaustion(int topLevelMaxIterations, int leaderMaxIterations)
    {
        var profile = TeamProfile(topLevelMaxIterations, leaderMaxIterations, memberIters: 3);
        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync("go");
        chan.Writer.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        return await TeamCoordinator.RunInteractiveAsync(
            profile, new ToolLoopClient(), "test-model", new NullSandbox(), "audit-panel",
            chan.Reader,
            onAssistantText: null, onWaitingForInput: null, onEvent: null,
            seedHistory: null, turnInterrupt: null, compactRequest: null,
            cwd: _temp, ct: cts.Token);
    }

    /// <summary>Leader dispatches once via assign_task, then keeps calling a
    /// harmless tool until its own (small) cap. The member is scripted to
    /// call a tool forever, so IT exits only on ITS cap. Returns the member's
    /// observed LLM-call count = its effective iteration budget.</summary>
    private async Task<(int MemberCalls, AgentResult Leader)> RunTeamCountingMemberIterations(
        int topLevelMaxIterations, int leaderMaxIterations, int memberMaxIterations)
    {
        var profile = TeamProfile(topLevelMaxIterations, leaderMaxIterations, memberMaxIterations);
        var client = new DispatchThenLoopClient();
        var chan = System.Threading.Channels.Channel.CreateUnbounded<string>();
        await chan.Writer.WriteAsync("go");
        chan.Writer.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var leader = await TeamCoordinator.RunInteractiveAsync(
            profile, client, "test-model", new NullSandbox(), "audit-panel-" + Guid.NewGuid().ToString("N")[..6],
            chan.Reader,
            onAssistantText: null, onWaitingForInput: null, onEvent: null,
            seedHistory: null, turnInterrupt: null, compactRequest: null,
            cwd: _temp, ct: cts.Token);

        return (client.MemberCalls, leader);
    }

    // ---- offline clients --------------------------------------------------

    private static ChatMessage ToolCall(string name, Dictionary<string, object?> args) =>
        new(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(Guid.NewGuid().ToString("N")[..8], name, args),
        });

    /// <summary>The iteration burner. Each call carries a DIFFERENT command on
    /// purpose: since 2026-09-05 (EpicForge law 129) AgentLoop refuses a sole
    /// call that repeats the previous one byte-for-byte after two identical
    /// results and ends the run `repeated_call_exhausted` at the fifth identical
    /// issue -- so a constant `true` forever would be stopped by THAT detector
    /// at call 5 and the caps these tests measure (50, 100, 6) would never be
    /// reached. A loop that keeps asking for something new is still a loop only
    /// the iteration cap can stop. The stimulus changed; no assertion did.</summary>
    private static ChatMessage Terminal(int n) =>
        ToolCall("terminal", new Dictionary<string, object?> { ["cmd"] = "true # " + n });

    /// <summary>Always calls `noop`. Never emits a tool-call-less turn, so the
    /// loop's "no tool call = done" exit is unreachable.</summary>
    private sealed class AlwaysCallsTool : IChatClient
    {
        public int Calls;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var n = Interlocked.Increment(ref Calls);
            // Varies per call for the same reason Terminal(n) does (law 129).
            return Task.FromResult(new ChatResponse(ToolCall("noop", new Dictionary<string, object?> { ["n"] = n })));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    /// <summary>Every agent it serves calls `terminal` forever.</summary>
    private sealed class ToolLoopClient : IChatClient
    {
        private int _n;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(Terminal(Interlocked.Increment(ref _n))));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Serves leader and member off one client, told apart by the member's
    /// system-prompt marker rather than by call order — so an interleaving
    /// cannot silently mis-attribute an iteration to the wrong agent.
    /// Leader dispatches exactly once, then loops on a harmless tool.
    /// </summary>
    private sealed class DispatchThenLoopClient : IChatClient
    {
        private int _memberCalls;
        private int _leaderCalls;
        private int _dispatched;
        public int MemberCalls => Volatile.Read(ref _memberCalls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? o = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var isMember = messages.Any(m => (m.Text ?? "").Contains(MemberMarker));

            if (isMember)
            {
                var n = Interlocked.Increment(ref _memberCalls);
                return Task.FromResult(new ChatResponse(Terminal(n)));
            }

            if (Interlocked.Exchange(ref _dispatched, 1) == 0)
            {
                return Task.FromResult(new ChatResponse(ToolCall("assign_task",
                    new Dictionary<string, object?>
                    {
                        ["member"] = MemberName,
                        ["task"] = "audit: burn iterations",
                    })));
            }

            return Task.FromResult(new ChatResponse(Terminal(Interlocked.Increment(ref _leaderCalls))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    /// <summary>Sandbox that succeeds at nothing in particular. Every bash call
    /// returns clean, so `terminal` is a pure iteration burner.</summary>
    private sealed class NullSandbox : ISandbox
    {
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("", 0, "/fake", false));
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default) => Task.FromResult("");
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string n, CancellationToken ct = default) => Task.FromResult(("", (string?)null));
        public Task<string> FileInsertAsync(string s, string p, int l, string n, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => Task.FromResult("");
        public Task SessionCreateAsync(string n, string cwd, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string cwd) => this;
        public ISandbox WithDispatchWorktree(string newCwd, string root) => this;
    }
}
