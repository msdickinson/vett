using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Llm;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// Covers the "detach from history, continue on a fresh plan" reset:
/// full history is persisted to a resumable JSONL (kept as reference),
/// the live context hard-resets to system + a forward plan, and the
/// snapshot round-trips through <see cref="HistorySeeder"/> so it is
/// genuinely resumable via <c>--resume</c>.
/// </summary>
public class ReplanCheckpointTests : IDisposable
{
    private readonly string _dir;

    public ReplanCheckpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-replan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AgentState BigState() => new()
    {
        Iteration = 42,
        Messages =
        [
            Chat.System("You are a coding agent."),
            Chat.User("Build feature X across the repo."),
            Chat.Assistant("I created foo.cs and updated bar.cs."),
            Chat.ToolResult("c1", new string('c', 800)),
            Chat.User("Now wire it into the endpoint."),
            Chat.Assistant("Wired MapFeatureX; tests pass."),
        ]
    };

    [Fact]
    public async Task UnderThreshold_DoesNothing()
    {
        var llm = new LlmSettings(new StubChatClient(_ => "PLAN"), "test", 0.0);
        var mw = CompactionMiddleware.ReplanCheckpoint(llm, tokenThreshold: 1_000_000, sessionLogDir: _dir);

        var state = BigState();
        var before = state.Messages.Count;
        await mw(state, default);

        Assert.Equal(before, state.Messages.Count);
        Assert.Empty(Directory.GetFiles(_dir)); // no snapshot written
    }

    [Fact]
    public async Task OverThreshold_ResetsToSystemPlusPlan_AndPersistsResumableSnapshot()
    {
        var stub = new StubChatClient(_ =>
            "GOAL: Build feature X.\nDONE: foo.cs, bar.cs.\nSTATE: wired.\nNEXT: run full suite.\nFACTS: MapFeatureX.");
        var llm = new LlmSettings(stub, "test", 0.0);
        var mw = CompactionMiddleware.ReplanCheckpoint(llm, tokenThreshold: 50, sessionLogDir: _dir, sessionId: "run1");

        var state = BigState();
        await mw(state, default);

        // Live context detached to system + plan-user + ack-assistant.
        Assert.Equal(3, state.Messages.Count);
        Assert.True(state.Messages[0].IsRole(ChatRole.System));
        Assert.True(state.Messages[1].IsRole(ChatRole.User));
        Assert.Contains("GOAL: Build feature X", state.Messages[1].GetText());
        Assert.Contains("NEXT: run full suite", state.Messages[1].GetText());
        Assert.True(state.Messages[2].IsRole(ChatRole.Assistant));

        // The planner was actually invoked.
        Assert.Equal(1, stub.CallCount);

        // Snapshot written and named deterministically — in the `compaction/`
        // SUBDIRECTORY, not the session dir itself. That nesting is load-bearing:
        // vett-chat's session picker does readdirSync(sessionDir).filter(endsWith
        // '.jsonl') with no name check, so a flat snapshot showed up as a fake
        // "session" — and since its first user turn is the original task, it got a
        // title nearly identical to the real session's and a newer mtime that
        // sorted it above. See CompactionMiddleware.ResolveSnapshotDir.
        var snapshot = Path.Combine(_dir, "compaction", "run1-checkpoint1.jsonl");
        Assert.True(File.Exists(snapshot));

        // The guarantee is the nesting, not just this path: nothing a compaction
        // writes may land where the picker scans.
        Assert.Empty(Directory.GetFiles(_dir, "*.jsonl"));

        // And it round-trips through the resume seeder — the history is
        // genuinely resumable, not just dumped.
        var seed = HistorySeeder.LoadFromJsonl(snapshot);
        Assert.Equal(2, seed.UserTurns);       // the 2 user turns
        Assert.Equal(2, seed.AssistantTurns);  // the 2 assistant turns (tool-only turns skipped)
        Assert.Contains(seed.Messages, m => m.GetText().Contains("Build feature X across the repo"));
        Assert.Contains(seed.Messages, m => m.GetText().Contains("Wired MapFeatureX"));
        // The plan itself references the on-disk snapshot for detail recovery.
        Assert.Contains(snapshot, state.Messages[1].GetText());
    }

    [Fact]
    public async Task SuccessiveCheckpoints_GetDistinctSnapshotFiles()
    {
        var llm = new LlmSettings(new StubChatClient(_ => "PLAN"), "test", 0.0);
        var mw = CompactionMiddleware.ReplanCheckpoint(llm, tokenThreshold: 50, sessionLogDir: _dir, sessionId: "run1");

        await mw(BigState(), default);
        await mw(BigState(), default);

        Assert.True(File.Exists(Path.Combine(_dir, "compaction", "run1-checkpoint1.jsonl")));
        Assert.True(File.Exists(Path.Combine(_dir, "compaction", "run1-checkpoint2.jsonl")));
    }

    [Fact]
    public async Task PlannerError_FallsBackToStaticPlan_StillResets()
    {
        // The LLM planner throwing must not block the loop — we still reset.
        var llm = new LlmSettings(new StubChatClient(_ => throw new InvalidOperationException("boom")), "test", 0.0);
        var mw = CompactionMiddleware.ReplanCheckpoint(llm, tokenThreshold: 50, sessionLogDir: _dir);

        var state = BigState();
        await mw(state, default);

        Assert.Equal(3, state.Messages.Count);
        Assert.Contains("Re-plan checkpoint", state.Messages[1].GetText());
    }
}
