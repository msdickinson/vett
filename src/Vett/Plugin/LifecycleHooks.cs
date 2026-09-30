using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Vett.Config;
using YamlDotNet.Serialization;

namespace Vett.Plugin;

/// <summary>
/// The six lifecycle events VETT emits. Hooks declared in the
/// profile's <c>hooks:</c> block subscribe to these by name.
///
/// Event semantics:
/// <list type="bullet">
///   <item><c>PreToolUse</c> — fires before each tool dispatch with the call envelope.
///   Decision honored: allow / deny / modify (rewrite arguments).</item>
///   <item><c>PostToolUse</c> — fires after each tool dispatch with the call + result.
///   Decision ignored in v1 (side-effects only — auditing, telemetry).</item>
///   <item><c>UserPromptSubmit</c> — fires when a chat user message arrives, BEFORE
///   the agent loop sees it. Decision honored: allow / deny / modify (rewrite text).</item>
///   <item><c>SessionStart</c> — fires once at chat-session boot. Side-effects only.</item>
///   <item><c>SessionEnd</c> — fires once when the panel closes. Side-effects only.</item>
///   <item><c>Stop</c> — fires when the loop sets a StopReason. Side-effects only.</item>
/// </list>
/// </summary>
public enum LifecycleHookEvent
{
    PreToolUse,
    PostToolUse,
    UserPromptSubmit,
    SessionStart,
    SessionEnd,
    Stop,
}

/// <summary>YAML schema for a single hook subscription.</summary>
public sealed class LifecycleHookConfig
{
    [YamlMember(Alias = "command")] public string Command { get; set; } = "";
    [YamlMember(Alias = "timeout_seconds")] public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Optional human-readable label; surfaces in logs + the
    /// chat permission card if the hook denies. Defaults to a slug
    /// derived from the command.</summary>
    [YamlMember(Alias = "name")] public string? Name { get; set; }
}

/// <summary>YAML schema for <see cref="Profile.Hooks"/>.</summary>
public sealed class HooksConfig
{
    [YamlMember(Alias = "pre_tool_use")] public List<LifecycleHookConfig>? PreToolUse { get; set; }
    [YamlMember(Alias = "post_tool_use")] public List<LifecycleHookConfig>? PostToolUse { get; set; }
    [YamlMember(Alias = "user_prompt_submit")] public List<LifecycleHookConfig>? UserPromptSubmit { get; set; }
    [YamlMember(Alias = "session_start")] public List<LifecycleHookConfig>? SessionStart { get; set; }
    [YamlMember(Alias = "session_end")] public List<LifecycleHookConfig>? SessionEnd { get; set; }
    [YamlMember(Alias = "stop")] public List<LifecycleHookConfig>? Stop { get; set; }
}

/// <summary>
/// One hook's decision after running. <c>Action</c> is one of:
/// <list type="bullet">
///   <item><c>allow</c> — proceed unchanged. Default if the hook returns
///   nothing usable (empty stdout, malformed JSON, timeout).</item>
///   <item><c>deny</c> — block the operation. <c>Reason</c> is surfaced as the
///   observation (for tool calls) or as a notification toast (for user
///   prompts).</item>
///   <item><c>modify</c> — proceed with the modified payload. <c>Modified</c>
///   carries the rewritten envelope.</item>
/// </list>
/// </summary>
public sealed record LifecycleHookDecision(string Action, string? Reason, JsonElement? Modified)
{
    public static LifecycleHookDecision Allow() => new("allow", null, null);
    public static LifecycleHookDecision Deny(string reason) => new("deny", reason, null);
    public static LifecycleHookDecision Modify(JsonElement modified) => new("modify", null, modified);
}

/// <summary>
/// Spawns hook commands as host subprocesses, pipes the event payload
/// to stdin as JSON, parses the JSON response from stdout, and
/// aggregates multi-hook decisions left-to-right (first deny wins;
/// modifies pipeline).
///
/// Fail-open is the v1 default: hook timeouts, malformed JSON, or
/// non-zero exits all return <c>allow</c>. A broken hook should never
/// deadlock the user. The hook's stderr is captured and logged but
/// not forwarded to the agent.
/// </summary>
public static class LifecycleHooks
{
    /// <summary>
    /// Run every hook subscribed to <paramref name="ev"/> against the
    /// supplied <paramref name="payload"/>. Returns the aggregated
    /// decision. Empty hook list returns <c>allow</c> immediately
    /// without spawning anything.
    /// </summary>
    public static async Task<LifecycleHookDecision> RunAsync(
        Profile profile,
        LifecycleHookEvent ev,
        JsonElement payload,
        CancellationToken ct)
    {
        var hooks = SelectHooks(profile, ev);
        if (hooks is null || hooks.Count == 0) return LifecycleHookDecision.Allow();

        var current = payload;
        var modified = false;
        foreach (var hook in hooks)
        {
            if (string.IsNullOrWhiteSpace(hook.Command)) continue;
            var decision = await RunOneAsync(hook, ev, current, ct);
            if (decision.Action == "deny") return decision;
            if (decision.Action == "modify" && decision.Modified.HasValue)
            {
                current = decision.Modified.Value;
                modified = true;
            }
        }

        return modified
            ? LifecycleHookDecision.Modify(current)
            : LifecycleHookDecision.Allow();
    }

    /// <summary>
    /// Run a single hook subprocess. Pure-ish — no global state, just
    /// a spawn + stdio pump. Internal-but-public for testing.
    /// </summary>
    public static async Task<LifecycleHookDecision> RunOneAsync(
        LifecycleHookConfig hook,
        LifecycleHookEvent ev,
        JsonElement payload,
        CancellationToken ct)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        // cmd.exe parses its own command line, so it gets the raw string.
        // bash must get the command as ONE argv entry after -c; a single
        // Arguments string is split on spaces off Windows, and bash would
        // then run only the first word.
        var psi = isWindows
            ? new ProcessStartInfo("cmd.exe", $"/c {hook.Command}")
            : new ProcessStartInfo("/bin/bash") { ArgumentList = { "-c", hook.Command } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        // Surface the event name to the hook script via env so a
        // single script can switch on it (`if [ "$VETT_HOOK_EVENT" = "pre_tool_use" ]; then …`).
        psi.Environment["VETT_HOOK_EVENT"] = EventName(ev);

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            // Spawn failure → fail-open with a deny would be wrong; the
            // user's intent was that the hook ran and approved. We don't
            // know that. Default to allow but log.
            Console.Error.WriteLine($"[hook] {EventName(ev)} \"{hook.Name ?? hook.Command}\" failed to start: {ex.Message}");
            return LifecycleHookDecision.Allow();
        }

        // Wrap the payload in an envelope describing the event, then
        // pipe it onto stdin.
        var envelope = JsonSerializer.SerializeToElement(new
        {
            @event = EventName(ev),
            payload,
        });
        try
        {
            await proc.StandardInput.WriteAsync(envelope.GetRawText());
            proc.StandardInput.Close();
        }
        catch
        {
            // Stdin pipe broke before we could finish writing; the
            // hook may have already exited. Continue to the wait below.
        }

        var timeoutMs = Math.Max(1, hook.TimeoutSeconds) * 1000;
        // Bound the stdout/stderr we'll buffer. A misbehaving hook
        // that spews 100MB shouldn't OOM the vett process; the
        // decision JSON we care about is at most a few hundred bytes.
        // Hard-stop at 1 MiB per stream — anything past that is read
        // and discarded so the pipe doesn't backpressure the child.
        const int OutputCap = 1 * 1024 * 1024;
        var stdoutTask = ReadCappedAsync(proc.StandardOutput, OutputCap, ct);
        var stderrTask = ReadCappedAsync(proc.StandardError, OutputCap, ct);
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            Console.Error.WriteLine($"[hook] {EventName(ev)} \"{hook.Name ?? hook.Command}\" timed out after {hook.TimeoutSeconds}s — fail-open allow");
            return LifecycleHookDecision.Allow();
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stderr))
            Console.Error.WriteLine($"[hook stderr {EventName(ev)} \"{hook.Name ?? hook.Command}\"] {stderr.Trim()}");

        return ParseDecision(stdout);
    }

    /// <summary>Read up to <paramref name="cap"/> bytes from a stream
    /// (UTF-8), then drain and discard the rest. Discarding the tail
    /// keeps the child's pipe from blocking on a full buffer when the
    /// hook is too chatty.</summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader, int cap, CancellationToken ct)
    {
        var buf = new char[8192];
        var sb = new StringBuilder(Math.Min(cap, 8192));
        var captured = 0;
        while (true)
        {
            int n;
            try { n = await reader.ReadAsync(buf, ct); }
            catch (OperationCanceledException) { break; }
            if (n <= 0) break;
            if (captured < cap)
            {
                var take = Math.Min(n, cap - captured);
                sb.Append(buf, 0, take);
                captured += take;
            }
            // Past the cap: keep reading to drain the pipe but throw
            // the bytes away.
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse a hook's stdout into a decision. Empty / blank → allow.
    /// Malformed JSON → allow (with a warning). Unknown <c>action</c>
    /// → allow. <c>deny</c> requires a <c>reason</c>; missing reason
    /// gets a generic stand-in. <c>modify</c> requires a
    /// <c>modified</c> object payload; missing one falls back to
    /// allow. Pure helper, exported for tests.
    /// </summary>
    public static LifecycleHookDecision ParseDecision(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return LifecycleHookDecision.Allow();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            // Clone so the JsonDocument can be safely disposed after this method returns.
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[hook] malformed JSON response, falling back to allow: {ex.Message}");
            return LifecycleHookDecision.Allow();
        }

        if (root.ValueKind != JsonValueKind.Object) return LifecycleHookDecision.Allow();

        var action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() ?? "allow"
            : "allow";

        switch (action.ToLowerInvariant())
        {
            case "deny":
                var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                    ? (r.GetString() ?? "denied by hook")
                    : "denied by hook";
                return LifecycleHookDecision.Deny(reason);

            case "modify":
                if (root.TryGetProperty("modified", out var m) && m.ValueKind == JsonValueKind.Object)
                    return LifecycleHookDecision.Modify(m.Clone());
                return LifecycleHookDecision.Allow();

            case "allow":
            default:
                return LifecycleHookDecision.Allow();
        }
    }

    /// <summary>
    /// Convert the <see cref="LifecycleHookEvent"/> to its
    /// snake_case wire / YAML name. Pure — exported for tests.
    /// </summary>
    public static string EventName(LifecycleHookEvent ev) => ev switch
    {
        LifecycleHookEvent.PreToolUse => "pre_tool_use",
        LifecycleHookEvent.PostToolUse => "post_tool_use",
        LifecycleHookEvent.UserPromptSubmit => "user_prompt_submit",
        LifecycleHookEvent.SessionStart => "session_start",
        LifecycleHookEvent.SessionEnd => "session_end",
        LifecycleHookEvent.Stop => "stop",
        _ => "unknown",
    };

    private static List<LifecycleHookConfig>? SelectHooks(Profile profile, LifecycleHookEvent ev)
    {
        var h = profile.Hooks;
        if (h is null) return null;
        return ev switch
        {
            LifecycleHookEvent.PreToolUse => h.PreToolUse,
            LifecycleHookEvent.PostToolUse => h.PostToolUse,
            LifecycleHookEvent.UserPromptSubmit => h.UserPromptSubmit,
            LifecycleHookEvent.SessionStart => h.SessionStart,
            LifecycleHookEvent.SessionEnd => h.SessionEnd,
            LifecycleHookEvent.Stop => h.Stop,
            _ => null,
        };
    }
}
