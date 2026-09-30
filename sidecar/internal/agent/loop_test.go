package agent

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"

	"github.com/msdickinson/vett/sidecar/internal/llm"
	"github.com/msdickinson/vett/sidecar/pkg/rpc"
	mw "github.com/msdickinson/vett/sidecar/pkg/middleware"
	"github.com/msdickinson/vett/sidecar/pkg/tool"
)

// fakeSandbox satisfies tool.Sandbox for the agent loop tests.
// BashExec is scripted: the first call returns a timeout; the second
// returns a clean result. Every other method is a no-op.
type fakeSandbox struct {
	bashCalls int32
	timedOut  bool
}

func (f *fakeSandbox) BashExec(ctx context.Context, sessionID, command string, timeout time.Duration, onChunk func(string)) (*rpc.BashExecResult, error) {
	n := atomic.AddInt32(&f.bashCalls, 1)
	if n == 1 {
		f.timedOut = true
		return &rpc.BashExecResult{
			Stdout: "partial output before hang\n", ExitCode: -1, Cwd: "/testbed", TimedOut: true,
		}, nil
	}
	return &rpc.BashExecResult{Stdout: "ok\n", ExitCode: 0, Cwd: "/testbed"}, nil
}
func (f *fakeSandbox) FileView(ctx context.Context, sessionID, path string, viewRange *[2]int) (*rpc.FileViewResult, error) {
	return &rpc.FileViewResult{}, nil
}
func (f *fakeSandbox) FileCreate(ctx context.Context, sessionID, path, fileText string) (*rpc.FileCreateResult, error) {
	return &rpc.FileCreateResult{}, nil
}
func (f *fakeSandbox) FileStrReplace(ctx context.Context, sessionID, path, oldStr, newStr string) (*rpc.FileStrReplaceResult, string, error) {
	return &rpc.FileStrReplaceResult{}, "", nil
}
func (f *fakeSandbox) FileInsert(ctx context.Context, sessionID, path string, insertLine int, newStr string) (*rpc.FileInsertResult, error) {
	return &rpc.FileInsertResult{}, nil
}
func (f *fakeSandbox) FileUndo(ctx context.Context, sessionID, path string) (*rpc.FileUndoResult, error) {
	return &rpc.FileUndoResult{}, nil
}

// scriptedLLM is an httptest server that emits canned responses for
// /chat/completions. Each call returns the next response in the list.
func scriptedLLM(t *testing.T, responses []string) (*httptest.Server, *int32) {
	t.Helper()
	var n int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		i := int(atomic.AddInt32(&n, 1)) - 1
		if i >= len(responses) {
			http.Error(w, "out of scripted responses", 500)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(responses[i]))
	}))
	return srv, &n
}

// terminalTool is a minimal inline terminal tool for the loop test —
// avoids importing the tools package (which would pull in all the
// middleware init too). Matches the real terminal tool's envelope.
func terminalTool() *tool.Tool {
	return &tool.Tool{
		Key:  "terminal",
		Name: "terminal",
		Execute: func(ctx context.Context, args map[string]any, env tool.Env) (string, error) {
			cmd, _ := args["command"].(string)
			res, err := env.Sandbox.BashExec(ctx, env.SessionID, cmd, 5*time.Second, nil)
			if err != nil {
				return "", err
			}
			envelope := ""
			if res.TimedOut {
				envelope += tool.TimeoutObservationPrefix + "5s.\n"
			}
			envelope += res.Stdout
			return envelope, nil
		},
	}
}

// TestLoopRecoverSessionOnTimeout — the load-bearing fix-4 check. After
// the first iteration's terminal tool returns a TimedOut result,
// the agent loop must invoke RecoverSession before running the next
// iteration.
func TestLoopRecoverSessionOnTimeout(t *testing.T) {
	// Two LLM responses:
	//  1. terminal(command="sleep 100")  — sandbox will timeout
	//  2. terminal(command="echo ok")    — sandbox returns clean
	//     then no-op finishes by hitting max_iterations=2.
	resp := func(callID, argsJSON string) string {
		body := map[string]any{
			"id": "chatcmpl-" + callID,
			"choices": []any{
				map[string]any{
					"index":         0,
					"finish_reason": "tool_calls",
					"message": map[string]any{
						"role":    "assistant",
						"content": "",
						"tool_calls": []any{
							map[string]any{
								"id":   callID,
								"type": "function",
								"function": map[string]any{
									"name":      "terminal",
									"arguments": argsJSON,
								},
							},
						},
					},
				},
			},
			"usage": map[string]any{"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15},
		}
		b, _ := json.Marshal(body)
		return string(b)
	}

	srv, _ := scriptedLLM(t, []string{
		resp("call-1", `{"command":"sleep 100"}`),
		resp("call-2", `{"command":"echo ok"}`),
	})
	defer srv.Close()

	fs := &fakeSandbox{}
	var recoverCount int32
	recover := func(ctx context.Context) error {
		atomic.AddInt32(&recoverCount, 1)
		return nil
	}

	loop := &Loop{
		Client:         llm.NewClient(srv.URL, ""),
		Model:          "test-model",
		Sandbox:        fs,
		SessionID:      "agent",
		Tools:          map[string]*tool.Tool{"terminal": terminalTool()},
		WireTools:      nil,
		Middlewares:    []mw.Middleware{},
		Temperature:    1.0,
		TopP:           0.95,
		MaxIterations:  2,
		RecoverSession: recover,
	}

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	result, err := loop.Run(ctx, "system prompt", "user message")
	if err != nil {
		t.Fatalf("loop.Run: %v", err)
	}
	if result.Iterations != 2 {
		t.Errorf("expected 2 iterations, got %d", result.Iterations)
	}
	if atomic.LoadInt32(&recoverCount) != 1 {
		t.Errorf("expected RecoverSession to be called exactly once, got %d", recoverCount)
	}
	if atomic.LoadInt32(&fs.bashCalls) != 2 {
		t.Errorf("expected 2 bash calls, got %d", fs.bashCalls)
	}
}

// TestLoopNoRecoverWhenNoTimeout — the recover hook is NOT invoked
// when all observations are clean. Guards against over-eager recovery.
func TestLoopNoRecoverWhenNoTimeout(t *testing.T) {
	resp := func(callID string) string {
		body := map[string]any{
			"id": "chatcmpl-" + callID,
			"choices": []any{
				map[string]any{
					"index":         0,
					"finish_reason": "tool_calls",
					"message": map[string]any{
						"role":    "assistant",
						"content": "",
						"tool_calls": []any{
							map[string]any{
								"id":   callID,
								"type": "function",
								"function": map[string]any{
									"name":      "terminal",
									"arguments": `{"command":"echo hi"}`,
								},
							},
						},
					},
				},
			},
			"usage": map[string]any{"prompt_tokens": 5, "completion_tokens": 2},
		}
		b, _ := json.Marshal(body)
		return string(b)
	}
	srv, _ := scriptedLLM(t, []string{resp("c1")})
	defer srv.Close()

	// Sandbox never times out.
	fs := &cleanSandbox{}
	var recoverCount int32
	loop := &Loop{
		Client:         llm.NewClient(srv.URL, ""),
		Model:          "test-model",
		Sandbox:        fs,
		SessionID:      "agent",
		Tools:          map[string]*tool.Tool{"terminal": terminalTool()},
		MaxIterations:  1,
		Temperature:    1.0,
		TopP:           0.95,
		RecoverSession: func(ctx context.Context) error { atomic.AddInt32(&recoverCount, 1); return nil },
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if _, err := loop.Run(ctx, "sys", "user"); err != nil {
		t.Fatal(err)
	}
	if atomic.LoadInt32(&recoverCount) != 0 {
		t.Errorf("RecoverSession should not have been called, got %d", recoverCount)
	}
}

// TestLoopRecoverSessionOnConsecutiveFailures — the silent session
// death path. BashExec returns an error on every call (sandbox's
// session is dead). The terminal tool wraps that into an
// Observation.Success=false, and after
// ConsecutiveTerminalFailureThreshold iterations the loop must
// trigger RecoverSession.
func TestLoopRecoverSessionOnConsecutiveFailures(t *testing.T) {
	// Script the LLM to issue a terminal call every iteration so we
	// see the failure path.
	resp := func(callID string) string {
		body := map[string]any{
			"id": "chatcmpl-" + callID,
			"choices": []any{
				map[string]any{
					"index":         0,
					"finish_reason": "tool_calls",
					"message": map[string]any{
						"role":    "assistant",
						"content": "",
						"tool_calls": []any{
							map[string]any{
								"id":   callID,
								"type": "function",
								"function": map[string]any{
									"name":      "terminal",
									"arguments": `{"command":"echo test"}`,
								},
							},
						},
					},
				},
			},
			"usage": map[string]any{"prompt_tokens": 5, "completion_tokens": 2},
		}
		b, _ := json.Marshal(body)
		return string(b)
	}
	srv, _ := scriptedLLM(t, []string{
		resp("c1"), resp("c2"), resp("c3"), resp("c4"),
	})
	defer srv.Close()

	// deadSandbox: BashExec always returns an error (session dead).
	fs := &deadSandbox{}
	var recoverCount int32
	loop := &Loop{
		Client:         llm.NewClient(srv.URL, ""),
		Model:          "test-model",
		Sandbox:        fs,
		SessionID:      "agent",
		Tools:          map[string]*tool.Tool{"terminal": terminalTool()},
		MaxIterations:  4,
		Temperature:    1.0,
		TopP:           0.95,
		RecoverSession: func(ctx context.Context) error {
			atomic.AddInt32(&recoverCount, 1)
			return nil
		},
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if _, err := loop.Run(ctx, "sys", "user"); err != nil {
		t.Fatal(err)
	}
	// With threshold=2, we expect recovery at least once after
	// iterations 2 (first two failed). The counter should reset
	// after recovery — if recovery itself doesn't restore the
	// session (dead sandbox keeps returning errors), it'll trigger
	// again at iteration 4. Accept any count >= 1.
	got := atomic.LoadInt32(&recoverCount)
	if got < 1 {
		t.Errorf("expected at least 1 recovery, got %d", got)
	}
}

// deadSandbox: every BashExec returns an error, simulating silent
// session death where the sidecar's bash child has exited but no
// timeout prefix was emitted.
type deadSandbox struct{}

func (deadSandbox) BashExec(ctx context.Context, sessionID, command string, timeout time.Duration, onChunk func(string)) (*rpc.BashExecResult, error) {
	return nil, errors.New("bash session is dead")
}
func (deadSandbox) FileView(ctx context.Context, sessionID, path string, viewRange *[2]int) (*rpc.FileViewResult, error) {
	return &rpc.FileViewResult{}, nil
}
func (deadSandbox) FileCreate(ctx context.Context, sessionID, path, fileText string) (*rpc.FileCreateResult, error) {
	return &rpc.FileCreateResult{}, nil
}
func (deadSandbox) FileStrReplace(ctx context.Context, sessionID, path, oldStr, newStr string) (*rpc.FileStrReplaceResult, string, error) {
	return &rpc.FileStrReplaceResult{}, "", nil
}
func (deadSandbox) FileInsert(ctx context.Context, sessionID, path string, insertLine int, newStr string) (*rpc.FileInsertResult, error) {
	return &rpc.FileInsertResult{}, nil
}
func (deadSandbox) FileUndo(ctx context.Context, sessionID, path string) (*rpc.FileUndoResult, error) {
	return &rpc.FileUndoResult{}, nil
}

type cleanSandbox struct{}

func (cleanSandbox) BashExec(ctx context.Context, sessionID, command string, timeout time.Duration, onChunk func(string)) (*rpc.BashExecResult, error) {
	return &rpc.BashExecResult{Stdout: "hi\n", ExitCode: 0, Cwd: "/testbed"}, nil
}
func (cleanSandbox) FileView(ctx context.Context, sessionID, path string, viewRange *[2]int) (*rpc.FileViewResult, error) {
	return &rpc.FileViewResult{}, nil
}
func (cleanSandbox) FileCreate(ctx context.Context, sessionID, path, fileText string) (*rpc.FileCreateResult, error) {
	return &rpc.FileCreateResult{}, nil
}
func (cleanSandbox) FileStrReplace(ctx context.Context, sessionID, path, oldStr, newStr string) (*rpc.FileStrReplaceResult, string, error) {
	return &rpc.FileStrReplaceResult{}, "", nil
}
func (cleanSandbox) FileInsert(ctx context.Context, sessionID, path string, insertLine int, newStr string) (*rpc.FileInsertResult, error) {
	return &rpc.FileInsertResult{}, nil
}
func (cleanSandbox) FileUndo(ctx context.Context, sessionID, path string) (*rpc.FileUndoResult, error) {
	return &rpc.FileUndoResult{}, nil
}
