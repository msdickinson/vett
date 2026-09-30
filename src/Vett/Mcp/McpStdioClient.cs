using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Vett.Mcp;

/// <summary>
/// MCP server connected over stdio. Spawns the server as a subprocess,
/// pipes JSON-RPC line-delimited messages on stdin/stdout, and
/// dispatches responses back to the matching pending request via
/// JSON-RPC id.
///
/// One instance per server. Owns the subprocess lifetime — call
/// <see cref="Dispose"/> to terminate.
///
/// Threading model: the stdout reader runs on a fire-and-forget
/// background task; <see cref="SendAsync"/> writes from the calling
/// task. A <see cref="ConcurrentDictionary{TKey, TValue}"/> of pending
/// requests keyed by id handles the response routing.
/// </summary>
public sealed class McpStdioClient : IDisposable
{
    public string Name { get; }
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonRpcEnvelope>> _pending = new();
    private int _nextId = 1;
    private readonly Task _readerTask;
    private bool _disposed;

    private McpStdioClient(string name, Process proc)
    {
        Name = name;
        _proc = proc;
        _stdin = proc.StandardInput;
        _readerTask = Task.Run(() => ReadLoop(proc));
    }

    /// <summary>
    /// Spawn the subprocess, complete the initialize handshake, and
    /// return a connected client. Throws if the spawn fails or the
    /// server doesn't respond to initialize within
    /// <paramref name="connectTimeoutSeconds"/>.
    /// </summary>
    public static async Task<McpStdioClient> ConnectAsync(
        string name,
        string command,
        Dictionary<string, string>? env,
        int connectTimeoutSeconds,
        CancellationToken ct,
        bool verbose = false)
    {
        var (shell, flag) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("cmd.exe", "/c")
            : ("/bin/bash", "-c");
        var psi = new ProcessStartInfo(shell, $"{flag} {command}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // NO BOM — `Encoding.UTF8` writes a UTF-8 BOM at the start of
            // the stream, which every MCP server's JSON parser rejects
            // ("Parse error: Unexpected UTF-8 BOM"). Silently caused a
            // 20s connect_timeout on every server, and made mcp_servers
            // effectively unusable — surfaced during the 2026-07-05 A/B
            // debug pass. Keep BOM-free on both directions so any future
            // asymmetric write doesn't repeat the bug.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        if (env is not null)
        {
            foreach (var (k, v) in env) psi.Environment[k] = v;
        }
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"MCP server '{name}' failed to start (Process.Start returned null)");

        // Drain stderr in the background so a chatty server doesn't
        // backpressure-block on its pipe. Forward to console only
        // when `verbose: true` is set in the per-server config; off
        // by default to keep production logs clean.
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                {
                    if (verbose && !string.IsNullOrWhiteSpace(line))
                        Console.Error.WriteLine($"[mcp:{name}] {line.TrimEnd()}");
                }
            }
            catch { /* server died; the reader loop will surface it */ }
        });

        var client = new McpStdioClient(name, proc);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSeconds));

            // initialize handshake
            var init = await client.SendAsync(McpProtocol.BuildInitialize(client.NextId(), "vett", "0.1.0"), connectCts.Token);
            if (init.Error is not null)
                throw new InvalidOperationException($"MCP server '{name}' rejected initialize: {init.Error.Message}");

            // initialized notification — fire-and-forget, no id
            await client.SendNotificationAsync(McpProtocol.BuildInitializedNotification(), connectCts.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }

    /// <summary>
    /// Send a JSON-RPC request and await its matching response. The
    /// <c>id</c> field on the request body must match an <c>id</c>
    /// returned by <see cref="NextId"/>. Times out after
    /// <paramref name="timeoutMs"/>; default 60s suits tool calls
    /// that may legitimately take a while.
    /// </summary>
    public async Task<JsonRpcEnvelope> SendAsync(object body, CancellationToken ct, int timeoutMs = 60_000)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(McpStdioClient));
        var json = JsonSerializer.Serialize(body);
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
            throw new InvalidOperationException("SendAsync body must carry an integer id");

        var tcs = new TaskCompletionSource<JsonRpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            // Pass ct through the write path so a wedged stdin pipe
            // doesn't outlast the caller's cancellation.
            await _stdin.WriteAsync(json.AsMemory(), ct);
            await _stdin.WriteAsync(Environment.NewLine.AsMemory(), ct);
            await _stdin.FlushAsync(ct);
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task SendNotificationAsync(object body, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(McpStdioClient));
        var json = JsonSerializer.Serialize(body);
        // Honor cancellation on the write itself — if the server's
        // stdin pipe is wedged, ct lets the caller bail rather than
        // hang forever. StreamWriter.WriteLineAsync(ReadOnlyMemory,
        // CancellationToken) is the .NET 7+ overload; we pass `json`
        // as a memory chunk + a separate newline write so both phases
        // observe ct.
        await _stdin.WriteAsync(json.AsMemory(), ct);
        await _stdin.WriteAsync(Environment.NewLine.AsMemory(), ct);
        await _stdin.FlushAsync(ct);
        // Notifications have no response; no TCS to register.
    }

    public int NextId() => Interlocked.Increment(ref _nextId);

    public async Task<List<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var resp = await SendAsync(McpProtocol.BuildToolsList(NextId()), ct);
        if (resp.Error is not null)
            throw new InvalidOperationException($"MCP server '{Name}' tools/list failed: {resp.Error.Message}");
        if (!resp.Result.HasValue) return new List<McpToolInfo>();
        return McpProtocol.ParseToolsList(resp.Result.Value);
    }

    public async Task<string> CallToolAsync(string toolName, object arguments, CancellationToken ct)
    {
        var resp = await SendAsync(McpProtocol.BuildToolsCall(NextId(), toolName, arguments), ct);
        if (resp.Error is not null)
            return $"Error: MCP tool '{toolName}' failed: {resp.Error.Message}";
        if (!resp.Result.HasValue) return "";
        return McpProtocol.ParseToolCallResult(resp.Result.Value);
    }

    private async Task ReadLoop(Process proc)
    {
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonRpcEnvelope env;
                try
                {
                    env = McpProtocol.ParseEnvelope(line);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[mcp:{Name}] dropped malformed line: {ex.Message}");
                    continue;
                }
                if (env.Id is int id && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(env);
                }
                // Server-initiated notifications (logging, etc) are
                // ignored in v1. v2 can route them through.
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mcp:{Name}] reader loop ended: {ex.Message}");
        }
        // If the server died with pending requests, fail them all so
        // callers don't hang.
        foreach (var (_, tcs) in _pending)
            tcs.TrySetException(new InvalidOperationException($"MCP server '{Name}' closed unexpectedly"));
        _pending.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stdin.Dispose(); } catch { }
        try
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(2000);
            }
        }
        catch { }
        try { _proc.Dispose(); } catch { }
    }
}
