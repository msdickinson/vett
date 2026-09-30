using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Llm;

namespace Vett.Tools;

/// <summary>
/// Context compaction middleware. Two strategies:
///
/// 1. Observation elision (fast, local): Keeps all messages but truncates
///    old tool outputs to a short summary. Preserves the message sequence
///    so the LLM knows what happened, without the full output text.
///
/// 2. Milestone checkpoint (at token threshold): Captures a summary of
///    progress so far, resets the conversation to system + summary + recent
///    messages. Prevents context overflow on long runs.
/// </summary>
public static class CompactionMiddleware
{
    /// <summary>
    /// Observation elision: truncate old tool results to save context space.
    /// Keeps the last N tool outputs intact, elides older ones.
    /// </summary>
    public static MiddlewareFn ObservationElision(int keepLastN = 5, int maxResultChars = 200)
    {
        return (state, ct) =>
        {
            // Find all tool result messages.
            var toolIndices = new List<int>();
            for (int i = 0; i < state.Messages.Count; i++)
            {
                if (state.Messages[i].IsRole(ChatRole.Tool))
                    toolIndices.Add(i);
            }

            // Keep the last N, elide older ones.
            var toElide = toolIndices.Count > keepLastN
                ? toolIndices.Take(toolIndices.Count - keepLastN).ToList()
                : [];

            foreach (var idx in toElide)
            {
                var msg = state.Messages[idx];
                var text = msg.GetText();

                if (text.Length > maxResultChars)
                {
                    // Replace with truncated version.
                    var summary = text[..maxResultChars] + "\n[...output elided...]";
                    var frc = msg.Contents.OfType<FunctionResultContent>().FirstOrDefault();
                    if (frc is not null)
                    {
                        msg.Contents.Clear();
                        msg.Contents.Add(new FunctionResultContent(frc.CallId ?? "", summary));
                    }
                }
            }

            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// LLM-backed summarizing condenser. Same shape as
    /// <see cref="MilestoneCheckpoint"/> but instead of a static
    /// "iteration N, X tool calls so far" line, runs the conversation
    /// through the LLM to produce a real summary that preserves
    /// decisions and current state. Lets the agent keep more useful
    /// context after a compaction at the cost of one extra LLM call.
    ///
    /// Falls back to a static summary if the LLM call errors — never
    /// blocks the loop. Both paths preserve the same orphan-cleanup
    /// rules around the kept-window boundary as MilestoneCheckpoint.
    /// </summary>
    public static MiddlewareFn LLMSummarizingCondenser(
        LlmSettings llm,
        int tokenThreshold = 30_000,
        int keepLastMessages = 5,
        string? sessionLogDir = null,
        string? sessionId = null)
    {
        var dir = ResolveSnapshotDir(sessionLogDir);
        var id = ResolveSnapshotId(sessionId, "condense");
        var checkpointSeq = 0;

        return async (state, ct) =>
        {
            var estimatedTokens = TokenEstimator.EstimateContext(
                state.Messages, state.LastRealInputTokens,
                state.LastRealInputMessageCount, llm.Model);
            if (estimatedTokens < tokenThreshold) return;

            checkpointSeq++;

            // ⭐ SNAPSHOT BEFORE CLEARING — and before the summarizer call, so
            // the history is on disk even if the LLM call hangs, throws, or the
            // whole run is cancelled mid-compaction. Writing it after the await
            // would lose exactly the runs that failed, which are the ones whose
            // transcript is worth the most.
            var snapshotPath = TrySnapshot(state.Messages, dir, id, checkpointSeq, "condense");
            var originalTask = CaptureOriginalTask(state);

            var systemMsg = state.Messages.FirstOrDefault(m => m.IsRole(ChatRole.System));
            var lastMessages = state.Messages.TakeLast(keepLastMessages).ToList();

            // Same orphan trim as MilestoneCheckpoint — drop a leading
            // Tool result whose matching assistant tool_call we just
            // dropped, or an Assistant-with-tool-calls whose results
            // we just truncated off the end. LLM APIs 400 on either.
            while (lastMessages.Count > 0)
            {
                var first = lastMessages[0];
                if (first.IsRole(ChatRole.Tool) || (first.IsRole(ChatRole.Assistant) && first.HasToolCalls()))
                {
                    lastMessages.RemoveAt(0);
                    continue;
                }
                break;
            }

            // Everything we're about to discard — that's what gets summarized.
            var toSummarize = state.Messages
                .Where(m => !m.IsRole(ChatRole.System))
                .Take(state.Messages.Count - lastMessages.Count - (systemMsg is not null ? 1 : 0))
                .ToList();

            var summary = await SummarizeAsync(
                llm, toSummarize, state.Iteration, estimatedTokens, snapshotPath,
                // The static fallback is built from the FULL message list, not
                // just the discarded slice: when the summarizer is unavailable
                // the mechanical digest is all the agent gets, so it should see
                // everything the snapshot saw.
                state.Messages, originalTask, ct);

            state.Messages.Clear();
            if (systemMsg is not null)
                state.Messages.Add(systemMsg);
            state.Messages.Add(Chat.User(summary));
            state.Messages.Add(Chat.Assistant("Understood. I'll continue from where I left off."));
            state.Messages.AddRange(lastMessages);
            InvalidateContextAnchor(state);
        };
    }

    private static async Task<string> SummarizeAsync(
        LlmSettings llm,
        List<ChatMessage> toSummarize,
        int iteration,
        int estimatedTokens,
        string? snapshotPath,
        List<ChatMessage> allMessages,
        string? originalTask,
        CancellationToken ct)
    {
        // Static fallback — used when the LLM call errors, i.e. exactly when
        // the endpoint is down and the run most needs to survive. It still
        // opens with "[Milestone checkpoint at iteration" so downstream
        // telemetry that grep'd for that string keeps matching, but the body is
        // now the mechanical digest rather than a bare token count: a fallback
        // that discards the transcript AND says nothing about it is the worst
        // outcome available at the worst moment.
        string staticFallback() =>
            BuildStaticDigest(allMessages, iteration, estimatedTokens, snapshotPath, originalTask);

        if (toSummarize.Count == 0) return staticFallback();

        var transcript = new System.Text.StringBuilder();
        foreach (var m in toSummarize)
        {
            var role = m.Role == ChatRole.Assistant ? "ASSISTANT" :
                       m.Role == ChatRole.Tool ? "TOOL" :
                       m.Role == ChatRole.User ? "USER" :
                       m.Role.ToString().ToUpperInvariant();
            var text = m.GetText();
            if (m.HasToolCalls() && string.IsNullOrEmpty(text))
                text = "(tool calls: " + string.Join(", ", m.GetToolCalls().Select(c => c.Name)) + ")";
            transcript.Append(role).Append(": ").Append(Trim(text, 1200)).Append('\n');
        }
        // Cap total transcript so the summarizer call itself doesn't
        // exceed reasonable budget — pulling 30K+ tokens through to
        // summarize 30K+ tokens defeats the purpose.
        const int transcriptCharCap = 24_000;
        var transcriptStr = transcript.ToString();
        if (transcriptStr.Length > transcriptCharCap)
            transcriptStr = transcriptStr[..transcriptCharCap] + "\n[…earlier turns truncated for summarizer]";

        const string condenseSystem =
            "You compress long agent-coding-session transcripts into compact running summaries. " +
            "Output ONE markdown block, ≤300 words, that preserves: the user's original ask, " +
            "key decisions made, files / functions / commands the agent has touched, current " +
            "state of the work, and any blockers / open questions. Skip pleasantries and " +
            "redundant restatements. Use past tense for completed steps, present for current state. " +
            "Do NOT include preamble like 'Here is the summary' — start directly with the content.";

        var req = new List<ChatMessage>
        {
            Chat.System(condenseSystem),
            Chat.User("Transcript to summarize:\n\n" + transcriptStr),
        };

        var opts = new ChatOptions { ModelId = llm.Model, Temperature = 0.0f };

        try
        {
            var resp = await llm.Client.GetResponseAsync(req, opts, ct);
            if (resp.Messages.Count == 0) return staticFallback();
            var body = resp.Messages[^1].GetText().Trim();
            if (string.IsNullOrEmpty(body)) return staticFallback();
            var reference = snapshotPath is not null
                ? $"\n\nFull prior transcript saved to {snapshotPath} (resumable) if you " +
                  "need a detail this summary omits."
                : "";
            // ⭐ The task line is OURS, not the model's. The condense prompt asks
            // for the original ask to be preserved, but "asked the model to keep
            // it" is not "the model kept it" — and once it is dropped from one
            // summary it is gone from every summary after. Stamping it here also
            // makes it machine-recoverable by ExtractOriginalTask.
            var taskLine = string.IsNullOrWhiteSpace(originalTask)
                ? "" : $"{OriginalTaskHeader}{Trim(originalTask!, 600)}\n\n";
            return $"[Earlier conversation summarized at iteration {iteration} " +
                   $"(was ~{estimatedTokens} tokens):]\n\n{taskLine}{body}{reference}";
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return staticFallback();
        }
    }

    /// <summary>
    /// Re-plan checkpoint — the HARD "detach from history, continue on a
    /// fresh plan" reset. Distinct from <see cref="LLMSummarizingCondenser"/>
    /// (which keeps the conversation going with a summary block inline): when
    /// estimated tokens cross <paramref name="tokenThreshold"/> this
    ///   1. persists the FULL current conversation to a resumable JSONL under
    ///      <paramref name="sessionLogDir"/> (same format <c>--resume</c>
    ///      reads) — the history is preserved as an on-disk artifact, not
    ///      discarded;
    ///   2. asks the model for a FORWARD-LOOKING handoff plan (goal, what's
    ///      done, what remains, next concrete steps, key files/decisions) —
    ///      as if briefing a fresh agent;
    ///   3. hard-resets the live context to system + that plan, dropping the
    ///      raw turn-by-turn history from the working window.
    /// This is what lets a very long autonomous run keep going indefinitely:
    /// the live context "drops to near nothing" at each checkpoint while the
    /// full trail stays on disk. Falls back to a static plan if the LLM call
    /// errors — never blocks the loop.
    /// </summary>
    public static MiddlewareFn ReplanCheckpoint(
        LlmSettings llm,
        int tokenThreshold = 100_000,
        string? sessionLogDir = null,
        string? sessionId = null)
    {
        // Shared with the other clearing strategies rather than open-coded.
        // This used to be its own copy that wrote FLAT into the session dir,
        // which is the directory vett-chat enumerates to build its session
        // picker — see ResolveSnapshotDir for why that matters and what it
        // looked like to a user.
        var dir = ResolveSnapshotDir(sessionLogDir);
        var id = ResolveSnapshotId(sessionId, "replan");
        var checkpointSeq = 0;

        return async (state, ct) =>
        {
            var estimatedTokens = TokenEstimator.EstimateContext(
                state.Messages, state.LastRealInputTokens,
                state.LastRealInputMessageCount, llm.Model);
            if (estimatedTokens < tokenThreshold) return;

            checkpointSeq++;

            // 1. Persist full history (best-effort — a write failure must not
            //    lose the run; we still reset to relieve context pressure).
            var logPath = TrySnapshot(state.Messages, dir, id, checkpointSeq, "checkpoint");
            var originalTask = CaptureOriginalTask(state);

            // 2. Forward-looking handoff plan.
            var systemMsg = state.Messages.FirstOrDefault(m => m.IsRole(ChatRole.System));
            var plan = await BuildHandoffPlanAsync(
                llm, state.Messages, state.Iteration, estimatedTokens, logPath, originalTask, ct);

            // 3. Hard reset to system + plan. No raw recent turns kept — the
            //    whole point is to detach; the plan carries forward what
            //    matters, and the JSONL carries the rest for reference.
            state.Messages.Clear();
            if (systemMsg is not null)
                state.Messages.Add(systemMsg);
            state.Messages.Add(Chat.User(plan));
            state.Messages.Add(Chat.Assistant("Understood. I'll continue from the plan above."));
            InvalidateContextAnchor(state);
        };
    }

    /// <summary>
    /// Write the user/assistant text turns of a conversation to a JSONL in
    /// the exact shape <see cref="Vett.Agent.HistorySeeder"/> reads, so the
    /// snapshot is directly resumable via <c>vett chat --resume &lt;path&gt;</c>.
    /// Tool calls / results are intentionally omitted — resume only replays
    /// user+assistant turns. Returns false on any IO failure.
    /// </summary>
    internal static bool TryPersistHistoryJsonl(List<ChatMessage> messages, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var sb = new System.Text.StringBuilder();
            var seq = 0;
            foreach (var m in messages)
            {
                string? type = m.IsRole(ChatRole.User) ? "user_message"
                    : m.IsRole(ChatRole.Assistant) ? "assistant_text"
                    : null;
                if (type is null) continue;
                var text = m.GetText();
                if (string.IsNullOrEmpty(text)) continue; // skip pure tool-call assistant turns
                var line = System.Text.Json.JsonSerializer.Serialize(new
                {
                    seq = seq++,
                    type,
                    text,
                });
                sb.Append(line).Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> BuildHandoffPlanAsync(
        LlmSettings llm,
        List<ChatMessage> messages,
        int iteration,
        int estimatedTokens,
        string? snapshotPath,
        string? originalTask,
        CancellationToken ct)
    {
        var reference = snapshotPath is not null
            ? $"\n\nFull prior transcript saved to {snapshotPath} (resumable) if you need a detail not in this plan."
            : "";

        // Replan discards the recent turns too — it is the hardest of the three
        // resets — so the task line matters most here. Machine-recoverable via
        // ExtractOriginalTask on the next checkpoint.
        var taskLine = string.IsNullOrWhiteSpace(originalTask)
            ? "" : $"\n\n{OriginalTaskHeader}{Trim(originalTask!, 600)}";

        string staticFallback() =>
            $"[Re-plan checkpoint at iteration {iteration}. The earlier conversation " +
            $"(~{estimatedTokens} tokens) was reset to relieve context. Continue the task " +
            $"from where you left off; re-read the workspace as needed to reconstruct state.]" +
            taskLine + reference;

        var toSummarize = messages.Where(m => !m.IsRole(ChatRole.System)).ToList();
        if (toSummarize.Count == 0) return staticFallback();

        var transcript = new System.Text.StringBuilder();
        foreach (var m in toSummarize)
        {
            var role = m.Role == ChatRole.Assistant ? "ASSISTANT" :
                       m.Role == ChatRole.Tool ? "TOOL" :
                       m.Role == ChatRole.User ? "USER" :
                       m.Role.ToString().ToUpperInvariant();
            var text = m.GetText();
            if (m.HasToolCalls() && string.IsNullOrEmpty(text))
                text = "(tool calls: " + string.Join(", ", m.GetToolCalls().Select(c => c.Name)) + ")";
            transcript.Append(role).Append(": ").Append(Trim(text, 1200)).Append('\n');
        }
        const int transcriptCharCap = 24_000;
        var transcriptStr = transcript.ToString();
        if (transcriptStr.Length > transcriptCharCap)
            transcriptStr = transcriptStr[..transcriptCharCap] + "\n[…earlier turns truncated for planner]";

        // Deliberately a PLAN prompt, not a "summarize" prompt: the output
        // is a briefing for a fresh agent that will continue with none of
        // this history in context.
        const string planSystem =
            "You are handing an in-progress coding task off to a FRESH agent that will " +
            "continue with NONE of the prior conversation in its context — only the plan " +
            "you write now. Produce a compact continuation brief (≤350 words) with these " +
            "sections:\n" +
            "GOAL: the user's original objective, verbatim intent.\n" +
            "DONE: what has already been accomplished (files created/edited, decisions made, " +
            "commands that succeeded).\n" +
            "STATE: current state of the work — what's in progress, what's known to work, " +
            "what's known broken.\n" +
            "NEXT: the concrete next steps, in order, the fresh agent should take.\n" +
            "FACTS: key paths, commands, names, gotchas it must not rediscover.\n" +
            "Be specific and actionable. Do NOT include preamble — start with 'GOAL:'.";

        var req = new List<ChatMessage>
        {
            Chat.System(planSystem),
            Chat.User("Conversation so far:\n\n" + transcriptStr),
        };
        var opts = new ChatOptions { ModelId = llm.Model, Temperature = 0.0f };

        try
        {
            var resp = await llm.Client.GetResponseAsync(req, opts, ct);
            if (resp.Messages.Count == 0) return staticFallback();
            var body = resp.Messages[^1].GetText().Trim();
            if (string.IsNullOrEmpty(body)) return staticFallback();
            return $"[Context was reset at iteration {iteration} (~{estimatedTokens} tokens). " +
                   $"Continue from this plan:]{taskLine}\n\n{body}{reference}";
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return staticFallback();
        }
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// ⛔ EVERY STRATEGY THAT REWRITES <c>state.Messages</c> MUST CALL THIS,
    /// AND MUST CALL IT AFTER THE REWRITE.
    ///
    /// <c>LastRealInputTokens</c> is the provider's exact size for a prompt
    /// that, once we have cleared history, NO LONGER EXISTS.
    /// <c>TokenEstimator.EstimateContext</c> anchors on that pair, so leaving
    /// it in place would report the PRE-compaction size on every subsequent
    /// iteration. The threshold would still be exceeded, so compaction would
    /// fire again immediately, and again after that — each pass eating another
    /// keep-window of the conversation while the number it is reacting to never
    /// moves. A self-sustaining shredder, driven entirely by a stale integer.
    ///
    /// Zeroing it degrades cleanly: EstimateContext falls back to the plain
    /// heuristic over the (now small) message list, and the anchor re-arms by
    /// itself on the next response.
    /// </summary>
    internal static void InvalidateContextAnchor(AgentState state)
    {
        state.LastRealInputTokens = 0;
        state.LastRealInputMessageCount = 0;
    }

    // ── Snapshot-before-discard, shared by every clearing strategy ───────────

    /// <summary>
    /// Where a compaction writes its history snapshot: a <c>compaction/</c>
    /// SUBDIRECTORY of the session log dir, never the session dir itself.
    ///
    /// ⛔ THE SUBDIRECTORY IS LOAD-BEARING, NOT TIDINESS. The session dir
    /// (<c>~/.vett/chat-sessions/</c> by default) is enumerated by the vett-chat
    /// extension to build its session launcher —
    /// <c>vett-chat/src/process/sessionIndex.ts:34</c> does
    /// <c>readdirSync(dir).filter(f =&gt; f.endsWith('.jsonl'))</c> with NO name
    /// check, and <c>ChatViewProvider.ts:69</c> fs.watches it for live updates.
    /// Writing snapshots flat into that directory would have made every
    /// compaction appear as a fake "session" in the user's picker.
    ///
    /// And it would have been worse than mere clutter, because of what a
    /// snapshot CONTAINS: its first user message is the original task, so
    /// <c>peekTitle</c> gives it a title nearly identical to the real session's,
    /// while its fresh mtime sorts it ABOVE that session. The launcher would
    /// show near-duplicate rows that a user cannot tell apart, and
    /// <c>deleteSession</c> would happily delete the one they picked. On a long
    /// Pro run — the exact case compaction exists for — one task would spray
    /// several of these.
    ///
    /// <c>readdirSync</c> is not recursive and a directory entry does not end in
    /// <c>.jsonl</c>, so a subdirectory is invisible to that scan while staying
    /// exactly as resumable: the path is reported to the agent and accepted by
    /// <c>--resume</c> verbatim.
    /// </summary>
    internal static string ResolveSnapshotDir(string? sessionLogDir)
    {
        var baseDir = string.IsNullOrWhiteSpace(sessionLogDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vett", "chat-sessions")
            : sessionLogDir;
        return Path.Combine(baseDir, "compaction");
    }

    /// <summary>
    /// One id per constructed middleware, so every snapshot from a single run
    /// shares a prefix and a counter disambiguates successive checkpoints.
    /// </summary>
    internal static string ResolveSnapshotId(string? sessionId, string prefix)
        => string.IsNullOrWhiteSpace(sessionId)
            ? prefix + "-" + Guid.NewGuid().ToString("N")[..8]
            : sessionId;

    /// <summary>
    /// ⭐ THE RULE THIS ENCODES: A STRATEGY THAT CLEARS THE CONVERSATION MUST
    /// WRITE IT DOWN FIRST.
    ///
    /// Until 2026-08-26 only <see cref="ReplanCheckpoint"/> did. The other two
    /// clearing strategies — <see cref="MilestoneCheckpoint"/> and
    /// <see cref="LLMSummarizingCondenser"/> — called
    /// <c>state.Messages.Clear()</c> with no on-disk record of what they were
    /// clearing. Everything older than <c>keep_last_messages</c> was gone, from
    /// process memory and from the world, with the only trace being whatever
    /// survived into a ≤300-word summary. On a long autonomous run that is the
    /// bulk of the work.
    ///
    /// Best-effort by design: a write failure must never abort the compaction,
    /// because failing to compact is how a run dies of context overflow. But it
    /// returns the path (or null) so the caller can TELL THE AGENT where its
    /// history went, rather than silently pretending it still has it.
    /// </summary>
    internal static string? TrySnapshot(
        List<ChatMessage> messages, string dir, string id, int seq, string strategy)
    {
        if (messages.Count == 0) return null;
        try
        {
            var path = Path.Combine(dir, $"{id}-{strategy}{seq}.jsonl");
            return TryPersistHistoryJsonl(messages, path) ? path : null;
        }
        catch
        {
            // Path.Combine can throw on an invalid configured dir. Same
            // contract as a failed write: no snapshot, but the run continues.
            return null;
        }
    }

    /// <summary>
    /// The prefixes of every summary block THIS FILE injects as a user turn.
    ///
    /// ⛔ ENUMERATED FROM THE INJECTION SITES, NOT REMEMBERED. All four are in
    /// this file: the milestone static summary, the condenser's static
    /// fallback, the condenser's LLM summary, and replan's two forms. A
    /// predicate over a guessed vocabulary silently misclassifies, and here the
    /// cost is specific: <see cref="BuildStaticDigest"/> uses this to find the
    /// user's ORIGINAL ask, and after the first compaction the first user
    /// message is one of OUR blocks. Get this list wrong and every later digest
    /// quotes a previous digest instead of the actual task — a summary that
    /// drifts a little further from the truth at every checkpoint.
    ///
    /// If you add or reword an injected block, add its prefix here.
    /// <c>CompactionMarkerCoverageTests</c> (tests/Vett.Tests/CompactionSnapshotTests.cs)
    /// fails if a strategy injects a block this list does not match. It RUNS
    /// each strategy and tests the string it actually emitted — a regex over
    /// this file would be a predicate over a guessed vocabulary and would pass
    /// while the behaviour drifted. Verified two-sided on 2026-08-26: deleting
    /// one entry here turns it red, and takes
    /// <c>OriginalTaskCaptureTests.EveryStrategyStampsARecoverableTaskLine</c>
    /// with it, which is the downstream damage made visible.
    /// </summary>
    internal static readonly string[] InjectedBlockPrefixes =
    [
        "[Milestone checkpoint at iteration",
        "[Earlier conversation summarized at iteration",
        "[Re-plan checkpoint at iteration",
        "[Context was reset at iteration",
    ];

    /// <summary>True if <paramref name="text"/> is a summary block we injected.</summary>
    internal static bool IsInjectedSummary(string? text)
        => !string.IsNullOrEmpty(text)
           && InjectedBlockPrefixes.Any(p => text!.StartsWith(p, StringComparison.Ordinal));

    /// <summary>The section header a digest writes the original ask under.
    /// Declared once because it is BOTH written and parsed back — a duplicated
    /// literal here would drift and the parse would silently start missing.</summary>
    private const string OriginalTaskHeader = "ORIGINAL TASK: ";

    /// <summary>
    /// ⛔ THE ORIGINAL ASK MUST BE CAPTURED ONCE, NOT RE-DERIVED PER CHECKPOINT.
    ///
    /// Re-deriving it works the first time and is wrong every time after. See
    /// <see cref="AgentState.OriginalTask"/> for the drift this prevents. Rungs,
    /// in order — earliest-known wins:
    ///   1. what we already captured this run;
    ///   2. the <c>ORIGINAL TASK:</c> line of a prior digest, when the earliest
    ///      user turn IS one of our blocks (the post-<c>--resume</c> case, where
    ///      the field is gone but the digest is still in the transcript);
    ///   3. the first real user turn — the only rung that existed before.
    /// </summary>
    internal static string? CaptureOriginalTask(AgentState state)
    {
        if (!string.IsNullOrWhiteSpace(state.OriginalTask)) return state.OriginalTask;

        var firstUser = state.Messages.FirstOrDefault(
            m => m.IsRole(ChatRole.User) && !string.IsNullOrWhiteSpace(m.GetText()));

        if (firstUser is not null && IsInjectedSummary(firstUser.GetText()))
        {
            var carried = ExtractOriginalTask(firstUser.GetText());
            if (!string.IsNullOrWhiteSpace(carried))
            {
                state.OriginalTask = carried;
                return carried;
            }
        }

        var real = state.Messages.FirstOrDefault(m =>
            m.IsRole(ChatRole.User)
            && !string.IsNullOrWhiteSpace(m.GetText())
            && !IsInjectedSummary(m.GetText()));

        if (real is not null) state.OriginalTask = Trim(real.GetText(), 600);
        return state.OriginalTask;
    }

    /// <summary>Pull the task back out of a digest block we wrote earlier.
    /// Sections in a digest are separated by a blank line, so the value runs to
    /// the next one.</summary>
    internal static string? ExtractOriginalTask(string? block)
    {
        if (string.IsNullOrEmpty(block)) return null;
        var start = block.IndexOf(OriginalTaskHeader, StringComparison.Ordinal);
        if (start < 0) return null;
        start += OriginalTaskHeader.Length;
        var end = block.IndexOf("\n\n", start, StringComparison.Ordinal);
        var value = (end < 0 ? block[start..] : block[start..end]).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Tool-call argument names that carry a workspace path.</summary>
    private static readonly string[] PathArgKeys =
        ["path", "file", "file_path", "filename", "old_path", "new_path"];

    /// <summary>
    /// A content-bearing summary built WITHOUT an LLM call.
    ///
    /// ⭐ WHY THIS EXISTS. The previous static summary was, in full:
    /// <c>"[Milestone checkpoint at iteration 12. 47 tool calls made so far.
    /// Context was 31000 tokens, reset to save space.]"</c> — a token count and
    /// nothing else. It named no file, no command, no decision, and not even
    /// the task. An agent handed that after a reset has been told only that it
    /// used to know things. It is the no-LLM strategy, so it is also the
    /// fallback whenever the endpoint is down or an <c>LlmSettings</c> was
    /// never threaded through — i.e. it runs precisely when nothing better can.
    ///
    /// Everything below is recovered from the message list itself: the original
    /// ask, which tools ran and how often, and which paths were touched. That
    /// is not as good as a real summary, and it does not pretend to be — but it
    /// is the difference between "you edited Foo.cs and Bar.cs, tests were run"
    /// and a bare integer.
    /// </summary>
    internal static string BuildStaticDigest(
        List<ChatMessage> messages, int iteration, int estimatedTokens, string? snapshotPath,
        string? originalTask = null)
    {
        var toolCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var paths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Supplied by CaptureOriginalTask on every real call site. The scan
        // below is the fallback for a caller that has no AgentState (and for
        // the very first compaction, where the two agree anyway).
        var originalAsk = string.IsNullOrWhiteSpace(originalTask) ? null : originalTask;

        foreach (var m in messages)
        {
            if (m.IsRole(ChatRole.User) && originalAsk is null)
            {
                var t = m.GetText();
                // Skip our own injected blocks — after the first compaction the
                // earliest user turn is a previous summary, not the real task.
                if (!string.IsNullOrWhiteSpace(t) && !IsInjectedSummary(t))
                    originalAsk = t;
            }

            if (!m.IsRole(ChatRole.Assistant) || !m.HasToolCalls()) continue;

            foreach (var tc in m.GetToolCalls())
            {
                toolCounts[tc.Name] = toolCounts.GetValueOrDefault(tc.Name) + 1;
                if (tc.Arguments is null) continue;
                foreach (var key in PathArgKeys)
                {
                    if (!tc.Arguments.TryGetValue(key, out var v) || v is null) continue;
                    var s = v.ToString();
                    // Cap length so a giant inlined blob in a mis-named arg
                    // can't blow up the digest we're building to SAVE space.
                    if (string.IsNullOrWhiteSpace(s) || s!.Length > 200) continue;
                    if (seenPaths.Add(s)) paths.Add(s);
                }
            }
        }

        var totalCalls = toolCounts.Values.Sum();
        var sb = new System.Text.StringBuilder();
        sb.Append("[Milestone checkpoint at iteration ").Append(iteration)
          .Append(". Context was ~").Append(estimatedTokens)
          .Append(" tokens; earlier turns were compacted. This digest was built ")
          .Append("mechanically from the transcript, not by a model.]\n");

        if (originalAsk is not null)
            sb.Append('\n').Append(OriginalTaskHeader).Append(Trim(originalAsk, 600)).Append('\n');

        if (totalCalls > 0)
        {
            sb.Append("\nTOOL USE (").Append(totalCalls).Append(" calls): ")
              .Append(string.Join(", ", toolCounts
                  .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                  .Take(12)
                  .Select(kv => $"{kv.Key}×{kv.Value}")))
              .Append('\n');
        }

        if (paths.Count > 0)
        {
            // Most recent first: what the agent touched last is the likeliest
            // thing it is still working on.
            var shown = paths.AsEnumerable().Reverse().Take(25).ToList();
            sb.Append("\nPATHS TOUCHED (").Append(paths.Count).Append(" distinct");
            if (paths.Count > shown.Count) sb.Append(", ").Append(shown.Count).Append(" most recent shown");
            sb.Append("):\n  ").Append(string.Join("\n  ", shown)).Append('\n');
        }

        if (snapshotPath is not null)
            sb.Append("\nFULL PRIOR TRANSCRIPT: ").Append(snapshotPath)
              .Append("\n  (resumable JSONL — read it if you need a detail this digest omits)\n");
        else
            sb.Append("\n⚠ The prior transcript could NOT be written to disk, so the detail ")
              .Append("above is all that survives this reset. Re-read the workspace to ")
              .Append("reconstruct anything you need.\n");

        return sb.ToString();
    }

    /// <summary>
    /// Milestone checkpoint: when estimated tokens exceed threshold,
    /// compress the conversation by keeping system + a progress summary
    /// + the last N messages.
    /// </summary>
    public static MiddlewareFn MilestoneCheckpoint(
        int tokenThreshold = 30_000,
        int keepLastMessages = 5,
        string? model = null,
        string? sessionLogDir = null,
        string? sessionId = null)
    {
        var dir = ResolveSnapshotDir(sessionLogDir);
        var id = ResolveSnapshotId(sessionId, "milestone");
        var checkpointSeq = 0;

        return (state, ct) =>
        {
            var estimatedTokens = Vett.Llm.TokenEstimator.EstimateContext(
                state.Messages, state.LastRealInputTokens,
                state.LastRealInputMessageCount, model);

            if (estimatedTokens < tokenThreshold)
                return Task.CompletedTask;

            checkpointSeq++;

            // ⭐ SNAPSHOT BEFORE CLEARING. Everything below drops all but the
            // last N messages; this is the only record that the rest existed.
            var snapshotPath = TrySnapshot(state.Messages, dir, id, checkpointSeq, "milestone");

            // Captured BEFORE the clear, and only ever on the first checkpoint —
            // after that the earliest surviving user turn is no longer the task.
            var originalTask = CaptureOriginalTask(state);

            var summary = BuildStaticDigest(
                state.Messages, state.Iteration, estimatedTokens, snapshotPath, originalTask);

            // Keep system message + summary + last N messages.
            var systemMsg = state.Messages.FirstOrDefault(m => m.IsRole(ChatRole.System));
            var lastMessages = state.Messages.TakeLast(keepLastMessages).ToList();

            // Drop leading orphans so the kept window starts at a clean turn
            // boundary. If lastMessages begins with a Tool result whose matching
            // assistant tool_call was just dropped, the LLM API will 400 on the
            // orphaned tool_call_id. Same logic for an Assistant-with-tool-calls
            // whose tool_results we just truncated off the end.
            while (lastMessages.Count > 0)
            {
                var first = lastMessages[0];
                if (first.IsRole(ChatRole.Tool) || (first.IsRole(ChatRole.Assistant) && first.HasToolCalls()))
                {
                    lastMessages.RemoveAt(0);
                    continue;
                }
                break;
            }

            state.Messages.Clear();

            if (systemMsg is not null)
                state.Messages.Add(systemMsg);

            state.Messages.Add(Chat.User(summary));
            state.Messages.Add(Chat.Assistant("Understood. I'll continue from where I left off."));
            state.Messages.AddRange(lastMessages);
            InvalidateContextAnchor(state);

            return Task.CompletedTask;
        };
    }
}
