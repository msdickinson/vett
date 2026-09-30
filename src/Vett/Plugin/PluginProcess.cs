using System.Diagnostics;
using System.Text.Json;

namespace Vett.Plugin;

/// <summary>
/// A long-lived plugin subprocess. Communicates via newline-delimited
/// JSON on stdin/stdout. Thread-safe — serializes calls through a semaphore.
/// </summary>
public sealed class PluginProcess : IDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private PluginProcess(Process proc)
    {
        _proc = proc;
        _stdin = proc.StandardInput;
        _stdin.AutoFlush = true;
        _stdout = proc.StandardOutput;
    }

    public static async Task<PluginProcess> StartAsync(string name, string cmd, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cmd)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var proc = Process.Start(psi)
            ?? throw new PluginException($"Failed to start plugin {name}");
        return new PluginProcess(proc);
    }

    public async Task<string> ExecuteAsync(Dictionary<string, object?> args, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await _stdin.WriteLineAsync(JsonSerializer.Serialize(new { type = "execute", args }));
            var line = await _stdout.ReadLineAsync(ct)
                ?? throw new PluginException("Plugin closed unexpectedly");

            var resp = JsonSerializer.Deserialize<JsonElement>(line);
            if (resp.TryGetProperty("error", out var err) && err.GetString() is { Length: > 0 } e)
                throw new PluginException(e);

            return resp.GetProperty("result").GetString() ?? "";
        }
        finally { _lock.Release(); }
    }

    public async Task<JsonElement?> ExecuteMiddlewareAsync(string json, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await _stdin.WriteLineAsync(json);
            var line = await _stdout.ReadLineAsync(ct);
            return line is null ? null : JsonSerializer.Deserialize<JsonElement>(line);
        }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        try { _proc.Kill(); } catch { }
        _proc.Dispose();
        _lock.Dispose();
    }
}
