namespace Vett.Config;

/// <summary>
/// Loads VETT.md / AGENTS.md project-instruction files and stitches them
/// into the system prompt. Modeled after Claude Code's CLAUDE.md, Cursor's
/// .cursorrules, and the AGENTS.md convention used by Codex CLI / others
/// for cross-tool interop.
///
/// Lookup model:
///
///   1. Workspace root is detected by walking up from <c>cwd</c> looking
///      for a <c>.git</c> directory. If found, every ancestor between the
///      workspace root and cwd is included (parent → cwd order, so closer-
///      to-cwd rules sit later in the prompt and get slight weight).
///   2. If no <c>.git</c> is found, only <c>cwd</c> itself is considered
///      (avoids accidentally loading rules from random filesystem
///      ancestors like <c>/home</c> or <c>C:\</c>).
///   3. <c>~/.vett/AGENTS.md</c> and <c>~/.vett/VETT.md</c> are appended
///      as user-global baselines. If <c>cwd</c> is already under
///      <c>~/.vett/</c>, the ancestor walk de-duplicates via path-set.
///
/// Per directory, four candidates are checked (any combination may exist):
///     <c>&lt;dir&gt;/AGENTS.md</c>
///     <c>&lt;dir&gt;/VETT.md</c>
///     <c>&lt;dir&gt;/.vett/AGENTS.md</c>
///     <c>&lt;dir&gt;/.vett/VETT.md</c>
///
/// Optional Cursor-MDC-style YAML frontmatter is supported on each file:
///
///     ---
///     description: short summary of the rule
///     alwaysApply: true
///     globs: ["**/*.py"]
///     ---
///
///     # body...
///
/// For v1, <c>alwaysApply: false</c> causes the file to be skipped (the
/// "agent-requested" load model would need a tool call and is deferred).
/// <c>globs</c> is parsed but not enforced — chat sessions don't have a
/// stable "currently editing" path the way file-attached rules in IDEs
/// do, so for v1 we always include matching files. <c>description</c>
/// is appended to the source header so the agent knows what the rule
/// is for at a glance.
///
/// Files that don't exist or fail to read are silently skipped — best-
/// effort by design. Concatenated content is wrapped in
/// &lt;project_instructions&gt; tags and prepended to the profile's system
/// prompt — same shape as Claude Code's CLAUDE.md.
/// </summary>
public static class ProjectInstructions
{
    /// <summary>
    /// Resolve all project-instruction content for a given working
    /// directory. Returns empty when nothing applies.
    /// </summary>
    public static string Load(string cwd)
    {
        var pieces = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in WalkWorkspace(cwd))
        {
            foreach (var path in CandidatesIn(dir))
            {
                AddPiece(pieces, path, seen);
            }
        }

        // User-global baseline. The ancestor walk already covers this
        // when cwd is under ~, hence the seen-set dedup.
        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? "";
        if (!string.IsNullOrEmpty(home))
        {
            AddPiece(pieces, Path.Combine(home, ".vett", "AGENTS.md"), seen);
            AddPiece(pieces, Path.Combine(home, ".vett", "VETT.md"), seen);
        }

        if (pieces.Count == 0) return "";
        return string.Join("\n\n---\n\n", pieces);
    }

    /// <summary>
    /// Wrap loaded content in a &lt;project_instructions&gt; block and
    /// prepend to the given system prompt. Returns the system prompt
    /// unchanged when no rule files exist.
    /// </summary>
    public static string Apply(string systemPrompt, string cwd)
    {
        var instructions = Load(cwd);
        if (string.IsNullOrEmpty(instructions)) return systemPrompt;
        return
            "<project_instructions>\n" +
            instructions + "\n" +
            "</project_instructions>\n\n" +
            systemPrompt;
    }

    /// <summary>The candidate paths Load checks (in walk order). Exposed
    /// so /stats-style commands can show what was loaded vs ignored.</summary>
    public static IEnumerable<string> ResolvePaths(string cwd)
    {
        foreach (var dir in WalkWorkspace(cwd))
            foreach (var p in CandidatesIn(dir))
                yield return p;
        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? "";
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".vett", "AGENTS.md");
            yield return Path.Combine(home, ".vett", "VETT.md");
        }
    }

    private static void AddPiece(List<string> pieces, string path, HashSet<string> seen)
    {
        try { path = Path.GetFullPath(path); } catch { return; }
        if (!seen.Add(path)) return;
        if (!File.Exists(path)) return;
        string raw;
        try { raw = File.ReadAllText(path); }
        catch { return; }

        var (meta, body) = ParseFrontmatter(raw);
        body = body.Trim();
        if (string.IsNullOrWhiteSpace(body)) return;

        // alwaysApply: false → opt out of always-on loading. The v2
        // "agent-requested" path would surface this rule via a tool call;
        // for now we just skip.
        if (meta.TryGetValue("alwaysApply", out var aa)
            && bool.TryParse(aa, out var alwaysApply)
            && !alwaysApply)
        {
            return;
        }

        var header = $"# Source: {path}";
        if (meta.TryGetValue("description", out var desc) && !string.IsNullOrEmpty(desc))
            header += $" — {desc}";

        pieces.Add($"{header}\n\n{body}");
    }

    private static IEnumerable<string> CandidatesIn(string dir)
    {
        yield return Path.Combine(dir, "AGENTS.md");
        yield return Path.Combine(dir, "VETT.md");
        yield return Path.Combine(dir, ".vett", "AGENTS.md");
        yield return Path.Combine(dir, ".vett", "VETT.md");
    }

    /// <summary>
    /// Walk from <c>cwd</c> up to the nearest <c>.git</c> ancestor (which
    /// defines the workspace root). Returns the directories from
    /// workspace-root → cwd (so the caller can iterate parent-first).
    ///
    /// If no <c>.git</c> is found anywhere up the chain, returns just
    /// <c>cwd</c> — we do NOT walk the entire filesystem. That avoids
    /// surprises like loading <c>/home/AGENTS.md</c> or
    /// <c>C:\AGENTS.md</c> if a stray file exists somewhere up the tree.
    /// </summary>
    public static List<string> WalkWorkspace(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return new List<string>();
        try { cwd = Path.GetFullPath(cwd); } catch { return new List<string>(); }

        // Probe for the workspace root (nearest .git ancestor, including cwd).
        string? root = null;
        var probe = cwd;
        while (!string.IsNullOrEmpty(probe))
        {
            if (Directory.Exists(Path.Combine(probe, ".git"))
                || File.Exists(Path.Combine(probe, ".git"))) // worktree marker file
            {
                root = probe;
                break;
            }
            var parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || parent == probe) break;
            probe = parent;
        }

        if (root is null) return new List<string> { cwd };

        // Walk from cwd up to (and including) root, then reverse so the
        // caller sees root-first (parent-most → cwd-most).
        var dirs = new List<string>();
        var current = cwd;
        while (!string.IsNullOrEmpty(current))
        {
            dirs.Add(current);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
        dirs.Reverse();
        return dirs;
    }

    /// <summary>
    /// Parse a Cursor-MDC-style YAML frontmatter block at the top of a
    /// rule file. Recognizes a leading <c>---\n...\n---\n</c> envelope
    /// and pulls out simple <c>key: value</c> pairs (one per line). NOT
    /// a full YAML parser — values that look like JSON arrays / mapping
    /// blocks are treated as raw strings, which is enough for the v1
    /// fields we care about (<c>description</c>, <c>alwaysApply</c>,
    /// <c>globs</c>).
    ///
    /// Returns the extracted metadata plus the remaining body. If no
    /// frontmatter is present the metadata is empty and the original
    /// text is returned unchanged.
    /// </summary>
    public static (Dictionary<string, string> Meta, string Body) ParseFrontmatter(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return (meta, text ?? "");

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0] != "---") return (meta, text);

        var i = 1;
        while (i < lines.Length && lines[i] != "---")
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var k = line[..colon].Trim();
                var v = line[(colon + 1)..].Trim();
                // Strip matching surrounding quotes for clarity. v1 doesn't
                // need to support multi-line strings or list syntax — the
                // raw value (e.g. `["**/*.py"]`) is fine for the consumers.
                if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0])
                    v = v[1..^1];
                if (!string.IsNullOrEmpty(k)) meta[k] = v;
            }
            i++;
        }
        // Closing '---' (if present) is at index i; body starts at i+1.
        // Missing closing fence → treat the whole file as body (no meta).
        if (i >= lines.Length) return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), text);
        var body = string.Join('\n', lines.Skip(i + 1));
        return (meta, body);
    }
}
