using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vett.Config;

public sealed class Profile
{
    [YamlMember(Alias = "name")] public string Name { get; set; } = "";
    [YamlMember(Alias = "description")] public string Description { get; set; } = "";
    [YamlMember(Alias = "sandbox")] public SandboxConfig Sandbox { get; set; } = new();
    [YamlMember(Alias = "llm")] public LlmConfig Llm { get; set; } = new();
    [YamlMember(Alias = "system_prompt")] public string SystemPrompt { get; set; } = "";
    [YamlMember(Alias = "system_prompt_file")] public string? SystemPromptFile { get; set; }
    [YamlMember(Alias = "user_template")] public string UserTemplate { get; set; } = "";
    [YamlMember(Alias = "user_template_file")] public string? UserTemplateFile { get; set; }
    [YamlMember(Alias = "tools")] public List<string> Tools { get; set; } = [];
    [YamlMember(Alias = "middleware")] public List<string> Middleware { get; set; } = [];
    [YamlMember(Alias = "max_iterations")] public int MaxIterations { get; set; } = 100;
    [YamlMember(Alias = "timeout_minutes")] public int TimeoutMinutes { get; set; } = 30;
    [YamlMember(Alias = "team")] public TeamConfig? Team { get; set; }

    /// <summary>
    /// Collapse identical (tool, arguments) calls emitted in the SAME
    /// assistant turn: execute the first, replay its result to the
    /// duplicates. Counters the single-turn duplicate fan-out (a leader
    /// emitting 102 identical `file_editor view` calls in one turn,
    /// observed 2026-07-09). Prompt rules like "never repeat a completed
    /// action" only constrain ACROSS turns, so they cannot reach this.
    /// Default true; set false to restore raw pass-through.
    /// </summary>
    [YamlMember(Alias = "turn_dedupe")] public bool TurnDedupe { get; set; } = true;
    [YamlMember(Alias = "tool_overrides")] public Dictionary<string, Dictionary<string, ParamOverride>>? ToolOverrides { get; set; }

    /// <summary>
    /// Auto-lint command run after every iteration where the agent edited
    /// at least one file via file_editor (create / str_replace / insert).
    /// Non-zero exit → output is appended to the conversation as a
    /// `&lt;lint_feedback&gt;` user-style turn so the agent picks it up
    /// on the next LLM call. Empty / null = disabled.
    ///
    /// Aider's pattern. Drives the "agent finishes successfully" vs
    /// "agent finishes with code that compiles + lints" gap.
    ///
    /// Run via the active sandbox's bash, so it works in both Local
    /// (native bash) and Docker modes.
    /// </summary>
    [YamlMember(Alias = "lint_cmd")] public string? LintCmd { get; set; }

    /// <summary>
    /// Same shape as <see cref="LintCmd"/> but for tests. Typically
    /// slower than lint and triggered less often — most teams set this
    /// only for tight unit-test commands they're happy to run on every
    /// agent edit. Empty / null = disabled.
    /// </summary>
    [YamlMember(Alias = "test_cmd")] public string? TestCmd { get; set; }

    /// <summary>
    /// Per-kind tool-call permission rules. When null, no gating —
    /// every tool call runs without prompting (pre-#5 behavior). When
    /// present, the chat session wires up a
    /// <see cref="Vett.Tools.PermissionGate"/> that classifies each
    /// tool call and either auto-allows, prompts the user via the
    /// chat UI, or denies. Benchmark runs (`vett run`) ignore this
    /// block entirely — they have no human in the loop.
    ///
    /// Bundled `coding.yaml` ships with sensible defaults: read +
    /// terminal_safe = auto; edit + terminal_unsafe + mcp = ask.
    /// </summary>
    [YamlMember(Alias = "permissions")] public PermissionsConfig? Permissions { get; set; }

    /// <summary>
    /// Repo-map injection (#14). When non-null + Enabled, the chat
    /// session walks the workspace at start, extracts top-level
    /// signatures via regex per language, and prepends a compact
    /// `&lt;repo_map&gt;` block to the system prompt so the agent has
    /// "what's in this codebase" grounding without having to ls / grep
    /// before every task. Bundled `coding.yaml` enables it by default
    /// with a 4000-char budget. Disable per-profile by omitting the
    /// block or setting `enabled: false`.
    /// </summary>
    [YamlMember(Alias = "repo_map")] public RepoMapConfig? RepoMap { get; set; }

    /// <summary>
    /// Lifecycle hooks (#20). Six event keys
    /// (<c>pre_tool_use</c> / <c>post_tool_use</c> / <c>user_prompt_submit</c> /
    /// <c>session_start</c> / <c>session_end</c> / <c>stop</c>) each map to a
    /// list of subscribed shell commands. Each command receives a JSON
    /// envelope on stdin describing the event + payload, and may
    /// return a JSON decision (allow / deny / modify) on stdout.
    /// PreToolUse + UserPromptSubmit honor the decision; other events
    /// run for side effects only in v1. See
    /// <see cref="Vett.Plugin.LifecycleHooks"/>.
    /// </summary>
    [YamlMember(Alias = "hooks")] public Vett.Plugin.HooksConfig? Hooks { get; set; }

    /// <summary>
    /// MCP (Model Context Protocol) servers to connect on chat-session
    /// start. Map of friendly name → server config; each server's
    /// discovered tools are namespaced as
    /// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> so they don't collide with
    /// builtins or each other. Connection failures log a warning and
    /// skip that server. v1 supports stdio transport only — HTTP / SSE
    /// is a v2 lift. See <see cref="Vett.Mcp.McpClientPool"/>.
    /// </summary>
    [YamlMember(Alias = "mcp_servers")] public Dictionary<string, Vett.Mcp.McpServerConfig>? McpServers { get; set; }

    /// <summary>
    /// Context-compaction tuning for the compaction middleware
    /// (<c>milestone_checkpoint</c>, <c>llm_summarizing_condenser</c>,
    /// <c>observation_elision</c>). Null = shipped defaults
    /// (30k threshold / keep-last-5 / 200-char elision) — the pre-config
    /// behavior. These thresholds used to be hardcoded at construction, so
    /// there was no way to raise the 30k trigger for a large-context model
    /// like deepseek-chat (128k) short of an editing the source. Set
    /// <c>threshold_tokens: 100000</c> to let a long run breathe before it
    /// compacts. Members inherit the profile block unless they set their
    /// own (see <see cref="MemberConfig.Compaction"/>).
    /// </summary>
    [YamlMember(Alias = "compaction")] public CompactionConfig? Compaction { get; set; }

    /// <summary>
    /// Relay-mode context telemetry (R1, 2026-07-10). When set with a
    /// positive <c>context_window</c>, every LLM response's REAL prompt
    /// size (<c>Usage.InputTokenCount</c> — the whole prompt just sent,
    /// not an estimate) is compared to the window: a <c>context_status</c>
    /// event is emitted per response, and crossing
    /// <c>handoff_threshold_pct</c> injects a wrap-up-now instruction so a
    /// relay leader exits SHARP at the threshold instead of degrading
    /// toward the hard limit. Iteration count is a bad proxy for context
    /// fullness; this is the measured signal. Null / window 0 = feature
    /// off, zero behavior change.
    /// </summary>
    [YamlMember(Alias = "relay")] public RelayConfig? Relay { get; set; }

    /// <summary>
    /// Absolute path of the file this profile was deserialised from, set by
    /// <see cref="Yaml.LoadProfile"/>. NOT a YAML key — writing
    /// <c>source_path:</c> in a profile is an unrecognised key and the audit
    /// reports it as one.
    ///
    /// Exists so a run artifact can name the RULER, not just its label. A
    /// profile NAME is ambiguous by construction: <see cref="Yaml.Resolve{T}"/>
    /// searches &lt;cwd&gt;/profiles, then ~/.vett/profiles, then the install
    /// directory, and those three copies are not required to agree — the
    /// user store in particular is a SHARED tool store that other runs and
    /// other people write to. Two runs of "the same profile" from different
    /// working directories can therefore be scored by different rulers, and
    /// before this field no artifact recorded which.
    ///
    /// Null when the profile came from text rather than a file
    /// (<see cref="Yaml.ParseProfile"/>) or was constructed in code.
    /// </summary>
    [YamlIgnore] public string? SourcePath { get; set; }
}

/// <summary>YAML schema for <see cref="Profile.Relay"/>. See
/// <see cref="Vett.Agent.RelayContext"/> for the decision logic.</summary>
public sealed class RelayConfig
{
    /// <summary>The model's REAL context window in tokens (e.g. 1048576
    /// for deepseek v4, 131072 for v3 chat). 0 (default) disables relay
    /// context telemetry entirely.</summary>
    [YamlMember(Alias = "context_window")] public int ContextWindow { get; set; }

    /// <summary>Percent of the window at which the harness starts telling
    /// the agent to wrap up and hand off. Default 65 — exiting sharp at
    /// 65% beats degrading at 95%. Re-nudges every +5 points past the
    /// last nudge so one warning can't be silently buried.</summary>
    [YamlMember(Alias = "handoff_threshold_pct")] public int HandoffThresholdPct { get; set; } = 65;
}

/// <summary>
/// YAML schema for <see cref="Profile.Compaction"/> /
/// <see cref="MemberConfig.Compaction"/>. Every field has a default that
/// reproduces the historical hardcoded value, so an omitted block or an
/// omitted field changes nothing.
/// </summary>
public sealed class CompactionConfig
{
    /// <summary>Estimated-token trigger for milestone_checkpoint /
    /// llm_summarizing_condenser. Below this, they no-op. Default 30k
    /// (was hardcoded). Raise toward the model's real window (e.g. 100k
    /// for a 128k model) so long runs don't compact prematurely.</summary>
    [YamlMember(Alias = "threshold_tokens")] public int ThresholdTokens { get; set; } = 30_000;

    /// <summary>How many recent messages survive a checkpoint/condense
    /// verbatim (the rest are summarized). Default 5.</summary>
    [YamlMember(Alias = "keep_last_messages")] public int KeepLastMessages { get; set; } = 5;

    /// <summary>observation_elision: truncate tool outputs older than the
    /// last N to this many chars. Default 200.</summary>
    [YamlMember(Alias = "elision_max_chars")] public int ElisionMaxChars { get; set; } = 200;

    /// <summary>observation_elision: how many most-recent tool outputs to
    /// keep at full length. Default 5.</summary>
    [YamlMember(Alias = "elision_keep_last")] public int ElisionKeepLast { get; set; } = 5;

    /// <summary>
    /// replan_checkpoint trigger. This is the HARD reset ("detach from
    /// history, continue on a fresh plan") variant, distinct from the
    /// inline <c>llm_summarizing_condenser</c>: at this token count it
    /// persists the full conversation to a resumable JSONL (kept as
    /// reference — nothing is lost) and re-seeds the live context with a
    /// forward-looking PLAN produced by the model, discarding the raw
    /// turn-by-turn history from the working window. Use it for very long
    /// autonomous runs where a clean plan-based restart beats an
    /// ever-growing summarized transcript. 0 = disabled (default). Should
    /// be set HIGHER than <see cref="ThresholdTokens"/> if both the
    /// condenser and replan are listed, so routine relief happens first.
    /// </summary>
    [YamlMember(Alias = "replan_threshold_tokens")] public int ReplanThresholdTokens { get; set; }

    /// <summary>
    /// Directory where replan_checkpoint writes its full-history JSONL
    /// snapshots (one per checkpoint, in the same format `--resume`
    /// reads). Null → <c>~/.vett/chat-sessions/</c>. The snapshot is the
    /// "history is still worth showing as history" artifact — the live
    /// context detaches from it, but it stays on disk, resumable.
    /// </summary>
    [YamlMember(Alias = "session_log_dir")] public string? SessionLogDir { get; set; }
}

/// <summary>
/// YAML schema for <see cref="Profile.RepoMap"/>. Optional fields:
/// <list type="bullet">
/// <item><c>enabled</c>: false → skip injection even if the block is present.</item>
/// <item><c>max_chars</c>: cap on the rendered block (default 4000 ≈ ~1k tokens).</item>
/// <item><c>extra_excludes</c>: directory names to skip in addition to the defaults
/// (node_modules / bin / obj / dist / etc).</item>
/// </list>
/// </summary>
public sealed class RepoMapConfig
{
    [YamlMember(Alias = "enabled")] public bool Enabled { get; set; } = true;
    [YamlMember(Alias = "max_chars")] public int MaxChars { get; set; } = Vett.Config.RepoMap.DefaultMaxChars;
    [YamlMember(Alias = "extra_excludes")] public List<string>? ExtraExcludes { get; set; }
    /// <summary>
    /// How files in the repo map are ordered. <c>density</c> is the
    /// session-12 v1 behavior — order by raw signature count.
    /// <c>references</c> (default as of session 16's #14 close-out)
    /// adds a PageRank-style cross-file scan: each file's score is
    /// `signature_count + (other-files-that-reference-its-symbols)`,
    /// so widely-used utility modules surface above leaf feature files.
    /// Falls back to <c>references</c> on unknown values.
    /// </summary>
    [YamlMember(Alias = "ranking")] public string? Ranking { get; set; }
}

/// <summary>
/// YAML schema for <see cref="Profile.Permissions"/>. Each field is
/// a string (`auto` / `ask` / `deny`) parsed at session start. Unknown
/// values fall back to the kind's default rule rather than failing
/// loudly — keeps profile authoring forgiving.
/// </summary>
public sealed class PermissionsConfig
{
    [YamlMember(Alias = "read")] public string? Read { get; set; }
    [YamlMember(Alias = "edit")] public string? Edit { get; set; }
    [YamlMember(Alias = "terminal_safe")] public string? TerminalSafe { get; set; }
    [YamlMember(Alias = "terminal_unsafe")] public string? TerminalUnsafe { get; set; }
    [YamlMember(Alias = "mcp")] public string? Mcp { get; set; }
    [YamlMember(Alias = "other")] public string? Other { get; set; }
}

public sealed class ParamOverride
{
    [YamlMember(Alias = "required")] public bool? Required { get; set; }
    [YamlMember(Alias = "description")] public string? Description { get; set; }
    [YamlMember(Alias = "default")] public string? Default { get; set; }
}

public sealed class SandboxConfig
{
    [YamlMember(Alias = "type")] public string Type { get; set; } = "";
    [YamlMember(Alias = "run_as_root")] public bool RunAsRoot { get; set; }
    [YamlMember(Alias = "home")] public string Home { get; set; } = "";
    [YamlMember(Alias = "default_cwd")] public string DefaultCwd { get; set; } = "";
}

public sealed class LlmConfig
{
    private string? _provider;

    /// <summary>Provider: "local", "openai", "anthropic", "azure", "google". Default: "local" (OpenAI-compatible).</summary>
    [YamlMember(Alias = "provider")]
    public string Provider
    {
        get => _provider ?? "local";
        // Empty stays "unset" so `provider:` with no value keeps inheriting,
        // exactly as the old !string.IsNullOrEmpty guard in Merge did.
        set => _provider = string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// True when <c>provider:</c> was actually written (in YAML or in code),
    /// false when <see cref="Provider"/> is only reporting its "local"
    /// default.
    ///
    /// Needed because <see cref="Provider"/> is a non-nullable string whose
    /// default is itself a VALID value, so "unset" and "explicitly local"
    /// are indistinguishable from the property alone. Merging used a
    /// <c>!= "local"</c> sentinel to paper over that, which silently
    /// DISCARDED an explicit member-level <c>provider: local</c> under a
    /// cloud base profile — the member kept the cloud provider. Same class
    /// of bug the nullable Temperature / TopP / RequestTimeoutSeconds /
    /// NumRetries fields were converted to fix; this one kept its sentinel
    /// because the type is a string.
    /// </summary>
    [YamlIgnore] public bool HasExplicitProvider => _provider is not null;
    [YamlMember(Alias = "endpoint")] public string Endpoint { get; set; } = "";
    [YamlMember(Alias = "model")] public string Model { get; set; } = "";
    /// <summary>Name of env var containing the API key. Key never goes in YAML.</summary>
    [YamlMember(Alias = "api_key_env")] public string ApiKeyEnv { get; set; } = "";
    /// <summary>Null = not set; consumers should fall back to 1.0. Nullable so member overrides aren't ambiguous with the default.</summary>
    [YamlMember(Alias = "temperature")] public double? Temperature { get; set; }
    /// <summary>Null = not set; the request omits top_p entirely so the
    /// provider applies its own default (vLLM defaults to 1.0).</summary>
    [YamlMember(Alias = "top_p")] public double? TopP { get; set; }
    /// <summary>
    /// OpenAI-style presence penalty (-2..2). Null = not set; the request omits it.
    ///
    /// MEASURED, not supposed (EpicForge run 6, 2026-09-05). At temperature 0.3
    /// with no penalty, a seat asked to replace ONE duplicate line re-issued the
    /// identical replacement five times and then filled its whole 4096-token
    /// reply with a repeating monologue ("I keep using the same text ...") and
    /// no tool call -- 33 of 33 capped replies across the run's 36 team logs
    /// were of this shape, 18% of every output token the run generated.
    /// Replaying the exact failing request against the same engine: no penalty
    /// 2/2 capped; temperature 0.8 2/2 capped; presence 0.8 + frequency 0.3
    /// 2/2 reached a tool call carrying a genuinely different string.
    /// Temperature is not the lever for a copy loop; a penalty on tokens already
    /// present is.
    /// </summary>
    [YamlMember(Alias = "presence_penalty")] public double? PresencePenalty { get; set; }
    /// <summary>OpenAI-style frequency penalty (-2..2). Null = not set; the request omits it. See <see cref="PresencePenalty"/>.</summary>
    [YamlMember(Alias = "frequency_penalty")] public double? FrequencyPenalty { get; set; }
    /// <summary>Per-request network timeout. Null = not set; consumers fall back to 300s.
    /// Nullable so member overrides aren't ambiguous with the default (same reasoning as Temperature).</summary>
    [YamlMember(Alias = "request_timeout_seconds")] public int? RequestTimeoutSeconds { get; set; }
    /// <summary>Same-endpoint transport retries for transient failures. Null = not set; consumers fall back to 5.</summary>
    [YamlMember(Alias = "num_retries")] public int? NumRetries { get; set; }
    /// <summary>
    /// Hard ceiling on the tokens the model may GENERATE in one call. Null =
    /// not set, and null is genuinely unbounded: the request omits max_tokens,
    /// so vLLM allows (max_model_len - prompt_tokens).
    ///
    /// MEASURED, not supposed. On 2026-09-01 an EpicForge E3 run had two seats
    /// go silent for 800+ seconds. The server was not stalled and not
    /// saturated: /metrics showed num_requests_running=2, num_requests_waiting=0,
    /// and generation_tokens_total climbing ~72 tok/s across the two while
    /// prompt_tokens_total stayed FLAT -- the same two requests, still writing.
    /// With max_model_len=204800 and a 31k prompt, an unbounded request may
    /// generate ~173,000 tokens; at the measured ~36 tok/s per request that is
    /// EIGHTY MINUTES for one call, against a request_timeout_seconds of 180.
    ///
    /// So the timeout could never win: the call is killed at 180s, retried, and
    /// each retry starts another unbounded generation. Worse, the abandoned
    /// generation keeps running server-side (vLLM logged no aborts), so a
    /// single stuck seat parks several zombie generations on the GPU -- which
    /// is throughput spent on answers nobody will ever read.
    ///
    /// Set this to something the timeout can actually cover:
    /// max_output_tokens &lt; request_timeout_seconds x observed tokens/sec.
    /// Then a runaway returns TRUNCATED at a bounded cost instead of hanging.
    /// </summary>
    [YamlMember(Alias = "max_output_tokens")] public int? MaxOutputTokens { get; set; }
    /// <summary>
    /// Stream the reply and bound SILENCE instead of total generation time.
    /// Null = on (the default); <c>stream: false</c> restores the buffered
    /// request, whose <c>request_timeout_seconds</c> then caps the WHOLE
    /// generation again.
    ///
    /// MEASURED 2026-09-05 (EpicForge run 5, HANDOFF law 135): under the
    /// buffered request the invariant above -- max_output_tokens &lt;
    /// request_timeout_seconds x tokens/sec -- silently went FALSE when eight
    /// seats shared the engine (~12.5 tok/s per request, so 180 s covers
    /// ~2,250 tokens against a 4,096 cap). Every reply longer than that timed
    /// out, was retried identically three times by the SDK and three times
    /// more by AgentLoop, and six seats died at the 1,800 s deadline with
    /// nothing landed. Streaming makes the timeout mean "the engine said
    /// nothing for N seconds", which no honest long answer can trip. See
    /// <see cref="Vett.Llm.SilenceBoundedChatClient"/>.
    /// </summary>
    [YamlMember(Alias = "stream")] public bool? Stream { get; set; }
    /// <summary>
    /// Qwen3-family chat_template_kwargs.enable_thinking. When set, every
    /// /chat/completions request gets `chat_template_kwargs: { enable_thinking: <value> }`
    /// merged into its body. Null = don't send the kwarg (server default).
    /// false = force thinking off (fast path for AEON / Qwen3 / Qwen3.6).
    /// </summary>
    [YamlMember(Alias = "enable_thinking")] public bool? EnableThinking { get; set; }

    /// <summary>
    /// Name of a capability in the capability catalogue
    /// (<c>capabilities/default.yaml</c>) - e.g. "flash", "pro".
    ///
    /// THIS IS THE FIELD THAT MAKES THE CONSTRAINT SYSTEM SHARED. When set,
    /// <see cref="Vett.Llm.ChatClientFactory.Create"/> stops reading
    /// endpoint/model/api_key_env off this block and instead asks the
    /// disk-backed <see cref="Vett.Capacity.CapacityBroker"/> at
    /// <c>~/.vett/capacity</c> for a lease. Because the ledger is a FILE, not
    /// process state, `vett run` and `vett chat` and every dispatched seat
    /// contend against the SAME pool - which is the entire point. Leave it
    /// empty (the default, and what all 18 shipped profiles do today) and this
    /// class behaves byte-for-byte as it did before the field existed.
    ///
    /// FAILS CLOSED. Naming a capability the catalogue cannot satisfy is an
    /// ERROR, not a silent fall-through to <see cref="Endpoint"/>. A budget you
    /// can bypass by mistyping it is not a budget.
    /// </summary>
    [YamlMember(Alias = "capability")] public string Capability { get; set; } = "";

    /// <summary>
    /// Scheduling priority when contending for a capability: "interactive"
    /// (100), "batch" (50), or "background" (10). Only consulted when
    /// <see cref="Capability"/> is set. Empty = let the caller decide, which in
    /// practice means `vett chat` claims interactive and everything else claims
    /// batch - a human waiting at a prompt outranks a queued run.
    /// </summary>
    [YamlMember(Alias = "priority")] public string Priority { get; set; } = "";

    /// <summary>
    /// The context window this seat is asking for, in tokens. REQUIRED when
    /// <see cref="Capability"/> is set, and ignored otherwise.
    ///
    /// It is a REQUEST, not a cap: the resolver checks it against each
    /// provider's served window, subtracts it from that provider's pool, and
    /// derives the compaction trigger from it. Zero is rejected rather than
    /// defaulted, because "a request for 0 tokens of context is not a request
    /// for a window" and a silent default here would let a 200k seat bind
    /// against a pool sized for 8k ones.
    /// </summary>
    [YamlMember(Alias = "context_tokens")] public int ContextTokens { get; set; }

    /// <summary>
    /// Ordered fallback endpoints (V1 harness edit). When the primary
    /// (this block's endpoint) is unreachable — connection refused, a
    /// whole-endpoint outage, a mid-run kill, or exhausted transport
    /// retries — <see cref="Vett.Llm.FailoverChatClient"/> advances to the
    /// next fallback and retries the same request there. Each entry is a
    /// full standalone LLM config (its own endpoint / model / api_key_env /
    /// provider), so failover can cross auth boundaries (e.g. OpenRouter →
    /// local aeon). Nested fallbacks are ignored (one level only). Null /
    /// empty = no failover, identical to pre-V1 behavior.
    /// </summary>
    [YamlMember(Alias = "fallbacks")] public List<LlmConfig>? Fallbacks { get; set; }
}

public sealed class TeamConfig
{
    [YamlMember(Alias = "leader")] public MemberConfig Leader { get; set; } = new();
    [YamlMember(Alias = "members")] public List<MemberConfig> Members { get; set; } = [];

    /// <summary>
    /// When true (typically chat profiles), completed assign_async tasks
    /// have their results AUTO-INJECTED into the leader's context at the
    /// next iteration boundary — so the leader doesn't have to remember
    /// to call check_tasks/wait_task to learn that work finished. The
    /// trade-off is a small token overhead per completion plus the
    /// leader needing prompt-level handling for "[task X completed: ...]"
    /// messages that arrive unsolicited.
    ///
    /// Default false — bench/automated profiles want explicit polling
    /// because there's no human to interject for, and the loop is
    /// simpler when results only arrive when explicitly waited on.
    /// </summary>
    [YamlMember(Alias = "auto_inject_async_results")] public bool AutoInjectAsyncResults { get; set; }

    /// <summary>
    /// Maximum tasks this leader may have IN FLIGHT at once via `assign_async`.
    /// **0 = unlimited, and that is the default.**
    ///
    /// ⛔ THE GAP THIS FILLS. Depth is capped (`MaxDispatchDepth`); width was
    /// capped by NOTHING. `assign_async` fires `Task.Run` unconditionally and
    /// its own tool description invites "multiple parallel calls", so the only
    /// ceiling was the model's restraint. Measured across the four manager-width
    /// arms on 2026-08-26: the widest single lead fanned out to **32**
    /// sub-workers, and at Pro w20 **11 of 20 leads** exhausted their own
    /// iteration budget while **no worker ever did** — the leads starved trying
    /// to manage the fan-out, not the workers doing the work.
    ///
    /// Over the ceiling, `assign_async` REFUSES with a message naming the limit
    /// and the count in flight, rather than throwing. Depth throws because it is
    /// a structural property decided before any work happens and nothing can
    /// rescue it; width is a moment-in-time property whose correct answer is
    /// "wait for a slot", which is something the leader can actually act on.
    ///
    /// A capped leader is also TOLD its budget on every successful assign — that
    /// is half the gap on its own: nothing ever told a manager what it had left.
    ///
    /// ⛔ REQUIRED ON EVERY `team:` NODE, INCLUDING NESTED ONES (Mark's call,
    /// 2026-08-27: "maybe we should require this" / "fail if its not there").
    /// `ValidateProfileForRun` throws when it is absent, so `vett run` / `vett
    /// chat` refuse to start rather than running uncapped.
    ///
    /// ⚠ THIS IS WHY THE TYPE IS `int?` AND NOT `int`. As a bare `int`, "the
    /// author omitted the key" and "the author wrote 0" both deserialise to 0 —
    /// so absence is UNDETECTABLE and no required-check can exist. Nullable is
    /// what makes the distinction representable at all. Do not "simplify" this
    /// back to `int`; that silently deletes the requirement.
    ///
    /// Explicit `0` remains legal and still means UNLIMITED — the requirement is
    /// that the author DECIDED, not that every team is capped. `validate` warns
    /// on an explicit 0 so a deliberate choice is still visible in the output.
    /// Measured 2026-08-27 before this landed: ZERO of the 12 team profiles set
    /// the key, so the ceiling shipped compiled, installed and dormant
    /// everywhere — `0` was not a conservative status quo protecting a subset,
    /// it protected nothing.
    /// </summary>
    [YamlMember(Alias = "max_concurrent_dispatches")] public int? MaxConcurrentDispatches { get; set; }

    /// <summary>
    /// When true, every member dispatch (assign_task / assign_async)
    /// runs inside its own per-task git worktree at
    /// ~/.vett/dispatches/&lt;panelId&gt;/&lt;taskId&gt;/. The leader
    /// reviews the captured diff and either accept_dispatch (apply to
    /// parent) or reject_dispatch (discard). Requires the working dir
    /// to be a git repo and the sandbox to support WithCwd (DirectBash
    /// only — RpcClient/Docker mode falls back gracefully).
    ///
    /// Default false — keeps existing single-worktree behavior so
    /// upgrading vett doesn't change behavior for profiles that haven't
    /// opted in.
    /// </summary>
    [YamlMember(Alias = "dispatch_worktree")] public bool DispatchWorktree { get; set; }

    /// <summary>
    /// Retention policy for dispatch worktrees once the dispatch
    /// finishes. One of:
    ///   - "auto-clean" — accept removes worktree; reject also removes.
    ///   - "keep-on-failure" (default) — accept removes; reject keeps
    ///     for inspection. Orphans (vett crashed mid-dispatch) kept.
    ///   - "keep-all" — never auto-remove; user cleans manually.
    /// Combined with <see cref="DispatchMaxAgeDays"/> for periodic auto-prune.
    /// </summary>
    [YamlMember(Alias = "dispatch_retention")] public string DispatchRetention { get; set; } = "keep-on-failure";

    /// <summary>
    /// Anything under ~/.vett/dispatches/ older than this is pruned on
    /// vett startup. Prevents unbounded disk growth even when
    /// retention is keep-on-failure or keep-all. Default 7 days.
    /// </summary>
    [YamlMember(Alias = "dispatch_max_age_days")] public int DispatchMaxAgeDays { get; set; } = 7;

    /// <summary>
    /// When false, suppress the leader-stuck-in-exploration nudge
    /// (AgentLoop's "call assign_async now" injected message). The
    /// nudge exists for BENCH runs where a leader that explores but
    /// never dispatches dies with members=[]; in a human-interactive
    /// chat there is no given task, so the nudge makes the model invent
    /// work (failure mode #26 in deepseek-resilience-failure-modes.md).
    /// Chat-facing profiles set leader_nudge: false; bench profiles
    /// keep the default true.
    /// </summary>
    [YamlMember(Alias = "leader_nudge")] public bool LeaderNudge { get; set; } = true;

    /// <summary>
    /// When true, the leader's `terminal` tool REFUSES file-writing commands
    /// (redirects, tee, sed -i, cp/mv/rm, git apply/checkout, ...). Reads,
    /// `dotnet build` and `dotnet test` are unaffected, so the verify gate
    /// still works. See <see cref="Vett.Tools.LeaderWriteGuard"/>.
    ///
    /// This makes "You do NOT write code yourself" a TOOL BOUNDARY instead of
    /// a request. Measured 2026-07-13: with file_editor removed from the
    /// leader but terminal left open, leaders still wrote files in 2 of 15
    /// runs — one hand-merged a dispatch worktree with `cp` (bypassing
    /// accept_dispatch, and the run PASSED because of it), one edited a file
    /// with `sed -i`.
    ///
    /// Default FALSE so existing profiles are byte-for-byte unaffected;
    /// opt in per profile.
    /// </summary>
    [YamlMember(Alias = "leader_terminal_readonly")] public bool LeaderTerminalReadonly { get; set; }

    /// <summary>
    /// BEST-OF-N (2026-07-13). When set, the leader gets the `assign_best_of`
    /// tool: run one unit of work N times as independent attempts (each with a
    /// different approach angle, each in its own worktree), then a JUDGE member
    /// compares the diffs and AT MOST ONE is applied — possibly NONE.
    ///
    /// The judge is a member, so in a hybrid profile it is Pro while the N
    /// candidates are local Flash: cheap parallel attempts, expensive single
    /// judgement. Requires dispatch_worktree: true (candidates need isolation).
    ///
    /// Null = tool not advertised; the leader dispatches once, as before.
    /// </summary>
    [YamlMember(Alias = "best_of")] public BestOfConfig? BestOf { get; set; }
}

/// <summary>Config for the leader's `assign_best_of` tool. See <see cref="Vett.Agent.BestOfTools"/>.</summary>
public sealed class BestOfConfig
{
    /// <summary>Default attempt count when the leader doesn't specify one. Clamped to 2..8.</summary>
    [YamlMember(Alias = "n")] public int N { get; set; } = 3;

    /// <summary>Member that picks the winner (or NONE). Must not be the candidate member.</summary>
    [YamlMember(Alias = "judge")] public string Judge { get; set; } = "reviewer";

    /// <summary>The member whose dispatches get the N-attempt treatment.</summary>
    [YamlMember(Alias = "member")] public string Member { get; set; } = "implementer";

    /// <summary>
    /// TRUE (default): assign_task(&lt;member&gt;, ...) is TRANSPARENTLY ROUTED
    /// through best-of-N. The leader does not choose — the profile does.
    ///
    /// This is deliberate. Offering `assign_best_of` as an extra tool and asking
    /// the leader to use it DOES NOT WORK: measured 2026-07-13 on the CRM build,
    /// a Pro leader with the tool advertised and the rule in its prompt called it
    /// ZERO times and used plain assign_task for every unit. Same lesson as
    /// file_editor and the terminal write-guard — a capability that is merely
    /// OFFERED gets ignored; a capability that is WIRED IN gets used.
    ///
    /// FALSE: the tool is advertised and the leader may call it explicitly.
    /// </summary>
    [YamlMember(Alias = "auto")] public bool Auto { get; set; } = true;
}

public sealed class MemberConfig
{
    [YamlMember(Alias = "name")] public string Name { get; set; } = "";
    [YamlMember(Alias = "system_prompt")] public string SystemPrompt { get; set; } = "";
    [YamlMember(Alias = "tools")] public List<string> Tools { get; set; } = [];
    [YamlMember(Alias = "middleware")] public List<string> Middleware { get; set; } = [];
    [YamlMember(Alias = "llm")] public LlmConfig? Llm { get; set; }
    [YamlMember(Alias = "max_iterations")] public int MaxIterations { get; set; }
    [YamlMember(Alias = "receives_from")] public List<string>? ReceivesFrom { get; set; }

    /// <summary>Per-member compaction tuning. Null → inherit the
    /// profile-level <see cref="Profile.Compaction"/>. Lets a
    /// context-heavy reviewer compact later than a lightweight member.</summary>
    [YamlMember(Alias = "compaction")] public CompactionConfig? Compaction { get; set; }

    /// <summary>
    /// NESTED TEAM (TIER 3, 2026-07-13). When set, this member is NOT a lone
    /// agent — it is itself a TEAM LEADER with its own members, and the parent
    /// leader's assign_task spins up a whole sub-team to handle the dispatch.
    ///
    /// This is what makes a THREE-TIER topology expressible:
    ///   top leader (Pro)  --assign_task(feature)-->
    ///     feature-lead (Pro, THIS member, has its own team:)  --assign_task-->
    ///       implementer (Flash)  ... sizes, dispatches, gates the build ...
    ///     feature-lead reports the finished feature back up
    ///   top leader accept_dispatch / reject_dispatch on the WHOLE feature
    ///
    /// The sub-team runs inside the parent dispatch's worktree, so the whole
    /// feature lands as ONE reviewable diff at the top — the top leader accepts
    /// or rejects the feature, not individual units.
    ///
    /// Null = ordinary member (a single agent loop). That is the default and
    /// every existing profile is unaffected.
    ///
    /// Recursion is bounded by TeamCoordinator.MaxDispatchDepth — read the
    /// constant rather than trusting a number quoted here; it was lowered from
    /// 5 to 2 on 2026-08-26 and its doc comment is the one place that
    /// explains what the units mean. NOTE: that
    /// guard existed but was DEAD CODE before this feature — `depth` was never
    /// incremented because nesting was impossible. It is live now.
    /// </summary>
    [YamlMember(Alias = "team")] public TeamConfig? Team { get; set; }
}

public sealed class Suite
{
    [YamlMember(Alias = "name")] public string Name { get; set; } = "";
    [YamlMember(Alias = "loader")] public LoaderConfig Loader { get; set; } = new();
    [YamlMember(Alias = "rendering")] public RenderingConfig Rendering { get; set; } = new();
    [YamlMember(Alias = "sandbox")] public SuiteSandboxConfig Sandbox { get; set; } = new();
}

public sealed class LoaderConfig
{
    [YamlMember(Alias = "type")] public string Type { get; set; } = "jsonl";
    [YamlMember(Alias = "path")] public string Path { get; set; } = "";
}

public sealed class RenderingConfig
{
    [YamlMember(Alias = "working_dir")] public string WorkingDir { get; set; } = "";
    [YamlMember(Alias = "docker_image_template")] public string? DockerImageTemplate { get; set; }
    // `fields:` REMOVED 2026-08-25 (defect-hunt F6, Mark's call). It bound
    // cleanly from YAML and was read by NOTHING -- the flagship
    // swe-bench-verified suite declared it, which is exactly why it looked
    // load-bearing. Loaders use .IgnoreUnmatchedProperties(), so any suite
    // still carrying `fields:` keeps parsing silently rather than erroring.
}

public sealed class SuiteSandboxConfig
{
    [YamlMember(Alias = "docker_image")] public string DockerImage { get; set; } = "";
}

// --- Loading ---

public static class Yaml
{
    private static readonly IDeserializer D = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static Profile LoadProfile(string path)
    {
        var p = D.Deserialize<Profile>(File.ReadAllText(path));
        p.SourcePath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path) ?? ".";
        if (!string.IsNullOrEmpty(p.SystemPromptFile) && string.IsNullOrEmpty(p.SystemPrompt))
        {
            var promptPath = Path.Combine(dir, p.SystemPromptFile);
            if (File.Exists(promptPath)) p.SystemPrompt = File.ReadAllText(promptPath);
        }
        if (!string.IsNullOrEmpty(p.UserTemplateFile) && string.IsNullOrEmpty(p.UserTemplate))
        {
            var tmplPath = Path.Combine(dir, p.UserTemplateFile);
            if (File.Exists(tmplPath)) p.UserTemplate = File.ReadAllText(tmplPath);
        }
        return p;
    }

    public static Profile ParseProfile(string yaml) => D.Deserialize<Profile>(yaml);

    /// <summary>
    /// Validate that a profile has everything needed for an actual run.
    /// Called by `vett run` / `vett chat` after profile load — NOT during
    /// parse — so tests and tools can deserialize partial profiles
    /// without tripping these checks. The user gets a clear error
    /// pointing at the YAML, not a generic 401 from the LLM endpoint
    /// five steps later.
    /// </summary>
    public static void ValidateProfileForRun(Profile p, string path, string? cliEndpoint = null, string? cliModel = null)
    {
        var errors = new List<string>();

        // Cloud providers need an api_key_env name. Local (vLLM/ollama)
        // does not — the endpoint is the auth boundary.
        var provider = (p.Llm.Provider ?? "").ToLowerInvariant();
        var needsApiKey = provider is "openai" or "anthropic" or "azure" or "google";
        if (needsApiKey && string.IsNullOrEmpty(p.Llm.ApiKeyEnv))
            errors.Add($"llm.api_key_env is required when llm.provider is '{provider}'. " +
                       "Add `api_key_env: OPENAI_API_KEY` (or whichever env var holds your key) under `llm:`.");

        if (!string.IsNullOrEmpty(p.Llm.ApiKeyEnv) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(p.Llm.ApiKeyEnv)))
            errors.Add($"Profile references env var '{p.Llm.ApiKeyEnv}' for the LLM API key, but it isn't set. " +
                       $"Run `export {p.Llm.ApiKeyEnv}=...` (or `set` on Windows) before invoking vett.");

        // A CAPABILITY PROFILE HAS NO ENDPOINT OR MODEL OF ITS OWN, BY DESIGN.
        // The catalogue supplies both at claim time, so demanding them here
        // would report an ERROR for a correct config -- the same defect as the
        // six CLI guards fixed alongside this (see
        // ChatClientFactory.FoldProfileValue). A false error from `validate` is
        // worse than a missing one: it sends the author to add exactly the
        // stale endpoint the capability layer exists to overrule.
        var bindsThroughCapacity = !string.IsNullOrWhiteSpace(p.Llm.Capability);

        if (!bindsThroughCapacity && provider == "local" && string.IsNullOrEmpty(p.Llm.Endpoint) && string.IsNullOrEmpty(cliEndpoint) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VETT_LLM_ENDPOINT")))
            errors.Add("llm.endpoint is required for provider 'local'. " +
                       "Set `endpoint: http://your-vllm-host:port/v1` in the profile, " +
                       "pass --endpoint, or export VETT_LLM_ENDPOINT.");

        if (!bindsThroughCapacity && string.IsNullOrEmpty(p.Llm.Model) && string.IsNullOrEmpty(cliModel) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VETT_LLM_MODEL")))
            errors.Add("llm.model is required (or pass --model, or set $VETT_LLM_MODEL).");

        // ...BUT IT MUST STILL SAY HOW BIG A WINDOW IT NEEDS. Exempting the two
        // checks above without adding this one would trade a false error for a
        // missing one: `capability: flash` with no context_tokens parses fine,
        // validates clean, and then throws at the first client construction --
        // after the run has started. The requirement is not new (CapacityBinding
        // rejects it); what is new is catching it before anything runs.
        if (bindsThroughCapacity && p.Llm.ContextTokens <= 0)
            errors.Add($"llm.capability is '{p.Llm.Capability}' but llm.context_tokens is " +
                       $"{p.Llm.ContextTokens}. A capability request has to say how large a " +
                       "window it needs -- set `context_tokens:` (e.g. 60000) under `llm:`.");

        // ⭐ EVERY `team:` NODE MUST DECLARE ITS OWN DISPATCH WIDTH.
        //
        // Mark's call 2026-08-27 ("fail if its not there"), and it is a THIRD
        // option to the two that were on the table — neither "leave the default
        // at 0" nor "pick a default number". Both of those are silent: the first
        // ships an unlimited fan-out to authors who never considered width, the
        // second retroactively changes profiles that were measured live without
        // one. Requiring the key is the only option where the author's intent is
        // always on the page.
        //
        // ⛔ WALKS NESTED NODES, and that is the whole point. Each `team:` block
        // gets its OWN TaskBoard (Coordinator builds one per level), so a capped
        // manager over 20 uncapped feature-leads is still an uncapped run — the
        // fan-out just happens one level down. Checking only `profile.Team` would
        // have covered 12 of the 74 team nodes in this repo and read as green.
        foreach (var (tpath, node) in TeamNodes(p.Team, "team"))
        {
            if (node.MaxConcurrentDispatches is null)
                errors.Add($"{tpath}.max_concurrent_dispatches is required but absent. "
                    + "Every team node must declare how many tasks its leader may have in "
                    + "flight at once, because an omitted ceiling means UNLIMITED and nothing "
                    + "at runtime reports it. Add `max_concurrent_dispatches: <n>` (a positive "
                    + "ceiling, e.g. one slot per member plus one), or `0` if you deliberately "
                    + "want this leader uncapped.");
            else if (node.MaxConcurrentDispatches < 0)
                errors.Add($"{tpath}.max_concurrent_dispatches is {node.MaxConcurrentDispatches}. "
                    + "A negative value reads as UNLIMITED at the dispatch site (the guard is "
                    + "`> 0`), which is the opposite of writing a ceiling down. Use a positive "
                    + "number, or 0 to mean unlimited deliberately.");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Profile {path} has {errors.Count} configuration error{(errors.Count > 1 ? "s" : "")}:\n  - " +
                string.Join("\n  - ", errors));
    }
    /// <summary>
    /// Every `team:` node reachable from <paramref name="team"/>, itself first,
    /// each paired with a dotted path naming it (`team`, `team/lead-a`, …) so a
    /// message can point at ONE node in a profile that declares 21 of them.
    ///
    /// ⚠ ONE WALKER, TWO CALLERS, DELIBERATELY. `ValidateProfileForRun` (fails a
    /// run) and `validate` (reports statically) must agree about which nodes
    /// exist, or one of them checks a set the other does not and the disagreement
    /// is invisible. They shared nothing before and the checks drifted apart.
    ///
    /// The depth guard is what makes a cyclic or absurdly deep profile terminate
    /// rather than hang; it is deliberately looser than
    /// <c>Coordinator.MaxDispatchDepth</c> so that an over-deep profile is
    /// REPORTED by these checks rather than silently truncated out of them.
    /// </summary>
    public static IEnumerable<(string Path, TeamConfig Team)> TeamNodes(
        TeamConfig? team, string path = "team", int depth = 0)
    {
        if (team is null || depth > 8) yield break;
        yield return (path, team);
        foreach (var m in team.Members ?? [])
        {
            if (m?.Team is null) continue;
            var who = string.IsNullOrWhiteSpace(m.Name) ? "?" : m.Name;
            foreach (var t in TeamNodes(m.Team, $"{path}/{who}", depth + 1)) yield return t;
        }
    }

    public static Suite LoadSuite(string path) => D.Deserialize<Suite>(File.ReadAllText(path));

    /// <summary>
    /// Resolve a profile/suite by name (or absolute path) using a fixed
    /// precedence:
    ///   1. Explicit file path (passed verbatim)
    ///   2. Workspace: <cwd>/<subdir>/<name>.yaml         (project override)
    ///   3. User:      ~/.vett/<subdir>/<name>.yaml        (per-user override)
    ///   4. Bundled:   <install-dir>/<subdir>/<name>.yaml  (shipped defaults)
    /// </summary>
    public static T? Resolve<T>(string nameOrPath, string subdir, Func<string, T> loader) where T : class
    {
        var path = ResolvePath(nameOrPath, subdir);
        return path is null ? null : loader(path);
    }

    /// <summary>
    /// The FILE <see cref="Resolve{T}"/> would load, or null if nothing
    /// matched. Same precedence, same <c>File.Exists</c> probes, in the
    /// same order — <see cref="Resolve{T}"/> is implemented on top of this
    /// so the two can never drift.
    ///
    /// Exists because a run that records only the profile NAME cannot
    /// prove which RULER it used: the same name resolves to three
    /// different files depending on the working directory
    /// (see <see cref="ResolveSearchDirs"/>), and the copies are not
    /// required to agree. Callers that write run artifacts should record
    /// <c>Path.GetFullPath(...)</c> of this.
    /// </summary>
    public static string? ResolvePath(string nameOrPath, string subdir)
    {
        if (File.Exists(nameOrPath)) return nameOrPath;
        foreach (var dir in ResolveSearchDirs(subdir))
        {
            var p = Path.Combine(dir, nameOrPath + ".yaml");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// <see cref="Resolve{T}"/> plus the file it came from. Loading twice
    /// (once for the value, once for the path) would re-probe the search
    /// dirs and could pick a DIFFERENT file if one appeared in between, so
    /// callers that need both must use this rather than two calls.
    /// </summary>
    public static (T? Value, string? Path) ResolveWithPath<T>(string nameOrPath, string subdir, Func<string, T> loader) where T : class
    {
        var path = ResolvePath(nameOrPath, subdir);
        return path is null ? (null, null) : (loader(path), path);
    }

    /// <summary>
    /// Directories searched (in precedence order) for profiles, suites,
    /// etc. Exposed so the `profiles` command can enumerate without
    /// duplicating the resolution policy.
    /// </summary>
    public static IEnumerable<string> ResolveSearchDirs(string subdir)
    {
        yield return Path.Combine(Directory.GetCurrentDirectory(), subdir);
        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? "";
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".vett", subdir);
        yield return Path.Combine(AppContext.BaseDirectory, subdir);
    }
}
