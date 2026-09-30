using System.Text;
using System.Text.Json;
using Vett.Tools;

namespace Vett.Agent;

/// <summary>
/// BEST-OF-N (2026-07-13). One leader tool, `assign_best_of`, that runs the
/// SAME unit of work N times with DIFFERENT prompt angles, has a JUDGE member
/// compare the N resulting diffs, and applies AT MOST ONE of them.
///
/// WHY A TOOL AND NOT A PROMPT. The leader can already fake this: assign_async
/// x5, then eyeball the diffs, then accept one. But best-of-N has failure modes
/// a prompt cannot prevent — accepting two winners, accepting without ever
/// consulting the judge, or "accepting" a candidate it never looked at. This
/// session established the pattern twice already: leaders ignore prompt rules
/// (file_editor freelancing; a `cp` hand-merge out of a dispatch worktree). So
/// the guarantees live in the harness:
///
///   * exactly ONE candidate is applied, or ZERO — never two
///   * the judge is ALWAYS consulted; the leader cannot skip it
///   * NONE is a first-class verdict — a real answer, not a failure
///   * every losing worktree is rejected/cleaned per retention policy
///
/// The judge is a MEMBER, so in a hybrid profile it is Pro while the N
/// candidates are Flash: "let the non-flash one review all of them and pick one
/// of them or NONE of them."
/// </summary>
public static class BestOfTools
{
    public const int MinN = 2;
    public const int MaxN = 8;

    /// <summary>
    /// Prompt angles appended to each candidate's task. Deliberately different
    /// STRATEGIES, not reworded copies — N identical prompts at temperature 0.3
    /// mostly produce N near-identical diffs, and a judge picking between clones
    /// buys nothing. Index i gets angle i (wrapping if N &gt; angles.Length).
    /// </summary>
    private static readonly string[] Angles =
    {
        "APPROACH: take the most DIRECT path — the smallest edit to the fewest files that fully satisfies the task.",
        "APPROACH: prioritise ROBUSTNESS — handle the edge cases and invalid inputs explicitly, even if the diff is larger.",
        "APPROACH: follow the EXISTING CONVENTIONS of this codebase as closely as possible — mirror the patterns, naming and structure already present in the files you touch.",
        "APPROACH: prioritise READABILITY — the clearest possible implementation, even if it is not the shortest.",
        "APPROACH: be CONSERVATIVE — change as little existing behaviour as you can; prefer adding over modifying.",
        "APPROACH: think about what a REVIEWER would object to, and pre-empt it.",
        "APPROACH: optimise for TESTABILITY — structure the change so its behaviour is easy to verify.",
        "APPROACH: solve it the way the ORIGINAL AUTHOR of this file would have — infer their intent from the surrounding code.",
    };

    private static string S(Dictionary<string, object?> a, string k)
    {
        if (!a.TryGetValue(k, out var v) || v is null) return "";
        if (v is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.ToString();
        return v.ToString() ?? "";
    }

    private static int I(Dictionary<string, object?> a, string k, int fallback)
    {
        if (!a.TryGetValue(k, out var v) || v is null) return fallback;
        if (v is JsonElement je)
            return je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n) ? n : fallback;
        return int.TryParse(v.ToString(), out var p) ? p : fallback;
    }

    /// <param name="runDispatch">
    /// Creates a board task, runs the member (which provisions its own dispatch
    /// worktree and registers a PendingDispatch when it produces a diff), and
    /// returns (taskId, finalReport).
    /// </param>
    /// <param name="runJudge">Runs the judge member and returns its final text.</param>
    public static Dictionary<string, ToolFn> Create(
        Dictionary<string, PendingDispatch> registry,
        object registryLock,
        // The SAME SemaphoreSlim instance handed to DispatchTools.Create — the
        // two paths write one shared parent worktree, so a second gate here
        // would serialize each path against itself and neither against the
        // other, which is indistinguishable from no gate at all.
        SemaphoreSlim promotionGate,
        DispatchWorktreeManager manager,
        string retentionPolicy,
        Func<string, string, CancellationToken, Task<(string TaskId, string Report)>> runDispatch,
        Func<string, string, CancellationToken, Task<string>> runJudge,
        string defaultJudge,
        int defaultN)
    {
        var keepAll = string.Equals(retentionPolicy, "keep-all", StringComparison.OrdinalIgnoreCase);

        return new Dictionary<string, ToolFn>
        {
            ["assign_best_of"] = async (args, _, _, ct) =>
            {
                var member = S(args, "member");
                var task = S(args, "task");
                var judge = S(args, "judge");
                if (string.IsNullOrWhiteSpace(judge)) judge = defaultJudge;
                var n = Math.Clamp(I(args, "n", defaultN), MinN, MaxN);

                if (member == "" || task == "")
                    return "Error: member and task required";
                if (string.Equals(member, judge, StringComparison.OrdinalIgnoreCase))
                    return $"Error: the judge ({judge}) cannot also be the candidate member — it would be grading its own work.";

                // ---- 1. run N candidates in parallel, each in its own worktree
                var runs = Enumerable.Range(0, n).Select(i =>
                {
                    var angled = $"{task}\n\n{Angles[i % Angles.Length]}\n\n"
                               + "You are ONE of several independent attempts at this task. Do not "
                               + "coordinate, do not reference other attempts — just produce your best "
                               + "version of the change.";
                    return runDispatch(member, angled, ct);
                }).ToArray();

                (string TaskId, string Report)[] done;
                try { done = await Task.WhenAll(runs); }
                catch (Exception ex) { return $"Error: best-of-{n} fan-out failed: {ex.Message}"; }

                // ---- 2. collect the candidates that actually produced a diff
                var candidates = new List<(int Num, string TaskId, PendingDispatch P)>();
                var noDiff = new List<string>();
                lock (registryLock)
                {
                    foreach (var (taskId, _) in done)
                    {
                        if (registry.TryGetValue(taskId, out var p) && p.Capture.HasChanges)
                            candidates.Add((candidates.Count + 1, taskId, p));
                        else
                            noDiff.Add(taskId);
                    }
                }

                if (candidates.Count == 0)
                    return $"best-of-{n}: NO candidate produced a diff (attempts: {string.Join(", ", noDiff)}). "
                         + "Nothing to judge and nothing applied. Re-dispatch with a clearer task.";

                // ---- 3. judge sees every candidate's diff + self-assessment
                var sb = new StringBuilder();
                sb.AppendLine($"You are judging {candidates.Count} INDEPENDENT attempts at the SAME task.");
                sb.AppendLine("Exactly one of them — or NONE of them — will be applied to the codebase.");
                sb.AppendLine();
                sb.AppendLine("THE TASK THEY WERE ALL GIVEN:");
                sb.AppendLine(task);
                sb.AppendLine();
                foreach (var (num, _, p) in candidates)
                {
                    sb.AppendLine($"===== CANDIDATE {num} =====");
                    sb.AppendLine($"self_assessment: {p.SelfAssessment}");
                    sb.AppendLine($"notes: {p.AssessmentNotes}");
                    sb.AppendLine($"files changed: {p.Capture.FilesChanged}");
                    sb.AppendLine($"diff stat:\n{p.Capture.DiffStat}");
                    var diff = p.Capture.Diff ?? "";
                    if (diff.Length > 8000) diff = diff[..8000] + "\n... [diff truncated]";
                    sb.AppendLine($"diff:\n{diff}");
                    sb.AppendLine();
                }
                sb.AppendLine("Judge them on CORRECTNESS first, then scope discipline (did it change only what");
                sb.AppendLine("the task asked?), then clarity. A candidate that does not build, silently deletes");
                sb.AppendLine("existing behaviour, or invents work nobody asked for is DISQUALIFIED no matter how");
                sb.AppendLine("good it looks. You may inspect the workspace read-only to check your reasoning.");
                sb.AppendLine();
                sb.AppendLine("CHOOSING NONE IS A REAL ANSWER. If every candidate is wrong, say NONE — do not");
                sb.AppendLine("settle for the least-bad diff. A wrong change that is applied costs more than no");
                sb.AppendLine("change at all.");
                sb.AppendLine();
                sb.AppendLine("Your LAST LINE must be EXACTLY one of:");
                sb.AppendLine($"  VERDICT: <1..{candidates.Count}>");
                sb.AppendLine("  VERDICT: NONE");
                sb.AppendLine("Put your reasoning ABOVE that line, in 2-4 sentences citing what you saw in the diffs.");

                string verdictText;
                try { verdictText = await runJudge(judge, sb.ToString(), ct); }
                catch (Exception ex) { verdictText = $"(judge failed: {ex.Message})"; }

                var (winner, parsed) = ParseVerdict(verdictText, candidates.Count);

                // ---- 4. apply AT MOST ONE; reject the rest. Enforced here, not asked for.
                var applied = "";
                if (winner is int w)
                {
                    var pick = candidates.First(c => c.Num == w);
                    // ⛔ SECOND PROMOTION PATH — same gate as accept_dispatch.
                    // best_of applies at most one candidate, so it cannot race
                    // ITSELF; it races accept_dispatch. Both write the SAME
                    // parent worktree through the same lock-free
                    // DispatchWorktreeManager, and a leader turn can carry a
                    // best_of call and an accept_dispatch call together. Gating
                    // only the accept_dispatch site would have been a partial
                    // fix that reads as a complete one.
                    PromotionProbe.Enter();
                    try
                    {
                    await promotionGate.WaitAsync(ct);
                    try
                    {
                        if (pick.P.Capture.Diff is not null)
                            await manager.ApplyAsync(pick.P.Capture.Diff, ct);
                        lock (registryLock) { registry.Remove(pick.TaskId); }
                        if (!keepAll)
                            try { await manager.DiscardAsync(pick.P.Worktree, CancellationToken.None); } catch { }
                        applied = $"APPLIED candidate {w} ({pick.TaskId}): {pick.P.Capture.FilesChanged} file(s).\n"
                                + $"Diff stat:\n{pick.P.Capture.DiffStat}";
                    }
                    catch (Exception ex)
                    {
                        applied = $"Candidate {w} won but FAILED TO APPLY: {ex.Message}\n"
                                + $"Its worktree is at {pick.P.Worktree.Path}. Nothing was applied.";
                        winner = null;   // nothing landed — the losers still get discarded below
                    }
                    finally { promotionGate.Release(); }
                    }
                    finally { PromotionProbe.Exit(); }
                }

                foreach (var (num, taskId, p) in candidates)
                {
                    if (winner is int win && num == win) continue;
                    lock (registryLock) { registry.Remove(taskId); }
                    if (!keepAll)
                        try { await manager.DiscardAsync(p.Worktree, CancellationToken.None); } catch { }
                }

                var head = winner is null
                    ? $"best-of-{candidates.Count}: VERDICT NONE — no candidate was applied. The workspace is UNCHANGED.\n"
                      + "Do NOT declare_done. Re-dispatch with a sharper task using the judge's reasoning below."
                    : applied;

                return $"{head}\n\n--- judge ({judge}) ---\n{verdictText}\n"
                     + (parsed ? "" : $"\n(NOTE: no parseable 'VERDICT:' line — treated as NONE, nothing applied.)");
            },
        };
    }

    /// <summary>
    /// Parse the judge's trailing VERDICT line. Anything we cannot parse is
    /// treated as NONE — FAIL CLOSED. A judge that rambles must never cause a
    /// diff to be applied by accident; the cost of a spurious NONE is one
    /// re-dispatch, the cost of a spurious apply is wrong code in the tree.
    /// </summary>
    public static (int? Winner, bool Parsed) ParseVerdict(string text, int candidateCount)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, false);

        var m = System.Text.RegularExpressions.Regex.Matches(
            text, @"VERDICT:\s*(NONE|\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Count == 0) return (null, false);

        var last = m[^1].Groups[1].Value;      // last verdict wins — models restate
        if (last.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return (null, true);
        if (int.TryParse(last, out var n) && n >= 1 && n <= candidateCount) return (n, true);
        return (null, false);                  // out-of-range index -> NONE, fail closed
    }

    public static readonly List<JsonElement> Schemas = new[]
    {
        """{"type":"function","function":{"name":"assign_best_of","description":"Run the SAME unit of work N times in parallel as independent attempts (each gets a different approach angle and its own isolated worktree), then have a JUDGE member compare all the resulting diffs and apply AT MOST ONE. The judge may return NONE, in which case nothing is applied and the workspace is unchanged. Use this for a unit where correctness matters more than cost and a single attempt is a coin-flip. You do NOT accept_dispatch afterwards — this tool applies the winner itself.","parameters":{"type":"object","properties":{"member":{"type":"string","description":"Member to run N times (e.g. 'implementer')."},"task":{"type":"string","description":"The unit of work. Same rules as assign_task: relay it faithfully, name the files, name the types/methods it must use."},"n":{"type":"integer","description":"How many independent attempts (2-8)."},"judge":{"type":"string","description":"Member that picks the winner (e.g. 'reviewer'). Must not be the same as `member`. Defaults to the profile's configured judge."}},"required":["member","task"]}}}""",
    }.Select(s => JsonDocument.Parse(s).RootElement).ToList();
}
