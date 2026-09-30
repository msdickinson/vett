using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Vett.Sandbox;

namespace Vett.Tools;

/// <summary>Tool function: takes args + sandbox, returns result string.</summary>
public delegate Task<string> ToolFn(Dictionary<string, object?> args, ISandbox sandbox, string sessionId, CancellationToken ct);

/// <summary>Middleware function: mutates agent state, returns nothing.</summary>
public delegate Task MiddlewareFn(AgentState state, CancellationToken ct);

public sealed class AgentState
{
    public List<ChatMessage> Messages { get; set; } = [];
    public List<Observation> LastObservations { get; set; } = [];
    public int Iteration { get; set; }
    public int MaxIterations { get; set; }

    /// <summary>
    /// The value of <see cref="Iteration"/> when the current user turn began.
    ///
    /// <see cref="MaxIterations"/> is a RUNAWAY DETECTOR — it answers "is this
    /// one request never going to stop?". But <see cref="Iteration"/> is
    /// cumulative over the whole session and never resets, so measuring the cap
    /// from zero charges the 40th question for everything the first 39 spent.
    /// An interactive session then dies of old age with no runaway anywhere in
    /// it (observed: a chat killed at exactly 200 while answering ordinary
    /// questions). Interactive callers measure the cap against this instead.
    ///
    /// Left at 0 for non-interactive runs, where the whole run IS one turn and
    /// the cumulative count is the right denominator.
    /// </summary>
    public int TurnStartIteration { get; set; }
    public bool StopLoop { get; set; }
    public string StopReason { get; set; } = "";
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }

    /// <summary>The provider's own <c>Usage.InputTokenCount</c> for the most
    /// recent request — the exact size of that prompt, tool schemas and all.
    /// 0 = no response yet, or history was rewritten since (see below).
    ///
    /// This exists because <c>TokenEstimator.Estimate</c> is measurably ~2.4x
    /// low on a real turn (it cannot see tool schemas, and no production model
    /// matches its ratio table), and compaction decides when to fire from that
    /// number. Both fields together let
    /// <c>TokenEstimator.EstimateContext</c> anchor on what the provider
    /// actually counted and guess only the tail appended since.</summary>
    public int LastRealInputTokens { get; set; }

    /// <summary>How many messages were in <see cref="Messages"/> when the
    /// request behind <see cref="LastRealInputTokens"/> was sent. Captured at
    /// the same instant, because the pair is only meaningful together.
    ///
    /// ⚠ ANY code that CLEARS or REWRITES <see cref="Messages"/> must reset
    /// <see cref="LastRealInputTokens"/> to 0. A stale anchor describes a
    /// prompt that no longer exists; left in place it reports the
    /// pre-compaction size forever, so compaction re-fires on every iteration
    /// and eats the conversation one keep-window at a time.</summary>
    public int LastRealInputMessageCount { get; set; }

    /// <summary>The user's ORIGINAL ask, captured the first time this run
    /// compacted and never overwritten afterwards.
    ///
    /// ⭐ WHY IT IS A FIELD AND NOT RE-DERIVED EACH TIME. A compaction digest
    /// recovers the original task by walking to the first user turn that is not
    /// one of our own injected blocks. That works exactly once. On the SECOND
    /// compaction the earliest real user turn is no longer the original ask —
    /// it is whatever the user said mid-run ("keep going", "now do the docs"),
    /// because the actual first turn was discarded by the first compaction. So
    /// the stated task walks forward one turn per checkpoint, and on the long
    /// multi-compaction runs this feature exists to serve it ends up quoting an
    /// incidental aside as the objective.
    ///
    /// Captured once, it cannot drift. <c>CompactionMiddleware.CaptureOriginalTask</c>
    /// falls back to scraping it out of a prior digest when this is empty —
    /// which is the case after a <c>--resume</c>, where the field is gone but
    /// the digest survived in the transcript.</summary>
    public string? OriginalTask { get; set; }
    /// <summary>CONSECUTIVE count of iterations where the model emitted
    /// no structured tool_calls despite (apparently) trying to. Caps the
    /// constrained-decoding retry loop so a model stuck in a bad-format
    /// groove doesn't burn the full max_iterations budget. Zeroed by any
    /// real reply (see the reset in AgentLoop before the assistant message
    /// is appended); it was a LIFETIME count until 2026-09-05 -- EpicForge
    /// law 129 -- despite every comment around its cap saying otherwise.</summary>
    public int MalformedToolCallRetries { get; set; }

    /// <summary>CONSECUTIVE count of iterations where the model returned
    /// an assistant message with `Contents.Count == 0` (no text, no
    /// tool calls, no reasoning). Caps the empty-response recovery so
    /// a wedged model can't loop forever. Diagnostic 2026-05-19 traced
    /// every observed cross-tier `members=[]` leader-bail failure to
    /// this exact pattern. Zeroed by any real reply (law 129).</summary>
    public int EmptyResponseRetries { get; set; }

    /// <summary>CONSECUTIVE count of iterations where the provider reported
    /// finish_reason=length -- the response hit `max_output_tokens` and was
    /// cut off mid-generation -- AND no tool call survived in it. Zeroed by
    /// any real reply (law 129: four truncations spread across 32 iterations
    /// killed a seat that had done twenty healthy turns between them).
    ///
    /// MEASURED 2026-09-01 (epic-forge E3 run 6, ef-team-flash, bound 4096).
    /// The leader spent an entire 4096-token turn in a floating-point
    /// arithmetic loop ("1.1 + 2.2 = 3.1? No. 1.1 + 2.2 = 3.1? ...") and was
    /// truncated before it ever emitted a call. A tool-call-less turn reads as
    /// "the model is done", so the run finalised -- with a COMPLETED dispatch
    /// still sitting unreviewed, whose worktree held the two modules the epic
    /// was missing. The work existed and was discarded because a cut-off
    /// sentence was mistaken for a final answer.
    ///
    /// Note the shape: this is the failure mode INTRODUCED by fixing the
    /// opposite one. Unbounded generation stalled runs 1-4; the bound fixed
    /// that and created this. Both are real, which is why the recovery caps
    /// rather than removes the bound.</summary>
    public int TruncatedResponseRetries { get; set; }

    /// <summary>Law 244 (batch 14, 2026-09-09): how many of the truncated-response
    /// recoveries in the CURRENT streak were prose loops (law 240). The loop
    /// clusters late -- s14c/d/e fired it 6-7 times each after ~2,000 s at
    /// 50k-78k input tokens, under the compaction threshold -- and the retry
    /// re-enters the same context that produced it; s14c's retry_count climbed
    /// 1->4 across four consecutive iterations and the seat died
    /// `truncated_response_exhausted`. At two in a row the loop compacts the
    /// history before the nudge. Zeroed with the other streaks by a real reply,
    /// and by the compaction itself.</summary>
    public int ConsecutiveProseLoopRecoveries { get; set; }

    /// <summary>The repeat-call break (AgentLoop, dispatch site; EpicForge law
    /// 129). Signature -- tool name + serialised arguments -- of the most recent
    /// turn that made exactly ONE tool call, and the bytes it returned. Null
    /// after a fan-out turn. A sole call repeating this signature whose two
    /// previous runs returned these exact bytes is refused rather than run.</summary>
    public string? LastSoleCallSignature { get; set; }
    public string? LastSoleCallResult { get; set; }

    /// <summary>How many consecutive sole calls have been identical -- same
    /// signature, same result -- to the one before them. 0 after any call that
    /// differs in either. Counts refused issues too, so the fifth identical
    /// issue can end the run (`repeated_call_exhausted`).</summary>
    public int IdenticalCallStreak { get; set; }

    /// <summary>Cumulative count of iterations where the model emitted
    /// prose with no tool call AND no prior tool activity in the
    /// conversation. Caps the nudge loop so a model that simply refuses
    /// to engage doesn't infinite-loop. Diagnostic 2026-05-19 found
    /// 3 of 145 sessions where a sub-agent said "Dispatched, waiting
    /// for results" in iter 1 and the harness closed the dispatch as
    /// "completed" with zero work done.</summary>
    public int NoToolEngagementRetries { get; set; }

    /// <summary>Cumulative count of iterations where the leader CAN
    /// dispatch (assign_async is in its tool list) but never has, AND
    /// emitted prose with no tool call. Caps the nudge loop so a
    /// leader that won't engage with dispatch despite being prodded
    /// can fall through to the original wait-for-input path.
    /// Diagnostic 2026-05-19 (patched f3 r1, full-sweep f2 r1) showed
    /// a leader-stuck-in-exploration pattern: leader does read-only
    /// tool calls indefinitely but never dispatches.</summary>
    public int LeaderStuckRetries { get; set; }

    /// <summary>R1 (relay): the context-fullness percent at which the
    /// last wrap-up nudge was injected; 0 = never. Gates the +5-point
    /// re-nudge cadence in <see cref="Vett.Agent.RelayContext"/>.</summary>
    public int LastContextNudgePct { get; set; }

    /// <summary>R1 (relay): a wrap-up instruction computed at usage
    /// capture (mid response processing, where appending a user message
    /// would break message ordering) and injected at the TOP of the next
    /// iteration, where ordering is safe — same channel the coordinator's
    /// pre-iteration hook and user interjections use.</summary>
    public string? PendingContextNudge { get; set; }

    /// <summary>When non-null, the next LLM call forces
    /// <c>tool_choice={"type":"function","function":{"name":&lt;value&gt;}}</c>
    /// so vLLM's constrained decoder enforces a valid call to that
    /// specific tool, token-by-token. Used to recover from iterations
    /// where the model produced tag-shaped junk in plain text (e.g.
    /// <c>&lt;file_editor&gt;</c>, <c>file_editor create file_text=...</c>)
    /// — we extract the tool name the model was apparently trying to
    /// call and force-pin the next sample to that name. AgentLoop sets
    /// this on detection and clears it after the next call.
    /// We use specific-tool rather than <c>tool_choice="required"</c>
    /// because the union-grammar path hangs on aeon-mtp + MTP; the
    /// single-tool grammar works cleanly.</summary>
    public string? ForceToolNameNextCall { get; set; }
}

public sealed class Observation
{
    public string ToolCallId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public string Result { get; set; } = "";
    public bool Success { get; init; }
}

/// <summary>The 5 built-in tools.</summary>
public static class Builtins
{
    public const string TimeoutPrefix = "Error: Command timed out after ";
    public const string SubmitMarker = "__VETT_SUBMIT__";

    /// <summary>
    /// Marks a tool result that reports "the async task you asked about is
    /// still running" — a BENIGN, EXPECTED status, not a fault.
    ///
    /// It has to keep the "Error:" prefix that every other failure carries,
    /// because AgentLoop.cs:1577 derives `tool_call_end.success` from exactly
    /// that prefix, and Harness.cs:439 documents why a false `success` on the
    /// accept/reject path corrupts the settle gate's `pendingReviews` counter.
    /// So the string stays an error to the transport, and this marker is how
    /// StuckDetector tells a benign wait apart from a real error loop.
    ///
    /// Shared constant on purpose: the producer (DispatchTools) and the
    /// consumer (BuiltinMiddleware.StuckDetector) drifting apart would
    /// silently restore the fan-out kill described there.
    /// </summary>
    public const string StillRunningMarker = "is still RUNNING";

    public static Dictionary<string, ToolFn> All() => new()
    {
        ["terminal"] = TerminalAsync,
        ["file_editor"] = FileEditorAsync,
        ["think"] = (_, _, _, _) => Task.FromResult("Your thought has been logged."),
        ["finish"] = (args, _, _, _) =>
        {
            var msg = Str(args, "message", "Task completed.");
            return Task.FromResult($"{SubmitMarker}{msg}");
        },
        ["task_tracker"] = TaskTracker.RunAsync,
    };

    public static List<MiddlewareFn> DefaultMiddleware() =>
    [
        BuiltinMiddleware.SubmitDetector,
        BuiltinMiddleware.OutputTruncation,
        BuiltinMiddleware.StuckDetector,
    ];

    private static async Task<string> TerminalAsync(
        Dictionary<string, object?> args, ISandbox sandbox, string sessionId, CancellationToken ct)
    {
        var command = Str(args, "command");
        // SWE-bench commands (pip install, pytest, sympy/django builds) routinely
        // exceed 120s. Bumped to 600s to avoid the bash-kill cascade in the sidecar.
        var timeout = Int(args, "timeout", 600);

        if (string.IsNullOrEmpty(command))
            return "Error: command is required";

        // 2026-09-10 LAWS 257 + 258: see RestoredPaths. This runs BEFORE the
        // shell, so a refused command destroys nothing -- s23c's three restores
        // were each undone by the very next `rm` it sent (257), and s24d's was
        // undone by four `cat > path <<EOF` redirects (258).
        if (RestoredWriteTarget(sessionId, command) is { } guard)
        {
            var code = guard.IsRm ? "rm_of_restored_file" : "overwrite_of_restored_file";
            var verb = guard.IsRm ? "delete" : "truncate and overwrite";
            return $"Error: {code}: this command would {verb} `{guard.Path}`, and the harness put that file back " +
                   "for you a moment ago after you deleted it -- it holds real work of yours that is not anywhere else. " +
                   "Destroying it again loses that work again and the next identical `create` will be refused, so the loop " +
                   "you are in cannot end this way. Change the file instead: `str_replace` with `old_str` = a snippet that " +
                   "is in it and `new_str` = what you want there, or `insert` to add lines. Once you have edited it, this " +
                   "command will run.";
        }

        var r = await sandbox.BashExecAsync(sessionId, command, timeout, ct);

        if (r.TimedOut)
        {
            // Report the deadline the sandbox ENFORCED, not the one we asked
            // for. DirectBash (every team-bench run uses it — Bench/Team/
            // Harness.cs builds one unconditionally) clamps this 600s default
            // down to its own 120s ceiling, so the old `{timeout}` here told
            // the model "timed out after 600 seconds" for a command that was
            // killed at 120s — while DirectBash's own trailer in r.Stdout said
            // 120s, leaving the two halves of one message contradicting each
            // other. Null means the sandbox applied no clamp (the sidecar
            // path), where the requested number was already the true one.
            //
            // This only corrects the NUMBER. The prefix is unchanged, so
            // every consumer that keys off it — AgentLoop's sawTimeout,
            // softFailed, and Observation.Success — behaves identically.
            var enforced = r.EffectiveTimeoutSec ?? timeout;
            return $"{TimeoutPrefix}{enforced} seconds.\n{r.Stdout}";
        }

        // Match OpenHands' TerminalObservation envelope: stdout first, then
        // metadata trailers. Order matches openhands-tools/terminal/definition.py
        // TerminalObservation.to_llm_content. Python interpreter line is omitted
        // (the sidecar doesn't currently track it).
        var sb = new System.Text.StringBuilder(r.Stdout);
        if (!string.IsNullOrEmpty(r.Cwd))
            sb.Append("\n[Current working directory: ").Append(r.Cwd).Append(']');
        sb.Append("\n[Command finished with exit code ").Append(r.ExitCode).Append(']');

        // Patch-6: when dotnet emits CS0246 (missing type/namespace) or
        // CS0103 (missing name), the model often loops trying random
        // fixes (creating duplicate files, adding wrong usings). Scan
        // the workspace for where the missing symbol is actually defined
        // and append a hint pointing at the right `using` directive.
        // Same shape as patches 4 + 5: tool-layer enforcement, no prompt
        // changes needed. Conservative: only runs on CS0246/CS0103 output.
        var hint = await TryBuildErrorHintAsync(r.Stdout, sandbox, sessionId, ct);
        if (!string.IsNullOrEmpty(hint))
            sb.Append('\n').Append(hint);

        return sb.ToString();
    }

    private static readonly Regex Cs0246Regex =
        new(@"error CS0246:[^']*'([^']+)'", RegexOptions.Compiled);
    private static readonly Regex Cs0103Regex =
        new(@"error CS0103:[^']*'([^']+)'", RegexOptions.Compiled);

    /// <summary>
    /// Patch-6: when dotnet build / dotnet test stdout contains CS0246 or
    /// CS0103 errors, search the workspace for where each missing symbol
    /// is actually defined and produce a hint suggesting the right
    /// `using` directive. Bounded to 5 symbols per call.
    /// </summary>
    private static async Task<string> TryBuildErrorHintAsync(
        string stdout, ISandbox sandbox, string sessionId, CancellationToken ct)
    {
        if (!stdout.Contains("error CS0246") && !stdout.Contains("error CS0103"))
            return "";

        var symbols = new HashSet<string>();
        foreach (Match m in Cs0246Regex.Matches(stdout))
        {
            var sym = m.Groups[1].Value;
            // Strip generic suffix: "List<Foo>" → "List". The base name is
            // what's defined in source, so what we search for.
            if (sym.IndexOf('<') is int idx && idx > 0) sym = sym[..idx];
            sym = sym.Trim();
            if (sym.Length > 0) symbols.Add(sym);
        }
        foreach (Match m in Cs0103Regex.Matches(stdout))
        {
            var sym = m.Groups[1].Value.Trim();
            if (sym.Length > 0) symbols.Add(sym);
        }
        if (symbols.Count == 0) return "";

        var hints = new System.Text.StringBuilder();
        var added = 0;
        foreach (var sym in symbols)
        {
            if (added >= 5) break;

            // Look for `class Sym | interface Sym | record Sym | enum Sym | struct Sym`
            // in .cs files under cwd. -E (ERE), --include, --line-buffered avoided.
            // Use single quotes; symbols are simple identifiers post-strip.
            var grepCmd = $"grep -rn --include='*.cs' -E '(class|interface|record|enum|struct)[[:space:]]+{sym}([[:space:]]|<|:|$)' . 2>/dev/null | head -3";
            var grepR = await sandbox.BashExecAsync(sessionId, grepCmd, 15, ct);
            if (grepR.ExitCode != 0 || string.IsNullOrWhiteSpace(grepR.Stdout))
                continue;

            // First match line: "./path/Foo.cs:42:    public class Foo : Bar"
            var firstHit = grepR.Stdout.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (firstHit is null) continue;
            var colonIdx = firstHit.IndexOf(':');
            if (colonIdx < 0) continue;
            var file = firstHit[..colonIdx];

            // Find the file's namespace declaration. Supports both
            // file-scoped (`namespace Foo;`) and block-scoped (`namespace Foo {`).
            var nsCmd = $"grep -E '^namespace ' '{file}' | head -1";
            var nsR = await sandbox.BashExecAsync(sessionId, nsCmd, 15, ct);
            var nsLine = nsR.Stdout.Trim();
            var ns = nsLine
                .Replace("namespace ", "")
                .TrimEnd(';', ' ', '{', '\r')
                .Trim();

            if (added == 0)
                hints.AppendLine("[Hint (missing-using detector):");
            if (!string.IsNullOrEmpty(ns))
                hints.AppendLine($"  - `{sym}` is defined in namespace `{ns}` ({file}). " +
                                 $"Add `using {ns};` to the top of the file that references it.");
            else
                hints.AppendLine($"  - `{sym}` is defined at {file}. " +
                                 "Check that file's namespace and add a matching `using`.");
            added++;
        }
        if (added > 0) hints.Append("]");
        return hints.ToString();
    }

    /// <summary>
    /// file_editor create with field validation. Two issues seen in
    /// the wild that this guards against:
    ///   1. Model forgets `file_text` entirely. The schema doesn't list it
    ///      as required (only command/path/security_risk are), so a missing
    ///      file_text used to silently produce a 0-byte file. Now we error
    ///      so the model can self-correct on retry.
    ///   2. Model uses `content` instead of `file_text` (cross-pollination
    ///      from other tool ecosystems). Accepted as an alias.
    /// </summary>
    // 2026-09-09 (s14a it.19-26, batch 14): after str_replace_noop refused a
    // no-op edit of facts-01.js, the seat took the file_exists hint below
    // literally -- `rm` then `create` -- and re-created the file with the SAME
    // 1,401 bytes it had just deleted, saw the same view, sent the no-op
    // again, deleted again: a three-reply livelock the repeated-call breaker
    // cannot see because rm / create / str_replace differ in shape. Per
    // (session, path) we keep the hash of the last create's bytes and how
    // many times in a row they have been re-created: the first identical
    // re-create is WARNED on (a genuine restore after a mistaken delete stays
    // possible), the second is REFUSED.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Hash, int Repeats, string Text)> LastCreate = new();

    // 2026-09-10 LAW 257 (batch 23, VETT da053df, RAN on the wire): law 255
    // restored `src/facts/facts-02.js` for s23c three times -- it.28, it.31,
    // it.34, 6,557 chars and 103 lines each -- and the seat answered each
    // restore with `rm -f` of that same path at it.30, it.33 and it.35, its
    // last iteration of the hour. The refusal it ignored three times says, in
    // those words, "Do NOT `rm` it and do NOT `create` it"; s23a read the same
    // sentence at it.29 and moved to view + str_replace, so the message works
    // on a seat that reads and reaches nobody else. Laws 255 and 256 both edit
    // what the seat READS. This one is a DOOR: while a path sits in here, a
    // `terminal` `rm` naming it is refused and never reaches the shell. The
    // seat leaves by EDITING the file (str_replace, insert, or a create with
    // different bytes) -- which is the thing we wanted it to do anyway.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> RestoredPaths = new();

    /// <summary>`rm` as a command word: at the start, or after a separator.</summary>
    private static readonly Regex RmWordRegex = new(@"(?:^|[;&|(]|\s)\s*rm\s", RegexOptions.Compiled);

    // 2026-09-10 LAW 258 (batch 24, VETT ac36d14, RAN on the wire): law 257's
    // door matches the command word `rm`. s24d walked around it with a SHELL
    // REDIRECT. On the wire: it.39 law 255 restored `src/facts/facts-01.js`
    // (1,500 chars / 22 lines) and said "Do NOT `rm` it and do NOT `create`
    // it"; it.41 the seat tried a `str_replace` and IT FAILED --
    // `str_replace_no_match`, because its `old_str` was retyped from memory and
    // s24d never called `view` once in the whole hour. A FAILED edit does NOT
    // clear RestoredPaths (see ClearRestored's callers: only a non-"Error:"
    // result clears), so the door was STILL ARMED when it.42 and it.43 sent FOUR
    // `cat > src/facts/facts-01.js <<'EOF'` calls that truncated the file and
    // rewrote it with degenerate text -- the arm ended with 19 entries, 5 of
    // them distinct, fourteen copies of one sentence, the worst facts artifact
    // in the batch. A truncating redirect destroys banked work exactly as `rm`
    // does, and neither law 255 nor law 257 saw it. `>>` and `tee -a` APPEND
    // and are left alone: the door is on DESTRUCTION, not on writing, because
    // a door that refused every write would only move the loop elsewhere.

    /// <summary>`tee` ending the text before a path, with no append flag.</summary>
    private static readonly Regex TeeTruncRegex = new(@"(?:^|[;&|(]|\s)tee((?:\s+-{1,2}[^\s]+)*)\s*$", RegexOptions.Compiled);

    /// <summary>Law 258: does this command TRUNCATE <paramref name="path"/>?
    /// `> path`, `>| path` and `tee path` do. `>> path` and `tee -a` do not.</summary>
    private static bool TruncatesPath(string command, string path)
    {
        for (var i = command.IndexOf(path, StringComparison.Ordinal); i >= 0;
             i = command.IndexOf(path, i + path.Length, StringComparison.Ordinal))
        {
            var head = command[..i].TrimEnd();
            if (head.EndsWith('"') || head.EndsWith('\'')) head = head[..^1].TrimEnd();
            if (head.EndsWith(">|", StringComparison.Ordinal)) return true;
            if (head.EndsWith('>') && !head.EndsWith(">>", StringComparison.Ordinal)) return true;
            var m = TeeTruncRegex.Match(head);
            if (m.Success && !m.Groups[1].Value.Contains('a')) return true;
        }
        return false;
    }

    /// <summary>Laws 257/258: the restored path this command would destroy, and
    /// whether by `rm` (true) or by a truncating redirect (false), or null.</summary>
    private static (string Path, bool IsRm)? RestoredWriteTarget(string sessionId, string command)
    {
        if (string.IsNullOrEmpty(command)) return null;
        var isRm = RmWordRegex.IsMatch(command);
        var prefix = sessionId + "\n";
        foreach (var k in RestoredPaths.Keys)
        {
            if (!k.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var path = k[prefix.Length..];
            if (path.Length == 0 || !command.Contains(path, StringComparison.Ordinal)) continue;
            if (isRm) return (path, true);
            if (TruncatesPath(command, path)) return (path, false);
        }
        return null;
    }

    /// <summary>Law 257: the seat edited the file, so the door opens.</summary>
    private static void ClearRestored(string sessionId, string path)
        => RestoredPaths.TryRemove(CreateKey(sessionId, path), out _);

    private static string CreateKey(string sessionId, string path) => sessionId + "\n" + path;

    // 2026-09-09 LAW 250 (batch 17, 29 refusals across five arms, all but one
    // on src/facts/facts-01.js): the str_replace_noop refusal above is
    // correct and the seat still re-sends the pair -- s17g it.24/26/29, s17f
    // it.26-28/31-32/40/44-47, s17a it.33-34/38-39 -- because the seat's plan
    // is "rewrite this 1,300-1,600-char block" and, with old_str in front of
    // it, its new_str comes out as a copy. The seat's own text says so ("I
    // need to actually change the content") and then it copies again; on
    // s17g it escaped by `rm` + `create` (it.27-28) and on the others by
    // luck at 3-6 iterations a piece. The first refusal explains the field;
    // the SECOND names the file's line count and the three calls that cannot
    // copy: `insert` at the last line for an append, `create` for a rewrite,
    // a 3-line `old_str` for a change. Consecutive per (session, path);
    // any edit of the path that is not refused clears it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> NoopRepeats = new();

    private static string ClearNoop(string sessionId, string path, string result)
    {
        if (!result.StartsWith("Error:", StringComparison.Ordinal)) { NoopRepeats.TryRemove(CreateKey(sessionId, path), out _); ClearRestored(sessionId, path); }
        return result;
    }

    /// <summary>The number of numbered lines in a file view ("N\tline"), or -1 when the view is not numbered.</summary>
    internal static int NumberedLineCount(string view)
    {
        var last = -1;
        foreach (var raw in view.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab > 0 && int.TryParse(line.AsSpan(0, tab), out var n)) last = n;
        }
        return last;
    }

    private static string Sha256Hex(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }

    private static async Task<string> CreateValidated(
        ISandbox sandbox, string sessionId, string path,
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var ft = StrOrNull(args, "file_text") ?? StrOrNull(args, "content");
        if (ft is null)
            return "Error: file_editor `create` requires the `file_text` parameter (the full contents to write). " +
                   $"Re-call as: {{\"command\":\"create\",\"path\":\"{path}\",\"file_text\":\"<contents>\",\"security_risk\":\"LOW\"}}";
        var key = CreateKey(sessionId, path);
        var hash = Sha256Hex(ft);
        var repeats = LastCreate.TryGetValue(key, out var prev) && prev.Hash == hash ? prev.Repeats + 1 : 0;
        if (repeats >= 2)
        {
            var refused = $"Error: create_repeats_last_create: you have now sent `create` for `{path}` with EXACTLY the same bytes " +
                   "three times in this session (deleting the file in between changes nothing). Re-creating identical content " +
                   "cannot make it different. Write content that is NOT already in this conversation for this file -- " +
                   "different entries, different subjects, different wording -- or move on to the next file.";
            // 2026-09-10 LAW 255 (batch 22, VETT 455f523: s22a it.24-28, s22b
            // it.19-20/41, s22d it.19-21, s22h it.27-32): every refusal above
            // landed on a path the seat had JUST `rm`'d, so the refusal left the
            // tree with the file GONE -- s22b and s22h ended their hour with
            // src/facts/ empty, s22h's 100 facts destroyed at it.26. The harness
            // is holding the bytes it refuses: when the path is absent, write
            // them back and say so; the call stays a refusal (the repeat counter
            // and law 254's compaction still see it). Present path: plain refusal.
            string restore;
            try { restore = await sandbox.FileCreateAsync(sessionId, path, ft, ct); }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("file_exists", StringComparison.Ordinal)) { restore = "Error: " + ex.Message; }
            if (restore.StartsWith("Error", StringComparison.Ordinal)) return refused;
            RestoredPaths[key] = 1;                        // LAW 257: the door closes on this path
            var lines = NumberedLineCount(restore);
            var recipe = lines > 1
                ? $" To ADD entries call file_editor `insert` with `insert_line` = {lines - 1} (before the closing line) and `new_str` = NEW lines that are not in this conversation."
                : " To ADD entries use file_editor `insert` with NEW lines that are not in this conversation.";
            return refused + $"\nRESTORED: `{path}` was ABSENT (your `rm` had deleted it) and now holds those exact bytes again " +
                   $"({ft.Length} chars{(lines > 0 ? $", {lines} lines" : "")}) -- that work is on disk. Do NOT `rm` it and do NOT `create` it." + recipe;
        }
        // 2026-09-09 LAW 252 (batch 18 s18h it.7/8/9/17, s18a it.18 -- every
        // `file_exists` the seats saw on batches 13-18): DirectBash does not
        // RETURN "Error: file_exists: <path>", it THROWS
        // InvalidOperationException("file_exists: <path>"), so the rewrite
        // below never ran on the wire; the seat got DispatchAsync's raw
        // "Error: file_exists: src/game/pieces.js" and no rm-then-create
        // hint. The test that guarded the rewrite used a sandbox that
        // returned the string. Fold the throw into the string path.
        string result;
        try
        {
            result = await sandbox.FileCreateAsync(sessionId, path, ft, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("file_exists", StringComparison.Ordinal))
        {
            result = "Error: " + ex.Message;
        }
        if (!result.StartsWith("Error", StringComparison.Ordinal))
        {
            LastCreate[key] = (hash, repeats, ft);
            if (repeats == 1)
                return $"Warning: create_repeats_last_create: `{path}` now holds EXACTLY the bytes you created for it earlier in this session " +
                       "(and then deleted or replaced). If you meant to write NEW content, this is not new -- the next identical " +
                       "`create` of this path will be refused.\n" + result;
        }
        // The sidecar returns "Error: file_exists: <path>" when create
        // is called on an existing file. This was observed to wedge AEON
        // for 3 ticket attempts in a row (Realmscape v3 T04 2026-05-25):
        // agent kept retrying `create` on Realm/RoomGraph.cs to add new
        // methods, got the same cryptic error each time, never figured
        // out it should use `str_replace` to modify the existing file.
        // Rewrite the error to point at the right tool + the typical
        // str_replace shape.
        if (result.StartsWith("Error: file_exists", StringComparison.Ordinal))
        {
            // 2026-09-10 LAW 256 (batch 22, VETT 455f523, RAN on the wire):
            // SIX of six `already exists` hints were followed by `rm` of that
            // same path, ZERO by `str_replace` -- s22a it.18, s22b it.15 and
            // it.32, s22d it.13, s22e it.25, s22f it.7; s23a it.20 repeated it
            // on the law-255 binary and then spent it.21-34 deleting and
            // regenerating facts-01.js. The rm-then-create branch below is the
            // SEED of the delete-and-regenerate livelock that law 243 refuses
            // and law 255 has to repair: the seat deletes a good file, writes
            // back bytes it has already written, and pays for it. It is our
            // sentence, not the model's idea. Offer it only on the FIRST
            // collision. Once this session has successfully created the path
            // (the key is banked), the seat has been here before, so give the
            // non-destructive recipe and do not put `rm` in front of it at all
            // -- law 255's "Do NOT `rm` it" was already on the wire at s23a
            // it.29 and the seat rm'd twice more, so a negation is not a fix.
            var head = $"Error: file_editor `create` failed — `{path}` already exists. ";
            if (LastCreate.ContainsKey(key))
                return head +
                       "You already created this path earlier in this session, so `create` is the wrong tool for it now. " +
                       "The content is on disk: to ADD to it call `insert`; to CHANGE part of it call `str_replace` " +
                       "(`old_str` = the existing snippet, `new_str` = the replacement). " +
                       "Regenerating this file from scratch would discard work you have already done.";
            return head +
                   $"To MODIFY an existing file, use `str_replace` instead (specify `old_str` = the existing snippet to replace, `new_str` = the replacement); to ADD lines, use `insert`. " +
                   $"Only if you must discard the whole file and start from DIFFERENT content, run `terminal` with `rm \"{path}\"` first — but prefer `str_replace`, since it preserves the rest of the file.";
        }
        return result;
    }

    /// <summary>
    /// `str_replace` validation. The sidecar HANGS indefinitely when
    /// invoked with an empty `old_str` (presumably tries to enumerate
    /// matches for an empty string, which matches every position in
    /// the file). Bug-#4 reproduction: A12 f3 r2 (2026-05-23 19:56Z)
    /// — leader emitted `str_replace` with `new_str` + `path` but no
    /// `old_str`. Tool call started at t+230s and never returned; the
    /// instance timed out at 1800s killing the run with the only A12
    /// fail in the all-3-patches sweep.
    ///
    /// Validate fast and return a self-correcting error so the model
    /// can retry with the missing field.
    /// </summary>
    private static async Task<string> StrReplaceValidated(
        ISandbox sandbox, string sessionId, string path,
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var oldStr = StrOrNull(args, "old_str");
        var newStr = StrOrNull(args, "new_str");
        if (string.IsNullOrEmpty(oldStr))
            return "Error: file_editor `str_replace` requires a non-empty `old_str` parameter " +
                   "(the exact text to replace). Re-call with both `old_str` and `new_str` set. " +
                   $"If you intended to rewrite the entire file, use `create` instead with `file_text`.";
        if (newStr is null)
            return "Error: file_editor `str_replace` requires a `new_str` parameter " +
                   "(the replacement text, may be empty to delete). Re-call with both `old_str` and `new_str` set.";
        // 2026-09-09 (s13c it.23-29, batch 13): the seat sent a str_replace
        // whose new_str was BYTE-IDENTICAL to its old_str (1,267 chars), the
        // sandbox applied it and returned the numbered view as success, and
        // the seat -- told "edited", seeing nothing edited -- sent the same
        // call five more times until the identical-call breaker ended the
        // arm at 1,591 s of 3,600 s with 80 facts unwritten. The breaker is
        // the executioner, not the cause: a no-op is a refusal, and it is
        // spelled as one on the FIRST call so the seat has something to
        // react to.
        if (string.Equals(oldStr, newStr, StringComparison.Ordinal))
        {
            var key = CreateKey(sessionId, path);
            var repeats = NoopRepeats.AddOrUpdate(key, 1, (_, n) => n + 1);
            if (repeats < 2)
                return "Error: str_replace_noop: `new_str` is identical to `old_str`, so the file would not change. " +
                       "Put the NEW text in `new_str` (the text you want to appear in place of `old_str`); " +
                       "to ADD lines after a snippet, set `new_str` to that snippet followed by the new lines.";
            // Law 250: the second refusal in a row names what can change the file.
            var lines = -1;
            try { lines = NumberedLineCount(await sandbox.FileViewAsync(sessionId, path, ct)); } catch (OperationCanceledException) { throw; } catch (Exception) { }
            var where = lines > 0 ? $"`{path}` has {lines} lines. " : "";
            var atLine = lines > 0 ? lines.ToString() : "<last line number>";
            return $"Error: str_replace_noop_repeated: `new_str` equalled `old_str` for `{path}` {repeats} times in a row -- " +
                   "when `old_str` is a block this long, your `new_str` comes out as a copy of it, and re-sending the pair " +
                   "cannot change the file. " + where + "Do ONE of these instead: " +
                   $"(a) to APPEND, call `insert` with `insert_line`: {atLine} and `new_str` = ONLY the new lines; " +
                   "(b) to REWRITE the whole file, first run `terminal` with `rm -f <path>`, THEN call `create` with `file_text` = the complete new content, written fresh " +
                   "(`create` on an existing path is refused; the `terminal` then `create` pair in ONE reply runs in that order); " +
                   "(c) to CHANGE a few lines, send an `old_str` of at most 3 lines and a `new_str` that differs from it.";
        }
        var (content, err) = await sandbox.FileStrReplaceAsync(sessionId, path, oldStr, newStr, ct);
        if (err is null) { NoopRepeats.TryRemove(CreateKey(sessionId, path), out _); ClearRestored(sessionId, path); }
        // 2026-09-09 (s12a it.19): the sandbox's `str_replace_no_match` /
        // `str_replace_multi_match` came back WITHOUT the `Error:` prefix that
        // every other refusal carries, so the loop's soft-fail marker read it
        // as success, `tool_call_end` said `success:true`, and a loop salvage
        // built on that bit told the seat its lines "were written" when the
        // file had not changed. A refusal is an error, spelled like one.
        return err is null ? content : "Error: " + err;
    }

    /// <summary>
    /// Apply the schema-advertised `view_range` to a numbered file view.
    ///
    /// The file_editor schema has promised `view_range` since day one but
    /// nothing ever read it: a member viewing a 1281-line file with
    /// view_range [565, 700] silently got the whole file from line 1, which
    /// output-truncation then clipped — the MIDDLE of any large file was
    /// unreachable no matter what range the model asked for. Shakedown runs
    /// #47–#49 all stalled exactly there: on #49, implementer-4 requested
    /// [565, 700] five times, never saw the real LowerStatement signature at
    /// line 565, hallucinated a `static ... =>` shape for it, and burned its
    /// whole budget on four str_replace no_match failures against text that
    /// does not exist. The ticket's target file was effectively read-proof.
    ///
    /// Slices the already-rendered "N\tline" output rather than re-reading
    /// the file, so every ISandbox (local + RPC) gets it for free. Directory
    /// listings carry no line-number prefixes and pass through untouched, as
    /// does anything with a missing/malformed range (full view — the old
    /// behavior, never worse). A range fully past EOF returns an explicit
    /// error naming the file's real line count instead of a silent full dump.
    /// </summary>
    public static string ApplyViewRange(string view, Dictionary<string, object?> args)
    {
        var range = IntArrayOrNull(args, "view_range");
        if (range is null || range.Length != 2) return view;
        var (start, end) = (range[0], range[1]);
        if (start < 1 || (end != -1 && end < start)) return view;

        var lines = view.Split('\n');
        var kept = new List<string>();
        var lastNumbered = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab <= 0 || !int.TryParse(line.AsSpan(0, tab), out var n)) continue;
            lastNumbered = n;
            if (n >= start && (end == -1 || n <= end)) kept.Add(line);
        }
        if (lastNumbered == 0) return view;   // not a numbered file view (directory listing etc.)
        if (kept.Count == 0)
        {
            // Range fully past EOF: the model hallucinated line numbers (e.g. asks
            // [830,870] of a 425-line file — ~8% of file_editor calls in bench runs
            // do this and then waste turns). Rather than erroring, clamp to a
            // SAME-SIZE window anchored at the real end of the file and say so, so
            // the model sees real content plus the true line count instead of
            // guessing again. A requested window bigger than the file shows the
            // whole file (its note makes that explicit, so it is never a silent dump).
            var window = end == -1 ? lastNumbered : end - start + 1;
            if (window < 1) window = 1;
            var clampStart = Math.Max(1, lastNumbered - window + 1);
            var tail = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                var tab = line.IndexOf('\t');
                if (tab <= 0 || !int.TryParse(line.AsSpan(0, tab), out var n)) continue;
                if (n >= clampStart) tail.Add(line);
            }
            return $"(note: requested view_range [{start}, {end}] is beyond the file's {lastNumbered} lines; showing the last {tail.Count})\n"
                 + string.Join("\n", tail) + "\n";
        }
        return $"(showing lines {start}-{(end == -1 ? lastNumbered : Math.Min(end, lastNumbered))} of {lastNumbered})\n"
             + string.Join("\n", kept) + "\n";
    }

    private static int[]? IntArrayOrNull(Dictionary<string, object?> a, string k)
    {
        if (!a.TryGetValue(k, out var v) || v is null) return null;
        try
        {
            if (v is System.Text.Json.JsonElement je)
                return je.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? je.EnumerateArray().Select(e => e.GetInt32()).ToArray()
                    : null;
            if (v is System.Collections.IEnumerable en and not string)
                return en.Cast<object?>().Select(Convert.ToInt32).ToArray();
        }
        catch { /* malformed range -> treated as absent */ }
        return null;
    }

    private static string? StrOrNull(Dictionary<string, object?> a, string k)
    {
        if (!a.TryGetValue(k, out var v) || v is null) return null;
        if (v is string s) return s;
        if (v is System.Text.Json.JsonElement je)
            return je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString() : je.ToString();
        return v.ToString();
    }

    private static async Task<string> FileEditorAsync(
        Dictionary<string, object?> args, ISandbox sandbox, string sessionId, CancellationToken ct)
    {
        var cmd = Str(args, "command_name", Str(args, "command"));
        var path = Str(args, "path");
        try
        {
            return cmd switch
            {
                "view" => ApplyViewRange(await sandbox.FileViewAsync(sessionId, path, ct), args),
                "create" => ClearNoop(sessionId, path, await CreateValidated(sandbox, sessionId, path, args, ct)),
                "str_replace" => await StrReplaceValidated(sandbox, sessionId, path, args, ct),
                "insert" => ClearNoop(sessionId, path, await sandbox.FileInsertAsync(sessionId, path, Int(args, "insert_line"), Str(args, "new_str"), ct)),
                "undo_edit" => await sandbox.FileUndoAsync(sessionId, path, ct),
                _ => $"Error: unknown file_editor command \"{cmd}\"",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Extract a string arg. Handles plain strings AND System.Text.Json.JsonElement,
    /// which is what Microsoft.Extensions.AI.OpenAI hands us for function-call
    /// arguments after JSON deserialization. Without the JsonElement branch,
    /// every tool call from a real OpenAI-compatible LLM silently sees empty args.
    /// </summary>
    internal static string Str(Dictionary<string, object?> a, string k, string d = "")
    {
        if (!a.TryGetValue(k, out var v) || v is null) return d;
        if (v is string s) return s;
        if (v is System.Text.Json.JsonElement je)
            return je.ValueKind == System.Text.Json.JsonValueKind.String ? (je.GetString() ?? d) : je.ToString();
        return v.ToString() ?? d;
    }

    internal static int Int(Dictionary<string, object?> a, string k, int d = 0)
    {
        if (!a.TryGetValue(k, out var v) || v is null) return d;
        if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Number)
            return je.GetInt32();
        try { return Convert.ToInt32(v); } catch { return d; }
    }
}
