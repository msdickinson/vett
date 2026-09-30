// Package middleware holds Vett's built-in middleware implementations.
// Each file registers one middleware at init time. Import for side
// effects in the cmd/vett binary.
package middleware

import (
	"context"
	"strings"

	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
	"github.com/msdickinson/vett/sidecar/pkg/tool"
)

func init() {
	mw.Register("submit_detector", func(_ map[string]any) mw.Middleware {
		return &SubmitDetector{}
	})
}

// SubmitDetector terminates the loop when an observation is prefixed with
// the finish tool's submit marker. StopReason is "finish_tool".
type SubmitDetector struct{}

func (s *SubmitDetector) Name() string { return "submit_detector" }

func (s *SubmitDetector) Process(ctx context.Context, state *mw.AgentState) error {
	if state.StopLoop {
		return nil
	}
	for _, obs := range state.LastObservations {
		if strings.HasPrefix(obs.Result, tool.SubmitMarker) {
			state.StopLoop = true
			state.StopReason = "finish_tool"
			return nil
		}
	}
	return nil
}
