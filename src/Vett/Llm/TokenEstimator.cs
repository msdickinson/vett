using Microsoft.Extensions.AI;
using Vett.Llm;

namespace Vett.Llm;

/// <summary>
/// Estimates token count for messages. Uses per-model character ratios
/// since exact tokenization requires model-specific tokenizers.
///
/// ⛔ 2026-08-26 — READ THIS BEFORE TRUSTING <see cref="Estimate(IEnumerable{ChatMessage}, string?)"/>
/// AS A CONTEXT-FULLNESS NUMBER. It is not one, and two audited defects say so:
///
///  1. IT DOES NOT COUNT TOOL SCHEMAS. Schemas are re-sent on EVERY request
///     (<c>AgentLoop.cs:491-518</c>) but live in <c>caps.ToolSchemas</c>, a
///     different field from <c>state.Messages</c>, and never reach this method.
///     Measured on a captured real request body
///     (<c>sidecar/testdata/ref-requests/turn-1-initial.wire.json</c>): message
///     text 12,153 chars → 3,046 estimated tokens, while the 5 tool schemas add
///     17,539 chars → ~4,385 tokens at this class's own ratio. The schemas were
///     58.3% of the body, so the estimate was 2.4x LOW on that turn. The blind
///     spot is roughly 2.4k-4.5k tokens per seat and it is INVISIBLE.
///
///  2. NO PRODUCTION MODEL MATCHES THE RATIO TABLE. Every live profile runs
///     `deepseek-v4-flash` or `deepseek/deepseek-v4-pro`; there is no
///     `deepseek` row, so every production seat silently takes the 4.0
///     default. The "calibrated against real token counts" claim below has NO
///     supporting artifact in the repo — no golden test, no fixture, no data
///     file. Treat it as undocumented, not as established.
///
/// Both errors point the SAME dangerous way — too low — so compaction fires
/// LATER than the profile asked, which is exactly the overflow the feature
/// exists to prevent.
///
/// ⇒ Callers deciding "is the context nearly full?" MUST use
/// <see cref="EstimateContext"/>, which anchors on the provider's real
/// InputTokenCount and reduces this heuristic to the few messages added since.
/// Plain <see cref="Estimate(IEnumerable{ChatMessage}, string?)"/> remains
/// correct for what it actually is: a relative size comparison between two
/// message lists.
/// </summary>
public static class TokenEstimator
{
    // Ratios — chars per token for common model families. See the class doc:
    // the word "calibrated" was in this comment for a long time with nothing
    // behind it, so it has been removed rather than left to be re-trusted.
    // internal, not private: the reachability gate in TokenEstimatorTests has to
    // ENUMERATE the rows to prove each one is selectable. A test that restated
    // the key list by hand would drift from the table the moment someone added a
    // row — which is exactly the change that introduces prefix shadowing.
    internal static readonly Dictionary<string, double> ModelRatios = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4"] = 3.8,
        ["gpt-4o"] = 3.9,
        ["gpt-3.5"] = 4.0,
        ["claude"] = 3.5,
        ["gemini"] = 4.0,
        ["nemotron"] = 3.8,
        ["qwen"] = 3.5,
        ["llama"] = 3.8,
    };

    /// <summary>
    /// Estimate total tokens for a list of messages.
    /// Includes role tokens (~4 per message) and content tokens.
    /// </summary>
    public static int Estimate(IEnumerable<ChatMessage> messages, string? model = null)
    {
        var ratio = GetRatio(model);
        var total = 0;

        foreach (var msg in messages)
        {
            total += 4; // role + framing overhead per message
            var text = msg.GetText();
            total += (int)(text.Length / ratio);

            // Tool calls add JSON overhead.
            foreach (var fc in msg.Contents.OfType<FunctionCallContent>())
            {
                total += 10; // function name + framing
                if (fc.Arguments is not null)
                {
                    var argsJson = System.Text.Json.JsonSerializer.Serialize(fc.Arguments);
                    total += (int)(argsJson.Length / ratio);
                }
            }
        }

        return total;
    }

    /// <summary>
    /// Context fullness, anchored on GROUND TRUTH where it exists.
    ///
    /// <paramref name="anchorTokens"/> is the provider's own
    /// <c>Usage.InputTokenCount</c> for the last request, and
    /// <paramref name="anchorMessageCount"/> is how many messages were in the
    /// list when that request went out (captured together in
    /// <c>AgentLoop</c> at the usage read). The provider counted the ENTIRE
    /// prompt — tool schemas, framing, its own tokenizer — so anchoring on it
    /// closes both defects in the class doc at once: the schema blind spot
    /// disappears because the schemas were in the number, and the ratio table
    /// stops mattering because the heuristic now only has to size the handful
    /// of messages appended since that response.
    ///
    /// Before the first response there is no anchor and this degrades to the
    /// plain heuristic. That is the safest moment for it to be wrong — the
    /// context is at its emptiest — and it self-corrects on the next turn.
    ///
    /// The anchor is INVALID once someone rewrites history: a compaction
    /// strategy that clears messages leaves an anchor describing a prompt that
    /// no longer exists, and carrying it forward would keep reporting the
    /// pre-compaction size and re-fire immediately, forever. Clearing
    /// strategies therefore reset it to 0, and the
    /// <c>anchorMessageCount &gt; messages.Count</c> guard here is a second line
    /// of defence for any caller that forgets.
    /// </summary>
    public static int EstimateContext(
        IReadOnlyList<ChatMessage> messages,
        int anchorTokens,
        int anchorMessageCount,
        string? model = null)
    {
        if (anchorTokens <= 0 || anchorMessageCount < 0 || anchorMessageCount > messages.Count)
            return Estimate(messages, model);

        // Only the tail added since the anchored request needs guessing.
        var sinceAnchor = 0;
        for (var i = anchorMessageCount; i < messages.Count; i++)
            sinceAnchor += Estimate([messages[i]], model);

        return anchorTokens + sinceAnchor;
    }

    /// <summary>Estimate tokens for a single string.</summary>
    public static int Estimate(string text, string? model = null)
        => (int)(text.Length / GetRatio(model));

    /// <summary>
    /// Test seam for the row-reachability gate. Exposes the selection rule
    /// directly so a test can assert "every row is selectable by its own key"
    /// without inferring the ratio back out of a character count.
    /// </summary>
    internal static double RatioFor(string? model) => GetRatio(model);

    /// <summary>
    /// Picks the ratio row for a model name.
    ///
    /// ⛔ 2026-08-26 — LONGEST MATCH WINS. THIS USED TO BE FIRST-MATCH-WINS AND
    /// THAT MADE A ROW UNREACHABLE. The keys are substrings, and one is a prefix
    /// of another: <c>"gpt-4o".Contains("gpt-4")</c> is true, so with a
    /// first-match loop the <c>gpt-4o</c> row could never be selected — every
    /// gpt-4o model silently took the gpt-4 ratio (3.8 instead of 3.9). A dead
    /// table row is worse than a missing one: it reads as a deliberate, tuned
    /// value that someone has already thought about.
    ///
    /// The old loop had a second, quieter problem. It returned on the FIRST
    /// match while enumerating a <see cref="Dictionary{TKey,TValue}"/>, whose
    /// enumeration order is explicitly not part of its contract. The answer was
    /// therefore only stable by accident of the current runtime's insertion
    /// order — reordering the initialiser, or a framework change, could silently
    /// move every estimate. Scoring all matches and breaking ties ordinally
    /// makes the result a function of the table's CONTENT, not its layout.
    ///
    /// ⚠ This does not make the number trustworthy — see the class doc. No
    /// production model matches any row here, so the fix changes nothing for
    /// deepseek seats; it is correctness in the lookup, not calibration.
    /// </summary>
    private static double GetRatio(string? model)
    {
        if (string.IsNullOrEmpty(model))
            return 4.0;

        string? bestKey = null;
        var bestRatio = 4.0; // default when nothing matches

        foreach (var (prefix, ratio) in ModelRatios)
        {
            if (!model.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var moreSpecific = bestKey is null
                || prefix.Length > bestKey.Length
                || (prefix.Length == bestKey.Length && string.CompareOrdinal(prefix, bestKey) < 0);

            if (moreSpecific)
            {
                bestKey = prefix;
                bestRatio = ratio;
            }
        }

        return bestRatio;
    }
}
