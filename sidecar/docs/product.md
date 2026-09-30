# VETT — Product Overview

## Vett in one sentence

**Vett is a pluggable benchmark harness** — you describe an agent (profile + tools + middleware + suite), and Vett runs it against any benchmark and scores it. The `openhands` profile is just ONE profile among many it can run.

## The 30-second version

Think of Vett like a tiny Kubernetes for AI coding agents: it's a runtime that takes a **declarative config** and executes it. The config says "use these tools, this LLM, this sandbox, this middleware stack, this benchmark suite." Vett does the rest — container lifecycle, LLM calls, tool dispatch, patch extraction, scoring.

Vett is **not** an agent. Vett *runs* agents, measures them, and compares them against each other on the same benchmarks.

## The layer cake

```
┌─────────────────────────────────────────────┐
│  Suite          swe-bench, tools-bench, ... │  WHAT: set of tasks to solve
├─────────────────────────────────────────────┤
│  Profile        openhands.yaml              │  HOW: agent recipe
├─────────────────────────────────────────────┤
│  Middleware     stuck-detector, truncation  │  SHAPE: message pipeline
├─────────────────────────────────────────────┤
│  Tools          terminal, file_editor, ...  │  ACTIONS: what the LLM can do
├─────────────────────────────────────────────┤
│  Sandbox        DockerSandbox + Go sidecar  │  WHERE: isolated execution
├─────────────────────────────────────────────┤
│  LLM Client     llm package                 │  WIRE: bytes to/from vLLM
└─────────────────────────────────────────────┘
```

Each layer is independently replaceable. You bring your own tool (Go file in `tools/` or YAML file for template tools), your own middleware (Go file in `middleware/`), your own profile (`profiles/foo.yaml`), your own suite. The sandbox and LLM client are infrastructure you usually don't touch.

## Each layer, briefly

### Suite (top)
A suite is **a dataset + a scorer**. Example: `swe-bench-verified` loads 500 Python bug-fix tickets from HuggingFace and scores them via the SWE-bench Docker test harness. A suite's only job is: produce instances, accept patches, return pass/fail. Suites know nothing about profiles or tools.

### Profile
A YAML file that ties everything together for one "agent persona." A profile says:
- What system prompt and user template to use
- Which tools the LLM can invoke
- Which middleware runs in what order
- LLM settings (model, temperature, top_p, max iterations)
- Sandbox configuration (run as root, working directory, etc.)

Different profiles are how Vett runs different agent designs on the same benchmark. The `openhands` profile mirrors openhands-sdk exactly. A future `swe-agent` profile would mirror mini-swe-agent. A future `vett-enhanced` profile might test Vett's own ideas measured against the baseline.

### Middleware
A **message pipeline**. Each middleware gets the current conversation state and can mutate it: truncate long tool outputs, compact old turns, detect stuck loops, wrap observations, gate submission. Middleware chains are what turn a raw LLM-in-a-loop into a functioning agent. Without middleware, even a smart model spirals on the first long output or gets stuck in the first loop.

### Tools
The **actions the LLM can invoke**. Each tool is a self-contained Go file in `tools/` (or a YAML command template for thin "just run this bash command" tools). Tools register themselves into a global registry at startup. The LLM sees them as OpenAI function schemas; Vett dispatches invocations to the Go implementation via the sandbox.

**Vett v2 is Go + YAML only.** No polyglot runtime loading. If you need Python or JavaScript logic, wrap it in a Go tool that shells out — but inside a Go file you control.

### Sandbox
**Isolated execution environment.** One Docker container per instance, with a persistent **Go sidecar binary** copied in at startup. The sidecar provides bash execution and file-editor ops over newline-JSON RPC on stdin/stdout. Every tool shares a persistent shell state — cwd, env, conda, running processes all survive across tool calls within a session.

**Sessions** are a first-class concept in the sandbox. A single-agent profile uses one session. A teams profile can create many sessions in the same container, each with its own bash child and its own worktree. Sessions isolate state between concurrent agents.

### LLM Client
The **lowest layer**: takes a list of messages + tools and sends the actual HTTP request to vLLM (or any OpenAI-compatible endpoint). All wire-format concerns live here — message shape, tool schema, sampling params. This is the code that must match reference harnesses byte-for-byte for mirror discipline to hold.

## The mirror discipline

Vett's `openhands` profile exists for one reason: **to faithfully reproduce openhands-sdk's behavior** on the same inputs. Same wire format, same tool schemas, same middleware chain, same termination rules. If openhands fails on a particular instance, Vett fails the same way. **No "improvements" live in the openhands profile.**

Why? Because Vett's value is **comparison across profiles on the same benchmark**. For comparison to be meaningful, at least one profile has to be a known reference — and a reference that silently differs from its original is useless. The openhands profile is the reference anchor. Any improvement Vett wants to experiment with goes in a *different* profile (e.g., `vett-swe-bench`) where the delta against the anchor is directly measurable.

## Multi-agent coordination

Vett's agent loop is **reentrant**. A running agent can dispatch sub-agents (members) which are themselves full agent loops running in isolated sessions. This is how the teams feature works, and it's baked into Phase 1 even though the `openhands` profile doesn't use it — the infrastructure is there so future profiles can flip it on without a retrofit.

### Dispatch modes

An agent (usually a team leader) has three ways to invoke other agents:

1. **Synchronous, one member.** Leader blocks until the member finishes. Simplest case.
2. **Synchronous, N members in parallel.** Leader blocks until ALL members finish. Used for best-of-N exploration.
3. **Asynchronous, fire-and-poll.** Leader gets a task ID immediately, continues its own work, and checks on the member later via poll/wait/list/cancel tools. Enables long-horizon coordination where the leader doesn't have to stall.

### The task registry

Each agent loop has its own **task registry** — a map of task IDs to running tasks. Tasks can be sub-agents OR long-running tool invocations (see below). The registry lives for the lifetime of the agent loop and is torn down when the agent finishes.

```
type Task struct {
    ID        string
    Kind      TaskKind      // "sub_agent" or "tool"
    Role      string        // member role for sub_agent, tool name for tool
    State     TaskState     // Running, Completed, Failed, Cancelled
    Started   time.Time
    Completed time.Time
    Result    *TaskResult   // populated when State != Running
    Error     error
    cancel    context.CancelFunc
}
```

When the agent loop exits (via `finish`, stuck, max iterations, error), the registry's `Close()` cancels any still-running tasks, waits a grace period, force-kills stragglers, destroys any sessions the tasks created, and logs the cleanup. No orphans.

### Dispatch tools (built-in)

These tools are provided by Vett itself and are available to any profile that lists them in its tool schema:

| Tool | What it does |
|---|---|
| `create_task` | Spawn one sub-agent, wait for it, return result. Sync. |
| `create_tasks_parallel` | Spawn N sub-agents in parallel, wait for all, return array. Sync. |
| `dispatch_task_async` | Spawn a sub-agent in the background, return task ID immediately. |
| `check_task` | Query a task's status without blocking. |
| `wait_for_task` | Block until a task finishes or timeout. |
| `list_tasks` | Enumerate all tasks in this agent's registry. |
| `cancel_task` | Kill a running task. |

The `openhands` profile does NOT include any of these (openhands-sdk is single-agent, mirror discipline). They exist for team profiles and other reentrant patterns.

### Async tool calls

The task registry also powers **asynchronous tool invocations**. Any tool that declares `SupportsAsync: true` in its registration can be called with `async: true` in its args. Instead of blocking, the call spawns the tool's Execute function in a goroutine and returns a task ID. The agent uses the same `check_task` / `wait_for_task` / `cancel_task` tools to interact with it.

This enables:
- Long-running builds and tests that the agent shouldn't wait on
- Parallel exploration (dispatch 3 greps in parallel, wait for all)
- Any tool call where "I'll check back later" is valuable

The `openhands` profile does NOT use async tool calls — its tool schemas don't expose the `async` flag and all tools are synchronous. This matches openhands-sdk exactly. Other profiles can opt in.

### Max dispatch depth

Reentrant dispatch is capped to prevent runaway nesting. The cap defaults to 2 (leader can dispatch a member; member can dispatch ONE level deeper; third-level dispatch fails). Configurable per profile via `team.leader.max_dispatch_depth`. This matches TicketForge's behavior and prevents pathological LLM loops.

### Sessions and isolation

Each dispatched sub-agent gets its own sidecar session (its own bash child with its own cwd and env). Team profiles that want isolated file trees get a git worktree per member, mapped to a session. This way members can run `pytest` concurrently without colliding on working directories.

See [profile-schema.md](profile-schema.md#teams-block-for-multi-agent-profiles) for how team profiles declare leaders and members in YAML.

## How the openhands profile gets assembled for a single SWE-bench instance

Walking the layers top-down:

1. **Suite (`swe-bench-verified`)** hands the runner an instance like `astropy__astropy-13033`: problem statement, base commit, Docker image name, test commands.

2. **Profile (`openhands.yaml`)** says: use the OpenHands V1 system prompt (verbatim 12,098 chars), this user-message template, these 5 tools in this exact order, this middleware chain, `temperature: 1.0`, `top_p: 0.95`, `max_iterations: 100`.

3. **Sandbox** boots `swebench/sweb.eval.x86_64.astropy_1776_astropy-13033` as a container, copies in the sidecar, creates the default session with `cwd=/testbed`, activates the `testbed` conda env via `PATH` export, seeds git's `safe.directory`. Container is now ready with a persistent bash session.

4. **Tools** load from `tools/`: `terminal`, `file_editor`, `task_tracker`, `finish`, `think`. Each is a Go file that registered itself at init time. Schemas get compiled to OpenAI function format. Execute callbacks are wired to the sandbox's session.

5. **Agent loop** starts: system prompt + user message → LLM call → get back tool calls → **middleware pipeline runs** (output truncation, observation formatting, submit detection, stuck detection) → dispatch tool calls via sandbox → add results as `role:tool` messages → loop.

6. **Finish**: either the model calls `finish`, a middleware sets `StopLoop=true`, or we hit `max_iterations`. Either way, `runner.GeneratePatch` runs `git add -A && git diff --cached` in the container via the sidecar.

7. **Suite scorer** feeds the resulting patch to SWE-bench's test harness in a fresh Docker container. Returns resolved/not-resolved.

## The key mental model

**Everything the model sees is produced by a layer below it, and every layer is independently replaceable.** If Vett's openhands profile gives different results than real openhands-sdk, the bug is in exactly one layer — we just have to find which. That's why the test strategy is layered: unit tests catch logic bugs in individual layers; integration tests catch bugs between layers; mirror tests catch wire-format drift; end-to-end tests catch behavioral drift across a full conversation.

## The sidecar (deep dive)

### Why it exists

An obvious alternative is to run each tool via `docker exec <container> bash -c "..."` from the host. That works, but:

- `cd /testbed` in call #1 is forgotten by call #2 (every exec is a fresh process)
- `source activate testbed` doesn't persist
- Environment variables set by one call don't affect the next
- Background processes started by one call are orphaned
- There's a ~200ms `docker exec` startup tax on every tool call, and SWE-bench runs make ~100 tool calls per instance
- **Every command passed through `bash -c` has to be shell-escaped by the host**, which is the single biggest source of subtle bugs we've hit historically (quotes, dollars, backslashes, newlines)

OpenHands solves this by running an agent-server inside the container. Vett's sidecar is the minimal version: one binary, one bash child, one RPC protocol.

### The shape

```
Host (Go)                                    Container (any Linux image)
┌──────────────────┐                         ┌─────────────────────────┐
│  vett            │                         │  vett-sidecar           │
│                  │    docker exec -i       │                         │
│  sandbox.Docker  │ ──── stdin  ────────►   │  stdin:  JSON requests  │
│  sidecar.Client  │ ◄─── stdout ──────────  │  stdout: JSON responses │
│                  │                         │                         │
│                  │                         │  ┌───────────────────┐  │
│                  │                         │  │ Sessions          │  │
│                  │                         │  │ (named bash       │  │
│                  │                         │  │  children)        │  │
│                  │                         │  └───────────────────┘  │
│                  │                         │                         │
│                  │                         │  ┌───────────────────┐  │
│                  │                         │  │ FileEditor ops    │  │
│                  │                         │  │ (native Go, no    │  │
│                  │                         │  │  shell escaping)  │  │
│                  │                         │  └───────────────────┘  │
└──────────────────┘                         └─────────────────────────┘
```

Four moving parts:
1. **`vett-sidecar` binary** — Go, static linux/amd64 ELF (~2 MB), zero runtime dependencies. Drops into any Linux container.
2. **`sandbox.Docker`** — on container startup, does `docker cp` + `docker exec -i /tmp/vett-sidecar` to launch the sidecar and keep stdin/stdout open.
3. **`sidecar.Client`** — host-side wrapper around the subprocess stdin/stdout. Serializes requests, reads responses, correlates by request ID.
4. **Persistent bash children** — one per session. Each started with `/bin/bash --noprofile --norc` (NON-interactive). An earlier draft said `-i`, but interactive bash enables readline on the stdin pipe, which wraps the sentinel line at the default 80-column width and echoes input into output. Phase 1b end-to-end testing caught this; non-interactive bash reads stdin line-by-line and emits only command output. Every `bash_exec` request writes command text into its session's bash stdin.

### The protocol

Newline-delimited JSON, one request per line, one response per line. No framing header, no length prefix.

```
→ {"id":"a1","op":"bash_exec","args":{"session_id":"agent","command":"ls","timeout_seconds":60}}
← {"id":"a1","ok":true,"result":{"stdout":"file1.py\nfile2.py","exit_code":0,"cwd":"/testbed","timed_out":false}}

→ {"id":"a2","op":"file_view","args":{"session_id":"agent","path":"/testbed/file1.py"}}
← {"id":"a2","ok":true,"result":{"content":"Here's the result of running `cat -n`...","is_directory":false}}

→ {"id":"a3","op":"file_str_replace","args":{"session_id":"agent","path":"/testbed/file1.py","old_str":"foo","new_str":"bar"}}
← {"id":"a3","ok":true,"result":{"content":"The file /testbed/file1.py has been edited..."}}
```

**Every op carries a `session_id`.** The default profile always uses `"agent"` (or whatever we pick). Teams profiles use multiple named sessions. There is no session-less code path.

Ops:
- `session_create(name, cwd)` — spawn a new bash child with cwd set
- `session_destroy(name)` — kill a session's bash child
- `bash_exec` — run a command in a session
- `file_view` / `file_create` / `file_str_replace` / `file_insert` / `file_undo` — native file ops
- `file_exists`, `list_dir` — utility ops
- `grep_files`, `git_diff`, `git_status` — native ops planned for correctness-critical paths that would otherwise risk shell escaping

Request IDs matter because the client is async: multiple tools can be in flight concurrently, and responses come back in completion order.

### The raw passthrough invariant

**The LLM's bash commands reach bash byte-for-byte identical.** When the LLM calls `terminal(command="pytest -v")`, the string `pytest -v` lands in bash's stdin unchanged. No `cd` prepend, no quote wrapping, no escaping, no substitution. Working directory is set at session creation time, once — never per-command.

The sidecar adds ONE thing around each command: a sentinel line AFTER the command that prints the exit code and current working directory so the sidecar knows when output is done and where bash ended up. That sentinel is a separate statement, not a modification of the user's command.

```go
marker := "__VETT_END_" + randomHex(8) + "__"
script := "{ " + command + "; } 2>&1\n" +
          "printf '\\n" + marker + " exit=%s cwd=%s\\n' \"$?\" \"$PWD\"\n"
```

The `{ ... ; }` is bash grouping for the `2>&1` redirect. **The user's command inside the braces is unchanged.** The sentinel `printf` is a separate statement that runs after.

This invariant is enforced by a test (`TestBashPassthroughIsBitExact`) that captures the literal bytes entering bash's stdin and asserts they equal the input for a table of tricky commands (with quotes, dollars, backslashes, newlines, empty strings, etc.). Any future refactor that accidentally adds escaping breaks the test.

### Streaming

The sidecar supports two modes for `bash_exec`:
- **Buffered** (default) — reads all command output, returns once at the end. Matches openhands-sdk's behavior. The `openhands` profile always uses buffered.
- **Streaming** — emits chunks as output arrives, then a final completion message. Used by infrastructure features (trace output, live dev mode, teams observability) but **not exposed to the LLM**. The LLM still sees one buffered result at the end.

A test (`TestStreamingAndBufferedProduceSameBytes`) verifies that running the same command in both modes concatenates to byte-identical output. Streaming is an observability feature, not an agent feature — the LLM's view of the world is unchanged.

### Timeouts and interrupts

Each `bash_exec` carries a `timeout_seconds`. If a command runs past its timeout:
1. Sidecar kills the foreground job by sending SIGINT to the session's bash process group
2. Grace period of 500ms for the marker to flush
3. Returns captured output with `timed_out: true` and `exit_code: -1`

Critical for SWE-bench because the model sometimes launches `python -m pytest` on a broken install and it hangs forever. Without timeouts, one bad test command would wedge the agent.

### File editor as a native op (not a bash shell-out)

File edits don't go through bash. `file_str_replace` is a native Go op: read file, find-and-replace in memory, atomic rename. **Zero shell escaping at any point.** The `old_str` can contain any byte — single quotes, backslashes, null bytes, weird Unicode — and it Just Works because the data travels as a JSON string in the RPC, not as a shell argument.

Semantics match openhands' `openhands-tools/file_editor/editor.py` byte-for-byte:
- `expandtabs` with tabstop=8 (Python `str.expandtabs` parity)
- `<response clipped>` truncation at 16KB with the exact suffix text
- Multi-match `str_replace` returns a line-numbered error
- Per-file undo history (not per-session initially, TBD for teams)
- Directory view uses `find -maxdepth 2 -not -path "*/\.*"`

### Lifecycle

```
sandbox.CreateDocker(image)
  │
  ├─ docker run -d --rm --entrypoint /bin/bash <image> -c "sleep infinity"
  ├─ docker cp vett-sidecar into container
  ├─ chmod +x /tmp/vett-sidecar
  ├─ docker exec -i /tmp/vett-sidecar   (launches it, keeps stdin/stdout attached)
  ├─ sidecar.Client wraps the subprocess
  ├─ session_create(name="agent", cwd="/testbed")
  ├─ InitExec: conda activate testbed (via PATH export)
  ├─ InitExec: git config safe.directory
  └─ return sandbox ready for use

...agent loop runs, hundreds of bash_exec / file_* calls all routed
   through the single sidecar process, all carrying session_id="agent"...

sandbox.Close
  ├─ sidecar.Client.Close  (close stdin, sidecar exits, all sessions die)
  └─ docker kill <container>  (container dies with --rm, auto-cleanup)
```

One container, one sidecar, N sessions (usually 1 for single-agent profiles, many for teams). Parallel instances (different shards) each get their own container + sidecar — no cross-instance leakage.

## The event bus

Vett has an internal **event bus** that every layer publishes to and multiple subscribers read from. It's the mechanism that makes trace files, live mode, streaming viewers, and external UIs possible without tangling up the agent loop.

### Publishers

- **LLM client** emits `llm_request`, `llm_chunk` (when streaming), `llm_response`
- **Agent loop** emits `iteration_start`, `iteration_end`, `tool_call_start`, `tool_call_end`
- **Sub-agent dispatcher** emits `sub_agent_start`, `sub_agent_end`
- **Middleware** emits `middleware_event` when it does something noteworthy
- **Sandbox / sidecar** emits `tool_call_chunk` (when streaming bash output)
- **Runner** emits `run_start`, `run_end`, `instance_start`, `instance_end`, `patch_generated`, `error`

### Subscribers (default)

- **Trace writer** — writes JSONL to `instances/<id>.trace.jsonl` and `run.trace.jsonl`. Non-lossy. Runs in its own goroutine with a bounded channel; if the channel fills up, the publisher blocks briefly (backpressure intentional — the trace file is the source of truth).
- **Progress reporter** — prints `iter 12/100, 14k tokens` to stderr. Lossy (drops events on overflow; we don't care if a progress line is missed).
- **Stuck detector integration** — not a subscriber per se, but the stuck detector middleware publishes middleware events back to the bus.

### Subscribers (opt-in via CLI flags)

- **Live stdout** — `vett run --live` prints streaming tokens and command output to stdout as they arrive. Lossy on overflow.
- **JSON event stream** — `vett run --events` emits the raw event stream as JSONL on stdout, one event per line, so external programs can pipe from Vett. Lossy on overflow.
- **VSCode extension** — when Vett is invoked from the VSCode extension, it uses `--events` to stream to the extension's UI.

### Principles

1. **Vett has no built-in UI.** UIs are external programs that consume the event stream. The extension, a TUI dashboard, a web dashboard — they're all separate. Vett's job is to produce events; someone else's job is to render them.
2. **Publishers don't block on subscribers.** Each subscriber runs in its own goroutine with its own channel. A slow subscriber doesn't stall the agent loop.
3. **Backpressure is subscriber-chosen.** Trace writer is non-lossy (block the publisher if needed). Progress reporter is lossy (drop events on overflow). External streams declare their policy at subscribe time.
4. **Event types are forward-compatible.** Adding new event types doesn't break existing subscribers (they ignore unknown events). Adding new fields to existing events is fine as long as old fields are preserved.

See [trace-format.md](trace-format.md) for the canonical event schemas.

## Where to zoom in next

If you want more depth on a specific layer, the docs are organized by topic:
- [CLI specification](cli-spec.md) — commands, flags, examples
- [Test strategy](test-strategy.md) — the four test tiers + preflight
- [OpenHands reference spec](openhands-reference-spec.md) — canonical openhands-sdk behavior Vett's `openhands` profile must mirror
- [OpenHands reproduction retrospective](openhands-reproduction.md) — what the earlier C# Vett effort taught us (historical)

## How this turned out

This document was written for a planned full rewrite in Go. It went the other way: the .NET 10 host in `src/Vett/` stayed and is the supported CLI, and Go kept the job it is best at, the small static `vett-sidecar` binary that runs inside each sandbox container. The design here (profiles, suites, tools, middleware, the sidecar protocol, the trace format) carried over; the Go host CLI in `sidecar/cmd/vett` is the prototype from that period and is not the supported entry point. See [docs/DESIGN.md](../../docs/DESIGN.md) for the current design.
