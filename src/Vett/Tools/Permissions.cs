using System.Text.Json;
using System.Threading.Channels;

namespace Vett.Tools;

/// <summary>
/// Permission system that gates tool calls in interactive chat. The
/// agent loop consults a <see cref="PermissionGate"/> before each tool
/// invocation; auto-allow rules pass through, ask-rules emit a
/// `permission_request` event and block until the user answers via
/// the chat UI, deny-rules return a synthesized error to the agent
/// without invoking the tool.
///
/// Designed so:
/// - Benchmark runs (`vett run`) skip the system entirely — no gate
///   is wired into <see cref="Vett.Agent.AgentEnvironment"/>.
/// - Chat sessions get the gate when the profile carries a
///   `permissions:` block (or vett-chat synthesizes one). Absent block
///   = no gating, preserves the pre-#5 default-allow behavior.
/// - The classifier maps tool name + args to a small set of
///   <see cref="PermissionKind"/> values. Edits / writes are Ask by
///   default; reads + safe terminal are Auto by default.
/// - "Safe terminal" is a conservative allowlist of read-only commands
///   (ls, cat, git status, etc) — anything else is unsafe.
/// </summary>
public enum PermissionKind
{
    /// <summary>Read-only file ops (file_editor view) and similar inspection tools.</summary>
    Read,
    /// <summary>Mutations: file_editor create/str_replace/insert/undo_edit, update_memory.</summary>
    Edit,
    /// <summary>Terminal call whose command parses as a known-safe read-only operation.</summary>
    TerminalSafe,
    /// <summary>Terminal call whose command isn't in the safe allowlist.</summary>
    TerminalUnsafe,
    /// <summary>MCP tool call (#21 — placeholder; v1 has no MCP wiring yet).</summary>
    Mcp,
    /// <summary>Control-plane / conversational tools (think, finish, ask_user_question, task_tracker, leader tools). Default Auto.</summary>
    Other,
}

public enum PermissionRule
{
    /// <summary>Tool runs without prompting.</summary>
    Auto,
    /// <summary>Tool blocks until the user answers via chat. Default for risky kinds.</summary>
    Ask,
    /// <summary>Tool returns a synthesized error to the agent without running.</summary>
    Deny,
}

/// <summary>
/// Per-kind rule set. Mutable so an "Allow this kind for the rest of
/// the session" answer in chat can flip the rule to Auto without
/// touching the profile YAML on disk. The chat panel re-pushes its
/// preferred set on every spawn — host is the source of truth for
/// per-session overrides; profile YAML is the source of truth for
/// per-profile defaults.
/// </summary>
public sealed class Permissions
{
    public PermissionRule Read { get; set; } = PermissionRule.Auto;
    public PermissionRule Edit { get; set; } = PermissionRule.Ask;
    public PermissionRule TerminalSafe { get; set; } = PermissionRule.Auto;
    public PermissionRule TerminalUnsafe { get; set; } = PermissionRule.Ask;
    public PermissionRule Mcp { get; set; } = PermissionRule.Ask;
    public PermissionRule Other { get; set; } = PermissionRule.Auto;

    public PermissionRule For(PermissionKind kind) => kind switch
    {
        PermissionKind.Read => Read,
        PermissionKind.Edit => Edit,
        PermissionKind.TerminalSafe => TerminalSafe,
        PermissionKind.TerminalUnsafe => TerminalUnsafe,
        PermissionKind.Mcp => Mcp,
        _ => Other,
    };

    public void Set(PermissionKind kind, PermissionRule rule)
    {
        switch (kind)
        {
            case PermissionKind.Read: Read = rule; break;
            case PermissionKind.Edit: Edit = rule; break;
            case PermissionKind.TerminalSafe: TerminalSafe = rule; break;
            case PermissionKind.TerminalUnsafe: TerminalUnsafe = rule; break;
            case PermissionKind.Mcp: Mcp = rule; break;
            default: Other = rule; break;
        }
    }

    public static PermissionRule Parse(string? raw, PermissionRule fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw.Trim().ToLowerInvariant() switch
        {
            "auto" or "allow" or "yes" or "true" => PermissionRule.Auto,
            "ask" or "prompt" or "confirm" => PermissionRule.Ask,
            "deny" or "block" or "no" or "false" => PermissionRule.Deny,
            _ => fallback,
        };
    }
}

/// <summary>
/// Classifies tool calls into <see cref="PermissionKind"/> values.
/// Pure static — no IO, no state — so it's trivially testable.
/// </summary>
public static class PermissionClassifier
{
    public static PermissionKind Classify(string toolName, IReadOnlyDictionary<string, object?> args)
    {
        switch (toolName)
        {
            case "terminal":
            case "bash_background":
                {
                    // bash_background uses the same classifier as terminal —
                    // it just runs the command in the background. The
                    // companion monitor / kill / jobs tools are handled
                    // separately as Read / Other.
                    var cmd = args.TryGetValue("command", out var v) ? v?.ToString() ?? "" : "";
                    if (!IsSafeTerminalCommand(cmd)) return PermissionKind.TerminalUnsafe;
                    // Among safe commands, file-content dumpers (cat / head /
                    // tail / less / more / strings / xxd / od / hexdump) are
                    // routed to the Read kind so a "Read: ask" rule actually
                    // gates "anything that exposes file contents to the
                    // model." Other safe commands (ls / pwd / git status /
                    // npm list / etc.) stay as TerminalSafe — they read
                    // state, not file bodies.
                    if (IsFileContentDumpCommand(cmd)) return PermissionKind.Read;
                    return PermissionKind.TerminalSafe;
                }
            case "file_editor":
                {
                    var op = args.TryGetValue("command", out var v) ? v?.ToString() ?? "" : "";
                    return op == "view" ? PermissionKind.Read : PermissionKind.Edit;
                }
            case "update_memory":
                return PermissionKind.Edit;
            case "monitor":
            case "bash_jobs":
                // Read-only inspection of a running / completed job that
                // was already allowed when bash_background ran. Don't
                // re-prompt for "look at output of a thing you let me start."
                return PermissionKind.Read;
            case "bash_kill":
            case "think":
            case "finish":
            case "task_tracker":
            case "ask_user_question":
            case "assign_task":
            case "assign_async":
            case "cancel_task":
            case "inject_into_task":
            case "check_task":
            case "check_tasks":
            case "wait_task":
            case "report_progress":
                return PermissionKind.Other;
            default:
                if (toolName.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase))
                    return PermissionKind.Mcp;
                // Unknown tool name — treat as Other (Auto by default).
                // Plugin tools land here; user can override via the
                // permission rules if they want stricter gating.
                return PermissionKind.Other;
        }
    }

    /// <summary>
    /// Allowlist-based "is this terminal command safe to auto-run?"
    /// classifier. Conservative — anything with shell metacharacters
    /// (pipes, redirects, command chaining, command substitution),
    /// or whose first token isn't in the read-only allowlist, is
    /// classified unsafe.
    ///
    /// `git` gets a sub-allowlist because `git status` is safe but
    /// `git push` / `git checkout` / `git reset --hard` aren't.
    /// </summary>
    public static bool IsSafeTerminalCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var trimmed = command.Trim();

        // Reject anything with shell metacharacters that could chain,
        // pipe, or redirect to another command. We're not trying to
        // build a shell parser — these heuristics are a safety net
        // against the agent constructing a "safe-prefix" + "; rm -rf"
        // compound command.
        // `&` (background), `&&`, `||`, `;`, `|`, `>`, `<`, backticks, `$()`.
        foreach (var ch in trimmed)
        {
            if (ch == '&' || ch == ';' || ch == '|' || ch == '>' || ch == '<' || ch == '`') return false;
        }
        if (trimmed.Contains("$(")) return false;

        // Tokenize on whitespace. Safe commands have a known first
        // token; some have a constrained second token (git, npm).
        var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        var head = parts[0].ToLowerInvariant();

        // Stand-alone read-only utilities. `find` and `grep`/`rg`/`ag`
        // can be coerced into mutation via -exec or -delete, so we
        // additionally veto args containing -exec / -delete / -ok.
        if (head is "ls" or "cat" or "head" or "tail" or "less" or "more"
            or "file" or "stat" or "wc" or "tree"
            or "pwd" or "whoami" or "hostname" or "uname" or "which" or "type" or "id"
            or "echo" or "printf" or "env" or "date" or "true" or "false"
            or "tr" or "cut" or "sort" or "uniq" or "tee"
            or "diff" or "cmp"
            or "tsc" or "eslint")
        {
            return true;
        }
        if (head is "find" or "grep" or "rg" or "ripgrep" or "ag" or "ack")
        {
            // Reject -exec / -delete / -ok / -execdir which all run
            // arbitrary commands.
            foreach (var p in parts)
            {
                if (p == "-exec" || p == "-execdir" || p == "-delete" || p == "-ok" || p == "-okdir") return false;
            }
            return true;
        }
        if (head == "git")
        {
            if (parts.Length < 2) return false;
            var sub = parts[1].ToLowerInvariant();
            return sub is "status" or "log" or "diff" or "show" or "branch"
                or "blame" or "ls-files" or "ls-tree" or "rev-parse" or "rev-list"
                or "symbolic-ref" or "config" or "remote" or "describe"
                or "shortlog" or "tag" or "stash" or "reflog" or "for-each-ref"
                or "cat-file" or "ls-remote" or "fsck";
        }
        if (head is "npm" or "pnpm" or "yarn")
        {
            if (parts.Length < 2) return false;
            var sub = parts[1].ToLowerInvariant();
            return sub is "list" or "ls" or "view" or "info" or "search" or "outdated" or "config" or "which";
        }
        if (head is "python" or "python3" or "node" or "deno" or "bun")
        {
            // -V / --version / -h / --help are all safe.
            if (parts.Length >= 2)
            {
                var sub = parts[1].ToLowerInvariant();
                if (sub is "-v" or "--version" or "-h" or "--help" or "version") return true;
            }
            return parts.Length == 1; // bare invocation = REPL but they exit on EOF
        }
        if (head is "pip" or "pip3")
        {
            if (parts.Length < 2) return false;
            var sub = parts[1].ToLowerInvariant();
            return sub is "list" or "show" or "freeze" or "config" or "--version" or "-V" or "search";
        }
        if (head == "go")
        {
            if (parts.Length < 2) return false;
            var sub = parts[1].ToLowerInvariant();
            return sub is "version" or "env" or "list" or "doc" or "vet" or "fmt";
        }
        if (head == "dotnet")
        {
            if (parts.Length < 2) return false;
            var sub = parts[1].ToLowerInvariant();
            return sub is "--version" or "--info" or "--list-sdks" or "--list-runtimes" or "list" or "tool";
        }
        if (head == "cargo")
        {
            if (parts.Length < 2) return false;
            var sub = parts[1].ToLowerInvariant();
            return sub is "--version" or "metadata" or "tree" or "search";
        }
        if (head == "docker")
        {
            if (parts.Length < 2) return false;
            var sub = parts[1].ToLowerInvariant();
            return sub is "ps" or "images" or "logs" or "inspect" or "version" or "info" or "stats" or "top";
        }
        return false;
    }

    /// <summary>
    /// True when the command's first token is a file-content dumper —
    /// reading the command's output exposes the file's bytes to the
    /// model in the same way `file_editor view` does. Used to route
    /// these commands through the Read permission kind so a "Read:
    /// ask" rule gates them. Caller is expected to have already
    /// confirmed the command is metachar-safe via
    /// <see cref="IsSafeTerminalCommand"/>.
    ///
    /// Limited to the dumpers already on the safe allowlist (cat /
    /// head / tail / less / more). Less common dumpers (strings, xxd,
    /// od, hexdump, bat) aren't safe-listed so they go through
    /// TerminalUnsafe and the user gets prompted regardless.
    /// </summary>
    public static bool IsFileContentDumpCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var trimmed = command.Trim();
        var idx = trimmed.IndexOf(' ');
        var head = (idx < 0 ? trimmed : trimmed[..idx]).ToLowerInvariant();
        return head is "cat" or "head" or "tail" or "less" or "more";
    }
}

/// <summary>
/// Round-trip service: emit a `permission_request` event, await the
/// chat UI's `permission_response` carrying the same request_id.
/// Mirrors the shape of <see cref="UserQuestionService"/> intentionally.
/// </summary>
public sealed class PermissionService
{
    // ROUTED BY ID, not a shared queue.
    //
    // This was an unbounded Channel with every waiter reading from the
    // same stream and writing back anything addressed to someone else.
    // Two independent defects came out of that, both measured
    // 2026-08-26 (RoundTripRequeueTests):
    //
    //   1. Re-queuing inside the read loop means the very next ReadAsync
    //      returns the same item, and on an unbounded channel BOTH calls
    //      complete synchronously — so the `await`s never yield. With no
    //      suspension point the method never returns its Task at all: it
    //      runs an infinite loop on the agent loop's own thread. The
    //      first test written for it deadlocked its own test thread,
    //      which is the symptom exactly. One orphaned reply is enough to
    //      trigger it — a double-clicked Allow, or a reply for a call
    //      cancelled before the answer landed — and nothing ever drains
    //      it, so it poisons the session.
    //
    //   2. Parking mismatches instead of re-queuing fixes the spin but
    //      DEADLOCKS sibling waiters: with two outstanding requests each
    //      waiter takes the other's reply out of the channel and then
    //      blocks holding it. That is not hypothetical — it is what the
    //      handoff tests caught when the park fix was tried.
    //
    // A dictionary keyed by request_id has neither problem: a reply goes
    // straight to the one waiter it names, nobody reads anyone else's
    // mail, and a waiter with nothing addressed to it simply blocks.
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<PermissionResponse>> _waiters = new();

    /// <summary>
    /// Replies that arrived before anyone was awaiting them. Keeps the
    /// old channel's tolerance for a UI that answers faster than the
    /// agent loop reaches its await, and gives orphans somewhere inert
    /// to sit — they are keyed, never scanned, so an orphan costs one
    /// dictionary entry instead of an infinite loop.
    /// </summary>
    private readonly Dictionary<string, PermissionResponse> _unclaimed = new();

    public async Task<PermissionResponse> AwaitDecisionAsync(string requestId, CancellationToken ct)
    {
        TaskCompletionSource<PermissionResponse> tcs;
        lock (_lock)
        {
            if (_unclaimed.Remove(requestId, out var alreadyAnswered)) return alreadyAnswered;
            if (!_waiters.TryGetValue(requestId, out tcs!))
            {
                // RunContinuationsAsynchronously: PostDecision is called
                // from the stdio reader loop, and we must not run the
                // agent's continuation on its thread.
                tcs = new TaskCompletionSource<PermissionResponse>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[requestId] = tcs;
            }
        }

        using var registration = ct.Register(static s =>
            ((TaskCompletionSource<PermissionResponse>)s!).TrySetCanceled(), tcs);
        try
        {
            return await tcs.Task;
        }
        finally
        {
            lock (_lock) { _waiters.Remove(requestId); }
        }
    }

    public void PostDecision(string requestId, PermissionResponse response)
    {
        lock (_lock)
        {
            if (_waiters.TryGetValue(requestId, out var tcs) && tcs.TrySetResult(response))
            {
                _waiters.Remove(requestId);
                return;
            }
            _unclaimed[requestId] = response;
        }
    }
}

/// <summary>
/// Wire shape for the chat UI's response. `Decision` is the literal
/// answer for THIS call. `RememberForKind`, when true, flips the
/// per-session rule so subsequent same-kind calls don't re-prompt.
/// "Allow always for this session" sets RememberForKind=true with
/// Decision=Allow; "Deny always" sets it true with Decision=Deny.
/// </summary>
public sealed record PermissionResponse(PermissionRule Decision, bool RememberForKind);

/// <summary>
/// Glue between the agent loop, the per-session rule set, and the
/// round-trip service. The loop calls <see cref="CheckAsync"/> before
/// each tool invocation; the gate handles the auto/ask/deny logic
/// AND the "remember for this session" rule mutation.
/// </summary>
public sealed class PermissionGate
{
    public Permissions Rules { get; }
    public PermissionService Service { get; }
    private readonly Action<string, Dictionary<string, object?>> _emit;

    public PermissionGate(Permissions rules, PermissionService service, Action<string, Dictionary<string, object?>> emit)
    {
        Rules = rules;
        Service = service;
        _emit = emit;
    }

    /// <summary>Allow / Deny for the call. Loop synthesizes an error observation on Deny.</summary>
    public async Task<PermissionRule> CheckAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken ct)
    {
        var kind = PermissionClassifier.Classify(toolName, args);
        var rule = Rules.For(kind);
        if (rule == PermissionRule.Auto) return PermissionRule.Auto;
        if (rule == PermissionRule.Deny) return PermissionRule.Deny;

        // Ask path — emit a permission_request event with the request
        // id so the UI can pair it up with our await on the channel.
        var requestId = Guid.NewGuid().ToString("N");
        _emit("permission_request", new()
        {
            ["request_id"] = requestId,
            ["tool_name"] = toolName,
            ["kind"] = kind.ToString(),
            ["arguments"] = args.ToDictionary(kv => kv.Key, kv => kv.Value),
            ["preview"] = BuildPreview(toolName, args),
        });

        PermissionResponse response;
        try
        {
            response = await Service.AwaitDecisionAsync(requestId, ct);
        }
        catch (OperationCanceledException)
        {
            // Session ending mid-question — return a Deny to the loop
            // so the agent sees a synthesized error rather than
            // hanging.
            return PermissionRule.Deny;
        }

        // Persist for the rest of this session if the user picked an
        // "always" answer. Lets the UI offer "Allow this kind" /
        // "Deny this kind" toggles without us having to re-prompt.
        if (response.RememberForKind && response.Decision != PermissionRule.Ask)
        {
            Rules.Set(kind, response.Decision);
        }

        return response.Decision == PermissionRule.Ask ? PermissionRule.Auto : response.Decision;
    }

    /// <summary>Short, human-readable preview shown in the chat card. Tool-specific.</summary>
    private static string BuildPreview(string toolName, IReadOnlyDictionary<string, object?> args)
    {
        switch (toolName)
        {
            case "terminal":
            case "bash_background":
                {
                    var cmd = args.TryGetValue("command", out var v) ? v?.ToString() ?? "" : "";
                    var name = args.TryGetValue("name", out var n) ? n?.ToString() : null;
                    var label = string.IsNullOrWhiteSpace(name)
                        ? cmd
                        : $"[{name}] {cmd}";
                    return label.Length <= 200 ? label : label[..200] + $"…[+{label.Length - 200}]";
                }
            case "file_editor":
                {
                    var op = args.TryGetValue("command", out var v) ? v?.ToString() ?? "" : "";
                    var path = args.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "";
                    return $"{op} {path}".Trim();
                }
            case "update_memory":
                {
                    var name = args.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
                    var del = args.TryGetValue("delete", out var d) && d is bool b && b;
                    return del ? $"delete memory: {name}" : $"save memory: {name}";
                }
            default:
                {
                    // Best-effort: first string-valued arg.
                    foreach (var (k, v) in args)
                    {
                        if (v is string s && !string.IsNullOrWhiteSpace(s))
                            return $"{k}={Trunc(s, 100)}";
                        if (v is JsonElement je && je.ValueKind == JsonValueKind.String)
                            return $"{k}={Trunc(je.GetString() ?? "", 100)}";
                    }
                    return toolName;
                }
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
