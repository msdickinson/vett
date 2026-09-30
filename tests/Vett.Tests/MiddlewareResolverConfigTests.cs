using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Llm;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// Covers the profile-configurable compaction wiring: thresholds that used
/// to be hardcoded (30k / keep-5 / 200-char) now flow from a
/// <see cref="CompactionConfig"/> through MiddlewareResolver into the
/// constructed middleware, and the LLM-backed condenser only registers when
/// an <see cref="LlmSettings"/> is supplied (the gap that silently disabled
/// it on the team path).
/// </summary>
public class MiddlewareResolverConfigTests : IDisposable
{
    private readonly string _dir;

    public MiddlewareResolverConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-resolver-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>
    /// MiddlewareResolver force-prepends submit_detector to every resolved
    /// chain — a profile that omits it turns `finish` / `declare_done` into an
    /// inert string (see <see cref="SubmitDetectorWiringTests"/>). These tests
    /// are about the COMPACTION wiring, so they assert on the middleware they
    /// actually named. The forced entry is CHECKED here rather than skipped
    /// blindly, so this helper cannot quietly absorb a chain that lost it.
    ///
    /// ⚠ IT ALSO FORCES <c>SessionLogDir</c> ONTO THE CONFIG. The clearing
    /// strategies snapshot history before discarding it, and an unset dir
    /// resolves to the user's REAL <c>~/.vett/chat-sessions/compaction/</c> —
    /// so the tests below, which deliberately cross the threshold, were writing
    /// junk snapshots into Mark's actual session store on every suite run.
    /// Injecting it here rather than per-test makes the class safe by
    /// construction: a test added later cannot forget.
    /// </summary>
    private List<MiddlewareFn> ResolveNamed(
        List<string> names,
        LlmSettings? llm = null,
        CompactionConfig? compaction = null,
        Action<string>? onDiagnostic = null)
    {
        compaction ??= new CompactionConfig();
        if (string.IsNullOrWhiteSpace(compaction.SessionLogDir))
            compaction.SessionLogDir = _dir;

        var all = MiddlewareResolver.Resolve(
            names, llm: llm, compaction: compaction, onDiagnostic: onDiagnostic);
        Assert.NotEmpty(all);
        Assert.Equal<MiddlewareFn>(BuiltinMiddleware.SubmitDetector, all[0]);
        return all.Skip(1).ToList();
    }

    private static AgentState BigState() => new()
    {
        Iteration = 10,
        Messages =
        [
            Chat.System("You are helpful."),
            Chat.User(new string('a', 500)),
            Chat.Assistant(new string('b', 500)),
            Chat.ToolResult("c1", new string('c', 500)),
            Chat.User("recent 1"),
            Chat.Assistant("recent 2"),
        ]
    };

    [Fact]
    public async Task MilestoneCheckpoint_HonorsConfiguredThreshold_Compacts()
    {
        // A low configured threshold must make the checkpoint fire — proving
        // the value flows from CompactionConfig, not the hardcoded 30k.
        var mws = ResolveNamed(
            ["milestone_checkpoint"], compaction: new CompactionConfig { ThresholdTokens = 50, KeepLastMessages = 2 });
        Assert.Single(mws);

        var state = BigState();
        await mws[0](state, default);

        Assert.Contains(state.Messages, m => m.GetText().Contains("checkpoint") || m.GetText().Contains("Milestone"));
        Assert.Equal("recent 2", state.Messages.Last().GetText());
    }

    [Fact]
    public async Task MilestoneCheckpoint_HighConfiguredThreshold_DoesNotCompact()
    {
        // Same state, but a 100k threshold (the deepseek-team value) must
        // leave it untouched — the whole point of the config knob.
        var mws = ResolveNamed(
            ["milestone_checkpoint"], compaction: new CompactionConfig { ThresholdTokens = 100_000 });

        var state = BigState();
        var before = state.Messages.Count;
        await mws[0](state, default);

        Assert.Equal(before, state.Messages.Count);
    }

    [Fact]
    public async Task LlmSummarizingCondenser_WithoutLlm_DegradesInsteadOfVanishing()
    {
        // ⚠ THIS ASSERTION IS INVERTED FROM WHAT IT ORIGINALLY SAID, ON PURPOSE.
        //
        // It used to read `Assert.Empty(withoutLlm)` under the comment "without
        // llm the name is unknown → silently skipped. This is exactly what
        // disabled the condenser on the team path before it threaded llm." So
        // the test PINNED the defect: a profile that declared a condenser and a
        // tuned threshold ran with no compaction whatsoever, and the suite went
        // green over it. A test can lock in a bug as firmly as it locks in a
        // fix, and this one did for as long as it existed.
        //
        // The resolver now degrades: a declared COMPACTION strategy resolves to
        // the best available strategy rather than to nothing. Threading llm in
        // at the call sites was the real fix; this is the floor beneath it, for
        // the next call site that forgets.
        var notes = new List<string>();
        var withoutLlm = ResolveNamed(
            ["llm_summarizing_condenser"],
            compaction: new CompactionConfig { ThresholdTokens = 50, KeepLastMessages = 2 },
            onDiagnostic: notes.Add);
        var substituted = Assert.Single(withoutLlm);
        Assert.Contains(notes, n => n.Contains("milestone_checkpoint"));
        // The substitute must inherit the PROFILE's threshold, not the 30k
        // default — degrading to a strategy that fires at the wrong size is
        // only marginally better than not degrading at all.
        Assert.Contains(notes, n => n.Contains("50"));

        // And it is a REAL strategy, not a placeholder: it compacts.
        var state = BigState();
        await substituted(state, default);
        Assert.Contains(state.Messages, m => CompactionMiddleware.IsInjectedSummary(m.GetText()));

        // With an llm, the genuine article registers and nothing is reported.
        notes.Clear();
        var llm = new LlmSettings(new StubChatClient(_ => "summary"), "test", 0.0);
        var withLlm = ResolveNamed(["llm_summarizing_condenser"], llm: llm, onDiagnostic: notes.Add);
        Assert.Single(withLlm);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task ObservationElision_HonorsConfiguredMaxChars()
    {
        var mws = ResolveNamed(
            ["observation_elision"],
            compaction: new CompactionConfig { ElisionKeepLast = 1, ElisionMaxChars = 20 });

        var state = new AgentState
        {
            Messages =
            [
                Chat.ToolResult("c1", new string('x', 500)), // old — elided
                Chat.ToolResult("c2", new string('y', 500)), // old — elided
                Chat.ToolResult("c3", "recent kept"),        // last 1 — kept
            ]
        };
        await mws[0](state, default);

        Assert.Contains("elided", state.Messages[0].GetText());
        Assert.Contains("elided", state.Messages[1].GetText());
        Assert.Equal("recent kept", state.Messages[2].GetText());
    }

    [Fact]
    public void DefaultCompaction_ReproducesHistoricalHardcodedValues()
    {
        var c = new CompactionConfig();
        Assert.Equal(30_000, c.ThresholdTokens);
        Assert.Equal(5, c.KeepLastMessages);
        Assert.Equal(200, c.ElisionMaxChars);
        Assert.Equal(5, c.ElisionKeepLast);
    }
}
