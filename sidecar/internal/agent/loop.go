// Package agent is Vett's per-instance agent loop. One Loop runs one
// instance end-to-end: build the initial messages, call the LLM, dispatch
// each tool call through the profile's tool set, run middleware, repeat
// until a terminal condition (finish, stuck, max iterations, timeout,
// error).
package agent

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/msdickinson/vett/sidecar/internal/agent/tasks"
	"github.com/msdickinson/vett/sidecar/internal/eventbus"
	"github.com/msdickinson/vett/sidecar/internal/llm"
	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
	"github.com/msdickinson/vett/sidecar/pkg/tool"
)

// SessionRecoverFn is a callback the runner supplies to rebuild the
// agent's sandbox session when the bash child dies. The loop calls it
// between iterations when it detects either:
//   - a timeout observation from the terminal tool
//     (tool.TimeoutObservationPrefix), or
//   - ConsecutiveTerminalFailureThreshold consecutive terminal tool
//     observations with Success=false (silent session death).
//
// Phase 2 found two real SWE-bench runs (astropy__astropy-7336 and
// -7166) where the bash child died without emitting the timeout
// prefix — the agent kept calling terminal, each call failing, and
// eventually gave up via finish with no real work captured. The
// consecutive-failure trigger catches that class of failure.
//
// Phase 2 limitation: recover resets the session's cwd to the profile's
// default_cwd (e.g. /testbed) rather than the last-known cwd from the
// dying session. The model can re-cd from its conversation history.
type SessionRecoverFn func(ctx context.Context) error

// ConsecutiveTerminalFailureThreshold is how many terminal tool calls
// in a row must fail (Observation.Success == false) before
// RecoverSession is triggered. 2 is deliberately low so silent session
// death is caught fast without letting a single flaky command churn
// the session.
const ConsecutiveTerminalFailureThreshold = 2


// Loop is one configured agent loop. The fields cover everything one
// iteration needs: the LLM client, the sandbox interface tools dispatch
// through, the per-profile tool map, the middleware chain, and the bus
// to publish events to.
type Loop struct {
	Client        *llm.Client
	Model         string
	Sandbox       tool.Sandbox
	SessionID     string
	Tools         map[string]*tool.Tool // wire-name → registered tool
	WireTools     []json.RawMessage     // schemas shipped to the LLM (in order)
	Middlewares   []mw.Middleware
	Temperature   float64
	TopP          float64
	MaxIterations int
	Bus           *eventbus.Bus
	Tasks         *tasks.Registry

	// InstanceID is stamped onto every event the loop publishes so
	// per-instance trace writers can filter correctly. The runner
	// sets it before calling loop.Run; tests that don't care can
	// leave it empty.
	InstanceID string

	// RecoverSession is called when a terminal tool observation shows
	// the session's bash child was killed by a timeout. The loop will
	// not attempt recovery if this is nil — the next tool call will
	// fail naturally with "session not found".
	RecoverSession SessionRecoverFn
}

// tag adds instance_id to a data map if the loop has one set. Helper
// so every Publish site stays one-liner-clean.
func (l *Loop) tag(data map[string]any) map[string]any {
	if l.InstanceID != "" {
		data["instance_id"] = l.InstanceID
	}
	return data
}

// Result is what Run returns to the caller.
type Result struct {
	Messages   []llm.Message // final message history
	StopReason string
	Iterations int
	InputTokens  int
	OutputTokens int
}

// Run drives the loop from initial system prompt + user message to a
// terminal state. Returns the final state (for patch extraction) and a
// stop reason.
func (l *Loop) Run(ctx context.Context, systemPrompt, userMessage string) (*Result, error) {
	state := &mw.AgentState{
		Messages: []llm.Message{
			{Role: "system", Parts: []llm.ContentPart{{Type: "text", Text: systemPrompt}}},
			{Role: "user", Parts: []llm.ContentPart{{Type: "text", Text: userMessage}}},
		},
		MaxIterations: l.MaxIterations,
	}

	consecutiveTerminalFailures := 0

	for state.Iteration < l.MaxIterations {
		state.Iteration++

		result, err := l.runOneIteration(ctx, state)
		if err != nil {
			reason := "llm_error"
			if errors.Is(err, errEmptyResponse) {
				reason = "empty_response"
			}
			return l.finalizeState(state, reason), fmt.Errorf("iteration %d: %w", state.Iteration, err)
		}

		state.TotalInputTokens += result.inputTokens
		state.TotalOutputTokens += result.outputTokens

		if state.StopLoop {
			return l.finalizeState(state, state.StopReason), nil
		}

		consecutiveTerminalFailures = l.checkSessionRecovery(ctx, state, consecutiveTerminalFailures)
	}
	return l.finalizeState(state, "max_iterations"), nil
}

func (l *Loop) dispatchCall(ctx context.Context, call llm.ToolCall, iteration int) mw.Observation {
	t := l.Tools[call.Function.Name]
	if t == nil {
		return mw.Observation{
			ToolCallID: call.ID,
			ToolName:   call.Function.Name,
			Result:     fmt.Sprintf("Error: tool %q is not registered for this profile", call.Function.Name),
			Success:    false,
		}
	}
	var argMap map[string]any
	if call.Function.Arguments != "" {
		if err := json.Unmarshal([]byte(call.Function.Arguments), &argMap); err != nil {
			return mw.Observation{
				ToolCallID: call.ID,
				ToolName:   call.Function.Name,
				Result:     fmt.Sprintf("Error: could not parse tool arguments: %v", err),
				Success:    false,
			}
		}
	}
	env := tool.Env{
		Sandbox:   l.Sandbox,
		SessionID: l.SessionID,
		CallID:    call.ID,
		Depth:     0,
	}
	start := time.Now()
	if l.Bus != nil {
		l.Bus.Publish(eventbus.Event{
			Type: eventbus.ToolCallStart,
			Data: l.tag(map[string]any{
				"iteration": iteration,
				"call_id":   call.ID,
				"tool_name": call.Function.Name,
				"arguments": argMap,
			}),
		})
	}
	result, err := t.Execute(ctx, argMap, env)
	if l.Bus != nil {
		l.Bus.Publish(eventbus.Event{
			Type: eventbus.ToolCallEnd,
			Data: l.tag(map[string]any{
				"iteration":     iteration,
				"call_id":       call.ID,
				"tool_name":     call.Function.Name,
				"duration_ms":   time.Since(start).Milliseconds(),
				"success":       err == nil,
				"result_length": len(result),
			}),
		})
	}
	obs := mw.Observation{
		ToolCallID: call.ID,
		ToolName:   call.Function.Name,
		Result:     result,
		Success:    err == nil,
		Error:      err,
	}
	if err != nil && result == "" {
		obs.Result = fmt.Sprintf("Error: %v", err)
	}
	return obs
}

func (l *Loop) finalize(state *mw.AgentState, reason string) *Result {
	return l.finalizeState(state, reason)
}

func (l *Loop) finalizeState(state *mw.AgentState, reason string) *Result {
	return &Result{
		Messages:     state.Messages,
		StopReason:   reason,
		Iterations:   state.Iteration,
		InputTokens:  state.TotalInputTokens,
		OutputTokens: state.TotalOutputTokens,
	}
}

// convertToolCalls rewrites llm.Response's RespToolCall into the wire
// ToolCall shape used in outgoing requests. Preserves ID and raw args.
func convertToolCalls(src []llm.RespToolCall) []llm.ToolCall {
	if len(src) == 0 {
		return nil
	}
	out := make([]llm.ToolCall, len(src))
	for i, c := range src {
		t := c.Type
		if t == "" {
			t = "function"
		}
		out[i] = llm.ToolCall{
			ID:   c.ID,
			Type: t,
			Function: llm.ToolCallFn{
				Name:      c.Function.Name,
				Arguments: c.Function.Arguments,
			},
		}
	}
	return out
}
