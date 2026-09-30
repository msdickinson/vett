using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vett.Tools;

/// <summary>
/// Long-running shell command service for chat sessions. The agent
/// fires `bash_background` to start a build / dev server / test
/// watcher; the call returns immediately with a job id; subsequent
/// `monitor` calls stream new output lines so the agent can react
/// (e.g. wait for "Listening on 0.0.0.0:3000", then proceed).
///
/// Lives only in chat (RunStdio) — benchmarks have no notion of "long
/// running" because the loop already runs to completion. Constructed
/// per-session and disposed on session end so leftover processes are
/// killed alongside.
///
/// Implementation: each job spawns a Process with redirected stdout/
/// stderr; OutputDataReceived / ErrorDataReceived handlers append to a
/// shared lock-guarded line buffer. Monitor reads from `sinceLine`
/// onwards. Kill calls Process.Kill(entireProcessTree:true).
/// </summary>
public sealed class BackgroundBashService : IDisposable
{
    private readonly ConcurrentDictionary<string, BackgroundJob> _jobs = new();
    private readonly string _cwd;
    private bool _disposed;

    public BackgroundBashService(string cwd)
    {
        _cwd = cwd;
    }

    /// <summary>Start a new background job. Returns job id + the name
    /// we'll use in the jobs table (caller-supplied or auto-generated).</summary>
    public (string Id, string Name) Start(string command, string? name = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BackgroundBashService));
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("command is required");

        var id = "bg-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var displayName = string.IsNullOrWhiteSpace(name) ? id : name.Trim();

        var shell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? FindGitBash() ?? "cmd"
            : "/bin/bash";
        // Prefer EndsWith over Contains: a bash binary at a path like
        // C:\my-cmd-tools\bash.exe contains "cmd" as a substring,
        // which would mistakenly route /c to bash. Match the actual
        // executable name (with or without .exe) instead.
        var shellName = Path.GetFileName(shell);
        var shellArg = (string.Equals(shellName, "cmd", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(shellName, "cmd.exe", StringComparison.OrdinalIgnoreCase))
            ? "/c"
            : "-c";
        var psi = new ProcessStartInfo(shell, $"{shellArg} \"{command.Replace("\"", "\\\"")}\"")
        {
            WorkingDirectory = _cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var proc = Process.Start(psi);
        if (proc is null) throw new InvalidOperationException("failed to start shell");

        // Close stdin so the child doesn't inherit our JSON-RPC pipe —
        // same defense as DirectBash.BashExecAsync.
        try { proc.StandardInput.Close(); } catch { }

        var job = new BackgroundJob(id, displayName, command, DateTime.UtcNow, proc);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            job.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            // Tag stderr inline so the agent / user can tell streams apart
            // without needing two separate buffers (which would interleave
            // weirdly when displayed). Same convention as docker logs.
            job.AppendLine("[stderr] " + e.Data);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Fire-and-forget exit watcher — populates ExitCode + Complete
        // when the process finishes so subsequent monitor calls report
        // the right status without us having to poll Process.HasExited.
        _ = Task.Run(async () =>
        {
            try
            {
                await proc.WaitForExitAsync();
                job.MarkComplete(proc.ExitCode);
            }
            catch
            {
                job.MarkComplete(-1);
            }
        });

        _jobs[id] = job;
        return (id, displayName);
    }

    /// <summary>Read output from a job. `sinceLine` is the cumulative
    /// line number you last saw (0 returns everything). The previous
    /// call's `total_lines` is the right cursor to pass back.
    ///
    /// `sinceLine` lives in the cumulative-emit axis, NOT the snapshot
    /// index axis. That distinction matters once the buffer has been
    /// truncated for memory: the agent's cursor stays valid across
    /// truncations, but lines older than the current buffer are gone
    /// and the slice will start later than `sinceLine`. The first
    /// element of the returned `Lines` is then the truncation sentinel
    /// (`[output truncated — earlier lines dropped to bound memory]`)
    /// so the agent knows it missed something.
    ///
    /// Returns null when the job id is unknown.</summary>
    public MonitorResult? GetOutput(string jobId, int sinceLine = 0)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return null;
        var snapshot = job.SnapshotLines();
        var totalEmitted = job.TotalEmitted;
        // Lines below this index were dropped during truncation and are
        // no longer in the buffer. Cursor translation: subtract that
        // offset to map the cumulative cursor onto the live snapshot.
        var droppedBefore = totalEmitted - snapshot.Count;
        var snapshotStart = (int)Math.Max(0, Math.Min(sinceLine - droppedBefore, snapshot.Count));
        var slice = snapshot.GetRange(snapshotStart, snapshot.Count - snapshotStart);
        return new MonitorResult(
            JobId: job.Id,
            Name: job.Name,
            Command: job.Command,
            StartedAt: job.StartedAt,
            Lines: slice,
            // FromLine reports the cumulative cursor where this slice
            // begins — could be > sinceLine when truncation chopped off
            // what the agent asked for.
            FromLine: (int)Math.Min(int.MaxValue, droppedBefore + snapshotStart),
            TotalLines: (int)Math.Min(int.MaxValue, totalEmitted),
            Complete: job.Complete,
            ExitCode: job.ExitCode);
    }

    /// <summary>Terminate a running job. Returns false when the job id is
    /// unknown; true when we attempted a kill (whether or not the process
    /// was already dead). Idempotent — repeat calls are safe.</summary>
    public bool Kill(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        try
        {
            if (!job.Process.HasExited)
                job.Process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone — fine.
        }
        return true;
    }

    /// <summary>Snapshot of every job for the agent's `bash_jobs` query.
    /// Sorted by start time (most recent first) so the agent sees what
    /// it just started at the top.</summary>
    public IReadOnlyList<JobSummary> ListJobs()
    {
        return _jobs.Values
            .OrderByDescending(j => j.StartedAt)
            .Select(j => new JobSummary(
                j.Id, j.Name, j.Command, j.StartedAt, j.Complete, j.ExitCode, j.LineCount))
            .ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var job in _jobs.Values)
        {
            try
            {
                if (!job.Process.HasExited) job.Process.Kill(entireProcessTree: true);
            }
            catch { /* best-effort */ }
            try { job.Process.Dispose(); } catch { }
        }
        _jobs.Clear();
    }

    private static string? FindGitBash()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }
}

/// <summary>One running (or completed) background job. Mutable; owns
/// its Process. AppendLine + SnapshotLines are thread-safe via lock —
/// expected pattern is "many writers (output handlers fire on the
/// thread pool), one reader at a time (the agent's monitor call)."
///
/// Output is capped at <see cref="MaxLines"/> total. When the cap is
/// hit, oldest lines are dropped first and a one-time truncation
/// marker is appended so a `monitor` call sees that earlier output
/// went away. This bounds memory for misbehaving long-running jobs
/// (a watch-build that prints 1MB/sec used to grow forever).</summary>
public sealed class BackgroundJob
{
    /// <summary>Hard cap on retained lines. ~10K lines covers the
    /// typical "tail of dev-server output" use case and bounds memory
    /// at roughly a few MB even on chatty jobs. Bump if a real
    /// workflow needs more; the cursor pattern (`since_line`) means
    /// agents shouldn't be holding all output in their context anyway.</summary>
    public const int MaxLines = 10_000;

    public string Id { get; }
    public string Name { get; }
    public string Command { get; }
    public DateTime StartedAt { get; }
    public Process Process { get; }
    public bool Complete { get; private set; }
    public int? ExitCode { get; private set; }
    public int LineCount
    {
        get { lock (_lines) return _lines.Count; }
    }

    private readonly List<string> _lines = new();
    /// <summary>Total lines ever emitted (incl. dropped). Lets a
    /// `since_line` cursor stay correct after truncation: snapshot
    /// ranges become invalid once we drop, but the consumer can
    /// detect the mismatch via TotalLines vs LineCount.</summary>
    public long TotalEmitted { get; private set; }
    private bool _truncationMarked;

    public BackgroundJob(string id, string name, string command, DateTime startedAt, Process process)
    {
        Id = id;
        Name = name;
        Command = command;
        StartedAt = startedAt;
        Process = process;
    }

    public void AppendLine(string line)
    {
        lock (_lines)
        {
            TotalEmitted++;
            _lines.Add(line);
            if (_lines.Count > MaxLines)
            {
                // Drop oldest. RemoveRange is O(n) but only fires
                // once we exceed the cap — amortized a no-op.
                var overflow = _lines.Count - MaxLines;
                _lines.RemoveRange(0, overflow);
                if (!_truncationMarked)
                {
                    // Single sentinel at the head so the consumer
                    // sees ONE marker, not one per dropped line.
                    _lines[0] = "[output truncated — earlier lines dropped to bound memory]";
                    _truncationMarked = true;
                }
            }
        }
    }

    /// <summary>Returns a stable copy of the line buffer at this moment.</summary>
    public List<string> SnapshotLines()
    {
        lock (_lines) return new List<string>(_lines);
    }

    public void MarkComplete(int exitCode)
    {
        ExitCode = exitCode;
        Complete = true;
    }
}

public sealed record MonitorResult(
    string JobId,
    string Name,
    string Command,
    DateTime StartedAt,
    IReadOnlyList<string> Lines,
    int FromLine,
    int TotalLines,
    bool Complete,
    int? ExitCode);

public sealed record JobSummary(
    string Id,
    string Name,
    string Command,
    DateTime StartedAt,
    bool Complete,
    int? ExitCode,
    int LineCount);
