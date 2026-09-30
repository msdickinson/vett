namespace Vett.Plugin;

public enum Lang { Go, Python, CSharp, TypeScript, Rust, Ruby, Yaml, Unknown }

public sealed record PluginInfo(
    string Name,
    string Kind,
    Lang Language,
    string Path,
    List<(string Name, string Type, string Desc)> Params);

/// <summary>
/// Scans workspace tools/ and middlewares/ directories for plugin files.
/// Detects language from file extension, extracts metadata from comments.
/// </summary>
public static class Scanner
{
    private static readonly (string Ext, Lang L)[] ExtMap =
    [
        (".go", Lang.Go),
        (".py", Lang.Python),
        (".cs", Lang.CSharp),
        (".ts", Lang.TypeScript),
        (".js", Lang.TypeScript),
        (".rs", Lang.Rust),
        (".rb", Lang.Ruby),
        (".yaml", Lang.Yaml),
        (".yml", Lang.Yaml),
    ];

    public static Lang Detect(string file)
    {
        foreach (var (ext, lang) in ExtMap)
        {
            if (file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return lang;
        }
        return Lang.Unknown;
    }

    public static List<PluginInfo> Scan(string workDir)
    {
        var plugins = new List<PluginInfo>();

        foreach (var (dir, kind) in new[] { ("tools", "tool"), ("middlewares", "middleware") })
        {
            var d = System.IO.Path.Combine(workDir, dir);
            if (!Directory.Exists(d))
                continue;

            foreach (var f in Directory.GetFileSystemEntries(d))
            {
                var lang = Directory.Exists(f) ? DetectSubdir(f) : Detect(f);
                if (lang == Lang.Unknown)
                    continue;

                plugins.Add(ExtractMeta(f, kind, lang));
            }
        }

        return plugins;
    }

    private static Lang DetectSubdir(string dir)
    {
        var candidates = new[]
        {
            ("main.go", Lang.Go),
            ("main.py", Lang.Python),
            ("Program.cs", Lang.CSharp),
            ("index.ts", Lang.TypeScript),
            ("main.rs", Lang.Rust),
        };

        return candidates
            .Where(c => File.Exists(System.IO.Path.Combine(dir, c.Item1)))
            .Select(c => c.Item2)
            .FirstOrDefault(Lang.Unknown);
    }

    private static PluginInfo ExtractMeta(string path, string kind, Lang lang)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var pars = new List<(string, string, string)>();

        if (!Directory.Exists(path))
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var c = line.TrimStart().TrimStart('/', '#', '*').TrimStart();

                    if (c.StartsWith("vett:tool "))
                    {
                        var parts = SplitDirective(c["vett:tool ".Length..]);
                        if (parts.Count > 0) name = parts[0];
                    }

                    if (c.StartsWith("vett:param "))
                    {
                        var parts = SplitDirective(c["vett:param ".Length..]);
                        if (parts.Count >= 2)
                            pars.Add((parts[0], parts[1], parts.Count > 2 ? parts[2] : ""));
                    }
                }
            }
            catch { }
        }

        return new PluginInfo(name, kind, lang, path, pars);
    }

    private static List<string> SplitDirective(string s)
    {
        var result = new List<string>();
        foreach (var part in s.Split('"')
            .SelectMany((v, i) => i % 2 == 0
                ? v.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : new[] { v }))
        {
            result.Add(part);
        }
        return result;
    }
}
