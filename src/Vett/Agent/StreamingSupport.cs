using Microsoft.Extensions.AI;

namespace Vett.Agent;

/// <summary>
/// Streaming wrapper for IChatClient. Streams tokens to a callback
/// while collecting the full response for the agent loop.
///
/// The loop still calls GetResponseAsync — streaming happens transparently.
/// Wrap the client: new StreamingChatClient(innerClient, token => Console.Write(token))
/// </summary>
public sealed class StreamingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly Action<string>? _onToken;

    public StreamingChatClient(IChatClient inner, Action<string>? onToken)
    {
        _inner = inner;
        _onToken = onToken;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        if (_onToken is null)
            return await _inner.GetResponseAsync(messages, options, ct);

        // Stream tokens to the callback as they arrive, then ask MEAI to
        // assemble the final ChatResponse. The previous hand-rolled
        // reconstruction emitted duplicate FunctionCallContent items because
        // streaming function calls arrive as multiple updates with partial
        // arguments — MEAI's ToChatResponse handles the merge correctly.
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                _onToken(update.Text);
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
        => _inner.GetStreamingResponseAsync(messages, options, ct);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
