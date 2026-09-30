using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Tests;

/// <summary>
/// ⭐⭐ THE CAP WAS DISCLOSED TO THE SEAT THAT DOES NOT WRITE FILES.
///
/// THE DEFECT (measured 2026-09-05, EpicForge run 5, `ef-team-flash`,
/// `max_output_tokens: 4096`, two teams on two different vett binaries).
/// EpicForge's team brief opens with an `OUTPUT BUDGET:` paragraph that names
/// the cap and says a reply cut before its tool call is discarded whole. It is
/// the brief's first paragraph, and it reaches exactly one seat:
///
///     T-mtnxznn14c4k   lead 25/25 requests carry it   implementer 0/27
///     T-mtnzll1wxcor   lead 11/11 requests carry it   implementer 0/8
///
/// The lead's `assign_task(member, task)` REWRITES the brief, and the member's
/// only file tool is `terminal`, so the member writes a whole file as one
/// heredoc inside one tool call's JSON — the exact shape the cap cuts
/// mid-arguments. Run 4's facts tasks died that way at 12288 tokens with
/// 44–55k chars of `file_editor create` markup; run 5's ordinary seats died
/// at 4096 with 15–17k chars of heredoc. The seat that emits the file had
/// never been told a cap existed.
///
/// ⛔ WHY THE FIX LIVES HERE AND NOT IN THE BRIEF. The brief is one caller's
/// user message and every delegation rewrites it. The number lives in
/// <see cref="LlmSettings.MaxOutputTokens"/> — the same value the loop installs
/// on the wire at `ChatOptions.MaxOutputTokens` — and every seat (solo, leader,
/// member, replay) enters through <see cref="AgentLoop.RunAsync"/> or
/// <see cref="AgentLoop.RunInteractiveAsync"/>, which build the system message.
/// So the notice is derived from the one number that IS the cap and attached
/// to the one message no delegation rewrites. No caller types a digit; a
/// profile that sets no cap gets no notice, because the provider's limit is
/// unknown and no number beats a wrong one (the same rule as the truncation
/// nudge that already reads this field).
///
/// The assertions read the FIRST REQUEST'S SYSTEM MESSAGE — what the model
/// was shown — not the helper's return value, because a helper that returns
/// the right string and is wired to neither entry point is the defect with a
/// green test on top.
/// </summary>
public sealed class OutputCapNoticeTests
{
    private sealed class NoSandbox : ISandbox
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

    /// <summary>Records every request's message list and answers in prose, so
    /// each run is exactly one LLM call and the system message is inspectable.</summary>
    private sealed class RecordingClient : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = new();
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (Requests) Requests.Add(messages.ToList());
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")]));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static AgentCapabilities NoTools() =>
        new(new Dictionary<string, ToolFn>(), new List<System.Text.Json.JsonElement>(), new List<MiddlewareFn>());

    // MaxIterations 1: a prose reply with no tool call is nudged and re-asked
    // until the iteration budget runs out, so the budget IS the request count.
    private static AgentEnvironment Env() => new(new NoSandbox(), "sess", 1, _ => { });

    private const string Sys = "You are a careful engineer. Complete the task.";

    private static string SystemTextOf(List<ChatMessage> request)
    {
        Assert.NotEmpty(request);
        Assert.Equal(ChatRole.System, request[0].Role);
        return request[0].Text ?? "";
    }

    /// <summary>Every maximal digit run in <paramref name="s"/>.</summary>
    private static List<string> DigitRuns(string s)
    {
        var runs = new List<string>();
        var cur = "";
        foreach (var c in s)
        {
            if (char.IsDigit(c)) cur += c;
            else if (cur.Length > 0) { runs.Add(cur); cur = ""; }
        }
        if (cur.Length > 0) runs.Add(cur);
        return runs;
    }

    // ---- 1. the one-shot path every team member and every replay uses --------

    [Fact]
    public async Task RunAsync_WithACap_TheFirstRequestsSystemMessageNamesIt()
    {
        var client = new RecordingClient();
        await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model", 1.0, null, 4096), NoTools(), Env(), Sys, "go");

        Assert.Single(client.Requests);
        var sys = SystemTextOf(client.Requests[0]);

        // The caller's prompt survives, verbatim, at the front.
        Assert.StartsWith(Sys, sys);
        // The notice is there, and it carries the number.
        Assert.Contains("OUTPUT CAP", sys);
        Assert.Contains("4096", sys);
        // And it says what the number DOES -- a bare number is trivia.
        Assert.Contains("tool call", sys);
        Assert.Contains("discarded", sys);
        Assert.Contains("several", sys);

        // No invented digit: every number in the notice IS the cap. A typed
        // constant here would drift from the wire the first time a profile
        // changed its cap (the bridge's own budget line had exactly that risk
        // and read the profile to avoid it).
        var notice = sys.Substring(Sys.Length);
        var runs = DigitRuns(notice);
        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.Equal("4096", r));
    }

    [Fact]
    public async Task RunAsync_WithoutACap_TheSystemMessageIsTheCallersPromptAndNothingElse()
    {
        var client = new RecordingClient();
        await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model", 1.0, null, null), NoTools(), Env(), Sys, "go");

        var sys = SystemTextOf(client.Requests[0]);
        Assert.Equal(Sys, sys);
        Assert.DoesNotContain("OUTPUT CAP", sys);
    }

    [Fact]
    public async Task RunAsync_TheNumberIsTheCapThatWasSet_NotAFixedOne()
    {
        // 4096 and 12288 are both live values (team seats vs. the solo/wide
        // profiles). A notice that passed for one and hard-coded it would fail
        // the other; this pins the notice to the setting, whichever it is.
        var client = new RecordingClient();
        await AgentLoop.RunAsync(
            new LlmSettings(client, "test-model", 1.0, null, 12288), NoTools(), Env(), Sys, "go");

        var sys = SystemTextOf(client.Requests[0]);
        Assert.Contains("12288", sys);
        Assert.DoesNotContain("4096", sys);
    }

    // ---- 2. the interactive path the leader and `vett chat` use -------------

    [Fact]
    public async Task RunInteractiveAsync_WithACap_TheFirstRequestsSystemMessageNamesIt()
    {
        var client = new RecordingClient();
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite("go");
        ch.Writer.Complete();

        var result = await AgentLoop.RunInteractiveAsync(
            new LlmSettings(client, "test-model", 1.0, null, 4096), NoTools(), Env(),
            Sys, ch.Reader, onAssistantText: null, onWaitingForInput: null, CancellationToken.None);

        Assert.Equal("user_closed", result.StopReason);
        Assert.NotEmpty(client.Requests);
        var sys = SystemTextOf(client.Requests[0]);
        Assert.StartsWith(Sys, sys);
        Assert.Contains("OUTPUT CAP", sys);
        Assert.Contains("4096", sys);
    }

    // ---- 3. a continued member does not get the notice twice ----------------

    [Fact]
    public async Task ContinueAsync_CarriesTheNoticeOnceAndDoesNotStackASecond()
    {
        // ContinueAsync clones the prior state's messages, whose system message
        // already carries the notice. A second injection would grow the system
        // prompt by one paragraph per continue_task, and a leader that
        // continues a member ten times would ship ten copies.
        var client = new RecordingClient();
        var llm = new LlmSettings(client, "test-model", 1.0, null, 4096);
        var first = await AgentLoop.RunAsync(llm, NoTools(), Env(), Sys, "go");
        var afterFirst = client.Requests.Count;
        Assert.True(afterFirst >= 1);
        // The same shape Coordinator.continue_task builds from the stored member.
        var prior = new AgentState { Messages = first.Messages, MaxIterations = 1 };
        await AgentLoop.ContinueAsync(llm, NoTools(), Env(), prior, "and again");

        Assert.True(client.Requests.Count > afterFirst, "the continuation made no request");
        var sys = SystemTextOf(client.Requests[afterFirst]);
        Assert.Equal(1, CountOf(sys, "OUTPUT CAP"));
    }

    [Fact]
    public void WithOutputCapNotice_IsIdempotent()
    {
        var once = AgentLoop.WithOutputCapNotice(Sys, 4096);
        var twice = AgentLoop.WithOutputCapNotice(once, 4096);
        Assert.Equal(once, twice);
        Assert.Equal(1, CountOf(twice, "OUTPUT CAP"));
        Assert.Equal(Sys, AgentLoop.WithOutputCapNotice(Sys, null));
    }

    private static int CountOf(string hay, string needle)
    {
        var n = 0;
        for (var i = hay.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = hay.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
