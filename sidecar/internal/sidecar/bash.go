// Package sidecar implements the agent-server process. Bash sessions
// are long-lived "bash --noprofile --norc" children fed via stdin pipes;
// every bash_exec request writes a wrapped command to the child and reads
// output up to the sentinel marker emitted by the framing printf.
//
// Cross-platform: runs in Linux containers (benchmark path) or natively
// on Windows/Linux (local coding assistant path).
package sidecar

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"os/exec"
	"strings"
	"sync"
	"time"
)

// BashSession wraps one persistent bash child. Safe for serial use by a
// single goroutine — the server loop guarantees per-session serialization.
type BashSession struct {
	name   string
	cmd    *exec.Cmd
	stdin  io.WriteCloser
	mu     sync.Mutex
	cwd    string
	dead   bool
	chunks chan []byte    // dedicated reader pushes output here
	readErr chan error    // closed (with an error once) when the reader exits

	// deathDiag is populated by the cmd.Wait goroutine when bash exits.
	// Read by Exec when it detects bash death so the error message bubbled
	// back to the C# host (and ultimately vett's run.log) carries the
	// actual exit code/signal/last-command. Protected by deathMu.
	deathMu   sync.Mutex
	deathDiag string
}

// NewBashSession starts a fresh bash child rooted at cwd with the given
// env overrides merged into the inherited environment.
func NewBashSession(name, cwd string, env map[string]string) (*BashSession, error) {
	// Non-interactive: we feed commands via stdin. An interactive shell
	// would enable readline on the pipe (wrapping the sentinel line at
	// the default terminal width) and print $PS1 prompts into output.
	cmd := exec.Command(bashPath(), "--noprofile", "--norc")
	if cwd != "" {
		cmd.Dir = cwd
	}
	if len(env) > 0 {
		cmd.Env = mergeEnv(env)
	}
	// Run in its own process group so timeouts can interrupt the group.
	setProcAttr(cmd)
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return nil, fmt.Errorf("bash stdin pipe: %w", err)
	}
	outR, outW := io.Pipe()
	cmd.Stdout = outW
	cmd.Stderr = outW
	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("bash start: %w", err)
	}
	bashStartedPid := cmd.Process.Pid
	s := &BashSession{
		name:    name,
		cmd:     cmd,
		stdin:   stdin,
		cwd:     cwd,
		chunks:  make(chan []byte, 64),
		readErr: make(chan error, 1),
	}
	registerBash(bashStartedPid, s)
	go func() {
		err := cmd.Wait()
		// DIAG: capture why bash exited and stash on the session so Exec
		// can include it in the error returned to the C# host (visible in
		// vett's run.log).
		var diag string
		if err == nil {
			diag = "exit=0 (clean)"
		} else if exitErr, ok := err.(*exec.ExitError); ok {
			diag = fmt.Sprintf("exit=%d signal=%v", exitErr.ExitCode(), exitErr.ProcessState.Sys())
		} else {
			diag = fmt.Sprintf("wait_err=%v", err)
		}
		lastCmd := lastBashCommand(bashStartedPid)
		full := fmt.Sprintf("[bash pid=%d %s last_cmd=%q]", bashStartedPid, diag, lastCmd)
		s.deathMu.Lock()
		s.deathDiag = full
		s.deathMu.Unlock()
		// Also append to a host-readable file (best effort). Path inside
		// container; survives only as long as the container does.
		_ = appendDiag(time.Now().Format("15:04:05.000") + " " + full + "\n")
		_ = outW.Close()
	}()
	// Dedicated reader goroutine: the single owner of the stdout pipe.
	// No other goroutine is allowed to read from it. Exec consumes chunks
	// via s.chunks with a select + deadline.
	go func() {
		defer close(s.chunks)
		buf := make([]byte, 4096)
		for {
			n, err := outR.Read(buf)
			if n > 0 {
				// Copy out of the shared buffer before handing off.
				chunk := make([]byte, n)
				copy(chunk, buf[:n])
				s.chunks <- chunk
			}
			if err != nil {
				s.readErr <- err
				return
			}
		}
	}()
	// Prime: run a no-op command so we have a stable cwd.
	if _, err := s.Exec(context.Background(), "true", 10*time.Second, nil); err != nil {
		_ = s.Close()
		return nil, fmt.Errorf("bash prime: %w", err)
	}
	return s, nil
}

// PID returns the bash child's process ID.
func (s *BashSession) PID() int {
	if s.cmd == nil || s.cmd.Process == nil {
		return 0
	}
	return s.cmd.Process.Pid
}

// Cwd returns the last-observed working directory.
func (s *BashSession) Cwd() string { return s.cwd }

// snapshotDeath returns whatever diagnostic the cmd.Wait goroutine has
// recorded so far. Empty string if bash hasn't actually died (yet).
func (s *BashSession) snapshotDeath() string {
	s.deathMu.Lock()
	defer s.deathMu.Unlock()
	return s.deathDiag
}

// Name returns the session name.
func (s *BashSession) Name() string { return s.name }

// ExecResult mirrors rpc.BashExecResult.
type ExecResult struct {
	Stdout   string
	ExitCode int
	Cwd      string
	TimedOut bool
}

// StreamCallback is invoked with each output chunk as it arrives. May be
// nil for buffered (non-streaming) execution.
type StreamCallback func(chunk string)

// Exec writes the framed command to bash stdin and reads output until the
// sentinel marker appears or the timeout fires. Context cancellation kills
// the foreground job via SIGINT to the process group.
func (s *BashSession) Exec(ctx context.Context, command string, timeout time.Duration, stream StreamCallback) (*ExecResult, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.dead {
		return nil, fmt.Errorf("bash session is dead %s", s.snapshotDeath())
	}

	script, marker := buildBashScript(command)
	if s.cmd != nil && s.cmd.Process != nil {
		setLastCommand(s.cmd.Process.Pid, command)
	}
	if _, err := io.WriteString(s.stdin, script); err != nil {
		s.dead = true
		return nil, fmt.Errorf("write to bash: %w %s", err, s.snapshotDeath())
	}

	if timeout <= 0 {
		timeout = 60 * time.Second
	}
	deadline := time.Now().Add(timeout)

	var buf bytes.Buffer
	emittedLen := 0
	// Holdback keeps enough tail bytes buffered that we never stream
	// part of the sentinel line. The marker-line needle is "\n"+marker,
	// and we also need room for the printf suffix, so leave a margin.
	holdback := len(marker) + 2
	timedOut := false

	flushSafe := func() {
		if stream == nil {
			return
		}
		safe := buf.Len() - holdback
		if safe > emittedLen {
			stream(string(buf.Bytes()[emittedLen:safe]))
			emittedLen = safe
		}
	}

loop:
	for {
		remaining := time.Until(deadline)
		if remaining <= 0 {
			timedOut = true
			break
		}
		select {
		case chunk, ok := <-s.chunks:
			if !ok {
				s.dead = true
				return nil, fmt.Errorf("bash stdout closed %s", s.snapshotDeath())
			}
			buf.Write(chunk)
			if idx := indexMarkerLine(buf.Bytes(), marker); idx >= 0 {
				// Everything before the newline that precedes the marker
				// is the user-visible stdout.
				body := buf.Bytes()[:idx]
				if stream != nil && emittedLen < len(body) {
					stream(string(body[emittedLen:]))
				}
				// Parse "<marker> exit=N cwd=X"
				rest := buf.Bytes()[idx+1:] // skip the newline before the marker
				end := bytes.IndexByte(rest, '\n')
				if end < 0 {
					// Rare: marker header but no trailing newline yet.
					// Wait for more data.
					continue
				}
				exitCode, cwd := parseMarkerLine(string(rest[:end]), marker)
				s.cwd = cwd
				return &ExecResult{
					Stdout:   string(body),
					ExitCode: exitCode,
					Cwd:      cwd,
					TimedOut: false,
				}, nil
			}
			flushSafe()
		case <-time.After(remaining):
			timedOut = true
			break loop
		case <-ctx.Done():
			s.killForeground()
			timedOut = true
			break loop
		}
	}

	// Timed out: kill the foreground job and drain any remaining output
	// with a short grace period. Body captured so far is returned.
	if timedOut {
		s.killForeground()
		grace := time.NewTimer(500 * time.Millisecond)
		defer grace.Stop()
	drain:
		for {
			select {
			case chunk, ok := <-s.chunks:
				if !ok {
					break drain
				}
				buf.Write(chunk)
				if idx := indexMarkerLine(buf.Bytes(), marker); idx >= 0 {
					buf.Truncate(idx)
					break drain
				}
			case <-grace.C:
				break drain
			}
		}
	}
	// Flush any remaining held-back bytes to the stream callback at
	// timeout so the concatenated stream equals the final Stdout.
	if stream != nil && emittedLen < buf.Len() {
		stream(string(buf.Bytes()[emittedLen:]))
	}
	return &ExecResult{
		Stdout:   buf.String(),
		ExitCode: -1,
		Cwd:      s.cwd,
		TimedOut: true,
	}, nil
}

// Close kills the bash child.
func (s *BashSession) Close() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.dead = true
	_ = s.stdin.Close()
	if s.cmd != nil && s.cmd.Process != nil {
		killProcessGroup(s.cmd.Process.Pid)
	}
	return nil
}

// killForeground interrupts the bash process group to stop the
// currently-running foreground job (but leave the bash child alive).
func (s *BashSession) killForeground() {
	if s.cmd == nil || s.cmd.Process == nil {
		return
	}
	interruptProcessGroup(s.cmd.Process.Pid)
}

// indexMarkerLine returns the offset of the newline that precedes the
// marker token. Returns -1 if the marker is not yet present.
func indexMarkerLine(buf []byte, marker string) int {
	needle := []byte("\n" + marker)
	return bytes.Index(buf, needle)
}

// mergeEnv merges overrides with os.Environ() giving overrides priority.
func mergeEnv(overrides map[string]string) []string {
	base := osEnviron()
	keys := map[string]bool{}
	for k := range overrides {
		keys[k] = true
	}
	out := make([]string, 0, len(base)+len(overrides))
	for _, kv := range base {
		eq := strings.IndexByte(kv, '=')
		if eq < 0 {
			out = append(out, kv)
			continue
		}
		if !keys[kv[:eq]] {
			out = append(out, kv)
		}
	}
	for k, v := range overrides {
		out = append(out, k+"="+v)
	}
	return out
}

// osEnviron is a test hook.
var osEnviron = func() []string { return syscallEnviron() }
