using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Capacity;
using Vett.Config;
using Vett.Llm;

namespace Vett.Tests;

/// <summary>
/// The opt-out for law 135 (<c>llm.stream: false</c>) must reach the wire,
/// and must survive every path a config takes to a client: Merge for member
/// seats, and CapacityBinding.Project for leased seats -- whose Clone had
/// silently dropped MaxOutputTokens before this and would have dropped
/// Stream the same way.
/// </summary>
public sealed class StreamOptOutTests
{
    [Fact]
    public async Task Stream_false_sends_the_buffered_request_and_the_old_total_time_bound_applies()
    {
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "file_editor",
            Arguments: JsonSerializer.Serialize(new { path = "a.js", content = "x" }),
            Fragments: 12,
            Gap: TimeSpan.FromMilliseconds(250),
            CompletionTokens: 10));
        using var client = ChatClientFactory.Create(new LlmConfig
        {
            Provider = "local",
            Endpoint = engine.Endpoint,
            Model = "fake",
            RequestTimeoutSeconds = 1,
            NumRetries = 0,
            Stream = false,
        });

        Assert.IsNotType<SilenceBoundedChatClient>(client);
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")]));
        Assert.IsNotType<LlmException>(ex);

        // The wire saw a buffered request: no stream:true.
        var body = Assert.Single(engine.RequestBodies);
        using var doc = JsonDocument.Parse(body);
        var streamed = doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        Assert.False(streamed, "stream:false must reach the request body");
    }

    [Fact]
    public void Merge_inherits_stream_and_lets_a_member_override_it()
    {
        var baseOff = new LlmConfig { Provider = "local", Endpoint = "http://127.0.0.1:1/v1", Model = "m", Stream = false };
        Assert.False(ChatClientFactory.Merge(baseOff, new LlmConfig { Temperature = 0.1 }).Stream);
        Assert.True(ChatClientFactory.Merge(baseOff, new LlmConfig { Stream = true }).Stream);
        Assert.Null(ChatClientFactory.Merge(new LlmConfig { Provider = "local" }, new LlmConfig { Temperature = 0.1 }).Stream);
    }

    [Fact]
    public void Project_preserves_stream_and_max_output_tokens_for_a_leased_seat()
    {
        var cfg = new LlmConfig
        {
            Provider = "local",
            Endpoint = "http://127.0.0.1:1/v1",
            Model = "m",
            Capability = "coding",
            Stream = false,
            MaxOutputTokens = 4096,
        };
        var provider = new CapabilityProvider { Name = "p", LocalityRaw = "local", Endpoint = "http://127.0.0.1:2/v1", Model = "leased" };
        var projected = CapacityBinding.Project(cfg, provider);
        Assert.False(projected.Stream);
        Assert.Equal(4096, projected.MaxOutputTokens);
        Assert.Equal("leased", projected.Model);
    }
}
