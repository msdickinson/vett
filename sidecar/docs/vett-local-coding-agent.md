# Vett Local Coding Agent — Design Doc

## What this is

A local interactive coding assistant powered by Vett's existing agent loop,
tools, and middleware. The user opens a project in VSCode, starts a chat
session, and the agent reads/edits files and runs commands in the local
repo while the user watches and can jump in at any point.

Think "Claude Code / Cursor / Aider" but built on Vett's infrastructure
so it shares the same tools, profiles, middleware, and LLM client that
power the benchmark harness. Improvements to one path benefit the other.

## What already exists (reuse, don't rebuild)

All of this is built and tested in the Vett v2 Go codebase at `VETT/`:

### Agent loop (`internal/agent/loop.go`)
- Iterates: build request → LLM call → parse tool calls → dispatch → 
  middleware → check stop conditions → repeat
- Handles tool call errors, malformed LLM responses, context cancellation
- Session recovery on terminal failures (generic + timeout)
- Event bus publishing at every stage for real-time observability
- Reentrant dispatch infrastructure for future sub-agent patterns

### Tools (`tools/*.go`, registered via `pkg/tool`)
- **terminal** — persistent bash session, raw passthrough, openhands
  envelope wrapping. The command the LLM writes reaches bash byte-for-byte.
- **file_editor** — view (cat -n), create, str_replace (exact match),
  insert (after line N), undo_edit. Native Go file ops, no shell escaping.
- **think** — no-op thought logger, returns acknowledgement
- **finish** — emits `__VETT_SUBMIT__` marker, submit_detector middleware
  catches it and terminates the loop
- **task_tracker** — plan/track task state (the LLM's scratchpad)

### Sidecar (`internal/sidecar/`, `cmd/vett-sidecar/`)
- Persistent bash child with sentinel-framing passthrough
- Native file editor ops (str_replace, view, create, insert, undo)
- Session management (create, destroy, list — named sessions)
- Streaming + buffered output modes
- Newline-JSON RPC protocol between host and sidecar over stdin/stdout

### Middleware (`middleware/*.go`, registered via `pkg/middleware`)
- **output_truncation** — clips observations > 16K chars with notice
- **submit_detector** — catches finish tool's marker, stops the loop
- **stuck_detector** — 4 scenarios from openhands-sdk: action-observation
  loop, action-error loop, monologue, alternating A-B-A-B. Terminates
  on first trip (openhands mirror). A future `vett-enhanced` profile
  could add nudge-before-terminate.

### LLM client (`internal/llm/`)
- OpenAI-compatible chat completions (vLLM, litellm, any endpoint)
- Wire types with exact key-order control for mirror parity
- 900s per-request timeout, configurable retries
- Returns raw outgoing body for trace capture

### Event bus (`internal/eventbus/`)
- Publish/subscribe with per-subscriber lossy/non-lossy policy
- Filtered subscriptions (by event type, by instance_id)
- Every iteration, LLM call, tool call, middleware event published
- Trace writer subscriber dumps JSONL to disk

### Profile system (`internal/config/`)
- YAML-based: system prompt, tools list, middleware chain, LLM settings
- `system_prompt_file` / `user_template_file` for external prompt files
- Embedded profiles (ship with binary) + workspace profiles (user overrides)
- Profile search: workspace `./profiles/` → embedded core

## What needs to be built

### 1. Local sandbox (no Docker)

**Current state:** the agent loop dispatches tools through `tool.Sandbox`
interface, which is implemented by `sandbox.Client` (talks to a sidecar
inside a Docker container over `docker exec -i`).

**What to build:** a `LocalSandbox` that implements `tool.Sandbox` by
running the sidecar binary directly on the host machine (no Docker, no
container). The sidecar is already a standalone Go binary that speaks
newline-JSON on stdin/stdout — it just needs to be launched as a local
subprocess instead of inside a container.

```go
// internal/sandbox/local.go
type LocalSandbox struct {
    Client    *Client       // same RPC client, different transport
    sidecar   *exec.Cmd     // local subprocess, not docker exec
    sessionID string
    workDir   string        // user's project directory
}

func StartLocal(ctx context.Context, workDir string) (*LocalSandbox, error) {
    // 1. Find the sidecar binary (embedded in the vett binary,
    //    extract to ~/.vett/bin/ same as today)
    // 2. exec.Command(sidecarPath) with stdin/stdout pipes
    // 3. NewClient(stdout, stdin) — same RPC client as Docker path
    // 4. Hello handshake
    // 5. SessionCreate("local", workDir, nil)
    // 6. Return &LocalSandbox{Client: client, ...}
}
```

The `tool.Sandbox` interface is already met by `*sandbox.Client`, and
`LocalSandbox` embeds `*Client`, so all tools work unchanged. No tool
code needs modification.

**Safety:** the sidecar runs with the user's permissions on their local
filesystem. No isolation. The LLM can `rm -rf /` through the terminal
tool if it wants. For local dev use this is acceptable (same as any
coding assistant). For untrusted workloads, use the Docker sandbox.

**Effort:** ~half a day. Most of it is the subprocess lifecycle +
extracting the sidecar binary to a temp location on non-Linux hosts
(the sidecar is a Linux binary; on macOS/Windows it would need a
native build or run through WSL).

**Cross-platform note:** the sidecar uses `/bin/bash` which doesn't
exist on Windows natively. Options:
- Use Git Bash's bash.exe (available since Git for Windows is installed)
- Use WSL2's bash
- Build a Windows-native sidecar that uses cmd.exe or PowerShell
  (bigger effort, different tool semantics)
Recommend: Git Bash for Phase 1, WSL2 as fallback.

### 2. Interactive agent loop mode

**Current state:** `agent.Loop.Run()` iterates from system prompt +
user message to a terminal condition (finish, stuck, max_iterations,
timeout). No way for the user to inject messages mid-loop.

**What to build:** a `RunInteractive()` method (or a flag on `Run`)
that pauses after each assistant response and waits for user input
on a channel. If the user sends a message, it's appended to the
conversation and the loop continues. If the user doesn't respond
within N seconds (configurable, default: auto-continue), the loop
continues on its own.

```go
type InteractiveConfig struct {
    // UserInput is a channel the UI sends user messages on.
    // The loop reads from it between iterations.
    UserInput <-chan string
    
    // AutoContinueAfter is how long the loop waits for user input
    // before continuing on its own. 0 = wait forever (pure chat mode).
    // 5*time.Second = agent works autonomously, user can jump in.
    AutoContinueAfter time.Duration
}

func (l *Loop) RunInteractive(ctx context.Context, 
    systemPrompt string, ic InteractiveConfig) (*Result, error) {
    // Same loop as Run(), but between iterations:
    // 1. Publish the assistant's response to the event bus
    // 2. select {
    //      case msg := <-ic.UserInput:
    //          append Message{Role: "user", Text: msg} to state
    //      case <-time.After(ic.AutoContinueAfter):
    //          // no user input, agent continues autonomously
    //      case <-ctx.Done():
    //          return
    //    }
}
```

**Effort:** ~1 day. The loop structure is already clean; adding the
select between iterations is straightforward. The tricky part is
making sure middleware (stuck detector especially) handles injected
user messages correctly — the reset-on-user rule already exists
(clears the detection window on a user message), so this should
work naturally.

### 3. VSCode extension

**Current state:** `ticketforge-vscode/` is an existing VSCode extension
with:
- Chat webview (React, signals-based state management)
- Dispatch cards (collapsible tool call display)
- Streaming display for agent output
- Agent reasoning toggle
- Retry button
- Structured error cards

**What to build:** fork or adapt the TicketForge extension to talk to
a local `vett` process instead of the TicketForge API. The extension
becomes a thin UI over a `vett chat` subprocess.

Communication options:
- **Subprocess stdio** — extension spawns `vett chat --profile coding
  --dir <workspace>`, reads/writes JSON events on stdout/stdin. Simplest.
  The event bus already emits structured events; the extension just needs
  to parse them.
- **WebSocket** — `vett chat --serve :8765` runs an HTTP/WS server,
  extension connects as a client. More standard for extensions but more
  code. Could use the same event types.
- **LSP-style** — language server protocol with custom methods. Most
  integrated with VSCode but most boilerplate.

Recommend: subprocess stdio for Phase 1 (matches how Claude Code works),
WebSocket for Phase 2 if multiple clients need to connect (e.g. a web
dashboard watching the same session).

**Key extension components to reuse from ticketforge-vscode:**
- `webview/components/ChatView.tsx` — the main chat UI
- `webview/components/DispatchCard.tsx` — tool call display
- `webview/state/signals.ts` — state management
- Message rendering, markdown support, code blocks

**Key changes:**
- Replace TicketForge API calls with stdio reads/writes to `vett chat`
- Add a "working directory" concept (the open VSCode workspace)
- Add a "profile" selector (dropdown: coding, debugging, refactoring)
- Add a "jump in" input box that sends user messages to the agent
  mid-loop via the InteractiveConfig.UserInput channel

**Effort:** ~1-2 days if forking ticketforge-vscode. ~3-5 days if
building from scratch. Recommend forking.

### 4. Coding profile (`profiles/coding.yaml`)

```yaml
name: coding
description: |
  Local interactive coding assistant. Works in the user's project
  directory. Reads files, runs commands, edits code, runs tests.
  The user can watch and jump in at any time.

sandbox:
  # No Docker — uses LocalSandbox
  type: local

llm:
  temperature: 0.7    # lower than openhands' 1.0 for more focused edits
  top_p: 0.95

system_prompt: |
  You are a coding assistant working in a local project directory.
  You can read files, edit them, run commands, and run tests.
  
  The user is watching your work in real time and may jump in with
  corrections or new directions at any point. When they do, adapt
  immediately.
  
  Be concise. Show your work through tool calls, not long
  explanations. When you're done, call finish with a brief summary.

tools:
  - terminal
  - file_editor
  - task_tracker
  - finish
  - think

middleware:
  - output_truncation
  - submit_detector
  - stuck_detector

max_iterations: 50
timeout_minutes: 30
```

**Effort:** ~1 hour. It's just a YAML file with a different system prompt.
The tools and middleware are identical to openhands — the local coding
agent benefits from every improvement made to the benchmark harness.

### 5. CLI entry point

```bash
# Start an interactive coding session in the current directory
vett chat --profile coding

# Start with a specific task
vett chat --profile coding --message "add unit tests for the auth module"

# Use a different LLM
vett chat --profile coding --endpoint http://localhost:8000/v1 --model qwen3-coder-next

# Auto-continue mode (agent works, you jump in when needed)
vett chat --profile coding --auto-continue 10s
```

The `vett chat` command:
1. Resolves the profile (workspace → embedded)
2. Starts a LocalSandbox rooted at CWD
3. Enters the interactive loop
4. Reads user input from stdin (CLI mode) or from the extension (stdio mode)
5. Publishes events to the bus for trace capture

**Effort:** ~half a day. Same pattern as `vett run` but with
`RunInteractive` instead of `Run`, and `LocalSandbox` instead of
`DockerSandbox`.

## Architecture diagram

```
┌─────────────────────────────────────┐
│  VSCode Extension (or terminal)     │
│  - Chat UI                          │
│  - Tool call display                │
│  - "Jump in" input                  │
│  - Profile selector                 │
├─────────────────────────────────────┤
│  vett chat (Go binary)              │
│  ┌──────────────────────────────┐   │
│  │ Interactive Agent Loop       │   │
│  │ (same code as benchmark)     │   │
│  │ + user-input channel         │   │
│  │ + auto-continue timer        │   │
│  ├──────────────────────────────┤   │
│  │ Profile: coding.yaml         │   │
│  │ Tools: terminal, file_editor │   │
│  │ MW: truncation, stuck, submit│   │
│  ├──────────────────────────────┤   │
│  │ LocalSandbox                 │   │
│  │ (sidecar as local subprocess)│   │
│  │ - bash child in project dir  │   │
│  │ - native file editor ops     │   │
│  └──────────────────────────────┘   │
├─────────────────────────────────────┤
│  LLM Endpoint                      │
│  (GPU-2 / local / cloud)            │
└─────────────────────────────────────┘
```

## What this shares with the benchmark harness

| Component | Benchmark path | Local coding path |
|---|---|---|
| Agent loop | `agent.Loop.Run` | `agent.Loop.RunInteractive` |
| Tools | Same 5 tools | Same 5 tools |
| Middleware | Same chain | Same chain |
| LLM client | Same client | Same client |
| Sidecar | In Docker container | Local subprocess |
| Event bus | Trace files | Extension UI |
| Profile | openhands.yaml | coding.yaml |
| Sandbox | DockerSandbox | LocalSandbox |

**Key principle:** any tool improvement, middleware fix, or LLM client
change benefits BOTH paths. The benchmark harness and the coding
assistant are the same agent running in different environments.

## Effort estimate

| Task | Days |
|---|---|
| LocalSandbox (no Docker) | 0.5 |
| Interactive loop mode | 1 |
| VSCode extension (fork ticketforge) | 1.5 |
| Coding profile + system prompt tuning | 0.5 |
| CLI `vett chat` command | 0.5 |
| **Total** | **4** |

## Open questions for the implementing AI

1. **Windows bash:** Git Bash vs WSL2 vs native PowerShell? Git Bash is
   the path of least resistance (already installed on the dev machine).
   The sidecar needs `/bin/bash` — on Windows, map this to Git Bash's
   `bash.exe` at `C:\Program Files\Git\bin\bash.exe`.

2. **Sidecar cross-compile:** the current sidecar is linux/amd64 only
   (it runs in Linux containers). For local mode on Windows, either
   cross-compile to windows/amd64 and use Git Bash, or run the Linux
   binary through WSL2. Recommend: build a windows/amd64 sidecar
   variant that uses Git Bash.

3. **File editor paths:** the sidecar uses Unix-style absolute paths
   (`/testbed/foo.py`). On Windows, paths are `C:\Users\dev\...`.
   The sidecar's file editor ops use `os.ReadFile` etc. which handle
   both, but the LLM might emit Unix-style paths. Handle with a
   path-normalizer in the local sandbox.

4. **Extension packaging:** should the extension be published to the
   VSCode marketplace, or side-loaded like ticketforge-vscode? For
   internal use, side-load. For the public Vett story, marketplace.

5. **Concurrent sessions:** can two VSCode windows run `vett chat`
   against different projects simultaneously? Yes — each gets its own
   sidecar subprocess, its own agent loop, its own bash child. No
   shared state. Just needs different session names.
