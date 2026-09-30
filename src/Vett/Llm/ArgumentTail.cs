namespace Vett.Llm;

/// <summary>
/// The one deflate ruler for "is this argument text repeating itself":
/// AgentLoop's cut-create salvage reads it after the cap (law 138/229) and
/// <see cref="SilenceBoundedChatClient"/> reads it DURING the stream to abort
/// a loop before the cap pays out (law 231). One implementation so the two
/// readings can never drift apart.
/// </summary>
public static class ArgumentTail
{
    /// <summary>Characters of tail the ratio is taken over.</summary>
    public const int SampleChars = 4096;

    /// <summary>Deflated size of the text's tail as a percentage of its size;
    /// null when there is too little to say anything.</summary>
    public static int? CompressionPct(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var tail = raw.Length > SampleChars ? raw[^SampleChars..] : raw;
        return CompressionPctOf(tail);
    }

    /// <summary>Same ruler over the last <see cref="SampleChars"/> of a
    /// builder, without materialising the whole text.</summary>
    public static int? CompressionPct(System.Text.StringBuilder text)
    {
        if (text.Length == 0) return null;
        var take = Math.Min(SampleChars, text.Length);
        return CompressionPctOf(text.ToString(text.Length - take, take));
    }

    private static int? CompressionPctOf(string tail)
    {
        if (tail.Length < 512) return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(tail);
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            z.Write(bytes);
        return (int)Math.Round(100.0 * ms.Length / bytes.Length);
    }
}

/// <summary>
/// The stream was cut by THIS client, not by the engine's cap: a tool call's
/// arguments had run past <see cref="SilenceBoundedChatClient.LoopAbortMinChars"/>
/// with a tail deflating under <see cref="SilenceBoundedChatClient.LoopAbortTailPct"/>.
/// Carried in <see cref="Microsoft.Extensions.AI.FunctionCallContent.Exception"/>
/// so AgentLoop treats the call exactly as a cap-cut one (it is), and so the
/// event can say the cut was ours.
/// </summary>
public sealed class RepeatingArgumentsException : Exception
{
    public int Chars { get; }
    public int TailPct { get; }

    /// <summary>Zero when ONE call's arguments looped (law 231). Otherwise the
    /// number of sibling tool calls the reply had started when their
    /// concatenated arguments proved to be a loop ACROSS calls (law 232: the
    /// run-11 architect emitted 99 <c>find | head -N000</c> calls to the cap,
    /// twice, each call short and parseable, the series a loop) -- every one
    /// of them is dropped, none runs.</summary>
    public int Siblings { get; }

    /// <summary>Law 245 (EpicForge batch 15, 2026-09-09): when the series
    /// across calls was a LITERAL repeat -- at most
    /// <see cref="SilenceBoundedChatClient.LiteralSiblingLoopMaxDistinct"/>
    /// distinct calls re-issued to the abort -- this is how many distinct
    /// calls there were, and the first copy of each was rebuilt to run. Zero
    /// when the series was a loop in every member (law 232's paging series)
    /// and none ran, or when one call's own arguments looped.</summary>
    public int Distinct { get; }

    /// <summary>Law 247 (EpicForge batch 16, 2026-09-09): how many leading
    /// siblings of the series were rebuilt and RAN -- the first copy of each
    /// distinct call on the literal path; on the paraphrase path the leading
    /// calls whose keys were new, up to the first repeat and at most
    /// <see cref="SilenceBoundedChatClient.LiteralSiblingLoopMaxDistinct"/>.
    /// Zero for a loop inside one call (its complete siblings are kept by the
    /// older rule and not counted here).</summary>
    public int Ran { get; }

    /// <summary>Law 249 instrumentation (EpicForge batch 18, 2026-09-09): the
    /// 1-based wire index of the first sibling whose <see cref="SilenceBoundedChatClient.SiblingKey"/>
    /// had already been seen in the series, or -1 when no key repeats before
    /// the cut. Batch 18 left 2 of 15 aborts unreadable because the outside
    /// ruler keyed on `summary` (which the key drops) -- this is the harness
    /// saying where IT saw the repeat.</summary>
    public int FirstRepeat { get; }

    public RepeatingArgumentsException(int chars, int tailPct, int siblings = 0, int distinct = 0, int ran = 0, int firstRepeat = -1)
        : base(siblings > 0 && distinct > 0
            ? $"tool-call stream aborted after {siblings} sibling calls ({chars} characters of arguments): {(distinct == 1 ? "the same call" : "the same " + distinct + " calls")} re-issued to the abort; the first copy of each ran, the repeats were dropped"
            : siblings > 0
            ? $"tool-call stream aborted after {siblings} sibling calls ({chars} characters of arguments): the last {ArgumentTail.SampleChars} compress to {tailPct}% of their size, a repetition loop across calls, not a plan"
              + (ran > 0 ? $"; the first {ran} of them ran, the rest were dropped" : "; none ran")
            : $"argument stream aborted after {chars} characters: the last {ArgumentTail.SampleChars} compress to {tailPct}% of their size, a repetition loop, not content")
    {
        Chars = chars;
        TailPct = tailPct;
        Siblings = siblings;
        Distinct = distinct;
        Ran = ran;
        FirstRepeat = firstRepeat;
    }
}
