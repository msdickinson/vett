//go:build integration

package sidecar_test

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/msdickinson/vett/sidecar/internal/sandbox"
	"github.com/msdickinson/vett/sidecar/pkg/rpc"
)

// These tests run against a real /bin/bash on a Linux host. On the dev
// Windows box the sidecar is cross-compiled to linux/amd64 and scp'd to
// VETT_SIDECAR_HOST (default "runner"); on a native Linux dev environment
// the sidecar runs in the local shell. Skip if neither is available.
//
// These tests cover what Quick-tier unit tests cannot: real bash sessions,
// real file filesystem, real timeouts. They are gated by the integration
// build tag so `go test ./...` stays fast.

func sidecarHost() string {
	if h := os.Getenv("VETT_SIDECAR_HOST"); h != "" {
		return h
	}
	return "runner"
}

func repoRoot(t *testing.T) string {
	t.Helper()
	_, thisFile, _, _ := runtime.Caller(0)
	return filepath.Clean(filepath.Join(filepath.Dir(thisFile), "..", ".."))
}

// buildAndStageSidecar cross-compiles the sidecar and installs it at a
// predictable location on the target host (the local filesystem or a
// remote Linux host via SSH). Returns a launcher that runs the sidecar
// and wires stdin/stdout pipes into a sandbox.Client.
func buildAndStageSidecar(t *testing.T) func(ctx context.Context) (*sandbox.Client, func()) {
	t.Helper()
	root := repoRoot(t)
	outBin := filepath.Join(root, "bin", "vett-sidecar-linux-amd64")
	build := exec.Command("go", "build", "-o", outBin, "./cmd/vett-sidecar")
	build.Dir = root
	build.Env = append(os.Environ(), "GOOS=linux", "GOARCH=amd64")
	if out, err := build.CombinedOutput(); err != nil {
		t.Fatalf("cross-compile sidecar: %v\n%s", err, out)
	}

	if runtime.GOOS == "linux" {
		return func(ctx context.Context) (*sandbox.Client, func()) {
			cmd := exec.CommandContext(ctx, outBin)
			return startClient(t, cmd)
		}
	}
	// Remote: scp to sidecarHost().
	host := sidecarHost()
	remotePath := "/tmp/vett-sidecar"
	// Stream the binary over SSH (scp doesn't handle Windows paths well).
	f, err := os.Open(outBin)
	if err != nil {
		t.Fatalf("open sidecar: %v", err)
	}
	defer f.Close()
	push := exec.Command("ssh", host, "cat > "+remotePath+" && chmod +x "+remotePath)
	push.Stdin = f
	push.Stdout = os.Stderr
	push.Stderr = os.Stderr
	if err := push.Run(); err != nil {
		t.Skipf("SSH to %s unavailable (set VETT_SIDECAR_HOST=linux-host to override): %v", host, err)
	}

	return func(ctx context.Context) (*sandbox.Client, func()) {
		cmd := exec.CommandContext(ctx, "ssh", host, remotePath)
		return startClient(t, cmd)
	}
}

func startClient(t *testing.T, cmd *exec.Cmd) (*sandbox.Client, func()) {
	t.Helper()
	stdin, err := cmd.StdinPipe()
	if err != nil {
		t.Fatalf("stdin: %v", err)
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		t.Fatalf("stdout: %v", err)
	}
	var stderr bytes.Buffer
	cmd.Stderr = &stderr
	if err := cmd.Start(); err != nil {
		t.Fatalf("start sidecar: %v", err)
	}
	client := sandbox.NewClient(stdout, stdin)
	cleanup := func() {
		_ = stdin.Close()
		_ = cmd.Wait()
		if stderr.Len() > 0 {
			t.Logf("sidecar stderr: %s", stderr.String())
		}
	}
	return client, cleanup
}

func TestIntegrationSidecarHelloSession(t *testing.T) {
	launch := buildAndStageSidecar(t)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	client, cleanup := launch(ctx)
	defer cleanup()

	if _, err := client.Hello(ctx, "integration-test"); err != nil {
		t.Fatalf("hello: %v", err)
	}
	if err := client.SessionCreate(ctx, "agent", "/tmp", nil); err != nil {
		t.Fatalf("session_create: %v", err)
	}
	res, err := client.BashExec(ctx, "agent", "echo hello && pwd", 10*time.Second, nil)
	if err != nil {
		t.Fatalf("bash_exec: %v", err)
	}
	if res.Stdout != "hello\n/tmp\n" {
		t.Errorf("unexpected stdout: %q", res.Stdout)
	}
	if res.ExitCode != 0 || res.Cwd != "/tmp" || res.TimedOut {
		t.Errorf("unexpected result: %+v", res)
	}
}

// TestIntegrationCwdPersistsAcrossExecs — cd in one call must be visible
// in the next call on the same session (persistent bash state).
func TestIntegrationCwdPersistsAcrossExecs(t *testing.T) {
	launch := buildAndStageSidecar(t)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	client, cleanup := launch(ctx)
	defer cleanup()

	mustHello(t, ctx, client)
	mustSession(t, ctx, client, "agent", "/tmp")

	if _, err := client.BashExec(ctx, "agent", "cd /var && pwd", 10*time.Second, nil); err != nil {
		t.Fatalf("cd: %v", err)
	}
	res, err := client.BashExec(ctx, "agent", "pwd", 10*time.Second, nil)
	if err != nil {
		t.Fatalf("pwd: %v", err)
	}
	if res.Cwd != "/var" || !strings.HasPrefix(res.Stdout, "/var") {
		t.Errorf("cd did not persist: %+v", res)
	}
}

// TestIntegrationSessionsAreIsolated — two sessions don't share env.
func TestIntegrationSessionsAreIsolated(t *testing.T) {
	launch := buildAndStageSidecar(t)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	client, cleanup := launch(ctx)
	defer cleanup()

	mustHello(t, ctx, client)
	mustSession(t, ctx, client, "a", "/tmp")
	mustSession(t, ctx, client, "b", "/var")

	if _, err := client.BashExec(ctx, "a", "export X=from_a", 5*time.Second, nil); err != nil {
		t.Fatalf("a export: %v", err)
	}
	resA, err := client.BashExec(ctx, "a", "echo ${X:-unset}", 5*time.Second, nil)
	if err != nil {
		t.Fatalf("a echo: %v", err)
	}
	resB, err := client.BashExec(ctx, "b", "echo ${X:-unset}", 5*time.Second, nil)
	if err != nil {
		t.Fatalf("b echo: %v", err)
	}
	if strings.TrimSpace(resA.Stdout) != "from_a" {
		t.Errorf("session a: expected from_a, got %q", resA.Stdout)
	}
	if strings.TrimSpace(resB.Stdout) != "unset" {
		t.Errorf("session b: expected unset, got %q", resB.Stdout)
	}
}

// TestIntegrationBashTimeoutKillsForegroundJob — a hung command is killed
// after the timeout and a fresh session can be created to continue work.
//
// Phase 1b limitation: killing the foreground job currently kills bash
// itself (SIGINT to the process group takes bash with it, because
// non-interactive bash doesn't catch SIGINT the way an interactive shell
// does). The session is therefore considered dead after a timeout; the
// agent loop creates a new session to recover. Revisiting this in a later
// phase will likely involve `set -m` + job-level pgid tracking.
func TestIntegrationBashTimeoutKillsForegroundJob(t *testing.T) {
	launch := buildAndStageSidecar(t)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	client, cleanup := launch(ctx)
	defer cleanup()

	mustHello(t, ctx, client)
	mustSession(t, ctx, client, "a", "/tmp")

	res, err := client.BashExec(ctx, "a", "echo before; sleep 30; echo after", 2*time.Second, nil)
	if err != nil {
		t.Fatalf("bash_exec: %v", err)
	}
	if !res.TimedOut {
		t.Errorf("expected timed_out=true, got %+v", res)
	}
	if !strings.Contains(res.Stdout, "before") {
		t.Errorf("expected 'before' in stdout, got %q", res.Stdout)
	}
	if strings.Contains(res.Stdout, "after") {
		t.Errorf("after should have been killed, got %q", res.Stdout)
	}
	// Recovery path: create a fresh session and continue work.
	if err := client.SessionCreate(ctx, "b", "/tmp", nil); err != nil {
		t.Fatalf("fresh session_create after timeout: %v", err)
	}
	res2, err := client.BashExec(ctx, "b", "echo recovered", 5*time.Second, nil)
	if err != nil {
		t.Fatalf("recovery exec: %v", err)
	}
	if strings.TrimSpace(res2.Stdout) != "recovered" {
		t.Errorf("fresh session exec failed: %q", res2.Stdout)
	}
}

// TestIntegrationFileEditRoundTrip — create, str_replace, view — the
// full file editor path through the sidecar.
func TestIntegrationFileEditRoundTrip(t *testing.T) {
	launch := buildAndStageSidecar(t)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	client, cleanup := launch(ctx)
	defer cleanup()

	mustHello(t, ctx, client)
	mustSession(t, ctx, client, "a", "/tmp")
	// Unique path so parallel runs don't collide.
	path := fmt.Sprintf("/tmp/vett-edit-%d.py", time.Now().UnixNano())

	if _, err := client.FileCreate(ctx, "a", path, "def foo():\n    return 1\n"); err != nil {
		t.Fatalf("file_create: %v", err)
	}
	defer func() {
		_, _ = client.BashExec(ctx, "a", "rm -f "+path, 5*time.Second, nil)
	}()

	_, toolErr, err := client.FileStrReplace(ctx, "a", path, "return 1", "return 2")
	if err != nil {
		t.Fatalf("str_replace: %v", err)
	}
	if toolErr != "" {
		t.Fatalf("unexpected tool error: %s", toolErr)
	}
	res, err := client.BashExec(ctx, "a", "cat "+path, 5*time.Second, nil)
	if err != nil {
		t.Fatalf("cat: %v", err)
	}
	if res.Stdout != "def foo():\n    return 2\n" {
		t.Errorf("unexpected body: %q", res.Stdout)
	}
}

// TestIntegrationFileInsertAndUndo — insert adds a new line after the
// given line number; undo pops it back. Both flow through the
// sandbox.Client wrapper methods added to fix the file_editor mirror
// discipline gap surfaced in the Phase 1 review.
func TestIntegrationFileInsertAndUndo(t *testing.T) {
	launch := buildAndStageSidecar(t)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	client, cleanup := launch(ctx)
	defer cleanup()

	mustHello(t, ctx, client)
	mustSession(t, ctx, client, "a", "/tmp")
	path := fmt.Sprintf("/tmp/vett-insert-%d.py", time.Now().UnixNano())

	if _, err := client.FileCreate(ctx, "a", path, "line 1\nline 2\nline 3\n"); err != nil {
		t.Fatalf("create: %v", err)
	}
	defer func() { _, _ = client.BashExec(ctx, "a", "rm -f "+path, 5*time.Second, nil) }()

	// Insert after line 2.
	if _, err := client.FileInsert(ctx, "a", path, 2, "inserted\n"); err != nil {
		t.Fatalf("insert: %v", err)
	}
	// Verify via cat.
	cat, err := client.BashExec(ctx, "a", "cat "+path, 5*time.Second, nil)
	if err != nil {
		t.Fatalf("cat after insert: %v", err)
	}
	want := "line 1\nline 2\ninserted\nline 3\n"
	if cat.Stdout != want {
		t.Errorf("post-insert body mismatch:\n  got:  %q\n  want: %q", cat.Stdout, want)
	}

	// Undo and verify.
	if _, err := client.FileUndo(ctx, "a", path); err != nil {
		t.Fatalf("undo: %v", err)
	}
	cat2, err := client.BashExec(ctx, "a", "cat "+path, 5*time.Second, nil)
	if err != nil {
		t.Fatalf("cat after undo: %v", err)
	}
	if cat2.Stdout != "line 1\nline 2\nline 3\n" {
		t.Errorf("post-undo body mismatch: %q", cat2.Stdout)
	}
}

// TestIntegrationStreamingConcatEqualsBuffered — run the same command in
// both modes; the concatenated stream chunks must equal the buffered
// stdout. Locks the invariant in docs/product.md §streaming.
func TestIntegrationStreamingConcatEqualsBuffered(t *testing.T) {
	launch := buildAndStageSidecar(t)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()

	run := func(stream bool) string {
		client, cleanup := launch(ctx)
		defer cleanup()
		mustHello(t, ctx, client)
		mustSession(t, ctx, client, "a", "/tmp")
		var chunks []string
		var onChunk func(string)
		if stream {
			onChunk = func(c string) { chunks = append(chunks, c) }
		}
		res, err := client.BashExec(ctx, "a",
			"for i in 1 2 3; do echo line-$i; done",
			10*time.Second, onChunk)
		if err != nil {
			t.Fatalf("bash_exec stream=%v: %v", stream, err)
		}
		if stream {
			return strings.Join(chunks, "")
		}
		return res.Stdout
	}
	buffered := run(false)
	streamed := run(true)
	if buffered != streamed {
		t.Errorf("buffered vs streamed mismatch:\n  buffered: %q\n  streamed: %q", buffered, streamed)
	}
	if buffered != "line-1\nline-2\nline-3\n" {
		t.Errorf("unexpected output: %q", buffered)
	}
}

// mustHello and mustSession are local helpers that fail the test fast.

func mustHello(t *testing.T, ctx context.Context, c *sandbox.Client) {
	t.Helper()
	if _, err := c.Hello(ctx, "integration-test"); err != nil {
		t.Fatalf("hello: %v", err)
	}
}

func mustSession(t *testing.T, ctx context.Context, c *sandbox.Client, name, cwd string) {
	t.Helper()
	if err := c.SessionCreate(ctx, name, cwd, nil); err != nil {
		t.Fatalf("session_create %q: %v", name, err)
	}
}

// Ensure imports stay used when the package grows.
var (
	_ = io.EOF
	_ = json.Marshal
	_ = rpc.OpHello
)
