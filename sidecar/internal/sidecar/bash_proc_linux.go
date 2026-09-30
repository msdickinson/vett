//go:build linux

package sidecar

import (
	"fmt"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"syscall"
	"time"
)

// setProcAttr configures the bash child to run in its own process group
// so timeouts can target the foreground job's subtree without killing
// the sidecar.
func setProcAttr(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
}

// killProcessGroup sends SIGKILL to the bash child's process group.
// Used for full session teardown — bash itself MUST die here.
func killProcessGroup(pid int) {
	_ = syscall.Kill(-pid, syscall.SIGKILL)
}

// interruptProcessGroup stops the bash child's currently-running foreground
// job WITHOUT killing bash itself.
//
// Old impl ("syscall.Kill(-pid, SIGINT)") delivered SIGINT to the entire
// process group, including bash (the group leader). A non-interactive bash
// reading from a stdin pipe exits on SIGINT, so every timeout killed the
// session and forced vett to recover (lost cwd, env, ~5-15 wasted iters).
//
// New impl walks /proc to find bash's direct children (the foreground
// job's root process(es)) and their descendants, SIGINTs them all, then
// SIGKILLs any stragglers after a brief grace period. Bash is left alive.
func interruptProcessGroup(bashPid int) {
	children := readChildren(bashPid)
	if len(children) == 0 {
		// Foreground command already exited (or never started). Nothing
		// to do — definitely don't fall back to killing the group.
		return
	}
	// Collect the full subtree(s) under each direct child.
	var all []int
	for _, c := range children {
		all = append(all, c)
		all = append(all, collectDescendants(c)...)
	}
	// Polite first: SIGINT lets traps run, mimics Ctrl+C.
	for _, p := range all {
		_ = syscall.Kill(p, syscall.SIGINT)
	}
	// Brief grace, then SIGKILL anything still running. 200ms is enough
	// for typical Python/test-runner shutdown handlers; longer would
	// stretch the timeout-recovery latency the agent sees.
	time.Sleep(200 * time.Millisecond)
	for _, p := range all {
		_ = syscall.Kill(p, syscall.SIGKILL)
	}
}

// readChildren returns the immediate children of pid as listed in
// /proc/<pid>/task/<pid>/children. Returns nil if the file can't be read
// (process gone, /proc unavailable, etc).
func readChildren(pid int) []int {
	data, err := os.ReadFile(fmt.Sprintf("/proc/%d/task/%d/children", pid, pid))
	if err != nil {
		return nil
	}
	fields := strings.Fields(string(data))
	if len(fields) == 0 {
		return nil
	}
	out := make([]int, 0, len(fields))
	for _, f := range fields {
		if p, err := strconv.Atoi(f); err == nil && p > 0 {
			out = append(out, p)
		}
	}
	return out
}

// collectDescendants walks the process tree rooted at pid (depth-first)
// and returns every descendant PID. Excludes pid itself.
func collectDescendants(pid int) []int {
	var result []int
	stack := []int{pid}
	// Bound the walk so a runaway proc tree can't hang the sidecar.
	visited := 0
	const maxVisited = 4096
	for len(stack) > 0 && visited < maxVisited {
		p := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		visited++
		for _, c := range readChildren(p) {
			result = append(result, c)
			stack = append(stack, c)
		}
	}
	return result
}
