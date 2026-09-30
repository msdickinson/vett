using System.Runtime.InteropServices;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// Covers BackgroundBashService against real `bash` / `cmd` so we
/// exercise the actual Process plumbing. Tests favor short-lived
/// commands so the suite stays under a second per case.
///
/// Cross-platform: each case picks a command shape that works on
/// Windows (cmd via DirectBash's git-bash fallback resolves to bash
/// when present; otherwise cmd /c — but we don't rely on cmd in tests
/// because echo + sleep have different syntax across the two).
/// </summary>
public class BackgroundBashServiceTests
{
    private string Tmp;

    public BackgroundBashServiceTests()
    {
        Tmp = Path.Combine(Path.GetTempPath(), "vett-bg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Tmp);
    }

    [Fact]
    public async Task Start_ShortCommand_RunsToCompletion()
    {
        using var svc = new BackgroundBashService(Tmp);
        var (jobId, _) = svc.Start("echo hello-bg");

        // Poll until complete — short commands finish in <100ms but the
        // exit watcher fires asynchronously.
        var result = await PollUntilCompleteAsync(svc, jobId, CompletionBudget);
        Assert.NotNull(result);
        Assert.True(result!.Complete);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("hello-bg"));
    }

    [Fact]
    public void Start_EmptyCommand_Throws()
    {
        using var svc = new BackgroundBashService(Tmp);
        Assert.Throws<ArgumentException>(() => svc.Start(""));
        Assert.Throws<ArgumentException>(() => svc.Start("   "));
    }

    [Fact]
    public async Task Start_CustomName_IsUsedInListJobs()
    {
        using var svc = new BackgroundBashService(Tmp);
        var (jobId, name) = svc.Start("echo named", "build watcher");
        Assert.Equal("build watcher", name);

        // Wait for completion so the test isn't racy.
        await PollUntilCompleteAsync(svc, jobId, CompletionBudget);
        var jobs = svc.ListJobs();
        var entry = jobs.Single(j => j.Id == jobId);
        Assert.Equal("build watcher", entry.Name);
    }

    [Fact]
    public async Task GetOutput_SinceLine_ReturnsOnlyNewLines()
    {
        using var svc = new BackgroundBashService(Tmp);
        // Three separate echoes via shell chaining — produces three
        // output lines.
        var (jobId, _) = svc.Start("echo line-1; echo line-2; echo line-3");
        await PollUntilCompleteAsync(svc, jobId, CompletionBudget);

        var first = svc.GetOutput(jobId, 0);
        Assert.NotNull(first);
        Assert.True(first!.TotalLines >= 3);

        // Re-read with sinceLine = TotalLines → should return empty.
        var second = svc.GetOutput(jobId, first.TotalLines);
        Assert.NotNull(second);
        Assert.Empty(second!.Lines);
        Assert.Equal(first.TotalLines, second.TotalLines);
    }

    [Fact]
    public void GetOutput_UnknownJobId_ReturnsNull()
    {
        using var svc = new BackgroundBashService(Tmp);
        Assert.Null(svc.GetOutput("does-not-exist"));
    }

    [Fact]
    public void Kill_UnknownJobId_ReturnsFalse()
    {
        using var svc = new BackgroundBashService(Tmp);
        Assert.False(svc.Kill("does-not-exist"));
    }

    [Fact]
    public async Task Kill_RunningJob_TerminatesIt()
    {
        // Skip on Windows-without-git-bash where the sleep semantics differ;
        // git-bash + Linux both honor `sleep`.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !File.Exists(@"C:\Program Files\Git\bin\bash.exe"))
            return;

        using var svc = new BackgroundBashService(Tmp);
        var (jobId, _) = svc.Start("sleep 30");
        // Give the process a moment to spin up.
        await Task.Delay(100);
        Assert.True(svc.Kill(jobId));
        // Wait for kill to take effect.
        var result = await PollUntilCompleteAsync(svc, jobId, CompletionBudget);
        Assert.NotNull(result);
        Assert.True(result!.Complete);
        // Killed processes return non-zero exit codes — exact value is OS-dependent.
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ListJobs_ReturnsMostRecentFirst()
    {
        using var svc = new BackgroundBashService(Tmp);
        var (id1, _) = svc.Start("echo first");
        await Task.Delay(50);
        var (id2, _) = svc.Start("echo second");

        var jobs = svc.ListJobs();
        Assert.Equal(2, jobs.Count);
        // Most recent first ordering.
        Assert.Equal(id2, jobs[0].Id);
        Assert.Equal(id1, jobs[1].Id);
    }

    [Fact]
    public void Dispose_KillsAllRunningJobs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !File.Exists(@"C:\Program Files\Git\bin\bash.exe"))
            return;

        var svc = new BackgroundBashService(Tmp);
        var (jobId, _) = svc.Start("sleep 30");
        // Start tracked.
        Assert.NotNull(svc.GetOutput(jobId));
        svc.Dispose();
        // After dispose, the jobs map is cleared.
        Assert.Null(svc.GetOutput(jobId));
    }

    [Fact]
    public void BackgroundJob_AppendLine_BelowCap_KeepsAllLinesNoSentinel()
    {
        var job = NewJob();
        for (int i = 0; i < 100; i++) job.AppendLine($"line-{i}");

        Assert.Equal(100, job.LineCount);
        Assert.Equal(100, job.TotalEmitted);
        var snap = job.SnapshotLines();
        Assert.Equal("line-0", snap[0]);
        Assert.Equal("line-99", snap[^1]);
        Assert.DoesNotContain(snap, l => l.Contains("output truncated"));
    }

    [Fact]
    public void BackgroundJob_AppendLine_AtExactlyMaxLines_StillNoSentinel()
    {
        var job = NewJob();
        for (int i = 0; i < BackgroundJob.MaxLines; i++) job.AppendLine($"L{i}");

        // Cap is `> MaxLines`, so MaxLines exactly is the no-truncation boundary.
        Assert.Equal(BackgroundJob.MaxLines, job.LineCount);
        Assert.Equal(BackgroundJob.MaxLines, job.TotalEmitted);
        var snap = job.SnapshotLines();
        Assert.Equal("L0", snap[0]);
        Assert.DoesNotContain(snap, l => l.Contains("output truncated"));
    }

    [Fact]
    public void BackgroundJob_AppendLine_FirstOverflow_DropsOldestAndInsertsSentinel()
    {
        var job = NewJob();
        // Emit MaxLines + 1 lines — exactly one overflow round.
        for (int i = 0; i < BackgroundJob.MaxLines + 1; i++) job.AppendLine($"L{i}");

        Assert.Equal(BackgroundJob.MaxLines, job.LineCount);
        Assert.Equal(BackgroundJob.MaxLines + 1, job.TotalEmitted);

        var snap = job.SnapshotLines();
        // Sentinel sits at the head, replacing what would have been L1 after RemoveRange.
        Assert.Equal("[output truncated — earlier lines dropped to bound memory]", snap[0]);
        // Tail is the newest line.
        Assert.Equal($"L{BackgroundJob.MaxLines}", snap[^1]);
    }

    [Fact]
    public void BackgroundJob_AppendLine_ManyOverflows_CapHoldsAndTotalEmittedAccumulates()
    {
        var job = NewJob();
        const int total = BackgroundJob.MaxLines + 500;
        for (int i = 0; i < total; i++) job.AppendLine($"L{i}");

        // Cap invariant — LineCount must NEVER exceed MaxLines no matter how many overflows.
        Assert.Equal(BackgroundJob.MaxLines, job.LineCount);
        // Cumulative axis tracks every appended line, including dropped ones.
        Assert.Equal(total, job.TotalEmitted);
        // The most-recent line is preserved at the tail.
        var snap = job.SnapshotLines();
        Assert.Equal($"L{total - 1}", snap[^1]);
    }

    /// <summary>Poll GetOutput until the job's `complete` flag flips true,
    /// or `timeout` elapses. Returns the last MonitorResult observed (or null
    /// if the job id is unknown — caller can assert in that case).</summary>
    /// <summary>
    /// Poll until the job reports Complete.
    ///
    /// ⛔ 2026-08-27: THIS USED TO RETURN AN INCOMPLETE RESULT ON TIMEOUT,
    /// which turned every caller's `Assert.True(result.Complete)` into a
    /// latency assertion with no diagnostic content — the observed failure
    /// read, in its entirety, "Expected: True / Actual: False". It fired on
    /// 2 of 20 full-suite runs: `echo hello-bg` genuinely takes under 100ms,
    /// but spawning git-bash while 1221 tests compete for cores does not,
    /// and the budget was 5s.
    ///
    /// Two changes. The budget is now a HANG DETECTOR (30s) rather than a
    /// guess at machine speed — this test's subject is "does the exit
    /// watcher fire and capture output", never "how fast". And a timeout now
    /// THROWS with the elapsed time and last-seen state instead of returning
    /// a value that fails an opaque assert somewhere else.
    ///
    /// The null return is preserved and still distinct: null means the job
    /// id was not found at all, which is a different fact from "found but
    /// never completed" and some callers assert on it.
    /// </summary>
    private static async Task<MonitorResult?> PollUntilCompleteAsync(
        BackgroundBashService svc, string jobId, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MonitorResult? last = null;
        while (sw.Elapsed < timeout)
        {
            last = svc.GetOutput(jobId);
            if (last is null) return null;          // job id unknown — a real, distinct outcome
            if (last.Complete) return last;
            await Task.Delay(50);
        }

        last = svc.GetOutput(jobId);
        if (last is null) return null;
        if (last.Complete) return last;

        throw new TimeoutException(
            $"Background job '{jobId}' never reported Complete within {timeout.TotalSeconds:F0}s. " +
            $"This budget is a hang detector, not a latency assertion — reaching it means the exit " +
            $"watcher never fired. Last seen: Complete={last.Complete}, ExitCode=" +
            $"{last.ExitCode?.ToString() ?? "(null)"}, TotalLines={last.TotalLines}, " +
            $"lines=[{string.Join(" | ", last.Lines.Take(5))}]");
    }

    /// <summary>
    /// The hang-detection budget shared by every poll in this class. Long on
    /// purpose; see <see cref="PollUntilCompleteAsync"/>.
    /// </summary>
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(30);

    /// <summary>BackgroundJob requires a Process reference for its constructor
    /// but the buffer/append logic doesn't touch it — Process.GetCurrentProcess()
    /// is a safe placeholder for unit testing AppendLine / SnapshotLines.</summary>
    private static BackgroundJob NewJob() =>
        new("test", "test", "noop", DateTime.UtcNow, System.Diagnostics.Process.GetCurrentProcess());
}
