using Vett.Agent;
using Vett.Config;
using Vett.Llm;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// Guards the completion signal end-to-end: `finish` / `declare_done` return a
/// <see cref="Builtins.SubmitMarker"/> string, and
/// <see cref="BuiltinMiddleware.SubmitDetector"/> is the ONLY thing in the
/// codebase that reads it. If a profile's resolved middleware chain doesn't
/// contain it, the marker is an inert string in the transcript: the agent
/// submits correctly, nothing stops the loop, and it keeps working past its own
/// submission until max_iterations or the timeout kills it.
///
/// That is exactly what shipped. Every team profile declared a NON-EMPTY
/// middleware list that omitted `submit_detector`, and
/// <see cref="MiddlewareResolver.ResolveOrDefault"/>'s fallback doesn't cover
/// it — that only fires on an EMPTY list. Observed symptom: the leader
/// declared done, kept looping, was handed a stuck-nudge, and re-dispatched
/// byte-identical work.
///
/// These tests assert the BEHAVIOUR (loop stops) rather than the presence of a
/// name in a list, so they stay honest if the wiring moves.
/// </summary>
public class SubmitDetectorWiringTests
{
    private static string? FindProfilesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "profiles", "coding-team-v2.yaml");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "profiles");
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Fresh state carrying a completion marker as the last observation.</summary>
    private static AgentState SubmittedState() => new()
    {
        Iteration = 3,
        Messages = [Chat.User("do the thing"), Chat.Assistant("working")],
        LastObservations =
        [
            new()
            {
                ToolCallId = "c1",
                ToolName = "declare_done",
                Result = Builtins.SubmitMarker + "Implemented and verified.",
                Success = true,
            }
        ],
    };

    private static async Task<AgentState> RunChainAsync(List<MiddlewareFn> chain)
    {
        var state = SubmittedState();
        foreach (var mw in chain) await mw(state, default);
        return state;
    }

    [Fact]
    public async Task NonEmptyListOmittingSubmitDetector_StillTerminates()
    {
        // The exact shape every shipped team profile had: a non-empty list that
        // never names submit_detector. ResolveOrDefault's empty-list fallback
        // does not rescue this.
        var chain = MiddlewareResolver.Resolve(
            ["output_truncation", "stuck_detector", "milestone_checkpoint", "observation_elision"]);

        var state = await RunChainAsync(chain);

        Assert.True(state.StopLoop, "declare_done returned a submit marker and the loop did not stop.");
        Assert.Equal("finish_tool", state.StopReason);
    }

    [Fact]
    public async Task EmptyList_StillTerminates()
    {
        // The already-working path, pinned so the fix can't regress it.
        var state = await RunChainAsync(MiddlewareResolver.ResolveOrDefault([]));

        Assert.True(state.StopLoop);
        Assert.Equal("finish_tool", state.StopReason);
    }

    [Fact]
    public async Task NonMarkerResult_DoesNotTerminate()
    {
        // The other side of the ruler. A forced submit_detector must not make
        // every chain stop unconditionally — that would turn this gate green
        // for the wrong reason.
        var chain = MiddlewareResolver.Resolve(["output_truncation", "stuck_detector"]);

        var state = new AgentState
        {
            Messages = [Chat.User("do the thing")],
            LastObservations =
            [
                new() { ToolCallId = "c1", ToolName = "terminal", Result = "[exit code: 0]", Success = true }
            ],
        };
        foreach (var mw in chain) await mw(state, default);

        Assert.False(state.StopLoop);
    }

    [Fact]
    public async Task AgentFinishedCritic_CanStillUnstop_AfterForcedDetector()
    {
        // The designed override must survive. The critic un-stops a finish it
        // disagrees with, which only works if it observes StopLoop already set
        // — i.e. the forced detector has to run BEFORE it, not after.
        var stub = new StubChatClient(_ => "DISAGREE: tests were never run");
        var llm = new LlmSettings(stub, "test", 0.0);

        var chain = MiddlewareResolver.Resolve(
            ["output_truncation", "agent_finished_critic"], llm: llm);

        var state = await RunChainAsync(chain);

        Assert.False(state.StopLoop);      // critic vetoed the submission
        Assert.True(stub.CallCount > 0);   // and it actually got to look at it
    }

    /// <summary>
    /// The population gate. Every shipped profile — leader chain and every
    /// member chain — must terminate on a completion marker. A per-profile fix
    /// would leave the next profile authored with the same hole; this spans the
    /// whole directory instead.
    /// </summary>
    [Fact]
    public void EveryShippedProfile_LeaderAndMembers_TerminateOnSubmitMarker()
    {
        var dir = FindProfilesDir();
        Assert.NotNull(dir);

        var checkedChains = new List<string>();
        var unparseable = new List<string>();
        var failures = new List<string>();

        foreach (var path in Directory.GetFiles(dir!, "*.yaml").OrderBy(p => p))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            Profile p;
            try { p = Yaml.LoadProfile(path); }
            catch (Exception ex) { unparseable.Add($"{name}: {ex.GetType().Name}"); continue; }

            void Check(string label, List<string> names, CompactionConfig? compaction)
            {
                var chain = MiddlewareResolver.ResolveOrDefault(names, compaction: compaction);
                var state = SubmittedState();
                foreach (var mw in chain) mw(state, default).GetAwaiter().GetResult();
                checkedChains.Add(label);
                if (!state.StopLoop) failures.Add(label);
            }

            Check(name, p.Middleware, p.Compaction);
            foreach (var m in p.Team?.Members ?? [])
                Check($"{name}/{m.Name}", m.Middleware.Count > 0 ? m.Middleware : p.Middleware,
                      m.Compaction ?? p.Compaction);
        }

        // The gate must span the live population, not pass on an empty sweep.
        string[] mustCover =
        [
            "ds-solo-flash", "ds-solo-pro", "ds-team-flash",
            "ds-team-flash-escalate", "ds-team-lead-pro", "ds-team-pro",
            "coding-team-v2",
        ];
        foreach (var required in mustCover)
            Assert.Contains(required, checkedChains);

        Assert.True(checkedChains.Count >= 20,
            $"only {checkedChains.Count} chains exercised — the sweep found too little to be a gate");

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{checkedChains.Count} chains ignore the completion marker " +
            $"(unparseable, not checked: {unparseable.Count}): {string.Join(", ", failures)}");
    }
}
