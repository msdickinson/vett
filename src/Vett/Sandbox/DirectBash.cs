using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Vett.Sandbox;

/// <summary>
/// Direct in-process bash execution — no sidecar subprocess.
/// Used by `vett chat` for local mode. Implements the same methods
/// as RpcClient so tools can use either interchangeably.
/// </summary>
public sealed class DirectBash : ISandbox, IDisposable
{
    public string Cwd { get; private set; }

    /// <summary>
    /// Parent workspace root for dispatch isolation. Non-null only on
    /// sandboxes returned by <see cref="WithDispatchWorktree"/>. When set,
    /// absolute paths under this root that aren't already under
    /// <see cref="Cwd"/> are rewritten to live under Cwd by ResolvePath.
    /// Without this, an agent passing an absolute parent path to file_editor
    /// would silently bypass the per-dispatch worktree.
    /// </summary>
    public string? DispatchWorkspaceRoot { get; }

    // ConcurrentDictionary because parallel tool dispatch can hit file ops on
    // different paths simultaneously; the previous Dictionary would corrupt.
    private readonly ConcurrentDictionary<string, Stack<string>> _undoHistory = new();

    public DirectBash(string cwd)
    {
        Cwd = cwd;
    }

    private DirectBash(string cwd, string dispatchWorkspaceRoot)
    {
        Cwd = cwd;
        DispatchWorkspaceRoot = dispatchWorkspaceRoot;
    }

    /// <summary>
    /// Return a fresh DirectBash bound to <paramref name="newCwd"/>. Used
    /// by the team coordinator when dispatch_worktree is enabled — each
    /// member gets its own DirectBash rooted at its per-task worktree.
    /// Undo history is intentionally NOT shared: the worktree starts as
    /// a fresh checkout so file paths don't overlap with the parent
    /// sandbox's history, and tying the histories together would let
    /// one member undo another's edits.
    /// </summary>
    public ISandbox WithCwd(string newCwd) => new DirectBash(newCwd);

    /// <summary>
    /// Same as <see cref="WithCwd"/> plus a parent workspace boundary that
    /// path-translates absolute parent paths into the worktree. See
    /// <see cref="ISandbox.WithDispatchWorktree"/> for the policy.
    /// </summary>
    public ISandbox WithDispatchWorktree(string newCwd, string workspaceRoot)
        => new DirectBash(newCwd, workspaceRoot);

    /// <summary>Hard upper bound on bash execution for the in-process
    /// (chat) sandbox. Tuned for chat scenarios — researcher reads,
    /// quick commands, edit-and-test cycles — where 2 minutes is
    /// already pessimistic. Was 300s; lowered after `python3 -c …`
    /// on Windows kept hanging on the Store-launcher stub for the
    /// full timeout window. Long benchmark runs use the Docker
    /// sidecar's own timeout (RpcClient), not this clamp.</summary>
    private const int MaxTimeoutSec = 120;

    /// <summary>
    /// The deadline this sandbox will actually enforce for a requested
    /// <paramref name="requestedSec"/>: floored at 1s, capped at
    /// <see cref="MaxTimeoutSec"/>.
    ///
    /// Extracted from the call site so the ceiling has a failure test that
    /// does not require burning the full timeout window to observe it. This
    /// is the same expression that was inline before — it changes no value.
    /// Callers that need to know whether the clamp bit should compare the
    /// result against what they asked for.
    /// </summary>
    public static int ClampTimeoutSec(int requestedSec)
        => Math.Min(Math.Max(requestedSec, 1), MaxTimeoutSec);

    public async Task<BashResult> BashExecAsync(
        string sessionId, string command, int timeoutSec = 60, CancellationToken ct = default)
    {
        var effectiveTimeout = ClampTimeoutSec(timeoutSec);

        // Root-cause workaround: on Windows, `python3` resolves to the
        // Microsoft Store launcher stub (AppData\Local\Microsoft\WindowsApps
        // \python3.exe). Invoked non-interactively it hangs forever waiting
        // for the Store UI it can never get. Rewrite to `python` if the
        // stub is what we'd hit — the agent doesn't have to know.
        if (_python3IsStub.Value)
            command = Regex.Replace(command, @"\bpython3\b", "python");

        var shell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? FindGitBash() ?? "cmd"
            : "/bin/bash";

        var shellArg = shell.Contains("cmd") ? "/c" : "-c";

        var psi = new ProcessStartInfo(shell, $"{shellArg} \"{command.Replace("\"", "\\\"")}\"")
        {
            WorkingDirectory = Cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            return new BashResult("Error: failed to start shell", -1, Cwd, false);

        // Close stdin immediately. Without this, child processes inherit our
        // stdin (the JSON-RPC pipe from the extension) and any tool that
        // reads from stdin — interactive prompts, the Microsoft Store
        // python3 stub probing for a TTY — will block forever waiting for
        // input that will never come.
        try { proc.StandardInput.Close(); } catch { }

        // Drain stdout and stderr in parallel. The previous code awaited
        // one then the other, which can deadlock on Windows when the
        // child fills the smaller pipe's buffer waiting for the parent
        // to read it. Deadlock + 60s timeout = "vett is stuck".
        //
        // We tie the reads to a CTS that fires when the process exits
        // OR our own timeout trips. proc.Kill() releases the streams
        // so the read tasks complete instead of blocking forever — the
        // root cause of the chat-panel-stuck-for-4-minutes report.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(readCts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(readCts.Token);
        var exitTask = proc.WaitForExitAsync(readCts.Token);

        // Wait for either: the process exits, our timeout fires, or the
        // outer cancellation token is tripped.
        var timeoutTask = Task.Delay(effectiveTimeout * 1000, readCts.Token);
        var winner = await Task.WhenAny(exitTask, timeoutTask);

        var timedOut = winner == timeoutTask;
        if (timedOut)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }

        // Cancel the read CTS so any reader that's still pending wakes
        // up. proc.Kill above closed the pipes; this is belt-and-braces.
        try { readCts.Cancel(); } catch { }

        var stdout = new StringBuilder();
        try { stdout.Append(await stdoutTask.WaitAsync(TimeSpan.FromSeconds(2), ct)); }
        catch { /* gave up — partial output is still better than hanging */ }
        try { stdout.Append(await stderrTask.WaitAsync(TimeSpan.FromSeconds(2), ct)); }
        catch { /* same */ }

        if (timedOut)
        {
            // Report the deadline we ACTUALLY enforced. The caller may have
            // asked for more (the team bench harness's terminal tool defaults
            // to 600s) and been clamped to MaxTimeoutSec here; the tool layer
            // needs the real number or it tells the model it had 600s when it
            // had 120s. Informational only — the kill already happened above.
            return new BashResult(
                stdout.ToString() + $"\n[bash timed out after {effectiveTimeout}s, process killed]",
                -1, Cwd, true, effectiveTimeout);
        }

        return new BashResult(stdout.ToString(), proc.ExitCode, Cwd, false);
    }

    public Task<string> FileViewAsync(string sessionId, string path, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);

        if (Directory.Exists(fullPath))
        {
            var entries = Directory.GetFileSystemEntries(fullPath)
                .Select(Path.GetFileName)
                .Order();
            return Task.FromResult(string.Join("\n", entries));
        }

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"file_not_found: {path}");

        var lines = File.ReadAllLines(fullPath);
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
            sb.AppendLine($"{i + 1}\t{lines[i]}");
        return Task.FromResult(sb.ToString());
    }

    public Task<string> FileCreateAsync(string sessionId, string path, string fileText, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);
        if (File.Exists(fullPath))
            throw new InvalidOperationException($"file_exists: {path}");

        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, fileText);
        return FileViewAsync(sessionId, path, ct);
    }

    public async Task<(string Content, string? Error)> FileStrReplaceAsync(
        string sessionId, string path, string oldStr, string newStr, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);
        var content = File.ReadAllText(fullPath);

        var count = CountOccurrences(content, oldStr);
        if (count == 0 && (content.Contains("\r\n") || oldStr.Contains("\r\n")))
        {
            // Line-ending rescue. On Windows, git worktrees are checked
            // out with core.autocrlf so files carry \r\n while models
            // send \n in old_str — an exact match can then never
            // succeed on any multi-line snippet (2026-07-08 tier4
            // diagnosis: six dispatches burned on str_replace_no_match
            // against a byte-identical-modulo-CRLF anchor). Match on
            // LF-normalized text; write back with the file's endings.
            var lfContent = content.Replace("\r\n", "\n");
            var lfOld = oldStr.Replace("\r\n", "\n");
            var lfCount = CountOccurrences(lfContent, lfOld);
            if (lfCount == 1)
            {
                var replaced = lfContent.Replace(lfOld, newStr.Replace("\r\n", "\n"));
                if (content.Contains("\r\n"))
                    replaced = replaced.Replace("\n", "\r\n");
                SaveUndo(fullPath, content);
                File.WriteAllText(fullPath, replaced);
                var v = await FileViewAsync(sessionId, path, ct);
                return (v, null);
            }
            if (lfCount > 1)
                return ("", $"str_replace_multi_match: found {lfCount} times in " + path);
        }
        if (count == 0)
            // Coach the recovery instead of just reporting the miss. On
            // shakedown #51 the implementer burned 18 consecutive no_match
            // attempts on one file because every anchor was TYPED FROM
            // MEMORY (a remembered/guessed signature) rather than copied
            // from a view. The model that reads this message needs the next
            // action, not just the verdict.
            return ("", "str_replace_no_match: not found in " + path + ". " +
                        "Do not retype the anchor from memory - view the exact region first " +
                        "(file_editor view with view_range) and copy old_str character-for-character " +
                        "from the file content, without the line-number prefixes." +
                        NoMatchRecovery(content, oldStr));
        if (count > 1)
            return ("", $"str_replace_multi_match: found {count} times in " + path);

        SaveUndo(fullPath, content);
        File.WriteAllText(fullPath, content.Replace(oldStr, newStr));
        // Await instead of .Result — the previous sync-over-async could deadlock
        // under any SyncContext that captures the calling thread.
        var view = await FileViewAsync(sessionId, path, ct);
        return (view, null);
    }

    /// <summary>
    /// Builds the text a seat actually needs after a failed anchor match: the
    /// current contents around where it was probably aiming.
    /// </summary>
    /// <remarks>
    /// MEASURED 2026-09-11 across 36 EpicForge probe transcripts (three solo
    /// passes and three six-seat rounds over the same six modules). Two facts
    /// forced this:
    ///
    /// 1. Of 20 str_replace_no_match events, 19 had an old_str that was absent
    ///    from the file under EVERY whitespace relaxation -- CR removal,
    ///    per-line trim, whole-space collapse. Exactly one was an indentation
    ///    miss. So this is not a strict-matcher problem and loosening the
    ///    matcher would have fixed 1 in 20. The seat has already lost track of
    ///    the file's contents by the time it asks.
    /// 2. What the seat does next: 9 of 20 go and look; the other 11 retype the
    ///    anchor from memory or destroy the file. Six of the seven terminal
    ///    follow-ups were `cat > src/...` or `rm src/...` -- whole-file
    ///    rewrites, which is precisely the banked work the harness exists to
    ///    protect.
    ///
    /// The coaching sentence above is correct advice that is ignored more often
    /// than it is followed. A message cannot close a loop the harness still
    /// permits, so hand back the text and leave nothing to guess.
    ///
    /// This is not a new class of payload: the SUCCESS path of
    /// FileStrReplaceAsync already returns a full FileViewAsync dump, so seats
    /// receive whole-file numbered views routinely. It is the same payload on
    /// the branch that needs it more, and it trades a whole-file rewrite
    /// (2-22 KB of OUTPUT tokens, measured) for cheaper input tokens. Output is
    /// what sets a seat's wall time, so this should be net-negative on wall
    /// even before counting the correctness gain.
    ///
    /// Deliberately windowed rather than dumping everything: per-seat context
    /// is the measured serialiser at six concurrent seats, so a recovery that
    /// costs 25 lines is worth more than one that costs 400.
    /// </remarks>
    /// <summary>Identifier-ish tokens of a line, for the anchor-locating score.</summary>
    private static HashSet<string> Tokens(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var cur = new StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') cur.Append(ch);
            else if (cur.Length > 0) { set.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) set.Add(cur.ToString());
        return set;
    }

    internal static string NoMatchRecovery(string content, string oldStr)
    {
        if (string.IsNullOrEmpty(content)) return "";

        var lines = content.Replace("\r\n", "\n").Split('\n');

        // Probe with the first line of the anchor that carries any content.
        // If the seat invented the whole anchor there may be no probe at all,
        // in which case fall through to the head-of-file view below.
        string probe = "";
        foreach (var l in (oldStr ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var t = l.Trim();
            if (t.Length >= 4) { probe = t; break; }
        }

        int best = -1;
        if (probe.Length > 0)
        {
            // Substring identity is far too brittle to locate a retyped anchor:
            // the seat's line almost always differs by an operator or a space,
            // which is precisely why the match failed in the first place. So
            // score on shared identifiers. "return a*b" and "return a + b"
            // carry the same three tokens and are obviously the same line;
            // no substring test relates them at all.
            var want = Tokens(probe);
            if (want.Count > 0)
            {
                double bestScore = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].Trim();
                    if (t.Length == 0) continue;
                    if (t == probe) { best = i; break; }              // exact line
                    var have = Tokens(t);
                    if (have.Count == 0) continue;
                    int shared = 0;
                    foreach (var w in want) if (have.Contains(w)) shared++;
                    double score = (double)shared / (want.Count + have.Count - shared);
                    if (score > bestScore) { bestScore = score; best = i; }
                }
                // Below half the tokens in common it is not the same line, and
                // pointing at an unrelated region is worse than admitting the
                // anchor is absent.
                if (bestScore < 0.5 && (best < 0 || lines[best].Trim() != probe)) best = -1;
            }
        }

        var sb = new StringBuilder();
        int from, to;
        if (best >= 0)
        {
            from = Math.Max(0, best - 8);
            to = Math.Min(lines.Length - 1, best + 12);
            sb.Append("\n\nThe closest line to your anchor is ");
            sb.Append(best + 1);
            sb.Append(". This is what the file ACTUALLY contains there right now - ");
            sb.Append("copy old_str from these lines, not from memory:\n");
        }
        else
        {
            from = 0;
            to = Math.Min(lines.Length - 1, 40);
            sb.Append("\n\nNo line in the file resembles your anchor, so it is not there at all. ");
            sb.Append("The file currently begins:\n");
        }

        for (int i = from; i <= to; i++)
        {
            sb.Append(i + 1);
            sb.Append('\t');
            sb.Append(lines[i]);
            sb.Append('\n');
        }
        if (to < lines.Length - 1)
        {
            sb.Append("... (");
            sb.Append(lines.Length);
            sb.Append(" lines total; use file_editor view with view_range for the rest)\n");
        }
        return sb.ToString();
    }

    public Task<string> FileInsertAsync(
        string sessionId, string path, int insertLine, string newStr, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);
        var content = File.ReadAllText(fullPath);
        SaveUndo(fullPath, content);

        var lines = content.Split('\n').ToList();
        var idx = Math.Min(insertLine, lines.Count);
        lines.Insert(idx, newStr);
        File.WriteAllText(fullPath, string.Join("\n", lines));
        return FileViewAsync(sessionId, path, ct);
    }

    public Task<string> FileUndoAsync(string sessionId, string path, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);
        if (!_undoHistory.TryGetValue(fullPath, out var stack))
            throw new InvalidOperationException($"no undo history for {path}");

        string previous;
        lock (stack)
        {
            if (stack.Count == 0)
                throw new InvalidOperationException($"no undo history for {path}");
            previous = stack.Pop();
        }

        File.WriteAllText(fullPath, previous);
        return FileViewAsync(sessionId, path, ct);
    }

    public void Dispose() { }

    // For compatibility — tools that need SessionCreate just no-op in direct mode.
    public Task SessionCreateAsync(string name, string cwd, CancellationToken ct = default)
    {
        Cwd = cwd;
        return Task.CompletedTask;
    }

    public Task SessionDestroyAsync(string name, CancellationToken ct = default)
        => Task.CompletedTask;

    private string ResolvePath(string path)
    {
        if (!Path.IsPathRooted(path))
            return Path.Combine(Cwd, path);

        // Plain WithCwd sandbox (no dispatch boundary) — absolute paths
        // pass through verbatim. This keeps non-team-mode behavior
        // identical to before.
        if (DispatchWorkspaceRoot is null)
            return path;

        // Dispatch sandbox: an absolute path may legitimately point into
        // the worktree (no rewrite needed) OR into the parent workspace
        // (rewrite to the worktree-equivalent so the agent can't escape
        // isolation by using the absolute parent path it was given). On
        // Windows the comparison is case-insensitive; everywhere else
        // case-sensitive matches the filesystem.
        var normalized = Path.GetFullPath(path);
        var worktreeRoot = TrimTrailingSlash(Path.GetFullPath(Cwd));
        var workspaceRoot = TrimTrailingSlash(Path.GetFullPath(DispatchWorkspaceRoot));
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (IsUnderOrEqual(normalized, worktreeRoot, cmp))
            return normalized;

        if (IsUnderOrEqual(normalized, workspaceRoot, cmp))
        {
            var rel = Path.GetRelativePath(workspaceRoot, normalized);
            // GetRelativePath returns "." when the input equals the root.
            // Combine handles that case correctly (cwd + "." → cwd).
            return Path.Combine(Cwd, rel);
        }

        // Outside both — system paths, /tmp scratch, etc. Pass through;
        // policing those is out of scope for dispatch isolation.
        return normalized;
    }

    private static string TrimTrailingSlash(string p)
        => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnderOrEqual(string path, string root, StringComparison cmp)
    {
        if (string.Equals(path, root, cmp))
            return true;
        var prefix = root + Path.DirectorySeparatorChar;
        if (path.StartsWith(prefix, cmp))
            return true;
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
            && path.StartsWith(root + Path.AltDirectorySeparatorChar, cmp))
            return true;
        return false;
    }

    private void SaveUndo(string fullPath, string content)
    {
        var stack = _undoHistory.GetOrAdd(fullPath, _ => new Stack<string>());
        // Lock the per-file stack so concurrent writes to the same file don't
        // corrupt it. Different files are independent, so contention is rare.
        lock (stack) { stack.Push(content); }
    }

    private static int CountOccurrences(string text, string search)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += search.Length;
        }
        return count;
    }

    private static readonly Lazy<bool> _python3IsStub = new(DetectPython3Stub);

    private static bool DetectPython3Stub()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            var psi = new ProcessStartInfo("where", "python3")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            // The Store stub lives under WindowsApps. If the FIRST resolved
            // python3 is that stub, we'll hit it on every command.
            var firstLine = output.Split('\n').FirstOrDefault()?.Trim() ?? "";
            return firstLine.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
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
            if (File.Exists(c))
                return c;
        }

        return null;
    }
}
