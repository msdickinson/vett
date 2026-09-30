// Package tasks is the per-agent-loop task registry: a map of task IDs
// to running sub-agents or async tool invocations.
//
// Phase 1 ships this as a skeleton. The openhands profile is single-agent
// and single-session, so no dispatch tools use the registry. Teams
// profiles in a later phase will populate it. The types live here so
// pkg/tool.Env can carry a *Registry field without pulling in an
// import-cycle tangle.
package tasks

import (
	"context"
	"sync"
	"time"
)

// Registry holds Tasks for one agent loop. Safe for concurrent access.
type Registry struct {
	mu    sync.Mutex
	tasks map[string]*Task
}

func NewRegistry() *Registry {
	return &Registry{tasks: map[string]*Task{}}
}

// Kind enumerates task kinds.
type Kind string

const (
	KindSubAgent Kind = "sub_agent"
	KindTool     Kind = "tool"
)

// State enumerates task lifecycle states.
type State string

const (
	StateRunning   State = "running"
	StateCompleted State = "completed"
	StateFailed    State = "failed"
	StateCancelled State = "cancelled"
)

// Task is one entry in the registry.
type Task struct {
	ID        string
	Kind      Kind
	Role      string
	State     State
	Started   time.Time
	Completed time.Time
	Result    string
	Error     error

	cancel context.CancelFunc
}

// Close cancels any still-running tasks and returns once they've all
// finished. Phase 1 openhands profile never calls Close on a non-empty
// registry because dispatch tools aren't wired up.
func (r *Registry) Close() {
	r.mu.Lock()
	defer r.mu.Unlock()
	for _, t := range r.tasks {
		if t.cancel != nil {
			t.cancel()
		}
	}
	r.tasks = map[string]*Task{}
}
