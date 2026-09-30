package team

import (
	"context"
	"fmt"
	"strings"

	"github.com/msdickinson/vett/sidecar/pkg/tool"
)

// LeaderToolset creates the 5 delegation tools for the leader agent.
// These are not globally registered — they're injected into the leader's
// tool map at runtime.
func LeaderToolset(board *Board, runMember RunMemberFn) map[string]*tool.Tool {
	return map[string]*tool.Tool{
		"assign_task":   assignTaskTool(board, runMember),
		"assign_async":  assignAsyncTool(board, runMember),
		"check_task":    checkTaskTool(board),
		"check_tasks":   checkTasksTool(board),
		"wait_task":     waitTaskTool(board),
		"declare_done":  declareDoneTool(),
	}
}

// RunMemberFn spawns a member agent loop and returns the final output.
// Blocks until the member completes or the context is cancelled.
type RunMemberFn func(ctx context.Context, memberName, taskDescription string) (string, error)

func assignTaskTool(board *Board, runMember RunMemberFn) *tool.Tool {
	return &tool.Tool{
		Key:  "assign_task",
		Name: "assign_task",
		Execute: func(ctx context.Context, args map[string]any, env tool.Env) (string, error) {
			member := asStr(args, "member")
			task := asStr(args, "task")
			if member == "" || task == "" {
				return "Error: 'member' and 'task' are required", nil
			}

			t := board.Create(member, task)
			board.MarkRunning(t.ID)

			result, err := runMember(ctx, member, task)
			if err != nil {
				board.MarkFailed(t.ID, err.Error())
				return fmt.Sprintf("[%s — %s FAILED]\n%s", t.ID, member, err.Error()), nil
			}

			board.MarkCompleted(t.ID, result)
			return fmt.Sprintf("[%s — %s completed]\n%s", t.ID, member, result), nil
		},
	}
}

func assignAsyncTool(board *Board, runMember RunMemberFn) *tool.Tool {
	return &tool.Tool{
		Key:  "assign_async",
		Name: "assign_async",
		Execute: func(ctx context.Context, args map[string]any, env tool.Env) (string, error) {
			member := asStr(args, "member")
			task := asStr(args, "task")
			if member == "" || task == "" {
				return "Error: 'member' and 'task' are required", nil
			}

			t := board.Create(member, task)

			// Spawn member in background goroutine.
			go func() {
				board.MarkRunning(t.ID)
				result, err := runMember(ctx, member, task)
				if err != nil {
					board.MarkFailed(t.ID, err.Error())
				} else {
					board.MarkCompleted(t.ID, result)
				}
			}()

			return fmt.Sprintf("%s assigned to %s. Use check_task or wait_task to get the result.", t.ID, member), nil
		},
	}
}

func checkTaskTool(board *Board) *tool.Tool {
	return &tool.Tool{
		Key:  "check_task",
		Name: "check_task",
		Execute: func(ctx context.Context, args map[string]any, env tool.Env) (string, error) {
			id := asStr(args, "task_id")
			if id == "" {
				return "Error: 'task_id' is required", nil
			}
			t := board.Get(id)
			if t == nil {
				return fmt.Sprintf("Error: task %q not found", id), nil
			}
			return FormatTask(t), nil
		},
	}
}

func checkTasksTool(board *Board) *tool.Tool {
	return &tool.Tool{
		Key:  "check_tasks",
		Name: "check_tasks",
		Execute: func(ctx context.Context, args map[string]any, env tool.Env) (string, error) {
			return FormatBoard(board.All()), nil
		},
	}
}

func waitTaskTool(board *Board) *tool.Tool {
	return &tool.Tool{
		Key:  "wait_task",
		Name: "wait_task",
		Execute: func(ctx context.Context, args map[string]any, env tool.Env) (string, error) {
			id := asStr(args, "task_id")
			if id == "" {
				return "Error: 'task_id' is required", nil
			}

			t := board.Get(id)
			if t == nil {
				return fmt.Sprintf("Error: task %q not found", id), nil
			}

			// If already terminal, return immediately.
			if isTerminal(t.Status) {
				return FormatTask(t), nil
			}

			// Wait for completion or context cancellation.
			select {
			case <-board.WaitCh(id):
				t = board.Get(id)
				return FormatTask(t), nil
			case <-ctx.Done():
				return "Error: cancelled while waiting for task", ctx.Err()
			}
		},
	}
}

func declareDoneTool() *tool.Tool {
	return &tool.Tool{
		Key:  "declare_done",
		Name: "declare_done",
		Execute: func(ctx context.Context, args map[string]any, env tool.Env) (string, error) {
			summary := asStr(args, "summary")
			if summary == "" {
				summary = "Task completed."
			}
			return tool.SubmitMarker + summary, nil
		},
	}
}

func asStr(args map[string]any, key string) string {
	v, ok := args[key]
	if !ok {
		return ""
	}
	s, ok := v.(string)
	if !ok {
		return fmt.Sprintf("%v", v)
	}
	return s
}

// LeaderToolSchemas returns the OpenAI function schemas for the leader tools.
func LeaderToolSchemas() []string {
	return []string{
		`{"type":"function","function":{"name":"assign_task","description":"Delegate a task to a team member and wait for the result (synchronous).","parameters":{"type":"object","properties":{"member":{"type":"string","description":"Name of the team member"},"task":{"type":"string","description":"Description of what the member should do"}},"required":["member","task"]}}}`,
		`{"type":"function","function":{"name":"assign_async","description":"Delegate a task to a team member in the background (asynchronous). Returns a task ID to check later.","parameters":{"type":"object","properties":{"member":{"type":"string","description":"Name of the team member"},"task":{"type":"string","description":"Description of what the member should do"}},"required":["member","task"]}}}`,
		`{"type":"function","function":{"name":"check_task","description":"Check the status and result of a specific task by ID.","parameters":{"type":"object","properties":{"task_id":{"type":"string","description":"The task ID returned by assign_async"}},"required":["task_id"]}}}`,
		`{"type":"function","function":{"name":"check_tasks","description":"List all tasks and their current status.","parameters":{"type":"object","properties":{}}}}`,
		`{"type":"function","function":{"name":"wait_task","description":"Block until a specific async task completes and return its result.","parameters":{"type":"object","properties":{"task_id":{"type":"string","description":"The task ID to wait for"}},"required":["task_id"]}}}`,
		`{"type":"function","function":{"name":"declare_done","description":"Signal that all work is complete. Stops the leader loop.","parameters":{"type":"object","properties":{"summary":{"type":"string","description":"Brief summary of what was accomplished"}},"required":["summary"]}}}`,
	}
}

// Suppress unused import.
var _ = strings.Builder{}
