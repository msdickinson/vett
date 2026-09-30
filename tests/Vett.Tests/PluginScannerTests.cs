using Vett.Plugin;

namespace Vett.Tests;

public class PluginScannerTests : IDisposable
{
    private readonly string _tempDir;

    public PluginScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vett-scan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_tempDir, "tools"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "middlewares"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Theory]
    [InlineData("search.py", Lang.Python)]
    [InlineData("build.go", Lang.Go)]
    [InlineData("format.ts", Lang.TypeScript)]
    [InlineData("lint.js", Lang.TypeScript)]
    [InlineData("check.rs", Lang.Rust)]
    [InlineData("validate.rb", Lang.Ruby)]
    [InlineData("bash.yaml", Lang.Yaml)]
    [InlineData("run.cs", Lang.CSharp)]
    [InlineData("readme.md", Lang.Unknown)]
    public void DetectsLanguageFromExtension(string filename, Lang expected)
    {
        Assert.Equal(expected, Scanner.Detect(filename));
    }

    [Fact]
    public void ScansToolsDirectory()
    {
        File.WriteAllText(Path.Combine(_tempDir, "tools", "search.py"), "# a python tool\n");
        File.WriteAllText(Path.Combine(_tempDir, "tools", "lint.go"), "// a go tool\n");

        var plugins = Scanner.Scan(_tempDir);

        Assert.Equal(2, plugins.Count);
        Assert.Contains(plugins, p => p.Name == "search" && p.Language == Lang.Python);
        Assert.Contains(plugins, p => p.Name == "lint" && p.Language == Lang.Go);
        Assert.All(plugins, p => Assert.Equal("tool", p.Kind));
    }

    [Fact]
    public void ScansMiddlewaresDirectory()
    {
        File.WriteAllText(Path.Combine(_tempDir, "middlewares", "limiter.py"), "# middleware\n");

        var plugins = Scanner.Scan(_tempDir);

        Assert.Single(plugins);
        Assert.Equal("middleware", plugins[0].Kind);
    }

    [Fact]
    public void ExtractsVettDirectives()
    {
        var content = """
            #vett:tool search "Search the codebase"
            #vett:param query string "The search pattern"
            #vett:param max_results integer "Max results to return"

            import subprocess
            def main():
                pass
            """;

        File.WriteAllText(Path.Combine(_tempDir, "tools", "search.py"), content);

        var plugins = Scanner.Scan(_tempDir);

        Assert.Single(plugins);
        Assert.Equal("search", plugins[0].Name);
        Assert.Equal(2, plugins[0].Params.Count);
        Assert.Equal("query", plugins[0].Params[0].Name);
        Assert.Equal("string", plugins[0].Params[0].Type);
        Assert.Equal("The search pattern", plugins[0].Params[0].Desc);
    }

    [Fact]
    public void IgnoresUnknownExtensions()
    {
        File.WriteAllText(Path.Combine(_tempDir, "tools", "readme.md"), "# not a tool\n");
        File.WriteAllText(Path.Combine(_tempDir, "tools", "data.json"), "{}");

        var plugins = Scanner.Scan(_tempDir);

        Assert.Empty(plugins);
    }

    [Fact]
    public void EmptyDirectoryReturnsEmpty()
    {
        var plugins = Scanner.Scan(_tempDir);

        Assert.Empty(plugins);
    }
}
