# VETT v3 C# — Specification

## Extracted from 3 prior implementations + 2 audit passes

### Core Principles
1. **No unnecessary abstractions.** No interfaces with one implementation. No registries. No adapter layers.
2. **Constructor injection.** Tools, middleware, sandbox — all passed as arguments, never global state.
3. **Single callback for events.** `Action<Event>` not a pub/sub bus.
4. **2 projects.** `Vett` (everything) + `Vett.Tests`. One DLL, one exe, one test assembly.
5. **Go sidecar.** The sidecar binary is Go. C# talks to it over JSON-RPC stdin/stdout.
6. **Subprocess protocol for plugins.** Tools and middleware in any language speak JSON on stdin/stdout.

### Project Structure
```
VETT-CS3/
├── Vett.slnx
├── src/Vett/
│   ├── Program.cs              — CLI entry point, all commands
│   ├── Agent/
│   │   ├── AgentLoop.cs        — Run + RunInteractive in one class
│   │   └── Team.cs             — TaskBoard + LeaderTools + Coordinator
│   ├── Config/
│   │   ├── Profile.cs          — Profile + Suite + all config types (records)
│   │   └── SpellCheck.cs       — YAML key misspelling detection
│   ├── Llm/
│   │   ├── LlmClient.cs       — HTTP client + cache/replay
│   │   └── Types.cs            — Message, ToolCall, Request, Response (records)
│   ├── Sandbox/
│   │   ├── RpcClient.cs        — JSON-RPC over stdin/stdout pipes
│   │   ├── DockerSandbox.cs    — Docker container lifecycle
│   │   └── LocalSandbox.cs     — Native subprocess sidecar
│   ├── Sidecar/
│   │   ├── SidecarServer.cs    — RPC server (runs inside container or locally)
│   │   ├── BashSession.cs      — Persistent bash child process
│   │   └── FileEditor.cs       — File ops (view, create, str_replace, insert, undo)
│   ├── Plugin/
│   │   ├── PluginManager.cs    — Scan, build, warm-up, schema gen
│   │   └── Hooks.cs            — Pre/post processing hooks
│   ├── Runner/
│   │   └── Runner.cs           — Benchmark orchestration + SWE-bench export
│   └── Tools/
│       └── BuiltinTools.cs     — terminal, file_editor, think, finish (one file)
├── tests/Vett.Tests/
│   └── *.cs                    — All tests
├── profiles/
│   ├── openhands.yaml
│   ├── coding.yaml
│   └── team-example.yaml
├── suites/
│   └── test-canned.yaml
├── schemas/
│   ├── terminal.json
│   ├── file_editor.json
│   ├── think.json
│   ├── finish.json
│   └── task_tracker.json
└── defaults/                   — Dropped by `vett install defaults`
    ├── tools/
    │   ├── bash.yaml           — YAML-only tool example
    │   └── pytest.yaml         — YAML-only tool example
    ├── profiles/
    │   ├── openhands.yaml
    │   └── coding.yaml
    └── suites/
        └── test-canned.yaml
```

### Key Design Decisions

**AgentLoop** — One class, two modes:
```csharp
// Benchmark mode: system prompt + user message, run to completion
Task<AgentResult> RunAsync(config, systemPrompt, userMessage, ct)

// Interactive mode: wait for user input between text-only responses
Task<AgentResult> RunInteractiveAsync(config, systemPrompt, userInput, callbacks, ct)
```
Both use the same iteration logic internally. No separate RunOneIteration extraction — 
the loop body is inline, ~80 lines, readable top to bottom.

**AgentConfig** replaces the Loop struct — everything the loop needs in one record:
```csharp
record AgentConfig(
    LlmClient Client,
    string Model,
    RpcClient Sandbox,
    string SessionId,
    Dictionary<string, ToolFn> Tools,
    List<JsonElement> ToolSchemas,
    List<MiddlewareFn> Middlewares,
    double Temperature,
    double TopP,
    int MaxIterations,
    Action<Event>? OnEvent
);
```

**Tools are functions, not objects:**
```csharp
// Not this:
public sealed class Tool { public required ExecuteFn Execute; ... }

// This:
delegate Task<string> ToolFn(Dictionary<string, object?> args, RpcClient sandbox, string sessionId, CancellationToken ct);
```
Registration is just adding to a dictionary. No registry class. No global state.

**Middleware are functions too:**
```csharp
delegate Task MiddlewareFn(AgentState state, CancellationToken ct);
```

**Events are a callback, not a bus:**
```csharp
// The loop calls this. The caller decides what to do with it.
Action<Event>? OnEvent
```

**RpcClient IS the sandbox.** No ISandbox interface, no SandboxAdapter wrapper.
Tools call `sandbox.BashExecAsync()` directly on the RpcClient.

**LlmClient includes cache.** One class handles both live calls and replay.
No separate CachedLlmClient wrapper.

### CLI Commands
```
vett version              — Print version
vett run                  — Run benchmark suite
vett chat                 — Interactive coding session
vett build                — Build workspace plugins
vett doctor               — Check SDK availability
vett call <tool>          — Test a single tool
vett replay               — Replay from cached LLM responses
vett analyze <results>    — LLM-driven failure triage
vett init                 — Scaffold workspace
vett install defaults     — Drop bundled profiles/tools/suites
vett profiles             — List available profiles
```

### Plugin System (7 languages + YAML)
- Python, Go, C#, TypeScript, Rust, Ruby, YAML
- Package detection: requirements.txt, package.json, go.mod, *.csproj, Cargo.toml, Gemfile
- Build caching: SHA256 hash, skip unchanged
- Long-lived subprocess: warm at startup, <1ms per call
- Comment-based schema: `#vett:tool name "description"` / `//vett:param name type "desc"`

### Team Orchestration (core, not plugin)
- Leader + N members defined in profile YAML
- 6 built-in leader tools: assign_task, assign_async, check_task, check_tasks, wait_task, declare_done
- Each member gets own agent loop in own Task
- Thread-safe TaskBoard with TaskCompletionSource for async wait
- LLM settings merge: member overrides profile defaults
- Sequential mode (receives_from) for no-leader pipelines

### Bundled Defaults (dropped by `vett install defaults`)
- `profiles/openhands.yaml` — faithful openhands-sdk mirror
- `profiles/coding.yaml` — interactive coding assistant
- `profiles/team-example.yaml` — example team with leader + 2 members
- `suites/test-canned.yaml` — 2-instance test fixture
- `tools/bash.yaml` — YAML-only bash wrapper
- `tools/pytest.yaml` — YAML-only pytest runner
- `schemas/*.json` — OpenAI function schemas for built-in tools
