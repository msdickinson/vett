// Package plugin implements the external tool/middleware subprocess
// protocol. User-authored extensions in any language communicate with
// vett over newline-delimited JSON on stdin/stdout.
//
// Lifecycle: spawn once at startup, keep alive, send requests, read
// responses, kill on shutdown. Same pattern as the sidecar.
package plugin

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os/exec"
	"sync"
	"sync/atomic"
)

// ToolRequest is sent to an external tool process on stdin.
type ToolRequest struct {
	Type      string         `json:"type"`       // "execute"
	CallID    string         `json:"call_id"`
	SessionID string         `json:"session_id"`
	Args      map[string]any `json:"args"`
}

// ToolResponse is read from an external tool process on stdout.
type ToolResponse struct {
	Result string `json:"result"`
	Error  string `json:"error,omitempty"`
}

// MiddlewareRequest is sent to an external middleware process on stdin.
type MiddlewareRequest struct {
	Type            string         `json:"type"` // "process"
	Iteration       int            `json:"iteration"`
	MaxIterations   int            `json:"max_iterations"`
	StopLoop        bool           `json:"stop_loop"`
	StopReason      string         `json:"stop_reason"`
	LastObservations []ObservationW `json:"last_observations"`
}

// ObservationW is the wire representation of an observation.
type ObservationW struct {
	ToolCallID string `json:"tool_call_id"`
	ToolName   string `json:"tool_name"`
	Result     string `json:"result"`
	Success    bool   `json:"success"`
}

// MiddlewareResponse is read from an external middleware process.
type MiddlewareResponse struct {
	StopLoop   bool   `json:"stop_loop"`
	StopReason string `json:"stop_reason,omitempty"`
}

// HelloRequest is sent on startup to verify the process is alive.
type HelloRequest struct {
	Type    string `json:"type"`    // "hello"
	Version string `json:"version"` // vett version
}

// HelloResponse confirms the plugin is ready.
type HelloResponse struct {
	Name    string `json:"name"`
	Version string `json:"version,omitempty"`
	Ready   bool   `json:"ready"`
}

// Process wraps a long-lived plugin subprocess.
type Process struct {
	Name    string
	cmd     *exec.Cmd
	stdin   io.WriteCloser
	reader  *bufio.Scanner
	mu      sync.Mutex
	nextID  atomic.Int64
	dead    bool
}

// StartProcess launches a plugin subprocess and performs the hello
// handshake. The process stays alive until Close is called.
func StartProcess(ctx context.Context, name string, command string, args ...string) (*Process, error) {
	cmd := exec.CommandContext(ctx, command, args...)
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return nil, fmt.Errorf("plugin %s: stdin pipe: %w", name, err)
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, fmt.Errorf("plugin %s: stdout pipe: %w", name, err)
	}
	cmd.Stderr = nil

	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("plugin %s: start: %w", name, err)
	}

	p := &Process{
		Name:   name,
		cmd:    cmd,
		stdin:  stdin,
		reader: bufio.NewScanner(stdout),
	}
	p.reader.Buffer(make([]byte, 64*1024), 1*1024*1024)

	// Hello handshake.
	helloReq := HelloRequest{Type: "hello", Version: "0.1.0-dev"}
	if err := p.sendJSON(helloReq); err != nil {
		p.Close()
		return nil, fmt.Errorf("plugin %s: hello send: %w", name, err)
	}

	var helloResp HelloResponse
	if err := p.readJSON(&helloResp); err != nil {
		p.Close()
		return nil, fmt.Errorf("plugin %s: hello read: %w", name, err)
	}
	if !helloResp.Ready {
		p.Close()
		return nil, fmt.Errorf("plugin %s: not ready", name)
	}

	return p, nil
}

// ExecuteTool sends a tool request and reads the response.
func (p *Process) ExecuteTool(callID, sessionID string, args map[string]any) (string, error) {
	p.mu.Lock()
	defer p.mu.Unlock()

	if p.dead {
		return "", fmt.Errorf("plugin %s is dead", p.Name)
	}

	req := ToolRequest{
		Type:      "execute",
		CallID:    callID,
		SessionID: sessionID,
		Args:      args,
	}
	if err := p.sendJSON(req); err != nil {
		p.dead = true
		return "", err
	}

	var resp ToolResponse
	if err := p.readJSON(&resp); err != nil {
		p.dead = true
		return "", err
	}

	if resp.Error != "" {
		return resp.Result, fmt.Errorf("%s", resp.Error)
	}
	return resp.Result, nil
}

// ExecuteMiddleware sends a middleware state and reads the response.
func (p *Process) ExecuteMiddleware(req MiddlewareRequest) (*MiddlewareResponse, error) {
	p.mu.Lock()
	defer p.mu.Unlock()

	if p.dead {
		return nil, fmt.Errorf("plugin %s is dead", p.Name)
	}

	req.Type = "process"
	if err := p.sendJSON(req); err != nil {
		p.dead = true
		return nil, err
	}

	var resp MiddlewareResponse
	if err := p.readJSON(&resp); err != nil {
		p.dead = true
		return nil, err
	}

	return &resp, nil
}

// Close kills the subprocess.
func (p *Process) Close() error {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.dead = true
	_ = p.stdin.Close()
	if p.cmd != nil && p.cmd.Process != nil {
		_ = p.cmd.Process.Kill()
	}
	return nil
}

func (p *Process) sendJSON(v any) error {
	data, err := json.Marshal(v)
	if err != nil {
		return err
	}
	data = append(data, '\n')
	_, err = p.stdin.Write(data)
	return err
}

func (p *Process) readJSON(v any) error {
	if !p.reader.Scan() {
		if err := p.reader.Err(); err != nil {
			return err
		}
		return io.EOF
	}
	return json.Unmarshal(p.reader.Bytes(), v)
}
