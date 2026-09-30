using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// presence_penalty / frequency_penalty travel from the profile YAML, through
/// LlmSettings, into the ChatOptions the loop hands the chat client. Each test
/// carries a CONTROL with the setting absent, so a stuck plumbing that always
/// emitted 0.8 could not pass.
///
/// Why these exist (EpicForge run 6, 2026-09-05): at temperature 0.3 with no
/// penalty a seat asked to replace ONE duplicate line re-issued the identical
/// replacement five times and then filled a 4096-token reply with a repeating
/// monologue and no tool call. 33 of 33 capped replies across 36 team logs had
/// that shape. Replaying the exact failing requests against the same engine:
/// no penalty 3/4 capped, temperature 0.8 2/2 capped, presence 0.8 +
/// frequency 0.3 0/4 capped -- every one reached a tool call.
/// </summary>
public sealed class PenaltyPlumbingTests
{
    private sealed class CapturingClient : IChatClient
    {
        public readonly List<ChatOptions?> Seen = new();
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Seen.Add(options);
            return Task.FromResult(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, "nothing to do; stopping.")]));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class InertSandbox : ISandbox
    {
        public string Cwd => "/fake";
        public Task<BashResult> BashExecAsync(string s, string cmd, int t = 60, CancellationToken ct = default)
            => Task.FromResult(new BashResult("", 0, "/fake", false));
        public Task<string> FileViewAsync(string s, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileCreateAsync(string s, string p, string f, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string, string?)> FileStrReplaceAsync(string s, string p, string o, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileInsertAsync(string s, string p, int l, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> FileUndoAsync(string s, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SessionCreateAsync(string n, string cwd, CancellationToken ct = default) => Task.CompletedTask;
        public Task SessionDestroyAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
        public ISandbox WithCwd(string cwd) => this;
        public ISandbox WithDispatchWorktree(string newCwd, string root) => this;
    }

    private static AgentCapabilities EmptyCaps() => new(
        new Dictionary<string, ToolFn>(),
        new List<JsonElement>(),
        new List<MiddlewareFn>());

    // ---- 1. LlmSettings -> ChatOptions (the loop's outbound request) -------

    [Fact]
    public async Task PenaltiesOnLlmSettings_ReachTheChatOptionsTheLoopSends()
    {
        var client = new CapturingClient();
        var env = new AgentEnvironment(new InertSandbox(), "sess", 2);

        await AgentLoop.RunAsync(
            new LlmSettings(client, "m", 0.3, 0.95, 4096, PresencePenalty: 0.8, FrequencyPenalty: 0.3),
            EmptyCaps(), env, "sys", "do the thing");

        var opts = Assert.Single(client.Seen.Take(1));
        Assert.NotNull(opts);
        Assert.Equal(0.8f, opts!.PresencePenalty);
        Assert.Equal(0.3f, opts.FrequencyPenalty);
        Assert.Equal(0.3f, opts.Temperature);
        Assert.Equal(0.95f, opts.TopP);
        Assert.Equal(4096, opts.MaxOutputTokens);
    }

    [Fact]
    public async Task Control_NoPenaltiesOnLlmSettings_LeavesBothNullOnTheWire()
    {
        var client = new CapturingClient();
        var env = new AgentEnvironment(new InertSandbox(), "sess", 2);

        await AgentLoop.RunAsync(
            new LlmSettings(client, "m", 0.3, 0.95, 4096),
            EmptyCaps(), env, "sys", "do the thing");

        var opts = Assert.Single(client.Seen.Take(1));
        Assert.NotNull(opts);
        Assert.Null(opts!.PresencePenalty);
        Assert.Null(opts.FrequencyPenalty);
    }

    // ---- 2. YAML -> LlmConfig (profile level and member level) --------------

    [Fact]
    public void ProfileYaml_PenaltiesParse_AtProfileAndMemberLevel_AndStayNullWhenAbsent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vett-penalty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var with = Path.Combine(dir, "with.yaml");
            File.WriteAllText(with,
                "name: p\n" +
                "llm:\n  model: m\n  temperature: 0.3\n  presence_penalty: 0.8\n  frequency_penalty: 0.3\n" +
                "team:\n  leader:\n    name: lead\n  members:\n" +
                "    - name: implementer\n      llm:\n        presence_penalty: 0.2\n");
            var p = Yaml.LoadProfile(with);
            Assert.Equal(0.8, p.Llm.PresencePenalty);
            Assert.Equal(0.3, p.Llm.FrequencyPenalty);
            var member = Assert.Single(p.Team!.Members);
            Assert.Equal(0.2, member.Llm!.PresencePenalty);
            // The member did not set frequency_penalty: null here is what lets the
            // Coordinator's `m.Llm?.X ?? profile.Llm.X` fall back to the profile.
            Assert.Null(member.Llm.FrequencyPenalty);

            var without = Path.Combine(dir, "without.yaml");
            File.WriteAllText(without, "name: p\nllm:\n  model: m\n  temperature: 0.3\n");
            var c = Yaml.LoadProfile(without);
            Assert.Null(c.Llm.PresencePenalty);
            Assert.Null(c.Llm.FrequencyPenalty);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
