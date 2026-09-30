using System.Text.Json;
using Vett.Mcp;

namespace Vett.Tests;

/// <summary>
/// Integration test proving <see cref="McpStdioClient.ConnectAsync"/> can
/// spawn the Python static-analysis MCP server (`static-analysis-python`)
/// and complete the initialize + tools/list handshake.
///
/// Added 2026-07-05 after a full A/B run silently failed with
/// "MCP server 'analyzer' failed to connect: A task was canceled." —
/// there was no integration coverage for McpStdioClient before, so nobody
/// noticed that the Windows path had a real spawn / handshake issue.
///
/// Skipped when the analyzer repo isn't present at the expected local
/// checkout path (CI without the sibling repo).
/// </summary>
public class McpAnalyzerIntegrationTest
{
    private static string AnalyzerRepoPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME") ?? "",
            "Documents", "source", "DickinsonBros", "apps", "static-analysis-python");

    [Fact]
    public async Task ConnectAsync_analyzer_completes_initialize_and_tools_list()
    {
        var repo = AnalyzerRepoPath;
        if (!Directory.Exists(repo))
        {
            // Analyzer repo not available on this host — nothing to test.
            return;
        }

        var env = new Dictionary<string, string> { ["PYTHONPATH"] = repo };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var client = await McpStdioClient.ConnectAsync(
            "analyzer",
            "python -m src.server --mcp",
            env,
            connectTimeoutSeconds: 20,
            ct: cts.Token,
            verbose: true);

        var tools = await client.ListToolsAsync(cts.Token);
        Assert.NotEmpty(tools);
        // Analyzer emits Q1-Q7 + snapshot/diff/inventory = 14 tools.
        Assert.Contains(tools, t => t.Name == "q1_signature");
        Assert.Contains(tools, t => t.Name == "q7_flask_endpoints");
        Assert.Contains(tools, t => t.Name == "snapshot");
        Assert.Contains(tools, t => t.Name == "inventory");
    }

    [Fact]
    public async Task ConnectAsync_analyzer_lists_snapshot_and_diff_and_inventory_tools()
    {
        // Regression pin for the T2/T3 tool additions — makes sure the
        // schema-of-tools shape flowing through McpStdioClient into
        // Coordinator matches what the analyzer emits.
        var repo = AnalyzerRepoPath;
        if (!Directory.Exists(repo)) return;

        var env = new Dictionary<string, string> { ["PYTHONPATH"] = repo };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = await McpStdioClient.ConnectAsync(
            "analyzer", "python -m src.server --mcp", env,
            connectTimeoutSeconds: 20, ct: cts.Token, verbose: false);

        var tools = await client.ListToolsAsync(cts.Token);
        var names = tools.Select(t => t.Name).ToHashSet();
        Assert.Contains("snapshot", names);
        Assert.Contains("diff", names);
        Assert.Contains("inventory", names);
    }
}
