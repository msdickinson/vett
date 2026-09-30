package tools

import (
	"context"
	"strings"
	"testing"
	"time"

	"github.com/msdickinson/vett/sidecar/pkg/rpc"
	"github.com/msdickinson/vett/sidecar/pkg/tool"
)

// fakeSandbox satisfies tool.Sandbox with canned responses.
type fakeSandbox struct {
	bashResult     *rpc.BashExecResult
	bashErr        error
	viewResult     *rpc.FileViewResult
	createResult   *rpc.FileCreateResult
	replaceResult  *rpc.FileStrReplaceResult
	replaceToolErr string
	insertResult   *rpc.FileInsertResult
	insertCalls    []insertCall
	undoResult     *rpc.FileUndoResult
	undoCalls      []undoCall
	bashCalls      []bashCall
}

type insertCall struct {
	path       string
	insertLine int
	newStr     string
}

type undoCall struct {
	path string
}

type bashCall struct {
	sessionID string
	command   string
	timeout   time.Duration
}

func (f *fakeSandbox) BashExec(ctx context.Context, sessionID, command string, timeout time.Duration, onChunk func(string)) (*rpc.BashExecResult, error) {
	f.bashCalls = append(f.bashCalls, bashCall{sessionID, command, timeout})
	return f.bashResult, f.bashErr
}
func (f *fakeSandbox) FileView(ctx context.Context, sessionID, path string, viewRange *[2]int) (*rpc.FileViewResult, error) {
	return f.viewResult, nil
}
func (f *fakeSandbox) FileCreate(ctx context.Context, sessionID, path, fileText string) (*rpc.FileCreateResult, error) {
	return f.createResult, nil
}
func (f *fakeSandbox) FileStrReplace(ctx context.Context, sessionID, path, oldStr, newStr string) (*rpc.FileStrReplaceResult, string, error) {
	return f.replaceResult, f.replaceToolErr, nil
}
func (f *fakeSandbox) FileInsert(ctx context.Context, sessionID, path string, insertLine int, newStr string) (*rpc.FileInsertResult, error) {
	f.insertCalls = append(f.insertCalls, insertCall{path, insertLine, newStr})
	if f.insertResult == nil {
		return &rpc.FileInsertResult{Content: "inserted"}, nil
	}
	return f.insertResult, nil
}
func (f *fakeSandbox) FileUndo(ctx context.Context, sessionID, path string) (*rpc.FileUndoResult, error) {
	f.undoCalls = append(f.undoCalls, undoCall{path})
	if f.undoResult == nil {
		return &rpc.FileUndoResult{Content: "undone"}, nil
	}
	return f.undoResult, nil
}

func TestTerminalSuccessEnvelope(t *testing.T) {
	fs := &fakeSandbox{bashResult: &rpc.BashExecResult{
		Stdout: "hello\n", ExitCode: 0, Cwd: "/testbed",
	}}
	env := tool.Env{Sandbox: fs, SessionID: "agent"}
	out, err := executeTerminal(context.Background(), map[string]any{"command": "echo hello"}, env)
	if err != nil {
		t.Fatalf("execute: %v", err)
	}
	if !strings.HasPrefix(out, "hello\n[Current working directory: /testbed]\n[Command finished with exit code 0]") {
		t.Errorf("envelope wrong:\n%s", out)
	}
	if len(fs.bashCalls) != 1 {
		t.Fatalf("expected 1 bash call, got %d", len(fs.bashCalls))
	}
	if fs.bashCalls[0].command != "echo hello" {
		t.Errorf("raw passthrough failed: got %q", fs.bashCalls[0].command)
	}
}

func TestTerminalTimeoutEnvelope(t *testing.T) {
	fs := &fakeSandbox{bashResult: &rpc.BashExecResult{
		Stdout: "partial\n", ExitCode: -1, Cwd: "/testbed", TimedOut: true,
	}}
	env := tool.Env{Sandbox: fs, SessionID: "agent"}
	out, err := executeTerminal(context.Background(), map[string]any{"command": "sleep 100", "timeout": float64(2)}, env)
	if err != nil {
		t.Fatalf("execute: %v", err)
	}
	if !strings.HasPrefix(out, "Error: Command timed out after 2s.\npartial\n") {
		t.Errorf("timeout envelope wrong:\n%s", out)
	}
	if !strings.Contains(out, "[Command finished with exit code -1]") {
		t.Errorf("expected exit code -1 in envelope:\n%s", out)
	}
}

func TestFinishEmitsSubmitMarker(t *testing.T) {
	env := tool.Env{}
	out, err := executeFinish(context.Background(), map[string]any{"message": "all tests pass"}, env)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(out, SubmitMarker) {
		t.Errorf("expected finish output to start with SubmitMarker, got %q", out)
	}
	if !strings.Contains(out, "all tests pass") {
		t.Errorf("expected message echoed, got %q", out)
	}
}

func TestThinkReturnsAck(t *testing.T) {
	env := tool.Env{}
	out, err := executeThink(context.Background(), map[string]any{"thought": "I think"}, env)
	if err != nil {
		t.Fatal(err)
	}
	if out == "" {
		t.Error("expected non-empty observation")
	}
}

func TestFileEditorCreateDispatches(t *testing.T) {
	fs := &fakeSandbox{createResult: &rpc.FileCreateResult{Content: "File created successfully at: /tmp/x"}}
	env := tool.Env{Sandbox: fs, SessionID: "agent"}
	out, err := executeFileEditor(context.Background(), map[string]any{
		"command": "create", "path": "/tmp/x", "file_text": "hi",
	}, env)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out, "File created successfully") {
		t.Errorf("create dispatch failed: %q", out)
	}
}

func TestFileEditorStrReplaceToolErrorPassesThrough(t *testing.T) {
	fs := &fakeSandbox{replaceToolErr: "No replacement was performed, old_str `foo` did not appear verbatim"}
	env := tool.Env{Sandbox: fs, SessionID: "agent"}
	out, err := executeFileEditor(context.Background(), map[string]any{
		"command": "str_replace", "path": "/tmp/x", "old_str": "foo", "new_str": "bar",
	}, env)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out, "did not appear verbatim") {
		t.Errorf("tool error should pass through as observation, got %q", out)
	}
}

func TestFileEditorInsertDispatches(t *testing.T) {
	fs := &fakeSandbox{insertResult: &rpc.FileInsertResult{Content: "The file /tmp/x has been edited."}}
	env := tool.Env{Sandbox: fs, SessionID: "agent"}
	out, err := executeFileEditor(context.Background(), map[string]any{
		"command":     "insert",
		"path":        "/tmp/x",
		"insert_line": float64(3),
		"new_str":     "new line\n",
	}, env)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out, "has been edited") {
		t.Errorf("insert dispatch did not return sandbox content: %q", out)
	}
	if len(fs.insertCalls) != 1 {
		t.Fatalf("expected 1 insert call, got %d", len(fs.insertCalls))
	}
	if fs.insertCalls[0].insertLine != 3 {
		t.Errorf("insertLine should be 3, got %d", fs.insertCalls[0].insertLine)
	}
	if fs.insertCalls[0].newStr != "new line\n" {
		t.Errorf("newStr wrong: %q", fs.insertCalls[0].newStr)
	}
}

func TestFileEditorInsertRejectsMissingLine(t *testing.T) {
	env := tool.Env{Sandbox: &fakeSandbox{}, SessionID: "agent"}
	out, err := executeFileEditor(context.Background(), map[string]any{
		"command": "insert",
		"path":    "/tmp/x",
		"new_str": "foo",
	}, env)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out, "insert_line") {
		t.Errorf("expected missing-line error, got %q", out)
	}
}

func TestFileEditorUndoEditDispatches(t *testing.T) {
	fs := &fakeSandbox{undoResult: &rpc.FileUndoResult{Content: "Last edit to /tmp/x undone successfully."}}
	env := tool.Env{Sandbox: fs, SessionID: "agent"}
	out, err := executeFileEditor(context.Background(), map[string]any{
		"command": "undo_edit",
		"path":    "/tmp/x",
	}, env)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out, "undone successfully") {
		t.Errorf("undo_edit dispatch did not return sandbox content: %q", out)
	}
	if len(fs.undoCalls) != 1 {
		t.Fatalf("expected 1 undo call, got %d", len(fs.undoCalls))
	}
}

func TestFileEditorUnknownCommand(t *testing.T) {
	env := tool.Env{Sandbox: &fakeSandbox{}, SessionID: "agent"}
	out, _ := executeFileEditor(context.Background(), map[string]any{
		"command": "invalid", "path": "/tmp/x",
	}, env)
	if !strings.Contains(out, "unknown file_editor command") {
		t.Errorf("expected unknown-command message, got %q", out)
	}
}

func TestFileEditorMissingArgs(t *testing.T) {
	env := tool.Env{Sandbox: &fakeSandbox{}, SessionID: "agent"}
	out, _ := executeFileEditor(context.Background(), map[string]any{}, env)
	if !strings.Contains(out, "missing 'command'") {
		t.Errorf("expected missing-command error, got %q", out)
	}
}

func TestAllOpenHandsToolsRegistered(t *testing.T) {
	for _, name := range []string{"terminal", "file_editor", "task_tracker", "finish", "think"} {
		if tool.Lookup(name) == nil {
			t.Errorf("expected %q to be registered", name)
		}
	}
}
