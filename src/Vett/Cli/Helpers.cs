using System.Text.Json;
using Vett.Tools;

namespace Vett.Cli;

public static class Helpers
{
    public const string Version = "0.1.0-dev";

    /// <summary>
    /// Warning text for running a TEAM profile through a SOLO-only command,
    /// or null when the profile has no team block and nothing is dropped.
    ///
    /// ⛔ THE DEFECT THIS NAMES (found 2026-08-26). `vett run` and `vett bench`
    /// go through Runner.cs, which never reads <c>profile.Team</c> — it builds
    /// one LlmSettings from the top-level <c>llm:</c> and calls AgentLoop
    /// directly. Only <c>vett chat</c> and <c>vett team-bench</c> reach
    /// TeamCoordinator. So a team profile handed to `run`/`bench` parses
    /// completely — leader, members, every per-member <c>llm:</c> — and then
    /// every bit of it is discarded, and the run proceeds SOLO.
    ///
    /// It failed SILENTLY-WELL, which is the dangerous kind: the run works,
    /// exits 0, and produces a result recorded against a team profile's name.
    /// Nothing downstream can tell that result apart from a real team run.
    ///
    /// ⚠ ProfileKeyAudit cannot catch this. That audit reports keys NO
    /// PROPERTY WILL RECEIVE; these keys bind perfectly and are then never
    /// consumed. "Parsed" and "used" are different claims, and only the first
    /// one has an instrument.
    ///
    /// A WARNING, NOT AN ERROR, deliberately: someone may be running a team
    /// profile's solo half on purpose, and turning that into a hard failure
    /// would break a working invocation to fix a labelling problem.
    /// </summary>
    public static string? TeamBlockIgnoredWarning(Vett.Config.Profile profile, string command)
    {
        var team = profile.Team;
        if (team is null) return null;

        var members = team.Members?.Count ?? 0;
        return $"Profile '{profile.Name}' declares a team ({members} member(s)), but `vett {command}` "
             + $"has NO team support — the entire `team:` block is IGNORED and this run is SOLO on the "
             + $"top-level `llm:` ({profile.Llm?.Model ?? "?"}). Its result must NOT be recorded or "
             + $"compared as a team result. Use `vett team-bench` (or `vett chat`) to actually run the team.";
    }

    /// <summary>
    /// Apply PLAN MODE restrictions to a tool dispatch dictionary using
    /// the Claude-Code-style "approve & switch" gate. Each write-capable
    /// tool gets wrapped: on first invocation the gate emits a
    /// `plan_mode_action_request` event and awaits user approval. On
    /// approve the gate unlocks for the rest of the session AND emits
    /// `chat_mode_changed:execute` so the UI flips Plan → Exec; on
    /// reject the wrap returns an error to the agent.
    ///
    /// Read-only ops (file_editor view, monitor, bash_jobs) pass through
    /// without prompting. The gate is a per-session unlock — once
    /// approved, all subsequent write attempts dispatch normally so the
    /// agent keeps its full context (no respawn, no re-exploration).
    ///
    /// Called from both the single-agent chat path (ChatCommand.RunStdio)
    /// and the team-leader chat path (TeamCoordinator) so plan mode
    /// applies consistently regardless of which path the chat takes.
    /// Only the chat layer ever passes `--mode plan`; benchmarks
    /// (`vett run`) never invoke this.
    /// </summary>
    public static void ApplyPlanModeToolRestrictions(
        IDictionary<string, ToolFn> tools,
        Vett.Tools.PlanModeUnlockGate gate)
    {
        // Belt + braces: the schema filter (profile.Tools) hides these
        // tools from the LLM, but Builtins.All() populates the dispatch
        // dict regardless. Without removing here, a model that
        // Wrap each write-capable tool: on first call, the gate
        // surfaces an "approve & switch" prompt; on approve, the gate
        // unlocks for the session AND emits chat_mode_changed:execute,
        // and the wrapped call dispatches normally. On reject, the
        // wrap returns an error to the agent.
        WrapWithGate(tools, "terminal", gate);
        WrapWithGate(tools, "bash_background", gate);
        WrapWithGate(tools, "bash_kill", gate);
        WrapWithGate(tools, "update_memory", gate);

        // file_editor mixes read (view) and write ops on a single schema.
        // View passes through unconditionally; non-view ops go through
        // the gate. Once unlocked, all ops dispatch directly.
        if (tools.TryGetValue("file_editor", out var fileEditor))
        {
            tools["file_editor"] = async (args, sandbox, sessionId, ct) =>
            {
                var op = args.TryGetValue("command", out var v) ? v?.ToString() ?? "" : "";
                if (op == "view") return await fileEditor(args, sandbox, sessionId, ct);
                if (gate.IsUnlocked) return await fileEditor(args, sandbox, sessionId, ct);
                var approved = await gate.CheckAsync("file_editor", args, ct);
                if (!approved)
                {
                    return $"Error: file_editor `{op}` was rejected by the user. " +
                        "Stay in plan mode — propose an alternative or ask the user what they'd like instead.";
                }
                return await fileEditor(args, sandbox, sessionId, ct);
            };
        }
    }

    /// <summary>
    /// Wrap a single write-capable tool with the plan-mode unlock gate.
    /// Pass-through once unlocked; "approve & switch" prompt on first
    /// call; error on reject. Tools not in the dispatch dict are
    /// silently skipped (e.g. update_memory may not be registered when
    /// the profile didn't list it).
    /// </summary>
    private static void WrapWithGate(
        IDictionary<string, ToolFn> tools,
        string name,
        Vett.Tools.PlanModeUnlockGate gate)
    {
        if (!tools.TryGetValue(name, out var inner)) return;
        tools[name] = async (args, sandbox, sessionId, ct) =>
        {
            if (gate.IsUnlocked) return await inner(args, sandbox, sessionId, ct);
            var approved = await gate.CheckAsync(name, args, ct);
            if (!approved)
            {
                return $"Error: `{name}` was rejected by the user. Stay in plan mode — " +
                    "propose an alternative or ask the user what they'd like instead.";
            }
            return await inner(args, sandbox, sessionId, ct);
        };
    }

    [Obsolete("exit_plan_mode replaced by per-call PlanModeUnlockGate; left in place only " +
              "until the schemas/exit_plan_mode.json file is removed in a follow-up cleanup.")]
    public static void RegisterExitPlanModeTool(
        IDictionary<string, ToolFn> tools,
        IList<JsonElement> schemas,
        Action<object> emit)
    {
        tools["exit_plan_mode"] = (args, _, _, _) =>
        {
            var plan = args.TryGetValue("plan", out var p) ? p?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(plan))
            {
                return Task.FromResult(
                    "Error: exit_plan_mode requires a non-empty `plan` argument with the markdown plan.");
            }
            emit(new
            {
                type = "exit_plan_mode_request",
                data = new { plan },
            });
            return Task.FromResult(
                "Plan submitted to user for approval. Your turn ends here — do not call any " +
                "more tools and do not emit any more text. Wait for the user's decision.");
        };

        try
        {
            var schemaPath = Path.Combine(
                AppContext.BaseDirectory, "schemas", "exit_plan_mode.json");
            if (File.Exists(schemaPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
                schemas.Add(doc.RootElement.Clone());
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load exit_plan_mode schema: {ex.Message}");
        }
    }

    public static string Env(string? flag, string key)
        => !string.IsNullOrEmpty(flag) ? flag : Environment.GetEnvironmentVariable(key) ?? "";

    /// <summary>
    /// Expand a leading ~ to the user's home directory. .NET's Path.Combine doesn't
    /// do this (it's a shell convention), so suite YAMLs like "~/.cache/..." would
    /// otherwise fail on every platform.
    /// </summary>
    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~')
            return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 1) return home;
        if (path[1] == '/' || path[1] == '\\') return Path.Combine(home, path[2..]);
        return path;
    }

    public static string? FindSidecar() => FindSidecarWithPaths(out _);

    /// <summary>
    /// Locate the sidecar binary, also returning every path that was
    /// checked. Callers print the searched paths in the error message
    /// when nothing matches — silent failures here cost the user 30
    /// minutes of "why doesn't it work."
    /// </summary>
    public static string? FindSidecarWithPaths(out string[] searched)
    {
        // Pick the right binary for the host. macOS comes in two flavors:
        // Apple Silicon (arm64, every Mac since late 2020) and Intel
        // (amd64, older). Linux + Windows are amd64-only for now.
        string name;
        if (OperatingSystem.IsWindows())
            name = "vett-sidecar-windows-amd64.exe";
        else if (OperatingSystem.IsMacOS())
            name = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? "vett-sidecar-darwin-arm64"
                : "vett-sidecar-darwin-amd64";
        else
            name = "vett-sidecar-linux-amd64";

        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("VETT_SIDECAR_PATH") ?? "",
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bin", name), // dev: src/Vett/bin/Debug/net10.0/.. → bin/
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vett", "bin", name),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "VETT", "bin", name),
            Path.Combine(Directory.GetCurrentDirectory(), "bin", name),
        };
        searched = candidates.Where(p => !string.IsNullOrEmpty(p)).Select(Path.GetFullPath).ToArray();

        foreach (var p in candidates)
        {
            if (!string.IsNullOrEmpty(p) && File.Exists(p))
                return Path.GetFullPath(p);
        }

        return null;
    }

    public static List<JsonElement> LoadSchemas(List<string> toolNames)
    {
        var schemas = new List<JsonElement>();
        foreach (var name in toolNames)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "schemas", $"{name}.json");
            if (!File.Exists(path))
                path = Path.Combine(Directory.GetCurrentDirectory(), "schemas", $"{name}.json");

            if (File.Exists(path))
                schemas.Add(JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone());
        }
        return schemas;
    }
}
