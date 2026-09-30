// Package middleware is Vett's message-pipeline contract. Each middleware
// in a profile's chain inspects and mutates AgentState between LLM turns.
//
// See docs/middleware-interface.md for the canonical spec.
package middleware

import (
	"context"
	"sync"

	"github.com/msdickinson/vett/sidecar/internal/llm"
)

// Middleware is one stage in the agent loop's message pipeline.
type Middleware interface {
	// Name returns a stable identifier, used in trace events and in
	// profile YAML.
	Name() string

	// Process runs once per iteration, after tool dispatch and before
	// the next LLM call. It may mutate state.Messages, set StopLoop,
	// or publish events.
	Process(ctx context.Context, state *AgentState) error
}

// AgentState is the shared state every middleware in a chain receives.
type AgentState struct {
	// Messages is the full conversation so far, mutable.
	Messages []llm.Message

	// LastToolCalls are the tool calls emitted by the LLM in the most
	// recent response. Empty on the first iteration.
	LastToolCalls []llm.ToolCall

	// LastObservations are the tool results produced by the dispatcher
	// for LastToolCalls. Populated before middleware runs.
	LastObservations []Observation

	// Iteration is the 1-indexed iteration counter.
	Iteration int

	// MaxIterations is the hard cap from the profile.
	MaxIterations int

	// StopLoop, if true at the end of the chain, terminates the loop.
	StopLoop bool

	// StopReason is a human-readable label for why StopLoop was set.
	StopReason string

	// Depth is the reentrant loop depth. 0 for the top-level loop.
	Depth int

	// Token accounting, populated by the agent loop from LLM responses.
	InputTokensLastIter  int
	OutputTokensLastIter int
	TotalInputTokens     int
	TotalOutputTokens    int
}

// Observation is one tool result paired with its originating tool call.
type Observation struct {
	ToolCallID string
	ToolName   string
	Result     string
	Success    bool
	Error      error
}

// ---------------- registry ----------------

// Factory constructs a middleware from its profile-config block.
type Factory func(config map[string]any) Middleware

var (
	registryMu sync.Mutex
	registry   = map[string]Factory{}
)

// Register installs a middleware factory in the global registry. Call
// from an init function. Panics on duplicates.
func Register(name string, f Factory) {
	registryMu.Lock()
	defer registryMu.Unlock()
	if _, exists := registry[name]; exists {
		panic("middleware: duplicate name: " + name)
	}
	registry[name] = f
}

// Lookup fetches a middleware factory by name. Returns nil if not found.
func Lookup(name string) Factory {
	registryMu.Lock()
	defer registryMu.Unlock()
	return registry[name]
}
