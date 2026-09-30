package main

import (
	"fmt"
	"os/exec"
	"runtime"
	"strings"

	"github.com/spf13/cobra"

	"github.com/msdickinson/vett/sidecar/internal/plugin"
)

func doctorCmd() *cobra.Command {
	return &cobra.Command{
		Use:   "doctor",
		Short: "Check which language runtimes and tools are available",
		Long: `doctor validates your environment for running vett. It checks:
  - Docker daemon accessibility
  - Language SDKs for plugin development (Go, Python, C#, Node, Rust, Ruby)
  - Sidecar binary availability
  - Workspace structure`,
		RunE: func(cmd *cobra.Command, args []string) error {
			fmt.Println("vett doctor — environment check")
			fmt.Println()

			// Check Docker.
			fmt.Print("  Docker:     ")
			if out, err := exec.Command(dockerBinName(), "info", "--format", "{{.ServerVersion}}").Output(); err == nil {
				fmt.Printf("✓ %s\n", strings.TrimSpace(string(out)))
			} else {
				fmt.Println("✗ not reachable (start Docker Desktop or set DOCKER_HOST)")
			}

			// Check sidecar.
			fmt.Print("  Sidecar:    ")
			if p, err := resolveSidecarPath(); err == nil {
				fmt.Printf("✓ %s\n", p)
			} else {
				fmt.Println("✗ not found (run: make sidecar && vett install)")
			}

			fmt.Println()
			fmt.Println("  Language SDKs (for workspace plugins):")

			type langCheck struct {
				name    string
				lang    plugin.Language
				verCmd  string
				verArgs []string
			}

			checks := []langCheck{
				{"Go", plugin.LangGo, "go", []string{"version"}},
				{"Python", plugin.LangPython, pythonBin(), []string{"--version"}},
				{"C#/.NET", plugin.LangCSharp, "dotnet", []string{"--version"}},
				{"Node.js", plugin.LangTypeScript, "node", []string{"--version"}},
				{"Rust", plugin.LangRust, "cargo", []string{"--version"}},
				{"Ruby", plugin.LangRuby, "ruby", []string{"--version"}},
			}

			installed := 0
			for _, c := range checks {
				fmt.Printf("    %-10s ", c.name)
				if err := plugin.CheckSDK(c.lang); err != nil {
					info := plugin.GetLangInfo(c.lang)
					hint := ""
					if info != nil {
						hint = info.InstallHint
					}
					fmt.Printf("✗ not found (install: %s)\n", hint)
				} else {
					out, _ := exec.Command(c.verCmd, c.verArgs...).Output()
					ver := strings.TrimSpace(string(out))
					if len(ver) > 60 {
						ver = ver[:57] + "..."
					}
					fmt.Printf("✓ %s\n", ver)
					installed++
				}
			}

			fmt.Println()
			fmt.Printf("  %d/%d language SDKs available\n", installed, len(checks))
			fmt.Println()
			fmt.Println("  You only need SDKs for the languages your workspace plugins use.")
			fmt.Println("  The core vett tools (terminal, file_editor, think, finish) need no SDK.")

			return nil
		},
	}
}

func dockerBinName() string { return "docker" }

func pythonBin() string {
	if runtime.GOOS == "windows" {
		return "python"
	}
	return "python3"
}
