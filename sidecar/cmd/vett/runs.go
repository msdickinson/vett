package main

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"github.com/spf13/cobra"
)

// `vett runs` subcommand family — list / tail / fetch / status for
// runs, local or via --remote <host>. Used for the "start a run on
// runner box, close laptop, check back later" flow built out in Phase 2
// Step 4's remote launcher.
//
// Local runs live at ./results/<run-id>/ (workspace-relative).
// Remote runs live at <host>:~/.vett/remote-work/<run-id>/results/<run-id>/.

func runsCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:   "runs",
		Short: "List, tail, fetch, report past runs (local or --remote)",
	}
	cmd.AddCommand(runsListCmd())
	cmd.AddCommand(runsTailCmd())
	cmd.AddCommand(runsFetchCmd())
	cmd.AddCommand(runsStatusCmd())
	cmd.AddCommand(runsReportCmd())
	return cmd
}

// runsReportCmd walks a directory of runs and prints a table with one
// row per instance across all runs. Handy for the "I kicked off 8
// parallel runs, show me a single overview" case.
func runsReportCmd() *cobra.Command {
	var (
		remoteHost string
		resultsDir string
	)
	cmd := &cobra.Command{
		Use:   "report",
		Short: "Print a table summarizing all runs in a results dir",
		RunE: func(cmd *cobra.Command, args []string) error {
			if remoteHost != "" {
				return runsReportRemote(remoteHost, resultsDir)
			}
			return runsReportLocal(resultsDir)
		},
	}
	cmd.Flags().StringVar(&remoteHost, "remote", "", "Read runs from an SSH host")
	cmd.Flags().StringVar(&resultsDir, "results-dir", "", "Directory to scan (default: ./results locally, ~/.vett/remote-work remotely)")
	return cmd
}

func runsReportLocal(resultsDir string) error {
	if resultsDir == "" {
		wd, _ := os.Getwd()
		resultsDir = filepath.Join(wd, "results")
	}
	entries, err := os.ReadDir(resultsDir)
	if err != nil {
		return err
	}
	printReportHeader()
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		// Some run dirs are deeply nested (remote-work/<id>/results/<id>).
		// For local results dirs, expect summary.json one level down.
		sp := filepath.Join(resultsDir, e.Name(), "summary.json")
		b, err := os.ReadFile(sp)
		if err != nil {
			// Skip runs that haven't finished writing summary.json.
			continue
		}
		printReportSummary(b, e.Name())
	}
	return nil
}

func runsReportRemote(host, resultsDir string) error {
	if resultsDir == "" {
		resultsDir = "$HOME/.vett/remote-work"
	}
	// For each subdir of resultsDir, cat the nested summary.json:
	// <dir>/<run-id>/results/<run-id>/summary.json
	listCmd := exec.Command("ssh", host, "ls "+resultsDir)
	noPathConv(listCmd)
	lsOut, err := listCmd.Output()
	if err != nil {
		return fmt.Errorf("ssh ls: %w", err)
	}
	printReportHeader()
	for _, name := range strings.Split(strings.TrimSpace(string(lsOut)), "\n") {
		if name == "" {
			continue
		}
		// Try the flat layout first (results/<id>/summary.json when
		// the dir IS <id>, or <dir>/<id>/summary.json), then fall
		// back to the nested remote-work layout.
		candidates := []string{
			resultsDir + "/" + name + "/summary.json",
			resultsDir + "/" + name + "/results/" + name + "/summary.json",
		}
		var b []byte
		for _, sp := range candidates {
			catCmd := exec.Command("ssh", host, "cat "+sp+" 2>/dev/null")
			noPathConv(catCmd)
			out, err := catCmd.Output()
			if err == nil && len(out) > 0 {
				b = out
				break
			}
		}
		if len(b) == 0 {
			continue
		}
		printReportSummary(b, name)
	}
	return nil
}

func printReportHeader() {
	fmt.Printf("%-40s  %-40s  %5s  %8s  %-16s  %8s\n",
		"RUN", "INSTANCE", "ITERS", "DURATION", "END_REASON", "PATCH")
	fmt.Println(strings.Repeat("-", 130))
}

func printReportSummary(b []byte, runName string) {
	var s struct {
		Instances []struct {
			InstanceID  string  `json:"instance_id"`
			Iterations  int     `json:"iterations"`
			DurationSec float64 `json:"duration_seconds"`
			EndReason   string  `json:"end_reason"`
			PatchChars  int     `json:"patch_chars"`
		} `json:"instances"`
	}
	if err := json.Unmarshal(b, &s); err != nil {
		return
	}
	for _, i := range s.Instances {
		fmt.Printf("%-40s  %-40s  %5d  %7.0fs  %-16s  %7db\n",
			truncate(runName, 40), truncate(i.InstanceID, 40),
			i.Iterations, i.DurationSec, i.EndReason, i.PatchChars)
	}
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n-3] + "..."
}

func runsListCmd() *cobra.Command {
	var (
		remoteHost string
		resultsDir string
	)
	cmd := &cobra.Command{
		Use:   "list",
		Short: "List runs (local ./results/, --results-dir <path>, or --remote <host>)",
		RunE: func(cmd *cobra.Command, args []string) error {
			if remoteHost != "" {
				return runsListRemote(remoteHost, resultsDir)
			}
			return runsListLocal(resultsDir)
		},
	}
	cmd.Flags().StringVar(&remoteHost, "remote", "", "List runs on an SSH host")
	cmd.Flags().StringVar(&resultsDir, "results-dir", "", "Directory to scan for runs (default: ./results locally, ~/.vett/remote-work remotely)")
	return cmd
}

func runsListLocal(resultsDir string) error {
	if resultsDir == "" {
		wd, err := os.Getwd()
		if err != nil {
			return err
		}
		resultsDir = filepath.Join(wd, "results")
	}
	entries, err := os.ReadDir(resultsDir)
	if err != nil {
		if os.IsNotExist(err) {
			fmt.Printf("no runs in %s (run `vett init` or pass --results-dir)\n", resultsDir)
			return nil
		}
		return err
	}
	for _, e := range entries {
		if e.IsDir() {
			fmt.Println(e.Name())
		}
	}
	return nil
}

func runsListRemote(host, resultsDir string) error {
	if resultsDir == "" {
		resultsDir = "~/.vett/remote-work"
	}
	cmd := exec.Command("ssh", host, "ls "+resultsDir)
	noPathConv(cmd)
	stderrBuf := &byteBuf{}
	cmd.Stderr = stderrBuf
	out, err := cmd.Output()
	if err != nil {
		return fmt.Errorf("ssh %s: %w (stderr: %s)", host, err, string(stderrBuf.Bytes()))
	}
	lines := strings.Split(strings.TrimSpace(string(out)), "\n")
	for _, l := range lines {
		if l != "" {
			fmt.Println(l, "  (remote:"+host+")")
		}
	}
	return nil
}

func runsTailCmd() *cobra.Command {
	var remoteHost string
	cmd := &cobra.Command{
		Use:   "tail <run-id>",
		Short: "Tail a run's trace files",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			runID := args[0]
			if remoteHost != "" {
				return runsTailRemote(remoteHost, runID)
			}
			return runsTailLocal(runID)
		},
	}
	cmd.Flags().StringVar(&remoteHost, "remote", "", "Tail a run on an SSH host")
	return cmd
}

func runsTailLocal(runID string) error {
	wd, _ := os.Getwd()
	traceFile := filepath.Join(wd, "results", runID, "run.trace.jsonl")
	if _, err := os.Stat(traceFile); err != nil {
		return fmt.Errorf("%s: %w", traceFile, err)
	}
	f, err := os.Open(traceFile)
	if err != nil {
		return err
	}
	defer f.Close()
	_, _ = io.Copy(os.Stdout, f)
	return nil
}

func runsTailRemote(host, runID string) error {
	remotePath := "~/.vett/remote-work/" + runID + "/results/" + runID + "/run.trace.jsonl"
	c := exec.Command("ssh", host, "cat "+remotePath+" 2>&1")
	c.Stdout = os.Stdout
	c.Stderr = os.Stderr
	return c.Run()
}

func runsFetchCmd() *cobra.Command {
	var (
		remoteHost string
		outDir     string
	)
	cmd := &cobra.Command{
		Use:   "fetch <run-id>",
		Short: "Fetch a run's results tree from --remote to local ./results/",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			runID := args[0]
			if remoteHost == "" {
				return fmt.Errorf("runs fetch requires --remote <host>")
			}
			wd, _ := os.Getwd()
			if outDir == "" {
				outDir = filepath.Join(wd, "results", runID)
			}
			if err := os.MkdirAll(outDir, 0o755); err != nil {
				return err
			}
			// tar over ssh: tar the remote results dir, extract locally.
			// We use tarCmd.Dir instead of `tar -C <path>` because on
			// Windows git-bash, `-C "C:\..."` confuses tar's arg parser.
			remotePath := "~/.vett/remote-work/" + runID + "/results/" + runID
			sshCmd := exec.Command("ssh", host(remoteHost), "tar -cf - -C "+remotePath+" .")
			tarCmd := exec.Command("tar", "-xf", "-")
			tarCmd.Dir = outDir
			pipe, err := sshCmd.StdoutPipe()
			if err != nil {
				return err
			}
			tarCmd.Stdin = pipe
			tarCmd.Stdout = os.Stderr
			tarCmd.Stderr = os.Stderr
			if err := tarCmd.Start(); err != nil {
				return err
			}
			if err := sshCmd.Run(); err != nil {
				_ = tarCmd.Wait()
				return fmt.Errorf("ssh tar: %w", err)
			}
			if err := tarCmd.Wait(); err != nil {
				return fmt.Errorf("local tar extract: %w", err)
			}
			fmt.Printf("✓ fetched %s → %s\n", runID, outDir)
			return nil
		},
	}
	cmd.Flags().StringVar(&remoteHost, "remote", "", "Remote SSH host to fetch from")
	cmd.Flags().StringVar(&outDir, "out", "", "Local output directory (default: ./results/<run-id>)")
	return cmd
}

// host indirection for testing.
var host = func(s string) string { return s }

// noPathConv sets MSYS_NO_PATHCONV=1 on cmd.Env so Git Bash on
// Windows doesn't mangle posix-style paths we pass through to ssh
// (e.g. "/mnt/ssd/vett/runs" being rewritten to "C:/Program Files/
// Git/mnt/ssd/vett/runs"). No-op on non-Windows; harmless if MSYS
// isn't present.
func noPathConv(cmd *exec.Cmd) {
	env := os.Environ()
	env = append(env, "MSYS_NO_PATHCONV=1", "MSYS2_ARG_CONV_EXCL=*")
	cmd.Env = env
}

// byteBuf is a tiny bytes.Buffer without importing bytes (keeps
// imports minimal in this file).
type byteBuf struct{ b []byte }

func (bb *byteBuf) Write(p []byte) (int, error) {
	bb.b = append(bb.b, p...)
	return len(p), nil
}
func (bb *byteBuf) Bytes() []byte { return bb.b }

func runsStatusCmd() *cobra.Command {
	var remoteHost string
	cmd := &cobra.Command{
		Use:   "status <run-id>",
		Short: "Show a one-line summary of a run (completed/errored/iterations)",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			runID := args[0]
			if remoteHost != "" {
				return runsStatusRemote(remoteHost, runID)
			}
			return runsStatusLocal(runID)
		},
	}
	cmd.Flags().StringVar(&remoteHost, "remote", "", "Status from an SSH host")
	return cmd
}

func runsStatusLocal(runID string) error {
	wd, _ := os.Getwd()
	runDir := filepath.Join(wd, "results", runID)
	summaryPath := filepath.Join(runDir, "summary.json")
	if b, err := os.ReadFile(summaryPath); err == nil {
		return printSummary(b)
	}
	// Run still in-progress — fall back to parsing the trace file for
	// approximate iteration counts per instance.
	return printLivePartialStatus(runDir)
}

func runsStatusRemote(host, runID string) error {
	remoteRunDir := "~/.vett/remote-work/" + runID + "/results/" + runID
	cmd := exec.Command("ssh", host, "cat "+remoteRunDir+"/summary.json")
	noPathConv(cmd)
	out, err := cmd.Output()
	if err == nil {
		return printSummary(out)
	}
	// Summary missing — fall back to partial status from the remote
	// trace dir via ssh + grep.
	return printRemotePartialStatus(host, remoteRunDir)
}

func printSummary(b []byte) error {
	var s struct {
		RunID          string  `json:"run_id"`
		Suite          string  `json:"suite"`
		Profile        string  `json:"profile"`
		Model          string  `json:"model"`
		InstanceCount  int     `json:"instance_count"`
		Completed      int     `json:"completed"`
		Errored        int     `json:"errored"`
		Resolved       int     `json:"resolved"`
		ResolutionRate float64 `json:"resolution_rate"`
		DurationSec    float64 `json:"duration_seconds"`
	}
	if err := json.Unmarshal(b, &s); err != nil {
		return fmt.Errorf("parse summary: %w", err)
	}
	fmt.Printf("run:          %s\n", s.RunID)
	fmt.Printf("suite:        %s\n", s.Suite)
	fmt.Printf("profile:      %s\n", s.Profile)
	fmt.Printf("model:        %s\n", s.Model)
	fmt.Printf("instances:    %d\n", s.InstanceCount)
	fmt.Printf("completed:    %d\n", s.Completed)
	fmt.Printf("errored:      %d\n", s.Errored)
	fmt.Printf("resolved:     %d (%.1f%%)\n", s.Resolved, s.ResolutionRate*100)
	fmt.Printf("duration:     %.1fs\n", s.DurationSec)
	return nil
}

func printLivePartialStatus(runDir string) error {
	fmt.Println("run still in progress (no summary.json yet)")
	instDir := filepath.Join(runDir, "instances")
	entries, err := os.ReadDir(instDir)
	if err != nil {
		return fmt.Errorf("no instance traces at %s: %w", instDir, err)
	}
	for _, e := range entries {
		if !strings.HasSuffix(e.Name(), ".trace.jsonl") {
			continue
		}
		path := filepath.Join(instDir, e.Name())
		b, _ := os.ReadFile(path)
		iters := strings.Count(string(b), `"event":"iteration_start"`)
		iid := strings.TrimSuffix(e.Name(), ".trace.jsonl")
		fmt.Printf("  %s: %d iterations\n", iid, iters)
	}
	return nil
}

func printRemotePartialStatus(host, remoteRunDir string) error {
	fmt.Println("run still in progress on", host, "(no summary.json yet)")
	// List trace files and count iteration_start events via ssh.
	c := exec.Command("ssh", host,
		"for t in "+remoteRunDir+"/instances/*.trace.jsonl; do [ -f \"$t\" ] && echo \"$(basename \"$t\" .trace.jsonl) $(grep -c iteration_start \"$t\")\"; done")
	noPathConv(c)
	out, err := c.Output()
	if err != nil {
		return fmt.Errorf("ssh partial status: %w", err)
	}
	for _, line := range strings.Split(strings.TrimSpace(string(out)), "\n") {
		if line == "" {
			continue
		}
		parts := strings.Fields(line)
		if len(parts) == 2 {
			fmt.Printf("  %s: %s iterations\n", parts[0], parts[1])
		} else {
			fmt.Println("  ", line)
		}
	}
	return nil
}
