package middleware

import (
	"context"

	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
)

const defaultMaxChars = 16000

func init() {
	mw.Register("output_truncation", func(config map[string]any) mw.Middleware {
		m := &OutputTruncation{MaxChars: defaultMaxChars}
		if v, ok := config["max_chars"].(int); ok {
			m.MaxChars = v
		} else if v, ok := config["max_chars"].(float64); ok {
			m.MaxChars = int(v)
		}
		return m
	})
}

// OutputTruncation clips observation text longer than MaxChars and
// appends the canonical openhands "response clipped" notice. Runs after
// tool dispatch, mutates state.Messages tool-role entries in place.
type OutputTruncation struct {
	MaxChars int
}

// TruncationNotice is appended to the clipped text. Matches the
// SIDE-CAR-level constant so observations look the same whether the
// truncation came from the file_editor ops or from here.
const TruncationNotice = "<response clipped><NOTE>To save on context only part of this file has been shown to you. You should retry this tool after you have searched inside the file with `grep -n` in order to find the line numbers of what you are looking for.</NOTE>"

func (m *OutputTruncation) Name() string { return "output_truncation" }

func (m *OutputTruncation) Process(ctx context.Context, state *mw.AgentState) error {
	max := m.MaxChars
	if max <= 0 {
		max = defaultMaxChars
	}
	// Mutate the most-recently-appended tool-role messages in-place.
	// LastObservations is the authoritative list for "this iteration's
	// tool results" so we iterate it and rewrite matching messages.
	for i := range state.LastObservations {
		if len(state.LastObservations[i].Result) > max {
			state.LastObservations[i].Result = state.LastObservations[i].Result[:max] + "\n" + TruncationNotice
		}
	}
	// Also rewrite the tail of state.Messages where the tool-role
	// messages for this iteration live.
	for i := len(state.Messages) - 1; i >= 0; i-- {
		if state.Messages[i].Role != "tool" {
			break
		}
		if len(state.Messages[i].Text) > max {
			state.Messages[i].Text = state.Messages[i].Text[:max] + "\n" + TruncationNotice
		}
	}
	return nil
}
