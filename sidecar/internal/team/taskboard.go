// Package team implements the multi-agent orchestration system.
// A leader agent delegates tasks to member agents via built-in tools.
// Each member runs its own agent loop in a separate goroutine with
// its own sandbox session.
package team

import (
	"fmt"
	"strings"
	"sync"
	"time"

	"github.com/msdickinson/vett/sidecar/internal/agent"
)

// TaskStatus tracks the lifecycle of a delegated task.
type TaskStatus string

const (
	StatusPending   TaskStatus = "pending"
	StatusRunning   TaskStatus = "running"
	StatusCompleted TaskStatus = "completed"
	StatusFailed    TaskStatus = "failed"
	StatusTimedOut  TaskStatus = "timed_out"
)

// Task is one delegated unit of work on the board.
type Task struct {
	ID          string
	MemberName  string
	Description string
	Status      TaskStatus
	Result      string
	Error       string
	CreatedAt   time.Time
	StartedAt   *time.Time
	CompletedAt *time.Time
}

// Board is the thread-safe shared state between the leader and member
// agents. The leader writes tasks via tools; members update status
// as they run. All methods are safe for concurrent use.
type Board struct {
	tasks   map[string]*Task
	waiters map[string][]chan struct{} // task ID → channels to signal on completion
	mu      sync.Mutex
	nextID  int
}

// NewBoard creates an empty task board.
func NewBoard() *Board {
	return &Board{
		tasks:   make(map[string]*Task),
		waiters: make(map[string][]chan struct{}),
	}
}

// Create adds a new task in Pending status.
func (b *Board) Create(memberName, description string) *Task {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.nextID++
	id := fmt.Sprintf("task-%d", b.nextID)
	t := &Task{
		ID:          id,
		MemberName:  memberName,
		Description: description,
		Status:      StatusPending,
		CreatedAt:   time.Now().UTC(),
	}
	b.tasks[id] = t
	return t
}

// MarkRunning transitions a task to Running.
func (b *Board) MarkRunning(id string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if t, ok := b.tasks[id]; ok {
		t.Status = StatusRunning
		now := time.Now().UTC()
		t.StartedAt = &now
	}
}

// MarkCompleted transitions a task to Completed with a result.
func (b *Board) MarkCompleted(id, result string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if t, ok := b.tasks[id]; ok {
		t.Status = StatusCompleted
		t.Result = result
		now := time.Now().UTC()
		t.CompletedAt = &now
	}
	b.notifyWaiters(id)
}

// MarkFailed transitions a task to Failed with an error.
func (b *Board) MarkFailed(id, errMsg string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if t, ok := b.tasks[id]; ok {
		t.Status = StatusFailed
		t.Error = errMsg
		now := time.Now().UTC()
		t.CompletedAt = &now
	}
	b.notifyWaiters(id)
}

// Get returns a task by ID, or nil if not found.
func (b *Board) Get(id string) *Task {
	b.mu.Lock()
	defer b.mu.Unlock()
	t := b.tasks[id]
	if t == nil {
		return nil
	}
	// Return a copy to avoid races.
	copy := *t
	return &copy
}

// All returns a snapshot of all tasks.
func (b *Board) All() []*Task {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]*Task, 0, len(b.tasks))
	for _, t := range b.tasks {
		copy := *t
		out = append(out, &copy)
	}
	return out
}

// WaitCh returns a channel that closes when the given task reaches a
// terminal state (Completed, Failed, or TimedOut).
func (b *Board) WaitCh(id string) <-chan struct{} {
	b.mu.Lock()
	defer b.mu.Unlock()

	// Already done?
	if t, ok := b.tasks[id]; ok && isTerminal(t.Status) {
		ch := make(chan struct{})
		close(ch)
		return ch
	}

	ch := make(chan struct{})
	b.waiters[id] = append(b.waiters[id], ch)
	return ch
}

// notifyWaiters signals all channels waiting on a task. Must be called
// with the lock held.
func (b *Board) notifyWaiters(id string) {
	for _, ch := range b.waiters[id] {
		close(ch)
	}
	delete(b.waiters, id)
}

func isTerminal(s TaskStatus) bool {
	return s == StatusCompleted || s == StatusFailed || s == StatusTimedOut
}

// FormatTask returns a human-readable summary of one task.
func FormatTask(t *Task) string {
	var sb strings.Builder
	fmt.Fprintf(&sb, "[%s] %s — %s\n", t.ID, t.MemberName, t.Status)
	fmt.Fprintf(&sb, "  Description: %s\n", t.Description)
	if t.Status == StatusCompleted && t.Result != "" {
		result := t.Result
		if len(result) > 500 {
			result = result[:500] + "...[truncated]"
		}
		fmt.Fprintf(&sb, "  Result: %s\n", result)
	}
	if t.Status == StatusFailed && t.Error != "" {
		fmt.Fprintf(&sb, "  Error: %s\n", t.Error)
	}
	return sb.String()
}

// FormatBoard returns a summary of all tasks.
func FormatBoard(tasks []*Task) string {
	if len(tasks) == 0 {
		return "No tasks on the board."
	}
	var sb strings.Builder
	for _, t := range tasks {
		sb.WriteString(FormatTask(t))
		sb.WriteString("\n")
	}
	return sb.String()
}

// MemberRunner is the function signature for running a member agent.
// The team coordinator provides this; it spawns an agent loop for the
// member and returns the final output.
type MemberRunner func(memberName, taskDescription string, result *agent.Result) error
