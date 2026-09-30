package main

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"sync"
	"time"

	"github.com/msdickinson/vett/sidecar/internal/agent"
	"github.com/msdickinson/vett/sidecar/internal/eventbus"
	"github.com/msdickinson/vett/sidecar/internal/sandbox"
)

// StdioRequest is a JSON message from the extension to vett.
type StdioRequest struct {
	Type string `json:"type"` // "user_message", "cancel", "ping"
	Text string `json:"text,omitempty"`
	ID   string `json:"id,omitempty"`
}

// StdioEvent is a JSON message from vett to the extension.
type StdioEvent struct {
	Type      string         `json:"type"`
	Timestamp string         `json:"timestamp,omitempty"`
	Data      map[string]any `json:"data,omitempty"`
	Text      string         `json:"text,omitempty"`
}

// stdioWriter writes JSON events to stdout, one per line. Thread-safe.
type stdioWriter struct {
	mu  sync.Mutex
	enc *json.Encoder
}

func newStdioWriter() *stdioWriter {
	return &stdioWriter{enc: json.NewEncoder(os.Stdout)}
}

func (w *stdioWriter) send(e StdioEvent) {
	if e.Timestamp == "" {
		e.Timestamp = time.Now().UTC().Format(time.RFC3339Nano)
	}
	w.mu.Lock()
	defer w.mu.Unlock()
	_ = w.enc.Encode(e)
}

// stdioChatLoop runs an interactive chat session over JSON on
// stdin/stdout. Designed for the VETT-CHAT VSCode extension.
func stdioChatLoop(ctx context.Context, loop *agent.Loop, sb *sandbox.LocalSandbox, bus *eventbus.Bus, systemPrompt, cwd string) error {
	out := newStdioWriter()

	// Subscribe to bus events and forward them to stdout.
	events := bus.Subscribe(256, true)
	go forwardBusEvents(events, out)

	// Emit ready event.
	out.send(StdioEvent{
		Type: "ready",
		Data: map[string]any{
			"session": sb.SessionID,
			"cwd":     cwd,
		},
	})

	// Read JSON requests from stdin.
	userCh := make(chan agent.UserInput, 4)
	cancelCtx, cancelFn := context.WithCancel(ctx)
	defer cancelFn()

	go func() {
		scanner := bufio.NewScanner(os.Stdin)
		scanner.Buffer(make([]byte, 64*1024), 1*1024*1024)
		for scanner.Scan() {
			line := scanner.Bytes()
			if len(line) == 0 {
				continue
			}
			var req StdioRequest
			if err := json.Unmarshal(line, &req); err != nil {
				out.send(StdioEvent{
					Type: "error",
					Data: map[string]any{"message": fmt.Sprintf("invalid request: %v", err)},
				})
				continue
			}
			switch req.Type {
			case "user_message":
				userCh <- agent.UserInput{Text: req.Text}
			case "cancel":
				cancelFn()
			case "ping":
				out.send(StdioEvent{Type: "pong"})
			default:
				out.send(StdioEvent{
					Type: "error",
					Data: map[string]any{"message": fmt.Sprintf("unknown request type: %q", req.Type)},
				})
			}
		}
		close(userCh)
	}()

	// Run the interactive loop.
	result, err := loop.RunInteractive(cancelCtx, systemPrompt, userCh, agent.InteractiveCallbacks{
		OnAssistantText: func(text string) {
			out.send(StdioEvent{
				Type: "assistant_text",
				Text: text,
			})
		},
		OnWaitingForInput: func() {
			out.send(StdioEvent{Type: "user_input_needed"})
		},
	})

	if err != nil && cancelCtx.Err() == nil {
		out.send(StdioEvent{
			Type: "error",
			Data: map[string]any{"message": err.Error()},
		})
	}

	doneData := map[string]any{}
	if result != nil {
		doneData["stop_reason"] = result.StopReason
		doneData["iterations"] = result.Iterations
		doneData["input_tokens"] = result.InputTokens
		doneData["output_tokens"] = result.OutputTokens
	}
	out.send(StdioEvent{Type: "done", Data: doneData})

	return nil
}

// forwardBusEvents reads from the event bus and writes matching events
// to the stdio output stream.
func forwardBusEvents(events <-chan eventbus.Event, out *stdioWriter) {
	for e := range events {
		var evType string
		switch e.Type {
		case eventbus.ToolCallStart:
			evType = "tool_call_start"
		case eventbus.ToolCallEnd:
			evType = "tool_call_end"
		case eventbus.IterationStart:
			evType = "iteration_start"
		case eventbus.IterationEnd:
			evType = "iteration_end"
		case eventbus.LLMRequest:
			evType = "llm_request"
		case eventbus.LLMResponse:
			evType = "llm_response"
		case eventbus.ErrorEvent:
			evType = "error"
		default:
			continue // skip events not relevant to the extension
		}
		out.send(StdioEvent{
			Type:      evType,
			Timestamp: e.Timestamp.UTC().Format(time.RFC3339Nano),
			Data:      e.Data,
		})
	}
}
