using System.Diagnostics;

namespace Vett.Tests;

/// <summary>
/// WAITING FOR A CONDITION, NOT FOR A DURATION.
///
/// ⛔ THE DEFECT THIS REPLACES (2026-08-27). Six tests synchronised with a
/// bare <c>await Task.Delay(N)</c> and then asserted on state that a
/// background task had to reach first — e.g.
/// <c>await Task.Delay(100); Assert.Equal(Completed, task.Status);</c>.
/// That is a sleep used as a synchronisation primitive: it encodes a guess
/// about how fast this machine is, and the guess is made once, by hand, at
/// the time of writing.
///
/// Measured cost: over 20 consecutive full-suite runs the suite was red on
/// 4 of them (20%), across three different tests, every one of them a
/// wall-clock assumption that held when the class ran alone and broke when
/// 1221 tests competed for the same cores. None of the failures was a
/// product defect. The signal-to-noise of the whole suite was set by the
/// slowest machine it ever ran on.
///
/// ⭐ WHY A LONG TIMEOUT IS NOT "RAISING THE CAP TO GET A GREEN". The two
/// things look alike and are opposites:
///
///   - Raising a cap that IS the claim launders a regression. If a test
///     asserts "this completes within 100ms", moving it to 30s deletes the
///     assertion.
///   - These tests never claimed latency. Their subject is "does the
///     background work reach this state at all" — the delay was incidental
///     plumbing that silently became a latency assertion nobody intended.
///
/// So the timeout here is a HANG DETECTOR, not a budget. It must be long
/// enough that only a genuine never-completes trips it. The power of the
/// assertion is unchanged: if the state is never reached, this still fails
/// — it just takes longer to say so, and it says WHY.
///
/// The old form also failed with no diagnostic content whatsoever
/// (<c>Assert.True() Failure / Expected: True / Actual: False</c>). Every
/// failure here names what was awaited and how long it actually waited.
/// </summary>
internal static class TestWait
{
    /// <summary>
    /// Default hang-detection budget. Deliberately generous: the suite runs
    /// 1221 tests in parallel and a process spawn under that load has been
    /// measured past 5s. Tests that genuinely hang cost this once.
    /// </summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>
    /// Poll <paramref name="condition"/> until it is true, or fail with a
    /// message naming <paramref name="what"/> and the elapsed time.
    /// </summary>
    /// <param name="what">
    /// What is being waited for, phrased so it reads in a failure message:
    /// "the background task to reach Completed".
    /// </param>
    public static async Task UntilAsync(
        Func<bool> condition,
        string what,
        int timeoutSeconds = DefaultTimeoutSeconds,
        Func<string>? diagnose = null)
    {
        var sw = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(timeoutSeconds);
        var polls = 0;

        while (sw.Elapsed < deadline)
        {
            polls++;
            // A condition that throws while the background task is mid-write
            // is treated as "not yet", never as a pass.
            try { if (condition()) return; }
            catch { /* not yet — keep polling */ }
            await Task.Delay(10);
        }

        // One last attempt, so a condition that became true during the final
        // sleep is not reported as a timeout.
        try { if (condition()) return; }
        catch (Exception ex)
        {
            throw new TimeoutException(
                $"Timed out after {sw.Elapsed.TotalSeconds:F1}s ({polls} polls) waiting for {what}; " +
                $"the condition threw on its final evaluation: {ex.GetType().Name}: {ex.Message}" +
                Diagnostic(diagnose));
        }

        throw new TimeoutException(
            $"Timed out after {sw.Elapsed.TotalSeconds:F1}s ({polls} polls) waiting for {what}. " +
            $"This budget is a HANG DETECTOR, not a latency assertion — reaching it means the " +
            $"state was never reached at all, not that the machine was slow." +
            Diagnostic(diagnose));
    }

    /// <summary>Convenience for the common "a collection reached N items" wait.</summary>
    public static Task UntilCountAsync<T>(
        IReadOnlyCollection<T> items,
        int atLeast,
        string what,
        int timeoutSeconds = DefaultTimeoutSeconds)
        => UntilAsync(
            () => items.Count >= atLeast,
            what,
            timeoutSeconds,
            diagnose: () => $"last observed count: {items.Count} (wanted >= {atLeast})");

    private static string Diagnostic(Func<string>? diagnose)
    {
        if (diagnose is null) return "";
        try { return " | " + diagnose(); }
        catch (Exception ex) { return $" | (diagnostic threw: {ex.Message})"; }
    }
}
