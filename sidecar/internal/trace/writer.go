// Package trace writes JSONL event streams to disk. A Writer subscribes
// to an eventbus.Bus and serializes each event to a file per the schema
// in docs/trace-format.md.
package trace

import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"sync"
	"time"

	"github.com/msdickinson/vett/sidecar/internal/eventbus"
)

// Writer subscribes to a bus and writes each received event as a JSON
// line to the underlying io.Writer. Safe for concurrent publishers; the
// single consumer goroutine serializes writes.
type Writer struct {
	out  io.Writer
	flush func() error
	close func() error
	ch   <-chan eventbus.Event
	done chan struct{}
	mu   sync.Mutex
}

// NewFileWriter opens path for append (creating it if missing), attaches
// a buffered writer, subscribes to bus (non-lossy — trace files are the
// source of truth), and starts a consumer goroutine. If filter is
// non-nil, only events accepted by it are written. Call Close to flush.
func NewFileWriter(path string, bus *eventbus.Bus, filter eventbus.Predicate) (*Writer, error) {
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return nil, fmt.Errorf("open trace file %s: %w", path, err)
	}
	bw := bufio.NewWriter(f)
	w := &Writer{
		out:   bw,
		flush: bw.Flush,
		close: f.Close,
		ch:    bus.SubscribeFiltered(256, false, filter),
		done:  make(chan struct{}),
	}
	go w.loop()
	return w, nil
}

// NewMemoryWriter is a test helper: writes events to the given io.Writer
// without opening a file. Caller owns the lifetime of out.
func NewMemoryWriter(out io.Writer, bus *eventbus.Bus) *Writer {
	w := &Writer{
		out:   out,
		flush: func() error { return nil },
		close: func() error { return nil },
		ch:    bus.Subscribe(256, false),
		done:  make(chan struct{}),
	}
	go w.loop()
	return w
}

func (w *Writer) loop() {
	defer close(w.done)
	for e := range w.ch {
		w.writeEvent(e)
	}
}

func (w *Writer) writeEvent(e eventbus.Event) {
	w.mu.Lock()
	defer w.mu.Unlock()
	line := map[string]any{
		"ts":    e.Timestamp.UTC().Format(time.RFC3339Nano),
		"event": string(e.Type),
		"v":     1,
	}
	for k, v := range e.Data {
		line[k] = v
	}
	b, err := json.Marshal(line)
	if err != nil {
		return
	}
	_, _ = w.out.Write(b)
	_, _ = w.out.Write([]byte("\n"))
}

// Close flushes pending writes and closes the underlying file. Safe to
// call multiple times.
func (w *Writer) Close() error {
	// Bus close will drain our channel; but we don't own the bus, so
	// just wait for our loop to catch up. The bus owner is expected to
	// call bus.Close() eventually, which will close our channel and let
	// our goroutine exit.
	w.mu.Lock()
	defer w.mu.Unlock()
	_ = w.flush()
	return w.close()
}

// Done returns a channel that closes when the consumer goroutine exits
// (after the bus has closed our subscription channel).
func (w *Writer) Done() <-chan struct{} { return w.done }
