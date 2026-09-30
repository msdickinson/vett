// Package tool is Vett's unit-of-action type. Every built-in tool
// (terminal, file_editor, think, finish, task_tracker, and the dispatch
// tools for future teams profiles) implements this interface and
// registers itself at package init time.
//
// See docs/tool-interface.md for the canonical spec.
package tool

import (
	"context"
	"sync"
	"time"

	"github.com/msdickinson/vett/sidecar/pkg/rpc"
)

// TimeoutObservationPrefix is the load-bearing prefix the terminal tool
// places on its observation when BashExec reports TimedOut. The agent
// loop matches on this literal to trigger SessionRecoverFn between
// iterations. Both ends (tools/terminal.go and internal/agent/loop.go)
// must use this same constant.
const TimeoutObservationPrefix = "Error: Command timed out after "

// SubmitMarker is the load-bearing prefix the finish tool places on
// its observation when the agent calls finish. The submit_detector
// middleware matches on this to terminate the agent loop cleanly.
// Lives here (pkg/tool) instead of tools/ so middleware can depend
// on it without importing the entire tools package.
const SubmitMarker = "__VETT_SUBMIT__"

// ExecuteFn runs a tool invocation and returns its observation string
// (what the LLM sees on the next turn) plus an error.
//
// Return semantics:
//   - (result, nil)            — tool ran, LLM sees result as observation
//   - (errorMsg, nil)          — tool ran, operation failed in a way the
//                                LLM should see (e.g. file-not-found)
//   - ("", err)                — infrastructure error; agent loop handles
type ExecuteFn func(ctx context.Context, args map[string]any, env Env) (string, error)

// Tool is the unit the LLM invokes. Pointer registration so the registry
// can share instances across profiles without copying.
type Tool struct {
	// Key is the unique identifier in the global registry. Must be
	// globally unique; duplicates panic at init.
	Key string

	// Name is what the LLM sees in the tool schema (the wire name).
	// Defaults to Key when empty.
	Name string

	// Description is optional Go-side documentation. The wire schema
	// shipped to the LLM for the openhands profile comes from embedded
	// JSON (internal/llm/schemas/*.json), not from this field.
	Description string

	// Execute is the callback the agent loop invokes on dispatch.
	Execute ExecuteFn

	// SupportsAsync declares whether this tool can be dispatched via
	// the task registry (future feature). Openhands profile ignores this.
	SupportsAsync bool
}

// Env carries capabilities available to a tool during its Execute call.
type Env struct {
	// Sandbox is the interface to the in-container sidecar. Terminal +
	// file_editor tools call into it; think/finish/task_tracker don't.
	Sandbox Sandbox

	// SessionID is the sidecar session this tool call runs in. Always
	// non-empty. Openhands profile always uses the same session.
	SessionID string

	// CallID is the unique ID for this tool invocation; used for trace
	// correlation.
	CallID string

	// Depth is the reentrant dispatch depth. 0 for the top-level loop.
	// Non-zero only for sub-agent dispatches (future).
	Depth int
}

// Sandbox is the subset of sandbox operations tools need. Defined as an
// interface here so pkg/tool doesn't import internal/sandbox (internal/
// packages can't be imported from pkg/).
type Sandbox interface {
	BashExec(ctx context.Context, sessionID, command string, timeout time.Duration, onChunk func(string)) (*rpc.BashExecResult, error)
	FileView(ctx context.Context, sessionID, path string, viewRange *[2]int) (*rpc.FileViewResult, error)
	FileCreate(ctx context.Context, sessionID, path, fileText string) (*rpc.FileCreateResult, error)
	FileStrReplace(ctx context.Context, sessionID, path, oldStr, newStr string) (*rpc.FileStrReplaceResult, string, error)
	FileInsert(ctx context.Context, sessionID, path string, insertLine int, newStr string) (*rpc.FileInsertResult, error)
	FileUndo(ctx context.Context, sessionID, path string) (*rpc.FileUndoResult, error)
}

// ---------------- registry ----------------

var (
	registryMu sync.Mutex
	registry   = map[string]*Tool{}
)

// Register installs a tool in the global registry. Call from an init
// function in the tool's source file. Panics on duplicate key, missing
// Execute, or missing Key — these are build-time bugs.
func Register(t *Tool) {
	registryMu.Lock()
	defer registryMu.Unlock()
	if t.Key == "" {
		panic("tool: Key is required")
	}
	if t.Execute == nil {
		panic("tool: Execute is required for " + t.Key)
	}
	if t.Name == "" {
		t.Name = t.Key
	}
	if _, exists := registry[t.Key]; exists {
		panic("tool: duplicate key: " + t.Key)
	}
	registry[t.Key] = t
}

// Lookup fetches a registered tool by Key. Returns nil if not found.
func Lookup(key string) *Tool {
	registryMu.Lock()
	defer registryMu.Unlock()
	return registry[key]
}

// All returns a copy of the registry keyed by Key.
func All() map[string]*Tool {
	registryMu.Lock()
	defer registryMu.Unlock()
	out := make(map[string]*Tool, len(registry))
	for k, v := range registry {
		out[k] = v
	}
	return out
}
