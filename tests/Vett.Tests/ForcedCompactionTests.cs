using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// The FORCED compaction path — what `/compact` in vett-chat, and a
/// <c>{"type":"compact"}</c> line on the stdio wire, actually do.
///
/// Two defects, both found 2026-08-26 by a LIVE probe against the local
/// Flash endpoint rather than by unit tests, which is the point worth
/// recording: every existing compaction test called the middleware
/// DIRECTLY with a hand-built message list, so all of them passed while
/// the wiring between the profile and this path was wrong. A test that
/// constructs its own inputs cannot catch a defect in who supplies them.
///
///   A. The forced path called
///          MilestoneCheckpoint(tokenThreshold: 0, model: llm.Model)
///      and passed nothing else, so it silently took the METHOD defaults
///      (keep 5, snapshot to the shared default dir) regardless of the
///      profile's `compaction:` block. Every AUTOMATIC checkpoint in the
///      same session honoured that block. The two paths disagreed, and
///      the one the user explicitly asked for was the one that ignored
///      the config.
///
///   B. The emit was gated on `state.Messages.Count &lt; before`. A
///      checkpoint rebuilds the list as system + summary + ack + last N,
///      which on a SHORT history is LARGER than what it replaced — so the
///      history was genuinely rewritten to a digest and the event never
///      fired. The user pressed /compact and the UI showed silence.
///      Counting MESSAGES to detect a change that is about CONTENT reads
///      the wrong quantity.
///
/// These drive the REAL <see cref="AgentLoop.RunInteractiveAsync"/> with a
/// scripted client, so they exercise the same call site production uses.
/// </summary>
public class ForcedCompactionTests
{
    // ---- fixtures ---------------------------------------------------------

    private sealed class TextClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NoSandbox : ISandbox
    {
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("", 0, "/fake", false));
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

    /// <summary>
    /// Runs one interactive turn with a pending forced compaction and returns
    /// every `compacted` event the loop emitted.
    ///
    /// The compaction is requested BEFORE the loop starts because the flag is
    /// claimed at the TOP of an iteration, and the loop parks on the input
    /// await until a message arrives — so a request with no following message
    /// would sit pending forever. That ordering is itself a thing worth
    /// encoding: the live probe had to send `compact` AND a user message.
    /// </summary>
    private static async Task<List<Dictionary<string, object?>>> RunForcedCompactionAsync(
        int seedMessages, int keepLastMessages)
    {
        var seed = new List<ChatMessage>();
        for (int i = 0; i < seedMessages; i++)
        {
            seed.Add(i % 2 == 0
                ? new ChatMessage(ChatRole.User, $"user turn {i}")
                : new ChatMessage(ChatRole.Assistant, $"assistant turn {i}"));
        }

        var events = new List<Dictionary<string, object?>>();
        var env = new AgentEnvironment(
            new NoSandbox(), "forced-compaction-test", MaxIterations: 2,
            OnEvent: e =>
            {
                if (e.Type == "compacted") events.Add(e.Data);
            });

        var caps = new AgentCapabilities([], [], []);
        var llm = new LlmSettings(new TextClient(), "test-model");

        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("go");
        ch.Writer.Complete();   // one turn, then the loop finalizes

        var compact = new CompactRequest
        {
            KeepLastMessages = keepLastMessages,
            SessionLogDir = Path.Combine(Path.GetTempPath(), "vett-forced-compaction-test"),
        };
        compact.Request();

        await AgentLoop.RunInteractiveAsync(
            llm, caps, env, "SYSTEM", ch.Reader,
            onAssistantText: null, onWaitingForInput: null,
            seedHistory: seed, turnInterrupt: null, compactRequest: compact,
            ct: CancellationToken.None);

        return events;
    }

    private static int Int(Dictionary<string, object?> d, string k) => Convert.ToInt32(d[k]);

    // ---- A. the profile's keep_last_messages must REACH the checkpoint -----

    /// <summary>
    /// Two-sided: the SAME history compacted with two different
    /// keep_last_messages must survive at two different sizes. A one-sided
    /// "it didn't crash" assertion would have passed against the defect,
    /// because the defect was a silently-substituted default, not an error.
    ///
    /// The checkpoint rebuilds as system + summary + ack + last N, and the
    /// seeded history here is plain text (no tool calls), so nothing is
    /// orphan-trimmed and `after` is exactly N + 3.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public async Task Forced_compaction_honours_the_profiles_keep_last_messages(int keepLast)
    {
        var events = await RunForcedCompactionAsync(seedMessages: 10, keepLastMessages: keepLast);

        var forced = Assert.Single(events, e => (string?)e["reason"] == "user_requested");
        Assert.Equal(keepLast + 3, Int(forced, "after"));
    }

    /// <summary>
    /// The contrast stated as one assertion rather than three independent
    /// runs: if keep_last were ignored (the defect), all three would collapse
    /// to the SAME value — the method default of 5, i.e. 8. Asserting they
    /// DIFFER is what makes this a test of the plumbing rather than of the
    /// checkpoint's arithmetic.
    /// </summary>
    [Fact]
    public async Task Different_keep_last_values_produce_different_kept_windows()
    {
        var small = await RunForcedCompactionAsync(seedMessages: 10, keepLastMessages: 2);
        var large = await RunForcedCompactionAsync(seedMessages: 10, keepLastMessages: 6);

        var a = Int(small.Single(e => (string?)e["reason"] == "user_requested"), "after");
        var b = Int(large.Single(e => (string?)e["reason"] == "user_requested"), "after");

        Assert.True(a < b,
            $"keep_last_messages did not reach the forced checkpoint: keep=2 -> {a}, keep=6 -> {b}. " +
            "Equal values mean both runs took the method default.");
    }

    // ---- B. the event must fire even when the count does not shrink -------

    /// <summary>
    /// THE DEFECT-B REGRESSION. A history shorter than the keep window
    /// rebuilds LARGER than it started, because system + summary + ack are
    /// added on top of a window that already holds everything. Under the old
    /// `if (Count &lt; before)` guard this emitted NOTHING.
    ///
    /// The assertion is that the event FIRES, not that the count shrank —
    /// on this path "did a compaction happen" is known (threshold 0 always
    /// fires, and the user asked for it), so it must never be inferred from
    /// a proxy that can point the wrong way.
    /// </summary>
    [Fact]
    public async Task Forced_compaction_emits_even_when_the_message_count_grows()
    {
        // 2 seeded + 1 user message + system = 4, against a keep window of 5.
        var events = await RunForcedCompactionAsync(seedMessages: 2, keepLastMessages: 5);

        var forced = Assert.Single(events, e => (string?)e["reason"] == "user_requested");
        var before = Int(forced, "before");
        var after = Int(forced, "after");

        Assert.True(after > before,
            $"expected the degenerate short-history growth this test exists to pin, got {before} -> {after}");
        Assert.True((bool)forced["grew"]!);
    }

    /// <summary>
    /// ⚠ KNOWN DEGENERATE BEHAVIOUR, PINNED DELIBERATELY — NOT AN ENDORSEMENT.
    ///
    /// When the history is at or below the keep window, `TakeLast(N)` scoops
    /// up the SYSTEM message too, and the rebuild then re-adds it at the
    /// front — so the system prompt appears TWICE and real turns are replaced
    /// by a digest that summarises almost nothing. Compaction here costs
    /// fidelity and buys no context back.
    ///
    /// This is pinned rather than fixed because the correct fix — skip the
    /// rebuild entirely when nothing would be removed — lives inside
    /// MilestoneCheckpoint, which every AUTOMATIC path shares, so it is a
    /// wider change than the forced-path bug this file was opened for.
    /// Bundling it would make one red test ambiguous between two causes.
    ///
    /// If someone fixes it, THIS TEST GOES RED. That is the intended signal:
    /// come back and update it, don't work around it.
    /// </summary>
    [Fact]
    public async Task Short_history_compaction_duplicates_the_system_message_KNOWN_DEGENERATE()
    {
        var events = await RunForcedCompactionAsync(seedMessages: 2, keepLastMessages: 5);
        var forced = events.Single(e => (string?)e["reason"] == "user_requested");

        // before = system + 2 seeded + 1 user = 4
        // after  = system + summary + ack + TakeLast(5)=all 4  = 7
        Assert.Equal(4, Int(forced, "before"));
        Assert.Equal(7, Int(forced, "after"));
    }
}
