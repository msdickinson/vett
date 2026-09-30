using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Agent;
using Vett.Config;
using Vett.Llm;

namespace Vett.Tests;

/// <summary>
/// ⛔ A CUT TOOL CALL ARRIVING OVER THE STREAM CARRIED NO RAW ARGUMENTS, SO
/// THE CUT-CREATE SALVAGE (law 229) NEVER FIRED IN PRODUCTION (EpicForge
/// batch 7 arm s7c, 2026-09-09 05:12Z, law 230): the live event read
/// <c>raw_argument_chars: null</c> at output_tokens == output_cap while the
/// parser had consumed 33,951 bytes. The buffered OpenAI adapter parks the
/// wire tool call in <see cref="FunctionCallContent.RawRepresentation"/>; the
/// streaming adapter parses privately and keeps only the exception. Every
/// EpicForge profile streams.
///
/// These tests drive the REAL factory client (streaming, silence-bounded)
/// against the in-process fake engine from <see cref="SilenceBoundedStreamingTests"/>.
/// FAIL-FIRST: <see cref="A_cut_tool_call_over_the_stream_keeps_the_bytes_that_arrived"/>
/// is RED before the repair (RawRepresentation null); the parseable control
/// gains a raw string too and its parsed arguments are unchanged.
///
/// Law 231 (batch 8, 05:30-05:40Z): with the bytes on the wire, every cut
/// was a repetition loop that had paid out the whole cap first.
/// <see cref="A_repeating_argument_stream_is_aborted_before_the_cap"/> is
/// RED before that repair (the fake's whole 55 K loop arrives, no abort);
/// the distinct-content control of the same size streams whole.
///
/// Law 232 (run-11 architect A-mttn9m3j2vz2, 05:14-05:44Z): the loop was
/// ACROSS sibling calls -- 99 short <c>find | head -N000</c> calls to the
/// cap, twice, 26 of the pass's 30 minutes.
/// <see cref="A_series_of_repeating_sibling_calls_is_aborted"/> is RED before
/// that repair (all 60 siblings arrive, parsed, finish tool_calls); the
/// distinct-series control streams whole. And because leaving the stream
/// early loses the adapter's tool calls, the complete siblings BEFORE a
/// looping call are rebuilt from their raw text
/// (<see cref="Complete_siblings_before_an_aborted_call_are_kept"/>).
/// </summary>
public sealed class StreamedRawArgumentsTests
{
    private static LlmConfig Local(FakeOpenAIEngine engine) => new()
    {
        Provider = "local",
        Endpoint = engine.Endpoint,
        Model = "fake",
        RequestTimeoutSeconds = 5,
        NumRetries = 0,
    };

    private static readonly ChatMessage[] Prompt = [new ChatMessage(ChatRole.User, "write the file")];

    private static string Create(string body)
        => JsonSerializer.Serialize(new { command = "create", path = "src/big.js", file_text = body, security_risk = "LOW" });

    private static string DistinctCreate(int lines)
        => Create(string.Join("\n", Enumerable.Range(0, lines).Select(i => $"export const v{i} = {i * 7 + 3}; // line {i}")));

    /// <summary>Lines no deflate window can fold: seeded random base64, so the
    /// tail reads ~50% like real code, never near the abort bound.</summary>
    private static string IncompressibleCreate(int lines)
    {
        var rng = new Random(20260909);
        var buf = new byte[36];
        return Create(string.Join("\n", Enumerable.Range(0, lines).Select(i =>
        {
            rng.NextBytes(buf);
            return $"export const s{i} = \"{Convert.ToBase64String(buf)}\";";
        })));
    }

    /// <summary>The live shape of law 231: one fact line over and over inside
    /// a create's file_text, 55 K characters of it (s8a ran to 45 K).</summary>
    private static string LoopingCreate()
        => Create(string.Concat(Enumerable.Repeat("Saturn's page is a real Saturn page.\n", 1500)));

    private const string RulersDir = "C:/work/rulers/";

    /// <summary>The live series of law 232, verbatim in shape: the run-11
    /// architect's sibling calls differed only in the number after head.</summary>
    private static string[] PagingSeries(int calls)
        => Enumerable.Range(1, calls).Select(n => JsonSerializer.Serialize(new
        {
            command = $"find \"{RulersDir}\" -type f 2>/dev/null | head -{n}000",
            security_risk = "LOW",
            summary = "Find all files in rulers directory",
        })).ToArray();

    /// <summary>Law 245: s15h it.18 as it ran -- one test command re-issued
    /// verbatim, call after call, to the ruler.</summary>
    private static string[] LiteralSeries(int calls)
        => Enumerable.Repeat(JsonSerializer.Serialize(new
        {
            command = $"cd \"{RulersDir}\" && node --test test/game-*.test.js 2>&1 | grep -E \"not ok|error:|actual:|expected:\" | head -40",
            security_risk = "LOW",
            summary = "Show failing test summary",
        }), calls).ToArray();

    /// <summary>A genuine fan-out: the same long path, a different pattern
    /// and purpose every call (the pass's real 9-call grep fan-out read 17%).</summary>
    private static string[] DistinctSeries(int calls)
    {
        var rng = new Random(20260909);
        var buf = new byte[24];
        return Enumerable.Range(0, calls).Select(i =>
        {
            rng.NextBytes(buf);
            var pattern = Convert.ToBase64String(buf);
            return JsonSerializer.Serialize(new
            {
                command = $"grep -n \"{pattern}\" \"{RulersDir}blocks-saturn/verify.js\"",
                security_risk = "LOW",
                summary = $"Search verify.js for {pattern[..8]} (probe {i})",
            });
        }).ToArray();
    }

    [Fact]
    public async Task A_cut_tool_call_over_the_stream_keeps_the_bytes_that_arrived()
    {
        // The engine's cap lands mid-string: the JSON never closes, exactly the
        // shape the live event carried (parse_error "Expected end of string").
        var full = DistinctCreate(60);
        var cut = full[..(full.Length - 90)];
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "file_editor", Arguments: cut, Fragments: 17,
            Gap: TimeSpan.FromMilliseconds(5), CompletionTokens: 8192));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var call = Assert.Single(resp.Messages.Single().Contents.OfType<FunctionCallContent>());
        Assert.Equal("file_editor", call.Name);
        Assert.NotNull(call.Exception);
        var raw = Assert.IsType<string>(call.RawRepresentation);
        Assert.Equal(cut, raw);

        // And the salvage now has something to bank: the complete lines.
        var salvage = AgentLoop.SalvageCutCreate(call.Name, raw, AgentLoop.TailCompressionPct(raw));
        Assert.NotNull(salvage);
        Assert.Equal("src/big.js", salvage!.Path);
        Assert.False(salvage.Complete);
        Assert.True(salvage.Lines > 40, "lines banked: " + salvage.Lines);
    }

    [Fact]
    public async Task A_complete_tool_call_over_the_stream_keeps_its_parsed_arguments_and_gains_the_raw_text()
    {
        var full = DistinctCreate(12);
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "file_editor", Arguments: full, Fragments: 5,
            Gap: TimeSpan.FromMilliseconds(5), CompletionTokens: 900));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var call = Assert.Single(resp.Messages.Single().Contents.OfType<FunctionCallContent>());
        Assert.Null(call.Exception);
        Assert.Equal("src/big.js", call.Arguments!["path"]?.ToString());
        Assert.Equal(full, Assert.IsType<string>(call.RawRepresentation));
    }

    [Fact]
    public void Attach_matches_by_call_id_first_and_never_overwrites_a_present_raw()
    {
        var calls = new Dictionary<int, SilenceBoundedChatClient.StreamedCall>
        {
            [0] = new() { CallId = "c-a" },
            [1] = new() { CallId = "c-b" },
        };
        calls[0].Text.Append("{\"a\":1");
        calls[1].Text.Append("{\"b\":2");
        var keep = new FunctionCallContent("c-b", "t", null) { RawRepresentation = "already" };
        var fill = new FunctionCallContent("c-a", "t", null);
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, [keep, fill]));

        SilenceBoundedChatClient.AttachRawArguments(resp, calls);

        Assert.Equal("already", keep.RawRepresentation);
        Assert.Equal("{\"a\":1", fill.RawRepresentation);
    }

    [Fact]
    public async Task A_repeating_argument_stream_is_aborted_before_the_cap()
    {
        var full = LoopingCreate();
        Assert.True(full.Length > 50_000, "fixture size: " + full.Length);
        var fragments = full.Length / 1000;
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "file_editor", Arguments: full, Fragments: fragments,
            Gap: TimeSpan.FromMilliseconds(5), CompletionTokens: 12288));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var msg = resp.Messages.Last();
        var call = Assert.Single(msg.Contents.OfType<FunctionCallContent>());
        Assert.Equal("file_editor", call.Name);
        Assert.Equal("call_fake_1", call.CallId);
        Assert.Null(call.Arguments);
        var abort = Assert.IsType<RepeatingArgumentsException>(call.Exception);
        var raw = Assert.IsType<string>(call.RawRepresentation);
        Assert.Equal(raw.Length, abort.Chars);
        Assert.Equal(0, abort.Siblings);
        Assert.True(abort.TailPct < SilenceBoundedChatClient.LoopAbortTailPct, "tail pct " + abort.TailPct);
        Assert.True(raw.Length >= SilenceBoundedChatClient.LoopAbortMinChars, "aborted too early: " + raw.Length);
        Assert.True(raw.Length < full.Length / 2, "the loop paid out: " + raw.Length + " of " + full.Length);
        Assert.StartsWith(raw, full);
        Assert.Equal(ChatFinishReason.Length, resp.FinishReason);

        // Downstream contract: AgentLoop sees exactly a cap-cut call and
        // refuses to salvage a loop.
        var cut = AgentLoop.FindCutTailToolCall(msg, [call], resp.FinishReason, 0, 12288, []);
        Assert.Same(call, cut);
        Assert.Null(AgentLoop.SalvageCutCreate(call.Name, raw, AgentLoop.TailCompressionPct(raw)));
    }

    [Fact]
    public async Task A_long_distinct_argument_stream_is_never_aborted()
    {
        var full = IncompressibleCreate(500);
        Assert.True(full.Length > 2 * SilenceBoundedChatClient.LoopAbortMinChars, "fixture size: " + full.Length);
        var pct = ArgumentTail.CompressionPct(full);
        Assert.True(pct > 3 * SilenceBoundedChatClient.LoopAbortTailPct, "control not distinct enough: " + pct);
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "file_editor", Arguments: full, Fragments: full.Length / 1000,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 9000));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var call = Assert.Single(resp.Messages.Single().Contents.OfType<FunctionCallContent>());
        Assert.Null(call.Exception);
        Assert.Equal("src/big.js", call.Arguments!["path"]?.ToString());
        Assert.Equal(full, Assert.IsType<string>(call.RawRepresentation));
        Assert.Equal(ChatFinishReason.ToolCalls, resp.FinishReason);
    }

    [Fact]
    public async Task Complete_siblings_before_an_aborted_call_are_kept()
    {
        // A short honest create, then the loop, in ONE reply. The adapter
        // emits a reply's calls only at the end of the stream, so without the
        // rebuild the honest call would vanish with the abort.
        var first = DistinctCreate(12);
        var loop = LoopingCreate();
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "file_editor", Arguments: "", Fragments: 40,
            Gap: TimeSpan.FromMilliseconds(3), CompletionTokens: 12288,
            Siblings: [first, loop]));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var msg = resp.Messages.Last();
        var calls = msg.Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("call_fake_1", calls[0].CallId);
        Assert.Null(calls[0].Exception);
        Assert.Equal("src/big.js", calls[0].Arguments!["path"]?.ToString());
        Assert.Equal(first, Assert.IsType<string>(calls[0].RawRepresentation));

        Assert.Equal("call_fake_2", calls[1].CallId);
        var abort = Assert.IsType<RepeatingArgumentsException>(calls[1].Exception);
        Assert.Equal(0, abort.Siblings);
        Assert.Equal(ChatFinishReason.Length, resp.FinishReason);
        Assert.Same(calls[1], AgentLoop.FindCutTailToolCall(msg, calls, resp.FinishReason, 0, 12288, []));
    }

    [Fact]
    public async Task A_series_of_repeating_sibling_calls_is_aborted()
    {
        var series = PagingSeries(60);
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "terminal", Arguments: "", Fragments: 2,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 12288,
            Siblings: series));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var msg = resp.Messages.Last();
        var calls = msg.Contents.OfType<FunctionCallContent>().ToList();
        // Law 247: every member is distinct, so this is not a literal repeat
        // (Distinct 0) -- but the LEADING three reads run, the rest are dropped.
        Assert.Equal(SilenceBoundedChatClient.LiteralSiblingLoopMaxDistinct + 1, calls.Count);
        for (var i = 0; i < SilenceBoundedChatClient.LiteralSiblingLoopMaxDistinct; i++)
        {
            Assert.Null(calls[i].Exception);
            Assert.Equal("call_fake_" + (i + 1), calls[i].CallId);
            Assert.Contains("head -" + (i + 1) + "000", calls[i].Arguments!["command"]?.ToString());
        }
        var call = calls[^1];
        Assert.Equal("terminal", call.Name);
        Assert.Null(call.Arguments);
        var abort = Assert.IsType<RepeatingArgumentsException>(call.Exception);
        Assert.True(abort.Siblings >= SilenceBoundedChatClient.SiblingLoopMinCalls, "fired early: " + abort.Siblings);
        Assert.True(abort.Siblings < 40, "fired late: " + abort.Siblings + " of " + series.Length);
        Assert.Equal("call_fake_" + abort.Siblings, call.CallId);
        Assert.True(abort.TailPct < SilenceBoundedChatClient.LoopAbortTailPct, "tail pct " + abort.TailPct);
        var raw = Assert.IsType<string>(call.RawRepresentation);
        Assert.Equal(raw.Length, abort.Chars);
        Assert.Contains("head -1000", raw);
        Assert.Contains("head -" + (abort.Siblings - 1) + "000", raw);
        Assert.Equal(ChatFinishReason.Length, resp.FinishReason);
        Assert.Same(call, AgentLoop.FindCutTailToolCall(msg, calls, resp.FinishReason, 0, 12288, []));
        Assert.Contains("sibling calls", abort.Message);
        Assert.Equal(0, abort.Distinct);
        Assert.Equal(SilenceBoundedChatClient.LiteralSiblingLoopMaxDistinct, abort.Ran);
        Assert.Contains("the first 3 of them ran", abort.Message);
    }

    [Fact]
    public async Task A_literal_series_of_one_repeated_call_runs_its_first_copy()
    {
        var series = LiteralSeries(40);
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "terminal", Arguments: "", Fragments: 2,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 12288,
            Siblings: series));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var msg = resp.Messages.Last();
        var calls = msg.Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("call_fake_1", calls[0].CallId);
        Assert.Null(calls[0].Exception);
        Assert.StartsWith("cd ", calls[0].Arguments!["command"]?.ToString());
        Assert.Equal(series[0], Assert.IsType<string>(calls[0].RawRepresentation));

        var abort = Assert.IsType<RepeatingArgumentsException>(calls[1].Exception);
        // The clock half: cut at the call after LiteralRepeatAbortAfter identical ones, not at 12.
        Assert.Equal(SilenceBoundedChatClient.LiteralRepeatAbortAfter + 1, abort.Siblings);
        Assert.Equal("call_fake_" + abort.Siblings, calls[1].CallId);
        Assert.Equal(1, abort.Distinct);
        Assert.Contains("the same call", abort.Message);
        Assert.Equal(ChatFinishReason.Length, resp.FinishReason);
        Assert.Same(calls[1], AgentLoop.FindCutTailToolCall(msg, calls, resp.FinishReason, 0, 12288, []));
    }

    [Fact]
    public async Task A_literal_series_of_two_alternating_calls_runs_each_once()
    {
        var a = LiteralSeries(1)[0];
        var b = JsonSerializer.Serialize(new { command = "cat src/game/score.js", security_risk = "LOW", summary = "View score.js" });
        var series = Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? a : b).ToArray();
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "terminal", Arguments: "", Fragments: 2,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 12288,
            Siblings: series));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var calls = resp.Messages.Last().Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(3, calls.Count);
        Assert.Equal(a, Assert.IsType<string>(calls[0].RawRepresentation));
        Assert.Equal(b, Assert.IsType<string>(calls[1].RawRepresentation));
        Assert.Equal("call_fake_2", calls[1].CallId);
        var abort = Assert.IsType<RepeatingArgumentsException>(calls[2].Exception);
        // Alternating, so never four identical in a row; law 247 cuts it once
        // RepeatedSiblingsAbortAfter complete siblings hold at most three keys
        // (it paid the full 12 calls to the ruler before).
        Assert.Equal(SilenceBoundedChatClient.RepeatedSiblingsAbortAfter + 1, abort.Siblings);
        Assert.Equal("call_fake_" + abort.Siblings, calls[2].CallId);
        Assert.Equal(2, abort.Distinct);
        Assert.Equal(2, abort.Ran);
        Assert.Contains("the same 2 calls", abort.Message);
    }

    /// <summary>Law 247, s16f it.15 as it ran: a complete create, then one
    /// call re-issued with a re-worded `summary` every time. The literal key
    /// of law 245 read the summary, so every copy was distinct, the whole
    /// series was a paraphrase loop, and the create was dropped with it.</summary>
    [Fact]
    public async Task A_reworded_repeat_after_a_create_is_literal_and_the_create_runs()
    {
        var create = JsonSerializer.Serialize(new
        {
            command = "create", path = "test/game-collision.test.js", security_risk = "LOW",
            file_text = "import { test } from 'node:test';\nimport assert from 'node:assert';\ntest('x', () => assert.ok(true));\n",
        });
        var reworded = Enumerable.Range(1, 30).Select(i => JsonSerializer.Serialize(new
        {
            command = "view", path = "test/game-collision.test.js", security_risk = "LOW",
            summary = i % 2 == 0 ? $"Run band 10 tests (attempt {i})" : $"Verify the collision test, pass {i}",
        }));
        var series = new[] { create }.Concat(reworded).ToArray();
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "file_editor", Arguments: "", Fragments: 2,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 12288,
            Siblings: series));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var calls = resp.Messages.Last().Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(3, calls.Count);
        Assert.Equal("call_fake_1", calls[0].CallId);
        Assert.Null(calls[0].Exception);
        Assert.Equal("create", calls[0].Arguments!["command"]?.ToString());
        Assert.Equal(create, Assert.IsType<string>(calls[0].RawRepresentation));
        Assert.Equal("call_fake_2", calls[1].CallId);
        Assert.Equal("view", calls[1].Arguments!["command"]?.ToString());
        var abort = Assert.IsType<RepeatingArgumentsException>(calls[2].Exception);
        // The clock half: the create, four re-worded copies, cut at the sixth call.
        Assert.Equal(SilenceBoundedChatClient.LiteralRepeatAbortAfter + 2, abort.Siblings);
        Assert.Equal(2, abort.Distinct);
        Assert.Equal(2, abort.Ran);
        // Law 249 instrumentation: the create is 1, the first view is 2, the
        // first re-worded view (same key once `summary` is dropped) is 3.
        Assert.Equal(3, abort.FirstRepeat);
        Assert.Equal(ChatFinishReason.Length, resp.FinishReason);
    }

    /// <summary>Law 247, the clock half: three calls in rotation never trip
    /// the four-identical cut; once eight complete siblings hold three keys
    /// the stream is cut, and each of the three runs once.</summary>
    [Fact]
    public async Task A_rotation_of_three_calls_is_cut_after_eight_and_each_runs_once()
    {
        var three = new[]
        {
            LiteralSeries(1)[0],
            JsonSerializer.Serialize(new { command = "cat src/game/score.js", security_risk = "LOW", summary = "View score.js" }),
            JsonSerializer.Serialize(new { command = "cat src/game/clear.js", security_risk = "LOW", summary = "View clear.js" }),
        };
        var series = Enumerable.Range(0, 30).Select(i => three[i % 3]).ToArray();
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "terminal", Arguments: "", Fragments: 2,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 12288,
            Siblings: series));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var calls = resp.Messages.Last().Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(4, calls.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(calls[i].Exception);
            Assert.Equal(three[i], Assert.IsType<string>(calls[i].RawRepresentation));
        }
        var abort = Assert.IsType<RepeatingArgumentsException>(calls[3].Exception);
        Assert.Equal(SilenceBoundedChatClient.RepeatedSiblingsAbortAfter + 1, abort.Siblings);
        Assert.Equal(3, abort.Distinct);
        Assert.Equal(3, abort.Ran);
        Assert.Contains("the same 3 calls", abort.Message);
    }

    /// <summary>Law 247, s16g it.13 as it ran: one distinct grep, then a
    /// series every member of which differs (the paging shape), to the
    /// ruler. Not a literal repeat -- but the grep and the first two reads
    /// run; before the repair none did.</summary>
    [Fact]
    public async Task A_paraphrase_series_runs_its_leading_distinct_calls()
    {
        var grep = JsonSerializer.Serialize(new
        {
            command = "node --test test/game-*.test.js 2>&1 | grep -E \"not ok|AssertionError|expected|actual\" | head -40",
            security_risk = "LOW",
            summary = "Show failing tests details",
        });
        var series = new[] { grep }.Concat(PagingSeries(60)).ToArray();
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "terminal", Arguments: "", Fragments: 2,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 12288,
            Siblings: series));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var calls = resp.Messages.Last().Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(SilenceBoundedChatClient.LiteralSiblingLoopMaxDistinct + 1, calls.Count);
        Assert.Equal(grep, Assert.IsType<string>(calls[0].RawRepresentation));
        Assert.StartsWith("node --test", calls[0].Arguments!["command"]?.ToString());
        Assert.Contains("head -1000", calls[1].Arguments!["command"]?.ToString());
        Assert.Contains("head -2000", calls[2].Arguments!["command"]?.ToString());
        var abort = Assert.IsType<RepeatingArgumentsException>(calls[3].Exception);
        Assert.True(abort.Siblings >= SilenceBoundedChatClient.SiblingLoopMinCalls, "fired early: " + abort.Siblings);
        Assert.Equal(0, abort.Distinct);
        Assert.Equal(3, abort.Ran);
        Assert.Equal(ChatFinishReason.Length, resp.FinishReason);
    }

    /// <summary>Law 249, s17b it.12 as it ran: twelve distinct calls (the
    /// plan), then one test command five times to the four-identical cut.
    /// Every call of the plan runs, and the first copy of the test command
    /// with it; before the repair three ran and nine writes were lost.</summary>
    [Fact]
    public async Task A_plan_with_a_looping_tail_runs_the_whole_plan()
    {
        var plan = DistinctSeries(12);
        var tail = LiteralSeries(6);
        var series = plan.Concat(tail).ToArray();
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "terminal", Arguments: "", Fragments: 2,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 12288,
            Siblings: series));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var calls = resp.Messages.Last().Contents.OfType<FunctionCallContent>().ToList();
        // 12 plan calls + the first copy of the tail + the cut call.
        Assert.Equal(plan.Length + 2, calls.Count);
        for (var i = 0; i < plan.Length; i++)
        {
            Assert.Null(calls[i].Exception);
            Assert.Equal(plan[i], Assert.IsType<string>(calls[i].RawRepresentation));
            Assert.Equal("call_fake_" + (i + 1), calls[i].CallId);
        }
        Assert.Equal(tail[0], Assert.IsType<string>(calls[plan.Length].RawRepresentation));
        Assert.Null(calls[plan.Length].Exception);
        var abort = Assert.IsType<RepeatingArgumentsException>(calls[^1].Exception);
        Assert.Equal(0, abort.Distinct);
        Assert.Equal(plan.Length + 1, abort.Ran);
        Assert.Contains("the first " + (plan.Length + 1) + " of them ran", abort.Message);
        Assert.Equal(ChatFinishReason.Length, resp.FinishReason);
    }

    /// <summary>Law 249's guard: a paging series whose LAST page happens to
    /// be re-issued has a repeated key, but its leading segment deflates
    /// like the loop it is -- the cap of three stands.</summary>
    [Fact]
    public async Task A_paging_series_ending_in_a_repeat_is_still_capped_at_three()
    {
        var pages = PagingSeries(24);
        var leadingPct = ArgumentTail.CompressionPct(string.Join("\n", pages) + "\n");
        Assert.True(leadingPct < SilenceBoundedChatClient.LoopAbortTailPct, "premise: the pages must deflate as a loop, got " + leadingPct);
        var series = pages.Concat(Enumerable.Repeat(pages[^1], 6)).ToArray();
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "terminal", Arguments: "", Fragments: 2,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 12288,
            Siblings: series));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var calls = resp.Messages.Last().Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(SilenceBoundedChatClient.LiteralSiblingLoopMaxDistinct + 1, calls.Count);
        var abort = Assert.IsType<RepeatingArgumentsException>(calls[^1].Exception);
        Assert.Equal(SilenceBoundedChatClient.LiteralSiblingLoopMaxDistinct, abort.Ran);
    }

    [Fact]
    public void The_plan_length_is_the_index_of_the_first_repeated_key()
    {
        static SilenceBoundedChatClient.StreamedCall Call(string text)
        {
            var c = new SilenceBoundedChatClient.StreamedCall { Name = "terminal" };
            c.Text.Append(text);
            return c;
        }
        var plan = DistinctSeries(5);
        var tail = LiteralSeries(3);
        var calls = new Dictionary<int, SilenceBoundedChatClient.StreamedCall>();
        var i = 0;
        foreach (var s in plan.Concat(tail)) calls[i++] = Call(s);
        var cut = Call("{\"command\":\"npm te");
        calls[i] = cut;
        // 5 distinct, then the tail's first copy is new (index 5), its second is the repeat.
        Assert.Equal(6, SilenceBoundedChatClient.PlanLength(calls, cut));
        // No repeat at all: -1.
        var noRepeat = new Dictionary<int, SilenceBoundedChatClient.StreamedCall>();
        i = 0;
        foreach (var s in plan) noRepeat[i++] = Call(s);
        noRepeat[i] = cut;
        Assert.Equal(-1, SilenceBoundedChatClient.PlanLength(noRepeat, cut));
    }

    [Fact]
    public void The_sibling_key_reads_substance_not_annotation()
    {
        var a = new SilenceBoundedChatClient.StreamedCall { Name = "terminal" };
        a.Text.Append(JsonSerializer.Serialize(new { command = "npm test", security_risk = "LOW", summary = "Run the tests" }));
        var b = new SilenceBoundedChatClient.StreamedCall { Name = "terminal" };
        b.Text.Append(JsonSerializer.Serialize(new { summary = "Run tests again, attempt 7", command = "npm test", security_risk = "HIGH" }));
        var c = new SilenceBoundedChatClient.StreamedCall { Name = "terminal" };
        c.Text.Append(JsonSerializer.Serialize(new { command = "npm test -- --grep score", security_risk = "LOW", summary = "Run the tests" }));
        var d = new SilenceBoundedChatClient.StreamedCall { Name = "file_editor" };
        d.Text.Append(JsonSerializer.Serialize(new { command = "npm test", security_risk = "LOW", summary = "Run the tests" }));
        Assert.Equal(SilenceBoundedChatClient.SiblingKey(a), SilenceBoundedChatClient.SiblingKey(b));
        Assert.NotEqual(SilenceBoundedChatClient.SiblingKey(a), SilenceBoundedChatClient.SiblingKey(c));
        Assert.NotEqual(SilenceBoundedChatClient.SiblingKey(a), SilenceBoundedChatClient.SiblingKey(d));
        // Not a complete object: the bytes are the key.
        var e = new SilenceBoundedChatClient.StreamedCall { Name = "terminal" };
        e.Text.Append("{\"command\":\"npm te");
        Assert.Equal("terminal\u0000{\"command\":\"npm te", SilenceBoundedChatClient.SiblingKey(e));
    }

    [Fact]
    public async Task Three_identical_calls_then_distinct_ones_are_never_aborted()
    {
        // Under the literal threshold, and too distinct for the ruler: the control.
        var same = LiteralSeries(SilenceBoundedChatClient.LiteralRepeatAbortAfter - 1);
        var series = same.Concat(DistinctSeries(10)).ToArray();
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "terminal", Arguments: "", Fragments: 2,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 3000,
            Siblings: series));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var calls = resp.Messages.Single().Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(series.Length, calls.Count);
        Assert.All(calls, c => Assert.Null(c.Exception));
        Assert.Equal(ChatFinishReason.ToolCalls, resp.FinishReason);
    }

    [Fact]
    public async Task A_series_of_distinct_sibling_calls_is_never_aborted()
    {
        var series = DistinctSeries(24);
        var pct = ArgumentTail.CompressionPct(string.Join("\n", series) + "\n");
        Assert.True(pct > 2 * SilenceBoundedChatClient.LoopAbortTailPct, "control not distinct enough: " + pct);
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "terminal", Arguments: "", Fragments: 2,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 3000,
            Siblings: series));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        var calls = resp.Messages.Single().Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(series.Length, calls.Count);
        Assert.All(calls, c => Assert.Null(c.Exception));
        Assert.Equal(series[5], Assert.IsType<string>(calls[5].RawRepresentation));
        Assert.StartsWith("grep -n", calls[23].Arguments!["command"]?.ToString());
        Assert.Equal(ChatFinishReason.ToolCalls, resp.FinishReason);
    }

    [Fact]
    public void The_two_tail_rulers_are_one_ruler()
    {
        var loop = string.Concat(Enumerable.Repeat("the same line again\n", 400));
        var sb = new System.Text.StringBuilder(loop);
        Assert.Equal(ArgumentTail.CompressionPct(loop), ArgumentTail.CompressionPct(sb));
        Assert.Equal(ArgumentTail.CompressionPct(loop), AgentLoop.TailCompressionPct(loop));
        Assert.True(ArgumentTail.CompressionPct(loop) < SilenceBoundedChatClient.LoopAbortTailPct);
    }

    /// <summary>s12b it.24's text, as it ran: one plan sentence and a stray
    /// think-close, 3,969 lines of which 7 were distinct, 113 K characters.</summary>
    private static string LoopingProse()
    {
        const string line = "The file now has 25 facts. I need to add 75 more distinct facts. Let me append batch 2 (facts 26-50):\n\n</think>\n";
        var sb = new StringBuilder();
        while (sb.Length < 113_000) sb.Append(line);
        return sb.ToString();
    }

    /// <summary>Honest long prose: every line different (a fact list with an
    /// incompressible token per line), longer than the abort threshold.</summary>
    private static string DistinctProse(int chars)
    {
        var rng = new Random(20260909);
        var buf = new byte[24];
        var sb = new StringBuilder();
        var i = 0;
        while (sb.Length < chars)
        {
            rng.NextBytes(buf);
            sb.Append("Fact ").Append(i++).Append(": Saturn ").Append(Convert.ToBase64String(buf)).Append(" in orbit.\n");
        }
        return sb.ToString();
    }

    /// <summary>Law 240 (s12b it.24, 2026-09-09 13:46Z): a reply whose TEXT is
    /// a repetition loop is stopped by the same ruler that stops a looping
    /// argument stream, long before the cap, and comes back as a capped
    /// no-call reply carrying the loop's reading.</summary>
    [Fact]
    public async Task A_repeating_prose_stream_is_aborted_before_the_cap()
    {
        var full = LoopingProse();
        Assert.True(full.Length > 100_000, "fixture size: " + full.Length);
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "", Arguments: "", Fragments: full.Length / 1000,
            Gap: TimeSpan.FromMilliseconds(5), CompletionTokens: 32768, Prose: full));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        Assert.Equal(ChatFinishReason.Length, resp.FinishReason);
        Assert.Empty(resp.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        var text = resp.Text;
        Assert.True(text.Length >= SilenceBoundedChatClient.ProseLoopMinChars, "aborted too early: " + text.Length);
        // Law 244 / batch 14: the prose floor is one sample, not the file-sized
        // argument floor; a loop that pays out to the old floor is the defect.
        Assert.True(text.Length < SilenceBoundedChatClient.LoopAbortMinChars, "the prose loop paid out past one sample: " + text.Length);
        Assert.True(text.Length < full.Length / 2, "the loop paid out: " + text.Length + " of " + full.Length);
        Assert.StartsWith(text, full);
        var reading = AgentLoop.ProseLoopOf(resp);
        Assert.NotNull(reading);
        Assert.Equal(text.Length, reading!.Value.Chars);
        Assert.True(reading.Value.Pct < SilenceBoundedChatClient.LoopAbortTailPct, "tail pct " + reading.Value.Pct);
        var nudge = AgentLoop.ProseLoopNudge(reading.Value.Chars, reading.Value.Pct);
        Assert.Contains("stopped it before it produced a tool call", nudge);
        Assert.DoesNotContain("output cap", nudge);
    }

    /// <summary>The control: long, distinct prose is never stopped and
    /// carries no loop reading.</summary>
    [Fact]
    public async Task A_long_distinct_prose_stream_is_never_aborted()
    {
        var full = DistinctProse(40_000);
        using var engine = new FakeOpenAIEngine(new FakeReply(
            ToolName: "", Arguments: "", Fragments: 40,
            Gap: TimeSpan.FromMilliseconds(2), CompletionTokens: 10000, Prose: full));
        using var client = ChatClientFactory.Create(Local(engine));

        var resp = await client.GetResponseAsync(Prompt);

        Assert.Equal(ChatFinishReason.Stop, resp.FinishReason);
        Assert.Equal(full, resp.Text);
        Assert.Null(AgentLoop.ProseLoopOf(resp));
    }
}
