using Microsoft.Extensions.AI;
using Vett.Llm;

namespace Vett.Tests;

/// <summary>Fake IChatClient that fails N times then succeeds.</summary>
public class FailThenSucceedClient : IChatClient
{
    private int _callCount;
    private readonly int _failCount;
    private readonly Exception? _exception;
    private readonly string _reply;

    public FailThenSucceedClient(int failCount, System.Net.HttpStatusCode? statusCode = null, string reply = "success")
    {
        _failCount = failCount;
        _reply = reply;
        _exception = statusCode.HasValue
            ? new HttpRequestException("test error", null, statusCode.Value)
            : null;
    }

    /// <summary>Ctor for a specific exception type (e.g. a connection-level
    /// HttpRequestException with no status, or a timeout).</summary>
    public FailThenSucceedClient(int failCount, Exception exception, string reply = "success")
    {
        _failCount = failCount;
        _exception = exception;
        _reply = reply;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); // real clients observe cancellation
        _callCount++;
        if (_callCount <= _failCount && _exception is not null)
            throw _exception;

        return Task.FromResult(new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, _reply)
        ]));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    public int CallCount => _callCount;
}

public class FailoverChatClientTests
{
    private static (IChatClient, string) Ep(IChatClient c, string label = "ep") => (c, label);

    [Fact]
    public async Task SingleEndpoint_Succeeds_NoFailover()
    {
        var a = new FailThenSucceedClient(0, reply: "A");
        var fc = new FailoverChatClient([Ep(a, "primary")]);

        var resp = await fc.GetResponseAsync([Chat.User("hi")]);

        Assert.Equal("A", resp.Messages.Last().GetText());
        Assert.Equal(1, a.CallCount);
    }

    [Fact]
    public async Task DeadPrimary_FailsOverToFallback()
    {
        // Primary is fully down (connection refused = HttpRequestException, no status).
        var primary = new FailThenSucceedClient(99, new HttpRequestException("connection refused"), reply: "P");
        var fallback = new FailThenSucceedClient(0, reply: "F");
        var fc = new FailoverChatClient([Ep(primary, "primary"), Ep(fallback, "fallback")]);

        var resp = await fc.GetResponseAsync([Chat.User("hi")]);

        Assert.Equal("F", resp.Messages.Last().GetText()); // served by fallback
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task FailsOver_On5xx()
    {
        var primary = new FailThenSucceedClient(99, System.Net.HttpStatusCode.ServiceUnavailable, reply: "P");
        var fallback = new FailThenSucceedClient(0, reply: "F");
        var fc = new FailoverChatClient([Ep(primary), Ep(fallback)]);

        var resp = await fc.GetResponseAsync([Chat.User("hi")]);
        Assert.Equal("F", resp.Messages.Last().GetText());
    }

    [Fact]
    public async Task Sticky_AfterFailover_StartsFromHealthyEndpoint()
    {
        var primary = new FailThenSucceedClient(99, new HttpRequestException("down"), reply: "P");
        var fallback = new FailThenSucceedClient(0, reply: "F");
        var fc = new FailoverChatClient([Ep(primary), Ep(fallback)]);

        await fc.GetResponseAsync([Chat.User("1")]);
        await fc.GetResponseAsync([Chat.User("2")]);

        // First call probed primary (1) + fallback (1). Second call should
        // start at the sticky fallback and NOT re-hit the dead primary.
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(2, fallback.CallCount);
    }

    [Fact]
    public async Task AllEndpointsDown_ThrowsLastException()
    {
        var a = new FailThenSucceedClient(99, System.Net.HttpStatusCode.BadGateway);
        var b = new FailThenSucceedClient(99, new HttpRequestException("b down"));
        var fc = new FailoverChatClient([Ep(a), Ep(b)]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => fc.GetResponseAsync([Chat.User("hi")]));
    }

    [Fact]
    public async Task BadRequest_400_DoesNotFailover()
    {
        // 400 (context overflow) would fail identically on the fallback, so
        // it must surface immediately without burning the fallback.
        var primary = new FailThenSucceedClient(99, System.Net.HttpStatusCode.BadRequest);
        var fallback = new FailThenSucceedClient(0, reply: "F");
        var fc = new FailoverChatClient([Ep(primary), Ep(fallback)]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => fc.GetResponseAsync([Chat.User("hi")]));
        Assert.Equal(0, fallback.CallCount); // never touched
    }

    [Fact]
    public async Task UserCancellation_Propagates_NoFailover()
    {
        var primary = new FailThenSucceedClient(0, reply: "P");
        var fallback = new FailThenSucceedClient(0, reply: "F");
        var fc = new FailoverChatClient([Ep(primary), Ep(fallback)]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fc.GetResponseAsync([Chat.User("hi")], null, cts.Token));
    }
}
