package sidecar

import (
	"os"
	"sync"
)

// Diagnostic plumbing — captures the LAST command sent to each bash process
// and writes a one-liner to /tmp/vett-bash-deaths.log when bash exits.
// Goal: figure out what's killing bash mid-instance during real SWE-bench
// runs (the death pattern visible in vett's run.log as
// "RPC bash_exec: bash stdout closed").

const diagLogPath = "/tmp/vett-bash-deaths.log"

var (
	diagMu       sync.Mutex
	lastCommands = map[int]string{}
)

// registerBash is called once per bash spawn. Currently a no-op slot for
// future per-bash diagnostic state — kept to make the call site obvious.
func registerBash(pid int, _ *BashSession) {
	diagMu.Lock()
	defer diagMu.Unlock()
	delete(lastCommands, pid)
}

// setLastCommand stashes the most recent user command sent to bash so the
// death-handler goroutine can include it in the diag log.
func setLastCommand(pid int, command string) {
	diagMu.Lock()
	defer diagMu.Unlock()
	if len(command) > 200 {
		command = command[:200] + "...(truncated)"
	}
	lastCommands[pid] = command
}

// lastBashCommand returns the last command sent to a given bash, then
// forgets it (the bash that ran it is now dead).
func lastBashCommand(pid int) string {
	diagMu.Lock()
	defer diagMu.Unlock()
	cmd := lastCommands[pid]
	delete(lastCommands, pid)
	return cmd
}

// appendDiag appends one line to the diag log. Best-effort; silent on error.
func appendDiag(line string) error {
	f, err := os.OpenFile(diagLogPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	if err != nil {
		return err
	}
	defer f.Close()
	_, err = f.WriteString(line)
	return err
}
