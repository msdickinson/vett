using System.Text;
using System.Text.RegularExpressions;

namespace Vett.Config;

/// <summary>
/// Aider-inspired repo map. Walks the workspace, extracts top-level
/// symbol signatures from common languages via regex, ranks files by
/// signature density, and renders a compact text block to inject into
/// the agent's system prompt.
///
/// Scope: regex extractors are deliberately lossy. They catch the
/// common shapes — `export function`, `class Foo`, `def bar(`, `pub fn`,
/// `public class`, `func F(`, etc — which gives the agent enough
/// grounding ("here's the shape of the project") without us shipping a
/// tree-sitter dependency. False positives (regex hitting something
/// inside a comment) are acceptable; false negatives (esoteric syntax)
/// are acceptable too. Aider's PageRank weighting is deferred — v1
/// ranks by raw signature count per file, top-N files until the
/// character budget is hit.
///
/// Output cap: <c>maxChars</c> (default 4000 ≈ ~1k tokens). Files
/// processed in workspace-walk order; truncation is "stop including
/// new files when the budget runs out."
/// </summary>
public static class RepoMap
{
    /// <summary>Default character budget for the rendered map. ~1k
    /// tokens at GPT-4 char-per-token ratio.</summary>
    public const int DefaultMaxChars = 4000;

    /// <summary>Files larger than this are skipped at extraction time —
    /// big bundled files (lockfiles, vendor blobs, generated outputs)
    /// flood the regex with thousands of false positives. Source files
    /// rarely cross this line; production assemblies + test files all
    /// fit comfortably under 256KB.</summary>
    public const long MaxFileBytes = 256 * 1024;

    /// <summary>Extension → language label. Drives both extractor
    /// selection and the markdown code-fence label in the rendered
    /// output. Order doesn't matter; extension lookup is hash-based.</summary>
    private static readonly Dictionary<string, string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ts"] = "typescript", [".tsx"] = "typescript",
        [".js"] = "javascript", [".jsx"] = "javascript", [".mjs"] = "javascript",
        [".py"] = "python",
        [".cs"] = "csharp",
        [".go"] = "go",
        [".rs"] = "rust",
        [".java"] = "java",
        [".rb"] = "ruby",
        [".kt"] = "kotlin", [".kts"] = "kotlin",
        [".swift"] = "swift",
    };

    /// <summary>Directories skipped during the walk. Mirror the
    /// excludes WorktreeManager uses for the cp-fallback copy.</summary>
    public static readonly HashSet<string> DefaultExcludeDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", ".vs", ".vscode-test",
        ".next", ".nuxt", ".turbo", ".cache", ".idea", ".vett",
        ".git", "target", "build", ".gradle", ".pytest_cache", "__pycache__",
        "venv", ".venv", "env", "vendor",
    };

    /// <summary>Walk + extract + rank + render. Returns an empty string
    /// when no signatures were found — the chat-prompt builder treats
    /// empty as "skip injection entirely."</summary>
    public static string Build(string workspaceRoot, int maxChars = DefaultMaxChars, IEnumerable<string>? extraExcludeDirs = null, RepoMapRanking ranking = RepoMapRanking.References)
    {
        if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return string.Empty;

        var excludes = new HashSet<string>(DefaultExcludeDirs, StringComparer.OrdinalIgnoreCase);
        if (extraExcludeDirs is not null)
        {
            foreach (var x in extraExcludeDirs) excludes.Add(x);
        }

        var files = WalkSourceFiles(workspaceRoot, excludes).ToList();
        var entries = new List<FileEntry>();
        // For the References ranker we need to keep file content in
        // memory long enough to do the cross-file scan. Density mode
        // discards content immediately so we don't spend memory on
        // anything we won't use.
        var contentByPath = ranking == RepoMapRanking.References
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : null;
        foreach (var (relPath, fullPath, language) in files)
        {
            try
            {
                var info = new FileInfo(fullPath);
                if (info.Length > MaxFileBytes) continue;
                var content = File.ReadAllText(fullPath);
                var signatures = ExtractSignatures(content, language);
                if (signatures.Count == 0) continue;
                entries.Add(new FileEntry(relPath, language, signatures));
                contentByPath?.Add(relPath, content);
            }
            catch { /* unreadable file — skip silently */ }
        }

        if (ranking == RepoMapRanking.References && contentByPath is not null && entries.Count > 0)
        {
            return RenderRanked(entries, contentByPath, maxChars);
        }

        // Density: rank by signature count descending, then by path for determinism.
        entries.Sort((a, b) =>
        {
            var cmp = b.Signatures.Count.CompareTo(a.Signatures.Count);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Path, b.Path);
        });

        return Render(entries, maxChars);
    }

    /// <summary>Wrap the rendered map in the same envelope shape used by
    /// project instructions / persistent memory. Returns the original
    /// system prompt unchanged when the map is empty.</summary>
    public static string Apply(string systemPrompt, string workspaceRoot, int maxChars = DefaultMaxChars, IEnumerable<string>? extraExcludeDirs = null, RepoMapRanking ranking = RepoMapRanking.References)
    {
        var body = Build(workspaceRoot, maxChars, extraExcludeDirs, ranking);
        if (string.IsNullOrEmpty(body)) return systemPrompt ?? string.Empty;
        var envelope = "<repo_map>\n" + body + "\n</repo_map>\n\n";
        return envelope + (systemPrompt ?? string.Empty);
    }

    /// <summary>Recursive workspace walk. Skips directories in
    /// <c>excludes</c> at every level. Yields source files only —
    /// extension must be in <see cref="KnownExtensions"/>. Returns
    /// (workspace-relative path, absolute path, language).</summary>
    private static IEnumerable<(string Rel, string Full, string Language)> WalkSourceFiles(string root, HashSet<string> excludes)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subdirs;
            string[] entries;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                entries = Directory.GetFiles(dir);
            }
            catch
            {
                continue; // permission denied / etc — skip silently
            }
            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (excludes.Contains(name)) continue;
                if (name.StartsWith(".") && !excludes.Contains(name)) continue; // skip dotdirs by default
                stack.Push(sub);
            }
            foreach (var path in entries)
            {
                var ext = Path.GetExtension(path);
                if (!KnownExtensions.TryGetValue(ext, out var lang)) continue;
                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                yield return (rel, path, lang);
            }
        }
    }

    /// <summary>Apply the right per-language extractor. Returns at most
    /// 12 signatures per file — enough to convey shape, capped to keep
    /// the budget small. Each signature is a one-line string already
    /// stripped of trailing whitespace.</summary>
    public static IReadOnlyList<string> ExtractSignatures(string source, string language)
    {
        var raw = language switch
        {
            "typescript" or "javascript" => ExtractTsJs(source),
            "python" => ExtractPython(source),
            "csharp" => ExtractCSharp(source),
            "go" => ExtractGo(source),
            "rust" => ExtractRust(source),
            "java" or "kotlin" => ExtractJavaLike(source),
            "ruby" => ExtractRuby(source),
            "swift" => ExtractSwift(source),
            _ => new List<string>(),
        };
        // Cap per-file. Big files (e.g. an index that re-exports
        // hundreds of symbols) shouldn't dominate the budget.
        const int PerFileCap = 12;
        if (raw.Count <= PerFileCap) return raw;
        return raw.Take(PerFileCap).ToList();
    }

    // --- Per-language extractors -----------------------------------

    // Common shapes only. These are heuristics, not parsers. They
    // accept some false positives (matches inside string literals or
    // comments) in exchange for being trivially testable + fast.
    // Pattern conventions: anchored to ^ at line start (multi-line
    // mode); capture the signature line up to a reasonable boundary.

    private static readonly Regex TsJsRegex = new(
        @"^(?:export\s+(?:default\s+)?)?(?:async\s+)?(?:function\s+(\w+)\s*\([^)]*\)|class\s+(\w+)(?:\s+extends\s+\w+)?|interface\s+(\w+)|type\s+(\w+)\s*=|const\s+(\w+)\s*=\s*(?:async\s*)?\([^)]*\)\s*=>|enum\s+(\w+))",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static List<string> ExtractTsJs(string source)
    {
        var seen = new HashSet<string>();
        var out_ = new List<string>();
        foreach (Match m in TsJsRegex.Matches(source))
        {
            var line = TrimSig(SafeLine(source, m.Index + m.Length - 1));
            if (line.Length == 0 || !seen.Add(line)) continue;
            out_.Add(line);
        }
        return out_;
    }

    private static readonly Regex PythonRegex = new(
        @"^[ \t]*(?:async\s+)?(?:def\s+\w+\s*\([^)]*\)(?:\s*->\s*[^:]+)?|class\s+\w+(?:\([^)]*\))?)\s*:",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static List<string> ExtractPython(string source)
    {
        var seen = new HashSet<string>();
        var out_ = new List<string>();
        foreach (Match m in PythonRegex.Matches(source))
        {
            // Python decorators / leading whitespace: only count
            // top-level + class-method-level. Skip if indented past
            // 4 spaces (heuristic for nested helpers).
            var line = SafeLine(source, m.Index + m.Length - 1);
            var indent = line.Length - line.TrimStart().Length;
            if (indent > 4) continue;
            var trimmed = TrimSig(line);
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            out_.Add(trimmed);
        }
        return out_;
    }

    private static readonly Regex CSharpRegex = new(
        @"^\s*(?:public|internal|protected|private|static|sealed|abstract|virtual|override|async|partial|\s)+\s*(?:class|interface|struct|record|enum)\s+\w+|^\s*(?:public|internal|protected|private)\s+(?:static\s+)?(?:async\s+)?(?:override\s+)?(?:virtual\s+)?(?:[^\s(]+\s+)+\w+\s*\([^)]*\)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static List<string> ExtractCSharp(string source)
    {
        var seen = new HashSet<string>();
        var out_ = new List<string>();
        foreach (Match m in CSharpRegex.Matches(source))
        {
            var line = TrimSig(SafeLine(source, m.Index + m.Length - 1));
            if (line.Length == 0 || !seen.Add(line)) continue;
            out_.Add(line);
        }
        return out_;
    }

    private static readonly Regex GoRegex = new(
        @"^(?:func\s+(?:\([^)]+\)\s+)?\w+\s*\([^)]*\)|type\s+\w+\s+(?:struct|interface)|var\s+\w+|const\s+\w+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static List<string> ExtractGo(string source)
    {
        var seen = new HashSet<string>();
        var out_ = new List<string>();
        foreach (Match m in GoRegex.Matches(source))
        {
            var line = TrimSig(SafeLine(source, m.Index + m.Length - 1));
            if (line.Length == 0 || !seen.Add(line)) continue;
            out_.Add(line);
        }
        return out_;
    }

    private static readonly Regex RustRegex = new(
        @"^(?:pub\s+)?(?:async\s+)?(?:fn\s+\w+|struct\s+\w+|enum\s+\w+|trait\s+\w+|impl(?:\s*<[^>]+>)?\s+(?:\w+\s+for\s+)?\w+|type\s+\w+|const\s+\w+|static\s+\w+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static List<string> ExtractRust(string source)
    {
        var seen = new HashSet<string>();
        var out_ = new List<string>();
        foreach (Match m in RustRegex.Matches(source))
        {
            var line = TrimSig(SafeLine(source, m.Index + m.Length - 1));
            if (line.Length == 0 || !seen.Add(line)) continue;
            out_.Add(line);
        }
        return out_;
    }

    private static readonly Regex JavaLikeRegex = new(
        @"^\s*(?:public|protected|private|abstract|final|static|sealed)?\s*(?:class|interface|enum|record)\s+\w+|^\s*(?:public|protected|private)\s+(?:static\s+)?(?:final\s+)?(?:[^\s(]+\s+)+\w+\s*\([^)]*\)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static List<string> ExtractJavaLike(string source)
    {
        var seen = new HashSet<string>();
        var out_ = new List<string>();
        foreach (Match m in JavaLikeRegex.Matches(source))
        {
            var line = TrimSig(SafeLine(source, m.Index + m.Length - 1));
            if (line.Length == 0 || !seen.Add(line)) continue;
            out_.Add(line);
        }
        return out_;
    }

    private static readonly Regex RubyRegex = new(
        @"^\s*(?:def\s+(?:self\.)?\w+(?:\s*\([^)]*\))?|class\s+\w+(?:\s*<\s*\w+)?|module\s+\w+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static List<string> ExtractRuby(string source)
    {
        var seen = new HashSet<string>();
        var out_ = new List<string>();
        foreach (Match m in RubyRegex.Matches(source))
        {
            var line = TrimSig(SafeLine(source, m.Index + m.Length - 1));
            if (line.Length == 0 || !seen.Add(line)) continue;
            out_.Add(line);
        }
        return out_;
    }

    private static readonly Regex SwiftRegex = new(
        @"^\s*(?:public|internal|fileprivate|private|open)?\s*(?:func\s+\w+|class\s+\w+|struct\s+\w+|enum\s+\w+|protocol\s+\w+|extension\s+\w+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static List<string> ExtractSwift(string source)
    {
        var seen = new HashSet<string>();
        var out_ = new List<string>();
        foreach (Match m in SwiftRegex.Matches(source))
        {
            var line = TrimSig(SafeLine(source, m.Index + m.Length - 1));
            if (line.Length == 0 || !seen.Add(line)) continue;
            out_.Add(line);
        }
        return out_;
    }

    /// <summary>The physical line containing position `idx`. Robust to
    /// idx pointing at a newline (which happens when a regex with
    /// `\s*` between alternations spans line boundaries — `^` matches
    /// at position 0, `\s*` swallows leading newlines, the match's
    /// reported index is at the very start). Callers pass `Match.Index
    /// + Match.Length - 1` so the line we extract is the one
    /// containing the END of the match (i.e. the symbol name) rather
    /// than the start (which can be a blank line above).</summary>
    private static string SafeLine(string source, int idx)
    {
        if (idx < 0 || source.Length == 0) return "";
        if (idx >= source.Length) idx = source.Length - 1;
        // Newlines are line-terminators, not starters — back up one
        // char so the line lookup lands on the line that just ended.
        if (source[idx] == '\n' && idx > 0) idx--;
        var start = source.LastIndexOf('\n', idx) + 1;
        var endNl = source.IndexOf('\n', idx);
        var end = endNl < 0 ? source.Length : endNl;
        if (end < start) return "";
        return source.Substring(start, end - start).Trim('\r');
    }

    /// <summary>Truncate signatures longer than 200 chars (rare but
    /// possible for verbose generic / templated declarations) so a
    /// single line doesn't dominate the budget.</summary>
    private static string TrimSig(string s)
    {
        var trimmed = s.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }

    private sealed record FileEntry(string Path, string Language, IReadOnlyList<string> Signatures);

    /// <summary>
    /// Ranking strategy for the repo map. <c>Density</c> is the
    /// session-12 v1 behavior — order files by raw signature count.
    /// <c>References</c> (default as of session 16's #14 close-out) is
    /// a PageRank-style heuristic: weight each file by how often its
    /// symbol names appear in OTHER files. A widely-imported util module
    /// surfaces above a giant feature file with no consumers.
    /// </summary>
    public enum RepoMapRanking
    {
        Density,
        References,
    }

    /// <summary>
    /// PageRank-style ranking. For each file, sum the cross-file
    /// occurrences of its symbol names — a name that appears in N
    /// other files contributes N to the file's score. The composite
    /// rank is `signature_count + reference_score`, so files that
    /// have many definitions AND are widely used dominate. Ties
    /// broken by path for determinism.
    ///
    /// True PageRank would iterate to a fixed point; we don't —
    /// one pass is enough to bubble the "core utility" files above
    /// the "leaf feature" files in practice. v3 candidate: weighted
    /// graph + power iteration.
    /// </summary>
    private static string RenderRanked(
        IReadOnlyList<FileEntry> entries,
        Dictionary<string, string> contentByPath,
        int maxChars)
    {
        // First pass: extract symbol names from every entry's
        // signatures. We build a (name → file) map so the cross-file
        // counter doesn't accidentally count a symbol's appearance in
        // its OWN defining file (those are the signatures themselves).
        var nameToFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            foreach (var sig in e.Signatures)
            {
                var name = ExtractSymbolName(sig);
                if (string.IsNullOrEmpty(name) || name.Length < 3) continue; // skip noisy short names
                if (!nameToFile.ContainsKey(name)) nameToFile[name] = e.Path;
            }
        }

        // Second pass: for each entry, sum cross-file references to its
        // symbol names. Cross-file = the symbol appears in any file
        // OTHER than the one where it was defined.
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            int score = 0;
            foreach (var sig in e.Signatures)
            {
                var name = ExtractSymbolName(sig);
                if (string.IsNullOrEmpty(name) || name.Length < 3) continue;
                // Count files (not occurrences) where name appears.
                // File-count is a more honest signal than occurrence-
                // count: a symbol mentioned 100 times in one consumer
                // is still one consumer.
                foreach (var (path, content) in contentByPath)
                {
                    if (path == e.Path) continue;
                    if (CountWordOccurrence(content, name) > 0) score++;
                }
            }
            scores[e.Path] = score;
        }

        var ranked = entries
            .OrderByDescending(e => e.Signatures.Count + scores.GetValueOrDefault(e.Path, 0))
            .ThenBy(e => e.Path, StringComparer.Ordinal)
            .ToList();

        return RenderWithScores(ranked, scores, maxChars);
    }

    /// <summary>
    /// Pull the canonical symbol name out of a signature line.
    /// Walks the line for the first token after a "definition keyword"
    /// (function / class / interface / def / fn / type / struct /
    /// enum / trait / etc) and returns it. Returns empty string when
    /// no clear match — the caller skips such signatures rather than
    /// pollute the ranker with garbage.
    ///
    /// Pure / exported for tests.
    /// </summary>
    public static string ExtractSymbolName(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return string.Empty;

        // Pass 1: kind-keyword followed by name. These are
        // unambiguous: `function foo`, `class Foo`, `def bar`, etc.
        const string KindKeywords = "function|class|interface|type|def|fn|struct|enum|trait|impl|module|record|protocol|extension";
        var kindMatch = System.Text.RegularExpressions.Regex.Match(
            signature,
            $@"\b(?:{KindKeywords})\s+([a-zA-Z_][a-zA-Z0-9_]*)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (kindMatch.Success) return kindMatch.Groups[1].Value;

        // Pass 2: a chain of modifier keywords ending at the name.
        // Stack semantics matter — `public async doThing()` should
        // capture `doThing`, not `async`. Greedy non-capturing repeat
        // consumes every modifier; the last identifier on the line
        // wins. Includes `const`/`let`/`var` (not technically modifiers
        // but they precede a name in TS/JS top-level declarations).
        const string Modifiers = "public|private|internal|protected|async|static|abstract|sealed|virtual|override|export|default|const|let|var";
        var modMatch = System.Text.RegularExpressions.Regex.Match(
            signature,
            $@"\b(?:{Modifiers})\s+(?:(?:{Modifiers})\s+)*([a-zA-Z_][a-zA-Z0-9_]*)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (modMatch.Success) return modMatch.Groups[1].Value;

        // Pass 3: decorator-prefixed signatures (Python @decorator,
        // TS/Java @Annotation). Skip the decorator(s) on the leading
        // line, return the first identifier on a SUBSEQUENT line.
        // Without this, `@something\ndecorated method` would capture
        // `something` instead of `decorated`.
        var lines = signature.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith('@'))
                continue; // decorator line — skip
            var fb = System.Text.RegularExpressions.Regex.Match(line, @"\b([a-zA-Z_][a-zA-Z0-9_]{2,})\b");
            if (fb.Success) return fb.Groups[1].Value;
        }
        return string.Empty;
    }

    /// <summary>
    /// Count word-boundary matches of <paramref name="word"/> in
    /// <paramref name="content"/>. Stops counting after one match
    /// since the cross-file reference scorer only asks "does it
    /// appear?" Pure helper — exported for tests.
    /// </summary>
    public static int CountWordOccurrence(string content, string word)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(word)) return 0;
        var idx = content.IndexOf(word, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var prevOk = idx == 0 || !IsIdentChar(content[idx - 1]);
            var endIdx = idx + word.Length;
            var nextOk = endIdx >= content.Length || !IsIdentChar(content[endIdx]);
            if (prevOk && nextOk) return 1;
            idx = content.IndexOf(word, idx + 1, StringComparison.Ordinal);
        }
        return 0;
    }

    private static bool IsIdentChar(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Render with per-file reference annotations so the agent
    /// sees the ranking signal alongside the symbols. Same envelope
    /// shape as <see cref="Render"/>; just adds a `(refs: N)` suffix
    /// to each file header.</summary>
    private static string RenderWithScores(
        IReadOnlyList<FileEntry> entries,
        IReadOnlyDictionary<string, int> scores,
        int maxChars)
    {
        if (entries.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("## Workspace symbol map");
        sb.AppendLine("Top-level signatures from source files in this workspace, ranked by signature count + cross-file references (a symbol referenced in many other files boosts its file's rank). Use it as a quick orientation aid — not authoritative; read the actual files when in doubt.");
        sb.AppendLine();
        var truncated = false;
        foreach (var e in entries)
        {
            if (sb.Length > maxChars) { truncated = true; break; }
            var refs = scores.GetValueOrDefault(e.Path, 0);
            if (refs > 0)
                sb.Append("### ").Append(e.Path).Append(" _(refs: ").Append(refs).AppendLine(")_");
            else
                sb.Append("### ").AppendLine(e.Path);
            foreach (var sig in e.Signatures)
            {
                if (sb.Length > maxChars) { truncated = true; break; }
                sb.Append("  ").AppendLine(sig);
            }
            sb.AppendLine();
        }
        if (truncated)
        {
            sb.AppendLine($"[truncated — capped at ~{maxChars} chars]");
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>Render the ranked entries as a markdown-ish text block.
    /// Stops including new files once `maxChars` is approached. Each
    /// file gets a `### <path>` header and indented signatures.</summary>
    private static string Render(IReadOnlyList<FileEntry> entries, int maxChars)
    {
        if (entries.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("## Workspace symbol map");
        sb.AppendLine("Top-level signatures from source files in this workspace, ranked by signature count. Use it as a quick orientation aid — it isn't authoritative; read the actual files when in doubt.");
        sb.AppendLine();
        var truncated = false;
        foreach (var e in entries)
        {
            if (sb.Length > maxChars) { truncated = true; break; }
            sb.Append("### ").AppendLine(e.Path);
            foreach (var sig in e.Signatures)
            {
                if (sb.Length > maxChars) { truncated = true; break; }
                sb.Append("  ").AppendLine(sig);
            }
            sb.AppendLine();
        }
        if (truncated)
        {
            sb.AppendLine($"[truncated — capped at ~{maxChars} chars]");
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}
