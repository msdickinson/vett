namespace Vett.Agent;

/// <summary>
/// One-shot signal that a runner can use to ask the agent loop to
/// compact the conversation history at the next iteration boundary,
/// regardless of the auto-threshold. Sibling to <see cref="TurnInterrupt"/>
/// — both are how interactive runners (e.g. <c>vett chat --stdio</c>)
/// inject control signals into the otherwise-LLM-driven loop.
///
/// Single-flag implementation: a /compact command followed by the user
/// hitting Enter again before the loop wakes up is fine — both pending
/// requests collapse into one compaction. We don't need a queue.
/// </summary>
public sealed class CompactRequest
{
    private int _pending;

    /// <summary>
    /// How many recent messages the FORCED compaction keeps verbatim —
    /// i.e. the profile's <c>compaction.keep_last_messages</c>.
    ///
    /// ⛔ WHY THIS IS HERE AT ALL. Until 2026-08-26 the forced path called
    /// <c>MilestoneCheckpoint(tokenThreshold: 0, model: llm.Model)</c> and
    /// passed nothing else, so it silently took the METHOD DEFAULTS (keep 5,
    /// snapshot to <c>~/.vett/chat-sessions/</c>) no matter what the profile
    /// said. A profile that tuned <c>keep_last_messages</c> got its value
    /// honoured by every automatic checkpoint and ignored by the one
    /// compaction the user explicitly asked for — the two paths quietly
    /// disagreed, and the explicit one lost. Same class as the resolver's own
    /// warning: config that is read, parsed, and then dropped reads exactly
    /// like config that works.
    ///
    /// Found by a live probe (scratchpad/live-2026-08-26): a profile setting
    /// keep_last_messages: 4 produced a forced compaction that kept 5.
    ///
    /// Defaults to 5 to match <see cref="Vett.Config.CompactionConfig"/>, so a
    /// caller that doesn't set it is no worse off than before this field
    /// existed. That also means an unset caller is INVISIBLE rather than
    /// broken — hence the test that asserts the value actually reaches the
    /// checkpoint, instead of trusting that every construction site remembers.
    /// </summary>
    public int KeepLastMessages { get; init; } = 5;

    /// <summary>
    /// Where the forced compaction writes its pre-clear history snapshot —
    /// the profile's <c>compaction.session_log_dir</c>. Null → the shared
    /// default. Threaded for the same reason as
    /// <see cref="KeepLastMessages"/>: the snapshot is the ONLY record that
    /// the discarded turns existed, and writing it to a different directory
    /// than every other strategy in the same session is how a safety artifact
    /// becomes unfindable at the moment it's needed.
    /// </summary>
    public string? SessionLogDir { get; init; }

    /// <summary>Mark that the user wants a compaction next time the
    /// loop reaches an iteration boundary.</summary>
    public void Request() => Interlocked.Exchange(ref _pending, 1);

    /// <summary>Atomically claim and clear the pending flag. Returns
    /// true exactly once per <see cref="Request"/> call.</summary>
    public bool TakePending() => Interlocked.Exchange(ref _pending, 0) != 0;
}
