package tools

import (
	"context"
	"fmt"

	"github.com/msdickinson/vett/sidecar/pkg/tool"
	"github.com/msdickinson/vett/sidecar/pkg/tool/args"
)

func init() {
	tool.Register(&tool.Tool{
		Key:     "task_tracker",
		Name:    "task_tracker",
		Execute: executeTaskTracker,
	})
}

// executeTaskTracker echoes the current task list back to the LLM. Phase 1
// doesn't persist task state — openhands uses it mainly for "explain your
// plan" rather than as durable state. Acknowledgement-style observation
// is sufficient.
func executeTaskTracker(ctx context.Context, a map[string]any, env tool.Env) (string, error) {
	cmd := args.String(a, "command", "plan")
	if list, ok := a["task_list"].([]any); ok {
		return fmt.Sprintf("Task list updated (%d tasks, command=%q).", len(list), cmd), nil
	}
	return fmt.Sprintf("task_tracker command %q recorded.", cmd), nil
}
