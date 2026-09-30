using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Text.Json;
using System.Threading.Channels;
using Vett.Agent;
using Vett.Config;
using Microsoft.Extensions.AI;
using Vett.Llm;
using Vett.Plugin;
using Vett.Sandbox;
using Vett.Tools;

namespace Vett.Cli;

public static class ChatCommand
{
    public static Command Create(ILogger logger)
    {
        var cmd = new Command("chat", "Interactive coding session");

        var profileOpt = new Option<string>("--profile") { DefaultValueFactory = _ => "coding" };
        var endpointOpt = new Option<string>("--endpoint");
        var modelOpt = new Option<string>("--model");
        var apiKeyOpt = new Option<string>("--api-key");
        var stdioOpt = new Option<bool>("--stdio");
        var cwdOpt = new Option<string>("--cwd");
        var resumeOpt = new Option<string>("--resume") { Description = "Path to a past chat session JSONL (e.g. ~/.vett/chat-sessions/chat-...jsonl). Resumes the conversation by seeding the agent's message history from the log." };

        // Per-session profile overrides. Built so vett-chat's Settings UI
        // can pass user tweaks as flags without modifying the profile
        // YAML on disk. Every flag is optional; unset → use the
        // profile's value. Nullable doubles use sentinel detection (NaN)
        // because System.CommandLine's Option<double?> binding is awkward.
        var temperatureOpt = new Option<double>("--temperature") {
            DefaultValueFactory = _ => double.NaN,
            Description = "Override the profile's sampling temperature for this session only. Profile YAML is not modified."
        };
        var topPOpt = new Option<double>("--top-p") {
            DefaultValueFactory = _ => double.NaN,
            Description = "Override the profile's nucleus-sampling top_p for this session only."
        };
        var maxIterOpt = new Option<int>("--max-iterations") {
            DefaultValueFactory = _ => -1,
            Description = "Override the profile's max_iterations cap for this session only. -1 = use profile default."
        };
        var timeoutOpt = new Option<int>("--timeout-minutes") {
            DefaultValueFactory = _ => -1,
            Description = "Override the profile's timeout_minutes for this session only. -1 = use profile default."
        };
        var systemAppendOpt = new Option<string>("--system-prompt-append") {
            Description = "Append this text to the profile's system prompt for this session. Useful for one-off instructions ('respond in Spanish', 'avoid bash, use Python only')."
        };
        var modeOpt = new Option<string>("--mode") {
            DefaultValueFactory = _ => "execute",
            Description = "Agent mode for this session: 'execute' (default — full tool access) or 'plan' (read-only: file_editor restricted to view, terminal/finish removed; system prompt instructs the agent to propose a plan in markdown without modifying state)."
        };
        // Per-kind permission overrides — layered on top of the
        // profile's `permissions:` block at session start. Each flag is
        // optional; empty string = use the profile's value. These mirror
        // the SessionOverrides fields exposed by the chat Settings UI.
        // WHAT AN AGENT-SIDE STOP MEANS IN CHAT (2026-08-29).
        //
        // Default (flag absent): the agent calling `finish`, or tripping a
        // stuck detector, ends the TURN. Control comes back to the person, the
        // conversation and its context stay live, and they can review what was
        // done and reply. Before this, `finish` called Finalize() and took the
        // whole session with it: Mark watched a chat close itself at iteration
        // 130 before he had read a word of the result.
        //
        // Flag present: the pre-2026-08-29 behaviour, for a `--stdio` chat
        // being driven by another program rather than a person, where "the
        // agent is done" should end the process.
        //
        // Autonomous `vett run` is not affected either way; it has no human to
        // hand control back to and terminates on these reasons as it always has.
        var finishEndsSessionOpt = new Option<bool>("--finish-ends-session") {
            Description = "End the whole chat session when the agent calls finish (or gets stuck), "
                        + "instead of handing control back to you. Off by default; useful when a "
                        + "script, not a person, is driving --stdio.",
        };

        var permReadOpt = new Option<string>("--permission-read") { DefaultValueFactory = _ => "" };
        var permEditOpt = new Option<string>("--permission-edit") { DefaultValueFactory = _ => "" };
        var permTermSafeOpt = new Option<string>("--permission-terminal-safe") { DefaultValueFactory = _ => "" };
        var permTermUnsafeOpt = new Option<string>("--permission-terminal-unsafe") { DefaultValueFactory = _ => "" };
        var permMcpOpt = new Option<string>("--permission-mcp") { DefaultValueFactory = _ => "" };
        var permOtherOpt = new Option<string>("--permission-other") { DefaultValueFactory = _ => "" };

        // TEAM SHAPE OVERRIDES (2026-08-28). A profile owns the PROMPTS and the
        // ROLES; these own the SHAPE. Without them, every new team size needed
        // its own generated YAML — which is literally what happened
        // (ds-manager-flash-w10.yaml and -w20.yaml are 2,912 and 5,602 lines of
        // byte-identical prompts differing only in member count).
        // All default to unset, so a command that passes none of them resolves
        // exactly the profile it always did.
        var teamOpt = new Option<string>("--team") {
            Description = "Resize the team for this run, reusing the profile's own role prompts. "
                        + "e.g. --team \"implementer x5, reviewer x2, researcher\". "
                        + "Names must already exist in the profile; a typo is an error, not a smaller team.",
        };
        var teamWidthOpt = new Option<int>("--team-width") {
            Description = "max_concurrent_dispatches for this run: how many members may work at once. 0 = unlimited.",
            DefaultValueFactory = _ => -1,
        };
        var leaderCtxOpt = new Option<int>("--leader-context") {
            Description = "Leader's compaction trigger in tokens. Give the leader a bigger window than the workers "
                        + "when it has to hold the whole plan plus every report.",
            DefaultValueFactory = _ => -1,
        };
        var workerCtxOpt = new Option<int>("--worker-context") {
            Description = "Compaction trigger in tokens applied to every member (per-role values from --context win).",
            DefaultValueFactory = _ => -1,
        };
        var perRoleCtxOpt = new Option<string>("--context") {
            Description = "Per-role compaction triggers, e.g. --context \"implementer=60000, reviewer=30000\". "
                        + "A rule named for a role applies to every seat cloned from it.",
        };

        cmd.Add(profileOpt);
        cmd.Add(teamOpt);
        cmd.Add(teamWidthOpt);
        cmd.Add(leaderCtxOpt);
        cmd.Add(workerCtxOpt);
        cmd.Add(perRoleCtxOpt);
        cmd.Add(endpointOpt);
        cmd.Add(modelOpt);
        cmd.Add(apiKeyOpt);
        cmd.Add(stdioOpt);
        cmd.Add(cwdOpt);
        cmd.Add(resumeOpt);
        cmd.Add(temperatureOpt);
        cmd.Add(topPOpt);
        cmd.Add(maxIterOpt);
        cmd.Add(timeoutOpt);
        cmd.Add(systemAppendOpt);
        cmd.Add(modeOpt);
        cmd.Add(finishEndsSessionOpt);
        cmd.Add(permReadOpt);
        cmd.Add(permEditOpt);
        cmd.Add(permTermSafeOpt);
        cmd.Add(permTermUnsafeOpt);
        cmd.Add(permMcpOpt);
        cmd.Add(permOtherOpt);

        cmd.SetAction(async (pr, ct) =>
        {
            var profileName = pr.GetValue(profileOpt) ?? "coding";
            var stdio = pr.GetValue(stdioOpt);
            var resumePath = pr.GetValue(resumeOpt);
            var cwd = string.IsNullOrEmpty(pr.GetValue(cwdOpt))
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(pr.GetValue(cwdOpt)!);

            // Load profile FIRST so it can supply endpoint/model/api-key
            // when the user hasn't passed flags / env vars. The profile
            // YAML is the source of truth for chat sessions; flags are
            // overrides, not requirements.
            var profile = Yaml.Resolve(profileName, "profiles", Yaml.LoadProfile);
            if (profile is null)
            {
                logger.LogError("Error: profile '{Profile}' not found in any of: {Dirs}",
                    profileName, string.Join(", ", Yaml.ResolveSearchDirs("profiles")));
                // 2 = config error, nothing ran. This was a bare `return;`,
                // so `vett chat` printed a fatal error and exited 0. That
                // matters most in --stdio mode: VETT Chat spawns this process
                // and a zero exit reads as a healthy session that simply
                // produced no events, rather than a failed launch.
                return 2;
            }

            // Apply the team-shape overrides IMMEDIATELY after load, before any
            // consumer reads profile.Team — so every downstream site (coordinator,
            // validation, the session log) sees one consistent shape and there is
            // no window in which half the process is looking at the base profile.
            {
                if (!Config.TeamOverride.TryParseRoster(pr.GetValue(teamOpt), out var roster, out var rosterErr))
                {
                    logger.LogError("{Error}", rosterErr);
                    return 2;
                }
                if (!Config.TeamOverride.TryParsePerRoleContext(pr.GetValue(perRoleCtxOpt), out var perRole, out var ctxErr))
                {
                    logger.LogError("{Error}", ctxErr);
                    return 2;
                }
                var width = pr.GetValue(teamWidthOpt);
                var leaderCtx = pr.GetValue(leaderCtxOpt);
                var workerCtx = pr.GetValue(workerCtxOpt);
                var spec = new Config.TeamOverride.Spec(
                    Roster: roster.Count > 0 ? roster : null,
                    // -1 is the "not supplied" sentinel; 0 is a REAL value for width
                    // (unlimited), so the two cannot share a representation.
                    Width: width >= 0 ? width : null,
                    LeaderContext: leaderCtx > 0 ? leaderCtx : null,
                    WorkerContext: workerCtx > 0 ? workerCtx : null,
                    PerRoleContext: perRole.Count > 0 ? perRole : null);

                if (!Config.TeamOverride.TryApply(profile, spec, out var applyErr, out var applySummary))
                {
                    logger.LogError("{Error}", applyErr);
                    return 2;
                }
                if (!string.IsNullOrEmpty(applySummary))
                    logger.LogInformation("Team override applied — {Summary}", applySummary);
            }

            // Resolution order, highest to lowest:
            //   1. --flag
            //   2. VETT_LLM_* env var
            //   3. profile.Llm.{Endpoint,Model}
            //   4. (api-key only) env var named by profile.Llm.ApiKeyEnv
            var endpoint = Helpers.Env(pr.GetValue(endpointOpt), "VETT_LLM_ENDPOINT");
            endpoint = ChatClientFactory.FoldProfileValue(profile.Llm, endpoint, profile.Llm.Endpoint);
            var model = Helpers.Env(pr.GetValue(modelOpt), "VETT_LLM_MODEL");
            model = ChatClientFactory.FoldProfileValue(profile.Llm, model, profile.Llm.Model);
            var apiKey = Helpers.Env(pr.GetValue(apiKeyOpt), "VETT_LLM_API_KEY");
            if (string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(profile.Llm.ApiKeyEnv))
                apiKey = Environment.GetEnvironmentVariable(profile.Llm.ApiKeyEnv) ?? "";

            // A fifth source sits below the profile: the PROVIDER's own base
            // URL. openai / anthropic / google each know where they live, so
            // demanding an explicit endpoint from them rejected input the
            // client layer could serve — `openai-example`, `anthropic-example`
            // and every onboarding-written cloud profile exited 2 here
            // (measured 2026-08-26). local / azure still have no default and
            // still fail below.
            if (string.IsNullOrEmpty(endpoint))
                endpoint = ChatClientFactory.DefaultEndpointFor(profile.Llm.Provider) ?? "";

            // A capability profile has no endpoint/model of its own by
            // design -- the catalogue owns them. Guarding on their absence
            // would reject exactly the configs the factory is able to serve.
            if (!ChatClientFactory.BindsThroughCapacity(profile.Llm)
                && (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(model)))
            {
                logger.LogError(
                    "Error: endpoint and model are required. Set them in the profile YAML " +
                    "(`llm.endpoint` / `llm.model`), via VETT_LLM_ENDPOINT / VETT_LLM_MODEL " +
                    "env vars, or pass --endpoint / --model on the command line. Profile " +
                    "loaded: {Profile} (provider={Provider}, endpoint={Endpoint}, model={Model})",
                    profileName, profile.Llm.Provider, profile.Llm.Endpoint, profile.Llm.Model);
                return 2;
            }

            // For local mode, use direct bash — no sidecar needed.
            using var localBash = new DirectBash(cwd);
            // A human is waiting at this prompt. When chat and a batch run
            // contend for the same capability, chat wins -- that is the whole
            // reason the broker carries a priority at all. Only consulted when
            // the profile names a capability AND leaves llm.priority unset;
            // an explicit priority in the profile still wins.
            Vett.Llm.CapacityBinding.DefaultPriority = Vett.Capacity.Priority.Interactive;
            var client = ChatClientFactory.Create(profile.Llm, endpoint, model, apiKey);
            // Join the model name back up with the client. Empty here means a
            // capability profile named none; the client knows what the
            // catalogue chose. See ChatClientFactory.EffectiveModel.
            model = ChatClientFactory.EffectiveModel(client, model);
            using var plugins = new PluginPool(cwd);
            await plugins.WarmUpAsync(ct);

            // Build tool map: builtins + plugins.
            var tools = Builtins.All();
            var schemas = Helpers.LoadSchemas(profile.Tools);
            var pluginToolMap = new Dictionary<string, ToolFn>();
            var pluginSchemaMap = new Dictionary<string, JsonElement>();
            foreach (var name in plugins.ToolNames())
            {
                var fn = plugins.GetTool(name);
                if (fn is not null)
                {
                    tools[name] = fn;
                    pluginToolMap[name] = fn;
                    var s = plugins.GenerateSchema(name);
                    if (s.ValueKind != JsonValueKind.Undefined)
                    {
                        schemas.Add(s);
                        pluginSchemaMap[name] = s;
                    }
                }
            }

            // Auto-memory tool (chat-only): captures workspace cwd in
            // the closure so memory always lands at <cwd>/.vett/memory/
            // regardless of where the agent's terminal navigates. Only
            // registered when the profile lists `update_memory` in its
            // tools — opt-in, doesn't enable itself silently.
            RegisterMemoryTool(tools, schemas, profile, cwd);

            var pluginMiddlewares = plugins.AllMiddlewares();

            // Team profile in chat: route to TeamCoordinator's interactive
            // path. The leader runs interactively (chats with the user);
            // assign_task tool calls dispatch members non-interactively
            // and tag their events with thread_id so the extension can
            // group them into per-thread tabs in the chat panel.
            // Note: previous one-shot team behavior moved into the run
            // command (`vett run`); chat is now consistently interactive.

            void OnEvent(Event e)
            {
                if (e.Type == "tool_call_start")
                    Console.Write($"  \u25B6 {e.Data.GetValueOrDefault("tool_name")}");
                if (e.Type == "tool_call_end")
                    Console.WriteLine();
                // Surface LLM errors so the user can see what went wrong
                // instead of just "stopped: llm_error" at the end.
                if (e.Type == "llm_error")
                    logger.LogError("LLM error: {Message}", e.Data.GetValueOrDefault("message"));
            }

            // Project instructions: prepend any AGENTS.md / VETT.md
            // content (workspace ancestors, .vett/, or ~/.vett/) to the
            // profile's system prompt. Same model as Claude Code's
            // CLAUDE.md. Mutating profile.SystemPrompt directly so
            // RunStdio/RunTerminal pick it up without further plumbing.
            var loadedInstructions = ProjectInstructions.Load(cwd);
            if (!string.IsNullOrEmpty(loadedInstructions))
            {
                profile.SystemPrompt = ProjectInstructions.Apply(profile.SystemPrompt, cwd);
                logger.LogInformation(
                    "Loaded project instructions ({Length} chars) from AGENTS.md/VETT.md",
                    loadedInstructions.Length);
            }

            // Persistent memory: prepend the .vett/memory/MEMORY.md
            // index (if any). Loaded AFTER project instructions so
            // memory sits closer to the base prompt — rules are
            // structural, memory is learned context. Order in the
            // resulting prompt:
            //   <project_instructions>...</project_instructions>
            //   <persistent_memory>...</persistent_memory>
            //   <profile system prompt>
            var loadedMemory = MemoryStore.LoadIndex(cwd);
            if (!string.IsNullOrEmpty(loadedMemory))
            {
                profile.SystemPrompt = MemoryStore.Apply(profile.SystemPrompt, cwd);
                logger.LogInformation(
                    "Loaded persistent memory ({Length} chars) from .vett/memory/MEMORY.md",
                    loadedMemory.Length);
            }

            // Repo-map injection (#14). Loaded LAST in the prefix
            // chain so the symbol-density block sits closest to the
            // base prompt — rules are structural, memory is learned,
            // repo map is "what's in this codebase right now." Default
            // budget = 4000 chars (~1k tokens). Disable per-profile by
            // omitting the block or setting `enabled: false`.
            if (profile.RepoMap?.Enabled == true)
            {
                var rankingStr = (profile.RepoMap.Ranking ?? "references").Trim().ToLowerInvariant();
                var ranking = rankingStr == "density"
                    ? Vett.Config.RepoMap.RepoMapRanking.Density
                    : Vett.Config.RepoMap.RepoMapRanking.References;
                var rmBefore = profile.SystemPrompt?.Length ?? 0;
                profile.SystemPrompt = Vett.Config.RepoMap.Apply(
                    profile.SystemPrompt ?? string.Empty,
                    cwd,
                    profile.RepoMap.MaxChars,
                    profile.RepoMap.ExtraExcludes,
                    ranking);
                var rmDelta = (profile.SystemPrompt?.Length ?? 0) - rmBefore;
                if (rmDelta > 0)
                {
                    logger.LogInformation(
                        "Loaded repo map ({Length} chars, ranking={Ranking}) from {Cwd}",
                        rmDelta, ranking, cwd);
                }
            }

            // Per-session overrides — applied AFTER profile load + AFTER
            // project instructions so the chat UI's Settings panel can
            // tweak any of these without editing YAML or restarting the
            // host. Each flag is independent; unspecified → keep
            // profile default. Mutating the in-memory profile is safe;
            // we never persist it back to disk.
            var tempArg = pr.GetValue(temperatureOpt);
            if (!double.IsNaN(tempArg))
                profile.Llm.Temperature = tempArg;
            var topPArg = pr.GetValue(topPOpt);
            if (!double.IsNaN(topPArg))
                profile.Llm.TopP = topPArg;
            var maxIterArg = pr.GetValue(maxIterOpt);
            if (maxIterArg > 0)
                profile.MaxIterations = maxIterArg;
            var timeoutArg = pr.GetValue(timeoutOpt);
            if (timeoutArg > 0)
                profile.TimeoutMinutes = timeoutArg;
            var systemAppendArg = pr.GetValue(systemAppendOpt);
            if (!string.IsNullOrWhiteSpace(systemAppendArg))
            {
                // Append rather than replace so VETT.md + profile prompt
                // stay intact; user instruction lands at the end where
                // the LLM weighs it most.
                profile.SystemPrompt = (profile.SystemPrompt ?? string.Empty).TrimEnd()
                    + "\n\n--- additional instructions ---\n"
                    + systemAppendArg.Trim();
            }

            // Plan mode — read-only design phase. Implementation is two
            // levers: (a) drop write-capable tools from the profile's
            // tool list so the agent CAN'T modify state even if asked,
            // and (b) prepend a strong system-prompt addendum so the
            // model knows to produce a plan rather than try to act.
            //
            // What gets dropped: `terminal` (arbitrary commands → write),
            // `finish` (declares the task done — wrong shape for plan
            // mode), and any `file_editor` non-view ops are constrained
            // by the system prompt (tool schema isn't easily filtered
            // op-by-op without regenerating it; the prompt does the
            // heavy lifting and works in practice).
            //
            // Soft enforcement (system prompt) + hard removal of the
            // most dangerous tool (terminal) is the v1 trade-off:
            // simpler than per-op schema rewriting, safe enough to
            // share with a team. Hardening (custom file_view_only tool)
            // can come later without breaking this design.
            var modeArg = (pr.GetValue(modeOpt) ?? "execute").ToLowerInvariant();
            var finishEndsSession = pr.GetValue(finishEndsSessionOpt);
            if (modeArg == "plan")
            {
                // System prompt only — the actual gate that intercepts
                // tool calls is constructed in RunStdio (it needs the
                // Emit closure that's only in scope there). All
                // schemas stay registered: the model can see the full
                // toolset; the gate handles approval per call.
                profile.SystemPrompt = (profile.SystemPrompt ?? string.Empty).TrimEnd()
                    + "\n\n--- PLAN MODE ---\n"
                    + "You are operating in PLAN MODE. The user wants to be in the loop before any "
                    + "state-changing action. You can read code, search, and run read-only commands "
                    + "freely. The first time you attempt a write (file edit, shell command, memory "
                    + "update), the user will see an Approve / Reject prompt. On Approve, the chat "
                    + "permanently switches to execute mode for the rest of this session and you "
                    + "won't be prompted again. On Reject, the call returns an error — propose an "
                    + "alternative or ask the user what they'd like instead.\n\n"
                    + "Do your normal work. Don't pre-emptively narrate \"I'll now ask permission\" "
                    + "— the prompt happens automatically when you attempt a write.\n\n"
                    + "CRITICAL: invoke tools through the structured function-call API only. Do NOT "
                    + "emit `<tool_call>...</tool_call>` XML, OpenHands-style markup, or any other "
                    + "tool-call-as-text. Those are NOT executed — vett only dispatches calls made "
                    + "through the proper API. If you find yourself about to write `<function=...` "
                    + "or `<parameter=...` in your message, stop and use the tool-call API instead.";
                logger.LogInformation("Plan mode active — gate-based per-call approval; full toolset registered.");
            }
            else if (modeArg != "execute")
            {
                logger.LogWarning("Unknown --mode value '{Mode}'. Falling back to 'execute'.", modeArg);
            }

            // Per-kind permission overrides. Apply on top of the
            // profile YAML so the chat Settings drawer can flip kinds
            // per-session without rewriting `~/.vett/profiles/*.yaml`.
            // Each flag is independent; empty means "leave profile
            // value alone." If the profile has no `permissions:` block
            // and the user passes any override, we synthesize one with
            // the per-kind defaults so the gate ends up wired up.
            var permRead = pr.GetValue(permReadOpt) ?? "";
            var permEdit = pr.GetValue(permEditOpt) ?? "";
            var permTermSafe = pr.GetValue(permTermSafeOpt) ?? "";
            var permTermUnsafe = pr.GetValue(permTermUnsafeOpt) ?? "";
            var permMcp = pr.GetValue(permMcpOpt) ?? "";
            var permOther = pr.GetValue(permOtherOpt) ?? "";
            var anyPermOverride =
                !string.IsNullOrEmpty(permRead) || !string.IsNullOrEmpty(permEdit) ||
                !string.IsNullOrEmpty(permTermSafe) || !string.IsNullOrEmpty(permTermUnsafe) ||
                !string.IsNullOrEmpty(permMcp) || !string.IsNullOrEmpty(permOther);
            if (anyPermOverride)
            {
                profile.Permissions ??= new PermissionsConfig();
                if (!string.IsNullOrEmpty(permRead)) profile.Permissions.Read = permRead;
                if (!string.IsNullOrEmpty(permEdit)) profile.Permissions.Edit = permEdit;
                if (!string.IsNullOrEmpty(permTermSafe)) profile.Permissions.TerminalSafe = permTermSafe;
                if (!string.IsNullOrEmpty(permTermUnsafe)) profile.Permissions.TerminalUnsafe = permTermUnsafe;
                if (!string.IsNullOrEmpty(permMcp)) profile.Permissions.Mcp = permMcp;
                if (!string.IsNullOrEmpty(permOther)) profile.Permissions.Other = permOther;
            }

            // Load resume seed before starting the loop so we fail loudly
            // on a bad path rather than silently starting a fresh session.
            HistorySeeder.SeedResult? seed = null;
            if (!string.IsNullOrEmpty(resumePath))
            {
                seed = HistorySeeder.LoadFromJsonl(Path.GetFullPath(resumePath));
                if (seed.Messages.Count == 0)
                {
                    logger.LogWarning(
                        "--resume {Path}: no usable user/assistant messages found. Starting a fresh session.",
                        resumePath);
                    seed = null;
                }
                else
                {
                    logger.LogInformation(
                        "Resuming from {Path} ({UserTurns} user / {AssistantTurns} assistant turns)",
                        resumePath, seed.UserTurns, seed.AssistantTurns);
                }
            }

            if (stdio)
                // RunStdio sets up its own JSON-emitting OnEvent so the
                // wire stream contains EVERY event (iteration_start,
                // llm_response, tool_call_*, dispatch_*, cancelled,
                // compacted). The OnEvent defined here is for the
                // human-readable terminal mode only.
                await RunStdio(profile, client, model, localBash, cwd, tools, schemas, pluginMiddlewares, seed?.Messages, modeArg == "plan", finishEndsSession, ct);
            else
                await RunTerminal(profile, client, model, localBash, cwd, tools, schemas, pluginMiddlewares, seed?.Messages, finishEndsSession, OnEvent, ct);

            return 0;
        });

        return cmd;
    }

    /// <summary>
    /// Register the chat-only `bash_background` / `monitor` /
    /// `bash_kill` / `bash_jobs` tools. All four are closures over the
    /// <see cref="Vett.Tools.BackgroundBashService"/> so they share
    /// the per-session job table. Tools register only when listed in
    /// the profile's <c>tools:</c> array — opt-in, mirrors the pattern
    /// for `ask_user_question` / `update_memory`.
    /// </summary>
    private static void RegisterBackgroundBashTools(
        Dictionary<string, ToolFn> tools,
        List<JsonElement> schemas,
        Vett.Config.Profile profile,
        Vett.Tools.BackgroundBashService bgBash)
    {
        bool any = false;
        if (profile.Tools.Contains("bash_background"))
        {
            tools["bash_background"] = (args, _, _, _) =>
            {
                try
                {
                    var command = Builtins.Str(args, "command");
                    if (string.IsNullOrEmpty(command))
                        return Task.FromResult("Error: 'command' is required");
                    var name = Builtins.Str(args, "name");
                    var (id, displayName) = bgBash.Start(command, string.IsNullOrWhiteSpace(name) ? null : name);
                    return Task.FromResult($"Started background job\n  job_id: {id}\n  name:   {displayName}\n\nRead its output with `monitor job_id={id}`. Stop it with `bash_kill job_id={id}`.");
                }
                catch (Exception ex)
                {
                    return Task.FromResult($"Error: {ex.Message}");
                }
            };
            any = true;
        }
        if (profile.Tools.Contains("monitor"))
        {
            tools["monitor"] = (args, _, _, _) =>
            {
                try
                {
                    var jobId = Builtins.Str(args, "job_id");
                    if (string.IsNullOrEmpty(jobId))
                        return Task.FromResult("Error: 'job_id' is required");
                    var sinceLine = Builtins.Int(args, "since_line", 0);
                    var result = bgBash.GetOutput(jobId, sinceLine);
                    if (result is null)
                        return Task.FromResult($"Error: no background job with id '{jobId}'");
                    var sb = new System.Text.StringBuilder();
                    sb.Append("job: ").Append(result.JobId)
                      .Append(" (").Append(result.Name).Append(") · ")
                      .Append(result.Complete
                          ? $"complete (exit_code={result.ExitCode?.ToString() ?? "n/a"})"
                          : "running")
                      .Append(" · lines ").Append(result.FromLine).Append('-')
                      .Append(result.FromLine + result.Lines.Count - 1)
                      .Append(" of ").Append(result.TotalLines)
                      .Append('\n');
                    sb.Append("started_at: ").Append(result.StartedAt.ToString("u")).Append('\n');
                    sb.Append("command: ").Append(result.Command).Append("\n\n");
                    if (result.Lines.Count == 0)
                    {
                        sb.Append(result.Complete
                            ? "(no new output; process is complete)"
                            : "(no new output yet; process still running — try again shortly)");
                    }
                    else
                    {
                        foreach (var line in result.Lines)
                            sb.Append(line).Append('\n');
                    }
                    sb.Append('\n').Append("total_lines: ").Append(result.TotalLines)
                      .Append(" — pass `since_line=").Append(result.TotalLines)
                      .Append("` next call to read only new output.");
                    return Task.FromResult(sb.ToString());
                }
                catch (Exception ex)
                {
                    return Task.FromResult($"Error: {ex.Message}");
                }
            };
            any = true;
        }
        if (profile.Tools.Contains("bash_kill"))
        {
            tools["bash_kill"] = (args, _, _, _) =>
            {
                try
                {
                    var jobId = Builtins.Str(args, "job_id");
                    if (string.IsNullOrEmpty(jobId))
                        return Task.FromResult("Error: 'job_id' is required");
                    var killed = bgBash.Kill(jobId);
                    return Task.FromResult(killed
                        ? $"Sent kill signal to {jobId}. The job's output buffer is preserved — a final `monitor job_id={jobId}` call will show what was printed before the kill."
                        : $"Error: no background job with id '{jobId}'");
                }
                catch (Exception ex)
                {
                    return Task.FromResult($"Error: {ex.Message}");
                }
            };
            any = true;
        }
        if (profile.Tools.Contains("bash_jobs"))
        {
            tools["bash_jobs"] = (_, _, _, _) =>
            {
                try
                {
                    var jobs = bgBash.ListJobs();
                    if (jobs.Count == 0)
                        return Task.FromResult("(no background jobs in this session)");
                    var sb = new System.Text.StringBuilder();
                    sb.Append(jobs.Count).Append(" background job").Append(jobs.Count == 1 ? "" : "s").Append(":\n\n");
                    foreach (var j in jobs)
                    {
                        var status = j.Complete
                            ? $"complete (exit_code={j.ExitCode?.ToString() ?? "n/a"})"
                            : "running";
                        sb.Append("  ").Append(j.Id)
                          .Append("  [").Append(status).Append("]  ")
                          .Append(j.LineCount).Append(" lines  · ")
                          .Append(j.Name).Append('\n')
                          .Append("    ").Append(j.Command).Append('\n');
                    }
                    return Task.FromResult(sb.ToString());
                }
                catch (Exception ex)
                {
                    return Task.FromResult($"Error: {ex.Message}");
                }
            };
            any = true;
        }
        if (!any) return;

        // Lazy-load whichever schemas are listed in the profile.
        foreach (var name in new[] { "bash_background", "monitor", "bash_kill", "bash_jobs" })
        {
            if (!profile.Tools.Contains(name)) continue;
            try
            {
                var schemaPath = Path.Combine(AppContext.BaseDirectory, "schemas", name + ".json");
                if (File.Exists(schemaPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
                    schemas.Add(doc.RootElement.Clone());
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load {name} schema: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Register the chat-only `update_memory` tool — it captures the
    /// host workspace cwd in a closure so memory always lands at
    /// <c>&lt;workspace&gt;/.vett/memory/</c>, regardless of what the
    /// agent's terminal cwd happens to be. Schema is loaded in tandem
    /// with registration so the LLM sees the tool. Both
    /// <c>RunStdio</c> and <c>RunTerminal</c> share this helper.
    /// </summary>
    private static void RegisterMemoryTool(
        Dictionary<string, ToolFn> tools,
        List<JsonElement> schemas,
        Profile profile,
        string cwd)
    {
        if (!profile.Tools.Contains("update_memory")) return;

        tools["update_memory"] = (args, _, _, ct) =>
        {
            try
            {
                var name = Builtins.Str(args, "name");
                if (string.IsNullOrEmpty(name))
                    return Task.FromResult("Error: 'name' is required");

                var rawDelete = args.TryGetValue("delete", out var dv) ? dv : null;
                bool deleteFlag = rawDelete switch
                {
                    bool b => b,
                    JsonElement je when je.ValueKind == JsonValueKind.True => true,
                    JsonElement je when je.ValueKind == JsonValueKind.False => false,
                    string s => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };

                if (deleteFlag)
                    return Task.FromResult(MemoryStore.Delete(cwd, name));

                var type = Builtins.Str(args, "type", "project");
                var description = Builtins.Str(args, "description");
                var content = Builtins.Str(args, "content");
                return Task.FromResult(MemoryStore.Save(cwd, name, type, description, content));
            }
            catch (Exception ex)
            {
                return Task.FromResult($"Error: {ex.Message}");
            }
        };

        try
        {
            var schemaPath = Path.Combine(AppContext.BaseDirectory, "schemas", "update_memory.json");
            if (File.Exists(schemaPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
                schemas.Add(doc.RootElement.Clone());
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load update_memory schema: {ex.Message}");
        }
    }

    private static async Task RunStdio(
        Profile profile, IChatClient client, string model,
        DirectBash bash, string cwd, Dictionary<string, ToolFn> tools,
        List<JsonElement> schemas, Dictionary<string, MiddlewareFn> pluginMiddlewares,
        IReadOnlyList<ChatMessage>? seedHistory,
        bool planMode,
        bool agentStopEndsSession,
        CancellationToken ct)
    {
        var outLock = new object();
        void Emit(object o)
        {
            lock (outLock) Console.WriteLine(JsonSerializer.Serialize(o));
        }

        // Plan mode: construct the per-call unlock gate + service, wrap
        // the write-capable tools in the dispatch dict so each one
        // surfaces an "approve & switch" prompt on first attempt.
        // After the user approves once, the gate stays unlocked for
        // the rest of the session and writes dispatch normally — no
        // respawn, no context loss.
        Vett.Tools.PlanModeUnlockService? planUnlockService = null;
        if (planMode)
        {
            planUnlockService = new Vett.Tools.PlanModeUnlockService();
            var planGate = new Vett.Tools.PlanModeUnlockGate(
                planUnlockService,
                (type, data) => Emit(new { type, data }));
            Helpers.ApplyPlanModeToolRestrictions(tools, planGate);
        }

        // Stdio mode forwards EVERY event from the agent loop / team
        // coordinator to stdout as JSON so the chat extension's raw
        // view sees the full trace. Without this, agent-lifecycle
        // events (iteration_start, llm_response, tool_call_start /
        // _end, dispatch_*, cancelled, compacted) get dropped — the UI
        // looks frozen for minutes while vett is actually working.
        void OnEvent(Event e) => Emit(new { type = e.Type, data = e.Data });

        // ⭐ PROCESS-SCOPED, not the literal "local" it used to be. This id is
        // the dispatch worktree PANEL ID and every agent's sandbox session name
        // (see RunScope for the measurement and the full reasoning) — with a
        // fixed value, two chat sessions open at once shared one namespace under
        // the per-USER ~/.vett/dispatches root, and their default member names
        // made the task ids collide too. Reported on `ready` so a client reads
        // the id that is actually in use rather than a constant.
        var sessionId = RunScope.Qualify("local");

        Emit(new { type = "ready", data = new { session = sessionId, cwd = bash.Cwd, resumed = seedHistory is { Count: > 0 }, seeded_turns = seedHistory?.Count ?? 0 } });

        // session_start hook (#20). Side-effects only — decision is
        // run but ignored. Useful for "send a notification when a
        // chat begins," workspace setup checks, etc. Errors fail-open.
        if (profile.Hooks?.SessionStart is { Count: > 0 })
        {
            var sessionStartPayload = JsonSerializer.SerializeToElement(new
            {
                cwd = bash.Cwd,
                profile_name = profile.Name,
                resumed = seedHistory is { Count: > 0 },
                seeded_turns = seedHistory?.Count ?? 0,
            });
            _ = await Vett.Plugin.LifecycleHooks.RunAsync(
                profile, Vett.Plugin.LifecycleHookEvent.SessionStart, sessionStartPayload, ct);
        }

        var turnInterrupt = new TurnInterrupt();
        // Thread the profile's compaction settings into the FORCED (/compact)
        // path. Without these the explicit user-requested compaction silently
        // ran on the method defaults while every automatic checkpoint in the
        // same session honoured the profile — see CompactRequest.
        var compactRequest = new CompactRequest
        {
            KeepLastMessages = profile.Compaction?.KeepLastMessages ?? 5,
            SessionLogDir = profile.Compaction?.SessionLogDir,
        };
        var pauseRequest = new PauseRequest();
        // Wake channel that the pause/resume control flow uses to break
        // out of an idle WaitForUserOrWake without consuming a user
        // message — needed when the chat clicks Resume while the loop
        // is waiting at the iteration boundary.
        var wake = Channel.CreateUnbounded<bool>();

        // Background-bash service — owns the lifetime of every
        // background job in this session. Disposed at the end of
        // RunStdio so leftover processes (forgotten dev servers, etc)
        // don't outlive the chat. Tools registered below are closures
        // over `bgBash` so they share state without going through a
        // service-locator.
        using var bgBash = new Vett.Tools.BackgroundBashService(cwd);
        RegisterBackgroundBashTools(tools, schemas, profile, bgBash);

        // MCP server pool (#21). Connect every configured server in
        // parallel, discover their tools, and merge into the agent's
        // tool map under `mcp__<server>__<tool>` names. Disposed at
        // the end of RunStdio. Failures are logged and skipped — a
        // single broken server doesn't block the rest of the chat.
        using var mcpPool = new Vett.Mcp.McpClientPool();
        if (profile.McpServers is { Count: > 0 })
        {
            try
            {
                await mcpPool.ConnectAllAsync(profile.McpServers, ct);
                foreach (var (name, fn) in mcpPool.ToolFunctions)
                    tools[name] = fn;
                foreach (var s in mcpPool.ToolSchemas)
                    schemas.Add(s);
            }
            catch (Exception ex)
            {
                Emit(new { type = "mcp_connect_error", data = new { message = ex.Message } });
            }
        }

        // Permission gate — opt-in via `permissions:` block in the
        // profile YAML. Absent = no gating (back-compat). Present =
        // every tool call gets classified and either auto-allows,
        // prompts the user, or denies. The PermissionService
        // handles round-trip with the chat UI via stdin
        // permission_response messages routed below.
        var permissionService = new Vett.Tools.PermissionService();
        Vett.Tools.PermissionGate? permissionGate = null;
        if (profile.Permissions is not null)
        {
            var rules = new Vett.Tools.Permissions
            {
                Read = Vett.Tools.Permissions.Parse(profile.Permissions.Read, Vett.Tools.PermissionRule.Auto),
                Edit = Vett.Tools.Permissions.Parse(profile.Permissions.Edit, Vett.Tools.PermissionRule.Ask),
                TerminalSafe = Vett.Tools.Permissions.Parse(profile.Permissions.TerminalSafe, Vett.Tools.PermissionRule.Auto),
                TerminalUnsafe = Vett.Tools.Permissions.Parse(profile.Permissions.TerminalUnsafe, Vett.Tools.PermissionRule.Ask),
                Mcp = Vett.Tools.Permissions.Parse(profile.Permissions.Mcp, Vett.Tools.PermissionRule.Ask),
                Other = Vett.Tools.Permissions.Parse(profile.Permissions.Other, Vett.Tools.PermissionRule.Auto),
            };
            permissionGate = new Vett.Tools.PermissionGate(rules, permissionService,
                (type, data) => Emit(new { type, data }));
        }

        // ask_user_question round-trip service. Lives only in chat-stdio
        // mode — benchmark runs (vett run) don't have a human to answer.
        // Tool registration happens AFTER Builtins.All() so the closure
        // captures `Emit` + `questionService` without leaking them into
        // the static Builtins surface.
        var questionService = new Vett.Tools.UserQuestionService();
        tools["ask_user_question"] = async (args, _, _, ct) =>
        {
            var question = args.TryGetValue("question", out var q) ? q?.ToString() ?? "" : "";
            var choicesObj = args.TryGetValue("choices", out var cs) ? cs : null;
            var choices = new List<string>();
            if (choicesObj is IEnumerable<object?> en)
            {
                foreach (var c in en)
                    if (c is not null) choices.Add(c.ToString() ?? "");
            }
            else if (choicesObj is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var c in je.EnumerateArray()) choices.Add(c.GetString() ?? "");
            }
            var questionId = Guid.NewGuid().ToString("N");
            Emit(new
            {
                type = "user_question",
                data = new { question_id = questionId, question, choices },
            });
            try
            {
                var answer = await questionService.AwaitAnswerAsync(questionId, ct);
                return $"User answered: {answer}";
            }
            catch (OperationCanceledException)
            {
                // Session ended mid-question — return a clear marker so
                // the agent loop's history shows what happened. The
                // outer ct will tear the loop down anyway.
                return "[ask_user_question cancelled — session ended before user answered]";
            }
        };
        // Pull the schema in alongside it so the LLM sees the tool.
        // ask_user_question is opt-in via the profile's `tools` list —
        // not added automatically here. ChatCommand still loads the
        // schema if the profile lists it.
        if (profile.Tools.Contains("ask_user_question"))
        {
            try
            {
                var schemaPath = Path.Combine(
                    AppContext.BaseDirectory, "schemas", "ask_user_question.json");
                if (File.Exists(schemaPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
                    schemas.Add(doc.RootElement.Clone());
                }
            }
            catch (Exception ex)
            {
                // Static method scope — no `logger` here. Stderr is the
                // chat extension's only signal for soft failures like
                // this; it'll surface in the panel's error tail.
                Console.Error.WriteLine($"Failed to load ask_user_question schema: {ex.Message}");
            }
        }

        // Inbound user-message channel — bounded so a flood of
        // chat-side messages doesn't pile up in memory if the agent
        // is mid-LLM-call.
        var ch = Channel.CreateBounded<string>(4);
        _ = Task.Run(async () =>
        {
            string? l;
            while ((l = Console.In.ReadLine()) is not null)
            {
                try
                {
                    var d = JsonDocument.Parse(l);
                    var type = d.RootElement.GetProperty("type").GetString();
                    if (type == "user_message")
                    {
                        var bodyText = d.RootElement.TryGetProperty("text", out var t)
                            ? (t.GetString() ?? "")
                            : "";

                        // Optional `images` array on the user_message
                        // protocol. Each entry: { data: <base64>,
                        // media_type: "image/png" | "image/jpeg" | ... }.
                        // Encoded into the channel-string via
                        // Chat.EncodeUserInput; the agent loop's User()
                        // factory parses it back into a multi-content
                        // ChatMessage at consume time. See
                        // ChatMessageHelpers.cs for the marker contract.
                        List<Chat.ImagePart>? images = null;
                        if (d.RootElement.TryGetProperty("images", out var imgs)
                            && imgs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var img in imgs.EnumerateArray())
                            {
                                var data = img.TryGetProperty("data", out var dd) ? (dd.GetString() ?? "") : "";
                                var mediaType = img.TryGetProperty("media_type", out var mm) ? (mm.GetString() ?? "image/png") : "image/png";
                                if (string.IsNullOrEmpty(data)) continue;
                                images ??= new List<Chat.ImagePart>();
                                images.Add(new Chat.ImagePart(data, mediaType));
                            }
                        }

                        // user_prompt_submit hook (#20). Fires before
                        // the message reaches the agent loop. Allow →
                        // forward unchanged. Deny → drop + emit notice.
                        // Modify → forward the rewritten text. Images
                        // are not exposed to / mutable by the hook in
                        // v1 (binary blob handling complicates the
                        // wire format; v2 candidate).
                        if (profile.Hooks?.UserPromptSubmit is { Count: > 0 })
                        {
                            var promptPayload = JsonSerializer.SerializeToElement(new
                            {
                                text = bodyText,
                                has_images = images?.Count > 0,
                            });
                            var promptDecision = await Vett.Plugin.LifecycleHooks.RunAsync(
                                profile, Vett.Plugin.LifecycleHookEvent.UserPromptSubmit, promptPayload, ct);
                            if (promptDecision.Action == "deny")
                            {
                                Emit(new
                                {
                                    type = "user_prompt_blocked",
                                    data = new { reason = promptDecision.Reason ?? "blocked by hook" },
                                });
                                continue; // don't enqueue
                            }
                            if (promptDecision.Action == "modify"
                                && promptDecision.Modified.HasValue
                                && promptDecision.Modified.Value.TryGetProperty("text", out var modText)
                                && modText.ValueKind == JsonValueKind.String)
                            {
                                bodyText = modText.GetString() ?? bodyText;
                            }
                        }

                        ch.Writer.TryWrite(Chat.EncodeUserInput(bodyText, images));
                    }
                    else if (type == "cancel")
                    {
                        // User pressed Stop. Aborts the in-flight LLM
                        // call + tool calls. Agent loop catches the
                        // OperationCanceledException, drops the partial
                        // turn, and waits for the next user message.
                        turnInterrupt.Interrupt();
                    }
                    else if (type == "compact")
                    {
                        // User typed /compact. Force a checkpoint at the
                        // next iteration boundary regardless of the
                        // auto-threshold.
                        compactRequest.Request();
                    }
                    else if (type == "pause")
                    {
                        // User clicked Pause. Loop checks the flag at
                        // the next iteration boundary, emits `paused`,
                        // and waits for either a user message OR an
                        // explicit resume.
                        pauseRequest.Request();
                    }
                    else if (type == "resume")
                    {
                        // User clicked Resume. Clear the flag and ping
                        // the wake channel so a loop currently parked
                        // in WaitForUserOrWake exits without needing a
                        // user message.
                        pauseRequest.Resume();
                        wake.Writer.TryWrite(true);
                    }
                    else if (type == "user_question_answer")
                    {
                        // Answer for a previously-asked ask_user_question
                        // tool call. Routed through the question service
                        // by question_id so multi-question scenarios stay
                        // straight (rare, but cheap to handle correctly).
                        var qid = d.RootElement.TryGetProperty("question_id", out var qq) ? qq.GetString() ?? "" : "";
                        var answer = d.RootElement.TryGetProperty("text", out var aa) ? aa.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(qid))
                            questionService.PostAnswer(qid, answer);
                    }
                    else if (type == "permission_response")
                    {
                        // Answer for a previously-emitted permission_request.
                        // Routed through PermissionService by request_id so
                        // parallel tool calls don't crosswire decisions.
                        // `decision` is the literal answer for THIS call;
                        // `remember_for_kind` (optional) flips the per-session
                        // rule so subsequent same-kind calls don't re-prompt.
                        var rid = d.RootElement.TryGetProperty("request_id", out var rr) ? rr.GetString() ?? "" : "";
                        var decisionStr = d.RootElement.TryGetProperty("decision", out var dd) ? dd.GetString() ?? "ask" : "ask";
                        var remember = d.RootElement.TryGetProperty("remember_for_kind", out var rm)
                            && rm.ValueKind == JsonValueKind.True;
                        var decisionRule = Vett.Tools.Permissions.Parse(decisionStr, Vett.Tools.PermissionRule.Ask);
                        if (!string.IsNullOrEmpty(rid))
                            permissionService.PostDecision(rid, new Vett.Tools.PermissionResponse(decisionRule, remember));
                    }
                    else if (type == "plan_mode_action_response")
                    {
                        // Answer for a plan_mode_action_request from the
                        // unlock gate. `approve` true → first call unlocks
                        // the gate for the rest of the session AND emits
                        // chat_mode_changed:execute (the gate handles both).
                        // `approve` false → wrap returns an error to the
                        // agent and the gate stays locked.
                        var rid = d.RootElement.TryGetProperty("request_id", out var rr) ? rr.GetString() ?? "" : "";
                        var approve = d.RootElement.TryGetProperty("approve", out var ap)
                            && ap.ValueKind == JsonValueKind.True;
                        if (!string.IsNullOrEmpty(rid) && planUnlockService is not null)
                        {
                            planUnlockService.PostDecision(rid, approve);
                        }
                    }
                }
                catch { }
            }
            ch.Writer.TryComplete();
        }, ct);

        var llm = new LlmSettings(client, model, profile.Llm.Temperature ?? 1.0, profile.Llm.TopP, profile.Llm.MaxOutputTokens,
            profile.Llm.PresencePenalty, profile.Llm.FrequencyPenalty);
        var caps = new AgentCapabilities(tools, schemas, MiddlewareResolver.ResolveOrDefault(profile.Middleware, pluginMiddlewares, llm, profile.Compaction));
        // AgentEnvironment carries the auto-check commands (lint/test
        // run after every iteration with a successful file_editor
        // write), the wake signal (used by Resume to break the loop's
        // idle wait), and the PauseRequest (loop checks at iteration
        // boundary, emits `paused` + waits when set).
        var env = new AgentEnvironment(
            bash, sessionId, profile.MaxIterations, OnEvent,
            WakeSignal: wake.Reader,
            AutoLintCmd: profile.LintCmd,
            AutoTestCmd: profile.TestCmd,
            PauseRequest: pauseRequest,
            PermissionGate: permissionGate,
            // Pass the profile so AgentLoop's DispatchAsync can reach
            // its `hooks:` block for PreToolUse / PostToolUse. Null in
            // benchmark runs (Runner.cs) — hooks fire only in chat.
            Profile: profile,
            // `vett chat` IS the human. This is the one entry point where the
            // runaway cap parks and waits instead of ending the session.
            HumanAtTheKeyboard: true,
            // ...and by default a person's chat is not ended by the agent
            // deciding it is finished. See AgentEnvironment.
            AgentStopEndsSession: agentStopEndsSession);

        AgentResult r;
        if (profile.Team is not null)
        {
            // Team profile in chat: leader is the interactive front-end;
            // members spawn on demand via assign_task with their events
            // tagged thread_id=<member name>.
            r = await TeamCoordinator.RunInteractiveAsync(
                profile, client, model, bash, sessionId,
                ch.Reader,
                t => Emit(new { type = "assistant_text", text = t }),
                () => Emit(new { type = "user_input_needed" }),
                OnEvent, seedHistory, turnInterrupt, compactRequest, cwd, ct,
                leaderPermissionGate: permissionGate,
                planMode: planMode,
                planUnlockService: planUnlockService,
                humanAtTheKeyboard: true,
                agentStopEndsSession: agentStopEndsSession);
        }
        else
        {
            r = await AgentLoop.RunInteractiveAsync(
                llm, caps, env, profile.SystemPrompt, ch.Reader,
                t => Emit(new { type = "assistant_text", text = t }),
                () => Emit(new { type = "user_input_needed" }), seedHistory, turnInterrupt, compactRequest, ct);
        }

        // V2 harness edit: surface token usage on the terminal event so the
        // queue worker can persist tokens → $ without scraping every
        // llm_response line. On the team/leader path this is the LEADER's
        // own usage; per-member usage rides the dispatch_end events (also V2).
        Emit(new
        {
            type = "done",
            data = new
            {
                stop_reason = r.StopReason,
                iterations = r.Iterations,
                input_tokens = r.InputTokens,
                output_tokens = r.OutputTokens,
                total_tokens = r.InputTokens + r.OutputTokens,
            },
        });

        // stop hook (#20). Side-effects only — useful for "alert me
        // when the agent stuck-detected" or "ship telemetry on
        // unsuccessful run."
        if (profile.Hooks?.Stop is { Count: > 0 })
        {
            var stopPayload = JsonSerializer.SerializeToElement(new
            {
                stop_reason = r.StopReason,
                iterations = r.Iterations,
            });
            _ = await Vett.Plugin.LifecycleHooks.RunAsync(
                profile, Vett.Plugin.LifecycleHookEvent.Stop, stopPayload, ct);
        }
        // session_end hook (#20). Symmetric with session_start — runs
        // exactly once per chat session, regardless of how the loop
        // exited (cancelled, finished, errored).
        if (profile.Hooks?.SessionEnd is { Count: > 0 })
        {
            var sessionEndPayload = JsonSerializer.SerializeToElement(new
            {
                cwd = bash.Cwd,
                profile_name = profile.Name,
                stop_reason = r.StopReason,
                iterations = r.Iterations,
            });
            _ = await Vett.Plugin.LifecycleHooks.RunAsync(
                profile, Vett.Plugin.LifecycleHookEvent.SessionEnd, sessionEndPayload, ct);
        }
    }

    private static async Task RunTerminal(
        Profile profile, IChatClient client, string model,
        DirectBash bash, string cwd, Dictionary<string, ToolFn> tools,
        List<JsonElement> schemas, Dictionary<string, MiddlewareFn> pluginMiddlewares,
        IReadOnlyList<ChatMessage>? seedHistory,
        bool agentStopEndsSession,
        Action<Event> onEvent, CancellationToken ct)
    {
        Console.WriteLine($"\x1b[1;36mvett chat\x1b[0m — {cwd}");
        if (seedHistory is { Count: > 0 })
            Console.WriteLine($"\x1b[2m  resumed with {seedHistory.Count} prior turns\x1b[0m");
        Console.WriteLine();

        var ch = Channel.CreateBounded<string>(4);
        _ = Task.Run(() =>
        {
            string? l;
            while ((l = Console.ReadLine()) is not null)
            {
                if (!string.IsNullOrWhiteSpace(l))
                    ch.Writer.TryWrite(l);
            }
            ch.Writer.TryComplete();
        }, ct);

        var llm = new LlmSettings(client, model, profile.Llm.Temperature ?? 1.0, profile.Llm.TopP, profile.Llm.MaxOutputTokens,
            profile.Llm.PresencePenalty, profile.Llm.FrequencyPenalty);
        var caps = new AgentCapabilities(tools, schemas, MiddlewareResolver.ResolveOrDefault(profile.Middleware, pluginMiddlewares, llm, profile.Compaction));
        // Same process-scoping as RunStdio above — see RunScope. The terminal
        // front-end is solo-only today, so this one is the sandbox session name
        // rather than a panel id, but two `vett chat` terminals against one
        // shared sandbox would collide on it exactly the same way.
        var env = new AgentEnvironment(bash, RunScope.Qualify("local"), profile.MaxIterations, onEvent,
            // The terminal front end reads Console.ReadLine, so there is a
            // person here by construction. This was simply missed when the flag
            // was introduced, which left `vett chat` (no --stdio) terminating on
            // finish and unable to park on the runaway cap. Piped stdin is safe:
            // EOF completes the channel and every park exits through its
            // "closed" arm instead of waiting forever.
            HumanAtTheKeyboard: true,
            AgentStopEndsSession: agentStopEndsSession);

        var r = await AgentLoop.RunInteractiveAsync(
            llm, caps, env, profile.SystemPrompt, ch.Reader,
            t => Console.WriteLine($"\n\x1b[1;32massistant:\x1b[0m {t}"),
            () => Console.Write("\n\x1b[1;33myou:\x1b[0m "), seedHistory, null, null, ct);

        Console.WriteLine($"\n\x1b[1;36m---\x1b[0m {r.Iterations} iters, {r.InputTokens}/{r.OutputTokens} tokens, {r.StopReason}");
    }
}
