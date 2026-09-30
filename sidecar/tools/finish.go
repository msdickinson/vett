package tools

import (
	"context"

	"github.com/msdickinson/vett/sidecar/pkg/tool"
	"github.com/msdickinson/vett/sidecar/pkg/tool/args"
)

// SubmitMarker is an alias for tool.SubmitMarker, preserved for any
// external code that referenced tools.SubmitMarker. Phase 2 moves the
// constant to pkg/tool so middleware can depend on it without pulling
// in the whole tools package.
const SubmitMarker = tool.SubmitMarker

func init() {
	tool.Register(&tool.Tool{
		Key:     "finish",
		Name:    "finish",
		Execute: executeFinish,
	})
}

// executeFinish returns the submit sentinel plus the agent's message so
// the trace can record why the run ended.
func executeFinish(ctx context.Context, a map[string]any, env tool.Env) (string, error) {
	message := args.String(a, "message", "")
	return tool.SubmitMarker + message, nil
}
