namespace Vett.Agent;

public enum TaskStatus { Pending, Running, Completed, Failed }

/// <summary>
/// A dispatched member’s loop ended on a non-clean stop reason AND produced
/// nothing to relay — no final text, no captured diff, no capture failure to
/// report. Thrown by <c>Coordinator.RunMember</c> so that
/// <c>LeaderTools.assign_task</c>’s existing catch marks the board task FAILED
/// and hands the leader "[id — member FAILED]" instead of the empty
/// "[id — member done]" that used to invite a fabricated answer.
/// </summary>
public sealed class DispatchFailedException : Exception
{
    public string TaskId { get; }
    public string Member { get; }
    public string StopReason { get; }
    public int Iterations { get; }

    public DispatchFailedException(string taskId, string member, string stopReason, int iterations)
        : base($"dispatch {taskId} ({member}) stopped on \"{stopReason}\" after {iterations} iteration(s) "
               + "and produced no result — no final message, no file changes. "
               + "This is NOT a completed task: nothing was produced. Re-assign it or report the stop_reason.")
    {
        TaskId = taskId;
        Member = member;
        StopReason = stopReason;
        Iterations = iterations;
    }
}

public sealed class TeamTask
{
    public string Id { get; init; } = "";
    public string Member { get; init; } = "";
    public string Description { get; init; } = "";
    public TaskStatus Status { get; set; }
    public string Result { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>
/// Thread-safe shared state between leader and member agents.
/// Leader creates tasks via delegation tools; members update status
/// as they run.
/// </summary>
public sealed class TaskBoard
{
    private readonly Dictionary<string, TeamTask> _tasks = new();
    private readonly Dictionary<string, List<TaskCompletionSource>> _waiters = new();
    private readonly Lock _lock = new();
    // Per-member counter so ids read as `researcher-1`, `researcher-2`,
    // `implementer-1`, etc. The previous `task-N` was technically correct
    // but read like a TODO item — users couldn't tell at a glance which
    // member instance an id referred to. Now the id is self-describing.
    private readonly Dictionary<string, int> _nextIdByMember = new();

    /// <summary>
    /// FIFO queue of completed/failed task ids the coordinator hasn't yet
    /// surfaced to the leader. Populated by Complete/Fail when
    /// <see cref="AutoInjectCompletions"/> is true. Drained at every
    /// leader iteration boundary via <see cref="DrainPendingDeliveries"/>.
    /// In bench mode (auto-inject off) this stays empty and the leader
    /// learns about completions only via explicit check_task/wait_task.
    /// </summary>
    private readonly Queue<string> _pendingDeliveries = new();

    /// <summary>
    /// FIFO queue of mid-flight progress reports a member pushed back to
    /// the leader via the `report_progress` tool. Each entry is a
    /// (taskId, message) pair; drained alongside completions so the
    /// leader sees streaming updates as if the task itself was reporting
    /// in. Empty in bench mode.
    /// </summary>
    private readonly Queue<(string TaskId, string Message)> _pendingProgress = new();

    /// <summary>Set by the coordinator at construction time based on
    /// `team.auto_inject_async_results` in the profile.</summary>
    public bool AutoInjectCompletions { get; set; }

    /// <summary>Fired (outside the lock) whenever a completion or
    /// progress report is enqueued. Coordinator subscribes to this to
    /// wake the leader's loop so deliveries surface in real time
    /// instead of waiting for the next user message. No-op listener
    /// in bench mode (no subscriber).</summary>
    public event Action? OnPending;

    private void FirePending()
    {
        // Fire outside any lock — subscribers may take their own
        // synchronization (e.g. write to a Channel) and we don't want
        // to inherit theirs into the board's hot path.
        try { OnPending?.Invoke(); } catch { /* listener errors must not break enqueue */ }
    }

    /// <summary>
    /// Per-task CancellationTokenSource so the leader can cancel a
    /// running task via cancel_task. Registered by assign_task /
    /// assign_async at dispatch time, removed when the task settles.
    /// </summary>
    private readonly Dictionary<string, CancellationTokenSource> _ctsByTaskId = new();

    /// <summary>
    /// Per-task message-injection writer. Registered by the coordinator
    /// at dispatch time; the corresponding ChannelReader is handed to
    /// the member's AgentLoop, which drains it at iteration boundaries
    /// and appends each message as a user turn. Lets the leader
    /// (responding to the chat user) inject mid-task guidance into a
    /// running member without cancelling and restarting.
    /// </summary>
    private readonly Dictionary<string, System.Threading.Channels.ChannelWriter<string>> _injectorsByTaskId = new();

    public void RegisterCts(string taskId, CancellationTokenSource cts)
    {
        lock (_lock) { _ctsByTaskId[taskId] = cts; }
    }

    public void UnregisterCts(string taskId)
    {
        lock (_lock) { _ctsByTaskId.Remove(taskId); }
    }

    /// <summary>
    /// Look up the current status of a task by id. Returns false if the
    /// task was never created; callers can use this to distinguish
    /// "still running" (true, Running) from "already accepted/rejected"
    /// (false) when shaping error messages — e.g. a leader calling
    /// review_dispatch speculatively before the dispatch has finished.
    /// </summary>
    public bool TryGetStatus(string taskId, out TaskStatus status)
    {
        lock (_lock)
        {
            if (_tasks.TryGetValue(taskId, out var t))
            {
                status = t.Status;
                return true;
            }
            status = default;
            return false;
        }
    }

    public void RegisterInjector(string taskId, System.Threading.Channels.ChannelWriter<string> writer)
    {
        lock (_lock) { _injectorsByTaskId[taskId] = writer; }
    }

    public void UnregisterInjector(string taskId)
    {
        lock (_lock)
        {
            if (_injectorsByTaskId.TryGetValue(taskId, out var w))
            {
                try { w.TryComplete(); } catch { }
                _injectorsByTaskId.Remove(taskId);
            }
        }
    }

    /// <summary>Inject a user-style message into a running task's
    /// pending-input queue. The member's loop drains this between
    /// iterations and appends each entry as a fresh user turn so the
    /// member can pivot. Returns true on success, false if the task
    /// has already settled or never existed.</summary>
    public bool Inject(string taskId, string message)
    {
        System.Threading.Channels.ChannelWriter<string>? writer;
        lock (_lock)
        {
            if (!_injectorsByTaskId.TryGetValue(taskId, out writer)) return false;
        }
        return writer.TryWrite(message);
    }

    /// <summary>Cancel a running task by id. Returns true if a live
    /// task was cancelled, false if it didn't exist or already
    /// settled.</summary>
    public bool Cancel(string taskId)
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (!_ctsByTaskId.TryGetValue(taskId, out cts)) return false;
            _ctsByTaskId.Remove(taskId);
        }
        try { cts.Cancel(); }
        catch { return false; }
        return true;
    }

    /// <summary>
    /// Maximum tasks a leader may have IN FLIGHT (Pending or Running) at once.
    /// **0 means unlimited.** Set from `team.max_concurrent_dispatches`, which
    /// is REQUIRED on every team node — see
    /// <see cref="Config.TeamConfig.MaxConcurrentDispatches"/>.
    ///
    /// ⚠ THIS PROPERTY STILL DEFAULTS TO 0, AND THAT IS DELIBERATE. The
    /// requirement lives at the CONFIG boundary, not here: a TaskBoard built
    /// directly in a test is not a profile an author wrote, and forcing every
    /// such construction to name a ceiling would add nothing. What guarantees a
    /// RUN is capped is `Coordinator.RequiredWidth`, which throws rather than
    /// coalescing an absent config value into this 0.
    /// </summary>
    public int MaxConcurrentDispatches { get; set; }

    /// <summary>Tasks not yet settled. Completed/Failed do NOT occupy a slot —
    /// the worktree and client are released at settle time, so that is exactly
    /// when the next dispatch may start.</summary>
    public int InFlight()
    {
        lock (_lock) return InFlightLocked();
    }

    private int InFlightLocked()
    {
        var n = 0;
        foreach (var t in _tasks.Values)
            if (t.Status is TaskStatus.Pending or TaskStatus.Running) n++;
        return n;
    }

    /// <summary>
    /// ⭐ RESERVE-OR-REFUSE, ATOMICALLY. Returns null when the leader is already
    /// at its ceiling; `inFlight` always reports the count that was observed.
    ///
    /// ⛔ WHY THIS IS NOT `if (InFlight() &lt; cap) Create(...)` AT THE CALL SITE.
    /// `assign_async`'s own tool description invites the model to make
    /// "multiple parallel calls", and it is fired through `Task.Run`. A
    /// check-then-act across two separate lock acquisitions lets every racer
    /// observe `cap - 1` and then every racer create — so the cap is exceeded by
    /// exactly the amount of concurrency it exists to limit. The check and the
    /// insert share ONE lock acquisition here for that reason.
    ///
    /// AT 0 (UNLIMITED) this method is behaviourally identical to
    /// <see cref="Create"/>, including the exact string `assign_async` returns —
    /// so an explicitly-uncapped profile is not paying for a check it declined.
    ///
    /// ⚠ HISTORY, because the reasoning was sound and its conclusion still got
    /// superseded. Until 2026-08-27 an absent key meant 0/unlimited BY DEFAULT,
    /// on the argument that switching a ceiling on by default would retroactively
    /// change five profiles measured live on 2026-08-26 (`ds-manager-*-w20`'s
    /// widest lead issued 32 dispatches) and invalidate the campaign's own
    /// numbers. That argument was right about DEFAULTING TO A NUMBER and wrong to
    /// stop there: measurement on 2026-08-27 found ZERO of the 12 team profiles
    /// set the key at all, so the ceiling was dormant everywhere and the default
    /// protected nothing. Mark's resolution ("fail if its not there") keeps the
    /// campaign arms uncapped — they now say `0` explicitly — while making
    /// silence impossible for everything else.
    /// </summary>
    public TeamTask? TryCreate(string member, string desc, out int inFlight)
    {
        lock (_lock)
        {
            inFlight = InFlightLocked();
            if (MaxConcurrentDispatches > 0 && inFlight >= MaxConcurrentDispatches) return null;
            return CreateLocked(member, desc);
        }
    }

    public TeamTask Create(string member, string desc)
    {
        lock (_lock) return CreateLocked(member, desc);
    }

    private TeamTask CreateLocked(string member, string desc)
    {
        {
            // Sanitize the member name into an id-safe slug. Members in
            // bundled profiles are already lowercase identifiers; this is
            // belt-and-braces for any future profile that uses spaces.
            var slug = string.IsNullOrWhiteSpace(member) ? "agent" : member.Trim().ToLowerInvariant().Replace(' ', '-');
            _nextIdByMember.TryGetValue(slug, out var n);
            n++;
            _nextIdByMember[slug] = n;
            var t = new TeamTask { Id = $"{slug}-{n}", Member = member, Description = desc };
            _tasks[t.Id] = t;
            return t;
        }
    }

    public void Complete(string id, string result)
    {
        bool queued;
        lock (_lock)
        {
            queued = false;
            if (_tasks.TryGetValue(id, out var t))
            {
                t.Status = TaskStatus.Completed;
                t.Result = result;
                if (AutoInjectCompletions) { _pendingDeliveries.Enqueue(id); queued = true; }
            }
            Notify(id);
        }
        if (queued) FirePending();
    }

    public void Fail(string id, string error)
    {
        bool queued;
        lock (_lock)
        {
            queued = false;
            if (_tasks.TryGetValue(id, out var t))
            {
                t.Status = TaskStatus.Failed;
                t.Error = error;
                if (AutoInjectCompletions) { _pendingDeliveries.Enqueue(id); queued = true; }
            }
            Notify(id);
        }
        if (queued) FirePending();
    }

    /// <summary>
    /// Drain every queued completion since the last call. Returns the
    /// task records the coordinator should fold into the leader's next
    /// LLM request as a synthesized "[Task X completed: …]" message.
    /// Used only when auto-inject is on; bench profiles never call it.
    /// </summary>
    public List<TeamTask> DrainPendingDeliveries()
    {
        lock (_lock)
        {
            if (_pendingDeliveries.Count == 0) return [];
            var ids = _pendingDeliveries.ToArray();
            _pendingDeliveries.Clear();
            var seen = new HashSet<string>();
            var result = new List<TeamTask>();
            foreach (var id in ids)
            {
                // Dedupe: a task could in principle be queued twice if it
                // was completed then re-completed via continue_task. Take
                // the most recent record (just the live one in _tasks).
                if (!seen.Add(id)) continue;
                if (_tasks.TryGetValue(id, out var t)) result.Add(t);
            }
            return result;
        }
    }

    /// <summary>
    /// Push a mid-flight progress report from a member into the leader's
    /// inbox. Queued and surfaced alongside completions on the leader's
    /// next iteration so it can relay updates to the user in real time.
    /// </summary>
    public void QueueProgress(string taskId, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        bool queued;
        lock (_lock)
        {
            if (!AutoInjectCompletions) return; // bench mode: drop silently
            _pendingProgress.Enqueue((taskId, message));
            queued = true;
        }
        if (queued) FirePending();
    }

    /// <summary>Drain all in-flight progress reports since the last
    /// call. Coordinator folds these into the same auto-injected
    /// `<background_task_results>` block so the leader sees one
    /// consolidated update per turn.</summary>
    public List<(string TaskId, string Message, string Member)> DrainPendingProgress()
    {
        lock (_lock)
        {
            if (_pendingProgress.Count == 0) return [];
            var entries = _pendingProgress.ToArray();
            _pendingProgress.Clear();
            var result = new List<(string, string, string)>(entries.Length);
            foreach (var (taskId, message) in entries)
            {
                var member = _tasks.TryGetValue(taskId, out var t) ? t.Member : "unknown";
                result.Add((taskId, message, member));
            }
            return result;
        }
    }

    /// <summary>
    /// Resolve a fuzzy task-id input to a real one on the board.
    /// Handles three cases:
    ///   1. Exact match → returns the input.
    ///   2. Bare integer "N" → matches first id ending in "-N".
    ///   3. Legacy / hallucinated "task-N" → matches the Nth task by
    ///      insertion order, then any id ending in "-N".
    /// Returns null when nothing matches. Lets the leader recover from
    /// id-format hallucinations ("task-0") without a full retry round.
    /// </summary>
    public string? ResolveTaskId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        lock (_lock)
        {
            if (_tasks.ContainsKey(input)) return input;

            // "5" → first key ending with "-5"
            if (int.TryParse(input, out var n))
            {
                foreach (var k in _tasks.Keys)
                    if (k.EndsWith($"-{n}", StringComparison.Ordinal)) return k;
            }

            // "task-N" pattern from older / hallucinated calls
            if (input.StartsWith("task-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(input["task-".Length..], out var idx))
            {
                // First, fall back to "<member>-N" form
                foreach (var k in _tasks.Keys)
                    if (k.EndsWith($"-{idx}", StringComparison.Ordinal)) return k;

                // Then by insertion order (1-based)
                var keys = _tasks.Keys.ToList();
                if (idx >= 1 && idx <= keys.Count) return keys[idx - 1];
                if (idx == 0 && keys.Count > 0) return keys[0]; // task-0 → first
            }

            return null;
        }
    }

    public void MarkRunning(string id)
    {
        lock (_lock)
        {
            if (_tasks.TryGetValue(id, out var t))
                t.Status = TaskStatus.Running;
        }
    }

    public TeamTask? Get(string id)
    {
        lock (_lock) { return _tasks.GetValueOrDefault(id); }
    }

    public List<TeamTask> All()
    {
        lock (_lock) { return [.. _tasks.Values]; }
    }

    public Task WaitAsync(string id, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_tasks.ContainsKey(id))
                throw new InvalidOperationException($"Unknown task id \"{id}\"");

            if (_tasks.TryGetValue(id, out var t) && t.Status is TaskStatus.Completed or TaskStatus.Failed)
                return Task.CompletedTask;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled());
            _waiters.TryAdd(id, []);
            _waiters[id].Add(tcs);
            return tcs.Task;
        }
    }

    private void Notify(string id)
    {
        if (_waiters.Remove(id, out var list))
        {
            foreach (var tcs in list)
                tcs.TrySetResult();
        }
    }

    public static string Format(TeamTask t)
    {
        var s = $"[{t.Id}] {t.Member} — {t.Status}\n  {t.Description}\n";
        if (t.Status == TaskStatus.Completed)
            s += $"  Result: {(t.Result.Length > 500 ? t.Result[..500] + "..." : t.Result)}\n";
        if (t.Status == TaskStatus.Failed)
            s += $"  Error: {t.Error}\n";
        return s;
    }

    public static string FormatAll(List<TeamTask> tasks)
        => tasks.Count == 0 ? "No tasks." : string.Join("\n", tasks.Select(Format));
}
