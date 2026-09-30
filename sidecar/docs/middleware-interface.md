# Vett Middleware Interface (Go)

Middleware is a **message pipeline** that runs between LLM turns. Each middleware in a profile's chain can inspect and mutate the conversation state before the next LLM call. This document defines the interface, lifecycle, and conventions for middleware in Vett v2.

## Core types

```go
// pkg/middleware/middleware.go

package middleware

import "context"

// Middleware is one stage in the agent loop's processing pipeline.
// Implementations mutate AgentState in place.
type Middleware interface {
    // Name returns a stable identifier used in trace events and
    // middleware_event logs. Must match what's in the profile YAML.
    Name() string

    // Process is called exactly once per agent iteration, after tool
    // dispatch has completed and before the next LLM call. If an earlier
    // middleware sets StopLoop=true, Process is still called on all
    // remaining middlewares (they can inspect the stop but cannot
    // un-set it).
    Process(ctx context.Context, state *AgentState) error
}
```

## AgentState

The shared state that flows through the middleware chain:

```go
type AgentState struct {
    // Messages is the full conversation so far. Middleware can read,
    // truncate, compact, rewrite, or annotate messages.
    Messages []Message

    // LastToolCalls are the tool calls emitted by the LLM in the
    // most recent response. Empty on the first iteration.
    LastToolCalls []ToolCall

    // LastObservations are the tool results produced by the tool
    // dispatcher for LastToolCalls. Populated before middleware runs.
    // Empty on the first iteration.
    LastObservations []Observation

    // Iteration is the current iteration counter, 1-indexed.
    Iteration int

    // MaxIterations is the profile's cap. When Iteration reaches this,
    // the loop will exit even if StopLoop is false.
    MaxIterations int

    // StopLoop, if true at the end of the middleware chain, terminates
    // the agent loop. Middleware sets this to signal "we're done" —
    // e.g., SubmitDetector sets it when finish is called.
    StopLoop bool

    // StopReason is a human-readable string describing why StopLoop
    // was set. Written into trace events and the instance_end event.
    StopReason string

    // Profile is the profile this agent is running under. Middleware
    // can read profile config (e.g. max_iterations, middleware settings)
    // but should not mutate it.
    Profile *config.Profile

    // Session is the sandbox session this agent runs in. Middleware
    // can read session state but should rarely need to touch it.
    Session *sandbox.SessionHandle

    // EventBus is the observability channel. Middleware publishes
    // middleware_event records here for the trace.
    EventBus eventbus.Publisher

    // Depth is the reentrant loop depth. 0 = top-level agent.
    Depth int
}
```

## Message and Observation types

```go
type Message struct {
    Role        string      // "system" | "user" | "assistant" | "tool"
    Content     Content     // see ContentPart for structured content
    ToolCalls   []ToolCall  // only for assistant messages
    ToolCallID  string      // only for tool messages — matches a prior assistant tool call
    ToolName    string      // only for tool messages — the name that was called
}

type Content struct {
    // Content can be a plain string (tool-role messages) or a list of
    // structured parts (system/user/assistant messages in V1 format).
    // Exactly one of Text or Parts is set.
    Text  string          // for tool-role messages
    Parts []ContentPart   // for non-tool-role messages
}

type ContentPart struct {
    Text string
    Type string  // always "text" for now, forward-compat for images etc.
}

type ToolCall struct {
    ID        string
    Name      string          // tool wire name
    Arguments map[string]any  // decoded JSON
}

type Observation struct {
    ToolCallID string
    ToolName   string
    Result     string
    Success    bool
    Error      error  // nil unless the tool returned an infra error
}
```

## Lifecycle

The agent loop's iteration looks like this:

```
1. Record iteration_start event
2. Build LLM request from state.Messages
3. Call LLM, parse response
4. Record llm_response event
5. Dispatch each tool call via the tool registry
6. Append assistant message + tool-role messages to state.Messages
7. Populate state.LastToolCalls and state.LastObservations
8. Run middleware chain in order, each middleware.Process(ctx, state)
9. If state.StopLoop is true, exit loop with state.StopReason
10. If state.Iteration >= state.MaxIterations, exit loop with "max_iterations"
11. Otherwise, increment state.Iteration, go to step 1
```

Middleware runs **after** tool dispatch, **before** the next LLM call. This is the only place they get called. Each middleware runs exactly once per iteration. The chain order is defined in the profile YAML and is preserved at runtime.

**Middleware cannot abort tool dispatch** — dispatch has already happened by the time middleware runs. If you want to gate what tools can run, you do it by modifying LastToolCalls BEFORE dispatch (via a pre-dispatch hook, which we don't currently support) or by controlling the tool list in the profile.

**Middleware cannot issue additional LLM calls within one iteration.** An iteration is one LLM call + one middleware chain run. If you want to make extra LLM calls (like the LLM summarizing condenser), that's a separate concern handled inside the middleware's own logic, not a chain primitive. See `LlmSummarizingCondenser` in `openhands-reference-spec.md` §8 for an example.

## Stop semantics

Any middleware can set `state.StopLoop = true` to terminate the loop. Conventions:

- **SubmitDetector** sets StopLoop when it sees the finish tool marker (`__VETT_SUBMIT__`) in the last observation. StopReason = "finish_tool".
- **StuckDetector** sets StopLoop when one of its 5 scenarios fires. StopReason = "action_observation_loop_4x" or similar.
- **Timeout tracking** (implemented by the loop itself, not middleware) triggers "step_timeout".
- **MaxIterations** (implemented by the loop) triggers "max_iterations".

If multiple middlewares set StopLoop in the same iteration, the FIRST one to set it wins — its StopReason is the final one. Subsequent middlewares running in the same iteration should check `state.StopLoop` before their own logic and skip if already true (unless they specifically want to augment the stop reason).

**Middleware cannot un-set StopLoop.** Once true, it stays true.

## Emitting events

When a middleware does something noteworthy, it publishes a `middleware_event` to the event bus:

```go
env.EventBus.Publish(eventbus.Event{
    Type: eventbus.MiddlewareEvent,
    Middleware: m.Name(),
    Action:     "terminate",
    Reason:     "action_observation_loop_4x",
    Iteration:  state.Iteration,
    Details:    map[string]any{"scenario": 1, "tool_name": "file_editor"},
})
```

These events are written to the trace and shown in `vett run --live` mode. Middleware should publish events for any observable action, not just terminations.

## Example: StuckDetector

```go
// middleware/stuck_detector.go

package middleware

import (
    "context"
    "github.com/msdickinson/vett/sidecar/pkg/middleware"
)

type StuckDetector struct {
    ActionObservationThreshold int  // default 4
    ActionErrorThreshold       int  // default 3
    MonologueThreshold         int  // default 3
    AlternatingThreshold       int  // default 6
}

func (s *StuckDetector) Name() string { return "stuck_detector" }

func (s *StuckDetector) Process(ctx context.Context, state *middleware.AgentState) error {
    if state.StopLoop {
        return nil  // already stopping, don't bother
    }

    // Scenario 1: same action + observation repeated N times
    if s.detectActionObservationLoop(state) {
        state.StopLoop = true
        state.StopReason = "action_observation_loop"
        state.EventBus.Publish(eventbus.Event{
            Type: eventbus.MiddlewareEvent,
            Middleware: "stuck_detector",
            Action: "terminate",
            Reason: "action_observation_loop",
            Details: map[string]any{"scenario": 1, "threshold": s.ActionObservationThreshold},
        })
        return nil
    }

    // Scenarios 2-5 similar...

    return nil
}
```

## Example: SubmitDetector

```go
type SubmitDetector struct{}

func (s *SubmitDetector) Name() string { return "submit_detector" }

func (s *SubmitDetector) Process(ctx context.Context, state *middleware.AgentState) error {
    for _, obs := range state.LastObservations {
        if strings.HasPrefix(obs.Result, "__VETT_SUBMIT__") {
            state.StopLoop = true
            state.StopReason = "finish_tool"
            return nil
        }
    }
    return nil
}
```

## Chain configuration in profiles

Profiles specify middleware by name, in order:

```yaml
middleware:
  - output_truncation
  - submit_detector
  - stuck_detector
```

See [profile-schema.md](profile-schema.md#middleware) for how profile YAML declares and configures middleware. Each middleware can have its own configuration block:

```yaml
middleware:
  - output_truncation:
      max_chars: 16000
  - submit_detector: {}
  - stuck_detector:
      action_observation_threshold: 4
      action_error_threshold: 3
      monologue_threshold: 3
      alternating_threshold: 6
```

## Registration

Middlewares register themselves at init time, same pattern as tools:

```go
// middleware/stuck_detector.go
func init() {
    middleware.Register("stuck_detector", func(config map[string]any) middleware.Middleware {
        return &StuckDetector{
            ActionObservationThreshold: args.Int(config, "action_observation_threshold", 4),
            ActionErrorThreshold:       args.Int(config, "action_error_threshold", 3),
            MonologueThreshold:         args.Int(config, "monologue_threshold", 3),
            AlternatingThreshold:       args.Int(config, "alternating_threshold", 6),
        }
    })
}
```

The registrar takes a constructor function that receives the profile's config block for this middleware. The constructor reads typed config values out and returns a ready-to-use Middleware instance.

## Reentrant loops

When an agent dispatches a sub-agent (via `create_task` etc.), the sub-agent runs its OWN agent loop with its OWN middleware chain taken from the **dispatched profile** (usually a member profile referenced by role). The sub-agent's middleware chain is completely independent of the parent's.

**Middleware instances are NOT shared between parent and child.** Each loop invocation constructs a fresh middleware chain from the profile's YAML config. This means:
- Parent's StuckDetector state doesn't carry over to the child
- Child's middleware can't see parent's state
- Each sub-agent has its own event stream (but all events flow into the same event bus with a `depth` field for filtering)

Max dispatch depth is enforced by the loop itself, not middleware. The `env.Depth` field on tool Env and the `state.Depth` field on AgentState both carry the current depth for middlewares that want to be depth-aware.

## Testing middleware

Every middleware should have unit tests that:
1. Construct a canned `AgentState` with specific messages/observations
2. Call `Process` with a mocked event bus
3. Assert the correct StopLoop / StopReason / published events

Example:

```go
func TestStuckDetector_Scenario1_ActionObservationLoop(t *testing.T) {
    state := &middleware.AgentState{
        Messages: buildMessagesWith4IdenticalActionObservationPairs(),
        Iteration: 10,
        EventBus: &fakeEventBus{},
    }
    sd := &StuckDetector{ActionObservationThreshold: 4, ...}

    err := sd.Process(context.Background(), state)

    require.NoError(t, err)
    require.True(t, state.StopLoop)
    require.Equal(t, "action_observation_loop", state.StopReason)
}
```

## What middleware is NOT

- Not a replacement for tools. Middleware doesn't run in response to LLM requests; it runs between iterations.
- Not a place for "business logic" that affects what tools can be called. Tool gating happens at the profile level (tool list).
- Not a hook system for arbitrary user code. Middleware is a specific, narrow contract with a defined lifecycle.
- Not stateful across iterations in the "long-running goroutine" sense. Middleware instances ARE stateful (they're objects that persist across iterations in one agent loop), but state mutations should be idempotent if possible and reset at loop start.

If your "middleware" needs to do something not described here — spawn goroutines, make HTTP calls, persist to disk — that's a sign it should be a tool, a sandbox op, or something else entirely.
