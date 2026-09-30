package middleware

import (
	"context"
	"strings"
	"testing"

	"github.com/msdickinson/vett/sidecar/internal/llm"
	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
)

func TestOutputTruncationClipsLongResult(t *testing.T) {
	ot := &OutputTruncation{MaxChars: 20}
	long := strings.Repeat("x", 100)
	state := &mw.AgentState{
		LastObservations: []mw.Observation{{Result: long}},
		Messages: []llm.Message{
			{Role: "assistant"},
			{Role: "tool", Text: long},
		},
	}
	if err := ot.Process(context.Background(), state); err != nil {
		t.Fatal(err)
	}
	if len(state.LastObservations[0].Result) == len(long) {
		t.Errorf("expected truncation, got unchanged length")
	}
	if !strings.Contains(state.LastObservations[0].Result, "<response clipped>") {
		t.Errorf("expected clipped notice, got %q", state.LastObservations[0].Result)
	}
	if !strings.Contains(state.Messages[1].Text, "<response clipped>") {
		t.Errorf("expected message text to be clipped too")
	}
}

func TestOutputTruncationLeavesShortResultAlone(t *testing.T) {
	ot := &OutputTruncation{MaxChars: 100}
	short := "hi"
	state := &mw.AgentState{
		LastObservations: []mw.Observation{{Result: short}},
		Messages: []llm.Message{
			{Role: "assistant"},
			{Role: "tool", Text: short},
		},
	}
	if err := ot.Process(context.Background(), state); err != nil {
		t.Fatal(err)
	}
	if state.LastObservations[0].Result != short {
		t.Errorf("short result should be unchanged, got %q", state.LastObservations[0].Result)
	}
}
