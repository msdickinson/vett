//go:build windows

package sidecar

import (
	"os"
	"os/exec"
	"syscall"
)

// setProcAttr configures the bash child to run in a new process group
// so we can send CTRL_BREAK to it independently.
func setProcAttr(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{
		CreationFlags: syscall.CREATE_NEW_PROCESS_GROUP,
	}
}

// killProcessGroup forcibly terminates the bash child process.
// On Windows there are no process groups in the Unix sense; we kill
// the process directly.
func killProcessGroup(pid int) {
	if p, err := os.FindProcess(pid); err == nil {
		_ = p.Kill()
	}
}

var generateConsoleCtrlEvent = syscall.NewLazyDLL("kernel32.dll").NewProc("GenerateConsoleCtrlEvent")

// interruptProcessGroup sends CTRL_BREAK_EVENT to the bash child's
// process group to interrupt the running foreground job. Best-effort;
// if the syscall fails the timeout path falls back to killProcessGroup.
func interruptProcessGroup(pid int) {
	// CTRL_BREAK_EVENT = 1. r1 == 0 means failure.
	r1, _, _ := generateConsoleCtrlEvent.Call(1, uintptr(pid))
	if r1 == 0 {
		// Fallback: kill the process outright.
		killProcessGroup(pid)
	}
}
