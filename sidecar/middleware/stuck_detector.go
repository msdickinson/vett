package middleware

import (
	"context"
	"encoding/json"

	"github.com/msdickinson/vett/sidecar/internal/llm"
	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
)

// Defaults from openhands-sdk StuckDetectionThresholds.
const (
	DefaultActionObservationThreshold = 4
	DefaultActionErrorThreshold       = 3
	DefaultMonologueThreshold         = 3
	DefaultAlternatingThreshold       = 6
	MaxEventsToScan                   = 20
)

func init() {
	mw.Register("stuck_detector", func(config map[string]any) mw.Middleware {
		m := &StuckDetector{
			ActionObservationThreshold: DefaultActionObservationThreshold,
			ActionErrorThreshold:       DefaultActionErrorThreshold,
			MonologueThreshold:         DefaultMonologueThreshold,
			AlternatingThreshold:       DefaultAlternatingThreshold,
		}
		readInt(config, "action_observation_threshold", &m.ActionObservationThreshold)
		readInt(config, "action_error_threshold", &m.ActionErrorThreshold)
		readInt(config, "monologue_threshold", &m.MonologueThreshold)
		readInt(config, "alternating_threshold", &m.AlternatingThreshold)
		return m
	})
}

func readInt(c map[string]any, key string, out *int) {
	if v, ok := c[key]; ok {
		switch n := v.(type) {
		case int:
			*out = n
		case float64:
			*out = int(n)
		}
	}
}

// StuckDetector terminates the agent loop when one of 5 stuck patterns
// is detected in the recent event window. Matches openhands-sdk's
// stuck_detector.py scenarios 1-4 (scenario 5 — context window error
// loop — is a TODO in the openhands source and is intentionally skipped
// in Vett's openhands profile too, per openhands-reference-spec.md §6).
//
// Reset-on-user rule: only events AFTER the most recent user message
// are considered. A new user message clears the detection window.
type StuckDetector struct {
	ActionObservationThreshold int
	ActionErrorThreshold       int
	MonologueThreshold         int
	AlternatingThreshold       int
}

func (s *StuckDetector) Name() string { return "stuck_detector" }

func (s *StuckDetector) Process(ctx context.Context, state *mw.AgentState) error {
	if state.StopLoop {
		return nil
	}
	events := eventsSinceLastUser(state.Messages)
	if len(events) > MaxEventsToScan {
		events = events[len(events)-MaxEventsToScan:]
	}

	if s.detectMonologue(events) {
		s.stop(state, "monologue_loop")
		return nil
	}
	if s.detectActionObservationLoop(events) {
		s.stop(state, "action_observation_loop")
		return nil
	}
	if s.detectActionErrorLoop(events) {
		s.stop(state, "action_error_loop")
		return nil
	}
	if s.detectAlternatingLoop(events) {
		s.stop(state, "alternating_action_observation_loop")
		return nil
	}
	return nil
}

func (s *StuckDetector) stop(state *mw.AgentState, reason string) {
	state.StopLoop = true
	state.StopReason = reason
}

// An event is either an assistant "action" (tool call) or a tool-role
// observation, in the order they appear in Messages.
type stuckEvent struct {
	kind string // "action" or "observation" or "assistant_text"
	// For "action": the tool call fingerprint (name + args JSON).
	// For "observation": the tool result string.
	// For "assistant_text": the text content.
	sig string
	// For "observation": whether the observation text indicates an error.
	isError bool
}

// eventsSinceLastUser returns stuckEvents from messages after the last
// user message.
func eventsSinceLastUser(msgs []llm.Message) []stuckEvent {
	lastUser := -1
	for i := len(msgs) - 1; i >= 0; i-- {
		if msgs[i].Role == "user" {
			lastUser = i
			break
		}
	}
	start := 0
	if lastUser >= 0 {
		start = lastUser + 1
	}
	var out []stuckEvent
	for _, m := range msgs[start:] {
		switch m.Role {
		case "assistant":
			if len(m.ToolCalls) == 0 {
				// Pure-text assistant message → monologue candidate.
				parts := ""
				for _, p := range m.Parts {
					parts += p.Text
				}
				out = append(out, stuckEvent{kind: "assistant_text", sig: parts})
			} else {
				for _, tc := range m.ToolCalls {
					out = append(out, stuckEvent{kind: "action", sig: toolCallSig(tc)})
				}
			}
		case "tool":
			out = append(out, stuckEvent{
				kind:    "observation",
				sig:     m.Text,
				isError: isErrorObservation(m.Text),
			})
		}
	}
	return out
}

// toolCallSig is a cache-busting-free fingerprint: tool name + args JSON.
// Matches openhands' _event_eq which compares action + tool_name and
// ignores ids/metrics.
func toolCallSig(tc llm.ToolCall) string {
	// Normalize arguments through json round-trip so key ordering is
	// stable. Small cost; keeps equality reliable across model sampling.
	var obj map[string]any
	_ = json.Unmarshal([]byte(tc.Function.Arguments), &obj)
	normalized, _ := json.Marshal(obj)
	return tc.Function.Name + ":" + string(normalized)
}

// isErrorObservation approximates openhands' AgentErrorEvent check:
// any observation text that begins with "Error:" or contains
// "Traceback" counts as an error.
func isErrorObservation(text string) bool {
	if len(text) == 0 {
		return false
	}
	if len(text) >= 6 && text[:6] == "Error:" {
		return true
	}
	return false
}

// Scenario 1: N consecutive action-observation pairs all identical.
func (s *StuckDetector) detectActionObservationLoop(events []stuckEvent) bool {
	n := s.ActionObservationThreshold
	if n <= 0 {
		return false
	}
	pairs := extractActionObservationPairs(events)
	if len(pairs) < n {
		return false
	}
	last := pairs[len(pairs)-n:]
	for i := 1; i < len(last); i++ {
		if last[i].action != last[0].action || last[i].observation != last[0].observation {
			return false
		}
	}
	return true
}

// Scenario 2: N consecutive (action, error observation) pairs, same action.
func (s *StuckDetector) detectActionErrorLoop(events []stuckEvent) bool {
	n := s.ActionErrorThreshold
	if n <= 0 {
		return false
	}
	pairs := extractActionObservationPairs(events)
	if len(pairs) < n {
		return false
	}
	last := pairs[len(pairs)-n:]
	for _, p := range last {
		if !p.isError {
			return false
		}
	}
	for i := 1; i < len(last); i++ {
		if last[i].action != last[0].action {
			return false
		}
	}
	return true
}

// Scenario 3: N consecutive pure-text assistant messages (monologue).
func (s *StuckDetector) detectMonologue(events []stuckEvent) bool {
	n := s.MonologueThreshold
	if n <= 0 {
		return false
	}
	count := 0
	for i := len(events) - 1; i >= 0; i-- {
		if events[i].kind == "assistant_text" {
			count++
			if count >= n {
				return true
			}
		} else {
			break
		}
	}
	return false
}

// Scenario 4: alternating A-B-A-B-A-B, 6 events (3 of each action), where
// actions[i] == actions[i+2] and observations[i] == observations[i+2].
func (s *StuckDetector) detectAlternatingLoop(events []stuckEvent) bool {
	n := s.AlternatingThreshold
	if n <= 0 || n%2 != 0 {
		return false
	}
	pairs := extractActionObservationPairs(events)
	half := n / 2
	if len(pairs) < half {
		return false
	}
	last := pairs[len(pairs)-half:]
	if half < 3 {
		return false
	}
	for i := 0; i < half-2; i++ {
		if last[i].action != last[i+2].action || last[i].observation != last[i+2].observation {
			return false
		}
	}
	// Must actually alternate (A != B).
	if last[0].action == last[1].action {
		return false
	}
	return true
}

type actionObs struct {
	action      string
	observation string
	isError     bool
}

// extractActionObservationPairs walks events and yields (action, next
// observation) pairs in order. Skips assistant_text events.
func extractActionObservationPairs(events []stuckEvent) []actionObs {
	var pairs []actionObs
	for i := 0; i < len(events); i++ {
		if events[i].kind != "action" {
			continue
		}
		// Find the next observation event.
		var obs *stuckEvent
		for j := i + 1; j < len(events); j++ {
			if events[j].kind == "observation" {
				obs = &events[j]
				break
			}
			if events[j].kind == "action" {
				break
			}
		}
		if obs == nil {
			continue
		}
		pairs = append(pairs, actionObs{
			action:      events[i].sig,
			observation: obs.sig,
			isError:     obs.isError,
		})
	}
	return pairs
}
