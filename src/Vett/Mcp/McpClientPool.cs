using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vett.Tools;
using YamlDotNet.Serialization;

namespace Vett.Mcp;

/// <summary>
/// MCP server config — one entry per named server in the profile's
/// <c>mcp_servers:</c> map. Connection is best-effort at session
/// start; failures log a warning and skip that server.
/// </summary>
public sealed class McpServerConfig
{
    [YamlMember(Alias = "command")] public string Command { get; set; } = "";
    [YamlMember(Alias = "env")] public Dictionary<string, string>? Env { get; set; }
    [YamlMember(Alias = "connect_timeout_seconds")] public int ConnectTimeoutSeconds { get; set; } = 30;
    /// <summary>
    /// When true, forward this server's stderr to the host's console
    /// (`[mcp:&lt;name&gt;] ...`). When false (default), stderr is drained
    /// silently — useful for chatty servers that log every request.
    /// Per-server because some servers are well-behaved and others
    /// flood; one knob per profile would be too coarse.
    /// </summary>
    [YamlMember(Alias = "verbose")] public bool Verbose { get; set; } = false;
}

/// <summary>
/// Manages the lifetime of every MCP server connected for a chat
/// session. Connect at session start, expose the union of all
/// servers' tools (namespaced as <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>
/// to avoid collisions with builtins / each other), then dispose at
/// session end to terminate every subprocess cleanly.
///
/// Failure to connect to any one server is logged but doesn't take
/// down the chat — the agent simply doesn't see that server's tools.
/// </summary>
public sealed class McpClientPool : IDisposable
{
    private readonly List<McpStdioClient> _clients = new();
    private readonly Dictionary<string, ToolFn> _toolFns = new();
    private readonly List<JsonElement> _toolSchemas = new();
    private readonly ILogger? _logger;

    public McpClientPool(ILogger? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyDictionary<string, ToolFn> ToolFunctions => _toolFns;
    public IReadOnlyList<JsonElement> ToolSchemas => _toolSchemas;
    public int ConnectedServerCount => _clients.Count;

    /// <summary>
    /// Connect to every server in <paramref name="servers"/> in
    /// parallel, discover their tools, and build the namespaced tool
    /// table. Idempotent on repeat calls — but a fresh pool is
    /// expected per session.
    /// </summary>
    public async Task ConnectAllAsync(Dictionary<string, McpServerConfig> servers, CancellationToken ct)
    {
        // Filter + capture (key, value) pairs in one pass so the post-
        // gather loop can pair results with server names by index
        // without re-scanning the dictionary. Previous version did an
        // O(N) `Keys.ToList().IndexOf` per result — quadratic over
        // server count. Doesn't matter at typical 1-3 servers, but
        // ugly enough to clean up while in here.
        var entries = servers
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Command))
            .ToList();

        var tasks = entries.Select(async kv =>
        {
            try
            {
                var client = await McpStdioClient.ConnectAsync(
                    kv.Key, kv.Value.Command, kv.Value.Env, kv.Value.ConnectTimeoutSeconds, ct, kv.Value.Verbose);
                var tools = await client.ListToolsAsync(ct);
                return (Client: (McpStdioClient?)client, Tools: tools, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Client: (McpStdioClient?)null, Tools: new List<McpToolInfo>(), Error: (Exception?)ex);
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        for (var i = 0; i < entries.Count; i++)
        {
            var server = entries[i];
            var result = results[i];
            if (result.Error is not null || result.Client is null)
            {
                var msg = result.Error?.Message ?? "unknown error";
                _logger?.LogWarning(
                    "MCP server '{Name}' failed to connect: {Message}. Skipping.",
                    server.Key, msg);
                // Also surface to stderr so team-bench runs (which don't route
                // ILogger to console) still see MCP connect failures during
                // debugging. Silent MCP failures were the 2026-07-05 A/B blocker.
                Console.Error.WriteLine($"[mcp:{server.Key}] connect failed: {result.Error?.GetType().Name}: {msg}");
                if (result.Error?.InnerException is not null)
                    Console.Error.WriteLine($"[mcp:{server.Key}]   inner: {result.Error.InnerException.GetType().Name}: {result.Error.InnerException.Message}");
                Console.Error.WriteLine($"[mcp:{server.Key}]   stack: {result.Error?.StackTrace?.Split('\n')?.FirstOrDefault()?.Trim()}");
                continue;
            }

            _clients.Add(result.Client);
            foreach (var tool in result.Tools)
            {
                var qualifiedName = QualifyToolName(server.Key, tool.Name);
                if (_toolFns.ContainsKey(qualifiedName))
                {
                    _logger?.LogWarning(
                        "MCP tool name collision on '{Name}' — second registration ignored.", qualifiedName);
                    continue;
                }
                var capturedClient = result.Client;
                var capturedToolName = tool.Name;
                _toolFns[qualifiedName] = async (args, _, _, callCt) =>
                {
                    try
                    {
                        return await capturedClient.CallToolAsync(capturedToolName, args, callCt);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return $"Error: MCP tool '{qualifiedName}' threw: {ex.Message}";
                    }
                };
                _toolSchemas.Add(BuildToolSchema(qualifiedName, tool, server.Key, tool.Name));
            }
            _logger?.LogInformation(
                "MCP server '{Name}' connected — registered {Count} tool{Plural}.",
                server.Key, result.Tools.Count, result.Tools.Count == 1 ? "" : "s");
        }
    }

    /// <summary>
    /// Build the OpenAI-style tool-schema JsonElement we need to
    /// register the MCP tool with the agent's LLM client.
    ///
    /// Auto-prepends the server name + original tool name to the
    /// description so the model can identify which MCP server backed
    /// the tool. Without this, an MCP-discovered tool with a one-word
    /// description like "Search" loses all context — there could be
    /// twenty servers each exposing a "search" tool, and the agent
    /// has no way to tell them apart from the namespaced name alone
    /// (gotcha 160). Pure — exported for tests.
    /// </summary>
    public static JsonElement BuildToolSchema(string qualifiedName, McpToolInfo tool, string? serverName = null, string? originalToolName = null)
    {
        var description = BuildToolDescription(qualifiedName, tool, serverName, originalToolName);
        var schemaObj = new
        {
            type = "function",
            function = new
            {
                name = qualifiedName,
                description,
                parameters = tool.InputSchema ?? JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement,
            },
        };
        var json = JsonSerializer.Serialize(schemaObj);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Compose the description string for an MCP-registered tool.
    /// Format:
    ///   <c>[mcp:server/original] description</c>
    /// when both server + original name are known and the description
    /// is non-empty;
    ///   <c>[mcp:server/original] (no description)</c>
    /// when the MCP server didn't supply one. Pure — exported for tests.
    /// </summary>
    public static string BuildToolDescription(string qualifiedName, McpToolInfo tool, string? serverName, string? originalToolName)
    {
        var prefix = !string.IsNullOrEmpty(serverName) && !string.IsNullOrEmpty(originalToolName)
            ? $"[mcp:{serverName}/{originalToolName}]"
            : $"[mcp:{qualifiedName}]";
        var body = string.IsNullOrEmpty(tool.Description)
            ? "(no description supplied by the MCP server)"
            : tool.Description;
        return $"{prefix} {body}";
    }

    /// <summary>
    /// Namespace a server-local tool name with its server prefix to
    /// avoid collisions across servers + with builtins. Format:
    /// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>. Pure — exported for tests.
    /// </summary>
    public static string QualifyToolName(string serverName, string toolName)
    {
        // Restrict server name + tool name to a tool-safe charset.
        // OpenAI / Anthropic require [a-zA-Z0-9_-] for tool names.
        var safeServer = SanitizeForToolName(serverName);
        var safeTool = SanitizeForToolName(toolName);
        return $"mcp__{safeServer}__{safeTool}";
    }

    private static string SanitizeForToolName(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var c in _clients)
        {
            try { c.Dispose(); } catch { }
        }
        _clients.Clear();
    }
}
