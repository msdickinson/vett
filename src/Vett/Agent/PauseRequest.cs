namespace Vett.Agent;

/// <summary>
/// Latched flag the chat UI uses to ask the agent loop to pause at the
/// next iteration boundary (between LLM responses, after the current
/// iteration's tool calls + middleware finish). The loop emits a
/// <c>paused</c> event so the chat can show a Resume button, then
/// waits on the same user-input channel as the regular
/// "waiting for input" path.
///
/// Resume is signaled by either:
///   1. The chat clearing the flag via <see cref="Resume"/> + posting a
///      no-op user_input_needed equivalent (we just rely on the user
///      sending a message OR clicking the Resume UI which posts a
///      magic resume token).
///   2. A real user message arriving on the input channel — the
///      loop drains the resume flag and treats the message as the
///      next user turn.
///
/// Sibling pattern to <see cref="TurnInterrupt"/> (which aborts the
/// current turn) and <see cref="CompactRequest"/> (which forces
/// compaction). Distinct concepts — pause is "freeze and wait,"
/// stop is "abort what's running."
/// </summary>
public sealed class PauseRequest
{
    private int _pending;

    /// <summary>Set the pause flag. Idempotent — multiple calls before
    /// the loop checks collapse into one pause.</summary>
    public void Request() => Interlocked.Exchange(ref _pending, 1);

    /// <summary>Clear the pause flag. Called by the runner when the
    /// chat sends a "resume" stdio message. Safe to call when not
    /// paused — no-op.</summary>
    public void Resume() => Interlocked.Exchange(ref _pending, 0);

    /// <summary>True while the pause flag is set. Read-only — the loop
    /// peeks but does not clear; it waits for an explicit
    /// <see cref="Resume"/> OR the user-input channel to deliver a
    /// new turn (which implicitly resumes by satisfying the wait).</summary>
    public bool IsPaused => Volatile.Read(ref _pending) != 0;
}
