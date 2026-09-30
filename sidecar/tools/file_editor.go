package tools

import (
	"context"
	"fmt"

	"github.com/msdickinson/vett/sidecar/pkg/tool"
	"github.com/msdickinson/vett/sidecar/pkg/tool/args"
)

func init() {
	tool.Register(&tool.Tool{
		Key:     "file_editor",
		Name:    "file_editor",
		Execute: executeFileEditor,
	})
}

// executeFileEditor dispatches the command to the appropriate sidecar op.
// Openhands file_editor has five commands: view, create, str_replace,
// insert, undo_edit.
func executeFileEditor(ctx context.Context, a map[string]any, env tool.Env) (string, error) {
	if env.Sandbox == nil {
		return "", fmt.Errorf("file_editor requires a sandbox")
	}
	cmd := args.String(a, "command", "")
	path := args.String(a, "path", "")
	if cmd == "" {
		return "Error: missing 'command' argument", nil
	}
	if path == "" {
		return "Error: missing 'path' argument", nil
	}
	switch cmd {
	case "view":
		var rng *[2]int
		if r := args.IntArray(a, "view_range", nil); len(r) == 2 {
			rng = &[2]int{r[0], r[1]}
		}
		res, err := env.Sandbox.FileView(ctx, env.SessionID, path, rng)
		if err != nil {
			// Native sandbox errors become LLM-visible observations.
			return err.Error(), nil
		}
		return res.Content, nil
	case "create":
		fileText := args.String(a, "file_text", "")
		res, err := env.Sandbox.FileCreate(ctx, env.SessionID, path, fileText)
		if err != nil {
			return err.Error(), nil
		}
		return res.Content, nil
	case "str_replace":
		oldStr := args.String(a, "old_str", "")
		newStr := args.String(a, "new_str", "")
		res, toolErr, err := env.Sandbox.FileStrReplace(ctx, env.SessionID, path, oldStr, newStr)
		if err != nil {
			return err.Error(), nil
		}
		if toolErr != "" {
			return toolErr, nil
		}
		return res.Content, nil
	case "insert":
		insertLine := args.Int(a, "insert_line", -1)
		if insertLine < 0 {
			return "Error: insert requires a non-negative 'insert_line' argument", nil
		}
		newStr := args.String(a, "new_str", "")
		res, err := env.Sandbox.FileInsert(ctx, env.SessionID, path, insertLine, newStr)
		if err != nil {
			return err.Error(), nil
		}
		return res.Content, nil
	case "undo_edit":
		res, err := env.Sandbox.FileUndo(ctx, env.SessionID, path)
		if err != nil {
			return err.Error(), nil
		}
		return res.Content, nil
	default:
		return fmt.Sprintf("Error: unknown file_editor command %q", cmd), nil
	}
}
