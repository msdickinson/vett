# Vett Tool Interface (Go)

This document defines how tools are declared, registered, and dispatched in Vett v2. Every built-in tool (`terminal`, `file_editor`, `think`, `finish`, `task_tracker`, `create_task`, etc.) implements this interface, and any new tool must follow it exactly.

## The tool type

```go
// pkg/tool/tool.go

package tool

import "context"

// Tool is the unit of agent action. Every tool registered in Vett must be a
// pointer to a Tool value with all required fields filled in.
type Tool struct {
    // Key is the unique identifier used by the tool registry. Must match
    // the Go variable name convention and be globally unique across all
    // registered tools.
    Key string

    // Name is what the LLM sees in the tool schema. Usually the same as
    // Key, but a profile can override the wire name (see
    // profile-schema.md for the key/name distinction).
    Name string

    // Description is the tool's natural-language description shown to the
    // LLM. Must match openhands-sdk's exact text for tools in the openhands
    // profile.
    Description string

    // Params declares the tool's parameters. Order matters for stable
    // schema generation (map iteration order in Go is not deterministic,
    // so we use a slice of KeyedParam instead of a plain map).
    Params []KeyedParam

    // Execute is the callback invoked when the LLM calls this tool.
    Execute ExecuteFn

    // SupportsAsync declares whether this tool can be called
    // asynchronously via the task registry. If true and the LLM passes
    // `async: true` in the args, the dispatcher spawns Execute in a
    // goroutine, registers it as a task, and returns a task ID
    // immediately. The LLM can then poll with check_task/wait_for_task/
    // cancel_task — the same tools that work on sub-agent dispatches.
    //
    // Short-lived tools (think, finish, file_view) should leave this
    // false. Long-lived tools (terminal, custom build/test runners,
    // async dispatch primitives) may opt in.
    //
    // The openhands profile does NOT use async tool calls — its tool
    // schemas don't expose the `async` flag. This field is infrastructure
    // for other profiles that want long-horizon tool invocations.
    SupportsAsync bool
}

type KeyedParam struct {
    Key  string
    Spec ParamSpec
}

type ParamSpec struct {
    Type        string         // one of: "string", "integer", "number", "boolean", "object", "array"
    Description string
    Required    bool
    Enum        []string       // for string params with a fixed choice set
    Items       *ParamSpec     // for arrays: the element type
    Properties  []KeyedParam   // for objects: nested fields
}

// ExecuteFn runs the tool. The context carries cancellation; args is the
// decoded JSON arguments from the LLM; env exposes the sandbox + the
// active session + other capabilities.
type ExecuteFn func(ctx context.Context, args map[string]any, env Env) (string, error)
```

## The Env interface

Tools receive an `Env` struct that exposes everything the tool needs:

```go
type Env struct {
    // Sandbox is the Docker container + sidecar. Tools that need to run
    // bash or edit files go through here.
    Sandbox sandbox.Sandbox

    // SessionID is the sidecar session this tool call should run in.
    // Always non-empty. For the openhands profile, it's always the
    // default session (e.g. "agent"). For team profiles, it's the
    // member's session.
    SessionID string

    // Tasks is the task registry for dispatch tools. Only non-nil for
    // profiles that have dispatch tools in their tool list.
    Tasks *tasks.Registry

    // EventBus is where the tool can publish observability events
    // (tool_call_chunk, sub_agent_start, etc.). Non-blocking.
    EventBus eventbus.Publisher

    // CallID is the unique ID for this specific tool invocation.
    // Used to correlate events in the trace.
    CallID string

    // Depth is the reentrant-loop depth. 0 for the top-level agent;
    // increments for each nested sub-agent dispatch. Used for
    // enforcing max-dispatch-depth limits.
    Depth int
}
```

## The Sandbox interface (as seen by tools)

Tools only see this subset of the sandbox:

```go
package sandbox

type Sandbox interface {
    // Exec runs a bash command in a specific session. Raw passthrough
    // applies here — the command string reaches bash byte-for-byte.
    // ONLY called by the terminal tool. Other tools use InitExec below
    // if they need to run bash-authored commands.
    Exec(ctx context.Context, sessionID, command string, timeout time.Duration, stream bool) (ExecResult, error)

    // InitExec is for tool-authored bash commands (not LLM-authored).
    // Allowed to build command strings with templates, paths, etc.
    // Never called from the terminal tool. Used for: patch extraction,
    // conda activation, git config, etc. The separation prevents
    // LLM commands from ever going through a path that builds command
    // strings.
    InitExec(ctx context.Context, sessionID, command string, timeout time.Duration) (ExecResult, error)

    // FileView, FileCreate, FileStrReplace, FileInsert, FileUndo,
    // FileExists, ListDir all map 1:1 to sidecar ops. The sandbox
    // delegates to the SidecarClient.
    FileView(ctx context.Context, sessionID, path string, viewRange *[2]int) (FileViewResult, error)
    FileCreate(ctx context.Context, sessionID, path, fileText string) error
    FileStrReplace(ctx context.Context, sessionID, path, oldStr, newStr string) (string, error)
    FileInsert(ctx context.Context, sessionID, path string, insertLine int, newStr string) (string, error)
    FileUndo(ctx context.Context, sessionID, path string) (string, error)
    FileExists(ctx context.Context, sessionID, path string) (FileExistsResult, error)
    ListDir(ctx context.Context, sessionID, path string, maxDepth int) ([]string, error)

    // Session management.
    SessionCreate(ctx context.Context, name, cwd string) error
    SessionDestroy(ctx context.Context, name string) error
}

type ExecResult struct {
    Stdout   string
    ExitCode int
    Cwd      string
    TimedOut bool
}

// FileViewResult, FileExistsResult: trivial structs matching sidecar-protocol.md
```

## Registration

Tools register themselves at init time via a package-level function:

```go
// tools/terminal.go
package tools

import (
    "context"
    "github.com/msdickinson/vett/sidecar/pkg/tool"
)

func init() {
    tool.Register(&tool.Tool{
        Key:  "terminal",
        Name: "terminal",
        Description: "Execute a bash command in the terminal within a persistent shell session. ...",
        Params: []tool.KeyedParam{
            {Key: "command", Spec: tool.ParamSpec{
                Type: "string",
                Description: "The bash command to execute. ...",
                Required: true,
            }},
            {Key: "is_input", Spec: tool.ParamSpec{
                Type: "boolean",
                Description: "If True, the command is an input to the running process. ...",
                Required: false,
            }},
            {Key: "timeout", Spec: tool.ParamSpec{
                Type: "number",
                Description: "Optional. Sets a maximum time limit...",
                Required: false,
            }},
            {Key: "reset", Spec: tool.ParamSpec{
                Type: "boolean",
                Description: "If True, reset the terminal...",
                Required: false,
            }},
            {Key: "security_risk", Spec: tool.ParamSpec{
                Type: "string",
                Description: "Security risk levels for actions.\n\n...",
                Required: true,
                Enum: []string{"UNKNOWN", "LOW", "MEDIUM", "HIGH"},
            }},
            {Key: "summary", Spec: tool.ParamSpec{
                Type: "string",
                Description: "A concise summary...",
                Required: false,
            }},
        },
        Execute: executeTerminal,
    })
}

func executeTerminal(ctx context.Context, args map[string]any, env tool.Env) (string, error) {
    command, _ := args["command"].(string)
    // ignore is_input and reset per openhands-reference-spec §13 gap 1
    timeout := 60
    if t, ok := args["timeout"].(float64); ok {
        timeout = int(t)
    }

    result, err := env.Sandbox.Exec(ctx, env.SessionID, command, time.Duration(timeout)*time.Second, false)
    if err != nil {
        return "", err
    }

    // Apply the openhands_bash envelope here — at the tool layer.
    // The sidecar returns raw stdout. The tool wraps it.
    envelope := result.Stdout
    if !strings.HasSuffix(envelope, "\n") && envelope != "" {
        envelope += "\n"
    }
    if result.TimedOut {
        envelope = "Error: Command timed out.\n" + envelope
    }
    envelope += fmt.Sprintf("[Current working directory: %s]\n", result.Cwd)
    envelope += fmt.Sprintf("[Command finished with exit code %d]", result.ExitCode)

    return envelope, nil
}
```

## Error conventions

Tools return `(string, error)`. Three cases:

### Success: `(result, nil)`
The tool ran, produced output. The result string becomes the tool-role observation in the conversation. The LLM sees it on the next turn.

### Tool error: `(result, nil)` where result IS the error message
If the tool itself runs fine but what it was asked to do failed — like `file_str_replace` where `old_str` isn't found — the tool returns the error description as the result, with `nil` error. The LLM sees it as an observation and decides what to do. This matches openhands' behavior.

Example:
```go
if occurrences == 0 {
    msg := fmt.Sprintf("No replacement was performed, old_str `%s` did not appear verbatim in %s.", oldStr, path)
    return msg, nil  // not an error — the tool worked, the edit didn't
}
```

### Infrastructure error: `("", err)`
If the tool itself breaks — sidecar RPC failed, container died, context cancelled — return an error. The agent loop decides whether to retry or abort. These are rare and indicate a bug or infrastructure failure.

Example:
```go
result, err := env.Sandbox.Exec(ctx, env.SessionID, command, timeout, false)
if err != nil {
    return "", fmt.Errorf("sandbox exec failed: %w", err)  // infrastructure error
}
```

**Rule:** tools never panic on normal LLM input. A tool that panics is a bug.

## Parameter decoding

`args map[string]any` comes from JSON unmarshaling, so:
- Strings are `string`
- Numbers are `float64` (JSON has no int type at the transport level)
- Booleans are `bool`
- Arrays are `[]any`
- Objects are `map[string]any`
- Null is `nil`

Tools should use type assertions with `ok` checks, never direct casts:

```go
command, ok := args["command"].(string)
if !ok {
    return "Error: 'command' must be a string", nil  // tool error, not infra error
}
```

A helper package `pkg/tool/args/` provides safe accessors:

```go
command := args.String("command", "")           // returns "" if missing
isInput := args.Bool("is_input", false)         // returns false if missing
timeout := args.Int("timeout", 60)              // returns 60 if missing
viewRange := args.IntArray("view_range", nil)   // returns nil if missing
```

## Tool registry

The registry is a package-level singleton in `pkg/tool`:

```go
var registry = map[string]*Tool{}
var registryMu sync.Mutex

func Register(t *Tool) {
    registryMu.Lock()
    defer registryMu.Unlock()
    if t.Key == "" {
        panic("tool: Key is required")
    }
    if _, exists := registry[t.Key]; exists {
        panic("tool: duplicate key: " + t.Key)
    }
    if t.Execute == nil {
        panic("tool: Execute is required for " + t.Key)
    }
    registry[t.Key] = t
}

func Lookup(key string) *Tool { ... }
func All() map[string]*Tool { ... }
```

Keys must be globally unique across the whole binary. Duplicates panic at init time — caught immediately on startup, never in production.

## Per-profile tool namespaces

A profile's tool list maps keys (implementation) to names (wire-visible). See [profile-schema.md](profile-schema.md#tool-references) for the details. The tool registry is keyed by `Key`; the profile's active tool set is keyed by `Name` (what the LLM sees). One profile can have `{key: openhands_file_editor, name: file_editor}` while another has `{key: swe_file_editor, name: file_editor}` — same wire name, different implementations.

**The key/name split happens at profile-load time.** The tool registry is profile-agnostic: it holds all registered tools keyed by Key. When a profile loads, it builds a `map[string]*Tool` from wire name → looked-up tool. The agent loop dispatches by wire name against the profile's map, never the global registry.

## Async tool invocation

If a tool declares `SupportsAsync: true` and its registered Params include an `async` boolean field, the dispatcher handles the async case before the tool's Execute runs:

1. LLM emits tool call with `args["async"] == true`
2. Dispatcher checks `tool.SupportsAsync`. If false, returns error "this tool does not support async dispatch" as observation.
3. If true, the dispatcher spawns a goroutine:
   ```go
   taskID := env.Tasks.SpawnTool(tool, args, env)
   return fmt.Sprintf(`{"task_id":"%s"}`, taskID), nil
   ```
4. The goroutine calls `tool.Execute(ctx, args, env)` (with `async` removed from args so the tool doesn't see it) and stores the result in the registry
5. LLM uses `check_task`, `wait_for_task`, `cancel_task`, `list_tasks` to interact with the running task

The tool's Execute function does NOT need to know about async mode. The goroutine + registry wrapping is transparent. A tool that works synchronously automatically works asynchronously as soon as `SupportsAsync: true` is set and the profile's schema exposes the `async` param.

**Cancellation:** the task registry passes a cancellable context to the goroutine. If the task is cancelled (via `cancel_task` or agent loop exit), the context is cancelled and the tool's Execute should return promptly. Tools that respect context cancellation get clean async behavior for free.

**Result storage:** the task's result is the same `(string, error)` that Execute returned. When `check_task` or `wait_for_task` is called after completion, the result string becomes the task's observation. The LLM sees it just like it would have seen a sync tool call result.

**Task IDs for tools:** use a prefix like `tool-` to distinguish from sub-agent tasks (`agent-`). Not required for correctness but helps trace readability.

## Built-in tools that Vett provides

These are always registered and available to any profile that lists them:

| Key | Purpose |
|---|---|
| `terminal` | bash execution via sidecar (openhands schema) |
| `file_editor` | file ops via sidecar (openhands schema) |
| `think` | no-op thought logger |
| `finish` | terminal signal (emits `__VETT_SUBMIT__` marker) |
| `task_tracker` | task planning state |
| `create_task` | synchronous sub-agent dispatch |
| `create_tasks_parallel` | sync parallel dispatch of N sub-agents |
| `dispatch_task_async` | async dispatch, returns task ID |
| `check_task` | non-blocking status query |
| `wait_for_task` | blocking wait with timeout |
| `list_tasks` | enumerate running tasks |
| `cancel_task` | kill a running task |

Dispatch tools (`create_task` and friends) are special: they don't use `env.Sandbox` — they use `env.Tasks` — and their Execute functions may invoke a reentrant agent loop internally. See `internal/agent/dispatch.go` for the implementation.

## Testing tools

Every tool should have:

1. **A unit test** that calls Execute with mocked `Env` and canned args, asserts the result
2. **A schema test** that marshals the tool's Params to JSON and diffs against a golden file (for openhands tools, this is the mirror test)
3. **An integration test** that runs Execute against a real sandbox+sidecar in a throwaway container

Example:

```go
func TestTerminalExecute_Success(t *testing.T) {
    env := tool.Env{Sandbox: &fakeSandbox{
        execResult: sandbox.ExecResult{Stdout: "hello\n", ExitCode: 0, Cwd: "/tmp"},
    }, SessionID: "test"}
    result, err := executeTerminal(ctx, map[string]any{"command": "echo hello"}, env)
    require.NoError(t, err)
    require.Contains(t, result, "hello\n")
    require.Contains(t, result, "[Current working directory: /tmp]")
    require.Contains(t, result, "[Command finished with exit code 0]")
}
```

## Summary

Tools are:
- A Go struct with a Key, Name, Description, Params slice, Execute callback
- Registered at init time by package-level `tool.Register()` calls
- Dispatched by the agent loop using the active profile's key/name map
- Given an `Env` that carries sandbox access, session, event bus, task registry, and depth
- Expected to return observations as strings and infrastructure errors as Go errors

Anything that doesn't fit this shape isn't a tool — it's something else (middleware, sandbox op, profile setting).
