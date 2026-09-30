using System.Threading.Channels;

namespace Vett.Tools;

/// <summary>
/// "Approve & switch" gate for plan mode. Mirrors the shape of
/// <see cref="PermissionGate"/> but with two-state semantics: the
/// gate starts LOCKED; the agent's first attempt at a write tool
/// surfaces an approval prompt; once the user approves, the gate
/// stays UNLOCKED for the rest of the session and write tools
/// dispatch normally.
///
/// On first unlock the gate ALSO emits `chat_mode_changed:execute`
/// so the chat UI flips Plan → Exec without a respawn — the agent
/// keeps its full conversation history (no re-exploration after
/// approval).
///
/// This is the Claude-Code-style behavior: "tap to upgrade" — a
/// per-call check that promotes the whole session on first approve.
/// Reject keeps the gate locked and returns an error to the agent.
/// </summary>
public sealed class PlanModeUnlockGate
{
    private readonly Action<string, Dictionary<string, object?>> _emit;
    private bool _unlocked;
    public PlanModeUnlockService Service { get; }

    public PlanModeUnlockGate(
        PlanModeUnlockService service,
        Action<string, Dictionary<string, object?>> emit)
    {
        Service = service;
        _emit = emit;
    }

    /// <summary>True when the gate has been opened by a prior approve.
    ///  Wrap callers can short-circuit dispatch without prompting.</summary>
    public bool IsUnlocked => _unlocked;

    /// <summary>
    /// Ask the user "approve and switch to execute, or reject?" for
    /// THIS specific tool call. Returns true on approve (gate
    /// unlocked + chat_mode_changed:execute emitted on first
    /// approve), false on reject. Idempotent once unlocked: returns
    /// true immediately without re-prompting.
    /// </summary>
    public async Task<bool> CheckAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken ct)
    {
        if (_unlocked) return true;

        var requestId = Guid.NewGuid().ToString("N");
        _emit("plan_mode_action_request", new()
        {
            ["request_id"] = requestId,
            ["tool_name"] = toolName,
            ["arguments"] = args.ToDictionary(kv => kv.Key, kv => kv.Value),
            ["preview"] = BuildPreview(toolName, args),
        });

        bool approve;
        try
        {
            approve = await Service.AwaitDecisionAsync(requestId, ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (approve && !_unlocked)
        {
            _unlocked = true;
            // Tell the chat UI we're now in execute mode. No respawn
            // happens server-side — the gate just opened — but the
            // toggle in the header should flip so the user knows the
            // restriction is gone for the rest of the session.
            _emit("chat_mode_changed", new()
            {
                ["mode"] = "execute",
                ["reason"] = "plan_mode_unlocked",
            });
        }
        return approve;
    }

    /// <summary>Short, human-readable preview shown in the chat card.
    ///  Mirrors PermissionGate's BuildPreview shape so the UI can use
    ///  the same rendering path.</summary>
    private static string BuildPreview(string toolName, IReadOnlyDictionary<string, object?> args)
    {
        switch (toolName)
        {
            case "terminal":
            case "bash_background":
                {
                    var cmd = args.TryGetValue("command", out var v) ? v?.ToString() ?? "" : "";
                    return cmd.Length > 200 ? cmd[..200] + "…" : cmd;
                }
            case "file_editor":
                {
                    var op = args.TryGetValue("command", out var v1) ? v1?.ToString() ?? "" : "";
                    var path = args.TryGetValue("path", out var v2) ? v2?.ToString() ?? "" : "";
                    return $"{op} {path}".Trim();
                }
            case "update_memory":
                {
                    var op = args.TryGetValue("operation", out var v1) ? v1?.ToString() ?? "" : "";
                    var name = args.TryGetValue("name", out var v2) ? v2?.ToString() ?? "" : "";
                    return $"{op}: {name}".Trim();
                }
            default:
                {
                    foreach (var v in args.Values)
                    {
                        if (v is string s && s.Length > 0) return s.Length > 200 ? s[..200] + "…" : s;
                    }
                    return toolName;
                }
        }
    }
}

/// <summary>
/// Round-trip service: the gate emits an event, then awaits the chat
/// UI's response by `request_id`. Mirrors <see cref="PermissionService"/>
/// down to the requeue-on-mismatch loop so parallel tool calls don't
/// crosswire decisions.
/// </summary>
public sealed class PlanModeUnlockService
{
    // Routed by request_id — see the long note on
    // PermissionService.AwaitDecisionAsync for why this is not a shared
    // channel. Short version: re-queuing a mismatched reply into the
    // channel you are about to read again never yields, so the method
    // never returns its Task and spins on the caller's thread; parking
    // the mismatch instead deadlocks sibling waiters. Covered by
    // RoundTripRequeueTests.
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _waiters = new();
    private readonly Dictionary<string, bool> _unclaimed = new();

    public async Task<bool> AwaitDecisionAsync(string requestId, CancellationToken ct)
    {
        TaskCompletionSource<bool> tcs;
        lock (_lock)
        {
            if (_unclaimed.Remove(requestId, out var alreadyAnswered)) return alreadyAnswered;
            if (!_waiters.TryGetValue(requestId, out tcs!))
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[requestId] = tcs;
            }
        }

        using var registration = ct.Register(static s =>
            ((TaskCompletionSource<bool>)s!).TrySetCanceled(), tcs);
        try
        {
            return await tcs.Task;
        }
        finally
        {
            lock (_lock) { _waiters.Remove(requestId); }
        }
    }

    public void PostDecision(string requestId, bool approve)
    {
        lock (_lock)
        {
            if (_waiters.TryGetValue(requestId, out var tcs) && tcs.TrySetResult(approve))
            {
                _waiters.Remove(requestId);
                return;
            }
            _unclaimed[requestId] = approve;
        }
    }
}
