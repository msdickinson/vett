package main

import (
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
)

// Remote execution: the "run the CLI on my laptop, do the work on the runner box,
// let me close the lid" path. Built for the user's specific runner box
// workflow but generalized so any host with a home dir + SSH + Docker
// works.
//
// Layout on the remote host:
//
//   ~/.vett/bin/vett                        the installed binary
//   ~/.vett/remote-work/<run-id>/           per-run workspace
//     profiles/                             synced from local
//     suites/                               synced from local
//     testdata/                             synced from local (if present)
//     results/                              output dir
//
// `vett run --remote <host>` does:
//   1. Ensure ~/.vett/bin/vett exists on <host>; if not, install it.
//   2. tar ./profiles ./suites ./testdata → ssh <host> extract into
//      ~/.vett/remote-work/<run-id>/
//   3. ssh <host> to exec ~/.vett/bin/vett run with the same flags
//      minus --remote / --detach / --fetch-on-complete, pointing
//      --output at ~/.vett/remote-work/<run-id>/results/<run-id>
//   4. Stream stdout/stderr back (foreground) or background via nohup
//      and return immediately (--detach).

// These paths are used both as arguments to plain ssh (where bash
// performs tilde expansion) and inside single-quoted nohup sh -c
// wrappers (where it does NOT). We use $HOME in the rendered
// command strings so they work in either context.
const remoteBinPath = "$HOME/.vett/bin/vett"
const remoteWorkBase = "$HOME/.vett/remote-work"

// runOnRemote implements `vett run --remote <host>` by syncing the
// workspace, optionally installing vett on <host>, and exec'ing
// `vett run` there.
func runOnRemote(host string, detach, fetchOnComplete bool, runArgs []string) error {
	if host == "" {
		return fmt.Errorf("--remote: host is required")
	}
	// (1) Ensure vett is installed on the remote.
	if err := ensureRemoteVett(host); err != nil {
		return fmt.Errorf("remote vett install: %w", err)
	}
	// (2) Derive a run-id that both local and remote use consistently.
	runID := "run-" + time.Now().UTC().Format("20060102-150405")
	remoteRunDir := remoteWorkBase + "/" + runID
	// (3) Sync workspace (profiles/, suites/, testdata/ if they exist).
	if err := syncWorkspaceToRemote(host, remoteRunDir); err != nil {
		return fmt.Errorf("sync workspace to %s: %w", host, err)
	}
	// (4) Build the remote vett run command line. Strip any explicit
	// --output flag from runArgs — we force output to the remote work
	// dir. Other flags pass through verbatim.
	cleaned := stripFlagWithValue(runArgs, "--output", "-o")
	remoteOutput := remoteRunDir + "/results/" + runID
	remoteCmd := buildRemoteRunCommand(remoteRunDir, cleaned, remoteOutput, detach)

	// (5) Print the handshake summary the user sees on their laptop.
	fmt.Printf("run-id:  %s\n", runID)
	fmt.Printf("remote:  %s\n", host)
	fmt.Printf("work:    %s\n", remoteRunDir)
	fmt.Printf("results: %s\n", remoteOutput)
	fmt.Println()
	fmt.Println("watch:")
	fmt.Printf("  vett runs tail %s --remote %s\n", runID, host)
	fmt.Println("fetch when done:")
	fmt.Printf("  vett runs fetch %s --remote %s\n", runID, host)
	fmt.Println()

	// (6) Execute. Detach = nohup on remote, return immediately.
	// Foreground = stream output through the ssh process.
	ssh := exec.Command("ssh", host, remoteCmd)
	ssh.Stdout = os.Stdout
	ssh.Stderr = os.Stderr
	if detach {
		// For detach, nohup-ed command returns as soon as it's launched.
		// ssh returns quickly and we return with the run-id already printed.
		return ssh.Run()
	}
	return ssh.Run()
}

// ensureRemoteVett checks whether ~/.vett/bin/vett exists on <host>,
// and if not, installs it by streaming the local binary over.
func ensureRemoteVett(host string) error {
	check := exec.Command("ssh", host, "test -x "+remoteBinPath+" && echo OK || echo MISSING")
	out, err := check.CombinedOutput()
	if err == nil && strings.Contains(string(out), "OK") {
		return nil
	}
	// Install by streaming the local binary. We use the same linux-amd64
	// binary the user is running: if they're on Linux, /proc/self/exe;
	// if they're on Windows or Mac, they should have cross-compiled a
	// linux binary and pointed VETT_REMOTE_BINARY at it.
	local, err := localLinuxBinary()
	if err != nil {
		return err
	}
	return installRemoteVett(host, local)
}

// localLinuxBinary locates a Linux-amd64 vett binary to install on
// the remote. Priority:
//  1. $VETT_REMOTE_BINARY if set
//  2. ./bin/vett-linux-amd64 in the current working directory (as
//     produced by `make install` / `GOOS=linux go build`)
//  3. /proc/self/exe if the caller is on Linux
func localLinuxBinary() (string, error) {
	if p := os.Getenv("VETT_REMOTE_BINARY"); p != "" {
		if _, err := os.Stat(p); err == nil {
			return p, nil
		}
	}
	if wd, err := os.Getwd(); err == nil {
		p := filepath.Join(wd, "bin", "vett-linux-amd64")
		if _, err := os.Stat(p); err == nil {
			return p, nil
		}
	}
	return "", fmt.Errorf("no linux vett binary found; set VETT_REMOTE_BINARY=/path/to/vett-linux-amd64 or cross-compile one into ./bin/vett-linux-amd64 first")
}

// installRemoteVett streams a local binary to <host>:~/.vett/bin/vett
// and chmod +x's it.
func installRemoteVett(host, localPath string) error {
	f, err := os.Open(localPath)
	if err != nil {
		return err
	}
	defer f.Close()
	cmd := exec.Command("ssh", host,
		"mkdir -p ~/.vett/bin && cat > ~/.vett/bin/vett && chmod +x ~/.vett/bin/vett && ls -la ~/.vett/bin/vett")
	cmd.Stdin = f
	cmd.Stdout = os.Stderr
	cmd.Stderr = os.Stderr
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("ssh install: %w", err)
	}
	return nil
}

// syncWorkspaceToRemote tars the local workspace's profiles/, suites/,
// and testdata/ dirs (if they exist) and streams them over ssh into
// <host>:<remoteDir>. The ~/.vett/datasets/ path is NOT synced because
// it's keyed to the remote user's home dir and SWE-bench datasets are
// already multi-hundred-MB — they should be imported once on the
// remote via `ssh host vett import-swebench`.
func syncWorkspaceToRemote(host, remoteDir string) error {
	wd, err := os.Getwd()
	if err != nil {
		return err
	}
	var toTar []string
	for _, sub := range []string{"profiles", "suites", "testdata"} {
		p := filepath.Join(wd, sub)
		if _, err := os.Stat(p); err == nil {
			toTar = append(toTar, sub)
		}
	}
	if len(toTar) == 0 {
		return fmt.Errorf("no profiles/, suites/, or testdata/ in %s — are you in a workspace? (try `vett init`)", wd)
	}
	// tar -cf - <dirs> | ssh host 'mkdir -p <remoteDir> && cd <remoteDir> && tar -xf -'
	tarArgs := append([]string{"-cf", "-"}, toTar...)
	tar := exec.Command("tar", tarArgs...)
	tar.Dir = wd
	ssh := exec.Command("ssh", host,
		"mkdir -p "+remoteDir+" && cd "+remoteDir+" && tar -xf -")
	// Pipe tar stdout → ssh stdin.
	pipe, err := tar.StdoutPipe()
	if err != nil {
		return err
	}
	ssh.Stdin = pipe
	ssh.Stdout = os.Stderr
	ssh.Stderr = os.Stderr
	if err := ssh.Start(); err != nil {
		return err
	}
	if err := tar.Run(); err != nil {
		_ = ssh.Wait()
		return fmt.Errorf("tar workspace: %w", err)
	}
	if err := pipe.Close(); err != nil && err != io.EOF {
		// pipe.Close on StdoutPipe can fail benignly after tar exits.
	}
	return ssh.Wait()
}

// buildRemoteRunCommand constructs the shell command string to execute
// on the remote host. Paths containing $HOME are passed UNQUOTED so
// bash expands the variable; user-supplied args are shellQuoted.
// Run-ids are timestamp-format (alphanumeric + dashes) so they're
// shell-safe without quoting.
func buildRemoteRunCommand(remoteDir string, runArgs []string, outputPath string, detach bool) string {
	var sb strings.Builder
	// remoteDir starts with $HOME/, safe unquoted.
	sb.WriteString("cd " + remoteDir + " && ")
	// remoteBinPath is $HOME/.vett/bin/vett, safe unquoted.
	sb.WriteString(remoteBinPath + " run")
	for _, arg := range runArgs {
		sb.WriteByte(' ')
		sb.WriteString(shellQuote(arg))
	}
	// outputPath is also $HOME/.vett/remote-work/<run-id>/results/<run-id>
	sb.WriteString(" --output " + outputPath)
	if detach {
		// Wrap in nohup so the remote process survives ssh disconnect,
		// redirect stdout/stderr to a log file in the PARENT (remoteDir),
		// not inside outputPath — outputPath might not exist yet, and
		// shells can't redirect into a dir that isn't there. remoteDir
		// is guaranteed to exist because syncWorkspaceToRemote mkdir'd it.
		logFile := remoteDir + "/remote.log"
		// Wrap the inner command in double quotes so $HOME expands
		// when sh -c interprets it. Inside double-quoted sh -c arg,
		// $HOME expands fine and the only thing we need to escape is
		// literal double-quotes and backslashes (none in our case).
		inner := sb.String() + " > " + logFile + " 2>&1"
		return `nohup sh -c "` + inner + `" > /dev/null 2>&1 & echo detached pid=$!`
	}
	return sb.String()
}

// stripFlagWithValue removes --flag <value> or --flag=<value> pairs
// from an arg list. Used to strip --output because the remote path
// has to be computed by vett, not the user.
func stripFlagWithValue(args []string, flags ...string) []string {
	out := make([]string, 0, len(args))
	skipNext := false
	for _, a := range args {
		if skipNext {
			skipNext = false
			continue
		}
		matched := false
		for _, f := range flags {
			if a == f {
				matched = true
				skipNext = true
				break
			}
			if strings.HasPrefix(a, f+"=") {
				matched = true
				break
			}
		}
		if !matched {
			out = append(out, a)
		}
	}
	return out
}

// shellQuote wraps s in single quotes and escapes any embedded single
// quotes. Safe for posix sh / bash.
func shellQuote(s string) string {
	if s == "" {
		return "''"
	}
	if !strings.ContainsAny(s, " \t\n'\"\\$`&|;<>(){}[]*?#~!") {
		return s
	}
	return "'" + strings.ReplaceAll(s, "'", `'\''`) + "'"
}
