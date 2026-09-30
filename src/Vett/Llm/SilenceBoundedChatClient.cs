using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Vett.Llm;

/// <summary>
/// Streams the reply and bounds SILENCE, not generation.
///
/// ⛔ WHY THIS EXISTS -- MEASURED, not supposed (EpicForge run 5,
/// 2026-09-05, HANDOFF law 135). With the buffered request, the SDK's
/// <c>NetworkTimeout</c> (= <c>request_timeout_seconds</c>, 180 s on
/// ef-team-flash) bounds the WHOLE generation: headers, every token, the
/// close. At 8 parallel seats the engine ran 8-11 requests at 59-192 tok/s
/// aggregate, i.e. ~12.5 tok/s PER REQUEST, so any honest reply longer than
/// ~2,250 tokens could not finish inside 180 s no matter what it said. The
/// SDK then retried the identical doomed request (3 tries, 540 s), AgentLoop
/// retried that stack (3 attempts, up to 1,620 s), and the seat hit the
/// 1,800 s deadline having landed nothing. Six of the run's seats died that
/// way; all twelve <c>llm_error</c> events in the run carried the same
/// "exceeded the configured timeout of 0:03:00" text; the profile's own
/// invariant <c>max_output_tokens &lt; request_timeout_seconds x tok/s</c>
/// read 4096 &lt; 2250 and was false at that concurrency. Raising the cap
/// re-tunes the lottery; this dissolves it.
///
/// HOW. <see cref="GetResponseAsync"/> asks the inner client for the
/// STREAMING response and assembles the same <see cref="ChatResponse"/> the
/// buffered call returned (tool calls, finish_reason, usage -- MEAI's
/// <c>ToChatResponse</c> does the merge). A linked idle timer is re-armed on
/// every update: the request fails only when the engine has said NOTHING for
/// <c>request_timeout_seconds</c>, which is a real fault, not a long answer.
/// When that fires, the cancellation is converted to an
/// <see cref="LlmException"/> naming the bound -- NEVER leaked as an
/// <see cref="OperationCanceledException"/>, because AgentLoop rethrows those
/// as "the caller cancelled" and would end the whole run instead of retrying
/// the turn. The caller's own token still propagates unchanged.
///
/// WHAT DOES NOT CHANGE. AgentLoop, Coordinator and every <c>LlmSettings</c>
/// site still call <c>GetResponseAsync</c>. FailoverChatClient wraps each
/// endpoint's client and this sits INSIDE, so an idle timeout hops endpoints
/// exactly as a network timeout did. The SDK's <c>ClientRetryPolicy</c> and
/// <c>RequestRetryPolicy</c> still cover the connect / time-to-first-byte
/// phase; a fault mid-stream is not retried on the wire (nothing in the
/// pipeline wraps enumeration), so a silent engine costs ONE request per
/// AgentLoop attempt, not three. Profiles opt out with <c>llm.stream: false</c>
/// to get the buffered request back byte-for-byte.
/// </summary>
public sealed class SilenceBoundedChatClient : IChatClient
{
    /// <summary>
    /// ⛔ A REPETITION LOOP INSIDE A TOOL CALL PAYS OUT THE WHOLE CAP BEFORE
    /// ANYONE CAN SAY SO (EpicForge batch 8, 2026-09-09 05:30-05:40Z, HANDOFF
    /// law 231). With the raw bytes finally on the wire (law 230), the first
    /// four cut calls read 28-45 K characters of arguments whose 4 KiB tail
    /// deflated to 4-6% -- the same line over and over -- for a file that
    /// lands at 7 K when written honestly; genuine files here deflate to
    /// 14-31%, a pure repeated line to 2%. Every one of those loops ran to
    /// the output cap (8-13 minutes at the seat's ~16 tok/s) and only THEN
    /// did AgentLoop's notice tell the model it had been repeating itself --
    /// after which it recovered at once with small writes. So the notice
    /// works; the cap payout is the waste.
    ///
    /// This client sees every argument delta, so it applies the same deflate
    /// ruler DURING the stream: once a call's arguments pass
    /// <see cref="LoopAbortMinChars"/> (past any genuine single file this
    /// fleet has written, and ~3k tokens into an 8k cap) it checks every
    /// <see cref="LoopAbortCheckEveryChars"/>, and if the tail deflates under
    /// <see cref="LoopAbortTailPct"/> -- deliberately STRICTER than the
    /// salvage's 12, because a false abort would tell an honest write it was
    /// looping -- it stops enumerating (which closes the HTTP stream) and
    /// hands AgentLoop the cut call it would have received at the cap: a
    /// <see cref="FunctionCallContent"/> with no arguments, the bytes that
    /// arrived in RawRepresentation, a <see cref="RepeatingArgumentsException"/>
    /// in Exception, and <c>finish_reason = length</c>. Nothing downstream
    /// changes: the same truncation notice fires, minutes earlier. Usage is
    /// NOT reported for an aborted turn (the engine never sent it), so the
    /// seat's own token count under-reads by the aborted generation; the
    /// event carries the character count instead.
    ///
    /// The adapter emits a reply's tool calls only when the stream ENDS, so
    /// leaving early also loses the complete sibling calls that arrived
    /// before the looping one. Those are rebuilt here from their own raw
    /// text (parsed the way the adapter would have parsed them) so they run;
    /// only the looping call is cut.
    /// </summary>
    public const int LoopAbortMinChars = 12288;
    /// <summary>Law 244 / batch 14 (2026-09-09): the PROSE ruler's floor. The
    /// argument floor above is sized so a genuine single file is never read
    /// as a loop; prose has no such file, and every prose loop in batch 14
    /// ("response response response ..." after the think block) took 120-190
    /// s of streaming to reach 12,288 characters (s14c runner.out clock:
    /// 651->771, 2110->2233, 2317->2507 s). The prose ruler now reads at one
    /// sample (<see cref="ArgumentTail.SampleChars"/>) and every
    /// <see cref="LoopAbortCheckEveryChars"/> after; the 8% tail bar is
    /// unchanged, and honest prose deflates to 30-50%.</summary>
    public const int ProseLoopMinChars = ArgumentTail.SampleChars;
    public const int LoopAbortTailPct = 8;
    public const int LoopAbortCheckEveryChars = 2048;

    /// <summary>
    /// ⛔ THE LOOP CAN BE ACROSS CALLS, EACH ONE SHORT AND PARSEABLE (EpicForge
    /// run 11 architect A-mttn9m3j2vz2, 2026-09-09 05:14-05:44Z, HANDOFF law
    /// 232). Twice in one pass the model emitted <c>find ... | head -1000</c>,
    /// <c>-2000</c>, ... <c>-90000</c> -- 64 then 99 sibling calls -- until
    /// the 12,288-token cap, 13 minutes each at ~16 tok/s; the per-call ruler
    /// above never sees it because no single call passes 200 characters.
    /// AgentLoop then RAN the 86 that parsed (identical listings, the context
    /// doubled from 40 K to 79 K), and the 30-minute architect cap fell with
    /// no verdict. The same ruler over the CONCATENATED arguments of the
    /// reply's calls reads the series: measured on that log, 8% at 24 calls
    /// and 6% from 28 on, while the pass's genuine 9-call grep fan-out read
    /// 17% and a 6-call one 26%. So once a reply has started
    /// <see cref="SiblingLoopMinCalls"/> calls, every NEW call re-reads the
    /// concatenation; under <see cref="LoopAbortTailPct"/> the stream is
    /// stopped and the WHOLE series is dropped (all of it is the loop: the
    /// first <c>head -1000</c> is not a plan either) -- AgentLoop receives one
    /// cut call carrying the series as its raw text and a
    /// <see cref="RepeatingArgumentsException"/> whose <c>Siblings</c> counts
    /// them, and its nudge names the series instead of "the arguments".
    /// </summary>
    public const int SiblingLoopMinCalls = 12;

    /// <summary>
    /// ⛔ LAW 245 (EpicForge batch 15, 2026-09-09 16:18-16:22Z, VETT 8796e09):
    /// THE SERIES CAN BE ONE CALL RE-ISSUED VERBATIM, AND ITS FIRST COPY IS
    /// THE PLAN. Four of eight solo-Flash arms lost an iteration each, inside
    /// the first 17 minutes, to a law-232 abort whose series was a single
    /// `terminal` call repeated to the ruler: s15b 31 copies of one
    /// <c>node --test test/game-clear.test.js | grep</c>, s15d 23 of one
    /// <c>node -e "...scoreFor(4,3)"</c>, s15h 18 of one <c>node --test
    /// test/game-*.test.js | grep "not ok"</c>, s15e a create then 32 copies
    /// of one test run. Law 232 drops the whole series because the
    /// architect's series was a loop in EVERY member (<c>head -1000</c>,
    /// <c>-2000</c>, ... each a different, escalating call); a literal repeat
    /// is the opposite shape -- had the stream parsed, the turn-dedupe
    /// collapse would have run the first copy and replayed its result to
    /// the rest. So when the aborted series holds at most this many DISTINCT
    /// calls, the first copy of each is rebuilt (as the complete siblings of
    /// a within-call loop already are) and runs; only the repeats are
    /// dropped, and the cut call's notice says so. A series with more
    /// distinct members than this is still law 232's: none runs.
    /// </summary>
    public const int LiteralSiblingLoopMaxDistinct = 3;

    /// <summary>Law 245, the clock half: the four batch-15 aborts above cost
    /// 212-535 s each on the runner clock (previous event to abort: s15b 329,
    /// s15d 245, s15e 535, s15h 212 s) streaming 18-32 copies to the 12-call
    /// ruler. Four identical complete calls in one reply never buy anything
    /// (the turn-dedupe collapse runs the first and replays its result), so
    /// once this many consecutive complete siblings are byte-identical the
    /// stream is stopped at the next call, whatever the deflate ruler says of
    /// so short a series.</summary>
    public const int LiteralRepeatAbortAfter = 4;

    /// <summary>
    /// ⛔ LAW 247 (EpicForge batch 16, 2026-09-09 17:18-17:22Z, VETT c7971f8):
    /// THE PARAPHRASE LOOP THAT LAW 245 LEFT TO THE 12-CALL RULER STILL THREW
    /// THE REQUEST AWAY WITH THE REPEATS. s16f it.15: a complete `create
    /// test/game-collision.test.js`, then 13 copies of one `node --test ...`
    /// command whose `summary` the model re-worded; s16g it.13: one distinct
    /// grep, then 33 test runs (5,291 chars, tail 12%); s16f it.19: 13 more.
    /// <see cref="SiblingKey"/> read the WHOLE argument text, so a re-worded
    /// `summary` made every copy a distinct call: the four-identical cut never
    /// fired, the 12-call ruler did, and the non-literal branch of
    /// <see cref="AppendAbortedCall"/> rebuilt nothing -- the create was lost
    /// with the loop, and the seat was told to try again. Two repairs:
    /// (1) the key reads the call's SUBSTANCE: the parsed arguments with
    /// <see cref="IgnoredSiblingKeyFields"/> removed (`summary` and
    /// `security_risk` annotate a call, they are not the call) and the rest
    /// in name order, so re-worded copies of one command are one key and
    /// take law 245's literal path; (2) whichever ruler fired, the LEADING
    /// complete siblings whose keys are new -- stopping at the first key
    /// already seen, at most <see cref="LiteralSiblingLoopMaxDistinct"/> of
    /// them -- are rebuilt and run: the request precedes the repeats. Law
    /// 232's escalating paging series (every key distinct) now runs its
    /// first three reads and drops the rest where it ran none; three reads
    /// cost less than the iteration a lost create costs. The event carries
    /// <c>loop_ran</c> and the head of every sibling (<c>sibling_heads</c>),
    /// because s16f's 14-call series left a 200-char head and a 300-char
    /// tail and nothing that could be read.
    /// </summary>
    public static readonly string[] IgnoredSiblingKeyFields = ["summary", "security_risk"];

    /// <summary>Law 247, the clock half: once this many complete siblings hold
    /// at most <see cref="LiteralSiblingLoopMaxDistinct"/> distinct keys,
    /// every key in the reply has been re-issued and the stream is cut at the
    /// next call -- an a/b/a/b alternation never trips the four-identical cut
    /// and paid the full 12 calls to the ruler.</summary>
    public const int RepeatedSiblingsAbortAfter = 8;

    /// <summary>
    /// ⛔ LAW 249 (EpicForge batch 17, 2026-09-09 18:16-18:18Z, VETT 20adc59):
    /// THE SERIES WAS A PLAN WITH A LOOPING TAIL, AND LAW 247 RAN THREE OF IT.
    /// s17b it.12: two module creates, two edits, EIGHT distinct test-file
    /// creates, then `node --test test/game-*.test.js` five times to the
    /// four-identical cut -- 17 siblings, 13 distinct keys, the first repeat
    /// at index 13. Not literal (13 distinct > 3), so the paraphrase path
    /// ran the leading distinct calls "at most
    /// <see cref="LiteralSiblingLoopMaxDistinct"/>": three ran, nine
    /// well-formed distinct writes were dropped, and the seat re-created
    /// the eight test files ONE PER ITERATION (it.14, it.15, ...). s17e
    /// it.18 (13 siblings, 8 distinct, repeat at 8) and s17f it.17 (17 / 13
    /// / 13) were the same shape within two minutes of each other. The cap
    /// of three exists for law 232's paging series, where EVERY key is
    /// distinct and the leading segment IS the loop; it has no business
    /// where a key repeats, because the loop begins at the first repeat and
    /// everything before it is the plan the seat would have run had the
    /// reply ended cleanly. So: when some key repeats, the leading segment
    /// up to the first repeat runs in full -- unless that segment's own
    /// text deflates under <see cref="LoopAbortTailPct"/> (a paging series
    /// that happens to end in a repeat is still a paging series), in which
    /// case the cap of three stands. When no key repeats, nothing changes.
    /// </summary>
    /// <summary>Law 249 instrumentation: the 1-based wire index of the first
    /// sibling before <paramref name="cut"/> whose <see cref="SiblingKey"/>
    /// had already been seen; -1 when no key repeats.</summary>
    internal static int FirstRepeatIndex(Dictionary<int, StreamedCall> calls, StreamedCall cut)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var kv in calls.OrderBy(kv => kv.Key))
        {
            if (ReferenceEquals(kv.Value, cut)) break;
            index++;
            if (!seen.Add(SiblingKey(kv.Value))) return index;
        }
        return -1;
    }

    internal static int PlanLength(Dictionary<int, StreamedCall> calls, StreamedCall cut)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var leading = new System.Text.StringBuilder();
        var count = 0;
        var repeated = false;
        foreach (var kv in calls.OrderBy(kv => kv.Key))
        {
            if (ReferenceEquals(kv.Value, cut)) break;
            if (!seen.Add(SiblingKey(kv.Value))) { repeated = true; break; }
            leading.Append(kv.Value.Text);
            leading.Append('\n');
            count++;
        }
        if (!repeated) return -1;
        if (ArgumentTail.CompressionPct(leading.ToString()) is int pct && pct < LoopAbortTailPct) return -1;
        return count;
    }

    /// <summary>
    /// ⛔ THE LOOP CAN BE PROSE, AND PROSE WAS NEVER READ (EpicForge batch 12
    /// arm s12b, 2026-09-09 13:20-13:46Z, HANDOFF law 240). Iteration 24 of an
    /// `ef-solo-flash-32k` seat emitted "The file now has 25 facts. I need to
    /// add 75 more distinct facts. Let me append batch 2 (facts 26-50):" and a
    /// stray `&lt;/think&gt;` over and over -- 113,183 characters, 3,969 lines
    /// of which 7 were distinct, the tail deflating to 3% -- for 1,546 s of a
    /// 3,600 s arm, to the 32,768-token cap, with no tool call; AgentLoop then
    /// discarded the whole reply (truncated_response_recovery) as it should.
    /// The two rulers above read only tool-call argument deltas, so a loop
    /// in the TEXT paid out the entire cap. Same ruler, same thresholds, over
    /// the reply's text: once it passes <see cref="LoopAbortMinChars"/> it is
    /// checked every <see cref="LoopAbortCheckEveryChars"/>, and a tail under
    /// <see cref="LoopAbortTailPct"/> stops the stream. Honest prose never
    /// reads under 8% (English deflates to ~30-40%; the architect's 7,487-token
    /// plan of the same hour was distinct). The reply comes back with
    /// <c>finish_reason = length</c>, the text that arrived, any COMPLETE tool
    /// calls that preceded the loop rebuilt from their raw text, and the
    /// abort's reading in <see cref="ChatResponse.AdditionalProperties"/>
    /// under <see cref="ProseLoopCharsKey"/> / <see cref="ProseLoopPctKey"/>,
    /// so AgentLoop's no-tool-call recovery can name the loop instead of the
    /// cap.
    /// </summary>
    public const string ProseLoopCharsKey = "prose_loop_chars";
    public const string ProseLoopPctKey = "prose_loop_pct";

    /// <summary>The reply's text proved to be a loop: how much had arrived
    /// and what its tail deflated to.</summary>
    internal sealed record ProseLoopAbort(int Chars, int Pct);

    /// <summary>One tool call as it arrives over the stream: its id and name
    /// from the first delta, its argument text accumulated from every delta.</summary>
    internal sealed class StreamedCall
    {
        public string? CallId;
        public string? Name;
        public readonly System.Text.StringBuilder Text = new();
        public int NextCheckAt = LoopAbortMinChars;
    }

    /// <summary>Why the stream was stopped: the call being written when the
    /// ruler fired, the tail reading, the bytes the ruler read (one call's
    /// arguments, or the series' concatenation), and how many sibling calls
    /// the series had -- zero for a loop inside one call.</summary>
    internal sealed record LoopAbort(StreamedCall Call, int Pct, string Raw, int Siblings);

    private readonly IChatClient _inner;
    private readonly TimeSpan _silenceBound;

    public SilenceBoundedChatClient(IChatClient inner, TimeSpan silenceBound)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (silenceBound <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(silenceBound), "the silence bound must be positive");
        _silenceBound = silenceBound;
    }

    /// <summary>The longest the engine may say nothing before the request fails.</summary>
    public TimeSpan SilenceBound => _silenceBound;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_silenceBound);

        var updates = new List<ChatResponseUpdate>();
        var calls = new Dictionary<int, StreamedCall>();
        LoopAbort? loop = null;
        var prose = new System.Text.StringBuilder();
        var nextProseCheck = ProseLoopMinChars;
        ProseLoopAbort? proseAbort = null;
        var wall = Stopwatch.StartNew();
        try
        {
            await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, idle.Token)
                               .ConfigureAwait(false))
            {
                updates.Add(update);
                loop = CollectRawArguments(update, calls);
                // Leaving the enumeration disposes the adapter's iterator and
                // with it the HTTP response: the engine stops generating.
                if (loop is not null) break;
                proseAbort = CollectProse(update, prose, ref nextProseCheck);
                if (proseAbort is not null) break;
                // Any progress re-arms the clock. CancelAfter on a live CTS
                // resets its timer; on an already-cancelled one it is a no-op,
                // and the enumeration above is what observes that.
                idle.CancelAfter(_silenceBound);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Ours (or the SDK's per-read NetworkTimeout, which under streaming
            // is the same silence bound) -- not the caller's. Surface it as an
            // LLM error so AgentLoop retries the TURN instead of ending the run.
            throw new LlmException(
                $"LLM stream silent for {_silenceBound.TotalSeconds:0}s "
                + $"(request_timeout_seconds) after {updates.Count} update(s) and "
                + $"{wall.Elapsed.TotalSeconds:0}s of wall clock; generation itself is "
                + "not time-bounded, only silence is.",
                ex);
        }

        var response = updates.ToChatResponse();
        AttachRawArguments(response, calls);
        if (loop is not null) AppendAbortedCall(response, calls, loop);
        else if (proseAbort is not null) MarkProseAbort(response, calls, proseAbort);
        return response;
    }

    /// <summary>
    /// ⛔ THE STREAMING ADAPTER DROPS THE BYTES A CUT TOOL CALL DID SEND
    /// (EpicForge batch 7 arm s7c, 2026-09-09 05:12Z, HANDOFF law 230). The
    /// buffered OpenAI adapter parks the wire <c>ChatToolCall</c> in
    /// <see cref="FunctionCallContent.RawRepresentation"/>, and AgentLoop's
    /// cut-create salvage (law 229) reads the arguments back from there. The
    /// STREAMING adapter accumulates the argument deltas privately, parses
    /// them once at the end, and on a parse failure keeps only the exception
    /// -- RawRepresentation is never set. So under this client (every
    /// profile that streams, i.e. every EpicForge seat) a cut
    /// <c>file_editor create</c> reported <c>raw_argument_chars: null</c>
    /// and salvaged nothing, while its unit tests -- which construct the
    /// buffered shape -- were green. The mechanism was built; the wire it
    /// runs on never delivered its input.
    ///
    /// Repair: the per-chunk <see cref="ChatResponseUpdate.RawRepresentation"/>
    /// IS set (to the SDK's <c>StreamingChatCompletionUpdate</c>), and its
    /// <c>ToolCallUpdates</c> carry every argument delta with the call's
    /// index and id. This client sees every chunk, so it accumulates the
    /// deltas itself and, after the merge, hands each
    /// <see cref="FunctionCallContent"/> that arrived without a raw
    /// representation its own argument text (matched by call id, else by
    /// position). A parseable call keeps its parsed arguments untouched; only
    /// the raw string is added, which is exactly what the buffered path
    /// already exposed.
    ///
    /// Returns the abort when a call's arguments (<see cref="LoopAbortMinChars"/>)
    /// or the reply's series of calls (<see cref="SiblingLoopMinCalls"/>) just
    /// proved to be a repetition loop, or null to keep streaming.
    /// </summary>
    internal static LoopAbort? CollectRawArguments(
        ChatResponseUpdate update,
        Dictionary<int, StreamedCall> calls)
    {
        if (update.RawRepresentation is not OpenAI.Chat.StreamingChatCompletionUpdate scu) return null;
        if (scu.ToolCallUpdates is not { Count: > 0 } toolCalls) return null;
        LoopAbort? looping = null;
        foreach (var tcu in toolCalls)
        {
            if (!calls.TryGetValue(tcu.Index, out var entry))
            {
                calls[tcu.Index] = entry = new StreamedCall { CallId = tcu.ToolCallId, Name = tcu.FunctionName };
                // A NEW call: re-read the series so far (law 232). The calls
                // before this one are complete; this one has no text yet.
                if (looping is null && calls.Count >= SiblingLoopMinCalls)
                {
                    var series = Concatenate(calls);
                    if (ArgumentTail.CompressionPct(series) is int sp && sp < LoopAbortTailPct)
                        looping = new LoopAbort(entry, sp, series, calls.Count);
                }
                // Law 245: the last LiteralRepeatAbortAfter complete siblings
                // are one call, byte for byte -- stop here, not at 12.
                if (looping is null && calls.Count > LiteralRepeatAbortAfter && LastSiblingsIdentical(calls, entry, LiteralRepeatAbortAfter))
                {
                    var series = Concatenate(calls);
                    looping = new LoopAbort(entry, ArgumentTail.CompressionPct(series) ?? 0, series, calls.Count);
                }
                // Law 247: RepeatedSiblingsAbortAfter complete siblings holding
                // at most LiteralSiblingLoopMaxDistinct keys -- every one of
                // them re-issued -- cut here, not at 12.
                if (looping is null && calls.Count > RepeatedSiblingsAbortAfter && DistinctSiblings(calls, entry) <= LiteralSiblingLoopMaxDistinct)
                {
                    var series = Concatenate(calls);
                    looping = new LoopAbort(entry, ArgumentTail.CompressionPct(series) ?? 0, series, calls.Count);
                }
            }
            else
            {
                entry.CallId ??= tcu.ToolCallId;
                entry.Name ??= tcu.FunctionName;
            }
            if (tcu.FunctionArgumentsUpdate is { } delta)
            {
                var s = delta.ToString();
                if (s.Length > 0) entry.Text.Append(s);
            }
            if (looping is null && entry.Text.Length >= entry.NextCheckAt)
            {
                entry.NextCheckAt = entry.Text.Length + LoopAbortCheckEveryChars;
                if (ArgumentTail.CompressionPct(entry.Text) is int p && p < LoopAbortTailPct)
                    looping = new LoopAbort(entry, p, entry.Text.ToString(), 0);
            }
        }
        return looping;
    }

    /// <summary>Law 240: the reply's text deltas, accumulated and read with
    /// the argument ruler once past <see cref="ProseLoopMinChars"/>. Returns
    /// the abort when the tail just proved to be a loop, else null.</summary>
    internal static ProseLoopAbort? CollectProse(ChatResponseUpdate update, System.Text.StringBuilder prose, ref int nextCheckAt)
    {
        foreach (var tc in update.Contents.OfType<TextContent>())
            if (!string.IsNullOrEmpty(tc.Text)) prose.Append(tc.Text);
        if (prose.Length < nextCheckAt) return null;
        nextCheckAt = prose.Length + LoopAbortCheckEveryChars;
        return ArgumentTail.CompressionPct(prose) is int p && p < LoopAbortTailPct
            ? new ProseLoopAbort(prose.Length, p)
            : null;
    }

    /// <summary>Law 240: the reply as AgentLoop must see it after a prose
    /// loop stopped the stream -- every COMPLETE call that arrived before the
    /// loop rebuilt from its raw text (the adapter emits calls only when the
    /// stream ends), <c>finish_reason = length</c>, and the abort's reading in
    /// the response's properties.</summary>
    internal static void MarkProseAbort(ChatResponse response, Dictionary<int, StreamedCall> calls, ProseLoopAbort abort)
    {
        var msg = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (msg is null)
        {
            msg = new ChatMessage(ChatRole.Assistant, []);
            response.Messages.Add(msg);
        }
        var present = msg.Contents.OfType<FunctionCallContent>()
            .Select(fc => fc.CallId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        foreach (var kv in calls.OrderBy(kv => kv.Key))
        {
            var sibling = kv.Value;
            if (sibling.CallId is not null && present.Contains(sibling.CallId)) continue;
            var text = sibling.Text.ToString();
            if (ParseArguments(text) is not { } args) continue;
            msg.Contents.Add(new FunctionCallContent(sibling.CallId ?? "", sibling.Name ?? "", args)
            {
                RawRepresentation = text,
            });
        }
        response.FinishReason = ChatFinishReason.Length;
        response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        response.AdditionalProperties[ProseLoopCharsKey] = abort.Chars;
        response.AdditionalProperties[ProseLoopPctKey] = abort.Pct;
    }

    /// <summary>Law 245: the tool name and the call's substance -- two
    /// siblings with the same key are the same call. Law 247: substance, not
    /// text: see <see cref="CanonicalArguments"/>.</summary>
    internal static string SiblingKey(StreamedCall call)
        => (call.Name ?? "") + "\u0000" + CanonicalArguments(call.Text.ToString());

    /// <summary>Law 247: the argument text as a key -- a complete JSON
    /// object's properties in name order with <see cref="IgnoredSiblingKeyFields"/>
    /// removed; the trimmed text itself when it is not a complete object (a
    /// call still streaming, or a malformed one, keys as its bytes).</summary>
    internal static string CanonicalArguments(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{') return trimmed;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return trimmed;
            var sb = new System.Text.StringBuilder();
            foreach (var prop in doc.RootElement.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (Array.IndexOf(IgnoredSiblingKeyFields, prop.Name) >= 0) continue;
                sb.Append(prop.Name).Append('=').Append(prop.Value.GetRawText()).Append('\u0001');
            }
            return sb.ToString();
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    /// <summary>Law 245: true when the <paramref name="count"/> complete
    /// siblings just before <paramref name="cut"/> (wire order) share one
    /// non-empty key.</summary>
    internal static bool LastSiblingsIdentical(Dictionary<int, StreamedCall> calls, StreamedCall cut, int count)
    {
        var before = calls.OrderBy(kv => kv.Key).Select(kv => kv.Value)
            .TakeWhile(c => !ReferenceEquals(c, cut)).ToList();
        if (before.Count < count) return false;
        var last = before.Skip(before.Count - count).ToList();
        if (last[0].Text.Length == 0) return false;
        var key = SiblingKey(last[0]);
        return last.All(c => SiblingKey(c) == key);
    }

    /// <summary>Law 245: how many distinct calls the complete siblings before
    /// <paramref name="cut"/> hold (zero when there are none).</summary>
    internal static int DistinctSiblings(Dictionary<int, StreamedCall> calls, StreamedCall cut)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in calls.OrderBy(kv => kv.Key))
        {
            if (ReferenceEquals(kv.Value, cut)) break;
            seen.Add(SiblingKey(kv.Value));
        }
        return seen.Count;
    }

    /// <summary>The reply's argument text so far, calls in wire order, one
    /// per line -- the bytes the sibling ruler reads.</summary>
    internal static string Concatenate(Dictionary<int, StreamedCall> calls)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var kv in calls.OrderBy(kv => kv.Key))
        {
            sb.Append(kv.Value.Text);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    internal static void AttachRawArguments(ChatResponse response, Dictionary<int, StreamedCall> calls)
    {
        if (calls.Count == 0) return;
        var byIndex = calls.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var position = 0;
        foreach (var fc in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
        {
            var mine = position < byIndex.Count ? byIndex[position] : null;
            position++;
            if (fc.RawRepresentation is not null) continue;
            var match = byIndex.FirstOrDefault(e => e.CallId is not null && e.CallId == fc.CallId) ?? mine;
            if (match is null || match.Text.Length == 0) continue;
            fc.RawRepresentation = match.Text.ToString();
        }
    }

    /// <summary>What AgentLoop would have seen at the cap, built from what
    /// arrived. For a loop inside one call: every complete sibling that came
    /// BEFORE it, parsed from its own text (the adapter never emitted them --
    /// it emits a reply's calls only when the stream ends), then the cut call:
    /// no arguments, its raw text, the abort as its exception. For a loop
    /// across calls: the cut call alone, carrying the series as its raw text;
    /// the siblings are the loop and none runs. Either way
    /// <c>finish_reason = length</c>, so the truncation path fires and the
    /// cut call is the message's last content.</summary>
    internal static void AppendAbortedCall(ChatResponse response, Dictionary<int, StreamedCall> calls, LoopAbort abort)
    {
        var msg = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (msg is null)
        {
            msg = new ChatMessage(ChatRole.Assistant, []);
            response.Messages.Add(msg);
        }
        // Law 245: a series of at most LiteralSiblingLoopMaxDistinct distinct
        // calls is a literal repeat; the first copy of each runs. Law 247:
        // any other series runs its LEADING distinct calls -- up to the first
        // key already seen, at most LiteralSiblingLoopMaxDistinct of them --
        // because the request precedes the repeats (s16f's create, s16g's
        // grep); the complete siblings of a within-call loop all run, as
        // before.
        var distinct = abort.Siblings == 0 ? 0 : DistinctSiblings(calls, abort.Call);
        var literal = abort.Siblings > 0 && distinct > 0 && distinct <= LiteralSiblingLoopMaxDistinct;
        // Law 249: the plan before the first repeated key runs whole; -1 when
        // no key repeats (law 232's shape) or the plan is itself a loop.
        var plan = abort.Siblings > 0 && !literal ? PlanLength(calls, abort.Call) : -1;
        var ran = 0;
        {
            var present = msg.Contents.OfType<FunctionCallContent>()
                .Select(fc => fc.CallId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in calls.OrderBy(kv => kv.Key))
            {
                var sibling = kv.Value;
                if (ReferenceEquals(sibling, abort.Call)) break;
                var text = sibling.Text.ToString();
                if (abort.Siblings > 0)
                {
                    var isNew = seen.Add(SiblingKey(sibling));
                    if (literal) { if (!isNew) continue; }
                    else if (!isNew || (plan < 0 && seen.Count > LiteralSiblingLoopMaxDistinct)) break;
                }
                if (sibling.CallId is not null && present.Contains(sibling.CallId)) continue;
                if (ParseArguments(text) is not { } args) continue;
                msg.Contents.Add(new FunctionCallContent(sibling.CallId ?? "", sibling.Name ?? "", args)
                {
                    RawRepresentation = text,
                });
                if (abort.Siblings > 0) ran++;
            }
        }
        msg.Contents.Add(new FunctionCallContent(abort.Call.CallId ?? "", abort.Call.Name ?? "", null)
        {
            Exception = new RepeatingArgumentsException(abort.Raw.Length, abort.Pct, abort.Siblings, literal ? distinct : 0, ran,
                abort.Siblings > 0 ? FirstRepeatIndex(calls, abort.Call) : -1),
            RawRepresentation = abort.Raw,
        });
        response.FinishReason = ChatFinishReason.Length;
    }

    /// <summary>A complete call's arguments as the adapter parses them: a
    /// JSON object, its values as <see cref="JsonElement"/>. Null when the
    /// text is not a complete object -- a call the stream had not finished.</summary>
    internal static Dictionary<string, object?>? ParseArguments(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var args = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
                args[prop.Name] = prop.Value.Clone();
            return args;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
        => _inner.GetStreamingResponseAsync(messages, options, ct);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
