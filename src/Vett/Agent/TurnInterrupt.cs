namespace Vett.Agent;

/// <summary>
/// Per-turn cancellation signal that <see cref="AgentLoop"/> uses to
/// implement Claude Code-style "stop" — abort the in-flight LLM call
/// and any running tool calls without killing the agent loop itself.
///
/// Usage:
///   - The runner (RunStdio) creates one instance.
///   - The runner calls <see cref="Interrupt"/> when the user sends a
///     `cancel` message on stdin.
///   - <see cref="AgentLoop"/> calls <see cref="NewTurn"/> at the top of
///     every iteration; the returned token is what gets passed into
///     <c>GetResponseAsync</c>, <c>DispatchAsync</c>, and middleware.
///   - When <see cref="Interrupt"/> fires, those operations throw
///     <see cref="OperationCanceledException"/>. The agent loop catches
///     it, drops the partial state, appends a "user interrupted" note,
///     and waits for the next user message — same loop, next turn.
///
/// The session-wide token (parent) is observed too — when it cancels,
/// the whole loop shuts down rather than just the current turn.
/// </summary>
public sealed class TurnInterrupt
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Begin a new turn. Disposes the previous CTS (if any) and returns
    /// a fresh token linked to <paramref name="parent"/>. Idempotent in
    /// the sense that repeated calls always reset cleanly.
    /// </summary>
    public CancellationToken NewTurn(CancellationToken parent)
    {
        lock (_lock)
        {
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            return _cts.Token;
        }
    }

    /// <summary>Trigger cancellation of the current turn. Safe to call
    /// when no turn is active (no-op).</summary>
    public void Interrupt()
    {
        lock (_lock)
        {
            try { _cts?.Cancel(); } catch { /* already disposed */ }
        }
    }

    /// <summary>True when the current turn has been interrupted (and
    /// the parent has NOT also been cancelled). Used by the loop to
    /// distinguish user-stop from session-stop.</summary>
    public bool IsTurnCancelled(CancellationToken parent) =>
        _cts is { IsCancellationRequested: true } && !parent.IsCancellationRequested;
}
