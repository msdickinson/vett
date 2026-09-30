using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Config;
using Vett.Llm;

namespace Vett.Tests;

/// <summary>
/// ⛔⛔ THE REQUEST TIMEOUT BOUNDED THE WHOLE GENERATION, AND UNDER LOAD THAT
/// KILLED EVERY LONG HONEST ANSWER (EpicForge run 5, 2026-09-05, law 135).
///
/// <c>request_timeout_seconds</c> reached the SDK as <c>NetworkTimeout</c> on a
/// BUFFERED request, so it ran from the first byte sent to the last byte
/// received. At eight parallel seats the engine decoded ~12.5 tok/s per
/// request; a 180 s bound therefore covered ~2,250 tokens against a 4,096
/// <c>max_output_tokens</c>, and every reply longer than that timed out no
/// matter how well it was going. Six seats died at the 1,800 s deadline that
/// way, each having re-sent the identical doomed request up to nine times.
///
/// The fix streams and re-arms the clock on every update, so the bound means
/// "the engine said nothing for N seconds". These tests drive the REAL
/// <see cref="ChatClientFactory"/> client against an in-process fake engine
/// that speaks the OpenAI wire format over a raw socket (no HttpListener URL
/// ACLs, no shared port): it answers a streaming request with a tool call in
/// argument fragments spaced well inside the bound but lasting well past it,
/// and answers a buffered request only after the whole duration.
///
/// FAIL-FIRST, measured on the code before the fix (buffered client):
///   - <see cref="A_long_reply_paced_inside_the_bound_completes_with_call_finish_and_usage"/>
///     RED: the 2 s network timeout fires at 2 s of a 6 s reply.
///   - <see cref="Silence_past_the_bound_is_an_llm_error_after_one_request_not_a_cancellation"/>
///     RED: the failure surfaces as the SDK's TaskCanceledException, not as
///     an <see cref="LlmException"/>.
///   - <see cref="The_callers_own_cancellation_still_surfaces_as_a_cancellation"/>
///     GREEN on both: the control that the conversion is scoped to OUR timer.
/// </summary>
public sealed class SilenceBoundedStreamingTests
{
    private static LlmConfig Local(FakeOpenAIEngine engine, int timeoutSeconds) => new()
    {
        Provider = "local",
        Endpoint = engine.Endpoint,
        Model = "fake",
        RequestTimeoutSeconds = timeoutSeconds,
        NumRetries = 0,
    };

    private static readonly ChatMessage[] Prompt = [new ChatMessage(ChatRole.User, "write the file")];

    [Fact]
    public async Task A_long_reply_paced_inside_the_bound_completes_with_call_finish_and_usage()
    {
        // 24 fragments x 250 ms = 6 s of steady progress; the bound is 2 s.
        var args = new Dictionary<string, object?>
        {
            ["path"] = "src/clear.js",
            ["content"] = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"export const line{i} = {i};")),
        };
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "file_editor",
            Arguments: JsonSerializer.Serialize(args),
            Fragments: 24,
            Gap: TimeSpan.FromMilliseconds(250),
            CompletionTokens: 3000));
        using var client = ChatClientFactory.Create(Local(engine, timeoutSeconds: 2));

        var sw = Stopwatch.StartNew();
        var resp = await client.GetResponseAsync(Prompt);
        sw.Stop();

        var call = Assert.Single(resp.Messages.Single().Contents.OfType<FunctionCallContent>());
        Assert.Equal("file_editor", call.Name);
        Assert.NotNull(call.Arguments);
        Assert.Equal("src/clear.js", call.Arguments!["path"]?.ToString());
        Assert.Equal(args["content"], call.Arguments["content"]?.ToString());
        Assert.Equal(ChatFinishReason.ToolCalls, resp.FinishReason);
        Assert.NotNull(resp.Usage);
        Assert.Equal(3000, resp.Usage!.OutputTokenCount);
        // It really waited the whole reply out past the old bound, and paid one request for it.
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(5), $"finished in {sw.Elapsed.TotalSeconds:0.0}s; the reply takes 6 s");
        Assert.Equal(1, engine.Requests);

        // The wire carried a streaming request that asks for usage -- law 131's
        // ruler (output_tokens on llm_response) depends on that flag.
        var body = Assert.Single(engine.RequestBodies);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean(), "request must be stream:true");
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean(),
            "request must ask for usage in the final chunk");
    }

    [Fact]
    public async Task Silence_past_the_bound_is_an_llm_error_after_one_request_not_a_cancellation()
    {
        // Two fragments 100 ms apart, then the engine says nothing for 8 s.
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "file_editor",
            Arguments: JsonSerializer.Serialize(new { path = "a.js", content = "x" }),
            Fragments: 2,
            Gap: TimeSpan.FromMilliseconds(100),
            CompletionTokens: 10,
            SilenceAfterFragment: 2,
            Silence: TimeSpan.FromSeconds(8)));
        using var client = ChatClientFactory.Create(Local(engine, timeoutSeconds: 1));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<LlmException>(() => client.GetResponseAsync(Prompt));
        sw.Stop();

        Assert.Contains("silent for 1s", ex.Message);
        Assert.Contains("request_timeout_seconds", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed.TotalSeconds:0.0}s; the bound is 1 s, the silence 8 s");
        // Nothing in the pipeline re-sends a request that failed mid-stream.
        Assert.Equal(1, engine.Requests);
    }

    [Fact]
    public async Task The_callers_own_cancellation_still_surfaces_as_a_cancellation()
    {
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "file_editor",
            Arguments: JsonSerializer.Serialize(new { path = "a.js", content = "x" }),
            Fragments: 40,
            Gap: TimeSpan.FromMilliseconds(200),
            CompletionTokens: 10));
        using var client = ChatClientFactory.Create(Local(engine, timeoutSeconds: 30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetResponseAsync(Prompt, cancellationToken: cts.Token));
        Assert.IsNotType<LlmException>(ex);
    }

    [Fact]
    public void The_factory_wraps_every_provider_path_by_default()
    {
        using var engine = new FakeOpenAIEngine(new FakeReply("t", "{}", 1, TimeSpan.Zero, 1));
        using var client = ChatClientFactory.Create(Local(engine, timeoutSeconds: 7));
        var bounded = Assert.IsType<SilenceBoundedChatClient>(client);
        Assert.Equal(TimeSpan.FromSeconds(7), bounded.SilenceBound);
    }
}

/// <summary>What the fake engine answers: one tool call, delivered in pieces.</summary>
/// <param name="SilenceAfterFragment">After this many argument fragments the stream goes quiet for <paramref name="Silence"/> (0 = never).</param>
internal sealed record FakeReply(
    string ToolName,
    string Arguments,
    int Fragments,
    TimeSpan Gap,
    int CompletionTokens,
    int SilenceAfterFragment = 0,
    TimeSpan Silence = default,
    // Law 232: several tool calls in ONE reply, wire index 0..n-1, ids
    // call_fake_1..n, each streamed in Fragments pieces. Null = the one
    // call in Arguments.
    IReadOnlyList<string>? Siblings = null,
    // Law 240: text streamed as `content` deltas BEFORE any tool call. With
    // an empty ToolName the reply is prose only and finishes `stop`.
    string? Prose = null);

/// <summary>
/// An OpenAI-shaped chat-completions endpoint on a raw loopback socket.
/// Streaming requests get SSE chunks paced by <see cref="FakeReply.Gap"/>;
/// buffered requests get the full completion only after the same total
/// duration (that IS the behaviour under test: the buffered client cannot
/// tell "slow" from "dead"). One connection per request (Connection: close),
/// so <see cref="Requests"/> counts wire requests exactly.
/// </summary>
internal sealed class FakeOpenAIEngine : IDisposable
{
    private readonly TcpListener _listener;
    private readonly FakeReply _reply;
    private readonly CancellationTokenSource _stop = new();
    private int _requests;
    private readonly List<string> _bodies = [];

    public FakeOpenAIEngine(FakeReply reply)
    {
        _reply = reply;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoop();
    }

    public string Endpoint => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
    public int Requests => Volatile.Read(ref _requests);
    public IReadOnlyList<string> RequestBodies { get { lock (_bodies) return _bodies.ToArray(); } }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        using var stream = client.GetStream();
        try
        {
            var body = await ReadRequestBody(stream);
            if (body is null) return;
            Interlocked.Increment(ref _requests);
            lock (_bodies) _bodies.Add(body);

            bool streaming;
            using (var doc = JsonDocument.Parse(body))
                streaming = doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;

            if (streaming) await WriteStreaming(stream);
            else await WriteBuffered(stream);
        }
        catch (IOException) { /* client went away -- the timeout under test */ }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task<string?> ReadRequestBody(NetworkStream stream)
    {
        var buf = new byte[64 * 1024];
        var got = 0;
        int headerEnd;
        while (true)
        {
            var n = await stream.ReadAsync(buf.AsMemory(got, buf.Length - got));
            if (n == 0) return null;
            got += n;
            headerEnd = IndexOf(buf, got, "\r\n\r\n"u8);
            if (headerEnd >= 0) break;
            if (got == buf.Length) return null;
        }
        var headers = Encoding.ASCII.GetString(buf, 0, headerEnd);
        var contentLength = 0;
        foreach (var line in headers.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line["Content-Length:".Length..].Trim());
        var bodyStart = headerEnd + 4;
        var body = new byte[contentLength];
        var have = Math.Min(contentLength, got - bodyStart);
        Array.Copy(buf, bodyStart, body, 0, have);
        while (have < contentLength)
        {
            var n = await stream.ReadAsync(body.AsMemory(have, contentLength - have));
            if (n == 0) return null;
            have += n;
        }
        return Encoding.UTF8.GetString(body);
    }

    private static int IndexOf(byte[] hay, int len, ReadOnlySpan<byte> needle)
        => hay.AsSpan(0, len).IndexOf(needle);

    private IEnumerable<string> ArgumentFragments(string text)
    {
        var n = Math.Max(1, _reply.Fragments);
        var size = (int)Math.Ceiling(text.Length / (double)n);
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    private static object Chunk(object delta, string? finish) => new
    {
        id = "chatcmpl-fake",
        @object = "chat.completion.chunk",
        created = 1_700_000_000,
        model = "fake",
        choices = new[] { new { index = 0, delta, finish_reason = finish } },
    };

    private async Task WriteStreaming(NetworkStream stream)
    {
        await Write(stream,
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n"
            + "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n");

        await Event(stream, Chunk(new { role = "assistant", content = (string?)null }, null));
        if (_reply.Prose is { } prose)
        {
            foreach (var frag in ArgumentFragments(prose))
            {
                if (_reply.Gap > TimeSpan.Zero) await Task.Delay(_reply.Gap);
                await Event(stream, Chunk(new { content = frag }, null));
            }
        }
        IReadOnlyList<string> siblings = _reply.ToolName.Length == 0
            ? Array.Empty<string>()
            : (_reply.Siblings ?? new[] { _reply.Arguments });
        var sent = 0;
        for (var i = 0; i < siblings.Count; i++)
        {
            await Event(stream, Chunk(new
            {
                tool_calls = new[] { new { index = i, id = "call_fake_" + (i + 1), type = "function", function = new { name = _reply.ToolName, arguments = "" } } },
            }, null));
            foreach (var frag in ArgumentFragments(siblings[i]))
            {
                if (_reply.Gap > TimeSpan.Zero) await Task.Delay(_reply.Gap);
                await Event(stream, Chunk(new
                {
                    tool_calls = new[] { new { index = i, function = new { arguments = frag } } },
                }, null));
                sent++;
                if (_reply.SilenceAfterFragment > 0 && sent == _reply.SilenceAfterFragment)
                    await Task.Delay(_reply.Silence);
            }
        }

        await Event(stream, Chunk(new { }, siblings.Count == 0 ? "stop" : "tool_calls"));
        await Event(stream, new
        {
            id = "chatcmpl-fake",
            @object = "chat.completion.chunk",
            created = 1_700_000_000,
            model = "fake",
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = 10, completion_tokens = _reply.CompletionTokens, total_tokens = 10 + _reply.CompletionTokens },
        });
        await WriteChunk(stream, "data: [DONE]\n\n");
        await Write(stream, "0\r\n\r\n");
    }

    private async Task WriteBuffered(NetworkStream stream)
    {
        // The buffered engine "thinks" for as long as the stream would take.
        var total = _reply.Gap * Math.Max(1, _reply.Fragments)
                    + (_reply.SilenceAfterFragment > 0 ? _reply.Silence : TimeSpan.Zero);
        await Task.Delay(total);
        var json = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-fake",
            @object = "chat.completion",
            created = 1_700_000_000,
            model = "fake",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                        tool_calls = (_reply.Siblings ?? [_reply.Arguments]).Select((a, i) => new { id = "call_fake_" + (i + 1), type = "function", function = new { name = _reply.ToolName, arguments = a } }).ToArray(),
                    },
                    finish_reason = "tool_calls",
                },
            },
            usage = new { prompt_tokens = 10, completion_tokens = _reply.CompletionTokens, total_tokens = 10 + _reply.CompletionTokens },
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        await Write(stream,
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static Task Event(NetworkStream stream, object chunk)
        => WriteChunk(stream, "data: " + JsonSerializer.Serialize(chunk) + "\n\n");

    private static async Task WriteChunk(NetworkStream stream, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await Write(stream, bytes.Length.ToString("x") + "\r\n");
        await stream.WriteAsync(bytes);
        await Write(stream, "\r\n");
        await stream.FlushAsync();
    }

    private static async Task Write(NetworkStream stream, string ascii)
        => await stream.WriteAsync(Encoding.ASCII.GetBytes(ascii));

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        _stop.Dispose();
    }
}
