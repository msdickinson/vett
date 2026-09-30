package trace

import (
	"bytes"
	"encoding/json"
	"testing"
	"time"

	"github.com/msdickinson/vett/sidecar/internal/eventbus"
)

func TestWriterEmitsOneLinePerEvent(t *testing.T) {
	bus := eventbus.NewBus()
	var buf bytes.Buffer
	w := NewMemoryWriter(&buf, bus)

	bus.Publish(eventbus.Event{
		Type: eventbus.RunStart,
		Data: map[string]any{"run_id": "r1", "suite": "test"},
	})
	bus.Publish(eventbus.Event{
		Type: eventbus.IterationStart,
		Data: map[string]any{"iteration": 1},
	})
	bus.Publish(eventbus.Event{
		Type: eventbus.RunEnd,
		Data: map[string]any{"run_id": "r1", "completed": 1},
	})

	// Give the consumer goroutine time to drain, then close the bus.
	time.Sleep(50 * time.Millisecond)
	bus.Close()
	select {
	case <-w.Done():
	case <-time.After(time.Second):
		t.Fatal("writer did not finish draining")
	}

	lines := bytes.Split(bytes.TrimRight(buf.Bytes(), "\n"), []byte{'\n'})
	if len(lines) != 3 {
		t.Fatalf("expected 3 lines, got %d\n%s", len(lines), buf.String())
	}
	for _, ln := range lines {
		var obj map[string]any
		if err := json.Unmarshal(ln, &obj); err != nil {
			t.Errorf("line not JSON: %s", ln)
		}
		if _, ok := obj["ts"]; !ok {
			t.Errorf("missing ts field: %s", ln)
		}
		if _, ok := obj["event"]; !ok {
			t.Errorf("missing event field: %s", ln)
		}
		if v, ok := obj["v"]; !ok || int(v.(float64)) != 1 {
			t.Errorf("missing/wrong v field: %s", ln)
		}
	}
}

func TestWriterPreservesCustomFields(t *testing.T) {
	bus := eventbus.NewBus()
	var buf bytes.Buffer
	w := NewMemoryWriter(&buf, bus)

	bus.Publish(eventbus.Event{
		Type: eventbus.ToolCallEnd,
		Data: map[string]any{
			"call_id":       "c1",
			"tool_name":     "terminal",
			"duration_ms":   float64(123),
			"success":       true,
			"result_length": float64(456),
		},
	})

	time.Sleep(50 * time.Millisecond)
	bus.Close()
	<-w.Done()

	var obj map[string]any
	if err := json.Unmarshal(bytes.TrimRight(buf.Bytes(), "\n"), &obj); err != nil {
		t.Fatal(err)
	}
	if obj["tool_name"] != "terminal" {
		t.Errorf("missing tool_name: %v", obj)
	}
	if obj["success"] != true {
		t.Errorf("missing success field: %v", obj)
	}
}
