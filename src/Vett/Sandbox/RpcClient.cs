using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vett.Sandbox;

/// <summary>
/// JSON-RPC client over stdin/stdout to the Go sidecar.
/// Implements ISandbox so tools work with it directly.
/// </summary>
public sealed class RpcClient : ISandbox, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;

    /// <summary>
    /// Empty for the RPC sandbox — the sidecar tracks cwd per-session and
    /// doesn't push it back on session_create. Schema substitution for
    /// benchmark runs happens in Runner.cs at schema-load time, so the
    /// agent-loop substitution path doesn't need this.
    /// </summary>
    public string Cwd => "";

    public RpcClient(Stream stdout, Stream stdin)
    {
        _writer = new StreamWriter(stdin) { AutoFlush = true };
        Task.Run(() => ReadLoop(new StreamReader(stdout)));
    }

    private async Task ReadLoop(StreamReader reader)
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var id = doc.RootElement.GetProperty("id").GetString() ?? "";
                if (_pending.TryRemove(id, out var tcs))
                    tcs.SetResult(doc.RootElement.Clone());
            }
        }
        catch { }
        finally
        {
            foreach (var (_, tcs) in _pending)
                tcs.TrySetException(new InvalidOperationException("sidecar closed"));
        }
    }

    private async Task<JsonElement> CallAsync(string op, object args, CancellationToken ct)
    {
        var id = $"r-{Interlocked.Increment(ref _nextId)}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        try
        {
            var req = JsonSerializer.Serialize(new { id, op, args });
            lock (_writer) { _writer.WriteLine(req); }
            var resp = await tcs.Task;
            if (resp.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
            {
                var err = resp.GetProperty("error").GetProperty("message").GetString() ?? "unknown error";
                throw new InvalidOperationException($"RPC {op}: {err}");
            }
            return resp.GetProperty("result");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    // --- Public API: these are what tools call directly ---

    public async Task HelloAsync(CancellationToken ct = default)
        => await CallAsync("hello", new { host_version = "0.1.0-dev", protocol_version = 1 }, ct);

    public async Task SessionCreateAsync(string name, string cwd, CancellationToken ct = default)
        => await CallAsync("session_create", new { name, cwd }, ct);

    public async Task SessionDestroyAsync(string name, CancellationToken ct = default)
        => await CallAsync("session_destroy", new { name }, ct);

    public async Task<BashResult> BashExecAsync(string sessionId, string command, int timeoutSec = 60, CancellationToken ct = default)
    {
        var r = await CallAsync("bash_exec", new { session_id = sessionId, command, timeout_seconds = timeoutSec }, ct);
        return new BashResult(
            r.GetProperty("stdout").GetString() ?? "",
            r.GetProperty("exit_code").GetInt32(),
            r.GetProperty("cwd").GetString() ?? "",
            r.TryGetProperty("timed_out", out var to) && to.GetBoolean());
    }

    public async Task<string> FileViewAsync(string sessionId, string path, CancellationToken ct = default)
    {
        var r = await CallAsync("file_view", new { session_id = sessionId, path }, ct);
        return r.GetProperty("content").GetString() ?? "";
    }

    public async Task<string> FileCreateAsync(string sessionId, string path, string fileText, CancellationToken ct = default)
    {
        var r = await CallAsync("file_create", new { session_id = sessionId, path, file_text = fileText }, ct);
        return r.GetProperty("content").GetString() ?? "";
    }

    public async Task<(string Content, string? Error)> FileStrReplaceAsync(string sessionId, string path, string oldStr, string newStr, CancellationToken ct = default)
    {
        try
        {
            var r = await CallAsync("file_str_replace", new { session_id = sessionId, path, old_str = oldStr, new_str = newStr }, ct);
            return (r.GetProperty("content").GetString() ?? "", null);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("str_replace"))
        {
            return ("", ex.Message);
        }
    }

    public async Task<string> FileInsertAsync(string sessionId, string path, int insertLine, string newStr, CancellationToken ct = default)
    {
        var r = await CallAsync("file_insert", new { session_id = sessionId, path, insert_line = insertLine, new_str = newStr }, ct);
        return r.GetProperty("content").GetString() ?? "";
    }

    public async Task<string> FileUndoAsync(string sessionId, string path, CancellationToken ct = default)
    {
        var r = await CallAsync("file_undo", new { session_id = sessionId, path }, ct);
        return r.GetProperty("content").GetString() ?? "";
    }

    /// <summary>
    /// Per-task worktree isolation isn't supported via the sidecar protocol
    /// — the sidecar is a single subprocess (potentially inside Docker)
    /// and per-task worktrees would require either a new session per
    /// dispatch (sidecar protocol change) or container-level bind mounts.
    /// Out of scope for v0.1; callers should detect this and fall back to
    /// direct dispatch. In practice, chat profiles use DirectBash so this
    /// path only fires for bench/Docker runs where worktrees are unwanted.
    /// </summary>
    public ISandbox WithCwd(string newCwd)
        => throw new NotSupportedException(
            "Per-task worktrees are not supported in sidecar/Docker sandbox mode. " +
            "Set team.dispatch_worktree: false in this profile, or run in local (DirectBash) mode.");

    public ISandbox WithDispatchWorktree(string newCwd, string workspaceRoot)
        => throw new NotSupportedException(
            "Per-task worktrees are not supported in sidecar/Docker sandbox mode. " +
            "Set team.dispatch_worktree: false in this profile, or run in local (DirectBash) mode.");

    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
}

/// <param name="EffectiveTimeoutSec">
/// The deadline the sandbox ACTUALLY enforced, when it differs from what the
/// caller asked for. <see cref="DirectBash"/> clamps every request to its own
/// ceiling (see DirectBash.MaxTimeoutSec), so a caller asking for 600s can be
/// killed at 120s; without this field the tool layer had no way to know that
/// and reported the REQUESTED number back to the model as if it had been
/// honoured. Null means "no clamp applied — the request stands", which is the
/// sidecar/<see cref="RpcClient"/> case: the Go sidecar only floors a
/// non-positive timeout at 60s and imposes no upper bound
/// (sidecar/internal/sidecar/server.go:273-275, bash.go:180-183).
/// This carries information only; it moves no deadline.
/// </param>
public sealed record BashResult(
    string Stdout, int ExitCode, string Cwd, bool TimedOut, int? EffectiveTimeoutSec = null);
