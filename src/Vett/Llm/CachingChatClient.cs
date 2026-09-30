using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Vett.Llm;

public enum CacheMode { Off, CaptureOnly, ReplayStrict, ReplayFallback }

/// <summary>
/// IChatClient decorator that caches responses for deterministic replay.
/// Wraps any provider — local, OpenAI, Anthropic, etc.
/// </summary>
public sealed class ReplayChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly List<CacheEntry> _entries = [];
    private readonly Dictionary<string, int> _index = new();

    public CacheMode Mode { get; set; } = CacheMode.Off;
    public int EntryCount => _entries.Count;

    public ReplayChatClient(IChatClient inner)
    {
        _inner = inner;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var hash = Hash(messages, options);

        // Check cache.
        if (Mode is CacheMode.ReplayStrict or CacheMode.ReplayFallback)
        {
            if (_index.TryGetValue(hash, out var idx))
                return _entries[idx].Response;

            if (Mode == CacheMode.ReplayStrict)
                throw new LlmException($"Cache miss (strict mode): {hash}");
        }

        // Live call.
        var response = await _inner.GetResponseAsync(messages, options, ct);

        // Store.
        if (Mode != CacheMode.Off)
        {
            _index[hash] = _entries.Count;
            _entries.Add(new CacheEntry(hash, response));
        }

        return response;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
        => _inner.GetStreamingResponseAsync(messages, options, ct);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();

    // --- Cache persistence ---

    public void LoadFrom(string path)
    {
        if (!File.Exists(path)) return;
        var json = File.ReadAllText(path);
        // For now, cache files store hash → serialized response pairs.
        // Full implementation would serialize ChatResponse — simplified here.
    }

    public void SaveTo(string path)
    {
        var json = JsonSerializer.Serialize(_entries.Select(e => new { e.Hash }), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static string Hash(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var sb = new StringBuilder();
        sb.Append(options?.ModelId ?? "");
        sb.Append(options?.Temperature?.ToString("F6") ?? "");
        sb.Append(options?.TopP?.ToString("F6") ?? "");
        foreach (var m in messages)
            sb.Append($"{m.Role}:{m.Text}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private sealed record CacheEntry(string Hash, ChatResponse Response);
}
