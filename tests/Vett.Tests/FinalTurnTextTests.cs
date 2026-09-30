using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Llm;
using Vett.Runner;
using Vett.Tools;
using Xunit;

namespace Vett.Tests;

/// <summary>
/// ⭐ A REFUSAL DELIVERED THROUGH THE SUBMIT TOOL IS STILL A REFUSAL.
///
/// Third site of one defect, found by sweeping the CONCEPT ("an agent that
/// answered only by submitting loses its answer") rather than the string. The
/// other two are in Coordinator (leader-facing report) and are covered by
/// DispatchFailOpenTests. This one is measurement-facing: it decides which
/// bucket a benchmark instance lands in.
/// </summary>
public class FinalTurnTextTests
{
    private const string Refusal = "I cannot solve this problem.";

    private static AgentResult Result(params ChatMessage[] messages)
        => new() { Messages = [.. messages], StopReason = "finish_tool" };

    private static ChatMessage BareCall(string id, string tool, string arg)
        => new(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(id, tool, new Dictionary<string, object?> { ["message"] = arg }),
        });

    private static ChatMessage Submitted(string id, string summary)
        => Chat.ToolResult(id, Builtins.SubmitMarker + summary);

    [Fact]
    public void Prose_in_the_last_turn_is_the_final_word()
    {
        var r = Result(
            new ChatMessage(ChatRole.Assistant, "working on it"),
            Chat.ToolResult("t1", "ok"),
            new ChatMessage(ChatRole.Assistant, "All tests pass."));

        Assert.Equal("All tests pass.", r.FinalTurnText());
    }

    /// <summary>
    /// THE FALSIFIER. Before the fix this returned null, so
    /// ResolutionClassifier never saw the refusal.
    /// </summary>
    [Fact]
    public void A_refusal_submitted_through_the_finish_tool_is_the_final_word()
    {
        var r = Result(
            new ChatMessage(ChatRole.Assistant, "let me look"),
            Chat.ToolResult("t1", "ok"),
            BareCall("f1", "finish", Refusal),
            Submitted("f1", Refusal));

        Assert.Equal(Refusal, r.FinalTurnText());
    }

    /// <summary>
    /// The same run, end to end through the classifier — because the harm is
    /// not "a string is null", it is "a refusal is counted as a confident wrong
    /// answer". Without the fix this returns FALSE_CONFIDENCE.
    /// </summary>
    [Fact]
    public void A_refusal_submitted_through_the_finish_tool_is_classified_ABSTAIN()
    {
        var r = Result(
            BareCall("f1", "finish", Refusal),
            Submitted("f1", Refusal));

        var instance = new InstanceResult
        {
            EndReason = "finish_tool",
            TestsPassed = false,
            LastAssistantMessage = r.FinalTurnText(),
        };

        Assert.Equal(ResolutionClassifier.Abstain, ResolutionClassifier.Classify(instance));
    }

    /// <summary>
    /// ⛔ THE TURN BOUNDARY IS THE POINT. An agent that submitted a refusal,
    /// was un-stopped (CriticMiddleware.cs:67 clears the stop reason and the
    /// loop continues), and then went on to work is NOT abstaining. Scanning
    /// the whole history — which is what the leader-facing extractor in
    /// Coordinator deliberately does — would resurrect that refusal here and
    /// relabel a PASS.
    /// </summary>
    [Fact]
    public void A_submission_the_agent_continued_past_is_not_its_final_word()
    {
        var r = Result(
            BareCall("f1", "finish", Refusal),
            Submitted("f1", Refusal),
            new ChatMessage(ChatRole.Assistant, "actually, second thought"),
            Chat.ToolResult("t2", "ok"),
            BareCall("t3", "terminal", "echo done"),
            Chat.ToolResult("t3", "done"));

        Assert.Null(r.FinalTurnText());
    }

    /// <summary>
    /// Same precedence as the leader-facing extractor: spoken text wins over a
    /// submission made in the same turn, so nothing that reads correctly today
    /// reads differently after this change.
    /// </summary>
    [Fact]
    public void Prose_beats_a_submission_made_in_the_same_turn()
    {
        var spoke = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent("Fixed the bug."),
            new FunctionCallContent("f1", "finish",
                new Dictionary<string, object?> { ["message"] = Refusal }),
        });

        var r = Result(spoke, Submitted("f1", Refusal));

        Assert.Equal("Fixed the bug.", r.FinalTurnText());
    }

    /// <summary>
    /// A run with no assistant turn at all said nothing. Null, not "" — the
    /// classifier's `is not null` test has to keep meaning "the agent spoke".
    /// </summary>
    [Fact]
    public void A_run_with_no_assistant_turn_has_no_final_word()
        => Assert.Null(Result(Chat.ToolResult("t1", "ok")).FinalTurnText());
}
