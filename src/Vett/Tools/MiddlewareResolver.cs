using Vett.Agent;
using Vett.Config;

namespace Vett.Tools;

/// <summary>
/// Resolves middleware names from profile YAML to actual MiddlewareFn functions.
/// No registry — just a dictionary lookup built at startup.
///
/// LLM-backed middleware (<c>agent_finished_critic</c>,
/// <c>llm_summarizing_condenser</c>, <c>replan_checkpoint</c>) are only
/// registered when the caller threads <see cref="LlmSettings"/> through.
/// Chat does (so users can list these in their profile YAML and they'll
/// fire); benchmarks deliberately don't pass <c>llm</c> — extra LLM calls
/// per instance don't make sense at scale and the benchmark grader is the
/// authoritative judge.
///
/// ⚠ 2026-08-26 — that policy was written for the CRITIC and then silently
/// applied to COMPACTION, which is a different kind of thing. Skipping a
/// second-opinion critic costs a benchmark nothing; skipping compaction
/// costs it the run, because the context grows until the provider rejects
/// it. An earlier revision of this doc described the no-op as uniformly
/// intentional; it was not, and the sentence is corrected here rather than
/// left to be read as current. A declared COMPACTION strategy now degrades
/// to <c>milestone_checkpoint</c> (no LLM required) instead of vanishing,
/// and every substitution or skip is reported through
/// <c>onDiagnostic</c>. The critic's exclusion is unchanged and remains
/// deliberate.
/// </summary>
public static class MiddlewareResolver
{
    /// <summary>
    /// Resolve a list of middleware names to functions. Builtins are checked
    /// first, then any plugin middleware passed in via <paramref name="extras"/>.
    /// LLM-backed middleware register only when <paramref name="llm"/> is non-null;
    /// a compaction strategy that can't register that way DEGRADES to
    /// <c>milestone_checkpoint</c> rather than disappearing. Unknown names are
    /// still non-fatal — they could be plugin middleware from a workspace that
    /// hasn't been loaded by this caller — but they are no longer invisible.
    ///
    /// <paramref name="compaction"/> tunes the threshold / keep-window /
    /// elision length of the compaction middleware. Null → shipped defaults
    /// (30k / 5 / 200), i.e. pre-config behavior. Passing a block is how a
    /// large-context profile (e.g. deepseek-chat 128k) raises the trigger so
    /// it doesn't compact at 30k.
    ///
    /// <paramref name="onDiagnostic"/> receives one message per name that did
    /// not resolve to exactly what the profile asked for. Callers with a
    /// console should print these; tests assert on them. Null = discard, which
    /// is the old silent behaviour and should be a deliberate choice.
    /// </summary>
    public static List<MiddlewareFn> Resolve(
        List<string> names,
        Dictionary<string, MiddlewareFn>? extras = null,
        LlmSettings? llm = null,
        CompactionConfig? compaction = null,
        Action<string>? onDiagnostic = null)
    {
        var result = new List<MiddlewareFn>();

        // Build the lookup fresh each call so the compaction closures bind
        // this session's thresholds and the LLM-backed entries close over
        // the active llm. Constructing them at module-init time would pin a
        // single client / a single threshold across all sessions.
        var c = compaction ?? new CompactionConfig();
        var map = new Dictionary<string, MiddlewareFn>(StringComparer.OrdinalIgnoreCase)
        {
            ["submit_detector"] = BuiltinMiddleware.SubmitDetector,
            ["output_truncation"] = BuiltinMiddleware.OutputTruncation,
            ["stuck_detector"] = BuiltinMiddleware.StuckDetector,
            ["observation_elision"] = CompactionMiddleware.ObservationElision(c.ElisionKeepLast, c.ElisionMaxChars),
            // session_log_dir is threaded into every CLEARING strategy, not
            // just replan: each one now snapshots the history it discards, and
            // a profile that configures where replan writes plainly means the
            // same directory for the others.
            ["milestone_checkpoint"] = CompactionMiddleware.MilestoneCheckpoint(
                c.ThresholdTokens, c.KeepLastMessages, llm?.Model, c.SessionLogDir),
        };
        if (llm is not null)
        {
            map["agent_finished_critic"] = CriticMiddleware.AgentFinishedCritic(llm);
            map["llm_summarizing_condenser"] = CompactionMiddleware.LLMSummarizingCondenser(
                llm, c.ThresholdTokens, c.KeepLastMessages, c.SessionLogDir);
            // Hard "detach + re-plan" reset. Uses its OWN threshold
            // (replan_threshold_tokens) so a profile can run the inline
            // condenser for routine relief AND fall back to a plan-based
            // reset at a higher watermark. Threshold 0 → registered but
            // inert (never fires), so listing it costs nothing.
            map["replan_checkpoint"] = CompactionMiddleware.ReplanCheckpoint(
                llm,
                c.ReplanThresholdTokens > 0 ? c.ReplanThresholdTokens : int.MaxValue,
                c.SessionLogDir);
        }

        foreach (var name in names)
        {
            if (map.TryGetValue(name, out var fn))
            {
                result.Add(fn);
                continue;
            }
            if (extras is not null && extras.TryGetValue(name, out var pluginFn))
            {
                result.Add(pluginFn);
                continue;
            }

            // ⛔ A DECLARED COMPACTION STRATEGY MUST NEVER RESOLVE TO NOTHING.
            //
            // Until 2026-08-26 it did, silently. `llm_summarizing_condenser`
            // and `replan_checkpoint` are registered above ONLY when an
            // LlmSettings was threaded in, and two of the five call sites in
            // this codebase don't thread one (Runner.cs, ReplayCommand.cs — and
            // Harness.cs passes compaction but not llm). On those paths the
            // name fell through to the silent-skip below, so a profile that
            // declared a condenser and a carefully tuned threshold_tokens ran
            // with NO COMPACTION AT ALL. Config that is read, parsed, and then
            // dropped reads exactly like config that works — right up until a
            // long run dies of context overflow, which is the one failure the
            // setting existed to prevent.
            //
            // The fix is to DEGRADE, not to skip: milestone_checkpoint needs no
            // LLM, and since 2026-08-26 it snapshots the discarded history to a
            // resumable JSONL and emits a mechanical digest. It is worse than a
            // model-written summary and much better than nothing. The
            // substitution is reported so it can be logged and asserted on,
            // rather than being a second silent behaviour replacing the first.
            if (string.Equals(name, "llm_summarizing_condenser", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(CompactionMiddleware.MilestoneCheckpoint(
                    c.ThresholdTokens, c.KeepLastMessages, llm?.Model, c.SessionLogDir));
                onDiagnostic?.Invoke(
                    $"'{name}' requires an LlmSettings, which this call site did not provide; " +
                    $"substituted 'milestone_checkpoint' at the same threshold ({c.ThresholdTokens} " +
                    "tokens) so compaction still occurs. The digest will be mechanical, not model-written.");
                continue;
            }

            if (string.Equals(name, "replan_checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                // Only substitute when replan would actually have been ACTIVE.
                // Its threshold defaults to 0 = disabled, so listing it costs
                // nothing by design; standing in for a middleware that was
                // never going to fire would ADD compaction the profile did not
                // ask for.
                if (c.ReplanThresholdTokens > 0)
                {
                    result.Add(CompactionMiddleware.MilestoneCheckpoint(
                        c.ReplanThresholdTokens, c.KeepLastMessages, llm?.Model, c.SessionLogDir));
                    onDiagnostic?.Invoke(
                        $"'{name}' requires an LlmSettings, which this call site did not provide; " +
                        $"substituted 'milestone_checkpoint' at the replan threshold " +
                        $"({c.ReplanThresholdTokens} tokens). The hard plan-based reset is not " +
                        "available without an LLM; the history snapshot still is.");
                }
                else
                {
                    onDiagnostic?.Invoke(
                        $"'{name}' is listed but inert (replan_threshold_tokens = 0) and no " +
                        "LlmSettings was provided; nothing was registered for it.");
                }
                continue;
            }

            if (string.Equals(name, "agent_finished_critic", StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately NOT substituted: there is no non-LLM critic, and
                // its omission on benchmark paths is an intentional policy (see
                // the class doc) rather than the accident the condenser's was.
                onDiagnostic?.Invoke(
                    $"'{name}' requires an LlmSettings and was not registered. This is expected on " +
                    "benchmark paths, which deliberately omit it.");
                continue;
            }

            // Genuinely unknown — could be plugin middleware from a workspace
            // this caller hasn't loaded, so it stays non-fatal, but it is no
            // longer invisible.
            onDiagnostic?.Invoke($"unknown middleware '{name}' was not registered by this caller.");
        }

        // submit_detector is NOT optional, so it is forced rather than looked
        // up. `finish` and `declare_done` signal completion by returning a
        // __VETT_SUBMIT__ marker, and SubmitDetector is the ONLY reader of that
        // marker in the codebase. A profile that declares a non-empty
        // middleware list without naming it turns the completion signal into an
        // inert string in the transcript: the agent submits correctly, nothing
        // sets StopLoop, and it keeps working past its own submission until
        // max_iterations or the timeout kills it. ResolveOrDefault's fallback
        // does not cover this -- that only fires on an EMPTY list.
        //
        // This shipped, and it was the common case rather than an edge: at the
        // commit that fixed it, 42 of the 46 non-empty agent chains in
        // profiles/ omitted the name (83 of 87 before that same commit retired
        // the dead profiles), leaders and members alike. Counted by loading
        // each profile and walking profile-level + per-member middleware lists;
        // empty lists are excluded because those already resolve through
        // ResolveOrDefault's fallback.
        //
        // (An earlier revision of this comment and the 922ca29 commit message
        // both said "48 of 55". That number is not reproducible from any tree
        // state and was wrong; the commit message stands as a record of what
        // was written at the time, but do not cite it.)
        //
        // The count is context, not the guarantee. The guarantee is that this
        // is enforced in CODE, so it holds for every profile in all three
        // resolution stores — repo, ~/.vett, and the install-dir snapshot —
        // including ones authored later and ones this repo never sees.
        // Observed symptom: a leader that declared done, kept looping, got
        // handed a stuck-nudge, and re-dispatched byte-identical work.
        //
        // Prepended, not appended, so agent_finished_critic -- which
        // deliberately un-stops a finish it disagrees with -- still observes
        // StopLoop and runs after it. Appending would silently disable that
        // override. Guarded on the name so a profile that DOES list
        // submit_detector keeps its chosen ordering.
        if (!names.Any(n => string.Equals(n, "submit_detector", StringComparison.OrdinalIgnoreCase)))
            result.Insert(0, BuiltinMiddleware.SubmitDetector);

        return result;
    }

    /// <summary>
    /// Resolve middleware names, falling back to default if the list is empty.
    /// </summary>
    public static List<MiddlewareFn> ResolveOrDefault(
        List<string> names,
        Dictionary<string, MiddlewareFn>? extras = null,
        LlmSettings? llm = null,
        CompactionConfig? compaction = null,
        Action<string>? onDiagnostic = null)
    {
        if (names.Count == 0)
            return Builtins.DefaultMiddleware();

        return Resolve(names, extras, llm, compaction, onDiagnostic);
    }
}
