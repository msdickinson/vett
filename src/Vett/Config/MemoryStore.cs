using System.Text;
using System.Text.RegularExpressions;

namespace Vett.Config;

/// <summary>
/// Persistent memory across sessions. Modeled after the Claude Code
/// auto-memory system: an agent keeps small markdown files describing
/// who the user is, what corrections they've given, what's going on
/// with the project, and where to look for external info; an index
/// (<c>MEMORY.md</c>) groups them by type.
///
/// Storage layout (workspace-scoped, in repo if the user wants):
///
///   &lt;workspace&gt;/.vett/memory/
///   ├── MEMORY.md                # index — one-line entry per memory
///   ├── user_role.md             # individual memory files
///   ├── feedback_terse.md
///   ├── project_release_freeze.md
///   └── reference_grafana_url.md
///
/// Each memory file carries a YAML frontmatter envelope identical to
/// the AGENTS.md format from <see cref="ProjectInstructions"/>:
///
///     ---
///     name: feedback_terse
///     type: feedback
///     description: User prefers terse responses with no trailing summaries
///     ---
///
///     &lt;markdown body&gt;
///
/// At session start the index is auto-loaded and prepended to the
/// system prompt (capped at 200 lines / 25KB so a runaway memory
/// doesn't crowd out everything else). The agent uses the
/// <c>update_memory</c> tool to add / update / delete entries.
/// Agent calls itself, no host plumbing needed beyond tool
/// registration.
/// </summary>
public static class MemoryStore
{
    public const int IndexLineCap = 200;
    public const int IndexCharCap = 25 * 1024;

    public static readonly string[] AllowedTypes = ["user", "feedback", "project", "reference"];

    public static string MemoryDir(string cwd) => Path.Combine(cwd, ".vett", "memory");
    public static string IndexPath(string cwd) => Path.Combine(MemoryDir(cwd), "MEMORY.md");
    public static string TopicPath(string cwd, string slug) => Path.Combine(MemoryDir(cwd), slug + ".md");

    /// <summary>
    /// Convert a free-form memory name into a filesystem-safe slug.
    /// Lowercase, alphanumerics + underscore + hyphen only; collapses
    /// runs of non-allowed chars to a single underscore. Empty input
    /// returns the empty string — callers must check.
    /// </summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var lower = name.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lower, @"[^a-z0-9_-]+", "_");
        slug = Regex.Replace(slug, @"_+", "_").Trim('_', '-');
        return slug;
    }

    /// <summary>
    /// Save (or overwrite) a single memory entry. Writes the topic
    /// file with frontmatter + body, and refreshes the corresponding
    /// line in MEMORY.md (creating the index if missing).
    ///
    /// Returns a short status string suitable for tool-result text.
    /// Throws on filesystem permission failures so the agent sees
    /// the underlying error.
    /// </summary>
    public static string Save(string cwd, string name, string type, string description, string content)
    {
        var slug = Sanitize(name);
        if (string.IsNullOrEmpty(slug))
            throw new ArgumentException("name must contain at least one alphanumeric character");
        var t = NormalizeType(type);
        Directory.CreateDirectory(MemoryDir(cwd));

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(slug).Append('\n');
        sb.Append("type: ").Append(t).Append('\n');
        if (!string.IsNullOrEmpty(description))
            sb.Append("description: ").Append(EscapeYamlScalar(description)).Append('\n');
        sb.Append("---\n\n");
        sb.Append(content?.Trim() ?? "");
        sb.Append('\n');
        File.WriteAllText(TopicPath(cwd, slug), sb.ToString());

        UpdateIndex(cwd, slug, t, description, remove: false);
        return $"Saved memory: {slug} ({t})";
    }

    /// <summary>Delete a memory entry by name. Removes the topic file
    /// and the index line if either exists. Returns a status string.</summary>
    public static string Delete(string cwd, string name)
    {
        var slug = Sanitize(name);
        if (string.IsNullOrEmpty(slug))
            throw new ArgumentException("name must contain at least one alphanumeric character");
        var path = TopicPath(cwd, slug);
        var existed = File.Exists(path);
        if (existed)
        {
            try { File.Delete(path); }
            catch (IOException) { /* best-effort — index update still useful */ }
        }
        UpdateIndex(cwd, slug, "", "", remove: true);
        return existed ? $"Removed memory: {slug}" : $"Memory '{slug}' did not exist (index entry removed if present).";
    }

    /// <summary>
    /// Load the MEMORY.md index for inclusion in the system prompt.
    /// Returns the empty string if no index exists. Capped at
    /// <see cref="IndexLineCap"/> lines / <see cref="IndexCharCap"/>
    /// chars; truncation is marked inline so the agent knows there's
    /// more it can read with <c>file_editor</c>.
    /// </summary>
    public static string LoadIndex(string cwd)
    {
        var path = IndexPath(cwd);
        if (!File.Exists(path)) return "";
        string raw;
        try { raw = File.ReadAllText(path); }
        catch { return ""; }
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var truncated = false;
        if (lines.Length > IndexLineCap)
        {
            lines = lines.Take(IndexLineCap).ToArray();
            truncated = true;
        }
        var trimmed = string.Join('\n', lines);
        if (trimmed.Length > IndexCharCap)
        {
            trimmed = trimmed[..IndexCharCap];
            truncated = true;
        }
        if (truncated)
            trimmed += "\n[…MEMORY.md truncated — agent can read the rest with file_editor view .vett/memory/MEMORY.md]";
        return trimmed;
    }

    /// <summary>
    /// Wrap loaded memory in a <c>&lt;persistent_memory&gt;</c> block
    /// and prepend to the given system prompt. Returns the prompt
    /// unchanged when no index exists. Mirrors
    /// <see cref="ProjectInstructions.Apply"/> shape.
    /// </summary>
    public static string Apply(string systemPrompt, string cwd)
    {
        var memory = LoadIndex(cwd);
        if (string.IsNullOrEmpty(memory)) return systemPrompt;
        return
            "<persistent_memory>\n" +
            "Auto-loaded from .vett/memory/MEMORY.md. Use the `update_memory` tool to add, update, or remove entries — entries persist across sessions.\n\n" +
            memory + "\n" +
            "</persistent_memory>\n\n" +
            systemPrompt;
    }

    private static string NormalizeType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "project";
        var t = type.Trim().ToLowerInvariant();
        return AllowedTypes.Contains(t) ? t : "project";
    }

    private static string EscapeYamlScalar(string s)
    {
        // Conservative: if the string contains any of `:#"[]{},&*!|>'%@`
        // / leading whitespace / a colon followed by space, wrap in
        // double quotes and escape `"` + `\`. Otherwise emit as plain.
        if (string.IsNullOrEmpty(s)) return "";
        var needsQuoting =
            s.IndexOf(':') >= 0 || s.IndexOf('#') >= 0 ||
            s.IndexOfAny(new[] { '"', '\'', '\\', '\n', '\r' }) >= 0 ||
            (s.Length > 0 && (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1])));
        if (!needsQuoting) return s;
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
    }

    /// <summary>
    /// Refresh the MEMORY.md index. Adds (or replaces) the entry's
    /// line under its type heading; or removes the entry's line if
    /// <paramref name="remove"/> is true. The index is regenerated
    /// from scratch each call so type-section headings, ordering,
    /// and de-duplication stay consistent.
    /// </summary>
    private static void UpdateIndex(string cwd, string slug, string type, string description, bool remove)
    {
        var dir = MemoryDir(cwd);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        // Discover all current memory files so the index always
        // reflects what's actually on disk. Any *.md other than
        // MEMORY.md is treated as a memory file; type/description
        // come from its frontmatter.
        var entries = new List<(string Slug, string Type, string Description)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(name, "MEMORY", StringComparison.OrdinalIgnoreCase)) continue;
            var (meta, _) = ProjectInstructions.ParseFrontmatter(File.ReadAllText(file));
            var t = meta.TryGetValue("type", out var tv) && AllowedTypes.Contains(tv.ToLowerInvariant()) ? tv.ToLowerInvariant() : "project";
            var d = meta.TryGetValue("description", out var dv) ? dv : "";
            entries.Add((name, t, d));
        }

        // If we're saving, the topic file we just wrote is already on
        // disk so it will appear in the enumeration above. If we're
        // removing, the topic file has already been deleted.
        // No additional bookkeeping needed.

        // Group by type in the canonical order.
        var sb = new StringBuilder();
        sb.Append("# Project memory\n\n");
        sb.Append("Auto-maintained by the agent's `update_memory` tool. Each entry below points at a topic file in this directory.\n\n");

        foreach (var t in AllowedTypes)
        {
            var group = entries.Where(e => e.Type == t).OrderBy(e => e.Slug, StringComparer.Ordinal).ToList();
            if (group.Count == 0) continue;
            sb.Append("## ").Append(Capitalize(t)).Append("\n\n");
            foreach (var e in group)
            {
                var safeDesc = string.IsNullOrEmpty(e.Description) ? "(no description)" : e.Description.Replace("\n", " ").Trim();
                sb.Append("- [").Append(e.Slug).Append(".md](").Append(e.Slug).Append(".md) — ").Append(safeDesc).Append('\n');
            }
            sb.Append('\n');
        }

        File.WriteAllText(IndexPath(cwd), sb.ToString());
    }

    private static string Capitalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}
