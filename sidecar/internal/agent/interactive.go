package agent

import (
	"context"
	"errors"
	"fmt"

	"github.com/msdickinson/vett/sidecar/internal/llm"
	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
)

// UserInput is a message injected into the conversation between
// iterations of the interactive loop.
type UserInput struct {
	Text string
}

// InteractiveCallbacks provides hooks for the interactive loop to
// communicate state changes to the UI layer (terminal or extension).
type InteractiveCallbacks struct {
	// OnAssistantText is called when the assistant produces text
	// (not tool calls). The UI can display this immediately.
	OnAssistantText func(text string)

	// OnWaitingForInput is called when the loop pauses to wait for
	// user input. The UI can show a prompt.
	OnWaitingForInput func()
}

// RunInteractive runs the agent loop in interactive mode. It waits on
// the userInput channel for messages, appends them to the conversation,
// and continues the loop.
//
// Behavior:
//   - Starts with system prompt only, waits for first user message
//   - After the LLM responds with text only (no tool calls): pauses,
//     waits for user input before continuing
//   - After the LLM makes tool calls: auto-continues (agent working)
//   - User can inject messages at any time via the channel
//   - Closing the userInput channel ends the loop
func (l *Loop) RunInteractive(
	ctx context.Context,
	systemPrompt string,
	userInput <-chan UserInput,
	cb InteractiveCallbacks,
) (*Result, error) {
	state := &mw.AgentState{
		Messages: []llm.Message{
			{Role: "system", Parts: []llm.ContentPart{{Type: "text", Text: systemPrompt}}},
		},
		MaxIterations: l.MaxIterations,
	}

	consecutiveTerminalFailures := 0

	// Wait for first user message before calling the LLM.
	if cb.OnWaitingForInput != nil {
		cb.OnWaitingForInput()
	}
	select {
	case msg, ok := <-userInput:
		if !ok {
			return l.finalizeState(state, "user_closed"), nil
		}
		state.Messages = append(state.Messages, llm.Message{
			Role:  "user",
			Parts: []llm.ContentPart{{Type: "text", Text: msg.Text}},
		})
	case <-ctx.Done():
		return l.finalizeState(state, "cancelled"), ctx.Err()
	}

	for state.Iteration < l.MaxIterations {
		state.Iteration++

		iterResult, err := l.runOneIteration(ctx, state)
		if err != nil {
			reason := "llm_error"
			if errors.Is(err, errEmptyResponse) {
				reason = "empty_response"
			}
			return l.finalizeState(state, reason), fmt.Errorf("iteration %d: %w", state.Iteration, err)
		}

		// Update token totals.
		state.TotalInputTokens += iterResult.inputTokens
		state.TotalOutputTokens += iterResult.outputTokens

		if state.StopLoop {
			return l.finalizeState(state, state.StopReason), nil
		}

		// Session recovery (same logic as Run).
		consecutiveTerminalFailures = l.checkSessionRecovery(ctx, state, consecutiveTerminalFailures)

		// If the assistant produced text (with or without tool calls),
		// notify the UI so the user can see it.
		if cb.OnAssistantText != nil && iterResult.assistantText != "" {
			cb.OnAssistantText(iterResult.assistantText)
		}

		// If text only (no tool calls), wait for user input.
		if iterResult.textOnly {
			if cb.OnWaitingForInput != nil {
				cb.OnWaitingForInput()
			}
			select {
			case msg, ok := <-userInput:
				if !ok {
					return l.finalizeState(state, "user_closed"), nil
				}
				state.Messages = append(state.Messages, llm.Message{
					Role:  "user",
					Parts: []llm.ContentPart{{Type: "text", Text: msg.Text}},
				})
				consecutiveTerminalFailures = 0
			case <-ctx.Done():
				return l.finalizeState(state, "cancelled"), ctx.Err()
			}
			continue
		}

		// Tool calls were dispatched — agent is working autonomously.
		// Check for injected user input non-blocking.
		select {
		case msg, ok := <-userInput:
			if !ok {
				return l.finalizeState(state, "user_closed"), nil
			}
			state.Messages = append(state.Messages, llm.Message{
				Role:  "user",
				Parts: []llm.ContentPart{{Type: "text", Text: msg.Text}},
			})
			consecutiveTerminalFailures = 0
		default:
			// No user input queued, continue autonomously.
		}
	}
	return l.finalizeState(state, "max_iterations"), nil
}
