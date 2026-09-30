using System.Text.Json;
using Microsoft.Extensions.AI;
using Vett.Llm;

namespace Vett.Agent;

/// <summary>
/// Reads a past chat session JSONL log (the format the VS Code
/// extension writes — `{seq, ts, type, instance_id, data}` envelopes)
/// and reconstructs an agent message history that can be fed back into
/// a new `vett chat --stdio` run.
///
/// Rules:
///   - <c>user_message</c> events become <see cref="ChatRole.User"/> messages.
///   - <c>assistant_text</c> events become <see cref="ChatRole.Assistant"/> messages.
///   - Tool calls / tool results are <em>intentionally skipped</em> in this
///     first iteration. The agent gets the conversation but doesn't
///     re-replay every Bash invocation. For full fidelity later, walk
///     <c>tool_call_end</c> events and add ToolResult messages alongside
///     <c>AssistantWithCalls</c>.
///   - Order is preserved by `seq` (envelope-level monotonic counter).
///   - Malformed lines, missing fields, or unknown types are skipped — the
///     log is best-effort; resume should never fail noisily.
/// </summary>
public static class HistorySeeder
{
    public sealed record SeedResult(List<ChatMessage> Messages, int UserTurns, int AssistantTurns);

    public static SeedResult LoadFromJsonl(string path)
    {
        var msgs = new List<ChatMessage>();
        var userTurns = 0;
        var assistantTurns = 0;
        if (!File.Exists(path))
            return new SeedResult(msgs, 0, 0);

        // Sort by `seq` so resume is robust to logs that were written
        // out of order (shouldn't happen, but cheap insurance).
        //
        // CRITICAL: open with FileShare.ReadWrite. The VS Code extension
        // is also holding a writable handle to this file (it appends in
        // place across resumes), and on Windows the default share mode
        // would throw IOException "file is being used by another
        // process". Linux/macOS are forgiving here, but Windows isn't.
        var rows = new List<(int Seq, string Type, string Text)>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var tEl)) continue;
                var type = tEl.GetString();
                if (type is not ("user_message" or "assistant_text")) continue;

                var seq = root.TryGetProperty("seq", out var sEl) && sEl.TryGetInt32(out var s) ? s : int.MaxValue;
                var text = ExtractText(root, type);
                if (string.IsNullOrEmpty(text)) continue;

                rows.Add((seq, type, text));
            }
            catch
            {
                // Skip malformed lines — partial / interrupted writes shouldn't
                // poison the resume.
            }
        }

        rows.Sort((a, b) => a.Seq.CompareTo(b.Seq));

        foreach (var (_, type, text) in rows)
        {
            if (type == "user_message")
            {
                msgs.Add(Chat.User(text));
                userTurns++;
            }
            else
            {
                msgs.Add(Chat.Assistant(text));
                assistantTurns++;
            }
        }

        return new SeedResult(msgs, userTurns, assistantTurns);
    }

    /// <summary>
    /// Extract the text payload from an envelope. Two layouts seen in the
    /// wild:
    ///   1. Extension-written user_message: data.text holds the message
    ///   2. Vett-emitted assistant_text: top-level `text` OR data.text
    /// We try both so the seeder is robust to either.
    /// </summary>
    private static string ExtractText(JsonElement root, string type)
    {
        if (root.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String)
        {
            var t = tEl.GetString();
            if (!string.IsNullOrEmpty(t)) return t;
        }
        if (root.TryGetProperty("data", out var dEl) && dEl.ValueKind == JsonValueKind.Object)
        {
            if (dEl.TryGetProperty("text", out var dtEl) && dtEl.ValueKind == JsonValueKind.String)
            {
                var t = dtEl.GetString();
                if (!string.IsNullOrEmpty(t)) return t;
            }
        }
        return "";
    }
}
