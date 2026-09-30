using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Vett.Agent;
using Vett.Bench.Team;
using Vett.Config;
using Vett.Llm;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// Guards the self-assessment CAPTURE OUTCOME discriminator.
///
/// WHY THIS EXISTS: `Captured` is a single bool, and three non-interchangeable
/// states collapsed into `false`:
///   - the model was asked and returned nothing  (a real measurement — it declined)
///   - the follow-up CALL threw                  (we never got to ask)
///   - the leader faulted, so there was nothing to ask at all
///
/// Failure-flagging recall is computed over exactly that column. Pooling a
/// thrown call with a genuine refusal lets HARNESS BREAKAGE masquerade as MODEL
/// CALIBRATION — and it biases hardest on long runs, because the direct ask
/// seeds from the leader's full message history, so the longer the run the more
/// likely the call is the thing that failed. COULD NOT MEASURE IS NOT MEASURED
/// ZERO.
///
/// The failure was already logged as a warning before this change. A log line
/// is not a column the analyzer reads — the discriminator has to be in the DATA.
/// </summary>
public class SelfAssessmentCaptureOutcomeTests
{
    private static Profile ProfileWith(LlmConfig? leaderLlm = null) => new()
    {
        Llm = new LlmConfig { Model = "base-model", Temperature = 0.0 },
        Team = leaderLlm is null ? null : new TeamConfig { Leader = new MemberConfig { Llm = leaderLlm } },
    };

    private static Task<TeamBenchSelfAssessment> AskAsync(StubChatClient stub, Profile? profile = null) =>
        Harness.AskSelfAssessmentDirectAsync(
            profile ?? ProfileWith(),
            stub,
            "base-model",
            [Chat.User("do the thing"), Chat.Assistant("done")],
            NullLogger.Instance,
            "inst-1",
            default);

    /// <summary>
    /// ⭐ THE POSITIVE CONJUNCT. Without it, an implementation that reported
    /// "ask_failed" for every input would satisfy every other assertion here.
    /// </summary>
    [Fact]
    public async Task A_real_reply_is_answered_and_captured()
    {
        var sa = await AskAsync(new StubChatClient(_ => "CONFIDENCE: 80\nREASON: tests pass"));

        Assert.True(sa.Captured);
        Assert.Equal("answered", sa.CaptureOutcome);
        Assert.Equal(80, sa.Confidence);
        Assert.True(sa.PredictedPass);
    }

    /// <summary>
    /// THE WHOLE POINT OF THE CHANGE, stated as itself: the two states that were
    /// byte-identical before must now be distinguishable. Asserting each label
    /// separately would still pass if both were mapped to the same string, so
    /// the inequality is asserted directly.
    /// </summary>
    [Fact]
    public async Task A_thrown_call_and_an_empty_reply_are_not_the_same_state()
    {
        var threw = await AskAsync(new StubChatClient(_ => throw new HttpRequestException("context overflow")));
        var empty = await AskAsync(new StubChatClient(_ => "   "));

        // Both are still "not captured" — that meaning is unchanged, so old
        // summaries and scripts reading `captured` keep working.
        Assert.False(threw.Captured);
        Assert.False(empty.Captured);

        // ...but they are no longer the same record.
        Assert.Equal("ask_failed", threw.CaptureOutcome);
        Assert.Equal("refused_empty", empty.CaptureOutcome);
        Assert.NotEqual(threw.CaptureOutcome, empty.CaptureOutcome);
    }

    /// <summary>
    /// A thrown ask must not be scored as a model that answered badly: no
    /// confidence, no predicted_pass. If either leaked through, an
    /// infrastructure fault would enter the recall computation as data.
    /// </summary>
    [Fact]
    public async Task A_thrown_call_contributes_no_confidence_to_the_analyzer()
    {
        var sa = await AskAsync(new StubChatClient(_ => throw new TimeoutException("no route")));

        Assert.Null(sa.Confidence);
        Assert.Null(sa.PredictedPass);
        Assert.Equal("ask_failed", sa.CaptureOutcome);
    }

    /// <summary>
    /// A reply that came back but didn't carry a CONFIDENCE token is CAPTURED —
    /// the model answered, just not in the requested format. That is a parse
    /// outcome, not a capture failure, and the raw text is kept for diagnosis.
    /// </summary>
    [Fact]
    public async Task An_unparseable_reply_still_counts_as_answered()
    {
        var sa = await AskAsync(new StubChatClient(_ => "I think it went fine, honestly."));

        Assert.True(sa.Captured);
        Assert.Equal("answered", sa.CaptureOutcome);
        Assert.Null(sa.Confidence);
        Assert.Contains("honestly", sa.RawText);
    }

    /// <summary>
    /// The ask must route through the LEADER's model when the profile binds one
    /// (`ds-team-lead-pro` is exactly that shape). Asking the base model while
    /// filing the answer under the leader's row attributes one model's
    /// calibration to another.
    /// </summary>
    [Fact]
    public async Task The_question_goes_to_the_leaders_model_not_the_base_model()
    {
        // Baseline: with no leader override the base client is reused as-is and
        // the base model is what gets asked.
        var noOverride = new ModelCapturingClient();
        await Harness.AskSelfAssessmentDirectAsync(
            ProfileWith(), noOverride, "base-model",
            [Chat.User("x")], NullLogger.Instance, "inst-base", default);
        Assert.Equal("base-model", noOverride.LastModelId);

        // With a leader override, the model id must be the LEADER's. Asserted
        // on ResolveClient directly: overriding the model makes it construct a
        // real client, which necessarily replaces the stub, so the ask path
        // cannot observe this — the routing rule lives here.
        var (_, resolved) = TeamCoordinator.ResolveClient(
            new LlmConfig { Endpoint = "http://127.0.0.1:1/v1", Model = "base-model" },
            noOverride, "base-model",
            new LlmConfig { Model = "leader-model" });

        Assert.Equal("leader-model", resolved);
    }

    /// <summary>
    /// A leader LLM config that cannot BUILD a client (no endpoint for a local
    /// provider) must degrade to ask_failed — not throw.
    ///
    /// ResolveClient constructs a new client whenever the leader overrides a
    /// client-shaping field, and construction validates config. That call used
    /// to sit ABOVE this method's try, so the throw escaped and an optional
    /// end-of-run diagnostic could take down a run whose assertions had already
    /// passed. "A failed question is not a failed run" has to cover failing to
    /// build the questioner, not just the call.
    /// </summary>
    [Fact]
    public async Task A_leader_client_that_cannot_be_built_degrades_instead_of_throwing()
    {
        var sa = await Harness.AskSelfAssessmentDirectAsync(
            ProfileWith(new LlmConfig { Model = "leader-model" }),   // no endpoint anywhere
            new ModelCapturingClient(), "base-model",
            [Chat.User("x")], NullLogger.Instance, "inst-unbuildable", default);

        Assert.False(sa.Captured);
        Assert.Equal("ask_failed", sa.CaptureOutcome);
    }

    /// <summary>Records the ModelId the harness actually asked with.</summary>
    private sealed class ModelCapturingClient : IChatClient
    {
        public string? LastModelId { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            LastModelId = options?.ModelId;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "CONFIDENCE: 55")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException("streaming not used by these tests");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
