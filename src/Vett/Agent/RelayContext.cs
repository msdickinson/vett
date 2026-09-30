using Vett.Config;

namespace Vett.Agent;

/// <summary>
/// R1 (relay) decision logic, pure and unit-testable: how full is the
/// context REALLY, and is it time to tell the agent to wrap up?
///
/// The input is the per-response <c>Usage.InputTokenCount</c> — the size
/// of the entire prompt that was just sent — which is the honest "how
/// full am I" number. TokenEstimator drifts (it feeds compaction, where
/// approximate is fine); iteration count is a bad proxy (task #48 burned
/// 134 iterations on one file, tier-1 smokes finish in 3). The relay
/// threshold is about exiting SHARP, measured not guessed.
/// </summary>
public static class RelayContext
{
    /// <summary>Whole-percent context fullness; 0 when disabled or when
    /// the provider reported no usage (never divide by zero, never nudge
    /// on missing data).</summary>
    public static int Pct(int inputTokens, int contextWindow)
        => contextWindow > 0 && inputTokens > 0
            ? (int)(100L * inputTokens / contextWindow)
            : 0;

    /// <summary>True when a wrap-up nudge should be injected: at/past the
    /// threshold AND at least 5 points past the last nudge (0 = never
    /// nudged). The +5 re-nudge cadence means a single warning cannot be
    /// silently buried by compaction or ignored into oblivion, without
    /// spamming every single response.</summary>
    public static bool ShouldNudge(int pct, int thresholdPct, int lastNudgedPct)
        => pct >= thresholdPct && pct >= lastNudgedPct + 5;

    /// <summary>The injected instruction. Deliberately concrete: commit,
    /// write the handoff, end the run — and the HANDOFF token contract
    /// the supervisor greps for (lane R2 convention).</summary>
    public static string NudgeText(int pct, RelayConfig cfg)
        => $"[context-status] Your context is at {pct}% of the " +
           $"{cfg.ContextWindow:N0}-token window (handoff threshold " +
           $"{cfg.HandoffThresholdPct}%). Wrap up NOW: finish or reject the " +
           "dispatch in flight, commit anything already landed and verified, " +
           "write your handoff/inflight notes, then end the run — " +
           "declare_done with HANDOFF as the FIRST word of the summary if " +
           "work remains for the next generation. Do NOT start new work.";
}
