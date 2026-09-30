package tools

import (
	"context"

	"github.com/msdickinson/vett/sidecar/pkg/tool"
	"github.com/msdickinson/vett/sidecar/pkg/tool/args"
)

func init() {
	tool.Register(&tool.Tool{
		Key:     "think",
		Name:    "think",
		Execute: executeThink,
	})
}

// executeThink is a no-op: the LLM uses it to deliberate aloud, and the
// observation just echoes back a short acknowledgement. Matches
// openhands' behavior on this tool.
func executeThink(ctx context.Context, a map[string]any, env tool.Env) (string, error) {
	_ = args.String(a, "thought", "")
	return "Your thought has been logged.", nil
}
