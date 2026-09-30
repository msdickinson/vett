// Package tools is where built-in tool implementations live. Each file
// registers one tool at init time via tool.Register. Import this package
// for its side effects (`_ "github.com/msdickinson/vett/sidecar/tools"`) in the
// cmd/vett binary so every tool is available to profiles.
package tools

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/msdickinson/vett/sidecar/pkg/tool"
	"github.com/msdickinson/vett/sidecar/pkg/tool/args"
)

func init() {
	tool.Register(&tool.Tool{
		Key:     "terminal",
		Name:    "terminal",
		Execute: executeTerminal,
	})
}

// executeTerminal runs a bash command in the agent's session and wraps
// the result in the openhands observation envelope:
//
//	<raw stdout>
//	[Current working directory: <cwd>]
//	[Command finished with exit code <N>]
//
// For timeouts the envelope is prefixed with
// "Error: Command timed out after <N>s.\n".
//
// `is_input` and `reset` are accepted and ignored per
// openhands-reference-spec.md §13 gap 1 (ref never invokes them on
// SWE-bench).
func executeTerminal(ctx context.Context, a map[string]any, env tool.Env) (string, error) {
	command := args.String(a, "command", "")
	timeoutSec := args.Int(a, "timeout", 60)
	if timeoutSec <= 0 {
		timeoutSec = 60
	}

	if env.Sandbox == nil {
		return "", fmt.Errorf("terminal tool requires a sandbox")
	}

	result, err := env.Sandbox.BashExec(ctx, env.SessionID, command, time.Duration(timeoutSec)*time.Second, nil)
	if err != nil {
		return "", fmt.Errorf("sandbox bash_exec: %w", err)
	}

	var b strings.Builder
	if result.TimedOut {
		// tool.TimeoutObservationPrefix is load-bearing: agent.Loop
		// matches on it between iterations to trigger SessionRecoverFn.
		b.WriteString(fmt.Sprintf("%s%ds.\n", tool.TimeoutObservationPrefix, timeoutSec))
	}
	b.WriteString(result.Stdout)
	if !strings.HasSuffix(result.Stdout, "\n") && result.Stdout != "" {
		b.WriteString("\n")
	}
	b.WriteString(fmt.Sprintf("[Current working directory: %s]\n", result.Cwd))
	exitCode := result.ExitCode
	if result.TimedOut {
		exitCode = -1
	}
	b.WriteString(fmt.Sprintf("[Command finished with exit code %d]", exitCode))
	return b.String(), nil
}
