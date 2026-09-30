package sandbox

import (
	"context"
	"fmt"
	"io"
	"os/exec"
)

// LocalSandbox runs the sidecar as a native subprocess on the host
// machine — no Docker, no container. Used by `vett chat` for local
// interactive coding sessions.
//
// The underlying *Client satisfies tool.Sandbox, so all tools work
// unchanged.
type LocalSandbox struct {
	Client     *Client
	SessionID  string
	sidecarCmd *exec.Cmd
	sidecarIn  io.WriteCloser
	sidecarOut io.ReadCloser
}

// LocalOptions configures StartLocal.
type LocalOptions struct {
	SidecarPath string // path to native sidecar binary (required)
	Cwd         string // working directory for the session (required)
	SessionName string // session name (default "local")
}

// StartLocal boots a sidecar as a local subprocess, performs the Hello
// handshake, and creates a session rooted at Cwd.
func StartLocal(ctx context.Context, opts LocalOptions) (*LocalSandbox, error) {
	if opts.SidecarPath == "" {
		return nil, fmt.Errorf("sandbox: SidecarPath is required")
	}
	if opts.Cwd == "" {
		return nil, fmt.Errorf("sandbox: Cwd is required")
	}
	if opts.SessionName == "" {
		opts.SessionName = "local"
	}

	cmd := exec.CommandContext(ctx, opts.SidecarPath)
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return nil, fmt.Errorf("sidecar stdin pipe: %w", err)
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, fmt.Errorf("sidecar stdout pipe: %w", err)
	}
	cmd.Stderr = nil // sidecar errors come back over RPC

	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("start sidecar: %w", err)
	}

	sb := &LocalSandbox{
		sidecarCmd: cmd,
		sidecarIn:  stdin,
		sidecarOut: stdout,
		SessionID:  opts.SessionName,
	}
	sb.Client = NewClient(stdout, stdin)

	// Hello handshake.
	if _, err := sb.Client.Hello(ctx, "0.1.0-dev"); err != nil {
		_ = sb.Close(context.Background())
		return nil, fmt.Errorf("sidecar hello: %w", err)
	}

	// Create the working session.
	if err := sb.Client.SessionCreate(ctx, opts.SessionName, opts.Cwd, nil); err != nil {
		_ = sb.Close(context.Background())
		return nil, fmt.Errorf("session create: %w", err)
	}

	return sb, nil
}

// Close shuts down the sidecar subprocess cleanly.
func (l *LocalSandbox) Close(ctx context.Context) error {
	if l.sidecarIn != nil {
		_ = l.sidecarIn.Close()
	}
	if l.sidecarCmd != nil {
		_ = l.sidecarCmd.Wait()
	}
	return nil
}
