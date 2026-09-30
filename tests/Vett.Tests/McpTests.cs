using System.Text.Json;
using Vett.Mcp;

namespace Vett.Tests;

/// <summary>
/// Covers the MCP client (#21). Pure-helper coverage uses
/// hand-rolled JSON-RPC strings; integration coverage uses a tiny
/// fake server written in bash that speaks the wire protocol enough
/// to pass initialize + tools/list + tools/call. This keeps the
/// tests self-contained — no python / node dependency, no real MCP
/// servers required.
/// </summary>
public class McpTests
{
    [Fact]
    public void ParseEnvelope_response_with_result()
    {
        var env = McpProtocol.ParseEnvelope("""{"jsonrpc":"2.0","id":7,"result":{"foo":"bar"}}""");
        Assert.Equal(7, env.Id);
        Assert.Null(env.Method);
        Assert.Null(env.Error);
        Assert.True(env.Result.HasValue);
        Assert.Equal("bar", env.Result!.Value.GetProperty("foo").GetString());
    }

    [Fact]
    public void ParseEnvelope_error_response()
    {
        var env = McpProtocol.ParseEnvelope("""{"jsonrpc":"2.0","id":7,"error":{"code":-32601,"message":"Method not found"}}""");
        Assert.NotNull(env.Error);
        Assert.Equal(-32601, env.Error!.Code);
        Assert.Equal("Method not found", env.Error.Message);
    }

    [Fact]
    public void ParseEnvelope_notification_no_id()
    {
        var env = McpProtocol.ParseEnvelope("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Null(env.Id);
        Assert.Equal("notifications/initialized", env.Method);
    }

    [Fact]
    public void ParseEnvelope_tolerates_string_id()
    {
        var env = McpProtocol.ParseEnvelope("""{"jsonrpc":"2.0","id":"42","result":{}}""");
        Assert.Equal(42, env.Id);
    }

    [Fact]
    public void ParseToolsList_extracts_name_description_schema()
    {
        var doc = JsonDocument.Parse("""
        {
          "tools": [
            {
              "name": "search",
              "description": "Search the knowledge base",
              "inputSchema": {"type":"object","properties":{"q":{"type":"string"}}}
            },
            {
              "name": "ping",
              "inputSchema": {"type":"object"}
            }
          ]
        }
        """);
        var tools = McpProtocol.ParseToolsList(doc.RootElement);
        Assert.Equal(2, tools.Count);
        Assert.Equal("search", tools[0].Name);
        Assert.Equal("Search the knowledge base", tools[0].Description);
        Assert.True(tools[0].InputSchema.HasValue);
        Assert.Equal("ping", tools[1].Name);
        Assert.Equal("", tools[1].Description); // missing description → empty string
    }

    [Fact]
    public void ParseToolsList_skips_unnamed_tools()
    {
        var doc = JsonDocument.Parse("""
        {
          "tools": [
            {"description": "no name"},
            {"name": "real_tool"}
          ]
        }
        """);
        var tools = McpProtocol.ParseToolsList(doc.RootElement);
        Assert.Single(tools);
        Assert.Equal("real_tool", tools[0].Name);
    }

    [Fact]
    public void ParseToolsList_handles_missing_tools_array()
    {
        var doc = JsonDocument.Parse("""{"capabilities":{}}""");
        var tools = McpProtocol.ParseToolsList(doc.RootElement);
        Assert.Empty(tools);
    }

    [Fact]
    public void ParseToolCallResult_concatenates_text_parts()
    {
        var doc = JsonDocument.Parse("""
        {
          "content": [
            {"type":"text","text":"first line"},
            {"type":"text","text":"second line"}
          ]
        }
        """);
        var result = McpProtocol.ParseToolCallResult(doc.RootElement);
        Assert.Equal("first line\nsecond line", result);
    }

    [Fact]
    public void ParseToolCallResult_isError_prefix()
    {
        var doc = JsonDocument.Parse("""
        {
          "content": [{"type":"text","text":"file not found"}],
          "isError": true
        }
        """);
        var result = McpProtocol.ParseToolCallResult(doc.RootElement);
        Assert.StartsWith("Error:", result);
        Assert.Contains("file not found", result);
    }

    [Fact]
    public void ParseToolCallResult_unknown_part_kind_stringifies()
    {
        var doc = JsonDocument.Parse("""
        {
          "content": [
            {"type":"text","text":"intro"},
            {"type":"image","data":"...base64..."}
          ]
        }
        """);
        var result = McpProtocol.ParseToolCallResult(doc.RootElement);
        Assert.Contains("intro", result);
        Assert.Contains("image", result);
    }

    [Fact]
    public void QualifyToolName_namespaces_and_sanitizes()
    {
        Assert.Equal("mcp__github__create_issue",
            McpClientPool.QualifyToolName("github", "create_issue"));
        Assert.Equal("mcp__file_sys__list_dir",
            McpClientPool.QualifyToolName("file/sys", "list dir"));
        Assert.Equal("mcp__a-b__c-d",
            McpClientPool.QualifyToolName("a-b", "c-d"));
    }

    [Fact]
    public void BuildToolSchema_wraps_inputSchema_under_function_parameters()
    {
        var inputSchema = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""").RootElement;
        var schema = McpClientPool.BuildToolSchema("mcp__x__search", new McpToolInfo("search", "Search", inputSchema));
        Assert.Equal("function", schema.GetProperty("type").GetString());
        Assert.Equal("mcp__x__search", schema.GetProperty("function").GetProperty("name").GetString());
        // Description now includes the [mcp:...] prefix per the
        // session-16 close-out (gotcha 160 fix). When server name +
        // original tool name aren't supplied, the prefix uses the
        // qualified name as a fallback.
        var description = schema.GetProperty("function").GetProperty("description").GetString();
        Assert.Contains("Search", description);
        Assert.StartsWith("[mcp:", description);
        Assert.True(schema.GetProperty("function").GetProperty("parameters").TryGetProperty("properties", out _));
    }

    [Fact]
    public void BuildToolSchema_falls_back_to_empty_schema_when_input_schema_missing()
    {
        var schema = McpClientPool.BuildToolSchema("mcp__x__y", new McpToolInfo("y", "", null));
        // Description prefix kicks in when serverName/originalToolName are not supplied:
        // "[mcp:mcp__x__y] (no description supplied by the MCP server)"
        var description = schema.GetProperty("function").GetProperty("description").GetString();
        Assert.Contains("mcp__x__y", description);
        Assert.Contains("no description", description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("object", schema.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public void BuildToolDescription_prefixes_with_server_and_original_name()
    {
        var d = McpClientPool.BuildToolDescription(
            "mcp__github__create_issue",
            new McpToolInfo("create_issue", "Create a GitHub issue", null),
            "github", "create_issue");
        Assert.StartsWith("[mcp:github/create_issue]", d);
        Assert.Contains("Create a GitHub issue", d);
    }

    [Fact]
    public void BuildToolDescription_uses_qualified_name_when_server_omitted()
    {
        var d = McpClientPool.BuildToolDescription(
            "mcp__x__y",
            new McpToolInfo("y", "Helpful tool", null),
            null, null);
        Assert.StartsWith("[mcp:mcp__x__y]", d);
        Assert.Contains("Helpful tool", d);
    }

    [Fact]
    public void BuildToolDescription_synthesizes_no_description_marker_when_blank()
    {
        var d = McpClientPool.BuildToolDescription(
            "mcp__x__y",
            new McpToolInfo("y", "", null),
            "x", "y");
        Assert.Contains("no description supplied", d, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildInitialize_carries_protocol_version_and_client_info()
    {
        var body = McpProtocol.BuildInitialize(1, "vett", "0.1.0");
        var json = JsonSerializer.Serialize(body);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("initialize", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal(McpProtocol.ProtocolVersion,
            doc.RootElement.GetProperty("params").GetProperty("protocolVersion").GetString());
        Assert.Equal("vett",
            doc.RootElement.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());
    }

    [Fact]
    public void BuildInitializedNotification_has_no_id()
    {
        var body = McpProtocol.BuildInitializedNotification();
        var json = JsonSerializer.Serialize(body);
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("id", out _));
        Assert.Equal("notifications/initialized", doc.RootElement.GetProperty("method").GetString());
    }
}
