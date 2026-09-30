using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Llm;

namespace Vett.Tools;

/// <summary>
/// LLM-backed critics that run between iterations to second-guess the
/// agent's stop decisions. Today: <see cref="AgentFinishedCritic"/>.
///
/// Critics make their own LLM calls outside the main agent loop, so
/// they need <see cref="LlmSettings"/> at construction time.
/// <see cref="MiddlewareResolver.ResolveOrDefault"/> picks them up when
/// the profile lists their YAML name AND the caller passes an
/// <c>LlmSettings</c> through (chat does; benchmarks don't, by design
/// — extra LLM calls per instance are too expensive at scale and the
/// benchmark grader is the authoritative judge anyway).
/// </summary>
public static class CriticMiddleware
{
    /// <summary>
    /// Catches premature <c>finish</c> calls. After SubmitDetector flips
    /// <see cref="AgentState.StopLoop"/> with reason <c>finish_tool</c>,
    /// the critic asks a fresh LLM "did the agent actually complete the
    /// user's request?" If the verdict is anything other than LGTM the
    /// loop is un-stopped, the verdict is injected as a user-style turn,
    /// and the agent gets one more chance to actually finish.
    ///
    /// Capped via <paramref name="maxRetries"/> so a contrarian critic
    /// can't infinite-loop the agent. Closure-scoped counter →
    /// per-session counter (each MiddlewareResolver call constructs a
    /// fresh delegate; resolver is called once per RunInteractiveAsync).
    /// </summary>
    public static MiddlewareFn AgentFinishedCritic(
        LlmSettings llm,
        int maxRetries = 1,
        int transcriptCharCap = 6000)
    {
        var retriesUsed = 0;

        return async (state, ct) =>
        {
            // Critic only runs when SubmitDetector latched the loop on
            // a finish_tool call. Other stop reasons (max_iterations,
            // stuck:*, llm_error) are out of scope — the agent didn't
            // claim to be done, so there's nothing to second-guess.
            if (!state.StopLoop || state.StopReason != "finish_tool") return;
            if (retriesUsed >= maxRetries) return;

            // Snapshot the user's original ask + recent history. Trimmed
            // hard so the critic call stays cheap; the critic is meant
            // to spot obvious shortfalls, not perform a deep audit.
            var transcript = BuildCriticTranscript(state.Messages, transcriptCharCap);

            var verdict = await CallCritic(llm, transcript, ct);
            if (verdict is null) return; // LLM error → trust the agent.

            if (verdict.StartsWith("LGTM", StringComparison.OrdinalIgnoreCase)) return;

            // Critic disagrees. Un-stop the loop and inject the verdict
            // as a user-style turn so the next LLM call sees it. The
            // <critic_feedback> tag mirrors the <lint_feedback> /
            // <test_feedback> envelope the auto-check loop uses — the
            // agent is already trained (in chat) to treat these as
            // self-correction prompts, not the human user speaking.
            state.StopLoop = false;
            state.StopReason = "";
            state.Messages.Add(Chat.User(
                "<critic_feedback>\n" +
                "A reviewer flagged your `finish` as premature. Their notes:\n\n" +
                verdict.Trim() +
                "\n\nAddress the gap, then call `finish` again when the user's request is genuinely complete." +
                "\n</critic_feedback>"));
            retriesUsed++;
        };
    }

    /// <summary>
    /// Builds a compact transcript for the critic prompt. Keeps the
    /// first user message verbatim (that's the original ask, the
    /// thing being graded against) and a tail-window of the most
    /// recent messages. Hard caps the total length; truncation
    /// markers tell the critic something was elided.
    /// </summary>
    private static string BuildCriticTranscript(List<ChatMessage> messages, int charCap)
    {
        var lines = new List<string>();
        var firstUser = messages.FirstOrDefault(m => m.IsRole(ChatRole.User));
        if (firstUser is not null)
            lines.Add("USER (original ask): " + Trim(firstUser.GetText(), 1500));

        // Tail window: last ~10 turns (LLM-generated content + tool
        // results). Skips system + that first user message so we don't
        // double-count.
        var skipFirst = firstUser is not null ? 1 : 0;
        var tail = messages
            .Where(m => !m.IsRole(ChatRole.System))
            .Skip(skipFirst)
            .TakeLast(10)
            .ToList();
        foreach (var m in tail)
        {
            var role = m.Role == ChatRole.Assistant ? "ASSISTANT" :
                       m.Role == ChatRole.Tool ? "TOOL" :
                       m.Role == ChatRole.User ? "USER" :
                       m.Role.ToString().ToUpperInvariant();
            var text = m.GetText();
            if (m.HasToolCalls() && string.IsNullOrEmpty(text))
            {
                text = "(tool calls: " + string.Join(", ", m.GetToolCalls().Select(c => c.Name)) + ")";
            }
            lines.Add($"{role}: " + Trim(text, 800));
        }

        var s = string.Join('\n', lines);
        if (s.Length > charCap) s = s[..charCap] + "\n[…transcript truncated]";
        return s;
    }

    private static async Task<string?> CallCritic(LlmSettings llm, string transcript, CancellationToken ct)
    {
        const string criticSystem =
            "You are a strict completion-quality critic. The agent has just claimed " +
            "to be done with a user request via the `finish` tool. Your job: decide " +
            "if the agent actually completed what the USER asked for in their original " +
            "message.\n\n" +
            "Reply with the literal token `LGTM` (no other text) if the agent's work " +
            "appears to fully address the user's request.\n\n" +
            "Otherwise, reply with a SHORT (≤4 sentence) note pointing at the specific " +
            "gap — what's missing, what's wrong, or what claim isn't backed up. Don't " +
            "invent new requirements; only flag obvious shortfalls visible in the " +
            "transcript. If you can't tell, reply LGTM.";

        var req = new List<ChatMessage>
        {
            Chat.System(criticSystem),
            Chat.User(transcript),
        };

        var opts = new ChatOptions
        {
            ModelId = llm.Model,
            Temperature = 0.0f, // Deterministic critic.
        };

        try
        {
            var resp = await llm.Client.GetResponseAsync(req, opts, ct);
            if (resp.Messages.Count == 0) return null;
            return resp.Messages[^1].GetText().Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Soft-fail — the critic is meant to be a safety net, not a
            // gate. If the LLM call itself errors, fall back to trusting
            // the agent's finish.
            return null;
        }
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
