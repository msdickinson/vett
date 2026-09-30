using System.Text.Json;

namespace Vett.Mcp;

/// <summary>
/// MCP (Model Context Protocol) wire-format helpers. Spec-driven —
/// see https://spec.modelcontextprotocol.io/. v1 implements the minimum
/// surface VETT needs: JSON-RPC 2.0 envelope, the
/// <c>initialize</c> handshake, <c>tools/list</c>, and <c>tools/call</c>.
/// Resources / prompts / sampling are deferred.
///
/// Pure / I/O-free — exposes builders + parsers only. Transport
/// (subprocess + stdio + line framing) lives in McpStdioClient.cs.
/// </summary>
public static class McpProtocol
{
    /// <summary>The protocol version vett claims to speak. MCP servers
    /// negotiate this during the initialize handshake — most servers
    /// accept any reasonable date string and adapt to the client.</summary>
    public const string ProtocolVersion = "2024-11-05";

    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>
    /// Build an initialize request body for a fresh JSON-RPC id.
    /// Caller serializes via JsonSerializer.Serialize and writes one
    /// JSON line to the server's stdin.
    /// </summary>
    public static object BuildInitialize(int id, string clientName, string clientVersion) => new
    {
        jsonrpc = "2.0",
        id,
        method = "initialize",
        @params = new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { tools = new { } },
            clientInfo = new { name = clientName, version = clientVersion },
        },
    };

    /// <summary>Build the post-initialize "initialized" notification
    /// (no id, no response expected). MCP servers don't begin
    /// servicing tool calls until they see this.</summary>
    public static object BuildInitializedNotification() => new
    {
        jsonrpc = "2.0",
        method = "notifications/initialized",
    };

    public static object BuildToolsList(int id) => new
    {
        jsonrpc = "2.0",
        id,
        method = "tools/list",
    };

    public static object BuildToolsCall(int id, string name, object arguments) => new
    {
        jsonrpc = "2.0",
        id,
        method = "tools/call",
        @params = new { name, arguments },
    };

    /// <summary>
    /// Parse a JSON-RPC response into a typed envelope. The result
    /// payload (if any) is left as a raw JsonElement so each method's
    /// response shape can be picked apart by the caller. Error
    /// responses surface via <see cref="JsonRpcEnvelope.Error"/>.
    /// </summary>
    public static JsonRpcEnvelope ParseEnvelope(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement.Clone();

        int? id = null;
        if (root.TryGetProperty("id", out var idEl))
        {
            if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var i)) id = i;
            // Some MCP servers stringify ids — accept either.
            else if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var ii)) id = ii;
        }
        var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        var result = root.TryGetProperty("result", out var r) ? (JsonElement?)r : null;
        JsonRpcError? error = null;
        if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
        {
            var code = e.TryGetProperty("code", out var cc) && cc.ValueKind == JsonValueKind.Number ? cc.GetInt32() : InternalError;
            var message = e.TryGetProperty("message", out var mm) && mm.ValueKind == JsonValueKind.String ? mm.GetString() ?? "" : "";
            error = new JsonRpcError(code, message);
        }
        return new JsonRpcEnvelope(id, method, result, error);
    }

    /// <summary>
    /// Parse the result of a successful <c>tools/list</c> call into a
    /// list of <see cref="McpToolInfo"/> records. Tolerant — missing
    /// fields default to empty rather than throwing.
    /// </summary>
    public static List<McpToolInfo> ParseToolsList(JsonElement result)
    {
        var tools = new List<McpToolInfo>();
        if (!result.TryGetProperty("tools", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return tools;
        foreach (var t in arr.EnumerateArray())
        {
            var name = t.TryGetProperty("name", out var nn) && nn.ValueKind == JsonValueKind.String
                ? nn.GetString() ?? ""
                : "";
            if (string.IsNullOrEmpty(name)) continue;
            var description = t.TryGetProperty("description", out var dd) && dd.ValueKind == JsonValueKind.String
                ? dd.GetString() ?? ""
                : "";
            var schema = t.TryGetProperty("inputSchema", out var ss) && ss.ValueKind == JsonValueKind.Object
                ? (JsonElement?)ss.Clone()
                : null;
            tools.Add(new McpToolInfo(name, description, schema));
        }
        return tools;
    }

    /// <summary>
    /// Pull the human-readable text out of a tools/call result. MCP
    /// returns a content array of typed parts; for v1 we concatenate
    /// the text parts and stringify everything else as JSON. This
    /// matches what the agent expects as a tool observation.
    /// </summary>
    public static string ParseToolCallResult(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return result.ToString();
        var sb = new System.Text.StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            var partType = part.TryGetProperty("type", out var pt) && pt.ValueKind == JsonValueKind.String
                ? pt.GetString()
                : null;
            if (partType == "text" && part.TryGetProperty("text", out var pttext) && pttext.ValueKind == JsonValueKind.String)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(pttext.GetString());
            }
            else
            {
                // Image / blob / unknown content types — stringify so
                // the agent at least sees something. v2 could decode
                // image content into the multimodal pipeline.
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(part.GetRawText());
            }
        }
        // Surface isError flag inline so the agent can see soft failures.
        if (result.TryGetProperty("isError", out var err) && err.ValueKind == JsonValueKind.True)
        {
            sb.Insert(0, "Error: ");
        }
        return sb.ToString();
    }
}

/// <summary>Parsed JSON-RPC envelope. <see cref="Method"/> is set on
/// notifications + server→client requests; <see cref="Result"/> on
/// successful responses; <see cref="Error"/> on error responses.</summary>
public sealed record JsonRpcEnvelope(int? Id, string? Method, JsonElement? Result, JsonRpcError? Error);

public sealed record JsonRpcError(int Code, string Message);

/// <summary>One MCP-discovered tool. <see cref="InputSchema"/> is the
/// JSON-Schema-shaped <c>parameters</c> blob the agent's LLM client
/// uses when building tool definitions.</summary>
public sealed record McpToolInfo(string Name, string Description, JsonElement? InputSchema);
