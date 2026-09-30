package middleware

import (
	"context"
	"testing"

	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
	"github.com/msdickinson/vett/sidecar/tools"
)

func TestSubmitDetectorTriggersOnMarker(t *testing.T) {
	sd := &SubmitDetector{}
	state := &mw.AgentState{
		LastObservations: []mw.Observation{
			{Result: tools.SubmitMarker + "done"},
		},
	}
	if err := sd.Process(context.Background(), state); err != nil {
		t.Fatal(err)
	}
	if !state.StopLoop || state.StopReason != "finish_tool" {
		t.Errorf("expected finish_tool stop, got stop=%v reason=%q", state.StopLoop, state.StopReason)
	}
}

func TestSubmitDetectorIgnoresOtherResults(t *testing.T) {
	sd := &SubmitDetector{}
	state := &mw.AgentState{
		LastObservations: []mw.Observation{
			{Result: "some output\n[Command finished with exit code 0]"},
		},
	}
	if err := sd.Process(context.Background(), state); err != nil {
		t.Fatal(err)
	}
	if state.StopLoop {
		t.Error("expected no stop for non-finish observations")
	}
}

func TestSubmitDetectorIsNoOpWhenAlreadyStopping(t *testing.T) {
	sd := &SubmitDetector{}
	state := &mw.AgentState{
		StopLoop:   true,
		StopReason: "something_else",
		LastObservations: []mw.Observation{
			{Result: tools.SubmitMarker + "done"},
		},
	}
	if err := sd.Process(context.Background(), state); err != nil {
		t.Fatal(err)
	}
	if state.StopReason != "something_else" {
		t.Errorf("should not overwrite existing stop reason: got %q", state.StopReason)
	}
}
