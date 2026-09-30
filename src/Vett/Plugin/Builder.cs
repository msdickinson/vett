using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Vett.Plugin;

public sealed record BuildResult(
    string Name,
    Lang Language,
    bool Built,
    bool Cached,
    string? Skipped,
    Exception? Error);

/// <summary>
/// Compiles workspace plugins. Checks SDK availability, caches by
/// source file hash, runs the appropriate build command per language.
/// </summary>
public static class Builder
{
    private static readonly (Lang L, string Sdk, string Hint, bool NeedsBuild)[] Langs =
    [
        (Lang.Go, "go", "winget install GoLang.Go", true),
        (Lang.Python, "python", "winget install Python.Python.3.12", false),
        (Lang.CSharp, "dotnet", "winget install Microsoft.DotNet.SDK.10", true),
        (Lang.TypeScript, "node", "winget install OpenJS.NodeJS", false),
        (Lang.Rust, "cargo", "curl https://sh.rustup.rs -sSf | sh", true),
        (Lang.Ruby, "ruby", "winget install RubyInstallerTeam.Ruby", false),
        (Lang.Yaml, "", "", false),
    ];

    public static List<BuildResult> Build(List<PluginInfo> plugins, string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        var hashDir = System.IO.Path.Combine(cacheDir, "hashes");
        Directory.CreateDirectory(hashDir);

        var results = new List<BuildResult>();

        foreach (var p in plugins)
        {
            var info = Langs.FirstOrDefault(l => l.L == p.Language);

            // Check SDK.
            if (info.Sdk is { Length: > 0 } && !SdkAvailable(info.Sdk))
            {
                results.Add(new(p.Name, p.Language, false, false,
                    $"{p.Language} not found. Install: {info.Hint}", null));
                continue;
            }

            // Interpreted — no build step.
            if (!info.NeedsBuild)
            {
                results.Add(new(p.Name, p.Language, true, false, null, null));
                continue;
            }

            // Cache check.
            var hash = HashFile(p.Path);
            var hf = System.IO.Path.Combine(hashDir, p.Name + ".sha256");
            if (File.Exists(hf) && File.ReadAllText(hf) == hash)
            {
                results.Add(new(p.Name, p.Language, true, true, null, null));
                continue;
            }

            // Build.
            var (cmd, args) = BuildCmd(p.Language, p.Path, cacheDir);
            try
            {
                RunProcess(cmd, args);
                File.WriteAllText(hf, hash);
                results.Add(new(p.Name, p.Language, true, false, null, null));
            }
            catch (Exception ex)
            {
                results.Add(new(p.Name, p.Language, false, false, null, ex));
            }
        }

        return results;
    }

    public static (string Cmd, string[] Args) RunCmd(Lang lang, string path)
    {
        return lang switch
        {
            Lang.Go or Lang.Rust => (path, []),
            Lang.Python => (OperatingSystem.IsWindows() ? "python" : "python3", [path]),
            Lang.CSharp => ("dotnet", ["run", "--project", path, "--no-build"]),
            Lang.TypeScript => System.IO.Path.GetExtension(path) == ".ts"
                ? ("npx", ["tsx", path])
                : ("node", [path]),
            Lang.Ruby => ("ruby", [path]),
            _ => (path, []),
        };
    }

    public static void InstallPackages(Lang lang, string dir)
    {
        // Build arg arrays directly — splitting on space corrupts quoted paths
        // (Windows paths like "C:\Program Files\..." become multiple broken args).
        var (file, cmd, args) = lang switch
        {
            Lang.Python when File.Exists(Path.Combine(dir, "requirements.txt"))
                => ("requirements.txt",
                    OperatingSystem.IsWindows() ? "python" : "python3",
                    new[] { "-m", "pip", "install", "-r", Path.Combine(dir, "requirements.txt"), "-q" }),

            Lang.TypeScript when File.Exists(Path.Combine(dir, "package.json"))
                => ("package.json", "npm", new[] { "install", "--prefix", dir, "--silent" }),

            Lang.Go when File.Exists(Path.Combine(dir, "go.mod"))
                => ("go.mod", "go", new[] { "mod", "download" }),

            Lang.Ruby when File.Exists(Path.Combine(dir, "Gemfile"))
                => ("Gemfile", "bundle", new[] { "install", "--gemfile", Path.Combine(dir, "Gemfile"), "--quiet" }),

            _ => ("", "", Array.Empty<string>()),
        };

        if (file.Length > 0)
        {
            try { RunProcess(cmd, args); }
            catch { /* best effort */ }
        }
    }

    // --- Internals ---

    private static (string Cmd, string[] Args) BuildCmd(Lang lang, string src, string cacheDir)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(src);
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";

        return lang switch
        {
            Lang.Go => ("go", ["build", "-o", System.IO.Path.Combine(cacheDir, name + ext), src]),
            Lang.CSharp => ("dotnet", ["build", Directory.Exists(src) ? src : System.IO.Path.GetDirectoryName(src)!, "-o", cacheDir, "-q"]),
            Lang.Rust => ("cargo", ["build", "--manifest-path", src, "--target-dir", cacheDir, "--release"]),
            _ => ("", []),
        };
    }

    public static bool SdkAvailable(string cmd)
    {
        // Most tools accept "--version", but Go uses bare "version" and exits
        // non-zero on unknown flags. Map the special cases so the SDK check
        // doesn't false-negative for installed Go.
        var flag = cmd.Equals("go", StringComparison.OrdinalIgnoreCase) ? "version" : "--version";
        try
        {
            var psi = new ProcessStartInfo(cmd, flag)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string HashFile(string path)
    {
        try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(); }
        catch { return ""; }
    }

    private static void RunProcess(string cmd, string[] args)
    {
        var psi = new ProcessStartInfo(cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Build failed: could not start {cmd}");

        if (!p.WaitForExit(60000))
        {
            try { p.Kill(); } catch { }
            throw new InvalidOperationException($"Build failed: {cmd} timed out after 60s");
        }

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Build failed: {p.StandardError.ReadToEnd()}");
    }
}
