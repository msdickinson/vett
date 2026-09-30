// Package eventbus is Vett's internal publish/subscribe channel for
// observability events. Publishers (LLM client, agent loop, sandbox,
// runner) call Publish; subscribers (trace writer, progress reporter,
// --live stdout, VSCode extension) attach channels.
//
// Phase 1 ships the minimal bus the agent loop and runner need. Full
// subscriber implementations land in Phase 1d.
package eventbus

import (
	"sync"
	"time"
)

// EventType enumerates the canonical types from docs/trace-format.md.
type EventType string

const (
	RunStart       EventType = "run_start"
	RunEnd         EventType = "run_end"
	InstanceStart  EventType = "instance_start"
	InstanceEnd    EventType = "instance_end"
	IterationStart EventType = "iteration_start"
	IterationEnd   EventType = "iteration_end"
	LLMRequest     EventType = "llm_request"
	LLMResponse    EventType = "llm_response"
	ToolCallStart  EventType = "tool_call_start"
	ToolCallEnd    EventType = "tool_call_end"
	MiddlewareEv   EventType = "middleware_event"
	PatchGenerated EventType = "patch_generated"
	ErrorEvent     EventType = "error"
)

// Event is the in-process representation. The trace writer serializes
// these to JSONL per docs/trace-format.md. Fields beyond Type/Timestamp
// are type-specific and live in Data.
type Event struct {
	Type      EventType
	Timestamp time.Time
	Data      map[string]any
}

// Bus is a minimal fan-out event bus. One publisher, many subscribers.
// Subscribers register a channel with a declared buffer size, lossy
// vs non-lossy semantics, and an optional filter predicate.
type Bus struct {
	mu   sync.RWMutex
	subs []*subscription
}

type subscription struct {
	ch     chan Event
	lossy  bool
	filter Predicate
}

// Predicate decides whether a subscriber receives an event. Return true
// to accept. A nil predicate accepts everything.
type Predicate func(Event) bool

// NewBus returns an empty bus.
func NewBus() *Bus { return &Bus{} }

// Subscribe registers a subscriber that receives every event. If lossy
// is true, Publish drops events on overflow; otherwise Publish blocks.
func (b *Bus) Subscribe(bufSize int, lossy bool) <-chan Event {
	return b.SubscribeFiltered(bufSize, lossy, nil)
}

// SubscribeFiltered is like Subscribe but only delivers events for
// which filter returns true. A nil filter accepts all events.
func (b *Bus) SubscribeFiltered(bufSize int, lossy bool, filter Predicate) <-chan Event {
	b.mu.Lock()
	defer b.mu.Unlock()
	sub := &subscription{ch: make(chan Event, bufSize), lossy: lossy, filter: filter}
	b.subs = append(b.subs, sub)
	return sub.ch
}

// Publish delivers an event to all subscribers whose filter accepts it.
func (b *Bus) Publish(e Event) {
	if e.Timestamp.IsZero() {
		e.Timestamp = time.Now().UTC()
	}
	b.mu.RLock()
	defer b.mu.RUnlock()
	for _, sub := range b.subs {
		if sub.filter != nil && !sub.filter(e) {
			continue
		}
		if sub.lossy {
			select {
			case sub.ch <- e:
			default:
			}
		} else {
			sub.ch <- e
		}
	}
}

// Canonical filters used by the runner.

// RunLevelOnly accepts events that belong in run.trace.jsonl per
// docs/trace-format.md §File layout. These are the events that span
// the whole run: run_start, run_end, instance_start, instance_end.
// Everything else is per-instance and belongs in the instance trace.
func RunLevelOnly() Predicate {
	return func(e Event) bool {
		switch e.Type {
		case RunStart, RunEnd, InstanceStart, InstanceEnd:
			return true
		}
		return false
	}
}

// ForInstance strictly accepts events whose Data["instance_id"] matches
// the given id. Events without an instance_id (including run_start and
// run_end) are rejected. The agent loop must tag every iteration/LLM/
// tool event with instance_id for this to filter correctly — see
// agent.Loop.InstanceID.
func ForInstance(instanceID string) Predicate {
	return func(e Event) bool {
		id, _ := e.Data["instance_id"].(string)
		return id == instanceID
	}
}

// Close drains and closes all subscriber channels. Idempotent.
func (b *Bus) Close() {
	b.mu.Lock()
	defer b.mu.Unlock()
	for _, sub := range b.subs {
		close(sub.ch)
	}
	b.subs = nil
}
