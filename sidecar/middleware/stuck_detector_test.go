package middleware

import (
	"context"
	"testing"

	"github.com/msdickinson/vett/sidecar/internal/llm"
	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
)

func newStuckDetector() *StuckDetector {
	return &StuckDetector{
		ActionObservationThreshold: DefaultActionObservationThreshold,
		ActionErrorThreshold:       DefaultActionErrorThreshold,
		MonologueThreshold:         DefaultMonologueThreshold,
		AlternatingThreshold:       DefaultAlternatingThreshold,
	}
}

func action(id, name, args string) llm.Message {
	return llm.Message{
		Role: "assistant",
		ToolCalls: []llm.ToolCall{
			{
				ID:   id,
				Type: "function",
				Function: llm.ToolCallFn{Name: name, Arguments: args},
			},
		},
	}
}

func obs(id, text string) llm.Message {
	return llm.Message{Role: "tool", Text: text, ToolCallID: id}
}

func userMsg(text string) llm.Message {
	return llm.Message{Role: "user", Parts: []llm.ContentPart{{Type: "text", Text: text}}}
}

func assistantText(text string) llm.Message {
	return llm.Message{Role: "assistant", Parts: []llm.ContentPart{{Type: "text", Text: text}}}
}

func runDetector(t *testing.T, msgs []llm.Message) (bool, string) {
	t.Helper()
	state := &mw.AgentState{Messages: msgs}
	sd := newStuckDetector()
	if err := sd.Process(context.Background(), state); err != nil {
		t.Fatalf("process: %v", err)
	}
	return state.StopLoop, state.StopReason
}

// Scenario 1: 4 identical (action, observation) pairs -> stuck.
func TestStuckDetectorScenario1_ActionObservationLoop(t *testing.T) {
	var msgs []llm.Message
	msgs = append(msgs, userMsg("start"))
	for i := 0; i < 4; i++ {
		msgs = append(msgs, action("c1", "terminal", `{"command":"ls"}`))
		msgs = append(msgs, obs("c1", "file.py\n"))
	}
	stop, reason := runDetector(t, msgs)
	if !stop || reason != "action_observation_loop" {
		t.Errorf("expected action_observation_loop stop, got stop=%v reason=%q", stop, reason)
	}
}

// 3 identical pairs should NOT trigger (threshold is 4).
func TestStuckDetectorScenario1_BelowThreshold(t *testing.T) {
	var msgs []llm.Message
	msgs = append(msgs, userMsg("start"))
	for i := 0; i < 3; i++ {
		msgs = append(msgs, action("c1", "terminal", `{"command":"ls"}`))
		msgs = append(msgs, obs("c1", "same\n"))
	}
	stop, _ := runDetector(t, msgs)
	if stop {
		t.Error("should not stop with only 3 identical pairs")
	}
}

// Scenario 2: 3 identical actions each followed by an Error: observation.
func TestStuckDetectorScenario2_ActionErrorLoop(t *testing.T) {
	var msgs []llm.Message
	msgs = append(msgs, userMsg("start"))
	for i := 0; i < 3; i++ {
		msgs = append(msgs, action("c", "terminal", `{"command":"broken"}`))
		msgs = append(msgs, obs("c", "Error: command not found"))
	}
	stop, reason := runDetector(t, msgs)
	if !stop {
		t.Fatal("expected stop")
	}
	if reason != "action_error_loop" && reason != "action_observation_loop" {
		// action_observation_loop can also fire if the error text and
		// action are both identical — but action_error_loop should
		// check first (threshold=3 < action_observation threshold=4).
		t.Errorf("expected action_error_loop, got %q", reason)
	}
}

// Scenario 3: 3 consecutive pure-text assistant messages (monologue).
func TestStuckDetectorScenario3_Monologue(t *testing.T) {
	msgs := []llm.Message{
		userMsg("start"),
		assistantText("I am thinking"),
		assistantText("Still thinking"),
		assistantText("Almost done"),
	}
	stop, reason := runDetector(t, msgs)
	if !stop || reason != "monologue_loop" {
		t.Errorf("expected monologue_loop, got stop=%v reason=%q", stop, reason)
	}
}

// Scenario 4: A-B-A-B-A-B alternating, threshold 6.
func TestStuckDetectorScenario4_AlternatingLoop(t *testing.T) {
	A := action("c1", "terminal", `{"command":"ls"}`)
	Aobs := obs("c1", "a-out\n")
	B := action("c2", "terminal", `{"command":"pwd"}`)
	Bobs := obs("c2", "b-out\n")
	msgs := []llm.Message{
		userMsg("start"),
		A, Aobs, B, Bobs,
		A, Aobs, B, Bobs,
		A, Aobs, B, Bobs,
	}
	stop, reason := runDetector(t, msgs)
	if !stop {
		t.Fatal("expected stop")
	}
	if reason != "alternating_action_observation_loop" && reason != "action_observation_loop" {
		t.Errorf("expected alternating_action_observation_loop, got %q", reason)
	}
}

// Reset-on-user: an otherwise-stuck sequence is cleared by a new user
// message appearing later.
func TestStuckDetectorResetsOnUserMessage(t *testing.T) {
	stuckSeq := []llm.Message{
		action("c", "terminal", `{"command":"x"}`),
		obs("c", "out"),
		action("c", "terminal", `{"command":"x"}`),
		obs("c", "out"),
		action("c", "terminal", `{"command":"x"}`),
		obs("c", "out"),
		action("c", "terminal", `{"command":"x"}`),
		obs("c", "out"),
	}
	msgs := append([]llm.Message{userMsg("first")}, stuckSeq...)
	msgs = append(msgs, userMsg("reset — try something else"))
	msgs = append(msgs,
		action("c", "terminal", `{"command":"y"}`),
		obs("c", "different"))
	stop, _ := runDetector(t, msgs)
	if stop {
		t.Error("expected user message to reset the detection window")
	}
}

// When multiple scenarios could fire, the first one detected (in
// Process's declared order) wins. This test just asserts SOMETHING
// stops; which specific reason is not a guarantee.
func TestStuckDetectorStopsOnMixedTriggers(t *testing.T) {
	msgs := []llm.Message{userMsg("x")}
	for i := 0; i < 4; i++ {
		msgs = append(msgs, action("c", "terminal", `{"command":"dup"}`))
		msgs = append(msgs, obs("c", "Error: dup"))
	}
	stop, _ := runDetector(t, msgs)
	if !stop {
		t.Error("expected stop")
	}
}
