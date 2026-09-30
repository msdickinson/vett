using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Llm;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// ⭐ THE GATE THE SOURCE COMMENT PROMISED.
///
/// <c>CompactionMiddleware.InjectedBlockPrefixes</c> carried the line
/// "CompactionMarkerCoverageTests fails if a strategy injects a block this
/// list does not match." That test did not exist — the name appeared nowhere
/// in the repo except in that sentence. A comment asserting an invariant is
/// gated, when nothing gates it, is worse than no comment: it stops the next
/// person writing the gate. This file makes the sentence true.
///
/// Why the invariant matters. <c>BuildStaticDigest</c> recovers the user's
/// ORIGINAL ask by walking to the first User turn that is NOT one of our own
/// injected blocks. After the first compaction the first user message IS one
/// of those blocks. If a prefix is missing from the list, the digest quotes a
/// previous digest instead of the real task, and every later checkpoint drifts
/// one step further from what the user actually asked for — silently, and
/// worse the longer the run goes. That is precisely the long Pro run this
/// feature exists to serve.
///
/// ⛔ THIS GATE RUNS THE STRATEGIES rather than scanning the source. A regex
/// over the file would be a predicate over a guessed vocabulary — it would
/// pass while the real behaviour drifted. Here each strategy is executed, the
/// block it actually injected is captured, and that string is tested against
/// the same helper production uses.
/// </summary>
public class CompactionMarkerCoverageTests : IDisposable
{
    private readonly string _dir;

    public CompactionMarkerCoverageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-marker-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AgentState BigState() => new()
    {
        Iteration = 7,
        Messages =
        [
            Chat.System("You are a coding agent."),
            Chat.User("Original ask: build feature X."),
            Chat.Assistant(new string('b', 900)),
            Chat.User("keep going"),
            Chat.Assistant("done"),
        ],
    };

    /// <summary>
    /// Every block any strategy injects, in every branch, must be recognised
    /// by <c>IsInjectedSummary</c>. Five branches across four strategies —
    /// LLM-success and static-fallback are DIFFERENT strings and both count.
    /// </summary>
    [Fact]
    public async Task EveryInjectedBlock_IsRecognisedByIsInjectedSummary()
    {
        var injected = new List<(string Branch, string Text)>();

        // 1. Milestone — always the mechanical digest, no LLM branch.
        var milestone = CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 1, keepLastMessages: 1, model: null, sessionLogDir: _dir);
        var s1 = BigState();
        await milestone(s1, default);
        injected.Add(("milestone", FirstInjectedUserTurn(s1)));

        // 2. Condenser, LLM answered.
        var okLlm = new LlmSettings(new StubChatClient(_ => "the model's summary"), "test", 0.0);
        var condenseOk = CompactionMiddleware.LLMSummarizingCondenser(
            okLlm, tokenThreshold: 1, keepLastMessages: 1, sessionLogDir: _dir);
        var s2 = BigState();
        await condenseOk(s2, default);
        injected.Add(("condenser-llm", FirstInjectedUserTurn(s2)));

        // 3. Condenser, LLM threw → static fallback.
        var badLlm = new LlmSettings(
            new StubChatClient(_ => throw new InvalidOperationException("down")), "test", 0.0);
        var condenseFail = CompactionMiddleware.LLMSummarizingCondenser(
            badLlm, tokenThreshold: 1, keepLastMessages: 1, sessionLogDir: _dir);
        var s3 = BigState();
        await condenseFail(s3, default);
        injected.Add(("condenser-static", FirstInjectedUserTurn(s3)));

        // 4. Replan, LLM answered.
        var replanOk = CompactionMiddleware.ReplanCheckpoint(
            new LlmSettings(new StubChatClient(_ => "GOAL: x\nNEXT: y"), "test", 0.0),
            tokenThreshold: 1, sessionLogDir: _dir);
        var s4 = BigState();
        await replanOk(s4, default);
        injected.Add(("replan-llm", FirstInjectedUserTurn(s4)));

        // 5. Replan, LLM threw → static plan.
        var replanFail = CompactionMiddleware.ReplanCheckpoint(
            badLlm, tokenThreshold: 1, sessionLogDir: _dir);
        var s5 = BigState();
        await replanFail(s5, default);
        injected.Add(("replan-static", FirstInjectedUserTurn(s5)));

        // Sanity: all five branches actually produced a block. Without this a
        // strategy that silently stopped injecting would make the gate pass
        // over an empty list — a vacuous green.
        Assert.Equal(5, injected.Count);
        Assert.All(injected, i => Assert.False(string.IsNullOrWhiteSpace(i.Text),
            $"branch '{i.Branch}' injected no user turn at all"));

        foreach (var (branch, text) in injected)
        {
            Assert.True(
                CompactionMiddleware.IsInjectedSummary(text),
                $"branch '{branch}' injected a block that InjectedBlockPrefixes does not " +
                $"match, so BuildStaticDigest will mistake it for the user's original ask. " +
                $"Add its prefix to CompactionMiddleware.InjectedBlockPrefixes.\n" +
                $"Block began: {text[..Math.Min(120, text.Length)]}");
        }
    }

    /// <summary>
    /// The negative half. A gate that only ever says "yes" cannot fail, and
    /// <c>IsInjectedSummary</c> saying yes to a real user turn would be worse
    /// than saying no to ours: the digest would skip the genuine ask.
    /// </summary>
    [Theory]
    [InlineData("Build feature X across the repo.")]
    [InlineData("[not one of ours] do the thing")]
    [InlineData("")]
    [InlineData("  [Milestone checkpoint at iteration 3]")]  // leading space — StartsWith is ordinal
    public void IsInjectedSummary_RejectsTurnsWeDidNotInject(string text)
    {
        Assert.False(CompactionMiddleware.IsInjectedSummary(text));
    }

    [Fact]
    public void IsInjectedSummary_HandlesNull()
    {
        Assert.False(CompactionMiddleware.IsInjectedSummary(null));
    }

    /// <summary>
    /// The consequence test, stated as behaviour rather than as a claim about
    /// prefixes: compact TWICE and the second digest must still quote the real
    /// original ask, not the first digest.
    /// </summary>
    [Fact]
    public async Task SecondCompaction_StillQuotesTheRealOriginalAsk()
    {
        var mw = CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 1, keepLastMessages: 2, model: null, sessionLogDir: _dir);

        var state = BigState();
        await mw(state, default);

        // Add more traffic and compact again.
        state.Messages.Add(Chat.User("second round of work"));
        state.Messages.Add(Chat.Assistant(new string('z', 900)));
        await mw(state, default);

        var digest = state.Messages.First(m =>
            m.IsRole(ChatRole.User) && CompactionMiddleware.IsInjectedSummary(m.GetText())).GetText();

        Assert.Contains("Original ask: build feature X.", digest);
        // And specifically NOT a digest quoting a digest.
        Assert.DoesNotContain("ORIGINAL TASK: [Milestone checkpoint", digest);
    }

    private static string FirstInjectedUserTurn(AgentState state)
        => state.Messages.FirstOrDefault(m => m.IsRole(ChatRole.User))?.GetText() ?? "";
}

/// <summary>
/// ⭐ THE RULE: A STRATEGY THAT CLEARS THE CONVERSATION MUST WRITE IT DOWN
/// FIRST.
///
/// Before 2026-08-26 only ReplanCheckpoint did. MilestoneCheckpoint and
/// LLMSummarizingCondenser called <c>state.Messages.Clear()</c> with no
/// on-disk record, so everything past the keep-window was gone from process
/// memory and from the world — on a long autonomous run, the bulk of the work.
/// These tests are the reason that cannot silently regress.
/// </summary>
public class CompactionSnapshotTests : IDisposable
{
    private readonly string _dir;

    public CompactionSnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-snap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AgentState WorkState() => new()
    {
        Iteration = 12,
        Messages =
        [
            Chat.System("You are a coding agent."),
            Chat.User("Add retry logic to the uploader."),
            Chat.AssistantWithCalls(null, [new FunctionCallContent("c1", "file_editor",
                new Dictionary<string, object?> { ["command"] = "str_replace", ["path"] = "src/Uploader.cs" })]),
            Chat.ToolResult("c1", new string('r', 600)),
            Chat.AssistantWithCalls(null, [new FunctionCallContent("c2", "terminal",
                new Dictionary<string, object?> { ["command"] = "dotnet test" })]),
            Chat.ToolResult("c2", new string('t', 600)),
            Chat.Assistant("Retry logic added; tests pass."),
            Chat.User("now update the docs"),
        ],
    };

    [Fact]
    public async Task Milestone_SnapshotsHistoryBeforeClearing_AndItRoundTripsThroughResume()
    {
        var mw = CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 1, keepLastMessages: 2, model: null,
            sessionLogDir: _dir, sessionId: "run1");

        var state = WorkState();
        await mw(state, default);

        var snapshot = Path.Combine(_dir, "compaction", "run1-milestone1.jsonl");
        Assert.True(File.Exists(snapshot), $"no snapshot at {snapshot}");

        // The history is genuinely RESUMABLE, not merely dumped: it loads
        // through the same seeder `--resume` uses (ChatCommand.cs:374).
        var seed = HistorySeeder.LoadFromJsonl(snapshot);
        Assert.Contains(seed.Messages, m => m.GetText().Contains("Add retry logic to the uploader"));
        Assert.Contains(seed.Messages, m => m.GetText().Contains("Retry logic added; tests pass"));

        // And the agent is TOLD where its history went, rather than being left
        // to believe it still has it.
        var digest = state.Messages[1].GetText();
        Assert.Contains(snapshot, digest);
    }

    [Fact]
    public async Task Condenser_SnapshotsBeforeTheAwait_SoACancelledRunKeepsItsHistory()
    {
        // ⭐ The snapshot must be written BEFORE the summarizer call, not after.
        // Writing it after would lose exactly the runs that failed mid-compaction
        // — the ones whose transcript is worth the most. A cancellation
        // propagates (it must never be laundered into a "successful" compaction),
        // so the only way the history survives is if it was already on disk.
        var llm = new LlmSettings(
            new StubChatClient(_ => throw new OperationCanceledException()), "test", 0.0);
        var mw = CompactionMiddleware.LLMSummarizingCondenser(
            llm, tokenThreshold: 1, keepLastMessages: 2, sessionLogDir: _dir, sessionId: "run2");

        var state = WorkState();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mw(state, default));

        var snapshot = Path.Combine(_dir, "compaction", "run2-condense1.jsonl");
        Assert.True(File.Exists(snapshot),
            "the condenser cleared or attempted to clear history without a snapshot on disk");

        var seed = HistorySeeder.LoadFromJsonl(snapshot);
        Assert.Contains(seed.Messages, m => m.GetText().Contains("Add retry logic to the uploader"));
    }

    [Fact]
    public async Task Condenser_CancellationPropagates_AndDoesNotLaunderIntoAStaticSummary()
    {
        // A cancelled compaction is a COULD-NOT-MEASURE, not a completed one.
        // If OperationCanceledException fell into the generic catch it would be
        // silently converted into "compaction succeeded with a static digest",
        // and the run would carry on having quietly destroyed its context on the
        // way out. The rethrow at the catch site is what prevents that.
        var llm = new LlmSettings(
            new StubChatClient(_ => throw new OperationCanceledException()), "test", 0.0);
        var mw = CompactionMiddleware.LLMSummarizingCondenser(
            llm, tokenThreshold: 1, keepLastMessages: 2, sessionLogDir: _dir);

        var state = WorkState();
        var before = state.Messages.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mw(state, default));

        // History untouched — the clear happens after the await, so a throw
        // leaves the conversation intact.
        Assert.Equal(before, state.Messages.Count);
        Assert.DoesNotContain(state.Messages,
            m => CompactionMiddleware.IsInjectedSummary(m.GetText()));
    }

    [Fact]
    public async Task Replan_CancellationPropagates()
    {
        var llm = new LlmSettings(
            new StubChatClient(_ => throw new OperationCanceledException()), "test", 0.0);
        var mw = CompactionMiddleware.ReplanCheckpoint(llm, tokenThreshold: 1, sessionLogDir: _dir);

        var state = WorkState();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mw(state, default));
        Assert.DoesNotContain(state.Messages,
            m => CompactionMiddleware.IsInjectedSummary(m.GetText()));
    }

    [Fact]
    public async Task SuccessiveMilestones_GetDistinctSnapshots()
    {
        var mw = CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 1, keepLastMessages: 2, model: null,
            sessionLogDir: _dir, sessionId: "runN");

        await mw(WorkState(), default);
        await mw(WorkState(), default);

        Assert.True(File.Exists(Path.Combine(_dir, "compaction", "runN-milestone1.jsonl")));
        Assert.True(File.Exists(Path.Combine(_dir, "compaction", "runN-milestone2.jsonl")));
    }

    /// <summary>
    /// Nothing a compaction writes may land in the directory vett-chat scans
    /// to build its session picker. Stated over the DIRECTORY, not over one
    /// filename, so it also covers a strategy added later.
    /// </summary>
    [Fact]
    public async Task NoStrategyWritesIntoTheSessionPickerDirectory()
    {
        var llm = new LlmSettings(new StubChatClient(_ => "summary"), "test", 0.0);

        await CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 1, keepLastMessages: 1, model: null, sessionLogDir: _dir)(WorkState(), default);
        await CompactionMiddleware.LLMSummarizingCondenser(
            llm, tokenThreshold: 1, keepLastMessages: 1, sessionLogDir: _dir)(WorkState(), default);
        await CompactionMiddleware.ReplanCheckpoint(
            llm, tokenThreshold: 1, sessionLogDir: _dir)(WorkState(), default);

        Assert.Empty(Directory.GetFiles(_dir, "*.jsonl"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_dir, "compaction"), "*.jsonl"));
    }

    [Fact]
    public void ResolveSnapshotDir_NestsUnderCompaction_ForBothDefaultAndExplicit()
    {
        Assert.Equal(
            Path.Combine(_dir, "compaction"),
            CompactionMiddleware.ResolveSnapshotDir(_dir));

        var fallback = CompactionMiddleware.ResolveSnapshotDir(null);
        Assert.EndsWith(Path.Combine("chat-sessions", "compaction"), fallback);
    }

    [Fact]
    public void TrySnapshot_ReturnsNullForEmptyHistory_AndForAnUnwritablePath()
    {
        // Empty history: nothing to preserve, so no file and no path to report.
        Assert.Null(CompactionMiddleware.TrySnapshot([], _dir, "id", 1, "milestone"));

        // Unwritable destination: best-effort by design — failing to compact is
        // how a run dies of context overflow, so a write failure must never
        // abort the compaction. It reports null so the caller can tell the agent
        // the truth instead of naming a file that isn't there.
        var badDir = Path.Combine(_dir, "nul\0bad");
        var result = CompactionMiddleware.TrySnapshot(
            [Chat.User("x")], badDir, "id", 1, "milestone");
        Assert.Null(result);
    }

    [Fact]
    public async Task WhenSnapshotFails_TheDigestSaysSo_RatherThanNamingAMissingFile()
    {
        // The digest must not promise a transcript that was never written. An
        // agent told to "read the full transcript at <path>" for a path that
        // does not exist burns iterations discovering that.
        var mw = CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 1, keepLastMessages: 1, model: null,
            sessionLogDir: Path.Combine(_dir, "nul\0bad"));

        var state = WorkState();
        await mw(state, default);

        var digest = state.Messages[1].GetText();
        Assert.Contains("could NOT be written", digest);
        Assert.DoesNotContain("FULL PRIOR TRANSCRIPT:", digest);
    }
}

/// <summary>
/// <c>BuildStaticDigest</c> replaced a summary that carried NO content — the
/// old milestone block was literally "[Milestone checkpoint at iteration N. M
/// tool calls made so far. Context was T tokens, reset to save space.]" A
/// token count is not a summary; an agent handed that has lost the task.
/// These assert the digest mechanically recovers real work from the transcript.
/// </summary>
public class StaticDigestTests
{
    private static List<ChatMessage> Transcript() =>
    [
        Chat.System("system"),
        Chat.User("Add retry logic to the uploader."),
        Chat.AssistantWithCalls(null, [new FunctionCallContent("c1", "file_editor",
            new Dictionary<string, object?> { ["command"] = "str_replace", ["path"] = "src/Uploader.cs" })]),
        Chat.ToolResult("c1", "ok"),
        Chat.AssistantWithCalls(null, [new FunctionCallContent("c2", "file_editor",
            new Dictionary<string, object?> { ["command"] = "create", ["path"] = "tests/UploaderTests.cs" })]),
        Chat.ToolResult("c2", "ok"),
        Chat.AssistantWithCalls(null, [new FunctionCallContent("c3", "terminal",
            new Dictionary<string, object?> { ["command"] = "dotnet test" })]),
        Chat.ToolResult("c3", "passed"),
    ];

    [Fact]
    public void RecoversOriginalTask_ToolCounts_AndPathsTouched()
    {
        var digest = CompactionMiddleware.BuildStaticDigest(
            Transcript(), iteration: 20, estimatedTokens: 51_000, snapshotPath: @"C:\snap\run1.jsonl");

        Assert.StartsWith("[Milestone checkpoint at iteration 20", digest);
        Assert.Contains("Add retry logic to the uploader.", digest);

        // Per-tool call counts, not just a total.
        Assert.Contains("file_editor", digest);
        Assert.Contains("terminal", digest);

        // Paths are pulled out of tool ARGUMENTS, which is where the real work
        // is recorded — an agent that knows which files it touched can pick the
        // task back up.
        Assert.Contains("src/Uploader.cs", digest);
        Assert.Contains("tests/UploaderTests.cs", digest);

        Assert.Contains(@"C:\snap\run1.jsonl", digest);
    }

    [Fact]
    public void SkipsOurOwnBlocksWhenLookingForTheOriginalTask()
    {
        // The first User turn is one of OUR blocks (as it is on every
        // compaction after the first). The digest must walk past it.
        var msgs = new List<ChatMessage>
        {
            Chat.System("system"),
            Chat.User("[Milestone checkpoint at iteration 4. earlier digest text]"),
            Chat.User("The REAL original ask."),
            Chat.Assistant("working"),
        };

        var digest = CompactionMiddleware.BuildStaticDigest(msgs, 9, 40_000, null);

        Assert.Contains("The REAL original ask.", digest);
        Assert.DoesNotContain("earlier digest text", digest);
    }

    [Fact]
    public void EmptyTranscript_ProducesAWarningNotACrash()
    {
        var digest = CompactionMiddleware.BuildStaticDigest([], 1, 100, null);
        Assert.False(string.IsNullOrWhiteSpace(digest));
        Assert.Contains("could NOT be written", digest);
    }
}

/// <summary>
/// ⛔ THE ORIGINAL ASK MUST NOT WALK FORWARD ONE TURN PER CHECKPOINT.
///
/// Re-deriving "the original task" from the surviving messages is correct
/// exactly once. On the second compaction the earliest real user turn is
/// whatever the user said mid-run — the actual first turn was discarded by the
/// first compaction — so the stated objective drifts, quietly, once per
/// checkpoint. On a run with ten compactions the agent is told its goal is some
/// incidental aside. These pin it down.
/// </summary>
public class OriginalTaskCaptureTests
{
    private static AgentState Fresh() => new()
    {
        Messages =
        [
            Chat.System("system"),
            Chat.User("Build feature X across the repo."),
            Chat.Assistant("ok"),
        ],
    };

    [Fact]
    public void CapturesTheFirstRealUserTurn_AndStoresIt()
    {
        var state = Fresh();
        Assert.Equal("Build feature X across the repo.", CompactionMiddleware.CaptureOriginalTask(state));
        Assert.Equal("Build feature X across the repo.", state.OriginalTask);
    }

    [Fact]
    public void OnceCaptured_ItIsNeverRecomputed()
    {
        var state = Fresh();
        CompactionMiddleware.CaptureOriginalTask(state);

        // Simulate the post-compaction shape: the original turn is gone and the
        // earliest real user turn is now a mid-run aside.
        state.Messages =
        [
            Chat.System("system"),
            Chat.User("[Milestone checkpoint at iteration 4. digest]"),
            Chat.User("keep going"),
        ];

        Assert.Equal("Build feature X across the repo.", CompactionMiddleware.CaptureOriginalTask(state));
    }

    [Fact]
    public void AfterResume_RecoversItFromThePriorDigest_NotFromTheMidRunAside()
    {
        // A --resume rebuilds AgentState from a JSONL, so OriginalTask is empty
        // but the digest survived in the transcript. Rung 2 exists for this.
        var digest = CompactionMiddleware.BuildStaticDigest(
            [Chat.User("Build feature X across the repo."), Chat.Assistant("ok")],
            iteration: 5, estimatedTokens: 40_000, snapshotPath: null);

        var state = new AgentState
        {
            Messages = [Chat.System("system"), Chat.User(digest), Chat.User("keep going")],
        };

        Assert.Equal("Build feature X across the repo.", CompactionMiddleware.CaptureOriginalTask(state));
    }

    [Fact]
    public void FallsBackToTheFirstRealTurn_WhenTheDigestCarriesNoTaskLine()
    {
        // A digest built from a transcript with no recoverable ask has no
        // ORIGINAL TASK section at all. Rung 2 must decline rather than return
        // an empty string that then gets cached as the task forever.
        var state = new AgentState
        {
            Messages =
            [
                Chat.System("system"),
                Chat.User("[Milestone checkpoint at iteration 4. Context was ~9 tokens.]"),
                Chat.User("keep going"),
            ],
        };

        Assert.Equal("keep going", CompactionMiddleware.CaptureOriginalTask(state));
    }

    [Fact]
    public void ReturnsNull_WhenThereIsNoUserTurnAtAll()
    {
        var state = new AgentState { Messages = [Chat.System("system")] };
        Assert.Null(CompactionMiddleware.CaptureOriginalTask(state));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no task header here")]
    [InlineData("ORIGINAL TASK: \n\nrest")]     // header with an empty value
    public void ExtractOriginalTask_DeclinesRatherThanReturningJunk(string? block)
    {
        Assert.Null(CompactionMiddleware.ExtractOriginalTask(block));
    }

    [Fact]
    public void ExtractOriginalTask_StopsAtTheNextSection()
    {
        var block = "[Milestone checkpoint at iteration 3]\n\nORIGINAL TASK: the ask\n\nTOOL USE (2 calls): a×2\n";
        Assert.Equal("the ask", CompactionMiddleware.ExtractOriginalTask(block));
    }

    [Fact]
    public async Task EveryStrategyStampsARecoverableTaskLine()
    {
        // Round-trip across all four block shapes: whatever each strategy
        // injects, the NEXT checkpoint must be able to read the task back out.
        // This is what makes the drift fix hold on a ten-compaction run rather
        // than just the second one.
        var dir = Path.Combine(Path.GetTempPath(), "vett-task-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var okLlm = new LlmSettings(new StubChatClient(_ => "body"), "test", 0.0);
            var badLlm = new LlmSettings(
                new StubChatClient(_ => throw new InvalidOperationException("down")), "test", 0.0);

            var strategies = new (string Name, MiddlewareFn Fn)[]
            {
                ("milestone", CompactionMiddleware.MilestoneCheckpoint(1, 1, null, dir)),
                ("condense-llm", CompactionMiddleware.LLMSummarizingCondenser(okLlm, 1, 1, dir)),
                ("condense-static", CompactionMiddleware.LLMSummarizingCondenser(badLlm, 1, 1, dir)),
                ("replan-llm", CompactionMiddleware.ReplanCheckpoint(okLlm, 1, dir)),
                ("replan-static", CompactionMiddleware.ReplanCheckpoint(badLlm, 1, dir)),
            };

            foreach (var (name, fn) in strategies)
            {
                var state = new AgentState
                {
                    Iteration = 3,
                    Messages =
                    [
                        Chat.System("system"),
                        Chat.User("Build feature X across the repo."),
                        Chat.Assistant(new string('b', 900)),
                        Chat.User("keep going"),
                    ],
                };

                await fn(state, default);

                var block = state.Messages.First(m =>
                    m.IsRole(ChatRole.User) && CompactionMiddleware.IsInjectedSummary(m.GetText())).GetText();

                Assert.Equal("Build feature X across the repo.",
                    CompactionMiddleware.ExtractOriginalTask(block));
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

/// <summary>
/// ⛔ THE ANCHOR. <c>TokenEstimator.Estimate</c> cannot see tool schemas —
/// measured at 58.3% of a real captured request body, making the estimate
/// ~2.4x LOW on that turn — and no production model matches its ratio table,
/// so every live seat takes the uncalibrated 4.0 default. Both errors point
/// the same dangerous way: compaction fires LATER than the profile asked,
/// which is the context overflow the feature exists to prevent.
///
/// <c>EstimateContext</c> fixes this by anchoring on the provider's own
/// InputTokenCount — a number AgentLoop already read one line away and threw
/// away — and guessing only the messages appended since.
/// </summary>
public class ContextAnchorTests
{
    private static List<ChatMessage> Msgs(int n)
        => Enumerable.Range(0, n).Select(i => Chat.User($"message {i}")).ToList();

    [Fact]
    public void WithNoAnchor_FallsBackToThePlainHeuristic()
    {
        var msgs = Msgs(5);
        Assert.Equal(
            TokenEstimator.Estimate(msgs, null),
            TokenEstimator.EstimateContext(msgs, anchorTokens: 0, anchorMessageCount: 0));
    }

    [Fact]
    public void WithAnAnchor_UsesTheRealCount_NotTheHeuristic()
    {
        var msgs = Msgs(5);
        var heuristic = TokenEstimator.Estimate(msgs, null);

        // The provider says this exact prompt was 40_000 tokens — schemas and
        // all. The heuristic, which cannot see schemas, says far less.
        var anchored = TokenEstimator.EstimateContext(msgs, 40_000, msgs.Count);

        Assert.Equal(40_000, anchored);
        Assert.True(anchored > heuristic,
            "the whole point is that the real count exceeds what the heuristic can see");
    }

    [Fact]
    public void MessagesAddedSinceTheAnchor_AreAddedOnTop()
    {
        var msgs = Msgs(5);
        var anchoredAtFive = TokenEstimator.EstimateContext(msgs, 40_000, 5);

        msgs.Add(Chat.User(new string('x', 4000)));
        var anchoredAtSix = TokenEstimator.EstimateContext(msgs, 40_000, 5);

        Assert.True(anchoredAtSix > anchoredAtFive,
            "a message appended after the anchored request must raise the estimate");
        // ~4000 chars at the 4.0 default ≈ 1000 tokens, plus per-message framing.
        Assert.InRange(anchoredAtSix - anchoredAtFive, 1000, 1100);
    }

    [Fact]
    public void AStaleAnchorLongerThanTheMessageList_IsIgnored()
    {
        // Defence in depth for a caller that clears history and forgets to
        // invalidate: an anchor describing more messages than exist cannot be
        // describing this conversation.
        var msgs = Msgs(2);
        Assert.Equal(
            TokenEstimator.Estimate(msgs, null),
            TokenEstimator.EstimateContext(msgs, 99_000, anchorMessageCount: 50));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(500, -1)]
    public void InvalidAnchors_DegradeToTheHeuristic(int anchorTokens, int anchorCount)
    {
        var msgs = Msgs(3);
        Assert.Equal(
            TokenEstimator.Estimate(msgs, null),
            TokenEstimator.EstimateContext(msgs, anchorTokens, anchorCount));
    }
}

/// <summary>
/// ⛔ THE SHREDDER REGRESSION. A clearing strategy that leaves the anchor in
/// place describes a prompt that no longer exists. EstimateContext would keep
/// reporting the PRE-compaction size, so the threshold would still be
/// exceeded, so compaction would fire again on the very next iteration — and
/// again after that, each pass eating another keep-window while the number it
/// reacts to never moves. A self-sustaining shredder driven by a stale
/// integer. These tests are the only thing standing between that and a long
/// Pro run.
/// </summary>
public class ContextAnchorInvalidationTests : IDisposable
{
    private readonly string _dir;

    public ContextAnchorInvalidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-anchor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AgentState AnchoredState() => new()
    {
        Iteration = 30,
        // The provider measured this prompt at 60k — well over any threshold
        // below — and there were 6 messages when it did.
        LastRealInputTokens = 60_000,
        LastRealInputMessageCount = 6,
        Messages =
        [
            Chat.System("system"),
            Chat.User("Original ask."),
            Chat.Assistant("a"),
            Chat.User("b"),
            Chat.Assistant("c"),
            Chat.User("d"),
        ],
    };

    [Fact]
    public async Task Milestone_InvalidatesTheAnchor_SoItDoesNotReFireImmediately()
    {
        var mw = CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 50_000, keepLastMessages: 2, model: null, sessionLogDir: _dir);

        var state = AnchoredState();
        await mw(state, default);

        Assert.Equal(0, state.LastRealInputTokens);
        Assert.Equal(0, state.LastRealInputMessageCount);

        // The real proof is behavioural: run the middleware again with no new
        // response in between. It must do NOTHING, because the compacted
        // conversation is genuinely small now.
        var afterFirst = state.Messages.Count;
        await mw(state, default);
        Assert.Equal(afterFirst, state.Messages.Count);
    }

    [Fact]
    public async Task Condenser_InvalidatesTheAnchor()
    {
        var llm = new LlmSettings(new StubChatClient(_ => "summary"), "test", 0.0);
        var mw = CompactionMiddleware.LLMSummarizingCondenser(
            llm, tokenThreshold: 50_000, keepLastMessages: 2, sessionLogDir: _dir);

        var state = AnchoredState();
        await mw(state, default);

        Assert.Equal(0, state.LastRealInputTokens);

        var afterFirst = state.Messages.Count;
        var callsAfterFirst = 1;
        await mw(state, default);
        Assert.Equal(afterFirst, state.Messages.Count);
        Assert.Equal(callsAfterFirst, ((StubChatClient)llm.Client).CallCount);
    }

    [Fact]
    public async Task Replan_InvalidatesTheAnchor()
    {
        var llm = new LlmSettings(new StubChatClient(_ => "GOAL: x"), "test", 0.0);
        var mw = CompactionMiddleware.ReplanCheckpoint(
            llm, tokenThreshold: 50_000, sessionLogDir: _dir);

        var state = AnchoredState();
        await mw(state, default);

        Assert.Equal(0, state.LastRealInputTokens);

        var afterFirst = state.Messages.Count;
        await mw(state, default);
        Assert.Equal(afterFirst, state.Messages.Count);
    }

    [Fact]
    public async Task TheAnchorIsWhatMakesCompactionFire_NotTheHeuristic()
    {
        // Two-sided: the SAME small conversation, the SAME threshold. With no
        // anchor the heuristic sees a tiny prompt and nothing happens. With the
        // provider's real count it is over threshold and compaction runs. This
        // is the defect the anchor exists to fix, expressed as a test.
        var mw = CompactionMiddleware.MilestoneCheckpoint(
            tokenThreshold: 50_000, keepLastMessages: 2, model: null, sessionLogDir: _dir);

        var unanchored = AnchoredState();
        unanchored.LastRealInputTokens = 0;
        unanchored.LastRealInputMessageCount = 0;
        var before = unanchored.Messages.Count;
        await mw(unanchored, default);
        Assert.Equal(before, unanchored.Messages.Count);
        Assert.DoesNotContain(unanchored.Messages,
            m => CompactionMiddleware.IsInjectedSummary(m.GetText()));

        var anchored = AnchoredState();
        await mw(anchored, default);
        Assert.Contains(anchored.Messages,
            m => CompactionMiddleware.IsInjectedSummary(m.GetText()));
    }
}

/// <summary>
/// ⛔ A DECLARED COMPACTION STRATEGY MUST NEVER RESOLVE TO NOTHING.
///
/// <c>llm_summarizing_condenser</c> and <c>replan_checkpoint</c> register only
/// when an LlmSettings is threaded in, and two of five call sites didn't
/// thread one. The name then fell through to a silent skip, so a profile that
/// declared a condenser and a tuned threshold ran with NO compaction at all.
/// Both ds-solo-* profiles name the condenser as their only strategy.
/// </summary>
public class MiddlewareDegradationTests : IDisposable
{
    private readonly string _dir;

    public MiddlewareDegradationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vett-degrade-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Condenser_WithoutLlm_DegradesToMilestone_AndSaysSo()
    {
        var notes = new List<string>();
        var chain = MiddlewareResolver.Resolve(
            ["llm_summarizing_condenser"], extras: null, llm: null,
            compaction: new CompactionConfig { ThresholdTokens = 48_000 },
            onDiagnostic: notes.Add);

        // submit_detector is force-prepended, so a working chain is 2 entries:
        // the completion signal plus the substituted compaction.
        Assert.Equal(2, chain.Count);

        var note = Assert.Single(notes);
        Assert.Contains("llm_summarizing_condenser", note);
        Assert.Contains("milestone_checkpoint", note);
        Assert.Contains("48000", note);
    }

    [Fact]
    public async Task TheSubstitutedStrategy_ActuallyCompacts()
    {
        // A chain of the right LENGTH proves nothing — the substitute has to
        // do the job. Run the resolved chain and check the conversation was
        // actually compacted at the profile's threshold.
        var chain = MiddlewareResolver.Resolve(
            ["llm_summarizing_condenser"], extras: null, llm: null,
            compaction: new CompactionConfig
            {
                ThresholdTokens = 100,
                KeepLastMessages = 1,
                // ⚠ Required: this test crosses the threshold, and an unset dir
                // snapshots into the user's real ~/.vett/chat-sessions/.
                SessionLogDir = _dir,
            });

        var state = new AgentState
        {
            Iteration = 3,
            Messages =
            [
                Chat.System("system"),
                Chat.User("the original ask"),
                Chat.Assistant(new string('b', 4000)),
                Chat.User("latest"),
            ],
        };

        foreach (var mw in chain) await mw(state, default);

        Assert.Contains(state.Messages, m => CompactionMiddleware.IsInjectedSummary(m.GetText()));
    }

    [Fact]
    public void ReplanAtZeroThreshold_IsInert_AndIsNotSubstituted()
    {
        // replan defaults to threshold 0 = disabled, so listing it costs
        // nothing by design. Standing in for a middleware that was never going
        // to fire would ADD compaction the profile did not ask for.
        var notes = new List<string>();
        var chain = MiddlewareResolver.Resolve(
            ["replan_checkpoint"], extras: null, llm: null,
            compaction: new CompactionConfig { ReplanThresholdTokens = 0 },
            onDiagnostic: notes.Add);

        Assert.Single(chain);  // submit_detector only
        Assert.Contains(notes, n => n.Contains("inert"));
    }

    [Fact]
    public void ReplanWithARealThreshold_IsSubstitutedAtThatThreshold()
    {
        var notes = new List<string>();
        MiddlewareResolver.Resolve(
            ["replan_checkpoint"], extras: null, llm: null,
            compaction: new CompactionConfig { ReplanThresholdTokens = 90_000 },
            onDiagnostic: notes.Add);

        Assert.Contains(notes, n => n.Contains("90000"));
    }

    [Fact]
    public void AgentFinishedCritic_IsReportedButDeliberatelyNotSubstituted()
    {
        // There is no non-LLM critic, and its omission on benchmark paths is
        // intentional policy rather than the accident the condenser's was.
        var notes = new List<string>();
        var chain = MiddlewareResolver.Resolve(
            ["agent_finished_critic"], extras: null, llm: null, onDiagnostic: notes.Add);

        Assert.Single(chain);
        Assert.Contains(notes, n => n.Contains("expected on benchmark paths"));
    }

    [Fact]
    public void AnUnknownName_IsStillNonFatal_ButNoLongerInvisible()
    {
        var notes = new List<string>();
        var chain = MiddlewareResolver.Resolve(
            ["definitely_not_a_middleware"], onDiagnostic: notes.Add);

        Assert.Single(chain);
        Assert.Contains(notes, n => n.Contains("unknown middleware"));
    }

    [Fact]
    public void WithAnLlm_TheRealCondenserRegisters_AndNothingIsReported()
    {
        var notes = new List<string>();
        var llm = new LlmSettings(new StubChatClient(_ => "s"), "test", 0.0);
        var chain = MiddlewareResolver.Resolve(
            ["llm_summarizing_condenser", "replan_checkpoint"],
            extras: null, llm: llm,
            compaction: new CompactionConfig { ReplanThresholdTokens = 90_000 },
            onDiagnostic: notes.Add);

        Assert.Equal(3, chain.Count);  // submit_detector + both strategies
        Assert.Empty(notes);
    }

    [Fact]
    public void MilestoneNeedsNoLlm_AndIsNeverDegraded()
    {
        var notes = new List<string>();
        var chain = MiddlewareResolver.Resolve(
            ["milestone_checkpoint"], extras: null, llm: null, onDiagnostic: notes.Add);

        Assert.Equal(2, chain.Count);
        Assert.Empty(notes);
    }
}
