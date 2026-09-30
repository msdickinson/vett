// Package main is the entry point for the `vett` host CLI binary.
//
// Phase 1 surface: `vett run`, `vett preflight`, `vett profiles`,
// `vett suites`, `vett version`, `--help`. `vett results` and
// `vett verify` are Phase 4.
package main

import (
	"fmt"
	"os"

	"github.com/spf13/cobra"

	vversion "github.com/msdickinson/vett/sidecar/internal/version"

	// Import for side effects: each tool/middleware registers itself
	// at init time via tool.Register / middleware.Register.
	_ "github.com/msdickinson/vett/sidecar/middleware"
	_ "github.com/msdickinson/vett/sidecar/tools"
)

func init() {
	// Propagate cmd/vett's linker-settable version into the shared
	// internal/version package so the runner emits the same string
	// on run_start events.
	vversion.Vett = version
}

// Version metadata. Set via -ldflags at build time; sensible defaults for
// `go run ./cmd/vett` and developer builds.
var (
	version    = "0.1.0-dev"
	commitHash = "unknown"
	buildDate  = "unknown"
)

func main() {
	rootCmd := &cobra.Command{
		Use:   "vett",
		Short: "Vett — a pluggable benchmark harness for AI coding agents",
		Long: `Vett runs AI coding agents against benchmark suites and scores them.

You describe an agent as a profile (system prompt + tools + middleware + LLM
settings) and Vett runs it against any suite. The openhands profile is a
faithful mirror of openhands-sdk; other profiles exist to measure deltas
against that baseline.`,
		SilenceUsage: true,
	}

	rootCmd.AddCommand(versionCmd())
	rootCmd.AddCommand(installCmd())
	rootCmd.AddCommand(initCmd())
	rootCmd.AddCommand(importSwebenchCmd())
	rootCmd.AddCommand(profilesCmd())
	rootCmd.AddCommand(suitesCmd())
	rootCmd.AddCommand(preflightCmd())
	rootCmd.AddCommand(runCmd())
	rootCmd.AddCommand(runsCmd())
	rootCmd.AddCommand(chatCmd())
	rootCmd.AddCommand(buildCmd())
	rootCmd.AddCommand(doctorCmd())
	rootCmd.AddCommand(replayCmd())
	rootCmd.AddCommand(callCmd())
	rootCmd.AddCommand(analyzeCmd())

	if err := rootCmd.Execute(); err != nil {
		fmt.Fprintln(os.Stderr, "vett:", err)
		os.Exit(1)
	}
}

func versionCmd() *cobra.Command {
	return &cobra.Command{
		Use:   "version",
		Short: "Print Vett version info",
		Run: func(cmd *cobra.Command, args []string) {
			fmt.Printf("vett %s\n", version)
			fmt.Printf("  commit:    %s\n", commitHash)
			fmt.Printf("  built:     %s\n", buildDate)
			fmt.Printf("  go:        %s\n", goVersion())
		},
	}
}
