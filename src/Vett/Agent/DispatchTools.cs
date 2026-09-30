using System.Text.Json;
using Vett.Tools;

namespace Vett.Agent;

/// <summary>
/// In-flight record of a dispatch awaiting leader review. Created in
/// Coordinator.RunMemberFull after the member finishes; cleared by
/// accept_dispatch (apply + discard worktree) or reject_dispatch (just
/// discard, optionally keep for inspection per retention policy).
/// SelfAssessment + AssessmentNotes are surfaced by review_dispatch so
/// the leader sees structured signals rather than scraping prose.
/// Task is the original assign_task description — surfaced for OWNS /
/// scope checks during review.
/// </summary>
public sealed record PendingDispatch(
    string TaskId,
    string Member,
    DispatchWorktree Worktree,
    DispatchCapture Capture,
    DispatchWorktreeManager Manager,
    DateTime CreatedAt,
    string SelfAssessment,
    string AssessmentNotes,
    string Task);

/// <summary>
/// ⭐ THE PREMISE INSTRUMENT for the promotion gate.
///
/// A concurrency test that never actually overlaps is a green control proving
/// only the case it constructs, and it looks identical to a passing one. This
/// counts callers inside the promotion region — INCLUDING those parked on the
/// gate — so a run can state positively that two promotions really did collide.
///
/// ⛔ THE COUNTER SITS OUTSIDE THE GATE ON PURPOSE. Inside, it would read 1 by
/// construction and would only be re-proving that a semaphore is a semaphore.
/// Outside, MaxOverlap >= 2 is evidence the gate had something to serialize;
/// MaxOverlap == 1 means the test never built the race and its green is empty.
/// The healthy NULL is 1 (or 0 if no promotion ran at all) — distinguish them.
///
/// Cost is two interlocked ops per accept, so it ships rather than being
/// #if'd out: an instrument compiled only into test builds cannot testify
/// about the binary that actually runs.
/// </summary>
public static class PromotionProbe
{
    /// <summary>
    /// ⛔ SCOPED PER RUN, NOT PER PROCESS — and that is a correctness
    /// requirement, not tidiness. Static counters looked fine and passed a full
    /// suite once, but SIX other test classes drive accept_dispatch and xUnit
    /// runs classes in parallel, so a neighbouring class's promotion would land
    /// between one test's Reset() and its assertion. The premise assert would
    /// then fail for a reason having nothing to do with the code under test:
    /// an instrument manufacturing its own flake.
    ///
    /// AsyncLocal flows into every task the run spawns — including the
    /// Task.WhenAll fan-out in AgentLoop that creates the concurrency being
    /// measured — while staying invisible to unrelated tests on other threads.
    /// </summary>
    private sealed class Counter
    {
        public int InFlight;
        public int MaxOverlap;
        public int Entries;
    }

    private static readonly AsyncLocal<Counter?> Current = new();

    /// <summary>Highest number of promotions simultaneously in the region.</summary>
    public static int MaxOverlap => Current.Value is { } c ? Volatile.Read(ref c.MaxOverlap) : 0;

    /// <summary>Total promotions that entered the region. 0 distinguishes
    /// "never promoted" from "promoted, but one at a time".</summary>
    public static int Entries => Current.Value is { } c ? Volatile.Read(ref c.Entries) : 0;

    /// <summary>Arms the probe for the current async flow. Until this is called
    /// the probe is inert, so production pays two null checks per accept and
    /// carries no shared mutable state at all.</summary>
    public static void Reset() => Current.Value = new Counter();

    public static void Enter()
    {
        if (Current.Value is not { } c) return;
        Interlocked.Increment(ref c.Entries);
        var now = Interlocked.Increment(ref c.InFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref c.MaxOverlap)))
        {
            if (Interlocked.CompareExchange(ref c.MaxOverlap, now, seen) == seen) break;
        }
    }

    public static void Exit()
    {
        if (Current.Value is { } c) Interlocked.Decrement(ref c.InFlight);
    }
}

/// <summary>
/// Leader review tools for dispatch worktrees: accept_dispatch,
/// reject_dispatch, list_pending_dispatches. Registered into the
/// leader's tool map only when team.dispatch_worktree=true; otherwise
/// the tools don't appear and the leader falls back to its existing
/// "trust the implementer's summary" workflow.
/// </summary>
public static class DispatchTools
{
    public static Dictionary<string, ToolFn> Create(
        Dictionary<string, PendingDispatch> registry,
        object registryLock,
        // Serializes promotion (`git apply` into the shared parent worktree)
        // across every path that can promote. NOT defaulted and NOT optional:
        // a caller that forgets it is exactly the bug this closes, so it has to
        // be a compile error rather than a silently-unguarded apply. Shared with
        // BestOfTools.Create — the same object must reach both.
        SemaphoreSlim promotionGate,
        DispatchWorktreeManager manager,
        string retentionPolicy,
        TaskBoard? board = null)
    {
        var keepOnFailure = !string.Equals(retentionPolicy, "auto-clean", StringComparison.OrdinalIgnoreCase);
        var keepAll = string.Equals(retentionPolicy, "keep-all", StringComparison.OrdinalIgnoreCase);

        // Build a status-aware error message for the "task_id not in
        // pending registry" case. Three sub-cases worth distinguishing:
        //   - task is currently Running         → tell the leader to wait
        //                                         for the [Background task ...
        //                                         completed] message
        //   - task was Completed/Failed and has been gone from the registry
        //                                       → likely already accepted/rejected
        //                                         OR completed with no diff captured
        //   - task never existed                 → typo in the id
        // Without the disambiguation the leader sees a generic "no pending
        // dispatch" and tries to figure out what to do — usually wrong.
        string MissingDispatchMessage(string taskId, string operation)
        {
            if (board is not null && board.TryGetStatus(taskId, out var status))
            {
                return status switch
                {
                    // NOTE: the literal "is still RUNNING" here is
                    // Builtins.StillRunningMarker — StuckDetector matches on it
                    // to keep a benign wait from counting as an error loop.
                    // Don't reword it inline; change the constant.
                    Vett.Agent.TaskStatus.Pending or Vett.Agent.TaskStatus.Running =>
                        $"Error: task_id '{taskId}' {Vett.Tools.Builtins.StillRunningMarker} — there's nothing to {operation} yet. Wait for the [Background task {taskId} (...) completed] message in <background_task_results>; that's when the dispatch is ready to review. Do not call this tool again until then; just acknowledge to the user and end your turn.",
                    Vett.Agent.TaskStatus.Completed or Vett.Agent.TaskStatus.Failed =>
                        $"Error: task_id '{taskId}' has settled but is no longer in the review registry. Either it was already accepted/rejected, or it completed with no diff captured (nothing to {operation}). Read your prior <background_task_results> blocks for what happened.",
                    _ => $"Error: task_id '{taskId}' is in unknown state."
                };
            }
            return $"Error: task_id '{taskId}' has no pending dispatch. Already accepted/rejected, or never existed.";
        }

        return new Dictionary<string, ToolFn>
        {
            ["accept_dispatch"] = async (args, _, _, ct) =>
            {
                var taskId = S(args, "task_id");
                var review = S(args, "review");
                if (taskId == "") return "Error: task_id required";
                if (string.IsNullOrWhiteSpace(review))
                    return "Error: review required — describe what you actually inspected before accepting (diff_stat, self_assessment, notes, build output if you ran one, OWNS scope check). 'looks good' is not a review.";

                // ONE PROMOTION AT A TIME. The gate spans lookup -> apply ->
                // remove, not just the apply, so the whole check-then-apply
                // sequence is atomic with respect to other promotions.
                //
                // WHAT IS MEASURED, not assumed:
                //   - Two accept_dispatch calls in ONE leader turn really do run
                //     concurrently (AgentLoop.cs:1149-1166 fans tool calls out
                //     through Task.WhenAll). PromotionProbe.MaxOverlap reads 2 in
                //     Two_accepts_in_ONE_turn_..., gated AND ungated.
                //   - The window is real: ApplyAsync does `git apply --check`
                //     then `git apply` as two separate processes
                //     (DispatchWorktreeManager.cs:711-739), and its own comment
                //     already concedes the gap between them.
                //
                // ⛔ WHAT IS *NOT* PROVEN — DO NOT UPGRADE THIS TO "FIXES A BUG".
                // The negative control (gate removed, probe confirming overlap)
                // came back 0 red in 27 runs. So this is DEFENCE IN DEPTH against
                // a TOCTOU window, NOT a repair of an observed corruption. The
                // reason the window is hard to lose: `git apply` here is plain —
                // no --index/--cached — so it takes no index.lock and each
                // process re-reads the file at apply time, which turns the loser
                // into an ordinary "patch does not apply" conflict rather than a
                // torn write. An earlier draft of this comment claimed an
                // index.lock failure mode; that was wrong and is retracted.
                //
                // It stays because the cost is one uncontended semaphore per
                // accept and it closes the window by construction, which beats
                // relying on git's per-process read-modify-write staying
                // atomic enough. See internal note FINAL-AUDIT-2026-08-27.md.
                PromotionProbe.Enter();
                try
                {
                await promotionGate.WaitAsync(ct);
                try
                {
                    PendingDispatch? pending;
                    lock (registryLock)
                    {
                        if (!registry.TryGetValue(taskId, out pending))
                            return MissingDispatchMessage(taskId, "accept");
                    }

                    try
                    {
                        if (pending.Capture.HasChanges && pending.Capture.Diff is not null)
                            await manager.ApplyAsync(pending.Capture.Diff, ct);
                    }
                    catch (DispatchApplyConflictException ex)
                    {
                        // Soft conflict — leave the dispatch worktree on disk
                        // so the leader can re-dispatch with merged context
                        // or escalate to user. Do NOT remove from registry;
                        // leader can retry accept after fixing the parent.
                        return $"Error: accept_dispatch failed to merge — {ex.Message}\n" +
                               $"The dispatch worktree is still at {pending.Worktree.Path}. " +
                               "Options: re-dispatch the implementer to redo against the latest parent state, " +
                               "or call reject_dispatch to abandon this attempt.";
                    }
                    catch (Exception ex)
                    {
                        return $"Error: accept_dispatch failed unexpectedly: {ex.Message}";
                    }

                    // Successful apply — discard the worktree UNLESS retention
                    // is keep-all (the user wants both accepted and rejected
                    // worktrees kept on disk for debugging — comparing what
                    // the implementer produced vs what got merged into parent).
                    lock (registryLock) { registry.Remove(taskId); }
                    if (!keepAll)
                    {
                        try { await manager.DiscardAsync(pending.Worktree, CancellationToken.None); } catch { }
                        return $"Accepted dispatch '{taskId}' ({pending.Member}). Applied {pending.Capture.FilesChanged} file(s) to the parent worktree.\n" +
                               $"Review: {review}\n" +
                               $"Diff stat:\n{pending.Capture.DiffStat}";
                    }
                    return $"Accepted dispatch '{taskId}' ({pending.Member}). Applied {pending.Capture.FilesChanged} file(s) to the parent worktree.\n" +
                           $"Review: {review}\n" +
                           $"Worktree kept at {pending.Worktree.Path} (retention=keep-all).\n" +
                           $"Diff stat:\n{pending.Capture.DiffStat}";
                }
                finally { promotionGate.Release(); }
                }
                finally { PromotionProbe.Exit(); }
            },

            ["reject_dispatch"] = async (args, _, _, _) =>
            {
                var taskId = S(args, "task_id");
                var reason = S(args, "reason");
                var review = S(args, "review");
                if (taskId == "") return "Error: task_id required";
                if (string.IsNullOrWhiteSpace(review))
                    return "Error: review required — describe what you actually inspected before rejecting (diff_stat, self_assessment, notes, OWNS scope check). 'wrong direction' is not a review.";

                PendingDispatch? pending;
                lock (registryLock)
                {
                    if (!registry.TryGetValue(taskId, out pending))
                        return MissingDispatchMessage(taskId, "reject");
                }

                lock (registryLock) { registry.Remove(taskId); }

                // Retention policy decides whether the worktree gets torn
                // down or stays for debugging. keep-on-failure (default)
                // keeps rejected worktrees so the user can inspect what
                // went wrong; auto-clean removes; keep-all never removes.
                if (!keepOnFailure && !keepAll)
                {
                    try { await manager.DiscardAsync(pending.Worktree, CancellationToken.None); } catch { }
                    return $"Rejected dispatch '{taskId}' ({pending.Member}). Worktree removed.\nReview: {review}\nReason: {reason}";
                }

                return $"Rejected dispatch '{taskId}' ({pending.Member}). Worktree kept at {pending.Worktree.Path} for inspection.\nReview: {review}\nReason: {reason}";
            },

            ["review_dispatch"] = (args, _, _, _) =>
            {
                // Returns the structured inputs a leader needs to decide
                // accept vs reject — same data the system prompt's "How to
                // decide" prose used to tell the model to scrape from
                // multiple events. With this tool, the model gets every
                // signal in one machine-readable response and the prose
                // can shrink to "call review_dispatch first".
                var taskId = S(args, "task_id");
                if (taskId == "") return Task.FromResult("Error: task_id required");

                PendingDispatch? pending;
                lock (registryLock)
                {
                    if (!registry.TryGetValue(taskId, out pending))
                        return Task.FromResult(MissingDispatchMessage(taskId, "review"));
                }

                var age = (DateTime.UtcNow - pending.CreatedAt).TotalSeconds;
                var sb = new System.Text.StringBuilder();
                sb.Append($"REVIEW (task_id: {taskId})\n");
                sb.Append($"  member: {pending.Member}\n");
                sb.Append($"  age: {age:F0}s\n");
                sb.Append($"  worktree: {pending.Worktree.Path}\n\n");
                sb.Append($"  files_changed: {pending.Capture.FilesChanged}\n");
                sb.Append($"  diff_stat:\n");
                foreach (var line in pending.Capture.DiffStat.TrimEnd().Split('\n'))
                    sb.Append($"    {line}\n");
                sb.Append('\n');
                sb.Append($"  self_assessment: {(string.IsNullOrEmpty(pending.SelfAssessment) ? "unknown" : pending.SelfAssessment)}\n");
                if (!string.IsNullOrWhiteSpace(pending.AssessmentNotes))
                {
                    sb.Append($"  notes:\n");
                    foreach (var line in pending.AssessmentNotes.Trim().Split('\n'))
                        sb.Append($"    {line}\n");
                    sb.Append('\n');
                }
                sb.Append($"  original_task:\n");
                foreach (var line in pending.Task.Trim().Split('\n'))
                    sb.Append($"    {line}\n");
                sb.Append('\n');
                sb.Append("Decide: accept_dispatch(task_id, review) or reject_dispatch(task_id, reason, review). Both require a `review` field describing what you observed.");
                return Task.FromResult(sb.ToString());
            },

            ["list_pending_dispatches"] = (_, _, _, _) =>
            {
                lock (registryLock)
                {
                    if (registry.Count == 0)
                        return Task.FromResult("No pending dispatches awaiting review.");

                    var lines = new List<string> { $"{registry.Count} pending dispatch(es):" };
                    foreach (var (id, p) in registry)
                    {
                        var age = (DateTime.UtcNow - p.CreatedAt).TotalSeconds;
                        // git diff --stat ends with "\n" so Split('\n') produces
                        // a trailing empty entry. TrimEnd first so LastOrDefault
                        // returns the actual summary line ("N files changed,
                        // M insertions(+), L deletions(-)"), not "".
                        var summaryLine = p.Capture.DiffStat
                            .TrimEnd()
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .LastOrDefault()
                            ?? p.Capture.DiffStat;
                        lines.Add(
                            $"  {id} ({p.Member}, age {age:F0}s) — {p.Capture.FilesChanged} files: {summaryLine.Trim()}");
                    }
                    return Task.FromResult(string.Join('\n', lines));
                }
            },
        };
    }

    public static readonly List<JsonElement> Schemas = new[]
    {
        """{"type":"function","function":{"name":"review_dispatch","description":"Inspect a pending dispatch's structured signals before deciding accept vs reject. Returns: member name, age, worktree path, files_changed count, diff_stat, self_assessment (confident|partial|uncertain|unknown), notes, and the original task description. Call this FIRST — it gives you everything you need to make a decision in one tool result, instead of scraping the same fields from the assign_task return value or dispatch_end event. Read-only; does not modify the registry.","parameters":{"type":"object","properties":{"task_id":{"type":"string","description":"task_id of the pending dispatch to review (e.g. 'implementer-1')."}},"required":["task_id"]}}}""",
        """{"type":"function","function":{"name":"accept_dispatch","description":"Apply a dispatched member's captured changes from its isolated worktree into the parent working directory. On a clean apply the worktree is removed; on a merge conflict the worktree stays on disk and you receive a structured error so you can re-dispatch or escalate. Requires a `review` field — what you actually inspected before deciding to accept.","parameters":{"type":"object","properties":{"task_id":{"type":"string","description":"task_id of the pending dispatch to accept (e.g. 'implementer-1')."},"review":{"type":"string","description":"What you inspected before accepting. Cite the specific signals you looked at: diff_stat, self_assessment, notes, build/test output if you ran one, OWNS scope check. Past-tense observations only — what you saw, not what you plan to do. Example: 'Reviewed diff (1 file, README.md, +24/-0), self_assessment=confident, notes match requested scope, no OWNS violations since OWNS was unrestricted.' Just \"looks good\" is not a valid review and the tool will reject the call."}},"required":["task_id","review"]}}}""",
        """{"type":"function","function":{"name":"reject_dispatch","description":"Discard a dispatched member's changes without applying. Use when the diff is wrong, the self_assessment is uncertain about something material, or you've decided to redo the work. Per retention policy, the worktree may be kept on disk for inspection (default) or cleaned up immediately. Requires both a `review` (what you inspected) and a `reason` (why you're rejecting based on what you saw).","parameters":{"type":"object","properties":{"task_id":{"type":"string"},"review":{"type":"string","description":"What you inspected before rejecting. Cite the specific signals you looked at: diff_stat, self_assessment, notes, OWNS scope check. Past-tense observations only. Example: 'Reviewed diff (3 files), self_assessment=uncertain, notes say tests didn't run; files include changes outside the OWNS I assigned.'"},"reason":{"type":"string","description":"Why you're rejecting based on what you reviewed. One sentence — what's wrong and what you'd do differently. Logged with the dispatch for debugging."}},"required":["task_id","review","reason"]}}}""",
        """{"type":"function","function":{"name":"list_pending_dispatches","description":"List dispatches waiting for accept_dispatch / reject_dispatch. Useful when you've forgotten which task_ids are still open or want to confirm a parallel fan-out completed.","parameters":{"type":"object","properties":{},"required":[]}}}""",
    }.Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToList();

    private static string S(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var v) || v is null) return "";
        if (v is JsonElement je && je.ValueKind == JsonValueKind.String) return je.GetString() ?? "";
        return v.ToString() ?? "";
    }
}
