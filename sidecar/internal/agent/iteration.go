package agent

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/msdickinson/vett/sidecar/internal/eventbus"
	"github.com/msdickinson/vett/sidecar/internal/llm"
	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
	"github.com/msdickinson/vett/sidecar/pkg/tool"
)

// errEmptyResponse is returned when the LLM returns no choices.
// Callers check for this to set StopReason to "empty_response"
// instead of "llm_error".
var errEmptyResponse = errors.New("llm returned no choices")

// iterResult is the outcome of one iteration.
type iterResult struct {
	textOnly      bool   // true if the LLM produced text without tool calls
	assistantText string // the text content (only meaningful when textOnly)
	inputTokens   int
	outputTokens  int
}

// runOneIteration executes one LLM call + tool dispatch + middleware cycle.
// It mutates state (appends messages, sets StopLoop, etc.) and returns
// metadata about what happened.
func (l *Loop) runOneIteration(ctx context.Context, state *mw.AgentState) (*iterResult, error) {
	if l.Bus != nil {
		l.Bus.Publish(eventbus.Event{
			Type: eventbus.IterationStart,
			Data: l.tag(map[string]any{"iteration": state.Iteration}),
		})
	}

	req := llm.Request{
		Model:       l.Model,
		Messages:    state.Messages,
		Tools:       l.WireTools,
		TopP:        l.TopP,
		Temperature: l.Temperature,
	}

	iterStart := time.Now()
	if l.Bus != nil {
		l.Bus.Publish(eventbus.Event{
			Type: eventbus.LLMRequest,
			Data: l.tag(map[string]any{
				"iteration":     state.Iteration,
				"message_count": len(req.Messages),
				"tool_count":    len(req.Tools),
			}),
		})
	}

	resp, _, err := l.Client.Send(ctx, req)
	if err != nil {
		return nil, fmt.Errorf("llm send: %w", err)
	}
	if len(resp.Choices) == 0 {
		return nil, errEmptyResponse
	}
	choice := resp.Choices[0]
	state.InputTokensLastIter = resp.Usage.PromptTokens
	state.OutputTokensLastIter = resp.Usage.CompletionTokens

	if l.Bus != nil {
		l.Bus.Publish(eventbus.Event{
			Type: eventbus.LLMResponse,
			Data: l.tag(map[string]any{
				"iteration":     state.Iteration,
				"finish_reason": choice.FinishReason,
				"input_tokens":  resp.Usage.PromptTokens,
				"output_tokens": resp.Usage.CompletionTokens,
			}),
		})
	}

	// Convert response into wire-layer tool calls and build the
	// assistant message to append.
	calls := convertToolCalls(choice.Message.ToolCalls)
	assistantMsg := llm.Message{
		Role:      "assistant",
		Parts:     []llm.ContentPart{},
		ToolCalls: calls,
	}
	if choice.Message.Content != "" {
		assistantMsg.Parts = []llm.ContentPart{{Type: "text", Text: choice.Message.Content}}
	}
	state.Messages = append(state.Messages, assistantMsg)
	state.LastToolCalls = calls
	state.LastObservations = nil

	// Dispatch each tool call through the profile's tool map.
	for _, call := range calls {
		obs := l.dispatchCall(ctx, call, state.Iteration)
		state.LastObservations = append(state.LastObservations, obs)
		state.Messages = append(state.Messages, llm.Message{
			Role:       "tool",
			Text:       obs.Result,
			ToolCallID: call.ID,
		})
	}

	// Run middleware chain. Any middleware can set StopLoop.
	for _, m := range l.Middlewares {
		if err := m.Process(ctx, state); err != nil && l.Bus != nil {
			l.Bus.Publish(eventbus.Event{
				Type: eventbus.ErrorEvent,
				Data: l.tag(map[string]any{"where": "middleware:" + m.Name(), "message": err.Error()}),
			})
		}
	}

	if l.Bus != nil {
		l.Bus.Publish(eventbus.Event{
			Type: eventbus.IterationEnd,
			Data: l.tag(map[string]any{
				"iteration":   state.Iteration,
				"duration_ms": time.Since(iterStart).Milliseconds(),
			}),
		})
	}

	return &iterResult{
		textOnly:      len(calls) == 0,
		assistantText: choice.Message.Content,
		inputTokens:   resp.Usage.PromptTokens,
		outputTokens:  resp.Usage.CompletionTokens,
	}, nil
}

// checkSessionRecovery checks for terminal tool timeout/failure patterns
// and triggers session recovery if needed. Returns the updated
// consecutiveTerminalFailures count.
func (l *Loop) checkSessionRecovery(ctx context.Context, state *mw.AgentState, consecutiveTerminalFailures int) int {
	terminalSeenThisIter := false
	terminalAllFailedThisIter := true
	for _, obs := range state.LastObservations {
		if obs.ToolName != "terminal" {
			continue
		}
		terminalSeenThisIter = true
		if obs.Success {
			terminalAllFailedThisIter = false
			break
		}
	}
	if terminalSeenThisIter {
		if terminalAllFailedThisIter {
			consecutiveTerminalFailures++
		} else {
			consecutiveTerminalFailures = 0
		}
	}

	shouldRecover := l.RecoverSession != nil && (timeoutObservedThisIter(state.LastObservations) || consecutiveTerminalFailures >= ConsecutiveTerminalFailureThreshold)

	if shouldRecover {
		if err := l.RecoverSession(ctx); err != nil {
			if l.Bus != nil {
				l.Bus.Publish(eventbus.Event{
					Type: eventbus.ErrorEvent,
					Data: l.tag(map[string]any{"where": "session_recover", "message": err.Error()}),
				})
			}
		} else {
			consecutiveTerminalFailures = 0
			if l.Bus != nil {
				reason := "terminal_timeout"
				if !timeoutObservedThisIter(state.LastObservations) {
					reason = "consecutive_terminal_failures"
				}
				l.Bus.Publish(eventbus.Event{
					Type: eventbus.MiddlewareEv,
					Data: l.tag(map[string]any{
						"middleware": "session_recovery",
						"action":    "recreate_session",
						"reason":    reason,
						"iteration": state.Iteration,
					}),
				})
			}
		}
	}

	return consecutiveTerminalFailures
}

// timeoutObservedThisIter returns true if any observation in the slice
// is a terminal tool result that starts with the timeout prefix.
func timeoutObservedThisIter(obs []mw.Observation) bool {
	for _, o := range obs {
		if o.ToolName == "terminal" && strings.HasPrefix(o.Result, tool.TimeoutObservationPrefix) {
			return true
		}
	}
	return false
}
