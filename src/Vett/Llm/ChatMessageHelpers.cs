// `using System;` is required to bring the System namespace's
// `StringComparison`, `Convert`, and `FormatException` into scope as
// unqualified names — inside the `Chat` class below, the bare token
// `System` resolves to the `Chat.System` factory method, so the usual
// `System.Convert` / `System.StringComparison` qualifiers won't compile.
using System;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Vett.Llm;

/// <summary>
/// Helpers for working with ChatMessage in the agent loop.
/// These bridge the gap between ChatMessage's content-block model
/// and our simpler "role + text + tool calls" mental model.
/// </summary>
public static class Chat
{
    /// <summary>
    /// Marker prefix that turns a user-channel string into a multimodal
    /// payload. The chat extension's image-paste path produces these via
    /// <see cref="EncodeUserInput"/>; the agent loop's <see cref="User"/>
    /// factory transparently parses them back into a multi-content
    /// ChatMessage. Plain strings (no marker) flow through as today.
    ///
    /// Why a marker on a string channel instead of a richer channel
    /// type: the user-input channel reaches deep through AgentLoop and
    /// Coordinator (10+ method signatures). A string-with-marker keeps
    /// every signature unchanged AND keeps existing call sites
    /// (continue_task, injected messages, tests) working without per-
    /// site updates. Image messages from the chat extension are the
    /// only producer of the marker today.
    /// </summary>
    private const string MultimodalPrefix = "__VETT_MULTIMODAL__";

    /// <summary>An image attached to a user message — base64-encoded
    /// bytes plus an IANA media type ("image/png", "image/jpeg", etc).</summary>
    public sealed record ImagePart(string Base64Data, string MediaType);

    // --- Factory methods ---

    public static ChatMessage System(string text) => new(ChatRole.System, text);

    /// <summary>
    /// Build a User ChatMessage from a channel string. Plain strings
    /// become text-only messages. Strings carrying the multimodal
    /// marker (see <see cref="EncodeUserInput"/>) become a multi-content
    /// message with TextContent + DataContent blocks for each image.
    /// Malformed payloads degrade to plain text rather than throwing —
    /// the agent never sees garbled JSON in its history.
    /// </summary>
    public static ChatMessage User(string text)
    {
        if (!text.StartsWith(MultimodalPrefix, StringComparison.Ordinal))
            return new ChatMessage(ChatRole.User, text);
        try
        {
            var json = text.AsSpan(MultimodalPrefix.Length).ToString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var bodyText = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(bodyText))
                contents.Add(new TextContent(bodyText));
            if (root.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in imgs.EnumerateArray())
                {
                    var data = img.TryGetProperty("data", out var d) ? (d.GetString() ?? "") : "";
                    var mediaType = img.TryGetProperty("media_type", out var m) ? (m.GetString() ?? "image/png") : "image/png";
                    if (string.IsNullOrEmpty(data)) continue;
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(data); }
                    catch (FormatException) { continue; }
                    contents.Add(new DataContent(bytes, mediaType));
                }
            }
            // No content at all (empty text, no valid images) → fall back
            // to the original string so we never produce an unsendable
            // empty-body user turn.
            if (contents.Count == 0)
                return new ChatMessage(ChatRole.User, text);
            return new ChatMessage(ChatRole.User, contents);
        }
        catch (JsonException)
        {
            return new ChatMessage(ChatRole.User, text);
        }
    }

    /// <summary>
    /// Inverse of <see cref="User"/> — wrap text + images into a single
    /// channel-string the agent loop will decode at consume time.
    /// Producers (chat-stdio loop) call this; the channel and AgentLoop
    /// stay <c>string</c> end-to-end.
    /// </summary>
    public static string EncodeUserInput(string text, IReadOnlyList<ImagePart>? images)
    {
        if (images is null || images.Count == 0) return text;
        var payload = JsonSerializer.Serialize(new
        {
            text,
            images = images.Select(i => new { data = i.Base64Data, media_type = i.MediaType }),
        });
        return MultimodalPrefix + payload;
    }

    public static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    public static ChatMessage AssistantWithCalls(string? text, List<FunctionCallContent> calls)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(text))
            contents.Add(new TextContent(text));
        contents.AddRange(calls);
        return new ChatMessage(ChatRole.Assistant, contents);
    }

    public static ChatMessage ToolResult(string callId, string result)
        => new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    // --- Query helpers ---

    public static bool IsRole(this ChatMessage msg, ChatRole role) => msg.Role == role;

    public static bool HasToolCalls(this ChatMessage msg)
        => msg.Contents.OfType<FunctionCallContent>().Any();

    public static List<FunctionCallContent> GetToolCalls(this ChatMessage msg)
        => msg.Contents.OfType<FunctionCallContent>().ToList();

    public static string GetText(this ChatMessage msg)
    {
        // Check regular text first.
        if (!string.IsNullOrEmpty(msg.Text))
            return msg.Text;

        // For tool result messages, extract text from FunctionResultContent.
        var frc = msg.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        if (frc?.Result is string s)
            return s;

        return "";
    }
}
