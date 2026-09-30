using Microsoft.Extensions.AI;
using Vett.Llm;

namespace Vett.Tools;

/// <summary>The 3 built-in middleware functions.</summary>
public static class BuiltinMiddleware
{
    public static Task SubmitDetector(AgentState state, CancellationToken ct)
    {
        foreach (var obs in state.LastObservations)
        {
            if (obs.Result.StartsWith(Builtins.SubmitMarker))
            {
                state.StopLoop = true;
                state.StopReason = "finish_tool";
            }
        }
        return Task.CompletedTask;
    }

    public static Task OutputTruncation(AgentState state, CancellationToken ct)
    {
        const int maxChars = 16_000;

        foreach (var obs in state.LastObservations)
        {
            if (obs.Result.Length > maxChars)
            {
                obs.Result = obs.Result[..maxChars] +
                    $"\n\n[Output truncated — {obs.Result.Length} chars, showing first {maxChars}]";
            }
        }

        // Sync tool messages with truncated observations.
        var byId = state.LastObservations.ToDictionary(o => o.ToolCallId);
        foreach (var msg in state.Messages)
        {
            if (!msg.IsRole(ChatRole.Tool)) continue;

            var frc = msg.Contents.OfType<FunctionResultContent>().FirstOrDefault();
            if (frc is not null && byId.TryGetValue(frc.CallId ?? "", out var obs))
            {
                // Replace the content with truncated version.
                msg.Contents.Clear();
                msg.Contents.Add(new FunctionResultContent(frc.CallId ?? "", obs.Result));
            }
        }

        return Task.CompletedTask;
    }

    public static Task StuckDetector(AgentState state, CancellationToken ct)
    {
        // Find messages since last user message.
        var start = 0;
        for (int i = state.Messages.Count - 1; i >= 0; i--)
        {
            if (state.Messages[i].IsRole(ChatRole.User))
            {
                start = i + 1;
                break;
            }
        }
        var recent = state.Messages.Skip(start).ToList();

        // Monologue: 4+ consecutive text-only assistant messages.
        var consecutive = 0;
        foreach (var m in recent)
        {
            if (m.IsRole(ChatRole.Assistant) && !m.HasToolCalls())
                consecutive++;
            else
                consecutive = 0;
        }

        if (consecutive >= 4)
        {
            state.StopLoop = true;
            state.StopReason = "stuck:monologue";
            return Task.CompletedTask;
        }

        // ── Action-error loop: 4+ consecutive TURNS whose tool results were
        //    all errors. ────────────────────────────────────────────────────
        //
        // ⛔ THIS USED TO COUNT TOOL MESSAGES, WHICH MADE IT A FAN-OUT KILLER.
        //
        // The old test was: take the last 4 Tool messages; if all 4 start with
        // "Error:", the agent is looping. That holds only while an agent makes
        // ONE tool call per turn. A leader that fans out issues N tool calls in
        // a SINGLE turn (AgentLoop.cs:1011-1038 runs them through Task.WhenAll),
        // so ONE turn yields N Tool messages. Four of them failing together is
        // one failed turn, not a loop — the agent has not even been given a
        // chance to react yet. A "loop" requires repetition, and repetition is
        // measured in turns.
        //
        // MEASURED (2026-08-26, team-fanout-tier2/fan5, ds-team-flash): the
        // leader correctly issued five assign_async calls in one turn, then
        // polled all five in the next turn. All five members were still
        // working, so all five polls returned the benign "still RUNNING"
        // status — five Error:-prefixed Tool messages from ONE turn. The
        // detector fired instantly and killed the leader at iteration 3 with
        // stop_reason='stuck:action_error_loop'. All five members went on to
        // finish their edits correctly, and every one of those diffs was
        // thrown away because the leader that had to accept them was already
        // dead. The suite failed with all five file assertions red while the
        // fan-out itself had worked perfectly.
        //
        // That gave fan-out a hard structural ceiling of THREE members: at
        // four, a single polling turn is fatal. Nothing in the codebase said
        // so, and no shipped suite dispatched more than one member, so it
        // never surfaced.
        //
        // TWO INDEPENDENT FIXES, BOTH NEEDED:
        //   1. Count by TURN (here). One bad turn is not a loop.
        //   2. Don't count a benign wait as an error at all (below). Polling a
        //      task that is still running is the CORRECT thing to do; the tool
        //      literally instructs the leader to wait. Turn-counting alone
        //      would still kill a leader that polls for four turns while its
        //      members are legitimately busy.
        //
        // Serial agents are unaffected: with one tool call per turn, "4
        // consecutive all-error turns" is exactly the old "4 consecutive
        // errors".
        static bool IsBenign(string text) =>
            text.Contains(Builtins.StillRunningMarker, StringComparison.Ordinal);

        var errorTurns = 0;
        for (var i = 0; i < recent.Count;)
        {
            if (!recent[i].IsRole(ChatRole.Tool)) { i++; continue; }

            // One maximal run of consecutive Tool messages == one turn's
            // worth of tool results, however many calls that turn made.
            var allErrors = true;
            var counted = 0;
            while (i < recent.Count && recent[i].IsRole(ChatRole.Tool))
            {
                var text = recent[i].GetText();
                if (!IsBenign(text))
                {
                    counted++;
                    if (!text.StartsWith("Error:")) allErrors = false;
                }
                i++;
            }

            // A turn made up entirely of benign waits (counted == 0) is not
            // evidence either way — leave the streak untouched rather than
            // letting a wait reset a genuine error streak or extend it.
            if (counted > 0) errorTurns = allErrors ? errorTurns + 1 : 0;
        }

        if (errorTurns >= 4)
        {
            state.StopLoop = true;
            state.StopReason = "stuck:action_error_loop";
        }

        return Task.CompletedTask;
    }
}
